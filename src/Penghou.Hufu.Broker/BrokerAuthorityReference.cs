namespace Penghou.Hufu.Broker;

/// <summary>
/// A host-issued opaque authority reference. The reference string alone confers
/// nothing: it resolves only under the authenticated session that it was bound
/// to, and every operation still receives fresh AE-1 authorization.
/// </summary>
public sealed record BrokerAuthorityReference(
    string Reference,
    string SessionId,
    string TenantId,
    string SubjectId,
    string Audience,
    string DelegationId,
    string Generation,
    string FenceId,
    AuthenticatedAuthorityContext AuthorityContext,
    string WorkspaceId,
    string RelativePath,
    AuthorityAction Action,
    int MaximumBytes,
    DateTimeOffset ExpiresAt);

/// <summary>Host-owned registry of opaque, session-bound authority references.</summary>
public interface IBrokerAuthorityReferenceStore
{
    BrokerAuthorityReference Register(
        BrokerSessionPrincipal session,
        string delegationId,
        string generation,
        string fenceId,
        AuthenticatedAuthorityContext authorityContext,
        string workspaceId,
        string relativePath,
        AuthorityAction action,
        int maximumBytes,
        TimeSpan? lifetime = null);

    ValueTask<BrokerAuthorityReference?> ResolveAsync(
        string reference,
        BrokerSessionPrincipal session,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RevokeAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>In-memory opaque reference store for the first profile.</summary>
public sealed class BrokerAuthorityReferenceStore : IBrokerAuthorityReferenceStore, IDisposable
{
    private static readonly TimeSpan MaximumReferenceLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, BrokerAuthorityReference> _references = new(StringComparer.Ordinal);
    private bool _disposed;

    public BrokerAuthorityReferenceStore(TimeProvider? timeProvider = null) =>
        _clock = timeProvider ?? TimeProvider.System;

    public BrokerAuthorityReference Register(BrokerSessionPrincipal session, string delegationId, string generation,
        string fenceId, AuthenticatedAuthorityContext authorityContext, string workspaceId, string relativePath,
        AuthorityAction action, int maximumBytes, TimeSpan? lifetime = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        foreach (var token in new[] { delegationId, generation, fenceId })
            if (!AuthorityValidation.ValidToken(token)) throw new ArgumentException("A bounded binding field is required.");
        if (!AuthorityValidation.ValidContext(authorityContext))
            throw new ArgumentException("A bounded authority context is required.", nameof(authorityContext));
        if (authorityContext.TenantId != session.TenantId || authorityContext.SubjectId != session.SubjectId)
            throw new ArgumentException("The authority context must match the authenticated session.", nameof(authorityContext));
        if (!AuthorityValidation.ValidToken(workspaceId))
            throw new ArgumentException("A bounded workspace is required.", nameof(workspaceId));
        if (AuthorityValidation.NormalizePath(relativePath) != relativePath)
            throw new ArgumentException("A canonical relative path is required.", nameof(relativePath));
        if (!Enum.IsDefined(action)) throw new ArgumentException("A known action is required.", nameof(action));
        if (maximumBytes is < BrokerProtocol.MinimumMaxBytes or > BrokerProtocol.MaximumMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var lifetimeValue = lifetime ?? TimeSpan.FromMinutes(5);
        if (lifetimeValue <= TimeSpan.Zero || lifetimeValue > MaximumReferenceLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        var now = _clock.GetUtcNow();
        var reference = new BrokerAuthorityReference(NewReference(), session.SessionId, session.TenantId, session.SubjectId,
            session.Audience, delegationId, generation, fenceId, authorityContext, workspaceId, relativePath, action,
            maximumBytes, now.Add(lifetimeValue));
        lock (_gate)
        {
            PruneLocked(now);
            _references[reference.Reference] = reference;
        }
        return reference;
    }

    public ValueTask<BrokerAuthorityReference?> ResolveAsync(string reference, BrokerSessionPrincipal session,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AuthorityValidation.ValidToken(reference) || session is null)
            return ValueTask.FromResult<BrokerAuthorityReference?>(null);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_references.TryGetValue(reference, out var found)) return ValueTask.FromResult<BrokerAuthorityReference?>(null);
            if (found.ExpiresAt <= now)
            {
                _references.Remove(reference);
                return ValueTask.FromResult<BrokerAuthorityReference?>(null);
            }
            if (found.SessionId != session.SessionId || found.TenantId != session.TenantId ||
                found.SubjectId != session.SubjectId || found.Audience != session.Audience)
                return ValueTask.FromResult<BrokerAuthorityReference?>(null);
            return ValueTask.FromResult<BrokerAuthorityReference?>(found);
        }
    }

    public ValueTask<bool> RevokeAsync(string reference, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AuthorityValidation.ValidToken(reference)) return ValueTask.FromResult(false);
        lock (_gate) return ValueTask.FromResult(_references.Remove(reference));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _references.Clear();
        }
    }

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var key in _references.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _references.Remove(key);
    }

    private static string NewReference() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
