using System.Security.Cryptography;
using System.Text;

namespace Penghou.Hufu.Broker;

/// <summary>Host-issued, single-use bootstrap material. The secret is never logged or exposed as evidence.</summary>
public sealed record BrokerSessionBootstrap(
    string BootstrapSecret,
    string TenantId,
    string SubjectId,
    string WorkerIdentity,
    string Audience,
    DateTimeOffset ExpiresAt);

/// <summary>
/// An authenticated broker session. Logical worker identity comes from the
/// host-issued bootstrap handshake, never from a PID, claimed ID or pipe
/// connection alone.
/// </summary>
public sealed record BrokerSessionPrincipal(
    string SessionId,
    string TenantId,
    string SubjectId,
    string WorkerIdentity,
    string Audience,
    int ProtocolVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Authenticate a presented bootstrap secret over an already-established connection.</summary>
public interface IBrokerSessionAuthenticator
{
    ValueTask<BrokerSessionPrincipal?> AuthenticateAsync(
        string bootstrapSecret,
        string connectionIdentity,
        int protocolVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory host-owned bootstrap/session registry for the first profile.
/// Bootstrap secrets are stored only as SHA-256 hashes, are single-use, and are
/// bounded by expiry. Sessions are bounded per worker. This is a cooperating-
/// worker facility, not hostile same-account containment.
/// </summary>
public sealed class BrokerSessionRegistry : IBrokerSessionAuthenticator, IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TimeProvider _clock;
    private readonly int _maximumSessionsPerWorker;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrokerSessionPrincipal> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public BrokerSessionRegistry(TimeProvider? timeProvider = null, int maximumSessionsPerWorker = BrokerProtocol.MaximumSessionsPerWorker)
    {
        if (maximumSessionsPerWorker is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumSessionsPerWorker));
        _clock = timeProvider ?? TimeProvider.System;
        _maximumSessionsPerWorker = maximumSessionsPerWorker;
    }

    /// <summary>
    /// Issue a short-lived, single-use high-entropy bootstrap secret bound to a
    /// logical worker. Deliver it only through a protected out-of-band path.
    /// </summary>
    public BrokerSessionBootstrap Issue(string tenantId, string subjectId, string workerIdentity, string audience,
        TimeSpan? lifetime = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var token in new[] { tenantId, subjectId, workerIdentity, audience })
            if (!AuthorityValidation.ValidToken(token)) throw new ArgumentException("A bounded bootstrap field is required.");
        var lifetimeValue = lifetime ?? TimeSpan.FromSeconds(30);
        if (lifetimeValue <= TimeSpan.Zero || lifetimeValue > BrokerProtocol.MaximumBootstrapLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        var now = _clock.GetUtcNow();
        var expiresAt = now.Add(lifetimeValue);
        var secret = NewSecret();
        lock (_gate)
        {
            PruneLocked(now);
            _pending[Hash(secret)] = new Pending(tenantId, subjectId, workerIdentity, audience, expiresAt);
        }
        return new BrokerSessionBootstrap(secret, tenantId, subjectId, workerIdentity, audience, expiresAt);
    }

    public ValueTask<BrokerSessionPrincipal?> AuthenticateAsync(string bootstrapSecret, string connectionIdentity,
        int protocolVersion, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (protocolVersion != BrokerProtocol.Version) return ValueTask.FromResult<BrokerSessionPrincipal?>(null);
        if (!AuthorityValidation.ValidToken(bootstrapSecret) || !AuthorityValidation.ValidToken(connectionIdentity))
            return ValueTask.FromResult<BrokerSessionPrincipal?>(null);
        var now = _clock.GetUtcNow();
        Pending? pending = null;
        lock (_gate)
        {
            PruneLocked(now);
            var key = Hash(bootstrapSecret);
            if (_pending.TryGetValue(key, out var found))
            {
                _pending.Remove(key); // single-use
                if (found.ExpiresAt > now) pending = found;
            }
            if (pending is null) return ValueTask.FromResult<BrokerSessionPrincipal?>(null);
            if (ActiveForWorkerLocked(pending.WorkerIdentity, now) >= _maximumSessionsPerWorker)
                return ValueTask.FromResult<BrokerSessionPrincipal?>(null);
            var session = new BrokerSessionPrincipal(NewSecret(), pending.TenantId, pending.SubjectId,
                pending.WorkerIdentity, pending.Audience, protocolVersion, now, now.Add(BrokerProtocol.MaximumSessionLifetime));
            _sessions[session.SessionId] = session;
            return ValueTask.FromResult<BrokerSessionPrincipal?>(session);
        }
    }

    /// <summary>Revoke a session so later reference resolution fails closed.</summary>
    public bool Revoke(string sessionId)
    {
        if (!AuthorityValidation.ValidToken(sessionId)) return false;
        lock (_gate) return _sessions.Remove(sessionId);
    }

    public bool IsActive(BrokerSessionPrincipal session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate) return _sessions.TryGetValue(session.SessionId, out var current) &&
            current == session && current.ExpiresAt > _clock.GetUtcNow();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
            _sessions.Clear();
        }
    }

    private int ActiveForWorkerLocked(string workerIdentity, DateTimeOffset now)
    {
        var count = 0;
        foreach (var session in _sessions.Values)
            if (session.WorkerIdentity == workerIdentity && session.ExpiresAt > now) count++;
        return count;
    }

    private void PruneLocked(DateTimeOffset now)
    {
        if (_pending.Count == 0 && _sessions.Count == 0) return;
        foreach (var key in _pending.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _pending.Remove(key);
        foreach (var key in _sessions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _sessions.Remove(key);
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(secret))).ToLowerInvariant();

    private sealed record Pending(string TenantId, string SubjectId, string WorkerIdentity, string Audience,
        DateTimeOffset ExpiresAt);
}
