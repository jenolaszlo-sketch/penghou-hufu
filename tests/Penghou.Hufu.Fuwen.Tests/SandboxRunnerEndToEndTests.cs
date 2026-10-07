using Gagamba.Execution;
using Gagamba.Runtime;
using Hufu.SandboxRunner;
using Xunit;

namespace Penghou.Hufu.Fuwen.Tests;

/// <summary>
/// The first consumer E2E over the real chain: the sample runner admits its
/// fixed plan, drives it with a real Zhinu engine, authorizes against the
/// pinned host profile, and launches <c>whoami</c> in a genuine Gagamba
/// domain. Hosting ability is probed structurally on the frozen SPI before
/// the consumer runs: a pre-acceptance refusal (no provider, unsatisfiable
/// negotiation, or unpreparable domain) skips with the exact classified
/// reasons. Anything after a successful Prepare — launch, completion,
/// admission, engine behavior — must succeed; post-acceptance failure is a
/// FAIL, never a skip.
/// </summary>
public sealed class SandboxRunnerEndToEndTests
{
    [SkippableFact]
    public async Task RunnerExecutesPinnedWhoamiThroughTheRealChain()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return;
        await ProbeHostingAbility();

        string workspace = Path.Combine(Path.GetTempPath(), "sandbox-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var record = await SandboxRunner.RunAsync(workspace);
            Assert.True(string.Equals(record.Status, "Succeeded", StringComparison.Ordinal),
                record.Status + ": " + record.Reason);
            Assert.Equal(SandboxRunner.InvocationId, record.Invocation);
            Assert.Equal(0, record.RootExitCode);
            Assert.NotEqual(Guid.Empty, Guid.Parse(record.RunId));
            Assert.False(string.IsNullOrWhiteSpace(record.AuthorityRequestId));
            Assert.Equal(SandboxRunner.ProfileId, record.ProfileId);
            Assert.Equal(SandboxRunner.ProfileRevision, record.ProfileRevision);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    private static async Task ProbeHostingAbility()
    {
        await using var runtime = ExecutionRuntime.Create();
        Skip.If(!runtime.HasProvider,
            "Host cannot host the diagnostic domain: " + runtime.RefusalReason);
        var prepared = runtime.Prepare(SandboxRunner.BuildRequirements());
        if (prepared is PrepareResult.Rejected rejected)
            Skip.If(true, "Host cannot host the diagnostic domain: " +
                string.Join("; ", rejected.Reasons));
        runtime.Discard(((PrepareResult.Accepted)prepared).Prepared);
    }
}
