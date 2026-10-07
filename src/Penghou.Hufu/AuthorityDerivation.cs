using System.Security.Cryptography;
using System.Text;

namespace Penghou.Hufu;

/// <summary>
/// Desired authority for a derivation request. Describes what is wanted, never
/// the resulting grant: it carries no grant, parent, snapshot, sequence, or
/// status identity. Those are Hufu-owned results.
/// </summary>
public sealed record RequestedAuthority
{
    public RequestedAuthority(
        IReadOnlyList<AuthorityAction> actions,
        AuthorityScope scope,
        IReadOnlyList<AuthorityScope> exclusions,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(exclusions);
        if (actions.Count is < 1 || actions.Count > AuthorityValidation.KnownActionCount ||
            actions.Any(action => !Enum.IsDefined(action)) || actions.Distinct().Count() != actions.Count)
            throw new ArgumentException("One or more distinct known actions are required.", nameof(actions));
        Scope = AuthorityValidation.FreezeScope(scope);
        var frozen = exclusions
            .Select(exclusion => exclusion is null
                ? throw new ArgumentException("Exclusions cannot contain null entries.", nameof(exclusions))
                : AuthorityValidation.FreezeScope(exclusion))
            .OrderBy(exclusion => exclusion.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(exclusion => exclusion.RelativePath, StringComparer.Ordinal)
            .ThenBy(exclusion => exclusion.Kind)
            .ToArray();
        if (frozen.Length > 128)
            throw new ArgumentException("Too many exclusions.", nameof(exclusions));
        if (frozen.Any(exclusion => !AuthorityValidation.Contains(Scope, exclusion)))
            throw new ArgumentException("Exclusions must remain within the requested scope.", nameof(exclusions));
        if (notBefore >= expiresAt)
            throw new ArgumentException("Requested validity must be a non-empty interval.");
        Actions = actions.Distinct().OrderBy(action => action).ToArray();
        Exclusions = frozen;
        NotBefore = notBefore.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    public IReadOnlyList<AuthorityAction> Actions { get; }
    public AuthorityScope Scope { get; }
    public IReadOnlyList<AuthorityScope> Exclusions { get; }
    public DateTimeOffset NotBefore { get; }
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// Asks Hufu to derive a child grant. Hufu creates the child; the caller
/// never constructs it. The caller nonce identifies the transport attempt
/// only and never participates in derivation identity.
/// </summary>
public sealed record AuthorityGrantDerivationRequest
{
    public AuthorityGrantDerivationRequest(
        string parentGrantId,
        AuthenticatedAuthorityContext childContext,
        string delegationId,
        string generation,
        RequestedAuthority requested,
        string requestIdentity)
    {
        if (!AuthorityValidation.ValidToken(parentGrantId))
            throw new ArgumentException("A bounded parent grant identity is required.", nameof(parentGrantId));
        if (!AuthorityValidation.ValidContext(childContext))
            throw new ArgumentException("A bounded child context is required.", nameof(childContext));
        if (!AuthorityValidation.ValidToken(delegationId))
            throw new ArgumentException("A bounded delegation identity is required.", nameof(delegationId));
        if (!AuthorityValidation.ValidToken(generation))
            throw new ArgumentException("A bounded generation identity is required.", nameof(generation));
        ArgumentNullException.ThrowIfNull(requested);
        if (!AuthorityValidation.ValidToken(requestIdentity))
            throw new ArgumentException("A bounded request identity is required.", nameof(requestIdentity));
        ParentGrantId = parentGrantId;
        ChildContext = childContext;
        DelegationId = delegationId;
        Generation = generation;
        Requested = requested;
        RequestIdentity = requestIdentity;
    }

    public string ParentGrantId { get; }
    public AuthenticatedAuthorityContext ChildContext { get; }
    public string DelegationId { get; }
    public string Generation { get; }
    public RequestedAuthority Requested { get; }
    public string RequestIdentity { get; }
}

/// <summary>Hufu-owned lineage for one derived grant issuance.</summary>
public sealed record AuthorityGrantLineage
{
    public AuthorityGrantLineage(
        string derivationIdentity,
        string childGrantId,
        AuthenticatedAuthorityContext childContext,
        string parentGrantId,
        AuthenticatedAuthorityContext parentContext,
        string delegationId,
        string generation,
        RequestedAuthority requested,
        AuthorityStoreActor actor,
        DateTimeOffset issuedAt)
    {
        if (!AuthorityValidation.ValidToken(derivationIdentity))
            throw new ArgumentException("A bounded derivation identity is required.", nameof(derivationIdentity));
        if (!AuthorityValidation.ValidToken(childGrantId))
            throw new ArgumentException("A bounded child grant identity is required.", nameof(childGrantId));
        if (!AuthorityValidation.ValidContext(childContext))
            throw new ArgumentException("A bounded child context is required.", nameof(childContext));
        if (!AuthorityValidation.ValidToken(parentGrantId))
            throw new ArgumentException("A bounded parent grant identity is required.", nameof(parentGrantId));
        if (!AuthorityValidation.ValidContext(parentContext))
            throw new ArgumentException("A bounded parent context is required.", nameof(parentContext));
        if (!AuthorityValidation.ValidToken(delegationId))
            throw new ArgumentException("A bounded delegation identity is required.", nameof(delegationId));
        if (!AuthorityValidation.ValidToken(generation))
            throw new ArgumentException("A bounded generation identity is required.", nameof(generation));
        ArgumentNullException.ThrowIfNull(requested);
        if (!AuthorityStoreValidation.ValidActor(actor))
            throw new ArgumentException("A bounded issuing actor is required.", nameof(actor));
        DerivationIdentity = derivationIdentity;
        ChildGrantId = childGrantId;
        ChildContext = childContext;
        ParentGrantId = parentGrantId;
        ParentContext = parentContext;
        DelegationId = delegationId;
        Generation = generation;
        Requested = requested;
        Actor = actor;
        IssuedAt = issuedAt.ToUniversalTime();
    }

    public string DerivationIdentity { get; }
    public string ChildGrantId { get; }
    public AuthenticatedAuthorityContext ChildContext { get; }
    public string ParentGrantId { get; }
    public AuthenticatedAuthorityContext ParentContext { get; }
    public string DelegationId { get; }
    public string Generation { get; }
    public RequestedAuthority Requested { get; }
    public AuthorityStoreActor Actor { get; }
    public DateTimeOffset IssuedAt { get; }
}

/// <summary>
/// The outcome of one derivation: issued authority, a fail-closed refusal,
/// or an unreachable decision. Uses the shared status taxonomy; malformed
/// requests throw rather than returning a status.
/// </summary>
public sealed record AuthorityGrantDerivationResult(
    AuthorityStatus Status,
    AuthorityGrant? Grant,
    string ParentGrantId,
    string DerivationIdentity,
    string? ReasonCode);

/// <summary>Lineage liveness verdict for one evaluated snapshot.</summary>
public enum AncestorLivenessVerdict
{
    /// <summary>Every derived grant in the snapshot traces to live ancestors.</summary>
    Live = 0,
    /// <summary>A known ancestor is no longer live.</summary>
    AncestorRevoked = 1,
    /// <summary>A derived marker has no matching lineage, or the lineage does not match.</summary>
    LineageUnavailable = 2
}

/// <summary>
/// Answers whether the derived grants in an evaluated snapshot trace to
/// live ancestors. Consulted only after the ordinary evaluator would
/// otherwise permit, so existing denial behavior is untouched.
/// </summary>
public interface IAncestorLiveness
{
    ValueTask<AncestorLivenessVerdict> CheckAsync(
        AuthoritySnapshot snapshot,
        AuthorityRequest request,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Atomic derived-authority persistence: lineage, child snapshot, and
/// idempotency record become visible together or not at all.
/// </summary>
public interface IAuthorityDerivationStore
{
    ValueTask<AuthorityGrantDerivationResult> DeriveAsync(
        AuthorityDerivationCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<AuthorityGrantLineage?> GetLineageAsync(
        string childGrantId,
        CancellationToken cancellationToken = default);
}

/// <summary>Privileged derivation command. Containment is always proven before anything is written.</summary>
public sealed record AuthorityDerivationCommand
{
    public AuthorityDerivationCommand(
        AuthorityStoreActor actor,
        AuthenticatedAuthorityContext parentContext,
        string parentGrantId,
        AuthenticatedAuthorityContext childContext,
        string delegationId,
        string generation,
        RequestedAuthority requested,
        string requestIdentity)
    {
        if (!AuthorityStoreValidation.ValidActor(actor))
            throw new ArgumentException("A bounded issuing actor is required.", nameof(actor));
        if (!AuthorityValidation.ValidContext(parentContext))
            throw new ArgumentException("A bounded parent context is required.", nameof(parentContext));
        if (!AuthorityValidation.ValidToken(parentGrantId))
            throw new ArgumentException("A bounded parent grant identity is required.", nameof(parentGrantId));
        if (!AuthorityValidation.ValidContext(childContext))
            throw new ArgumentException("A bounded child context is required.", nameof(childContext));
        if (!AuthorityValidation.ValidToken(delegationId))
            throw new ArgumentException("A bounded delegation identity is required.", nameof(delegationId));
        if (!AuthorityValidation.ValidToken(generation))
            throw new ArgumentException("A bounded generation identity is required.", nameof(generation));
        ArgumentNullException.ThrowIfNull(requested);
        if (!AuthorityValidation.ValidToken(requestIdentity))
            throw new ArgumentException("A bounded request identity is required.", nameof(requestIdentity));
        Actor = actor;
        ParentContext = parentContext;
        ParentGrantId = parentGrantId;
        ChildContext = childContext;
        DelegationId = delegationId;
        Generation = generation;
        Requested = requested;
        RequestIdentity = requestIdentity;
    }

    public AuthorityStoreActor Actor { get; }
    public AuthenticatedAuthorityContext ParentContext { get; }
    public string ParentGrantId { get; }
    public AuthenticatedAuthorityContext ChildContext { get; }
    public string DelegationId { get; }
    public string Generation { get; }
    public RequestedAuthority Requested { get; }
    public string RequestIdentity { get; }
}

/// <summary>
/// The single Hufu-owned definition of attenuation: canonical derivation
/// identity and the child-contained-by-parent proof. Biscuit restrictions
/// must conform to this semantics rather than define their own.
/// </summary>
public static class AuthorityDerivation
{
    /// <summary>
    /// Computes the logical derivation identity. The caller nonce never
    /// participates: retries share identity, changed requests do not.
    /// </summary>
    public static string DerivationIdentity(
        string parentGrantId,
        string delegationId,
        string generation,
        RequestedAuthority requested)
    {
        if (!AuthorityValidation.ValidToken(parentGrantId))
            throw new ArgumentException("A bounded parent grant identity is required.", nameof(parentGrantId));
        if (!AuthorityValidation.ValidToken(delegationId))
            throw new ArgumentException("A bounded delegation identity is required.", nameof(delegationId));
        if (!AuthorityValidation.ValidToken(generation))
            throw new ArgumentException("A bounded generation identity is required.", nameof(generation));
        ArgumentNullException.ThrowIfNull(requested);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, AuthorityValidation.StrictUtf8, true);
        WriteText(writer, "Penghou.Hufu.DerivationIdentity.v1");
        WriteText(writer, parentGrantId);
        WriteText(writer, delegationId);
        WriteText(writer, generation);
        WriteAuthority(writer, requested);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the canonical hash of requested authority alone. Delegability
    /// approval binds this hash, so an approval for one authority envelope can
    /// never match a materially different one.
    /// </summary>
    public static string RequestedAuthorityHash(RequestedAuthority requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, AuthorityValidation.StrictUtf8, true);
        WriteText(writer, "Penghou.Hufu.RequestedAuthority.v1");
        WriteAuthority(writer, requested);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();
    }

    private static void WriteAuthority(BinaryWriter writer, RequestedAuthority requested)
    {
        foreach (var action in requested.Actions.OrderBy(action => action)) writer.Write((int)action);
        WriteScope(writer, requested.Scope);
        foreach (var exclusion in requested.Exclusions
                     .OrderBy(exclusion => exclusion.WorkspaceId, StringComparer.Ordinal)
                     .ThenBy(exclusion => exclusion.RelativePath, StringComparer.Ordinal)
                     .ThenBy(exclusion => exclusion.Kind))
            WriteScope(writer, exclusion);
        writer.Write(requested.NotBefore.UtcTicks);
        writer.Write(requested.ExpiresAt.UtcTicks);
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        var bytes = AuthorityValidation.StrictUtf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteScope(BinaryWriter writer, AuthorityScope scope)
    {
        WriteText(writer, scope.WorkspaceId);
        WriteText(writer, scope.RelativePath);
        writer.Write((int)scope.Kind);
    }

    /// <summary>
    /// Proves requested child authority is contained by a parent grant:
    /// actions and scope narrow, parent restrictions intersecting the child
    /// scope persist, and validity fits inside the parent window.
    /// </summary>
    public static bool IsContainedBy(RequestedAuthority child, AuthorityGrant parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        try
        {
            if (child.Actions.Any(action => !parent.Actions.Contains(action)))
                return false;
            if (!AuthorityValidation.Contains(parent.Scope, child.Scope))
                return false;
            foreach (var restriction in parent.Exclusions.Where(exclusion => Overlaps(exclusion, child.Scope)))
            {
                if (!child.Exclusions.Any(exclusion =>
                        AuthorityValidation.Contains(exclusion, restriction) ||
                        AuthorityValidation.Contains(exclusion, child.Scope)))
                    return false;
            }
            return child.NotBefore >= parent.NotBefore && child.ExpiresAt <= parent.ExpiresAt;
        }
        catch
        {
            return false;
        }
    }

    private static bool Overlaps(AuthorityScope first, AuthorityScope second) =>
        AuthorityValidation.Contains(first, second) || AuthorityValidation.Contains(second, first);

    /// <summary>
    /// Lineage-aware liveness over an explicit derivation registry and the
    /// same snapshot source the authorizer uses, so parent liveness shares
    /// the admission's view of current state. Snapshots without derived
    /// grants return <see cref="AncestorLivenessVerdict.Live"/> with no I/O.
    /// </summary>
    public sealed class LineageAncestorLiveness(
        IAuthoritySnapshotSource source,
        IAuthorityDerivationStore derivations) : IAncestorLiveness
    {
        public async ValueTask<AncestorLivenessVerdict> CheckAsync(
            AuthoritySnapshot snapshot,
            AuthorityRequest request,
            DateTimeOffset evaluatedAt,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(request);
            var derived = snapshot.Layers
                .SelectMany(layer => layer.Grants)
                .Where(grant => grant.ParentGrantId is not null)
                .ToArray();
            if (derived.Length == 0)
                return AncestorLivenessVerdict.Live;

            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var grant in derived)
            {
                var verdict = await CheckLineageAsync(grant.Id, grant.ParentGrantId!, evaluatedAt, visited, cancellationToken)
                    .ConfigureAwait(false);
                if (verdict != AncestorLivenessVerdict.Live)
                    return verdict;
            }

            return AncestorLivenessVerdict.Live;
        }

        private async ValueTask<AncestorLivenessVerdict> CheckLineageAsync(
            string grantId,
            string parentGrantId,
            DateTimeOffset evaluatedAt,
            HashSet<string> visited,
            CancellationToken cancellationToken)
        {
            if (!visited.Add(grantId))
                return AncestorLivenessVerdict.LineageUnavailable;
            AuthorityGrantLineage? lineage;
            try
            {
                lineage = await derivations.GetLineageAsync(grantId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                return AncestorLivenessVerdict.LineageUnavailable;
            }
            if (lineage is null || lineage.ParentGrantId != parentGrantId || lineage.ChildGrantId != grantId)
                return AncestorLivenessVerdict.LineageUnavailable;

            AuthoritySnapshot? parent;
            try
            {
                parent = await source.GetCurrentAsync(lineage.ParentContext, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                return AncestorLivenessVerdict.LineageUnavailable;
            }
            var resolved = parent?.Layers
                .SelectMany(layer => layer.Grants)
                .FirstOrDefault(grant => grant.Id == parentGrantId);
            if (parent is null || resolved is null ||
                !(resolved.NotBefore <= evaluatedAt && evaluatedAt < resolved.ExpiresAt) ||
                parent.ValidUntil <= evaluatedAt)
                return AncestorLivenessVerdict.AncestorRevoked;

            if (resolved.ParentGrantId is null)
                return AncestorLivenessVerdict.Live;
            return await CheckLineageAsync(resolved.Id, resolved.ParentGrantId, evaluatedAt, visited, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
