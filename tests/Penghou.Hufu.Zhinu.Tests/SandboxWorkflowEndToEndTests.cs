using Gagamba.Execution;
using Penghou.Hufu.Sandbox;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Zhinu.Tests;

/// <summary>
/// Engine-level HZ-1 proof: a real WorkflowEngine drives the durable sandbox
/// activity, concentrating on cancellation, revocation, and the crash windows
/// rather than the happy path alone.
/// </summary>
public sealed class SandboxWorkflowEndToEndTests
{
    private const string Workflow = "hz1-e2e";
    private static CancellationToken Ct => CancellationToken.None;

    private sealed class SandboxWorkflow : IWorkflow<SandboxActivityInput, SandboxActivityOutcome>
    {
        private readonly SandboxExecutionStep _step;
        public SandboxWorkflow(SandboxExecutionStep step) => _step = step;
        public Task<SandboxActivityOutcome> RunAsync(WorkflowContext context, SandboxActivityInput input,
            CancellationToken cancellationToken) =>
            context.StepAsync("sandbox", input,
                (value, stepContext, token) => _step.ExecuteAsync(stepContext, value, token),
                cancellationToken: cancellationToken);
    }

    [Fact] // A
    public async Task A_NormalEngineRunReachesCompleted()
    {
        var f = Fixture(new FakeProvider { ExitCode = 0 });
        try
        {
            var runId = await f.Engine.StartAsync<SandboxActivityInput>(Workflow, "1", Input(), cancellationToken: Ct);
            await f.Engine.ExecuteAsync(runId, Ct);

            Assert.Equal(WorkflowStatus.Completed, (await f.Engine.GetRunAsync(runId, Ct))!.Status);
            var step = Assert.Single(await f.Engine.GetStepsAsync(runId, Ct));
            Assert.Equal(StepStatus.Completed, step.Status);
            Assert.Equal(ExternalOperationStatus.Completed,
                Assert.Single(await f.Store.ListAsync(runId, cancellationToken: Ct)).Status);
        }
        finally { Cleanup(f); }
    }

    [Fact] // B
    public async Task B_WorkflowCancellationTerminatesAndRecordsCancelled()
    {
        var provider = new FakeProvider { BlockUntilTerminated = true };
        var f = Fixture(provider);
        try
        {
            var runId = await f.Engine.StartAsync<SandboxActivityInput>(Workflow, "1", Input(), cancellationToken: Ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var execution = f.Engine.ExecuteAsync(runId, cts.Token);
            await PollAsync(() => provider.LaunchCalls > 0);
            await f.Engine.CancelAsync(runId, Ct);
            try { await execution; } catch { }

            var operation = Assert.Single(await f.Store.ListAsync(runId, cancellationToken: Ct));
            Assert.Equal(ExternalOperationStatus.Cancelled, operation.Status);
            Assert.Contains("WorkflowCancelled", operation.Error, StringComparison.Ordinal);
            Assert.Equal(1, provider.TerminateCalls);
        }
        finally { Cleanup(f); }
    }

    [Fact] // C
    public async Task C_AuthorityRevocationTerminatesIndependently()
    {
        var provider = new FakeProvider { BlockUntilTerminated = true };
        var f = Fixture(provider);
        try
        {
            var runId = await f.Engine.StartAsync<SandboxActivityInput>(Workflow, "1", Input(), cancellationToken: Ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var execution = f.Engine.ExecuteAsync(runId, cts.Token);
            await PollAsync(() => provider.LaunchCalls > 0);
            await f.Step.RevokeAndTerminateAsync("sandbox:1", Ct);
            await execution;
            var output = await f.Engine.WaitForCompletionAsync<SandboxActivityOutcome>(runId, cancellationToken: Ct);

            Assert.Equal(SandboxActivityStatus.Revoked, output.Status);
            var operation = Assert.Single(await f.Store.ListAsync(runId, cancellationToken: Ct));
            Assert.Equal(ExternalOperationStatus.Cancelled, operation.Status);
            Assert.Contains("AuthorityRevoked", operation.Error, StringComparison.Ordinal);
        }
        finally { Cleanup(f); }
    }

    [Theory] // E: crash while Running, decided by the recorded granted capability
    [InlineData(true, SandboxRecoveryAction.RetryFreshAttempt)]
    [InlineData(false, SandboxRecoveryAction.Abandon)]
    public async Task E_CrashWhileRunningFollowsRecordedOwnerDeathCleanup(
        bool ownerDeathCleanup, SandboxRecoveryAction expected)
    {
        var provider = new FakeProvider
        {
            BlockUntilTerminated = true,
            Capabilities = ownerDeathCleanup ? WellKnownPlatforms.Windows : WellKnownPlatforms.Linux,
        };
        var f = Fixture(provider);
        try
        {
            var runId = await f.Engine.StartAsync<SandboxActivityInput>(Workflow, "1", Input(), cancellationToken: Ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var execution = f.Engine.ExecuteAsync(runId, cts.Token);
            await PollAsync(() => provider.LaunchCalls > 0); // durable Running reached

            // Simulate host loss: reopen a fresh engine over the same store.
            var reopened = f.Reopen();
            Assert.NotNull(reopened);

            var operation = Assert.Single(await f.Store.ListAsync(runId, cancellationToken: Ct));
            Assert.Equal(ExternalOperationStatus.Running, operation.Status);
            var decision = Assert.Single(SandboxRecovery.Plan(new[] { operation }));
            Assert.Equal(expected, decision.Action);

            // No provider handle, preparation token, or reusable authorization
            // capability is recovered from durable state.
            Assert.DoesNotContain("ExecutionHandle", operation.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Gagamba", operation.PayloadJson, StringComparison.OrdinalIgnoreCase);

            await f.Step.RevokeAndTerminateAsync("sandbox:1", Ct);
            try { await execution; } catch { }
        }
        finally { Cleanup(f); }
    }

    // ---- fixture ----

    private sealed record Harness(
        WorkflowEngine Engine, WorkflowRegistry Registry, SqliteWorkflowStore Store,
        SandboxExecutionStep Step, string DatabasePath)
    {
        public WorkflowEngine Reopen() => new(Store, Registry,
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10), LeaseDuration = TimeSpan.FromSeconds(30) });
    }

    private static Harness Fixture(FakeProvider provider)
    {
        string db = Path.Combine(Path.GetTempPath(), "hz1-e2e-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
        {
            DatabasePath = db,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false,
        });
        var step = new SandboxExecutionStep(Profile(), new FakeAuthorizer(), provider, store);
        var registry = new WorkflowRegistry().Register(Workflow, "1", new SandboxWorkflow(step));
        var engine = new WorkflowEngine(store, registry,
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10), LeaseDuration = TimeSpan.FromSeconds(30) });
        return new Harness(engine, registry, store, step, db);
    }

    private static void Cleanup(Harness fixture)
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { File.Delete(fixture.DatabasePath); } catch { }
    }

    private static SandboxExecutionProfile Profile()
    {
        var invocation = new SandboxApprovedInvocation("echo", "workspace-1", "bin/echo", "/bin/echo",
            "-n hi", "/tmp", Array.Empty<string>(), new[]
            {
                ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full),
                ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Full),
                ExecutionRequirement.Require(ExecutionCapability.OwnerDeathCleanup, CapabilityLevel.Full),
            });
        return new SandboxExecutionProfile("hz1-profile", "r1", new Dictionary<string, string>(), new[] { invocation });
    }

    private static SandboxActivityInput Input() => new()
    {
        AuthorityContext = new AuthenticatedAuthorityContext("tenant-1", "subject-1", "run-1", "arev-1", "fence-1"),
        InvocationId = "echo",
        Requirements = new ExecutionRequirements(new[]
        {
            ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full),
            ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Full),
        }),
    };

    private static async Task PollAsync(Func<bool> condition, int milliseconds = 15000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException("condition not met");
            await Task.Delay(20, Ct);
        }
    }
}
