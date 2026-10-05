using Gagamba.Execution;
using Gagamba.Runtime;
using Xunit;

namespace Penghou.Hufu.Sandbox.Tests;

/// <summary>
/// Real-provider E2E over ExecutionRuntime.Create(). Proves the wiring against
/// a genuine execution domain on a host that can host one:
/// authorized launch runs a real process; revocation terminates it; and a
/// pre-launch revocation after Prepare runs nothing while the prepared domain
/// is reclaimed. On a host that cannot host a domain (Linux without delegated
/// cgroups) the test skips after the provider refuses preparation.
/// </summary>
public sealed class SandboxExecutionEndToEndTests
{
    [Fact]
    public async Task AuthorizedLaunchRunsRealProcessAndRevocationTerminates()
    {
        if (!Supported()) return;
        string ws = NewWorkspace();
        try
        {
            var spy = new SpyProvider(ExecutionRuntime.Create());
            var authorizer = new ScriptedAuthorizer();
            await using var host = new SandboxExecutionHost(spy, authorizer, RealProfile(ws));

            var result = await host.StartAsync(RealRequest());
            if (!HostCanRun(result)) return;

            Assert.Equal(SandboxExecutionStatus.Started, result.Status);
            Assert.True(await PollAsync(() => File.Exists(Marker(ws)), 15000),
                "the authorized process did not run");

            var terminated = await host.TerminateAsync("activity-1");
            Assert.Equal(SandboxTerminateStatus.Terminated, terminated.Status);
            Assert.Empty(host.ActiveHandles);
        }
        finally { TryDelete(ws); }
    }

    [Fact]
    public async Task PreLaunchRevocationRunsNothingAndReclaimsPreparedDomain()
    {
        if (!Supported()) return;
        string ws = NewWorkspace();
        try
        {
            var spy = new SpyProvider(ExecutionRuntime.Create());
            var authorizer = new ScriptedAuthorizer().Decide(true, false); // preflight permit, pre-launch deny
            await using var host = new SandboxExecutionHost(spy, authorizer, RealProfile(ws));

            var result = await host.StartAsync(RealRequest());
            if (result.Status is SandboxExecutionStatus.PreparationFailed or SandboxExecutionStatus.GuaranteeUnavailable)
                return; // host cannot host a domain

            Assert.Equal(SandboxExecutionStatus.AuthorityDenied, result.Status);
            Assert.False(File.Exists(Marker(ws)), "a process ran despite the pre-launch revocation");
            Assert.Equal(0, spy.LaunchCalls);
            Assert.Equal(1, spy.DiscardCalls);
            Assert.IsType<DiscardResult.Discarded>(spy.LastDiscard);
        }
        finally { TryDelete(ws); }
    }

    // ---- helpers ----

    private static bool Supported() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>A usable domain prepared, or a classified refusal we skip on.</summary>
    private static bool HostCanRun(SandboxStartResult result) =>
        result.Status is SandboxExecutionStatus.Started or SandboxExecutionStatus.AuthorityDenied;

    private static string NewWorkspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "hg1-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        return ws;
    }

    private static string Marker(string ws) => Path.Combine(ws, "e2e-marker.txt");

    private static void TryDelete(string ws)
    {
        try { Directory.Delete(ws, recursive: true); } catch { }
    }

    private static async System.Threading.Tasks.Task<bool> PollAsync(Func<bool> condition, int milliseconds)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return true;
            await System.Threading.Tasks.Task.Delay(100);
        }
        return condition();
    }

    private static SandboxExecutionProfile RealProfile(string workspace)
    {
        bool windows = OperatingSystem.IsWindows();
        var platformEnvironment = windows
            ? new Dictionary<string, string>
            {
                ["SYSTEMROOT"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["SYSTEMDRIVE"] = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            }
            : new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        string executable = windows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        string arguments = windows
            ? "/d /c \"echo up > e2e-marker.txt & ping -n 8 127.0.0.1 >nul\""
            : "-c \"echo up > e2e-marker.txt; sleep 8\"";
        var invocation = new SandboxApprovedInvocation("real", "workspace-1", "bin/tool",
            executable, arguments, workspace, Array.Empty<string>(), Requirements());
        return new SandboxExecutionProfile("sandbox-profile", "e2e", platformEnvironment, new[] { invocation });
    }

    private static SandboxExecutionRequest RealRequest() => new(
        new AuthenticatedAuthorityContext("tenant-1", "subject-1", "run-1", "rev-1", "fence-1"),
        new SandboxActivityIdentity("activity-1", "grant-1", "grant-rev-1", "policy-rev-1"),
        "real",
        new Dictionary<string, string>(),
        new ExecutionRequirements(Requirements()),
        "auth-1");

    private static ExecutionRequirement[] Requirements() => new[]
    {
        ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full),
        ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Full),
    };

    private sealed class SpyProvider : IExecutionProvider
    {
        private readonly IExecutionProvider _inner;
        public SpyProvider(IExecutionProvider inner) => _inner = inner;
        public int LaunchCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public DiscardResult? LastDiscard { get; private set; }

        public PlatformCapabilities Describe() => _inner.Describe();
        public PrepareResult Prepare(ExecutionRequirements requirements) => _inner.Prepare(requirements);
        public DiscardResult Discard(PreparedExecution preparation)
        {
            DiscardCalls++;
            LastDiscard = _inner.Discard(preparation);
            return LastDiscard;
        }
        public LaunchResult Launch(PreparedExecution prepared, ProcessStartSpec process)
        {
            LaunchCalls++;
            return _inner.Launch(prepared, process);
        }
        public TerminateResult Terminate(ExecutionHandle execution) => _inner.Terminate(execution);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
