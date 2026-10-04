using Penghou.Workflow.Abstractions;

namespace Penghou.Hufu.Workflow;

/// <summary>Proposed time-bounded evaluation evidence before dispatch or approval parking; not a callback dispatch receipt.</summary>
/// <remarks>Result has no evidence reference until recording succeeds. Expiry or a runtime fence can still reject it afterward.</remarks>
public sealed class WorkflowAuthorizationRecord
{
    internal WorkflowAuthorizationRecord(ExecutionAuthorizationContext context, WorkflowAuthorityBinding? binding,
        IEnumerable<AuthorityRequestAuthorization> requestAuthorizations, WorkflowApprovalResult? approval,
        ExecutionAuthorizationResult result)
    {
        Context = context; Binding = binding;
        RequestAuthorizations = Array.AsReadOnly(requestAuthorizations.ToArray());
        Approval = approval; Result = result;
    }
    public ExecutionAuthorizationContext Context { get; }
    public WorkflowAuthorityBinding? Binding { get; }
    public IReadOnlyList<AuthorityRequestAuthorization> RequestAuthorizations { get; }
    public WorkflowApprovalResult? Approval { get; }
    public ExecutionAuthorizationResult Result { get; }
}

/// <summary>Durably record the exact proposed evaluation and return its bounded attributable reference. Null/failure blocks dispatch.</summary>
public interface IWorkflowAuthorizationRecorder
{
    ValueTask<string?> RecordAsync(WorkflowAuthorizationRecord record, CancellationToken cancellationToken = default);
}
