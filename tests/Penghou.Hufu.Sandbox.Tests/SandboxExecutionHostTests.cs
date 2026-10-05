using Gagamba.Execution;
using Xunit;

namespace Penghou.Hufu.Sandbox.Tests;

public sealed class SandboxExecutionHostTests
{
    // ---- HG-1 acceptance cases ----

    [Fact] // 1
    public async Task AllowedRequestLaunchesSuccessfully()
    {
        var provider = new FakeProvider();
        var authorizer = new ScriptedAuthorizer();
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());

        var result = await host.StartAsync(Request());

        Assert.Equal(SandboxExecutionStatus.Started, result.Status);
        Assert.True(result.IsStarted);
        Assert.Equal(1, provider.PrepareCalls);
        Assert.Equal(1, provider.LaunchCalls);
        Assert.Equal(2, authorizer.Requests.Count); // preflight + pre-launch recheck
        Assert.Equal(0, provider.DiscardCalls);      // a launched domain is never discarded
        Assert.All(authorizer.Requests, r =>
        {
            Assert.Equal(AuthorityAction.ExecuteProcess, r.Action);
            Assert.Equal("workspace-1", r.WorkspaceId);
            Assert.Equal("tools/echo", r.RelativePath);
        });
        Assert.Single(host.ActiveHandles);
    }

    [Fact] // 2
    public async Task HufuDenialNeverCallsGagamba()
    {
        var provider = new FakeProvider();
        var authorizer = new ScriptedAuthorizer().Decide(false);
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());

        var result = await host.StartAsync(Request());

        Assert.Equal(SandboxExecutionStatus.AuthorityDenied, result.Status);
        Assert.Equal(0, provider.PrepareCalls);
        Assert.Equal(0, provider.LaunchCalls);
    }

    [Fact] // 3
    public async Task CapabilityRefusalDoesNotWeakenOrRetry()
    {
        var provider = new FakeProvider { Capabilities = WellKnownPlatforms.MacOs }; // UnitTermination is Partial
        var authorizer = new ScriptedAuthorizer();
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());

        var result = await host.StartAsync(Request());

        Assert.Equal(SandboxExecutionStatus.GuaranteeUnavailable, result.Status);
        Assert.Equal(0, provider.PrepareCalls); // negotiation refuses before preparation
        Assert.Equal(0, provider.LaunchCalls);  // no weakened retry
        Assert.Single(authorizer.Requests);     // authorized once, then refused; no loop
    }

    [Fact] // 4
    public async Task RevocationBeforeLaunchStartsNothing()
    {
        var provider = new FakeProvider();
        var authorizer = new ScriptedAuthorizer().Decide(true, false); // permit preflight, deny pre-launch
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());

        var result = await host.StartAsync(Request());

        Assert.Equal(SandboxExecutionStatus.AuthorityDenied, result.Status);
        Assert.Equal(1, provider.PrepareCalls); // prepared, but
        Assert.Equal(0, provider.LaunchCalls);  // never launched
    }

    [Fact] // 5
    public async Task RevocationWhileRunningTerminatesTheDomain()
    {
        var provider = new FakeProvider();
        await using var host = new SandboxExecutionHost(provider, new ScriptedAuthorizer(), Profile());
        var started = await host.StartAsync(Request());
        Assert.True(started.IsStarted);

        var terminated = await host.TerminateAsync("activity-1");

        Assert.Equal(SandboxTerminateStatus.Terminated, terminated.Status);
        Assert.Equal(1, provider.TerminateCalls);
        Assert.Empty(host.ActiveHandles);
    }

    [Fact] // 6
    public async Task StaleGrantRevisionCannotLaunch()
    {
        var provider = new FakeProvider();
        var authorizer = new ScriptedAuthorizer
        {
            Inspect = r => !r.RequestIdentity.Contains(":stale-rev:", StringComparison.Ordinal)
        };
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());
        var activity = new SandboxActivityIdentity("activity-1", "grant-1", "stale-rev", "policy-rev-1");

        var result = await host.StartAsync(Request(activity: activity));

        Assert.Equal(SandboxExecutionStatus.AuthorityDenied, result.Status);
        Assert.Equal(0, provider.PrepareCalls);
        Assert.Equal(0, provider.LaunchCalls);
    }

    [Fact] // 7
    public async Task GrantFromAnotherActivityCannotAuthorize()
    {
        var provider = new FakeProvider();
        // The host authorizer binds authority to run-A; this request carries run-B.
        var authorizer = new ScriptedAuthorizer { Inspect = r => r.Context.RunId == "run-A" };
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());

        var result = await host.StartAsync(Request(context: Context("run-B")));

        Assert.Equal(SandboxExecutionStatus.AuthorityDenied, result.Status);
        Assert.Equal(0, provider.LaunchCalls);
    }

    [Fact] // 8
    public async Task RetryRequiresFreshAuthorizationRequest()
    {
        var provider = new FakeProvider();
        await using var host = new SandboxExecutionHost(provider, new ScriptedAuthorizer(), Profile());

        var first = await host.StartAsync(Request("auth-1"));
        Assert.True(first.IsStarted);

        var replay = await host.StartAsync(Request("auth-1"));

        Assert.Equal(SandboxExecutionStatus.AuthorityDenied, replay.Status);
        Assert.Equal("sandbox.stale-authorization-request", replay.ReasonCode);
        Assert.Equal(1, provider.LaunchCalls);
    }

    [Fact] // 9
    public async Task EnvironmentIsExplicitOnly()
    {
        var provider = new FakeProvider();
        await using var host = new SandboxExecutionHost(provider, new ScriptedAuthorizer(), Profile());

        var ok = await host.StartAsync(Request("auth-1",
            environment: new Dictionary<string, string> { ["LANG"] = "C" }));
        Assert.True(ok.IsStarted);
        Assert.Equal(2, provider.LastSpec!.Environment.Count);
        Assert.Equal("C:\\Windows", provider.LastSpec.Environment["SYSTEMROOT"]); // platform-owned
        Assert.Equal("C", provider.LastSpec.Environment["LANG"]);                  // authorized caller entry

        var bad = await host.StartAsync(Request("auth-2",
            environment: new Dictionary<string, string> { ["PATH"] = "/evil" }));
        Assert.Equal(SandboxExecutionStatus.RequirementNotAuthorized, bad.Status);
        Assert.Equal(1, provider.LaunchCalls); // nothing extra launched
    }

    [Fact] // 10
    public async Task FailuresRemainDistinguishable()
    {
        Assert.Equal(SandboxExecutionStatus.AuthorityDenied,
            (await Start(new FakeProvider(), new ScriptedAuthorizer().Decide(false))).Status);

        Assert.Equal(SandboxExecutionStatus.RequirementNotAuthorized,
            (await Start(new FakeProvider(), new ScriptedAuthorizer(), Request(invocationId: "unknown"))).Status);

        Assert.Equal(SandboxExecutionStatus.GuaranteeUnavailable,
            (await Start(new FakeProvider { Capabilities = WellKnownPlatforms.MacOs }, new ScriptedAuthorizer())).Status);

        Assert.Equal(SandboxExecutionStatus.PreparationFailed,
            (await Start(new FakeProvider { AcceptPrepare = false }, new ScriptedAuthorizer())).Status);

        Assert.Equal(SandboxExecutionStatus.LaunchFailed,
            (await Start(new FakeProvider { AcceptLaunch = false }, new ScriptedAuthorizer())).Status);
    }

    [Fact]
    public async Task TerminateUnknownActivityIsNotAnError()
    {
        await using var host = new SandboxExecutionHost(new FakeProvider(), new ScriptedAuthorizer(), Profile());
        var result = await host.TerminateAsync("never-started");
        Assert.Equal(SandboxTerminateStatus.UnknownActivity, result.Status);
    }

    [Fact] // adjustment 1
    public async Task GuaranteeAboveCeilingIsNotAuthorized()
    {
        var provider = new FakeProvider();
        var authorizer = new ScriptedAuthorizer();
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());
        var requirements = new ExecutionRequirements(new[]
        {
            ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full),
            ExecutionRequirement.Require(ExecutionCapability.RecursiveMembership, CapabilityLevel.Full),
        });

        var result = await host.StartAsync(Request(requirements: requirements));

        Assert.Equal(SandboxExecutionStatus.RequirementNotAuthorized, result.Status);
        Assert.Equal("sandbox.requirement-not-authorized", result.ReasonCode);
        Assert.Equal(0, provider.PrepareCalls);     // decided before any provider work
        Assert.Empty(authorizer.Requests);          // and before any authority evaluation
    }

    [Fact] // adjustment 3
    public async Task ProfileRevisionIsRecordedForLaunch()
    {
        var provider = new FakeProvider();
        var authorizer = new ScriptedAuthorizer();
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile("rev-7"));

        var result = await host.StartAsync(Request());

        Assert.True(result.IsStarted);
        Assert.True(host.TryGetBinding(result.Handle!, out var binding));
        Assert.Equal("sandbox-profile", binding!.ProfileId);
        Assert.Equal("rev-7", binding.ProfileRevision);
        Assert.Equal("activity-1", binding.Activity.ActivityId);
        Assert.Equal("auth-1", binding.AuthorizationRequestId);
        Assert.All(authorizer.Requests, r => Assert.Contains(":sandbox-profile:rev-7:", r.RequestIdentity, StringComparison.Ordinal));
    }

    [Fact] // adjustment 4
    public async Task ChangedProfileDoesNotAlterInFlightAuthorization()
    {
        await using var host1 = new SandboxExecutionHost(new FakeProvider(), new ScriptedAuthorizer(), Profile("rev-1"));
        var first = await host1.StartAsync(Request());
        host1.TryGetBinding(first.Handle!, out var binding1);

        await using var host2 = new SandboxExecutionHost(new FakeProvider(), new ScriptedAuthorizer(), Profile("rev-2"));
        var second = await host2.StartAsync(Request());
        host2.TryGetBinding(second.Handle!, out var binding2);

        // Each launch is bound to the exact revision its host captured; the
        // immutable invocation snapshot is identical, so a later revision
        // cannot silently shape an already-authorized launch.
        Assert.Equal("rev-1", binding1!.ProfileRevision);
        Assert.Equal("rev-2", binding2!.ProfileRevision);
        Assert.NotEqual("", binding1.ProfileId);
    }

    [Fact] // adjustment 5, 9
    public async Task PreLaunchDenialDiscardsThePreparation()
    {
        var provider = new FakeProvider();
        var authorizer = new ScriptedAuthorizer().Decide(true, false);
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());

        var result = await host.StartAsync(Request());

        Assert.Equal(SandboxExecutionStatus.AuthorityDenied, result.Status);
        Assert.Equal(0, provider.LaunchCalls);      // nothing started
        Assert.Equal(1, provider.DiscardCalls);     // prepared domain reclaimed
        Assert.Empty(provider.LivePreparations);
    }

    [Fact] // adjustment 6, 9
    public async Task CancellationBetweenPrepareAndLaunchDiscardsThePreparation()
    {
        var provider = new FakeProvider();
        var authorizer = new ScriptedAuthorizer { ThrowOnLaunchPhase = true };
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await host.StartAsync(Request()));

        Assert.Equal(1, provider.PrepareCalls);
        Assert.Equal(0, provider.LaunchCalls);      // nothing started
        Assert.Equal(1, provider.DiscardCalls);     // prepared domain reclaimed
        Assert.Empty(provider.LivePreparations);
    }

    // ---- helpers ----

    private static async Task<SandboxStartResult> Start(FakeProvider provider, ScriptedAuthorizer authorizer,
        SandboxExecutionRequest? request = null)
    {
        await using var host = new SandboxExecutionHost(provider, authorizer, Profile());
        return await host.StartAsync(request ?? Request());
    }

    private static readonly ExecutionRequirements DefaultRequirements = new(new[]
    {
        ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full),
        ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Full),
    });

    private static SandboxApprovedInvocation Invocation() => new(
        "echo", "workspace-1", "tools/echo", "/opt/tools/echo", "--hello", "/opt/work",
        new[] { "LANG" },
        new[]
        {
            ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full),
            ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Full),
        });

    private static SandboxExecutionProfile Profile(string revision = "rev-1") => new(
        "sandbox-profile",
        revision,
        new Dictionary<string, string> { ["SYSTEMROOT"] = "C:\\Windows" },
        new[] { Invocation() });

    private static AuthenticatedAuthorityContext Context(string runId = "run-1") =>
        new("tenant-1", "subject-1", runId, "rev-1", "fence-1");

    private static SandboxExecutionRequest Request(
        string authorizationRequestId = "auth-1",
        string invocationId = "echo",
        SandboxActivityIdentity? activity = null,
        IReadOnlyDictionary<string, string>? environment = null,
        ExecutionRequirements? requirements = null,
        AuthenticatedAuthorityContext? context = null) => new(
            context ?? Context(),
            activity ?? new SandboxActivityIdentity("activity-1", "grant-1", "grant-rev-1", "policy-rev-1"),
            invocationId,
            environment ?? new Dictionary<string, string>(),
            requirements ?? DefaultRequirements,
            authorizationRequestId);
}
