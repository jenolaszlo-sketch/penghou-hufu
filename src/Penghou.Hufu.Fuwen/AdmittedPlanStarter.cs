using System.Text.Json;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Hufu.Fuwen;

/// <summary>
/// Durable provenance recorded on every run started through
/// <see cref="AdmittedPlanStarter"/>, binding the Zhinu run back to the exact
/// admitted plan, admission, catalogue, and policy revisions.
/// </summary>
public sealed record PlanStartProvenance(
    string ExecutionFingerprint,
    string PlanRevision,
    string AdmissionReceiptFingerprint,
    string CatalogueSnapshotRevision,
    string ResolvedDescriptorSetFingerprint,
    string CapabilityPolicyRevision,
    string CapabilityGrantFingerprint);

/// <summary>One request to start an already verified, admitted plan.</summary>
public sealed record PlanStartRequest(
    byte[] CanonicalPlanBytes,
    string ClaimedExecutionFingerprint,
    string ClaimedCatalogueSnapshotRevision,
    string ClaimedDescriptorSetFingerprint,
    string ClaimedPolicyRevision,
    string ClaimedGrantFingerprint,
    string WorkflowInputJson);

/// <summary>Outcome of <see cref="AdmittedPlanStarter.StartAsync"/>.</summary>
public abstract record PlanStartResult
{
    private PlanStartResult() { }

    /// <summary>The run was created; execution belongs to workers.</summary>
    public sealed record Started(Guid RunId, PlanStartProvenance Provenance) : PlanStartResult;

    /// <summary>The start was refused with a stable machine-readable reason.</summary>
    public sealed record Refused(string ReasonCode, string Message) : PlanStartResult;
}

/// <summary>
/// Context port the host does not provide: plans requiring context
/// snapshots cannot execute here and are refused before admission.
/// </summary>
public sealed class UnsupportedContextProvider : IContextProvider
{
    public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host provides no context snapshots.");
}

/// <summary>
/// Inference port the host does not provide: plans requiring model
/// inference cannot execute here and are refused before admission.
/// </summary>
public sealed class UnsupportedInferenceProvider : IInferenceExecutor
{
    public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host provides no model inference.");
}

/// <summary>
/// The smallest trustworthy plan start: a verified plan is re-admitted
/// against the host's own trusted catalogue and policy, the fresh receipt's
/// claims are compared to the presented claims, the definition is registered
/// through the frozen Fuwen-Zhinu boundary, and a Zhinu run is created with
/// provenance metadata. The host never executes: starting returns a run ID
/// for workers and operators; execution belongs elsewhere.
/// </summary>
/// <remarks>
/// Fail-closed refusals (stable <c>ReasonCode</c> values): integrity-failed,
/// admission-failed, catalogue-mismatch, descriptor-mismatch,
/// policy-mismatch, grant-mismatch, unsupported-node, missing-intent,
/// registration-failed, invalid-input, start-failed. Profile-name resolution
/// stays enforced at execution by the frozen composition, which refuses
/// unknown profiles before any provider work.
/// </remarks>
public sealed class AdmittedPlanStarter
{
    private readonly WorkflowCompiler _compiler;
    private readonly FuwenZhinuExecutionPorts _ports;
    private readonly IWorkflowDefinitionStore _definitionStore;
    private readonly SqliteWorkflowStore _workflowStore;

    /// <summary>
    /// Composes host-owned seams: a compiler over the trusted catalogue and
    /// policy, execution ports (activities resolve through the host; context
    /// and inference ports may be explicit rejections), and the definition
    /// and workflow stores. The plan contributes only intent and inputs.
    /// </summary>
    public AdmittedPlanStarter(
        WorkflowCompiler compiler,
        FuwenZhinuExecutionPorts ports,
        IWorkflowDefinitionStore definitionStore,
        SqliteWorkflowStore workflowStore)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
        _workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));
    }

    public async ValueTask<PlanStartResult> StartAsync(
        PlanStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CanonicalPlanBytes);

        WorkflowDefinitionDocument document;
        try
        {
            document = WorkflowDefinitionDocument.LoadVerified(
                request.ClaimedExecutionFingerprint, request.CanonicalPlanBytes);
        }
        catch (Exception error) when (error is WorkflowDefinitionIntegrityException or ArgumentException)
        {
            return new PlanStartResult.Refused("integrity-failed", error.Message);
        }

        var plan = document.ReadPlan();
        var slice = CheckExecutionSlice(plan);
        if (slice is not null)
            return slice;

        WorkflowAdmissionResult admission;
        try
        {
            admission = await new WorkflowAdmissionService(_compiler)
                .AdmitAsync(plan, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new PlanStartResult.Refused("admission-failed", error.Message);
        }

        if (!admission.Succeeded || admission.Receipt is null)
            return new PlanStartResult.Refused("admission-failed",
                string.Join("; ", admission.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
        var receipt = admission.Receipt;

        if (!string.Equals(receipt.CatalogueSnapshotRevision, request.ClaimedCatalogueSnapshotRevision, StringComparison.Ordinal))
            return new PlanStartResult.Refused("catalogue-mismatch",
                "The host catalogue revision does not match the presented admission.");
        if (!string.Equals(receipt.ResolvedDescriptorSetFingerprint, request.ClaimedDescriptorSetFingerprint, StringComparison.Ordinal))
            return new PlanStartResult.Refused("descriptor-mismatch",
                "The host resolved descriptors do not match the presented admission.");
        if (!string.Equals(receipt.CapabilityPolicyRevision, request.ClaimedPolicyRevision, StringComparison.Ordinal))
            return new PlanStartResult.Refused("policy-mismatch",
                "The host capability policy does not match the presented admission.");
        if (!string.Equals(receipt.CapabilityGrantFingerprint, request.ClaimedGrantFingerprint, StringComparison.Ordinal))
            return new PlanStartResult.Refused("grant-mismatch",
                "The host capability grants do not match the presented admission.");

        FuwenZhinuWorkflowRegistration registration;
        try
        {
            registration = await new FuwenZhinuWorkflowFactory(
                    _definitionStore,
                    new FuwenZhinuProviderRuntimeIdentity(
                        receipt.CatalogueSnapshotRevision,
                        receipt.ResolvedDescriptorSetFingerprint),
                    _ports)
                .CreateAsync(plan.Name, plan.Revision, admission, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is FuwenZhinuAdmissionException or ArgumentException or InvalidOperationException)
        {
            return new PlanStartResult.Refused("registration-failed", error.Message);
        }

        JsonDocument input;
        try
        {
            input = JsonDocument.Parse(request.WorkflowInputJson);
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            return new PlanStartResult.Refused("invalid-input", error.Message);
        }

        var provenance = new PlanStartProvenance(
            receipt.ExecutionFingerprint,
            plan.Revision,
            receipt.ReceiptFingerprint,
            receipt.CatalogueSnapshotRevision,
            receipt.ResolvedDescriptorSetFingerprint,
            receipt.CapabilityPolicyRevision,
            receipt.CapabilityGrantFingerprint);
        try
        {
            await using var engine = new WorkflowEngine(
                _workflowStore,
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions());
            var runId = await engine.StartAsync(
                plan.Name,
                plan.Revision,
                input.RootElement.Clone(),
                metadata: provenance,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new PlanStartResult.Started(runId, provenance);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or WorkflowNotFoundException)
        {
            return new PlanStartResult.Refused("start-failed", error.Message);
        }
        finally
        {
            input.Dispose();
        }
    }

    /// <summary>
    /// The FZ-1 execution slice this host supports: activity nodes carrying
    /// a neutral execution intent, plus returns. Returns null when the whole
    /// tree conforms, else the refusal. Profile-name resolution stays
    /// enforced at execution by the frozen composition.
    /// </summary>
    private static PlanStartResult.Refused? CheckExecutionSlice(WorkflowPlan plan)
    {
        var queue = new Queue<WorkflowNode>(plan.Nodes);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var child in Children(node))
                queue.Enqueue(child);
            switch (node)
            {
                case ActivityNode activity:
                    if (activity.ExecutionIntent is null ||
                        string.IsNullOrWhiteSpace(activity.ExecutionIntent.Profile))
                        return new PlanStartResult.Refused("missing-intent",
                            $"Activity '{activity.StructuralPath}' carries no neutral execution intent.");
                    break;
                case ReturnNode:
                    break;
                case ConditionalNode conditional:
                    return new PlanStartResult.Refused("unsupported-node",
                        $"Conditional '{conditional.StructuralPath}' is outside this host's execution slice.");
                case FanOutNode fanOut:
                    return new PlanStartResult.Refused("unsupported-node",
                        $"Fan-out '{fanOut.StructuralPath}' is outside this host's execution slice.");
                case RepeatNode repeat:
                    return new PlanStartResult.Refused("unsupported-node",
                        $"Repetition '{repeat.StructuralPath}' is outside this host's execution slice.");
                case ContextNode context:
                    return new PlanStartResult.Refused("unsupported-node",
                        $"Context '{context.StructuralPath}' is outside this host's execution slice.");
                case InferenceNode inference:
                    return new PlanStartResult.Refused("unsupported-node",
                        $"Inference '{inference.StructuralPath}' is outside this host's execution slice.");
                case CheckpointNode checkpoint:
                    return new PlanStartResult.Refused("unsupported-node",
                        $"Checkpoint '{checkpoint.StructuralPath}' is outside this host's execution slice.");
                case WaitNode wait:
                    return new PlanStartResult.Refused("unsupported-node",
                        $"Wait '{wait.StructuralPath}' is outside this host's execution slice.");
                default:
                    return new PlanStartResult.Refused("unsupported-node",
                        $"Node '{node.StructuralPath}' is outside this host's execution slice.");
            }
        }

        return null;
    }

    private static IEnumerable<WorkflowNode> Children(WorkflowNode node) => node switch
    {
        ConditionalNode conditional => conditional.Then.Concat(conditional.Else),
        FanOutNode fanOut => fanOut.Body,
        RepeatNode repeat => repeat.Body,
        _ => Array.Empty<WorkflowNode>()
    };
}
