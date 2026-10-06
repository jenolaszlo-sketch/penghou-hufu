using System.Text.Json;
using Gagamba.Execution;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu.Fuwen;
using Penghou.Hufu.Sandbox;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Fuwen.Tests;

/// <summary>
/// FZ-1 end-to-end: an agent-authored Fuwen plan carries neutral execution
/// intent; Zhinu durably executes the activity; the Hufu composition resolves
/// the trusted profile and authorizes; Gagamba runs the real execution domain.
/// </summary>
public sealed class SandboxActivityEndToEndTests
{
    [Fact]
    public async Task FuwenIntentExecutesThroughZhinuHufuAndTheSandbox()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.sandbox", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var activityPath = StructuralNodeIdentity.Create("demo", "build");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        var intent = new ActivityExecutionIntent("echo",
            [new ExecutionGuarantee("execution.unit-termination", ExecutionGuaranteeLevel.Full)],
            []);

        var plan = new WorkflowPlanBuilder("demo", "1", str, json, "routing/1")
            .AddNode(new ActivityNode("build", activityPath, activityDesc, [], json) { ExecutionIntent = intent })
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();

        var admission = await new WorkflowAdmissionService(new WorkflowCompiler(
                new InMemoryTrustedCatalogue([
                    new TrustedCatalogueDescriptor(activityDesc,
                        callableContract: new CallableContract(
                            new CallableSignature([], json), CallableEffect.Write,
                            CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
                ]),
                capabilityPolicy: new CapabilityGrantPolicy("policy/1", [])))
            .AdmitAsync(plan);
        Assert.True(admission.Succeeded,
            string.Join("; ", admission.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));

        var provider = new FakeProvider { Capabilities = WellKnownPlatforms.Windows };
        var authorizer = new FakeAuthorizer();
        var invocation = new SandboxApprovedInvocation("echo", "workspace-1", "bin/echo", "/bin/echo",
            "-n hi", "/tmp", Array.Empty<string>(),
            [ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full)]);
        var profile = new SandboxExecutionProfile("fz1-profile", "r1", new Dictionary<string, string>(), new[] { invocation });
        var host = new SandboxExecutionHost(provider, authorizer, profile);
        var executor = new SandboxActivityExecutor(host,
            new Dictionary<string, string> { ["echo"] = "echo" }, NeutralGuaranteeMap.Default, new FixedAuthority());
        var ports = new FuwenZhinuExecutionPorts(executor, new UnusedContext(), new UnusedInference());

        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    admission.Receipt!.CatalogueSnapshotRevision,
                    admission.Receipt.ResolvedDescriptorSetFingerprint),
                ports)
            .CreateAsync("fuwen.fz1", "1", admission);

        var root = Path.Combine(Path.GetTempPath(), "hz-fuwen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(root, "workflow.db"),
                Pooling = false,
            });
            await using var engine = new WorkflowEngine(store,
                registration.Register(new WorkflowRegistry()),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });

            using var input = JsonDocument.Parse("\"go\"");
            var runId = await engine.StartAsync("fuwen.fz1", "1", input.RootElement.Clone());
            await engine.ExecuteAsync(runId);
            var output = await engine.WaitForCompletionAsync<JsonElement>(runId);

            Assert.True(provider.LaunchCalls > 0, "the sandbox execution domain did not run");
            Assert.Equal(JsonValueKind.Object, output.ValueKind);
            Assert.Equal("echo", output.GetProperty("invocation").GetString());
        }
        finally
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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
