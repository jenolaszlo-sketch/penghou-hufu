using System.Text.Json.Serialization;

namespace Penghou.Hufu.Biscuit;

/// <summary>Immutable trusted registration of exact canonical bytes and a closed grant lineage.</summary>
public sealed class BiscuitCredentialRegistration
{
    [JsonConstructor]
    internal BiscuitCredentialRegistration(string fingerprint, int tokenLength, BiscuitWorkloadBinding binding,
        string rootKeyId, string issuerId, string layerId, string grantVersion, string authorityScopeId,
        string restrictionId, AuthorityGrant rootGrant, AuthorityGrant effectiveGrant,
        IReadOnlyList<string> revocationIds, string? parentFingerprint, int blockCount, int sourceBytes, int checkCount)
    {
        if (!BiscuitProfile.IsHash(fingerprint) || tokenLength is < 1 or > BiscuitProfile.MaximumTokenBytes ||
            !BiscuitProfile.ValidBinding(binding) || !AuthorityValidation.ValidToken(rootKeyId) ||
            !AuthorityValidation.ValidToken(issuerId) || !AuthorityValidation.ValidToken(layerId) ||
            !AuthorityValidation.ValidToken(grantVersion) || !AuthorityValidation.ValidToken(authorityScopeId) ||
            !AuthorityValidation.ValidToken(restrictionId) || blockCount is < 1 or > BiscuitProfile.MaximumBlocks ||
            sourceBytes is < 1 or > BiscuitProfile.MaximumSourceBytes || checkCount is < 0 or > BiscuitProfile.MaximumChecks ||
            parentFingerprint is not null && !BiscuitProfile.IsHash(parentFingerprint) ||
            (blockCount == 1) != (parentFingerprint is null))
            throw new ArgumentException("Invalid bounded credential registration.");
        ArgumentNullException.ThrowIfNull(revocationIds);
        if (revocationIds.Count != blockCount || revocationIds.Any(s => s is null || s.Length is < 2 or > 512 ||
            s.Length % 2 != 0 || !s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            revocationIds.Distinct(StringComparer.Ordinal).Count() != revocationIds.Count)
            throw new ArgumentException("Invalid credential revocation chain.");
        Fingerprint = fingerprint; TokenLength = tokenLength; Binding = binding; RootKeyId = rootKeyId;
        IssuerId = issuerId; LayerId = layerId; GrantVersion = grantVersion; AuthorityScopeId = authorityScopeId;
        RestrictionId = restrictionId; RootGrant = BiscuitProfile.FreezeGrant(rootGrant);
        EffectiveGrant = BiscuitProfile.FreezeGrant(effectiveGrant);
        if (!BiscuitProfile.IsSubset(RootGrant, EffectiveGrant)) throw new ArgumentException("Effective grant exceeds its root.");
        RevocationIds = Array.AsReadOnly(revocationIds.ToArray()); ParentFingerprint = parentFingerprint;
        BlockCount = blockCount; SourceBytes = sourceBytes; CheckCount = checkCount;
    }
    public string Fingerprint { get; }
    public int TokenLength { get; }
    public BiscuitWorkloadBinding Binding { get; }
    public string RootKeyId { get; }
    public string IssuerId { get; }
    public string LayerId { get; }
    public string GrantVersion { get; }
    public string AuthorityScopeId { get; }
    public string RestrictionId { get; }
    public AuthorityGrant RootGrant { get; }
    public AuthorityGrant EffectiveGrant { get; }
    public IReadOnlyList<string> RevocationIds { get; }
    public string? ParentFingerprint { get; }
    public int BlockCount { get; }
    public int SourceBytes { get; }
    public int CheckCount { get; }
    [JsonIgnore]
    public string GrantVersionIdentity => BiscuitCodec.GrantVersionIdentity(this);
    public override string ToString() => "BiscuitCredentialRegistration(hufu-biscuit-v1; credential not stored)";
}

[JsonSerializable(typeof(BiscuitCredentialRegistration))]
[JsonSerializable(typeof(BiscuitDecisionEvidence))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal partial class BiscuitJsonContext : JsonSerializerContext;
