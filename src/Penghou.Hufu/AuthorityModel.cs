using System.Security.Cryptography;
using System.Text;

namespace Penghou.Hufu;

// Append new actions to preserve the persisted numeric values of existing actions.
// ExecuteProcess authorizes starting a process whose executable is the request's
// scoped resource (workspace + canonical relative path). It does not by itself
// contain the process; the host selects and records an external execution provider.
public enum AuthorityAction { ReadFile, ListDirectory, ReadMetadata, PatchFile, Release, WriteFile, ExecuteProcess }
public enum AuthorityScopeKind { Exact, Subtree }
public enum AuthorityStatus { Unavailable, Deny, Permit }
public sealed record AuthenticatedAuthorityContext(string TenantId, string SubjectId, string RunId,
    string RevisionId, string FenceId);
public sealed record AuthorityScope(string WorkspaceId, string RelativePath, AuthorityScopeKind Kind);
public sealed record AuthorityGrant(string Id, IReadOnlyList<AuthorityAction> Actions, AuthorityScope Scope,
    IReadOnlyList<AuthorityScope> Exclusions, DateTimeOffset NotBefore, DateTimeOffset ExpiresAt)
{
    /// <summary>Parent grant identity when this grant is derived; null for root grants.</summary>
    public string? ParentGrantId { get; init; }
}
public sealed record AuthorityLayer(string Id, IReadOnlyList<AuthorityGrant> Grants);
public sealed record AuthorityRequest(AuthenticatedAuthorityContext Context, AuthorityAction Action,
    string WorkspaceId, string RelativePath, string RequestIdentity);
public sealed record AuthorityDecision(AuthorityStatus Status, string ReasonCode, string SnapshotVersion,
    string EvaluatorIdentity, string SnapshotIdentity);

/// <summary>Host-authenticated current facts. Possession or construction confers no authority.</summary>
public sealed class AuthoritySnapshot
{
    public AuthoritySnapshot(AuthenticatedAuthorityContext context, string version,
        IEnumerable<AuthorityLayer> layers, IEnumerable<AuthorityScope> mandatoryDenials, DateTimeOffset validUntil)
    {
        if (!AuthorityValidation.ValidContext(context) || !AuthorityValidation.ValidToken(version))
            throw new ArgumentException("Bounded authenticated context and version are required.");
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(mandatoryDenials);
        Context = context; Version = version; ValidUntil = validUntil.ToUniversalTime();
        var layerArray = layers.Take(9).ToArray();
        if (layerArray.Length is < 1 or > 8) throw new ArgumentException("One to eight authority layers are required.");
        var frozen = new List<AuthorityLayer>();
        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        var grantCount = 0; var exclusionCount = 0;
        foreach (var layer in layerArray)
        {
            if (layer is null || !AuthorityValidation.ValidToken(layer.Id) || !layerIds.Add(layer.Id) ||
                layer.Grants is null || layer.Grants.Count > 128 - grantCount)
                throw new ArgumentException("Invalid or excessive authority layer.");
            var grants = new List<AuthorityGrant>();
            var grantIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < layer.Grants.Count; i++)
            {
                var grant = layer.Grants[i];
                if (grant is null || !AuthorityValidation.ValidToken(grant.Id) || !grantIds.Add(grant.Id) ||
                    grant.Actions is null || grant.Actions.Count is < 1 || grant.Actions.Count > AuthorityValidation.KnownActionCount || grant.Exclusions is null ||
                    grant.Exclusions.Count > 128 - exclusionCount || grant.NotBefore >= grant.ExpiresAt ||
                    (grant.ParentGrantId is not null && !AuthorityValidation.ValidToken(grant.ParentGrantId)))
                    throw new ArgumentException("Invalid or excessive grant.");
                grantCount++; exclusionCount += grant.Exclusions.Count;
                var scope = AuthorityValidation.FreezeScope(grant.Scope);
                var actions = grant.Actions.ToArray();
                if (actions.Any(a => !Enum.IsDefined(a)) || actions.Distinct().Count() != actions.Length)
                    throw new ArgumentException("Unknown or duplicate action.");
                var exclusions = new AuthorityScope[grant.Exclusions.Count];
                for (var j = 0; j < exclusions.Length; j++)
                {
                    exclusions[j] = AuthorityValidation.FreezeScope(grant.Exclusions[j]);
                    if (!AuthorityValidation.Contains(scope, exclusions[j]))
                        throw new ArgumentException("Exclusions must remain within their grant scope.");
                }
                grants.Add(new(grant.Id, Array.AsReadOnly(actions), scope, Array.AsReadOnly(exclusions),
                    grant.NotBefore.ToUniversalTime(), grant.ExpiresAt.ToUniversalTime())
                {
                    ParentGrantId = grant.ParentGrantId
                });
            }
            frozen.Add(new(layer.Id, Array.AsReadOnly(grants.ToArray())));
        }
        var denials = mandatoryDenials.Take(129).Select(AuthorityValidation.FreezeScope).ToArray();
        if (denials.Length > 128) throw new ArgumentException("Too many mandatory denials.");
        Layers = Array.AsReadOnly(frozen.ToArray()); MandatoryDenials = Array.AsReadOnly(denials);
        Identity = ComputeIdentity();
    }
    public AuthenticatedAuthorityContext Context { get; }
    public string Version { get; }
    public DateTimeOffset ValidUntil { get; }
    public IReadOnlyList<AuthorityLayer> Layers { get; }
    public IReadOnlyList<AuthorityScope> MandatoryDenials { get; }
    public string Identity { get; }

    /// <summary>
    /// Checks that neither snapshot expiry nor a grant validity transition invalidates
    /// an evaluation across asynchronous work. This does not authenticate current state
    /// or grant permission, and never substitutes for a fresh resource/start check.
    /// </summary>
    public bool HasUnchangedValidity(DateTimeOffset evaluatedAt, DateTimeOffset now) =>
        now >= evaluatedAt && ValidUntil > evaluatedAt && ValidUntil > now &&
        !Layers.SelectMany(layer => layer.Grants).Any(grant =>
            (grant.NotBefore <= evaluatedAt && evaluatedAt < grant.ExpiresAt) !=
            (grant.NotBefore <= now && now < grant.ExpiresAt));
    private string ComputeIdentity()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, AuthorityValidation.StrictUtf8, true);
        Text("Penghou.Hufu.AuthoritySnapshot.v1");
        Text(Context.TenantId); Text(Context.SubjectId); Text(Context.RunId); Text(Context.RevisionId); Text(Context.FenceId);
        Text(Version); Long(ValidUntil.UtcTicks); Int(Layers.Count);
        foreach (var layer in Layers)
        {
            Text(layer.Id); Int(layer.Grants.Count);
            foreach (var grant in layer.Grants)
            {
                Text(grant.Id); Int(grant.Actions.Count);
                foreach (var action in grant.Actions) Int((int)action);
                Scope(grant.Scope); Long(grant.NotBefore.UtcTicks); Long(grant.ExpiresAt.UtcTicks);
                // Parent lineage participates in identity only when present, so
                // pre-lineage snapshots keep their exact historical identity
                // while derived grants are tamper-evident. Empty is rejected at
                // validation, so absent and present can never collide.
                if (grant.ParentGrantId is string parent) Text(parent);
                Int(grant.Exclusions.Count); foreach (var exclusion in grant.Exclusions) Scope(exclusion);
            }
        }
        Int(MandatoryDenials.Count); foreach (var denial in MandatoryDenials) Scope(denial);
        writer.Flush();
        if (stream.Length > 1_048_576) throw new ArgumentException("Authority snapshot exceeds its encoding ceiling.");
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();
        void Ensure(int size) { if (stream.Length + size > 1_048_576) throw new ArgumentException("Authority snapshot exceeds its encoding ceiling."); }
        void Int(int value) { Ensure(4); writer.Write(value); }
        void Long(long value) { Ensure(8); writer.Write(value); }
        void Text(string value) { var count = AuthorityValidation.StrictUtf8.GetByteCount(value); Ensure(4 + count); var bytes = AuthorityValidation.StrictUtf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        void Scope(AuthorityScope scope) { Text(scope.WorkspaceId); Text(scope.RelativePath); Int((int)scope.Kind); }
    }
}

public interface IAuthorityEvaluator
{
    AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now);
}

/// <summary>
/// Required trusted host boundary. Authenticate context and return current grant,
/// revocation, revision and fence facts. Null or unavailable state is never permission.
/// This read interface is not an atomic mutation-start protocol.
/// </summary>
public interface IAuthoritySnapshotSource
{
    ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Required attributable decision evidence; failure blocks protected dispatch or release.</summary>
public interface IAuthorityDecisionRecorder
{
    ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision,
        CancellationToken cancellationToken = default);
}

public static class AuthorityValidation
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>Distinct AuthorityAction values; a grant may carry at most one of each.</summary>
    internal static int KnownActionCount { get; } = Enum.GetValues<AuthorityAction>().Length;
    public static bool ValidToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(value) <= 1024; } catch (EncoderFallbackException) { return false; }
    }
    public static bool ValidContext(AuthenticatedAuthorityContext? context) => context is not null &&
        ValidToken(context.TenantId) && ValidToken(context.SubjectId) && ValidToken(context.RunId) &&
        ValidToken(context.RevisionId) && ValidToken(context.FenceId);
    public static bool IsValidRequest(AuthorityRequest? request)
    {
        if (request is null || !ValidContext(request.Context) || !Enum.IsDefined(request.Action) ||
            !ValidToken(request.WorkspaceId) || !ValidToken(request.RequestIdentity)) return false;
        try { return request.RelativePath == NormalizePath(request.RelativePath); } catch { return false; }
    }
    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > 512 || StrictUtf8.GetByteCount(path) > 2048 || path.Any(char.IsControl) ||
            path.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || path.StartsWith('/') || path.EndsWith('/'))
            throw new ArgumentException("A bounded canonical relative path is required.");
        if (path.Length == 0) return path;
        var parts = path.Split('/');
        if (parts.Length > 16 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new ArgumentException("Invalid canonical path segments.");
        return string.Create(path.Length, path, (span, value) =>
        { for (var i = 0; i < span.Length; i++) span[i] = value[i] is >= 'A' and <= 'Z' ? (char)(value[i] + 32) : value[i]; });
    }
    internal static AuthorityScope FreezeScope(AuthorityScope scope)
    {
        if (scope is null || !ValidToken(scope.WorkspaceId) || !Enum.IsDefined(scope.Kind))
            throw new ArgumentException("Invalid authority scope.");
        return scope with { RelativePath = NormalizePath(scope.RelativePath) };
    }
    public static bool Contains(AuthorityScope parent, AuthorityScope child)
    {
        try { parent = FreezeScope(parent); child = FreezeScope(child); } catch { return false; }
        return parent.WorkspaceId == child.WorkspaceId &&
            (parent.Kind == AuthorityScopeKind.Subtree && (parent.RelativePath.Length == 0 || child.RelativePath == parent.RelativePath ||
                child.RelativePath.StartsWith(parent.RelativePath + "/", StringComparison.Ordinal)) ||
             parent.Kind == AuthorityScopeKind.Exact && child.Kind == AuthorityScopeKind.Exact && child.RelativePath == parent.RelativePath);
    }
}
