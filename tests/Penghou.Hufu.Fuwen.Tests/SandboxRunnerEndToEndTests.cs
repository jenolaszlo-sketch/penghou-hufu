using Hufu.SandboxRunner;
using Xunit;

namespace Penghou.Hufu.Fuwen.Tests;

/// <summary>
/// The first consumer E2E over the real chain: the sample runner admits its
/// fixed plan, drives it with a real Zhinu engine, authorizes against the
/// pinned host profile, and launches <c>whoami</c> in a genuine Gagamba
/// domain. On a host that cannot host a domain the provider refuses with a
/// classified status and the test yields, following the HG-1 E2E convention.
/// </summary>
public sealed class SandboxRunnerEndToEndTests
{
    [Fact]
    public async Task RunnerExecutesPinnedWhoamiThroughTheRealChain()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return;
        string workspace = Path.Combine(Path.GetTempPath(), "sandbox-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var record = await SandboxRunner.RunAsync(workspace);
            if (!string.Equals(record.Status, "Succeeded", StringComparison.Ordinal) && IsHostRefusal(record.Reason))
                return; // classified provider refusal; the host cannot host a domain here.
            Assert.True(string.Equals(record.Status, "Succeeded", StringComparison.Ordinal), record.Reason);
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

    private static bool IsHostRefusal(string reason) =>
        reason.Contains("Guarantee unavailable", StringComparison.Ordinal) ||
        reason.Contains("Sandbox failed", StringComparison.Ordinal);
}
