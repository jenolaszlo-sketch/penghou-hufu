using Gagamba.Execution;
using Gagamba.Runtime;
using Xunit;

namespace Penghou.Hufu.Sandbox.Tests;

/// <summary>
/// Real-provider termination proof: a long sleep runs in a genuine execution
/// domain; termination ends it and completion reports Terminated with no
/// remaining handles. No engine, no SQLite, no timing races:
/// terminate-then-observe is causal. This complements the engine-driven
/// fake-provider cancellation tests (wiring plus durable evidence) without
/// duplicating their timing-sensitive full-stack shape.
/// </summary>
public sealed class RealDomainTerminationTests
{
    [Fact]
    public async Task RevocationStyleTerminateEndsRealDomain()
    {
        if (!Supported()) return;
        string ws = NewWorkspace();
        try
        {
            await using var host = new SandboxExecutionHost(
                ExecutionRuntime.Create(), new ScriptedAuthorizer(), RealProfile(ws));

            var result = await host.StartAsync(RealRequest());
            if (result.Status is SandboxExecutionStatus.PreparationFailed or SandboxExecutionStatus.GuaranteeUnavailable)
                return; // host cannot host a domain (convention of this suite)
            Assert.Equal(SandboxExecutionStatus.Started, result.Status);
            Assert.NotEmpty(host.ActiveHandles);

            // The wait must be in flight before termination, on its own
            // thread: the Windows provider wait blocks in native waits
            // until the domain ends, and TerminateAsync reclaims the handle
            // mapping, so a later wait would not find it. This mirrors
            // SandboxExecutionStep's cancel path exactly.
            var waitTask = Task.Run(() => host.WaitForCompletionAsync(result.Handle!).AsTask());
            Thread.Sleep(TimeSpan.FromSeconds(2));
            var terminated = await host.TerminateAsync("activity-1");
            Assert.True(terminated.Status == SandboxTerminateStatus.Terminated,
                "Terminate: " + string.Join(";", terminated.Reasons ?? Array.Empty<string>()));

            var completion = await waitTask.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.True(completion.Status == SandboxCompletionStatus.Terminated,
                "Completion: " + string.Join(";", completion.Reasons ?? Array.Empty<string>()));
            Assert.Empty(host.ActiveHandles);
        }
        finally { TryDelete(ws); }
    }

    // ---- helpers ----

    private static bool Supported() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static string NewWorkspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "hg1-terminate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        return ws;
    }

    private static void TryDelete(string ws)
    {
        try { Directory.Delete(ws, recursive: true); } catch { }
    }

    private static SandboxExecutionProfile RealProfile(string workspace)
    {
        bool windows = OperatingSystem.IsWindows();
        var platformEnvironment = windows
            ? new Dictionary<string, string>
            {
                ["SYSTEMROOT"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["SYSTEMDRIVE"] = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
                ["PSModulePath"] = Environment.GetEnvironmentVariable("PSModulePath") ?? "",
            }
            : new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin" };
        // A ~30 second sleep: terminate arrives in milliseconds, so a broken
        // termination still surfaces quickly as a natural exit instead of a hang.
        string executable = windows
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/bin/sh";
        string arguments = windows
            ? "-NoProfile -Command \"Start-Sleep -Seconds 30\""
            : "-c \"sleep 30\"";
        var invocation = new SandboxApprovedInvocation("sleep", "workspace-1", "bin/sleep",
            executable, arguments, workspace, Array.Empty<string>(), Requirements());
        return new SandboxExecutionProfile("terminate-profile", "r1", platformEnvironment, new[] { invocation });
    }

    private static SandboxExecutionRequest RealRequest() => new(
        new AuthenticatedAuthorityContext("tenant-1", "subject-1", "run-1", "rev-1", "fence-1"),
        new SandboxActivityIdentity("activity-1", "grant-1", "grant-rev-1", "policy-rev-1"),
        "sleep",
        new Dictionary<string, string>(),
        new ExecutionRequirements(Requirements()),
        "auth-1");

    private static ExecutionRequirement[] Requirements() => new[]
    {
        ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Partial),
    };
}
