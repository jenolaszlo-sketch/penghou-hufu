using System.Collections.Concurrent;
using Gagamba.Execution;
using Penghou.Hufu.Sandbox;
using Penghou.Zhinu;

namespace Penghou.Hufu.Zhinu;

/// <summary>
/// HZ-1A: one durable workflow activity that executes one approved sandbox
/// invocation through <see cref="SandboxExecutionHost"/>.
///
/// Ordering and rules:
/// <list type="number">
/// <item>the external operation is registered (Requested) <b>before</b> any
/// effect; the window between that durable record and the Running record after
/// launch is the uncertain crash window;</item>
/// <item>the negotiated capability (not the OS name) decides crash recovery —
/// sufficient <c>OwnerDeathCleanup</c> allows a fresh attempt, otherwise the
/// operation is abandoned and needs reconciliation;</item>
/// <item>cleanup uses a separate bounded token, so a cancelled workflow token
/// can never abort termination/completion;</item>
/// <item>the two authorization layers stay separate: this step never treats a
/// prior admit as the launch authority.</item>
/// </list>
/// No provider handle is ever persisted.
/// </summary>
public sealed class SandboxExecutionStep :
    IWorkflowStep<SandboxActivityInput, SandboxActivityOutcome>,
    IAsyncDisposable
{
    private const string ProviderName = "hufu-sandbox";

    private readonly SandboxExecutionProfile _profile;
    private readonly IExecutionProvider _provider;
    private readonly IWorkflowExternalOperationRepository _operations;
    private readonly SandboxExecutionHost _host;
    private readonly TimeSpan _cleanupBudget;
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.Ordinal);

    public SandboxExecutionStep(SandboxExecutionProfile profile, IAuthorityRequestAuthorizer authority,
        IExecutionProvider provider, IWorkflowExternalOperationRepository operations,
        TimeSpan? cleanupBudget = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _cleanupBudget = cleanupBudget ?? TimeSpan.FromMinutes(2);
        _host = new SandboxExecutionHost(provider, authority, profile);
    }

    public async Task<SandboxActivityOutcome> ExecuteAsync(WorkflowStepContext context,
        SandboxActivityInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);

        string idempotencyKey = $"sandbox:{context.StepExecutionId:D}:{context.Attempt}:{context.Revision}";
        string ownerId = $"hufu-sandbox:{context.StepExecutionId:D}:{context.Attempt}";
        string activityId = $"{context.StepKey}:{context.Revision}";
        string authorizationRequestId =
            $"{context.WorkflowRunId:N}:{context.StepExecutionId:N}:{context.Attempt}:{context.Revision}";

        var (negotiated, ownerDeathCleanupSufficient) =
            Negotiate(input.Requirements, _provider.Describe());
        var authority = input.AuthorityContext;
        var correlation = new SandboxExecutionCorrelation
        {
            WorkflowRunId = context.WorkflowRunId,
            StepExecutionId = context.StepExecutionId,
            StepKey = context.StepKey,
            Attempt = context.Attempt,
            Revision = context.Revision,
            AuthorizationRequestId = authorizationRequestId,
            TenantId = authority.TenantId,
            SubjectId = authority.SubjectId,
            AuthorityRunId = authority.RunId,
            AuthorityRevision = authority.RevisionId,
            AuthorityFence = authority.FenceId,
            ProfileId = _profile.ProfileId,
            ProfileRevision = _profile.ProfileRevision,
            RequestedGuarantees = SandboxCorrelationJson.Describe(input.Requirements.Required),
            NegotiatedGuarantees = negotiated,
            OwnerDeathCleanupSufficient = ownerDeathCleanupSufficient,
            Outcome = "Requested",
        };

        // 1. Durable Requested before any effect.
        var operation = await _operations.RegisterAsync(new ExternalOperationRegistration
        {
            WorkflowRunId = context.WorkflowRunId,
            StepId = context.StepExecutionId,
            StepKey = context.StepKey,
            StepRevision = context.Revision,
            Attempt = context.Attempt,
            IdempotencyKey = idempotencyKey,
            Provider = ProviderName,
            RecoveryIntent = ownerDeathCleanupSufficient
                ? ExternalOperationRecoveryIntent.Retry
                : ExternalOperationRecoveryIntent.Abandon,
            PayloadJson = SandboxCorrelationJson.Serialize(correlation),
        }, cancellationToken).ConfigureAwait(false);

        // Replay of an already-progressing operation: reconcile, never relaunch.
        if (operation.Status != ExternalOperationStatus.Requested)
            return Reconcile(operation);
        correlation = correlation with { ExternalOperationId = operation.OperationId };

        var activity = new SandboxActivityIdentity(activityId,
            grantId: context.WorkflowRunId.ToString("N"),
            grantRevision: context.Attempt.ToString(),
            policyRevision: context.Revision.ToString());
        var request = new SandboxExecutionRequest(authority, activity, input.InvocationId,
            input.Environment, input.Requirements, authorizationRequestId);

        var start = await _host.StartAsync(request, cancellationToken).ConfigureAwait(false);
        if (!start.IsStarted)
        {
            // Nothing launched: a classified pre-effect failure is terminal.
            var failed = correlation with { Outcome = start.Status.ToString(), TerminationReason = start.ReasonCode };
            await SafeFailAsync(operation.OperationId, ownerId, failed).ConfigureAwait(false);
            return new SandboxActivityOutcome(Classify(start.Status), null, start.ReasonCode);
        }

        // 2. Effects may have begun: move to Running (fenced by the captured
        //    generation). Acquire without the workflow token so cancellation
        //    cannot skip the durable Running transition; cancellation is
        //    observed in the wait below and handled with the cleanup token.
        await _operations.AcquireAsync(operation.OperationId, ownerId, operation.LeaseGeneration, CancellationToken.None)
            .ConfigureAwait(false);
        var running = correlation with
        {
            SandboxExecutionId = start.Handle!.ExecutionId.ToString("D"),
            Outcome = "Running",
        };

        // 3. Wait for completion; the cleanup token is separate so cancellation
        //    cannot stop cleanup.
        using var cleanup = new CancellationTokenSource(_cleanupBudget);
        var waitTask = _host.WaitForCompletionAsync(start.Handle!, cleanup.Token);
        var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelSignal.TrySetResult());

        var first = await Task.WhenAny(waitTask.AsTask(), cancelSignal.Task).ConfigureAwait(false);
        if (ReferenceEquals(first, cancelSignal.Task))
        {
            // Workflow cancellation: terminate, then await completion on the cleanup token.
            await _host.TerminateAsync(activityId, cleanup.Token).ConfigureAwait(false);
            await ObserveQuietlyAsync(waitTask).ConfigureAwait(false);
            var reason = _revoked.ContainsKey(activityId) ? "AuthorityRevoked" : "WorkflowCancelled";
            await SafeFailAsync(operation.OperationId, ownerId,
                running with { Outcome = "Cancelled", TerminationReason = reason }).ConfigureAwait(false);
            throw new OperationCanceledException("sandbox execution cancelled", cancellationToken);
        }

        var completion = await waitTask.ConfigureAwait(false);
        switch (completion.Status)
        {
            case SandboxCompletionStatus.NaturalExit when completion.RootExitCode == 0:
                await _operations.CompleteAsync(operation.OperationId, ownerId,
                    SandboxCorrelationJson.Serialize(running with { Outcome = "Completed" }), cancellationToken)
                    .ConfigureAwait(false);
                return new SandboxActivityOutcome(SandboxActivityStatus.Completed, 0);

            case SandboxCompletionStatus.NaturalExit:
                await SafeFailAsync(operation.OperationId, ownerId, running with
                {
                    Outcome = "ExecutionFailed",
                    TerminationReason = $"RootExitCode={completion.RootExitCode}",
                }).ConfigureAwait(false);
                return new SandboxActivityOutcome(
                    SandboxActivityStatus.ExecutionFailed, completion.RootExitCode, "RootExitCode");

            case SandboxCompletionStatus.Terminated:
                var terminalReason = _revoked.ContainsKey(activityId) ? "AuthorityRevoked" : "WorkflowCancelled";
                await SafeFailAsync(operation.OperationId, ownerId,
                    running with { Outcome = "Cancelled", TerminationReason = terminalReason }).ConfigureAwait(false);
                return new SandboxActivityOutcome(
                    terminalReason == "AuthorityRevoked" ? SandboxActivityStatus.Revoked : SandboxActivityStatus.WorkflowCancelled,
                    null, terminalReason);

            default:
                await SafeFailAsync(operation.OperationId, ownerId, running with
                {
                    Outcome = "Failed",
                    TerminationReason = "CompletionFailed",
                }).ConfigureAwait(false);
                return new SandboxActivityOutcome(SandboxActivityStatus.RecoveryRequired, null, "CompletionFailed");
        }
    }

    /// <summary>
    /// Effect of an authority revocation: mark the activity revoked, then
    /// terminate its execution domain. The running step observes Terminated and
    /// records <c>AuthorityRevoked</c>, distinct from workflow cancellation.
    /// </summary>
    public async ValueTask RevokeAndTerminateAsync(string activityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(activityId);
        _revoked[activityId] = 1;
        await _host.TerminateAsync(activityId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static (IReadOnlyList<string> Negotiated, bool OwnerDeathCleanupSufficient) Negotiate(
        ExecutionRequirements requested, PlatformCapabilities capabilities)
    {
        var negotiation = ExecutionNegotiator.Negotiate(capabilities, requested.Required, requested.Preferred);
        bool sufficient = capabilities.TryGet(ExecutionCapability.OwnerDeathCleanup, out var grant) &&
            grant.Satisfies(CapabilityLevel.Full, allowConstructed: false);
        return (negotiation.Met, sufficient);
    }

    private SandboxActivityOutcome Reconcile(WorkflowExternalOperation operation)
    {
        var correlation = SandboxCorrelationJson.Deserialize(operation.PayloadJson);
        return operation.Status switch
        {
            ExternalOperationStatus.Completed => new SandboxActivityOutcome(SandboxActivityStatus.Completed),
            ExternalOperationStatus.Failed or ExternalOperationStatus.Cancelled => new SandboxActivityOutcome(
                correlation?.TerminationReason == "AuthorityRevoked"
                    ? SandboxActivityStatus.Revoked
                    : SandboxActivityStatus.RecoveryRequired, null, correlation?.TerminationReason),
            _ => new SandboxActivityOutcome(SandboxActivityStatus.RecoveryRequired, null, "operation-in-flight"),
        };
    }

    private async Task SafeFailAsync(Guid operationId, string ownerId, SandboxExecutionCorrelation correlation)
    {
        try
        {
            await _operations.FailAsync(operationId, ownerId, SandboxCorrelationJson.Serialize(correlation),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* the durable record remains Requested/Running for reconciliation */ }
    }

    private static async Task ObserveQuietlyAsync(ValueTask<SandboxCompletionResult> wait)
    {
        try { await wait.ConfigureAwait(false); } catch { }
    }

    private static SandboxActivityStatus Classify(SandboxExecutionStatus status) => status switch
    {
        SandboxExecutionStatus.AuthorityDenied => SandboxActivityStatus.AuthorityDenied,
        SandboxExecutionStatus.RequirementNotAuthorized => SandboxActivityStatus.RequirementNotAuthorized,
        SandboxExecutionStatus.GuaranteeUnavailable => SandboxActivityStatus.GuaranteeUnavailable,
        SandboxExecutionStatus.PreparationFailed => SandboxActivityStatus.PreparationFailed,
        SandboxExecutionStatus.LaunchFailed => SandboxActivityStatus.LaunchFailed,
        _ => SandboxActivityStatus.RecoveryRequired,
    };
}
