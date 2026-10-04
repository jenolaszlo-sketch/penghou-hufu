namespace Penghou.Hufu.Workflow;

/// <summary>Trusted approval policy outcomes; the default is fail-closed.</summary>
public enum WorkflowApprovalDecision { Unavailable = 0, NotRequired = 1, Required = 2, Approved = 3, Denied = 4 }

/// <summary>A typed approval result attributable to the entire current host-authenticated binding.</summary>
public sealed record WorkflowApprovalResult
{
    public WorkflowApprovalResult(WorkflowApprovalDecision decision, string bindingIdentity, string evidenceId,
        DateTimeOffset validUntil, string? approvalRequestId = null)
    {
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        WorkflowBounds.Token(bindingIdentity, nameof(bindingIdentity));
        WorkflowBounds.Token(evidenceId, nameof(evidenceId));
        if (validUntil == default) throw new ArgumentOutOfRangeException(nameof(validUntil));
        if ((decision == WorkflowApprovalDecision.Required) != (approvalRequestId is not null))
            throw new ArgumentException("Only Required has pending correlation.", nameof(approvalRequestId));
        if (approvalRequestId is not null) WorkflowBounds.Token(approvalRequestId, nameof(approvalRequestId));
        Decision = decision; BindingIdentity = bindingIdentity; EvidenceId = evidenceId;
        ValidUntil = validUntil.ToUniversalTime(); ApprovalRequestId = approvalRequestId;
    }
    public WorkflowApprovalDecision Decision { get; }
    public string BindingIdentity { get; }
    public string EvidenceId { get; }
    public DateTimeOffset ValidUntil { get; }
    public string? ApprovalRequestId { get; }
}

/// <summary>Trusted approval policy/custody. Required means pending; Approved never skips current Hufu authority checks.</summary>
/// <remarks>The service must validate retained approval facts and rebind them to each fresh full context. No reason-code parsing.</remarks>
public interface IWorkflowApprovalCoordinator
{
    ValueTask<WorkflowApprovalResult?> EvaluateAsync(WorkflowAuthorityBinding binding, CancellationToken cancellationToken = default);
}
