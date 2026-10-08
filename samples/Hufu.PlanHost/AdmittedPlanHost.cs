using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu.Fuwen;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Hufu.PlanHost;

/// <summary>A summary of the host's trusted catalogue.</summary>
public sealed record PlanHostCatalogueInfo(
    string Profile, string Activity, string ActivityVersion, string CatalogueSnapshotRevision);

/// <summary>
/// Outcome of one admitted-plan start. Status is "Started", "Refused",
/// "AdmissionRejected", or "Failed". On success, RunId is the durable Zhinu
/// run the operator then inspects; the remaining fields are the provenance
/// binding the run to the exact admitted plan, catalogue, and policy.
/// </summary>
public sealed record PlanHostStartRecord(string Status, string Reason, string RunId,
    string ExecutionFingerprint, string PlanRevision, string AdmissionReceiptFingerprint,
    string CatalogueSnapshotRevision, string ResolvedDescriptorSetFingerprint,
    string CapabilityPolicyRevision, string CapabilityGrantFingerprint, string DatabasePath);

/// <summary>
/// The smallest trustworthy "supported host" for starting an admitted Fuwen
/// plan. It owns a trusted catalogue and capability policy, re-admits the
/// verified plan against them, and starts a durable Zhinu run through
/// <see cref="AdmittedPlanStarter"/>, returning a run id and provenance. It
/// never executes: execution belongs to a worker, and the started run is then
/// inspectable with the existing operator surfaces (show/wait/evidence/...).
///
/// The plan contributes only intent and descriptors; the executable
/// definitions live in this host's catalogue. A plan whose descriptors are not
/// in the catalogue is refused before any run is created.
/// </summary>
public static class AdmittedPlanHost
{
    public const string WorkflowName = "host";
    public const string WorkflowVersion = "1";
    public const string LogicalProfile = "diagnostic.echo";
    public const string ActivityName = "host.echo";
    public const string ActivityVersion = "1";
    public const string PolicyRevision = "policy/1";

    /// <summary>The host's trusted activity descriptor. The plan must reference exactly this.</summary>
    public static DescriptorReference BuildActivityDescriptor() =>
        new(DescriptorKind.Activity, ActivityName, ActivityVersion,
            new ContentDigest("sha256", "descriptor/v1", Sha256Hex($"{ActivityName}|{ActivityVersion}")));

    /// <summary>The host's trusted catalogue (the definitions that make a plan executable here).</summary>
    public static InMemoryTrustedCatalogue BuildCatalogue()
    {
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        return new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(BuildActivityDescriptor(),
                callableContract: new CallableContract(
                    new CallableSignature([], json), CallableEffect.Read,
                    CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        ]);
    }

    /// <summary>The compiler that admits plans against the host catalogue and policy.</summary>
    public static WorkflowCompiler BuildCompiler() =>
        new(BuildCatalogue(), capabilityPolicy: new CapabilityGrantPolicy(PolicyRevision, []));

    /// <summary>
    /// A demo plan referencing only this host's catalogue: one activity with a
    /// neutral execution intent, returning the activity output. In a real
    /// deployment the plan would be authored (or compiled from Fuwen source)
    /// and verified with <c>Fuwen.Inspect</c> before submission.
    /// </summary>
    public static WorkflowPlan BuildPlan()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var activityPath = StructuralNodeIdentity.Create("host", "echo");
        var returnPath = StructuralNodeIdentity.Create("host", "return_result");
        var intent = new ActivityExecutionIntent(LogicalProfile,
            [new ExecutionGuarantee("execution.unit-termination", ExecutionGuaranteeLevel.Partial)], []);

        return new WorkflowPlanBuilder(WorkflowName, WorkflowVersion, str, json, "routing/1")
            .AddNode(new ActivityNode("echo", activityPath, BuildActivityDescriptor(), [], json)
            {
                ExecutionIntent = intent
            })
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion(WorkflowName, [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();
    }

    /// <summary>Describes the host catalogue without starting anything.</summary>
    public static PlanHostCatalogueInfo Describe() =>
        new(LogicalProfile, ActivityName, ActivityVersion, BuildCatalogue().SnapshotRevision);

    /// <summary>
    /// Verifies, re-admits, and starts the demo plan, returning the durable run
    /// id and provenance. Start-only: the run is left Pending for a worker.
    /// </summary>
    public static Task<PlanHostStartRecord> StartAsync(string workspaceRoot, string inputJson = "\"go\"",
        CancellationToken cancellationToken = default) =>
        StartAsync(workspaceRoot, BuildPlan(), inputJson, cancellationToken);

    /// <summary>Starts an arbitrary plan that references this host's catalogue.</summary>
    public static async Task<PlanHostStartRecord> StartAsync(string workspaceRoot, WorkflowPlan plan,
        string inputJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        Directory.CreateDirectory(workspaceRoot);

        var document = WorkflowDefinitionDocument.Create(plan);
        var compiler = BuildCompiler();
        var admission = await new WorkflowAdmissionService(compiler)
            .AdmitAsync(plan, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!admission.Succeeded || admission.Receipt is null)
            return new PlanHostStartRecord("AdmissionRejected",
                string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}")),
                "", document.ExecutionFingerprint, plan.Revision, "", "", "", "", "", workspaceRoot);
        var receipt = admission.Receipt;

        string databasePath = Path.Combine(workspaceRoot, "plan-host.db");
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = databasePath,
                Pooling = false,
            });
            // Start does not execute, so the ports are never invoked; a worker
            // owns provider composition. Stub ports keep start portable.
            var ports = new FuwenZhinuExecutionPorts(
                new NotExecutingActivity(), new UnsupportedContext(), new UnsupportedInference());
            var starter = new AdmittedPlanStarter(compiler, ports, new InMemoryWorkflowDefinitionStore(), store);
            var request = new PlanStartRequest(
                document.CanonicalBytes.ToArray(),
                receipt.ExecutionFingerprint,
                receipt.CatalogueSnapshotRevision,
                receipt.ResolvedDescriptorSetFingerprint,
                receipt.CapabilityPolicyRevision,
                receipt.CapabilityGrantFingerprint,
                inputJson);

            var result = await starter.StartAsync(request, cancellationToken).ConfigureAwait(false);
            return result switch
            {
                PlanStartResult.Started started => FromProvenance("Started", "", databasePath, started.Provenance, started.RunId),
                PlanStartResult.Refused refused => new PlanHostStartRecord("Refused",
                    $"{refused.ReasonCode}: {refused.Message}", "",
                    receipt.ExecutionFingerprint, plan.Revision, receipt.ReceiptFingerprint,
                    receipt.CatalogueSnapshotRevision, receipt.ResolvedDescriptorSetFingerprint,
                    receipt.CapabilityPolicyRevision, receipt.CapabilityGrantFingerprint, databasePath),
                _ => Failed("Unexpected start result.", document, plan, receipt, databasePath),
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Failed(Flatten(error), document, plan, receipt, databasePath);
        }
        finally
        {
            try { SqliteConnection.ClearAllPools(); } catch { }
        }
    }

    private static PlanHostStartRecord FromProvenance(string status, string reason, string databasePath,
        PlanStartProvenance provenance, Guid runId) =>
        new(status, reason, runId.ToString("D"), provenance.ExecutionFingerprint, provenance.PlanRevision,
            provenance.AdmissionReceiptFingerprint, provenance.CatalogueSnapshotRevision,
            provenance.ResolvedDescriptorSetFingerprint, provenance.CapabilityPolicyRevision,
            provenance.CapabilityGrantFingerprint, databasePath);

    private static PlanHostStartRecord Failed(string reason, WorkflowDefinitionDocument document,
        WorkflowPlan plan, WorkflowAdmissionReceipt receipt, string databasePath) =>
        new("Failed", reason, "", document.ExecutionFingerprint, plan.Revision, receipt.ReceiptFingerprint,
            receipt.CatalogueSnapshotRevision, receipt.ResolvedDescriptorSetFingerprint,
            receipt.CapabilityPolicyRevision, receipt.CapabilityGrantFingerprint, databasePath);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Flatten(Exception error)
    {
        var parts = new List<string>();
        for (var current = error; current is not null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" <- ", parts);
    }

    private sealed class NotExecutingActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This host starts plans but does not execute work.");
    }

    private sealed class UnsupportedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This host provides no context snapshots.");
    }

    private sealed class UnsupportedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This host provides no model inference.");
    }
}
