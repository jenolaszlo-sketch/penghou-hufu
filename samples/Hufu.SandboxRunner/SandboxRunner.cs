using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gagamba.Execution;
using Gagamba.Runtime;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu;
using Penghou.Hufu.Fuwen;
using Penghou.Hufu.Sandbox;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Hufu.SandboxRunner;

/// <summary>
/// Auditable outcome of one governed run. Status is "Succeeded",
/// "AdmissionRejected", or "Failed"; Reason carries diagnostics or the
/// flattened failure chain. The remaining fields correlate the run across
/// the Fuwen plan, Zhinu execution, Hufu authorization, and Gagamba domain.
/// </summary>
public sealed record SandboxRunRecord(string Status, string Reason, string Invocation, int RootExitCode,
    string RunId, string AuthorityRequestId, string ProfileId, string ProfileRevision, string Platform);

/// <summary>
/// First real consumer of the frozen FZ-1 chain: one fixed, admitted Fuwen
/// plan (a single read-only activity with neutral execution intent) is
/// executed durably by Zhinu, resolved by <c>Penghou.Hufu.Fuwen</c> to a
/// host-registered trusted invocation, authorized per attempt, and run in a
/// real Gagamba execution domain. The plan names only a logical profile; the
/// executable, arguments, working directory, ceiling, and authority identity
/// stay host-controlled. No filesystem brokering, network, quotas, or new
/// infrastructure: the pinned command performs no I/O beyond its own stdout.
/// </summary>
public static class SandboxRunner
{
    public const string LogicalProfile = "diagnostic.whoami";
    public const string InvocationId = "whoami";
    public const string ProfileId = "sandbox-runner";
    public const string ProfileRevision = "r1";
    public const string WorkspaceId = "diagnostic";
    public const string ExecutableRelativePath = "tools/whoami";

    public static async Task<SandboxRunRecord> RunAsync(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));
        Directory.CreateDirectory(workspaceRoot);
        bool windows = OperatingSystem.IsWindows();
        string platform = windows ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";
        string executable = windows
            ? Path.Combine(Environment.SystemDirectory, "whoami.exe")
            : "/usr/bin/whoami";

        var admission = await AdmitPlan();
        if (!admission.Succeeded)
            return new SandboxRunRecord("AdmissionRejected",
                string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}")),
                InvocationId, -1, "", "", ProfileId, ProfileRevision, platform);

        var platformEnvironment = windows
            ? new Dictionary<string, string>
            {
                ["SYSTEMROOT"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["SYSTEMDRIVE"] = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            }
            : new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        var invocation = new SandboxApprovedInvocation(InvocationId, WorkspaceId, ExecutableRelativePath,
            executable, "", workspaceRoot, Array.Empty<string>(),
            [ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Partial)]);
        var profile = new SandboxExecutionProfile(ProfileId, ProfileRevision, platformEnvironment, new[] { invocation });
        var authorizer = new PinnedExecutionAuthorizer(WorkspaceId, ExecutableRelativePath,
            Sha256Hex($"{ProfileId}|{ProfileRevision}|{executable}"));

        await using var host = new SandboxExecutionHost(ExecutionRuntime.Create(), authorizer, profile);
        var executor = new SandboxActivityExecutor(host,
            new Dictionary<string, string> { [LogicalProfile] = InvocationId },
            NeutralGuaranteeMap.Default, new FixedRunnerAuthority());
        var ports = new FuwenZhinuExecutionPorts(executor, new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                ports)
            .CreateAsync("hufu.sandbox-runner", "1", admission);

        string databasePath = Path.Combine(workspaceRoot, "runner.db");
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = databasePath,
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store,
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(50) });

            using var input = JsonDocument.Parse("\"go\"");
            var runId = await engine.StartAsync("hufu.sandbox-runner", "1", input.RootElement.Clone());
            await engine.ExecuteAsync(runId);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId);

            if (output.ValueKind != JsonValueKind.Object ||
                !output.TryGetProperty("invocation", out var invocationElement) ||
                !output.TryGetProperty("rootExitCode", out var exitElement) ||
                exitElement.ValueKind != JsonValueKind.Number)
                return new SandboxRunRecord("Failed", "The activity returned an unexpected outcome shape.",
                    InvocationId, -1, runId.ToString("D"), authorizer.LastRequestIdentity ?? "",
                    ProfileId, ProfileRevision, platform);
            return new SandboxRunRecord("Succeeded", "",
                invocationElement.GetString() ?? "", exitElement.GetInt32(), runId.ToString("D"),
                authorizer.LastRequestIdentity ?? "", ProfileId, ProfileRevision, platform);
        }
        catch (Exception error)
        {
            return new SandboxRunRecord("Failed", Flatten(error), InvocationId, -1, "",
                authorizer.LastRequestIdentity ?? "", ProfileId, ProfileRevision, platform);
        }
        finally
        {
            try { SqliteConnection.ClearAllPools(); } catch { }
        }
    }

    private static async Task<WorkflowAdmissionResult> AdmitPlan()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.whoami", "1",
            new ContentDigest("sha256", "descriptor/v1", Sha256Hex("sample.whoami|1")));
        var activityPath = StructuralNodeIdentity.Create("runner", "probe");
        var returnPath = StructuralNodeIdentity.Create("runner", "return_result");
        var intent = new ActivityExecutionIntent(LogicalProfile,
            [new ExecutionGuarantee("execution.unit-termination", ExecutionGuaranteeLevel.Partial)],
            []);

        var plan = new WorkflowPlanBuilder("runner", "1", str, json, "routing/1")
            .AddNode(new ActivityNode("probe", activityPath, activityDesc, [], json) { ExecutionIntent = intent })
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("runner", [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();

        return await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(activityDesc,
                        callableContract: new CallableContract(
                            new CallableSignature([], json), CallableEffect.Read,
                            CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Flatten(Exception error)
    {
        var parts = new List<string>();
        for (var current = error; current is not null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" <- ", parts);
    }

    private sealed class FixedRunnerAuthority : IActivityAuthorityContextSource
    {
        public AuthenticatedAuthorityContext ContextFor(ActivityExecutionRequest request) =>
            new("diagnostic-tenant", "sandbox-runner", "run-1", "rev-1", "fence-1");
    }

    private sealed class UnusedContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
