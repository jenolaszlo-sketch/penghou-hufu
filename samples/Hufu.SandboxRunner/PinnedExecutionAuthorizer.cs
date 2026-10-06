using Penghou.Hufu;

namespace Hufu.SandboxRunner;

/// <summary>
/// Sample host authorizer: permits <see cref="AuthorityAction.ExecuteProcess"/>
/// only for one pinned workspace/executable pair and denies everything else.
/// Host policy for this sample, not infrastructure: the pin is fixed in code,
/// the snapshot identity binds the exact profile revision and executable, and
/// every decision is returned to the runner, whose stdout JSON is the evidence
/// sink for this sample run.
/// </summary>
public sealed class PinnedExecutionAuthorizer : IAuthorityRequestAuthorizer
{
    private readonly string _workspaceId;
    private readonly string _relativePath;
    private readonly string _snapshotIdentity;

    public PinnedExecutionAuthorizer(string workspaceId, string relativePath, string snapshotIdentity)
    {
        if (!AuthorityValidation.ValidToken(workspaceId))
            throw new ArgumentException("A bounded workspace id is required.", nameof(workspaceId));
        if (string.IsNullOrEmpty(relativePath))
            throw new ArgumentException("A canonical relative path is required.", nameof(relativePath));
        if (snapshotIdentity is not { Length: 64 })
            throw new ArgumentException("A 64-character snapshot identity is required.", nameof(snapshotIdentity));
        _workspaceId = workspaceId;
        _relativePath = relativePath;
        _snapshotIdentity = snapshotIdentity;
    }

    /// <summary>Identity of the most recent authorization request, for audit correlation.</summary>
    public string? LastRequestIdentity { get; private set; }

    public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestIdentity = request?.RequestIdentity;
        if (request is null || !AuthorityValidation.IsValidRequest(request) ||
            request.Action != AuthorityAction.ExecuteProcess ||
            !string.Equals(request.WorkspaceId, _workspaceId, StringComparison.Ordinal) ||
            !string.Equals(request.RelativePath, _relativePath, StringComparison.Ordinal))
            return ValueTask.FromResult(new AuthorityRequestAuthorization(request!, AuthorityStatus.Deny));
        var decision = new AuthorityDecision(AuthorityStatus.Permit, "sandbox-runner.pinned-approval",
            "runner-snapshot-v1", "sandbox-runner-v1", _snapshotIdentity);
        return ValueTask.FromResult(new AuthorityRequestAuthorization(request, AuthorityStatus.Permit, decision, true));
    }
}
