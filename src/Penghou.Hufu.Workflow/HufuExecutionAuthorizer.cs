using Penghou.Workflow.Abstractions;

namespace Penghou.Hufu.Workflow;

/// <summary>Finite, resource-scoped translation of the neutral execution contract into fresh Hufu decisions.</summary>
/// <remarks>Requires trusted binding, explicit approval policy and mandatory evidence. It does not fence runtime claims or actual effects.</remarks>
public sealed class HufuExecutionAuthorizer : IExecutionAuthorizer
{
    private readonly string providerId;
    private readonly string hostNamespace;
    private readonly string mappingId;
    private readonly IWorkflowAuthorityBindingSource bindings;
    private readonly IAuthorityRequestAuthorizer authority;
    private readonly IWorkflowApprovalCoordinator approvals;
    private readonly IWorkflowAuthorizationRecorder recorder;
    private readonly TimeProvider clock;
    private readonly TimeSpan maximumValidity;

    public HufuExecutionAuthorizer(string providerId, string hostNamespace, string mappingId,
        IWorkflowAuthorityBindingSource bindings, IAuthorityRequestAuthorizer authority,
        IWorkflowApprovalCoordinator approvals, IWorkflowAuthorizationRecorder recorder,
        TimeProvider? timeProvider = null, TimeSpan? maximumValidity = null)
    {
        WorkflowBounds.Token(providerId, nameof(providerId));
        WorkflowBounds.Token(hostNamespace, nameof(hostNamespace));
        WorkflowBounds.Token(mappingId, nameof(mappingId));
        this.providerId = providerId; this.hostNamespace = hostNamespace; this.mappingId = mappingId;
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        this.recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        clock = timeProvider ?? TimeProvider.System;
        this.maximumValidity = maximumValidity ?? TimeSpan.FromSeconds(30);
        if (this.maximumValidity <= TimeSpan.Zero || this.maximumValidity > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maximumValidity));
    }

    public async ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(ExecutionAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = new CancellationTokenSource(maximumValidity, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var serviceToken = linked.Token;
        var evaluatedAt = clock.GetUtcNow();
        var deadline = evaluatedAt.Add(maximumValidity);
        WorkflowAuthorityBinding? binding = null;
        WorkflowApprovalResult? approval = null;
        var decisions = new List<AuthorityRequestAuthorization>();
        var outcome = ExecutionAuthorizationDecision.Unavailable;
        var reason = "hufu.workflow.unavailable";
        try
        {
            if (context.Requirements.Count == 0 || context.Requirements.Any(r => !WorkflowAuthorityRequirements.TryGetAction(r, out _)))
            {
                outcome = ExecutionAuthorizationDecision.Denied; reason = "hufu.workflow.unsupported-requirements";
            }
            else
            {
                binding = await bindings.ResolveAsync(context, serviceToken).AsTask().WaitAsync(serviceToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (binding is null || !binding.Matches(context, hostNamespace, mappingId) || binding.ValidUntil <= clock.GetUtcNow())
                    reason = "hufu.workflow.binding-unavailable";
                else
                {
                    deadline = Min(deadline, binding.ValidUntil);
                    outcome = ExecutionAuthorizationDecision.Allowed;
                    for (var i = 0; i < binding.Targets.Count; i++)
                    {
                        var target = binding.Targets[i];
                        WorkflowAuthorityRequirements.TryGetAction(target.Requirement, out var action);
                        var request = new AuthorityRequest(binding.AuthorityContext, action, target.WorkspaceId,
                            target.RelativePath, $"wf:{binding.Identity}:{i}");
                        var decision = await authority.AuthorizeAsync(request, serviceToken).AsTask().WaitAsync(serviceToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (decision is null || decision.Request != request || !Enum.IsDefined(decision.Status))
                        {
                            outcome = ExecutionAuthorizationDecision.Unavailable; reason = "hufu.workflow.authority-malformed"; break;
                        }
                        decisions.Add(decision);
                        if (decision.Status == AuthorityStatus.Deny && decision.Decision?.Status == AuthorityStatus.Deny && decision.EvidenceRecorded)
                        {
                            outcome = ExecutionAuthorizationDecision.Denied; reason = "hufu.workflow.authority-denied"; break;
                        }
                        if (!decision.IsAuthorized)
                        {
                            outcome = ExecutionAuthorizationDecision.Unavailable; reason = "hufu.workflow.authority-unavailable"; break;
                        }
                        if (clock.GetUtcNow() < evaluatedAt || clock.GetUtcNow() >= deadline)
                        {
                            outcome = ExecutionAuthorizationDecision.Unavailable; reason = "hufu.workflow.expired"; break;
                        }
                    }
                    if (outcome == ExecutionAuthorizationDecision.Allowed)
                    {
                        approval = await approvals.EvaluateAsync(binding, serviceToken).AsTask().WaitAsync(serviceToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (approval is null || approval.BindingIdentity != binding.Identity || approval.ValidUntil <= clock.GetUtcNow())
                        {
                            outcome = ExecutionAuthorizationDecision.Unavailable; reason = "hufu.workflow.approval-unavailable";
                        }
                        else
                        {
                            deadline = Min(deadline, approval.ValidUntil);
                            (outcome, reason) = approval.Decision switch
                            {
                                WorkflowApprovalDecision.NotRequired or WorkflowApprovalDecision.Approved =>
                                    (ExecutionAuthorizationDecision.Allowed, "hufu.workflow.allowed"),
                                WorkflowApprovalDecision.Required => (ExecutionAuthorizationDecision.ApprovalRequired, "hufu.workflow.approval-required"),
                                WorkflowApprovalDecision.Denied => (ExecutionAuthorizationDecision.Denied, "hufu.workflow.approval-denied"),
                                _ => (ExecutionAuthorizationDecision.Unavailable, "hufu.workflow.approval-unavailable")
                            };
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { outcome = ExecutionAuthorizationDecision.Unavailable; reason = "hufu.workflow.provider-unavailable"; }

        var now = clock.GetUtcNow();
        if ((outcome is ExecutionAuthorizationDecision.Allowed or ExecutionAuthorizationDecision.ApprovalRequired) &&
            (now < evaluatedAt || now >= deadline))
        {
            outcome = ExecutionAuthorizationDecision.Unavailable; reason = "hufu.workflow.expired";
        }
        var decisionId = Guid.NewGuid().ToString("N");
        var result = Create(outcome, reason);
        try
        {
            var evidence = await recorder.RecordAsync(new(context, binding, decisions, approval, result), serviceToken)
                .AsTask().WaitAsync(serviceToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (evidence is null) return Create(ExecutionAuthorizationDecision.Unavailable, "hufu.workflow.evidence-unavailable");
            WorkflowBounds.Token(evidence, nameof(evidence));
            now = clock.GetUtcNow();
            if ((outcome is ExecutionAuthorizationDecision.Allowed or ExecutionAuthorizationDecision.ApprovalRequired) &&
                (now < evaluatedAt || now >= deadline))
                return Create(ExecutionAuthorizationDecision.Unavailable, "hufu.workflow.expired");
            return Create(outcome, reason, evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Create(ExecutionAuthorizationDecision.Unavailable, "hufu.workflow.evidence-unavailable"); }

        ExecutionAuthorizationResult Create(ExecutionAuthorizationDecision status, string code, string? evidence = null) =>
            new(status, context.AuthorizationRequestId, providerId, decisionId, evaluatedAt,
                expiresAt: status is ExecutionAuthorizationDecision.Allowed or ExecutionAuthorizationDecision.ApprovalRequired ? deadline : null,
                approvalRequestId: status == ExecutionAuthorizationDecision.ApprovalRequired ? approval!.ApprovalRequestId : null,
                reasonCode: code, evidenceId: evidence);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
