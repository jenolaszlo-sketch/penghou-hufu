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
/// Consumer-9 proof: a verified plan starts through the trusted host as a
/// durable Zhinu run with provenance, or refuses with a classified reason.
/// Every fail-closed rule is exercised; a worker engine then proves the
/// started run executes through the frozen composition.
/// </summary>
public sealed class AdmittedPlanStartTests : IAsyncLifetime
{
    private HostFixture? _host;

    public Task InitializeAsync()
    {
        _host = CreateHost();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
            try { Directory.Delete(_host.Root, recursive: true); } catch { }
        }

        await Task.CompletedTask;
    }

    private HostFixture Active => _host ?? throw new InvalidOperationException("Host is not initialized.");

    [Fact]
    public async Task StartsAdmittedPlanAndRecordsProvenance()
    {
        var host = Active;
        var plan = EchoPlan();
        var document = WorkflowDefinitionDocument.Create(plan);
        var receipt = (await host.AdmitAsync(plan)).Receipt!;
        var request = Request(document, receipt, "\"go\"");

        var result = await host.Starter.StartAsync(request);

        var started = Assert.IsType<PlanStartResult.Started>(result);
        Assert.NotEqual(Guid.Empty, started.RunId);
        var run = await host.WorkflowStore.GetRunAsync(started.RunId);
        Assert.NotNull(run);
        Assert.Equal(WorkflowStatus.Pending, run!.Status);
        Assert.Contains(receipt.ExecutionFingerprint, run.MetadataJson, StringComparison.Ordinal);
        Assert.Contains(receipt.CatalogueSnapshotRevision, run.MetadataJson, StringComparison.Ordinal);
        Assert.Contains(plan.Revision, run.MetadataJson, StringComparison.Ordinal);
        Assert.Equal(receipt.ReceiptFingerprint, started.Provenance.AdmissionReceiptFingerprint);
    }

    [Fact]
    public async Task SecondStartCreatesFreshRun()
    {
        var host = Active;
        var plan = EchoPlan();
        var document = WorkflowDefinitionDocument.Create(plan);
        var receipt = (await host.AdmitAsync(plan)).Receipt!;

        var first = Assert.IsType<PlanStartResult.Started>(
            await host.Starter.StartAsync(Request(document, receipt, "\"a\"")));
        var second = Assert.IsType<PlanStartResult.Started>(
            await host.Starter.StartAsync(Request(document, receipt, "\"b\"")));

        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(WorkflowStatus.Pending, (await host.WorkflowStore.GetRunAsync(first.RunId))!.Status);
        Assert.Equal(WorkflowStatus.Pending, (await host.WorkflowStore.GetRunAsync(second.RunId))!.Status);
    }

    [Fact]
    public async Task TamperedBytesAndWrongFingerprintRefuse()
    {
        var host = Active;
        var plan = EchoPlan();
        var document = WorkflowDefinitionDocument.Create(plan);
        var receipt = (await host.AdmitAsync(plan)).Receipt!;
        var canonical = document.CanonicalBytes.ToArray();

        var tampered = (byte[])canonical.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        var tamperedResult = await host.Starter.StartAsync(Request(tampered, receipt, "\"go\""));
        Assert.Equal("integrity-failed", Assert.IsType<PlanStartResult.Refused>(tamperedResult).ReasonCode);

        var wrong = Request(canonical, receipt, "\"go\"") with
        {
            ClaimedExecutionFingerprint = "sha256:fuwen-execution/v1:" + new string('0', 64)
        };
        Assert.Equal("integrity-failed",
            Assert.IsType<PlanStartResult.Refused>(await host.Starter.StartAsync(wrong)).ReasonCode);
    }

    [Fact]
    public async Task MismatchedClaimsRefuseWithClassification()
    {
        var host = Active;
        var plan = EchoPlan();
        var document = WorkflowDefinitionDocument.Create(plan);
        var receipt = (await host.AdmitAsync(plan)).Receipt!;
        var canonical = document.CanonicalBytes.ToArray();

        Assert.Equal("catalogue-mismatch",
            Assert.IsType<PlanStartResult.Refused>(await host.Starter.StartAsync(
                Request(canonical, receipt, "\"go\"") with { ClaimedCatalogueSnapshotRevision = "other" })).ReasonCode);
        Assert.Equal("descriptor-mismatch",
            Assert.IsType<PlanStartResult.Refused>(await host.Starter.StartAsync(
                Request(canonical, receipt, "\"go\"") with { ClaimedDescriptorSetFingerprint = "other" })).ReasonCode);
        Assert.Equal("policy-mismatch",
            Assert.IsType<PlanStartResult.Refused>(await host.Starter.StartAsync(
                Request(canonical, receipt, "\"go\"") with { ClaimedPolicyRevision = "other" })).ReasonCode);
        Assert.Equal("grant-mismatch",
            Assert.IsType<PlanStartResult.Refused>(await host.Starter.StartAsync(
                Request(canonical, receipt, "\"go\"") with { ClaimedGrantFingerprint = "other" })).ReasonCode);
    }

    [Fact]
    public async Task UnsupportedNodeAndMissingIntentRefuse()
    {
        var host = Active;
        var plan = EchoPlan();
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var waiting = plan with
        {
            Nodes = new WorkflowNode[]
            {
                new WaitNode("wait", "demo/wait", "approval", json),
                new ReturnNode("return_result", "demo/return_result",
                    new NodeOutputBinding("demo/wait", [])),
            },
            ExecutionOrder = new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase(["demo/wait"]),
                    new WorkflowExecutionPhase(["demo/return_result"]),
                ]),
            ])
        };
        var waitingDocument = WorkflowDefinitionDocument.Create(waiting);
        var waitingResult = await host.Starter.StartAsync(new PlanStartRequest(
            waitingDocument.CanonicalBytes.ToArray(), waitingDocument.ExecutionFingerprint,
            "catalogue", "descriptors", "policy", "grants", "\"go\""));
        var waitingRefusal = Assert.IsType<PlanStartResult.Refused>(waitingResult);
        Assert.Equal("unsupported-node", waitingRefusal.ReasonCode);
        Assert.Contains("demo/wait", waitingRefusal.Message, StringComparison.Ordinal);

        var plain = plan with
        {
            Nodes = plan.Nodes.Select(node => node is ActivityNode plainActivity && plainActivity.Name == "build"
                ? plainActivity with { ExecutionIntent = null }
                : node).ToList()
        };
        var plainDocument = WorkflowDefinitionDocument.Create(plain);
        var plainResult = await host.Starter.StartAsync(new PlanStartRequest(
            plainDocument.CanonicalBytes.ToArray(), plainDocument.ExecutionFingerprint,
            "catalogue", "descriptors", "policy", "grants", "\"go\""));
        Assert.Equal("missing-intent", Assert.IsType<PlanStartResult.Refused>(plainResult).ReasonCode);
    }

    [Fact]
    public async Task WorkerEngineCompletesStartedRun()
    {
        var host = Active;
        var plan = EchoPlan();
        var document = WorkflowDefinitionDocument.Create(plan);
        var receipt = (await host.AdmitAsync(plan)).Receipt!;
        var started = Assert.IsType<PlanStartResult.Started>(
            await host.Starter.StartAsync(Request(document, receipt, "\"go\"")));

        var ports = new FuwenZhinuExecutionPorts(host.Executor, new UnusedContext(), new UnusedInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    receipt.CatalogueSnapshotRevision,
                    receipt.ResolvedDescriptorSetFingerprint),
                ports)
            .CreateAsync("demo", "1", await host.AdmitAsync(plan));
        await using var engine = new WorkflowEngine(host.WorkflowStore,
            registration.Register(new WorkflowRegistry()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
        await engine.ExecuteAsync(started.RunId);
        var output = await engine.WaitForCompletionAsync<JsonElement>(started.RunId);

        Assert.Equal(JsonValueKind.Object, output.ValueKind);
        Assert.Equal("echo", output.GetProperty("invocation").GetString());
        Assert.Equal(WorkflowStatus.Completed, (await host.WorkflowStore.GetRunAsync(started.RunId))!.Status);
    }

    [Fact]
    public async Task NullRequestThrows()
    {
        var host = Active;
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await host.Starter.StartAsync(null!));
    }

    // ---- host fixture ----

    private sealed record HostFixture(
        AdmittedPlanStarter Starter,
        WorkflowCompiler Compiler,
        SandboxActivityExecutor Executor,
        SqliteWorkflowStore WorkflowStore,
        string Root)
    {
        public async ValueTask<WorkflowAdmissionResult> AdmitAsync(WorkflowPlan plan) =>
            await new WorkflowAdmissionService(Compiler).AdmitAsync(plan);
    }

    private static HostFixture CreateHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "hz-fuwen-start", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.sandbox", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var catalogue = new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(activityDesc,
                callableContract: new CallableContract(
                    new CallableSignature([], json), CallableEffect.Write,
                    CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
        ]);
        var compiler = new WorkflowCompiler(catalogue, capabilityPolicy: new CapabilityGrantPolicy("policy/1", []));
        var provider = new FakeProvider { Capabilities = WellKnownPlatforms.Windows };
        var invocation = new SandboxApprovedInvocation("echo", "workspace-1", "bin/echo", "/bin/echo",
            "-n hi", "/tmp", Array.Empty<string>(),
            [ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full)]);
        var profile = new SandboxExecutionProfile("fz1-profile", "r1", new Dictionary<string, string>(), new[] { invocation });
        var sandbox = new SandboxExecutionHost(provider, new FakeAuthorizer(), profile);
        var executor = new SandboxActivityExecutor(sandbox,
            new Dictionary<string, string> { ["echo"] = "echo" }, NeutralGuaranteeMap.Default, new FixedAuthority());
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(root, "workflow.db"),
            Pooling = false,
        });
        var starter = new AdmittedPlanStarter(
            compiler,
            new FuwenZhinuExecutionPorts(executor, new UnusedContext(), new UnusedInference()),
            new InMemoryWorkflowDefinitionStore(),
            store);
        return new HostFixture(starter, compiler, executor, store, root);
    }

    private static PlanStartRequest Request(
        WorkflowDefinitionDocument document, WorkflowAdmissionReceipt receipt, string inputJson) =>
        Request(document.CanonicalBytes.ToArray(), receipt, inputJson);

    private static PlanStartRequest Request(
        byte[] canonicalBytes, WorkflowAdmissionReceipt receipt, string inputJson) => new(
        canonicalBytes,
        receipt.ExecutionFingerprint,
        receipt.CatalogueSnapshotRevision,
        receipt.ResolvedDescriptorSetFingerprint,
        receipt.CapabilityPolicyRevision,
        receipt.CapabilityGrantFingerprint,
        inputJson);

    private static WorkflowPlan EchoPlan()
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
        return new WorkflowPlanBuilder("demo", "1", str, json, "routing/1")
            .AddNode(new ActivityNode("build", activityPath, activityDesc, [], json) { ExecutionIntent = intent })
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();
    }
}
