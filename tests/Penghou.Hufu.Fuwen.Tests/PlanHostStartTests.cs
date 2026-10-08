using System.Text.Json;
using Hufu.PlanHost;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu.Fuwen;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Fuwen.Tests;

/// <summary>
/// Consumer-10 proof: a supported host admits a Fuwen plan against its own
/// trusted catalogue and starts a durable Zhinu run, returning the run id and
/// provenance. Start is portable (no execution); a worker then completes the
/// handed-off run. Fail-closed refusal for an unknown descriptor is covered.
/// </summary>
public sealed class PlanHostStartTests : IAsyncLifetime
{
    private string _root = "";

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "plan-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void CatalogueDescribesTrustedDefinitions()
    {
        var info = AdmittedPlanHost.Describe();
        Assert.Equal(AdmittedPlanHost.LogicalProfile, info.Profile);
        Assert.Equal(AdmittedPlanHost.ActivityName, info.Activity);
        Assert.False(string.IsNullOrWhiteSpace(info.CatalogueSnapshotRevision));
    }

    [Fact]
    public async Task StartReturnsRunIdAndProvenance()
    {
        var record = await AdmittedPlanHost.StartAsync(_root);

        Assert.True(record.Status == "Started", record.Status + ": " + record.Reason);
        Assert.NotEqual(Guid.Empty, Guid.Parse(record.RunId));
        Assert.False(string.IsNullOrWhiteSpace(record.ExecutionFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(record.AdmissionReceiptFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(record.CatalogueSnapshotRevision));
        Assert.False(string.IsNullOrWhiteSpace(record.ResolvedDescriptorSetFingerprint));
        Assert.Equal(AdmittedPlanHost.PolicyRevision, record.CapabilityPolicyRevision);
        Assert.False(string.IsNullOrWhiteSpace(record.CapabilityGrantFingerprint));
        Assert.True(File.Exists(record.DatabasePath));

        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = record.DatabasePath, Pooling = false });
        await using var engine = new WorkflowEngine(store, new WorkflowRegistry(), new ZhinuOptions());
        var run = await engine.GetRunAsync(Guid.Parse(record.RunId));
        Assert.NotNull(run);
        Assert.Equal(WorkflowStatus.Pending, run!.Status);
        Assert.Equal(AdmittedPlanHost.WorkflowName, run.WorkflowName);
        Assert.Contains(record.AdmissionReceiptFingerprint, run.MetadataJson, StringComparison.Ordinal);
        Assert.Contains(record.ExecutionFingerprint, run.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartedRunExecutesWithAWorker()
    {
        var record = await AdmittedPlanHost.StartAsync(_root);
        Assert.True(record.Status == "Started", record.Status + ": " + record.Reason);

        var plan = AdmittedPlanHost.BuildPlan();
        var admission = await new WorkflowAdmissionService(AdmittedPlanHost.BuildCompiler()).AdmitAsync(plan);
        Assert.True(admission.Succeeded);
        var ports = new FuwenZhinuExecutionPorts(new FakeActivity(), new NoContext(), new NoInference());
        var registration = await new FuwenZhinuWorkflowFactory(
                new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(
                    record.CatalogueSnapshotRevision, record.ResolvedDescriptorSetFingerprint),
                ports)
            .CreateAsync(plan.Name, plan.Revision, admission);

        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = record.DatabasePath, Pooling = false });
        await using var engine = new WorkflowEngine(store,
            registration.Register(new WorkflowRegistry()),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) });
        var runId = Guid.Parse(record.RunId);
        await engine.ExecuteAsync(runId);
        var output = await engine.WaitForCompletionAsync<JsonElement>(runId);

        Assert.Equal("plan-host", output.GetProperty("host").GetString());
        Assert.Equal(WorkflowStatus.Completed, (await engine.GetRunAsync(runId))!.Status);
        var events = await engine.GetEventsAsync(runId, 0, 100);
        Assert.Contains(events, e => e.EventType == "workflow-completed");
    }

    [Fact]
    public async Task PlanWithUnknownDescriptorIsRefused()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var json = new PrimitiveType(FuwenPrimitiveKind.Json);
        var unknown = new DescriptorReference(DescriptorKind.Activity, "host.unknown", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('b', 64)));
        var activityPath = StructuralNodeIdentity.Create("host", "echo");
        var returnPath = StructuralNodeIdentity.Create("host", "return_result");
        var plan = new WorkflowPlanBuilder("host", "1", str, json, "routing/1")
            .AddNode(new ActivityNode("echo", activityPath, unknown, [], json)
            {
                ExecutionIntent = new ActivityExecutionIntent(AdmittedPlanHost.LogicalProfile,
                    [new ExecutionGuarantee("execution.unit-termination", ExecutionGuaranteeLevel.Partial)], [])
            })
            .AddNode(new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])))
            .SetExecutionOrder(new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("host", [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]))
            .Build();

        var record = await AdmittedPlanHost.StartAsync(_root, plan, "\"go\"");

        Assert.True(record.Status is "AdmissionRejected" or "Refused" or "Failed",
            record.Status + ": " + record.Reason);
        Assert.NotEqual("Started", record.Status);
        // No run was created.
        if (File.Exists(record.DatabasePath))
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = record.DatabasePath, Pooling = false });
            await using var engine = new WorkflowEngine(store, new WorkflowRegistry(), new ZhinuOptions());
            Assert.Empty(await engine.GetRunsAsync(new RunQuery { Limit = 10 }));
        }
    }

    private sealed class FakeActivity : IActivityExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse("{\"host\":\"plan-host\"}");
            return ValueTask.FromResult(
                ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement.Clone())));
        }
    }

    private sealed class NoContext : IContextProvider
    {
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoInference : IInferenceExecutor
    {
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
