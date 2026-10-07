using Gagamba.Execution;
using Gagamba.Runtime;
using Hufu.SandboxRunner;
using Penghou.Hufu;
using Penghou.Hufu.Sandbox;
using Xunit;

namespace Penghou.Hufu.Fuwen.Tests;

/// <summary>
/// TEMPORARY diagnostic split (PR only): bisects the macOS hang in the full
/// runner test into probe / direct-host / full-stack phases. Deleted after
/// the root cause is identified.
/// </summary>
public sealed class MacOsHangBisectionTests
{
    [SkippableFact]
    public async Task Phase1_ProbeOnlyReportsStructuredVerdict()
    {
        var (canRun, reasons) = await SandboxRunner.ProbeHostingAbilityAsync();
        if (!canRun)
            Skip.If(true, "Host cannot host: " + string.Join("; ", reasons));
        Assert.True(canRun);
    }

    [SkippableFact]
    public async Task Phase2_DirectHostStartAndWaitCompletes()
    {
        var (canRun, reasons) = await SandboxRunner.ProbeHostingAbilityAsync();
        if (!canRun)
            Skip.If(true, "Host cannot host: " + string.Join("; ", reasons));

        bool windows = OperatingSystem.IsWindows();
        string executable = windows
            ? Path.Combine(Environment.SystemDirectory, "whoami.exe")
            : "/usr/bin/whoami";
        var platformEnvironment = windows
            ? new Dictionary<string, string>
            {
                ["SYSTEMROOT"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["SYSTEMDRIVE"] = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            }
            : new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        var invocation = new SandboxApprovedInvocation(SandboxRunner.InvocationId, SandboxRunner.WorkspaceId,
            SandboxRunner.ExecutableRelativePath, executable, "", Path.GetTempPath(), Array.Empty<string>(),
            SandboxRunner.BuildRequirements().Required);
        var profile = new SandboxExecutionProfile(SandboxRunner.ProfileId, SandboxRunner.ProfileRevision,
            platformEnvironment, new[] { invocation });
        var authorizer = new PinnedExecutionAuthorizer(SandboxRunner.WorkspaceId,
            SandboxRunner.ExecutableRelativePath, new string('b', 64));

        await using var host = new SandboxExecutionHost(ExecutionRuntime.Create(), authorizer, profile);
        var request = new SandboxExecutionRequest(
            new AuthenticatedAuthorityContext("diagnostic-tenant", "sandbox-runner", "run-1", "rev-1", "fence-1"),
            new SandboxActivityIdentity("activity-1", "grant-1", "rev-1", "policy-1"),
            SandboxRunner.InvocationId,
            new Dictionary<string, string>(),
            SandboxRunner.BuildRequirements(),
            "auth-bisect-1");
        var start = await host.StartAsync(request);
        Assert.True(start.Status == SandboxExecutionStatus.Started,
            "Start: " + start.Status + " " + (start.ReasonCode ?? string.Join("; ", start.Reasons ?? Array.Empty<string>())));
        var completion = await host.WaitForCompletionAsync(start.Handle!);
        Assert.True(completion.Status == SandboxCompletionStatus.NaturalExit,
            "Completion: " + completion.Status);
        Assert.Equal(0, completion.RootExitCode);
    }
}
