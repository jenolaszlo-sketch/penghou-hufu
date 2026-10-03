using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;

namespace Penghou.Hufu.IO;

/// <summary>
/// Adapts a host-authenticated Hufu context to the neutral IO authorization
/// hook. The provider must still call this hook for each discovered resource.
/// </summary>
public sealed class HufuResourceAuthorizer : IResourceAuthorizer
{
    private readonly AuthenticatedAuthorityContext _context;
    private readonly HostInvocation _invocation;
    private readonly WorkspaceId _workspace;
    private readonly IAuthorityRequestAuthorizer _requestAuthorizer;

    public HufuResourceAuthorizer(AuthenticatedAuthorityContext context, HostInvocation invocation,
        WorkspaceId workspace, IAuthoritySnapshotSource source, IAuthorityEvaluator evaluator,
        IAuthorityDecisionRecorder recorder, TimeProvider? timeProvider = null)
        : this(context, invocation, workspace,
            new CurrentAuthorityRequestAuthorizer(source, evaluator, recorder, timeProvider))
    {
    }

    public HufuResourceAuthorizer(AuthenticatedAuthorityContext context, HostInvocation invocation,
        WorkspaceId workspace, IAuthorityRequestAuthorizer requestAuthorizer)
    {
        if (!AuthorityValidation.ValidContext(context) || invocation is null ||
            !AuthorityValidation.ValidToken(invocation.InvocationId) ||
            !AuthorityValidation.ValidToken(invocation.SubjectId) || invocation.SubjectId != context.SubjectId ||
            !AuthorityValidation.ValidToken(invocation.EffectId) || !AuthorityValidation.ValidToken(invocation.AttemptId) ||
            !AuthorityValidation.ValidToken(invocation.EffectScopeId) ||
            (invocation.ParentEffectScopeId is not null && !AuthorityValidation.ValidToken(invocation.ParentEffectScopeId)) ||
            !AuthorityValidation.ValidToken(invocation.RequestIdentity.Value) ||
            !AuthorityValidation.ValidToken(workspace.Value))
            throw new ArgumentException("A bounded authenticated Hufu context and host invocation are required.");
        _context = context;
        _invocation = invocation;
        _workspace = workspace;
        _requestAuthorizer = requestAuthorizer ?? throw new ArgumentNullException(nameof(requestAuthorizer));
    }

    public async ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryMap(request, out var action, out var path))
            return new(AuthorizationStatus.Deny, "hufu-resource.invalid-request");

        var authorityRequest = new AuthorityRequest(_context, action, _workspace.Value, path,
            RequestDigest(request, action, path));
        AuthorityRequestAuthorization? result;
        try
        {
            result = await _requestAuthorizer.AuthorizeAsync(authorityRequest, cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(AuthorizationStatus.Unavailable, "hufu-resource.authority-unavailable"); }
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null || result.Request != authorityRequest || !Enum.IsDefined(result.Status) ||
            result.Decision is null || result.Decision.Status != result.Status || !result.EvidenceRecorded ||
            !AuthorityValidation.ValidToken(result.Decision.ReasonCode) ||
            !AuthorityValidation.ValidToken(result.Decision.SnapshotVersion) ||
            !AuthorityValidation.ValidToken(result.Decision.EvaluatorIdentity) ||
            result.Decision.SnapshotIdentity is not { Length: 64 } hash ||
            hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return new(AuthorizationStatus.Unavailable, "hufu-resource.invalid-decision");
        if (result.Status == AuthorityStatus.Permit && !result.IsAuthorized)
            return new(AuthorizationStatus.Unavailable, "hufu-resource.evidence-unavailable");
        return result.Status switch
        {
            AuthorityStatus.Permit => new(AuthorizationStatus.Permit, result.Decision.ReasonCode),
            AuthorityStatus.Deny => new(AuthorizationStatus.Deny, result.Decision.ReasonCode),
            _ => new(AuthorizationStatus.Unavailable, result.Decision.ReasonCode)
        };
    }

    private bool TryMap(ResourceAuthorizationRequest? request, out AuthorityAction action, out string path)
    {
        action = default;
        path = "";
        if (request is null || request.Invocation is null ||
            !SameInvocation(request.Invocation, _invocation) ||
            request.SnapshotRequestIdentity != request.Invocation.RequestIdentity ||
            !AuthorityValidation.ValidToken(request.SnapshotRequestIdentity.Value) ||
            !Enum.IsDefined(request.Action) || request.Resource is null) return false;

        action = request.Action switch
        {
            ResourceAction.ReadFile => AuthorityAction.ReadFile,
            ResourceAction.ListDirectory => AuthorityAction.ListDirectory,
            ResourceAction.ReadMetadata => AuthorityAction.ReadMetadata,
            ResourceAction.PatchFile => AuthorityAction.PatchFile,
            ResourceAction.WriteFile => AuthorityAction.WriteFile,
            _ => (AuthorityAction)(-1)
        };
        if (!Enum.IsDefined(action)) return false;

        WorkspaceId resourceWorkspace;
        WorkspacePath resourcePath;
        switch (request.Resource)
        {
            case ResourceBinding.WorkspaceFile file:
                resourceWorkspace = file.Workspace;
                resourcePath = file.Path;
                if (request.Action is ResourceAction.ListDirectory) return false;
                break;
            case ResourceBinding.WorkspaceDirectory directory:
                resourceWorkspace = directory.Workspace;
                resourcePath = directory.Path;
                if (request.Action is ResourceAction.ReadFile or ResourceAction.WriteFile or ResourceAction.PatchFile) return false;
                break;
            case ResourceBinding.WorkspaceEntry entry:
                resourceWorkspace = entry.Workspace;
                resourcePath = entry.Path;
                if (request.Action is ResourceAction.ReadFile or ResourceAction.WriteFile or ResourceAction.PatchFile) return false;
                break;
            default:
                return false;
        }
        if (resourceWorkspace != _workspace || string.IsNullOrEmpty(resourceWorkspace.Value)) return false;
        try
        {
            path = AuthorityValidation.NormalizePath(WindowsWorkspacePath.ToIdentityPath(resourcePath));
            return true;
        }
        catch { return false; }
    }

    private static bool SameInvocation(HostInvocation actual, HostInvocation expected) =>
        actual with { RequestIdentity = default } == expected with { RequestIdentity = default };

    private static string RequestDigest(ResourceAuthorizationRequest request, AuthorityAction action, string path)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        Text("Penghou.Hufu.IO.ResourceDecision.v1");
        Text(request.Invocation.InvocationId); Text(request.Invocation.SubjectId); Text(request.Invocation.EffectId);
        Text(request.Invocation.AttemptId); Text(request.Invocation.EffectScopeId);
        Text(request.Invocation.ParentEffectScopeId ?? ""); Text(request.SnapshotRequestIdentity.Value);
        Text(request.Resource is ResourceBinding.WorkspaceDirectory ? "directory" :
            request.Resource is ResourceBinding.WorkspaceEntry ? "entry" : "file");
        Text(request.Resource is ResourceBinding.WorkspaceFile file ? file.Workspace.Value :
            request.Resource is ResourceBinding.WorkspaceDirectory directory ? directory.Workspace.Value :
            ((ResourceBinding.WorkspaceEntry)request.Resource).Workspace.Value);
        writer.Write((int)action); Text(path);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();

        void Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }
}
