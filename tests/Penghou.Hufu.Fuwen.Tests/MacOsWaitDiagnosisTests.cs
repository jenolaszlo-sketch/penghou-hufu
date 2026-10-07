using System.Diagnostics;
using System.Text;
using Gagamba.Execution;
using Gagamba.Runtime;
using Hufu.SandboxRunner;
using Penghou.Hufu.Sandbox;
using Xunit;

namespace Penghou.Hufu.Fuwen.Tests;

/// <summary>
/// TEMPORARY diagnostic (PR only): bounds the macOS completion wait and dumps
/// live launchd domain state into the failure message, so the CI log shows
/// ground truth instead of a silent hang. Deleted after diagnosis.
/// </summary>
public sealed class MacOsWaitDiagnosisTests
{
    [SkippableFact]
    public async Task DiagnoseWhoamiCompletionOnMacOs()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var (canRun, reasons) = await SandboxRunner.ProbeHostingAbilityAsync();
        if (!canRun)
            Skip.If(true, "Host cannot host: " + string.Join("; ", reasons));

        // Control first: the GM-2-proven shape through the same host stack.
        var shResult = await RunOnce("/bin/sh", "-c \"exit 0\"");
        Assert.True(shResult.Completed, "sh exit 0 did not complete: " + shResult.Detail);

        // Suspect: whoami with empty args through the same stack.
        var whoamiResult = await RunOnce("/usr/bin/whoami", "");
        Assert.True(whoamiResult.Completed,
            $"whoami did not complete within 60s. {whoamiResult.Detail} Domain state:\n{PrintDomain()}");
        Assert.Equal(0, whoamiResult.ExitCode);
    }

    private sealed record Attempt(bool Completed, string Detail, int ExitCode);

    private static async Task<Attempt> RunOnce(string executable, string arguments)
    {
        var platformEnvironment = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        var invocation = new SandboxApprovedInvocation("diag", "diagnostic", "tools/diag",
            executable, arguments, Path.GetTempPath(), Array.Empty<string>(),
            SandboxRunner.BuildRequirements().Required);
        var profile = new SandboxExecutionProfile("diag-profile", "r1", platformEnvironment, new[] { invocation });
        var authorizer = new PinnedExecutionAuthorizer("diagnostic", "tools/diag", new string('c', 64));

        await using var host = new SandboxExecutionHost(ExecutionRuntime.Create(), authorizer, profile);
        var request = new SandboxExecutionRequest(
            new Penghou.Hufu.AuthenticatedAuthorityContext("t", "s", "r", "rev", "fence"),
            new SandboxActivityIdentity("activity-1", "grant-1", "rev-1", "policy-1"),
            "diag",
            new Dictionary<string, string>(),
            SandboxRunner.BuildRequirements(),
            "auth-diag-" + Guid.NewGuid().ToString("N"));
        var start = await host.StartAsync(request);
        if (start.Status != SandboxExecutionStatus.Started)
            return new Attempt(false, "Start: " + start.Status, -1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var completion = await host.WaitForCompletionAsync(start.Handle!, cts.Token);
            return completion.Status switch
            {
                SandboxCompletionStatus.NaturalExit => new Attempt(true, "", completion.RootExitCode ?? -1),
                _ => new Attempt(false, "Completion: " + completion.Status, -1),
            };
        }
        catch (OperationCanceledException)
        {
            return new Attempt(false, "Wait timed out after 60s", -1);
        }
    }

    private static string PrintDomain()
    {
        try
        {
            string uid = RunCapture("id", "-u").Trim();
            string output = RunCapture("launchctl", $"print gui/{uid}");
            if (output.Length > 6000)
            {
                var kept = new StringBuilder();
                foreach (string line in output.Split('\n'))
                    if (line.Contains("gagamba", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("state =", StringComparison.Ordinal) ||
                        line.Contains("exit code", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("pid =", StringComparison.Ordinal))
                        kept.AppendLine(line.Trim());
                return kept.ToString();
            }
            return output;
        }
        catch (Exception ex)
        {
            return "domain capture failed: " + ex.GetType().Name;
        }
    }

    private static string RunCapture(string file, string args)
    {
        var psi = new ProcessStartInfo(file, "")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string part in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(part);
        using var process = Process.Start(psi);
        if (process is null) return "";
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        return output + error;
    }
}
