using System.Text.Json;
using Penghou.IO.Abstractions;
using Penghou.Luban.Changes;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Penghou.Luban;

namespace Penghou.Hufu.Luban.Sqlite;

internal sealed class PatchPreviewAuthorizer : IPreviewAuthorizer
{
    private readonly HufuSinglePatchHost _host;
    private readonly EffectInvocation _invocation;
    private readonly CompiledPreviewDocument _document;
    private readonly ExactPatchOperation _operation;
    internal PatchPreviewAuthorizer(HufuSinglePatchHost host, EffectInvocation invocation, CompiledPreviewDocument document)
    {
        if (invocation is null || !AuthorityValidation.ValidToken(invocation.SubjectId) || !AuthorityValidation.ValidToken(invocation.EffectId) ||
            !AuthorityValidation.ValidToken(invocation.AttemptId) || document is null || document.Nodes.Count != 1 ||
            document.Nodes[0].Operation is not ExactPatchOperation operation || document.Limits.MaxFileBytes is < 1 or > 1_048_576 ||
            operation.Path != PatchIdentity.Canonical(operation.Path)) throw new ArgumentException("One bounded canonical exact patch is required.");
        _host = host; _invocation = invocation; _document = document; _operation = operation;
    }
    public async ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Valid(request)) return new(LanguageAuthorityStatus.Deny);
        try
        {
            var path = request.ResourcePath!;
            var requirements = request.Phase switch
            {
                PreviewAuthorizationPhase.ResourceAccess => new[] { (request.Action == ResourceAction.ReadFile ? AuthorityAction.ReadFile : AuthorityAction.ReadMetadata, path) },
                PreviewAuthorizationPhase.Release => new[] { (AuthorityAction.Release, path) },
                _ => new[] { (AuthorityAction.ReadFile, _operation.Path), (AuthorityAction.Release, _operation.Path) }
                    .Concat(PatchIdentity.Ancestors(_operation.Path).Select(p => (AuthorityAction.ReadMetadata, p))).ToArray()
            };
            foreach (var (action, resource) in requirements)
            {
                var identity = PatchIdentity.Hash(JsonSerializer.Serialize(new
                {
                    Domain = "Penghou.Hufu.SinglePatchPreview.v1", request.Invocation, request.DocumentIdentity,
                    request.NodeIdentity, request.Phase, request.Action, request.ResourceRequestIdentity,
                    request.PlanIdentity, AuthorityAction = action, Path = resource,
                    OriginalHash = request.Proposal?.OriginalSha256, ProposedHash = request.Proposal?.ProposedSha256
                }, PatchIdentity.Json));
                var result = await _host.CheckPreview(action, _document.Workspace.Value, resource, identity, _invocation.SubjectId, cancellationToken).ConfigureAwait(false);
                if (!result.IsAuthorized) return new(result.Status == AuthorityStatus.Deny ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Unavailable);
            }
            return new(LanguageAuthorityStatus.Permit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(LanguageAuthorityStatus.Unavailable); }
    }
    private bool Valid(PreviewAuthorizationRequest? r)
    {
        if (r is null || r.Invocation != _invocation || r.DocumentIdentity != _document.Identity || r.NodeIdentity != _document.Nodes[0].Identity ||
            r.Descriptor != "files.patch" || r.SchemaVersion != PreviewProfile.SchemaVersion || r.CatalogueVersion != PreviewProfile.CatalogueVersion ||
            r.ProviderProfile != PreviewProfile.ProviderProfile || r.Workspace != _document.Workspace || r.Operation != _operation ||
            !Enum.IsDefined(r.Phase) || r.ResourcePath is null) return false;
        var exact = r.ResourcePath == _operation.Path;
        var metadata = r.Action == ResourceAction.ReadMetadata && PatchIdentity.Ancestors(_operation.Path).Contains(r.ResourcePath);
        if (!exact && !metadata) return false;
        if (r.Phase == PreviewAuthorizationPhase.Release ? !PatchIdentity.AnyHash(r.PlanIdentity) : r.PlanIdentity is not null) return false;
        if (r.ResourceRequestIdentity is { } identity && (!AuthorityValidation.ValidToken(identity.Value) ||
            !identity.Value.StartsWith(ResourceRequestIdentity.IdentityPrefix, StringComparison.Ordinal) ||
            !PatchIdentity.AnyHash(identity.Value[ResourceRequestIdentity.IdentityPrefix.Length..]))) return false;
        return r.Phase switch
        {
            PreviewAuthorizationPhase.Preflight => exact && r.Action is null && r.Proposal is null && r.ResourceRequestIdentity is null,
            PreviewAuthorizationPhase.TargetAdmission => exact && r.Action == ResourceAction.PatchFile && r.Proposal is null && r.ResourceRequestIdentity is null,
            PreviewAuthorizationPhase.ResourceAccess => (exact && r.Action == ResourceAction.ReadFile || metadata) && r.Proposal is null && r.ResourceRequestIdentity is not null,
            PreviewAuthorizationPhase.ProposalAdmission => exact && r.Action == ResourceAction.PatchFile && r.ResourceRequestIdentity is null && ValidProposal(r.Proposal),
            PreviewAuthorizationPhase.Release => r.Proposal is not null ? exact && r.Action == ResourceAction.PatchFile && r.ResourceRequestIdentity is null && ValidProposal(r.Proposal) :
                exact && r.Action is null && r.ResourceRequestIdentity is null || (exact && r.Action == ResourceAction.ReadFile || metadata) && r.ResourceRequestIdentity is not null,
            _ => false
        };
    }
    private bool ValidProposal(CapturedFilePatch? p)
    {
        try
        {
            if (p is null || p.RelativePath != _operation.Path || p.OriginalContent is null || p.ProposedContent is null ||
                p.OriginalByteLength != p.OriginalContent.Length || p.ProposedByteLength != p.ProposedContent.Length ||
                p.OriginalSha256 != p.OriginalContent.Sha256 || p.ProposedSha256 != p.ProposedContent.Sha256 ||
                p.OriginalVersion.Value != PatchIdentity.VersionPrefix + p.OriginalSha256 ||
                _operation.ExpectedVersion is { } expected && expected != p.OriginalVersion ||
                p.OriginalByteLength > _document.Limits.MaxFileBytes || p.ProposedByteLength > _document.Limits.MaxFileBytes ||
                (long)p.OriginalByteLength + p.ProposedByteLength > _document.Limits.MaxReadBytes || p.Patches.Count != _operation.Patches.Count) return false;
            for (var i = 0; i < p.Patches.Count; i++)
                if (p.Patches[i].StartOffset != _operation.Patches[i].StartOffset || p.Patches[i].DeleteLength != _operation.Patches[i].DeleteLength ||
                    !p.Patches[i].ReplacementUtf8.ToArray().AsSpan().SequenceEqual(_operation.Patches[i].ReplacementUtf8.ToArray())) return false;
            var patches = _operation.Patches.Select(t => new TextPatch(t.StartOffset, t.DeleteLength, t.ReplacementUtf8.ToArray())).ToArray();
            var bytes = Utf8PatchMaterializer.Materialize(p.OriginalContent.ToArray(), patches,
                new PatchLimits(_document.Limits.MaxPatchCount, _document.Limits.MaxReplacementBytes, _document.Limits.MaxFileBytes));
            return bytes.AsSpan().SequenceEqual(p.ProposedContent.ToArray());
        }
        catch { return false; }
    }
}
