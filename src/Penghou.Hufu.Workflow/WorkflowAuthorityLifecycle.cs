using System.Security.Cryptography;
using System.Text;

namespace Penghou.Hufu.Workflow;

/// <summary>Workflow lifecycle event kinds. These are workflow vocabulary and stay in the adapter, not in Hufu core.</summary>
public enum WorkflowAuthorityEventKind { Suspend = 0, Resume = 1, Cancel = 2, Complete = 3, Supersede = 4 }

/// <summary>
/// One claimed workflow lifecycle event. All identity fields are caller-supplied
/// labels; the trust source authenticates the real facts. The event is an input
/// to authenticated translation, never an automatic permission change.
/// </summary>
public sealed record WorkflowAuthorityEvent
{
    public WorkflowAuthorityEvent(
        string source, string tenantId, string subjectId, string runId, string activityId,
        string delegationId, string generation, string revisionId, string fenceId,
        string eventIdentity, long eventOrder, long expectedAuthoritySequence,
        WorkflowAuthorityEventKind kind, string? reason = null)
    {
        WorkflowBounds.Token(source, nameof(source));
        WorkflowBounds.Token(tenantId, nameof(tenantId));
        WorkflowBounds.Token(subjectId, nameof(subjectId));
        WorkflowBounds.Token(runId, nameof(runId));
        WorkflowBounds.Token(activityId, nameof(activityId));
        WorkflowBounds.Token(delegationId, nameof(delegationId));
        WorkflowBounds.Token(generation, nameof(generation));
        WorkflowBounds.Token(revisionId, nameof(revisionId));
        WorkflowBounds.Token(fenceId, nameof(fenceId));
        WorkflowBounds.Token(eventIdentity, nameof(eventIdentity));
        if (eventOrder < 0) throw new ArgumentOutOfRangeException(nameof(eventOrder));
        if (expectedAuthoritySequence < 0) throw new ArgumentOutOfRangeException(nameof(expectedAuthoritySequence));
        if (!Enum.IsDefined(kind)) throw new ArgumentException("A known workflow event kind is required.", nameof(kind));
        if (reason is not null) WorkflowBounds.Token(reason, nameof(reason));
        Source = source; TenantId = tenantId; SubjectId = subjectId; RunId = runId; ActivityId = activityId;
        DelegationId = delegationId; Generation = generation; RevisionId = revisionId; FenceId = fenceId;
        EventIdentity = eventIdentity; EventOrder = eventOrder; ExpectedAuthoritySequence = expectedAuthoritySequence;
        Kind = kind; Reason = reason;
    }

    public string Source { get; }
    public string TenantId { get; }
    public string SubjectId { get; }
    public string RunId { get; }
    public string ActivityId { get; }
    public string DelegationId { get; }
    public string Generation { get; }
    public string RevisionId { get; }
    public string FenceId { get; }
    public string EventIdentity { get; }
    public long EventOrder { get; }
    public long ExpectedAuthoritySequence { get; }
    public WorkflowAuthorityEventKind Kind { get; }
    public string? Reason { get; }
}

/// <summary>Independently authenticated facts for one workflow event. Caller labels alone authenticate nothing.</summary>
public sealed record WorkflowAuthorityEventPrincipal(
    string Source, string TenantId, string SubjectId, string RunId, string ActivityId,
    string DelegationId, string Generation, string RevisionId, string FenceId, string EventIdentity);

/// <summary>Authenticate the event through the trusted runtime channel and resolve its exact facts.</summary>
public interface IWorkflowAuthorityEventTrustSource
{
    ValueTask<WorkflowAuthorityEventPrincipal?> AuthenticateAsync(
        WorkflowAuthorityEvent @event,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-side translator from authenticated workflow events to neutral Hufu
/// execution-lifecycle transitions. It maps Suspend/Resume to the same-binding
/// gate and Cancel/Complete/Supersede to a terminal transition, applies the
/// transition durably, and returns the durable receipt. It never grants
/// authority and never creates a new generation.
/// </summary>
public sealed class WorkflowAuthorityLifecycleTranslator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IWorkflowAuthorityEventTrustSource _trust;
    private readonly IExecutionLifecycleStore _store;

    public WorkflowAuthorityLifecycleTranslator(IWorkflowAuthorityEventTrustSource trust, IExecutionLifecycleStore store)
    {
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<ExecutionLifecycleApplyResult> ApplyAsync(
        WorkflowAuthorityEvent @event,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (@event is null) return new(ExecutionLifecycleApplyStatus.InvalidRequest);
        try
        {
            var principal = await _trust.AuthenticateAsync(@event, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (principal is null) return new(ExecutionLifecycleApplyStatus.Unavailable);
            if (!Matches(principal, @event)) return new(ExecutionLifecycleApplyStatus.Conflict);
            var (kind, reason) = Map(@event.Kind);
            var transition = new ExecutionLifecycleTransition(
                new ExecutionLifecycleBinding(principal.TenantId, principal.SubjectId, principal.RunId),
                @event.ExpectedAuthoritySequence,
                TransitionId(principal.Source, principal.EventIdentity),
                kind, reason, principal.Generation, principal.FenceId, @event.Reason);
            return await _store.ApplyAsync(transition, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ArgumentException) { return new(ExecutionLifecycleApplyStatus.InvalidRequest); }
        catch { return new(ExecutionLifecycleApplyStatus.Unavailable); }
    }

    private static bool Matches(WorkflowAuthorityEventPrincipal principal, WorkflowAuthorityEvent @event) =>
        principal.Source == @event.Source && principal.TenantId == @event.TenantId && principal.SubjectId == @event.SubjectId &&
        principal.RunId == @event.RunId && principal.ActivityId == @event.ActivityId && principal.DelegationId == @event.DelegationId &&
        principal.Generation == @event.Generation && principal.RevisionId == @event.RevisionId && principal.FenceId == @event.FenceId &&
        principal.EventIdentity == @event.EventIdentity;

    private static (ExecutionLifecycleTransitionKind Kind, ExecutionLifecycleTerminalReason Reason) Map(
        WorkflowAuthorityEventKind kind) => kind switch
        {
            WorkflowAuthorityEventKind.Suspend => (ExecutionLifecycleTransitionKind.Suspend, ExecutionLifecycleTerminalReason.None),
            WorkflowAuthorityEventKind.Resume => (ExecutionLifecycleTransitionKind.Resume, ExecutionLifecycleTerminalReason.None),
            WorkflowAuthorityEventKind.Cancel => (ExecutionLifecycleTransitionKind.Terminate, ExecutionLifecycleTerminalReason.Cancelled),
            WorkflowAuthorityEventKind.Complete => (ExecutionLifecycleTransitionKind.Terminate, ExecutionLifecycleTerminalReason.Completed),
            WorkflowAuthorityEventKind.Supersede => (ExecutionLifecycleTransitionKind.Terminate, ExecutionLifecycleTerminalReason.Superseded),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static string TransitionId(string source, string eventIdentity)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, true);
        Write(writer, "Penghou.Hufu.Workflow.EventTransition.v1");
        Write(writer, source);
        Write(writer, eventIdentity);
        writer.Flush();
        return "wfe-" + Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();

        static void Write(BinaryWriter writer, string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }
}
