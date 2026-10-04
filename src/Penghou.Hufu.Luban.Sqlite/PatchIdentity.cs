using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Penghou.IO.Abstractions;
using Penghou.Luban.Changes;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;

namespace Penghou.Hufu.Luban.Sqlite;

internal static class PatchIdentity
{
    internal const string WriterProfile = "local-windows-ntfs-controlled-write-v1";
    internal const string ParticipantProfile = "hufu-sqlite-single-patch-start-v1";
    internal const string VersionPrefix = "local-read-v1:sha256:";
    internal const int MaxRecordBytes = 32_768;
    internal static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text))).ToLowerInvariant();
    internal static bool IsHash(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool AnyHash(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
    private static bool RequestHash(string? text) => text is not null && text.StartsWith(ResourceRequestIdentity.IdentityPrefix, StringComparison.Ordinal) && AnyHash(text[ResourceRequestIdentity.IdentityPrefix.Length..]);
    internal static string Canonical(string path) => AuthorityValidation.NormalizePath(WindowsWorkspacePath.Normalize(new(path), false).Value);

    internal static bool Valid(AuthenticatedAuthorityContext context, PatchAdmissionRequest? a)
    {
        try
        {
            if (!AuthorityValidation.ValidContext(context) || a is null || a.Document is null || a.Plan is null || a.Invocation is null ||
                a.WriterProfile != WriterProfile || !AuthorityValidation.ValidToken(a.OperationId) ||
                a.Invocation.SubjectId != context.SubjectId || a.Invocation.InvocationId != a.OperationId ||
                a.Invocation.RequestIdentity != a.ResourceRequestIdentity || !RequestHash(a.ResourceRequestIdentity.Value) ||
                a.Invocation.SubjectId != a.Plan.Invocation.SubjectId || a.Invocation.EffectId != a.Plan.Invocation.EffectId ||
                a.Invocation.AttemptId != a.Plan.Invocation.AttemptId || !AuthorityValidation.ValidToken(a.Invocation.EffectId) ||
                !AuthorityValidation.ValidToken(a.Invocation.AttemptId) || a.Invocation.EffectScopeId != a.Plan.Identity ||
                a.Invocation.ParentEffectScopeId != a.Document.Identity || a.Plan.DocumentIdentity != a.Document.Identity ||
                a.Plan.Workspace != a.Document.Workspace || !AuthorityValidation.ValidToken(a.Plan.Workspace.Value) ||
                a.Plan.Limits != a.Document.Limits || a.Plan.Nodes.Count != 1 || a.Document.Nodes.Count != 1 ||
                !a.Plan.CaptureComplete || a.Document.Nodes[0].Operation is not ExactPatchOperation exact ||
                a.Plan.Nodes[0].Proposals.Count != 1 || a.Document.Limits.MaxFileBytes is < 1 or > 1_048_576 ||
                a.Plan.SchemaVersion != PreviewProfile.SchemaVersion || a.Plan.CatalogueVersion != PreviewProfile.CatalogueVersion ||
                a.Plan.ProviderProfile != PreviewProfile.ProviderProfile) return false;
            var node = a.Plan.Nodes[0];
            var p = node.Proposals[0];
            if (node.NodeIdentity != a.Document.Nodes[0].Identity || node.Descriptor != "files.patch" ||
                node.State != PreviewNodeState.Proposed || node.Selection != PreviewSelection.Unspecified ||
                node.UnresolvedReason is not null || node.Dependencies.Count != 0 ||
                exact.ExpectedVersion is { } expected && expected != p.OriginalVersion ||
                p.RelativePath != exact.Path || p.RelativePath != Canonical(p.RelativePath) ||
                p.OriginalContent is null || p.ProposedContent is null ||
                p.OriginalByteLength != p.OriginalContent.Length || p.ProposedByteLength != p.ProposedContent.Length ||
                p.OriginalSha256 != p.OriginalContent.Sha256 || p.ProposedSha256 != p.ProposedContent.Sha256 ||
                p.OriginalVersion.Value != VersionPrefix + p.OriginalSha256 ||
                p.OriginalByteLength > a.Document.Limits.MaxFileBytes || p.ProposedByteLength > a.Document.Limits.MaxFileBytes ||
                (long)p.OriginalByteLength + p.ProposedByteLength > a.Document.Limits.MaxReadBytes ||
                p.Patches.Count != exact.Patches.Count) return false;
            for (var i = 0; i < exact.Patches.Count; i++)
                if (p.Patches[i].StartOffset != exact.Patches[i].StartOffset || p.Patches[i].DeleteLength != exact.Patches[i].DeleteLength ||
                    !p.Patches[i].ReplacementUtf8.ToArray().AsSpan().SequenceEqual(exact.Patches[i].ReplacementUtf8.ToArray())) return false;
            var patches = exact.Patches.Select(t => new TextPatch(t.StartOffset, t.DeleteLength, t.ReplacementUtf8.ToArray())).ToArray();
            var recompiled = PreviewCompiler.Compile([new FilePatchStage(exact.Path, patches, exact.ExpectedVersion)], a.Plan.Workspace, a.Document.Limits);
            if (!recompiled.Succeeded || recompiled.Document!.Identity != a.Document.Identity ||
                recompiled.Document.Nodes[0].Identity != node.NodeIdentity) return false;
            var materialized = Utf8PatchMaterializer.Materialize(p.OriginalContent.ToArray(), patches,
                new PatchLimits(a.Document.Limits.MaxPatchCount, a.Document.Limits.MaxReplacementBytes, a.Document.Limits.MaxFileBytes));
            if (!materialized.AsSpan().SequenceEqual(p.ProposedContent.ToArray())) return false;
            var reads = a.Plan.Observations;
            if (reads.Count != 1 || reads[0].NodeIdentity != node.NodeIdentity || reads[0].Action != ResourceAction.ReadFile ||
                reads[0].RelativePath != p.RelativePath || reads[0].Version != p.OriginalVersion ||
                reads[0].Digest != p.OriginalSha256 || reads[0].ByteLength != p.OriginalByteLength || !reads[0].IsComplete) return false;
            return ResourceRequestIdentity.Compute(new FileWriteRequest(a.Invocation, a.Plan.Workspace, new(p.RelativePath),
                p.ProposedContent.ToArray(), new(a.Document.Limits.MaxFileBytes),
                new(WritePreconditionKind.MustMatchVersion, p.OriginalVersion))) == a.ResourceRequestIdentity;
        }
        catch { return false; }
    }

    internal static string Admission(AuthenticatedAuthorityContext context, PatchAdmissionRequest a)
    {
        if (!Valid(context, a)) throw new ArgumentException("An exact bounded single-target admission is required.");
        return Hash("Penghou.Hufu.SinglePatchAdmission.v1\n" + JsonSerializer.Serialize(new
        {
            Context = context, a.OperationId, a.Invocation, a.ResourceRequestIdentity, a.WriterProfile,
            DocumentIdentity = a.Document.Identity, PlanIdentity = a.Plan.Identity,
            NodeIdentity = a.Document.Nodes[0].Identity, a.Plan.Workspace, a.Plan.Limits,
            a.Plan.SchemaVersion, a.Plan.CatalogueVersion, a.Plan.ProviderProfile
        }, Json));
    }

    internal static bool Matches(AuthenticatedAuthorityContext context, PatchAdmissionRequest a, MutationStartRequest? m) =>
        Valid(context, a) && m is not null && m.Invocation == a.Invocation && m.RequestIdentity == a.ResourceRequestIdentity &&
        m.Workspace == a.Plan.Workspace && m.Path.Value == a.Plan.Nodes[0].Proposals[0].RelativePath &&
        m.ProviderProfile == WriterProfile && AuthorityValidation.ValidToken(m.ObjectIdentity) &&
        m.OriginalVersion == a.Plan.Nodes[0].Proposals[0].OriginalVersion &&
        m.ProposedVersion.Value == VersionPrefix + a.Plan.Nodes[0].Proposals[0].ProposedSha256 &&
        m.OriginalByteLength == a.Plan.Nodes[0].Proposals[0].OriginalByteLength && m.ProposedByteLength == a.Plan.Nodes[0].Proposals[0].ProposedByteLength;

    internal static string Binding(PatchOutcomeRecord r) => JsonSerializer.Serialize(new
    {
        Profile = ParticipantProfile, r.Actor, r.Context, r.OperationId, r.AdmissionIdentity,
        r.DocumentIdentity, r.NodeIdentity, r.PlanIdentity, r.Start, r.DecisionCommandId, r.SnapshotSequence
    }, Json);
    internal static AuthorityOperationStartCommand Command(PatchOutcomeRecord r) => new(r.Actor, r.OperationId,
        new(r.Context, AuthorityAction.PatchFile, r.Start.Workspace.Value, r.Start.Path.Value, r.Start.RequestIdentity.Value),
        r.DecisionCommandId, r.SnapshotSequence, r.BindingIdentity, r.BindingJson);
    internal static string Evidence(PatchOutcomeRecord r) => "hufu-patch-start:" + Hash(JsonSerializer.Serialize(Command(r), Json));

    internal static bool ValidRecord(PatchOutcomeRecord? r)
    {
        try
        {
            if (r is null || !AuthorityStoreValidation.ValidActor(r.Actor) || !AuthorityValidation.ValidContext(r.Context) ||
                r.Actor.TenantId != r.Context.TenantId || !AuthorityValidation.ValidToken(r.OperationId) || !IsHash(r.AdmissionIdentity) ||
                !AnyHash(r.DocumentIdentity) || !AnyHash(r.NodeIdentity) || !AnyHash(r.PlanIdentity) || r.Start is null ||
                r.Start.Invocation is null || r.Start.Invocation.SubjectId != r.Context.SubjectId ||
                r.Start.Invocation.InvocationId != r.OperationId || r.Start.Invocation.ParentEffectScopeId != r.DocumentIdentity ||
                r.Start.Invocation.EffectScopeId != r.PlanIdentity || r.Start.Invocation.RequestIdentity != r.Start.RequestIdentity ||
                !RequestHash(r.Start.RequestIdentity.Value) || !AuthorityValidation.ValidToken(r.Start.Invocation.EffectId) ||
                !AuthorityValidation.ValidToken(r.Start.Invocation.AttemptId) || !AuthorityValidation.ValidToken(r.Start.Workspace.Value) ||
                r.Start.Path.Value != Canonical(r.Start.Path.Value) || r.Start.ProviderProfile != WriterProfile ||
                !AuthorityValidation.ValidToken(r.Start.ObjectIdentity) || !Version(r.Start.OriginalVersion) || !Version(r.Start.ProposedVersion) ||
                r.Start.OriginalByteLength is < 0 or > 1_048_576 || r.Start.ProposedByteLength is < 0 or > 1_048_576 ||
                !AuthorityValidation.ValidToken(r.DecisionCommandId) || r.SnapshotSequence < 1 ||
                r.BindingJson != Binding(r) || r.BindingIdentity != Hash(r.BindingJson) || !Enum.IsDefined(r.State) ||
                r.State is PatchRecoveryState.NotFound or PatchRecoveryState.Unavailable) return false;
            if (r.State == PatchRecoveryState.Reserved) return r.EvidenceId is null && r.ObservedVersion is null;
            if (r.EvidenceId != Evidence(r)) return false;
            return r.State switch
            {
                PatchRecoveryState.Started => r.ObservedVersion is null,
                PatchRecoveryState.Completed => r.ObservedVersion == r.Start.ProposedVersion,
                PatchRecoveryState.NoMutation => r.ObservedVersion is null || r.ObservedVersion == r.Start.OriginalVersion,
                PatchRecoveryState.Ambiguous => r.ObservedVersion is null || Version(r.ObservedVersion.Value),
                _ => false
            };
        }
        catch { return false; }
    }
    private static bool Version(ResourceVersion v) => v.Value is { } text && text.StartsWith(VersionPrefix, StringComparison.Ordinal) && AnyHash(text[VersionPrefix.Length..]);
    internal static IEnumerable<string> Ancestors(string path)
    {
        yield return ""; var current = "";
        foreach (var part in path.Split('/')) { current = current.Length == 0 ? part : current + "/" + part; yield return current; }
    }
}
