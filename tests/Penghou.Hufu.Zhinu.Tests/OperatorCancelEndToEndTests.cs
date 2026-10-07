using Gagamba.Execution;
using Gagamba.Runtime;
using Penghou.Hufu.Sandbox;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Zhinu.Tests;

/// <summary>
/// Operator-cancel integration over the real chain: a governed sleep runs in
/// a genuine execution domain; cancellation with actor/reason (the exact call
/// the operator CLI makes) reaches native termination, records
/// Cancelled/WorkflowCancelled durably, retains the audit, and stays
/// idempotent on repeat. Termination is counted at the provider boundary; the
/// kernel kill itself is GQ-1's proven guarantee, not re-proven here. On a
/// host that cannot host a domain the provider refuses with a classified
/// reason and the test yields.
/// </summary>
public sealed class OperatorCancelEndToEndTests
{
    private const string Workflow = "hz1-operator-cancel";
    private static CancellationToken Ct => CancellationToken.None;

    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public OperatorCancelEndToEndTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    private System.Diagnostics.Stopwatch? _clock;
    private void Mark(string message) =>
        _output.WriteLine($"[{_clock?.Elapsed.TotalSeconds:F1}s] {message}");

    private sealed class SleepWorkflow : IWorkflow<SandboxActivityInput, SandboxActivityOutcome>
    {
        private readonly SandboxExecutionStep _step;
        public SleepWorkflow(SandboxExecutionStep step) => _step = step;
        public Task<SandboxActivityOutcome> RunAsync(WorkflowContext context, SandboxActivityInput input,
            CancellationToken cancellationToken) =>
            context.StepAsync("sleep", input,
                (value, stepContext, token) => _step.ExecuteAsync(stepContext, value, token),
                cancellationToken: cancellationToken);
    }

    [SkippableFact]
    public async Task OperatorCancelTerminatesRealDomainAndRecordsEvidence()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return;
        await RunScenarioAsync();
    }

    private async Task RunScenarioAsync()
    {
        await using var probe = ExecutionRuntime.Create();
        if (!probe.HasProvider)
            Skip.If(true, "Host cannot host the diagnostic domain: " + probe.RefusalReason);
        var probeRequirements = new ExecutionRequirements(new[]
        {
            ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Partial),
        });
        var probePrepared = probe.Prepare(probeRequirements);
        if (probePrepared is PrepareResult.Rejected rejected)
            Skip.If(true, "Host cannot host the diagnostic domain: " + string.Join("; ", rejected.Reasons));
        probe.Discard(((PrepareResult.Accepted)probePrepared).Prepared);

        bool windows = OperatingSystem.IsWindows();
        string ws = Path.Combine(Path.GetTempPath(), "hz1-opcancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);

        SandboxExecutionStep? step = null;
        try
        {
            var store = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(ws, "workflow.db"),
                BusyTimeout = TimeSpan.FromSeconds(5),
                Pooling = false,
            });
            var platformEnvironment = windows
                ? new Dictionary<string, string>
                {
                    ["SYSTEMROOT"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    ["SYSTEMDRIVE"] = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
                    ["PSModulePath"] = Environment.GetEnvironmentVariable("PSModulePath") ?? "",
                }
                : new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin" };
            // ~120 seconds of harmless waiting. Test-thread observations
            // (store reads, engine calls) can stall for tens of seconds
            // behind the busy engine's SQLite polling while execution itself
            // proceeds correctly, so the sleep must outlast any observer
            // stall with wide margin; the fixed blocking wait below does not
            // depend on task scheduling at all. The Windows provider denies
            // loopback (no ping) and headless console waits fail closed, so
            // the sleeper is PowerShell Start-Sleep with the platform-owned
            // PSModulePath the GW-2 runner finding requires. Unix uses sleep.
            var invocation = new SandboxApprovedInvocation("sleep", "workspace-1", "bin/sleep",
                windows
                    ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                    : "/bin/sh",
                windows ? "-NoProfile -Command \"Start-Sleep -Seconds 120\"" : "-c \"sleep 120\"",
                ws, Array.Empty<string>(),
                new[] { ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Partial) });
            var profile = new SandboxExecutionProfile("hz1-opcancel", "r1", platformEnvironment, new[] { invocation });
            var spy = new SpyProvider(ExecutionRuntime.Create());
            step = new SandboxExecutionStep(profile, new FakeAuthorizer(), spy, store);
            var registry = new WorkflowRegistry().Register(Workflow, "1", new SleepWorkflow(step));
            // A minutes-long lease: short leases expire while a stalled host
            // cannot renew, fencing the run out from under the activity.
            // (The HZ-1 fixture's 30s lease suits millisecond fakes only.)
            var engine = new WorkflowEngine(store, registry,
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(100), LeaseDuration = TimeSpan.FromMinutes(10) });

            var runId = await engine.StartAsync<SandboxActivityInput>(Workflow, "1", Input(), cancellationToken: Ct);
            _clock = System.Diagnostics.Stopwatch.StartNew();
            Mark("started");
            var execution = engine.ExecuteAsync(runId, Ct);

            // Let launch settle without any store reads: a blocking sleep
            // elapses in real time regardless of task-scheduling stalls that
            // can delay test-thread observations for tens of seconds while
            // execution itself proceeds correctly.
            Thread.Sleep(TimeSpan.FromSeconds(10));
            Mark("settled; launch calls observed: " + spy.LaunchCalls);
            Assert.Equal(1, spy.LaunchCalls);

            // The exact call the operator CLI makes for `runs cancel`.
            Mark("cancelling");
            await engine.CancelAsync(runId, "operator", "stuck review", Ct);
            Mark("cancel returned");
            // Bounded: never hang the runner even if the engine stalls.
            // Engine-side cancellation surfacing is asserted via durable
            // state below, so only a wait timeout fails here.
            try { await execution.WaitAsync(TimeSpan.FromMinutes(5), Ct); }
            catch (TimeoutException) { Assert.Fail("execution did not settle within 5 minutes"); }
            catch { }
            Mark("execution settled");

            // The workflow is durably Cancelled; on timeout dump the full
            // observed state instead of failing opaquely.
            var runSeenCancelled = await PollStateAsync(async () =>
                (await engine.GetRunAsync(runId, Ct))?.Status == WorkflowStatus.Cancelled);
            if (!runSeenCancelled)
            {
                var runDump = (await engine.GetRunAsync(runId, Ct))?.Status.ToString() ?? "?";
                var opsDump = string.Join(" | ", (await store.ListAsync(runId, cancellationToken: Ct))
                    .Select(candidate => $"{candidate.Status}/{candidate.Attempt}/{candidate.Error}/" +
                        $"created={candidate.CreatedAt:O}/completed={candidate.CompletedAt:O}"));
                var auditHits = (await engine.GetEventsAsync(runId, 0, 200, Ct))
                    .Where(@event => (@event.DataJson ?? "").Contains("operator", StringComparison.Ordinal))
                    .Select(@event => @event.EventType + ":" + @event.DataJson);
                Assert.Fail($"Run did not cancel (run={runDump}, launch={spy.LaunchCalls}, " +
                    $"terminate={spy.TerminateCalls}): {opsDump} audit=[{string.Join(";", auditHits)}]");
            }
            // The external operation is Cancelled with the workflow reason.
            WorkflowExternalOperation? operation = null;
            await PollAsync(async () =>
            {
                operation = (await store.ListAsync(runId, cancellationToken: Ct))
                    .SingleOrDefault(candidate => candidate.Status == ExternalOperationStatus.Cancelled);
                return operation is not null;
            });
            Assert.Contains("WorkflowCancelled", operation!.Error, StringComparison.Ordinal);
            // Cancellation reached native termination exactly once.
            Assert.Equal(1, spy.TerminateCalls);
            // The actor/reason audit is retained in the durable events.
            var events = await engine.GetEventsAsync(runId, 0, 200, Ct);
            Assert.Contains(events, @event =>
                (@event.DataJson ?? "").Contains("operator", StringComparison.Ordinal) &&
                (@event.DataJson ?? "").Contains("stuck review", StringComparison.Ordinal));

            // A second cancel is an explicit no-op, not an error.
            await engine.CancelAsync(runId, "operator", "again", Ct);
            Assert.Equal(WorkflowStatus.Cancelled, (await engine.GetRunAsync(runId, Ct))!.Status);
            Assert.Equal(1, spy.TerminateCalls);
        }
        finally
        {
            if (step is not null)
            {
                try { await step.DisposeAsync(); } catch { }
            }
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    private static SandboxActivityInput Input() => new()
    {
        AuthorityContext = new AuthenticatedAuthorityContext("tenant-1", "subject-1", "run-1", "arev-1", "fence-1"),
        InvocationId = "sleep",
        Requirements = new ExecutionRequirements(new[]
        {
            ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Partial),
        }),
    };

    private sealed class SpyProvider : IExecutionProvider
    {
        private readonly IExecutionProvider _inner;
        public SpyProvider(IExecutionProvider inner) => _inner = inner;
        public int LaunchCalls { get; private set; }
        public int TerminateCalls { get; private set; }

        public PlatformCapabilities Describe() => _inner.Describe();
        public PrepareResult Prepare(ExecutionRequirements requirements) => _inner.Prepare(requirements);
        public DiscardResult Discard(PreparedExecution preparation) => _inner.Discard(preparation);
        public LaunchResult Launch(PreparedExecution prepared, ProcessStartSpec process)
        {
            LaunchCalls++;
            return _inner.Launch(prepared, process);
        }
        public TerminateResult Terminate(ExecutionHandle execution)
        {
            TerminateCalls++;
            return _inner.Terminate(execution);
        }
        public ValueTask<CompletionResult> WaitForCompletionAsync(ExecutionHandle execution,
            CancellationToken cancellationToken = default) =>
            _inner.WaitForCompletionAsync(execution, cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static async Task PollAsync(Func<bool> condition, int milliseconds = 15000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException("condition not met");
            await Task.Delay(100);
        }
    }

    private static async Task PollAsync(Func<Task<bool>> condition, int milliseconds = 15000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(false))
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException("condition not met");
            await Task.Delay(100);
        }
    }

    private static async Task<bool> PollStateAsync(Func<Task<bool>> condition, int milliseconds = 20000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(false))
        {
            if (watch.ElapsedMilliseconds > milliseconds) return false;
            await Task.Delay(100);
        }

        return true;
    }
}
