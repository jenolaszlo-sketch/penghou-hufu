using Gagamba.Execution;
using Penghou.Fuwen;
using Penghou.Hufu.Fuwen;
using Penghou.Hufu.Sandbox;
using Xunit;

namespace Penghou.Hufu.Fuwen.Tests;

public sealed class SandboxActivityExecutorTests
{
    // ---- FZ-1 acceptance cases ----

    [Fact] // 1
    public async Task AllowedIntentExecutesThroughTheAuthorityPath()
    {
        var f = Fixture(WellKnownPlatforms.Windows, Ceiling(ExecutionCapability.UnitTermination, ExecutionCapability.SurvivesRootExit));

        var result = await f.Executor.ExecuteAsync(Request(Intent(("execution.unit-termination", ExecutionGuaranteeLevel.Full))));

        Assert.Null(result.Failure);
        Assert.NotNull(result.Output);
    }

    [Fact] // 5 (over ceiling)
    public async Task RequestAboveTheTrustedCeilingIsRequirementNotAuthorized()
    {
        // Ceiling permits unit-termination and root-exit only.
        var f = Fixture(WellKnownPlatforms.Windows, Ceiling(ExecutionCapability.UnitTermination, ExecutionCapability.SurvivesRootExit));

        var result = await f.Executor.ExecuteAsync(
            Request(Intent(("execution.owner-death-cleanup", ExecutionGuaranteeLevel.Full))));

        Assert.NotNull(result.Failure);
        Assert.Equal(ExecutionFailureCode.PolicyRejected, result.Failure!.Code);
        Assert.Equal(0, f.Provider.PrepareCalls); // refused before any provider work
    }

    [Fact] // 6 (host inability)
    public async Task PermittedButUnavailableGuaranteeIsGuaranteeUnavailable()
    {
        // Ceiling permits owner-death-cleanup; the macOS-native platform cannot provide it.
        var f = Fixture(WellKnownPlatforms.MacOs, Ceiling(
            ExecutionCapability.SurvivesRootExit, ExecutionCapability.OwnerDeathCleanup));

        var result = await f.Executor.ExecuteAsync(
            Request(Intent(("execution.owner-death-cleanup", ExecutionGuaranteeLevel.Full))));

        Assert.NotNull(result.Failure);
        Assert.Equal(ExecutionFailureCode.ProviderError, result.Failure!.Code);
    }

    [Fact]
    public async Task UnknownProfileIsRefused()
    {
        var f = Fixture(WellKnownPlatforms.Windows, Ceiling(ExecutionCapability.UnitTermination));

        var result = await f.Executor.ExecuteAsync(Request(
            new ActivityExecutionIntent("no-such-profile",
                [new ExecutionGuarantee("execution.unit-termination", ExecutionGuaranteeLevel.Full)], [])));

        Assert.Equal(ExecutionFailureCode.PolicyRejected, result.Failure!.Code);
    }

    [Fact]
    public async Task UnknownCapabilityIdentifierFailsClosed()
    {
        var f = Fixture(WellKnownPlatforms.Windows, Ceiling(ExecutionCapability.UnitTermination));

        var result = await f.Executor.ExecuteAsync(
            Request(Intent(("execution.not-a-capability", ExecutionGuaranteeLevel.Full))));

        Assert.Equal(ExecutionFailureCode.PolicyRejected, result.Failure!.Code);
    }

    [Fact] // 7
    public async Task EveryAttemptGetsFreshAuthorizationAndDistinctIdentity()
    {
        var f = Fixture(WellKnownPlatforms.Windows, Ceiling(ExecutionCapability.UnitTermination));
        var intent = Intent(("execution.unit-termination", ExecutionGuaranteeLevel.Full));

        await f.Executor.ExecuteAsync(Request(intent, stepRevision: "1"));
        await f.Executor.ExecuteAsync(Request(intent, stepRevision: "2"));

        // Two attempts -> two authority requests each (preflight + pre-launch), all distinct.
        Assert.Equal(4, f.Authorizer.Requests.Count);
        Assert.Equal(4, f.Authorizer.Requests.Select(r => r.RequestIdentity).Distinct().Count());
    }

    [Fact] // 8 (audit correlation)
    public async Task AuthorityRequestIdentityCorrelatesProfileRevisionAndFuwenIdentity()
    {
        var f = Fixture(WellKnownPlatforms.Windows, Ceiling(ExecutionCapability.UnitTermination));
        var request = Request(Intent(("execution.unit-termination", ExecutionGuaranteeLevel.Full)));

        await f.Executor.ExecuteAsync(request);

        var identity = Assert.Single(f.Authorizer.Requests.Take(1)).RequestIdentity;
        // Fuwen operation key -> authority request -> trusted profile revision.
        Assert.Contains(request.Invocation.OperationKey, identity, StringComparison.Ordinal);
        Assert.Contains("fz1-profile", identity, StringComparison.Ordinal);
        Assert.Contains(":r1:", identity, StringComparison.Ordinal);
    }

    [Fact] // 7 (negative: plan cannot manufacture authority)
    public void ActivityRequestExposesNoDownstreamControlledAuthority()
    {
        string[] banned = ["Executable", "Environment", "GuaranteeCeiling", "ProfileRevision", "GrantRevision", "Handle", "Pid"];
        var offenders = typeof(ActivityExecutionRequest).GetProperties()
            .Where(p => banned.Any(b => p.Name.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToArray();
        Assert.Empty(offenders);
    }

    // ---- helpers ----

    private sealed record Harness(SandboxActivityExecutor Executor, FakeProvider Provider, FakeAuthorizer Authorizer);

    private static Harness Fixture(PlatformCapabilities capabilities, IReadOnlyList<ExecutionRequirement> ceiling)
    {
        var provider = new FakeProvider { Capabilities = capabilities };
        var authorizer = new FakeAuthorizer();
        var invocation = new SandboxApprovedInvocation("echo", "workspace-1", "bin/echo", "/bin/echo",
            "-n hi", "/tmp", Array.Empty<string>(), ceiling);
        var profile = new SandboxExecutionProfile("fz1-profile", "r1", new Dictionary<string, string>(), new[] { invocation });
        var host = new SandboxExecutionHost(provider, authorizer, profile);
        var executor = new SandboxActivityExecutor(host,
            new Dictionary<string, string> { ["echo"] = "echo" },
            NeutralGuaranteeMap.Default, new FixedAuthority());
        return new Harness(executor, provider, authorizer);
    }

    private static ExecutionRequirement[] Ceiling(params ExecutionCapability[] capabilities) =>
        capabilities.Select(c => ExecutionRequirement.Require(c, CapabilityLevel.Full)).ToArray();

    private static ActivityExecutionIntent Intent(params (string Capability, ExecutionGuaranteeLevel Level)[] required) =>
        new("echo", required.Select(r => new ExecutionGuarantee(r.Capability, r.Level)).ToArray(),
            Array.Empty<ExecutionGuarantee>());

    private static ActivityExecutionRequest Request(ActivityExecutionIntent intent, string stepRevision = "1")
    {
        var invocation = new ExecutionInvocation(
            "sha256:fuwen-execution/v1:" + new string('a', 64),
            "workflow/build",
            "run/build",
            stepRevision,
            "sha256:fuwen-request/v1:" + new string('b', 64));
        var activity = new DescriptorReference(DescriptorKind.Activity, "sandbox-execute", "1",
            new ContentDigest("sha256", "fuwen-descriptor/v1", new string('c', 64)));
        return new ActivityExecutionRequest(invocation, activity, Array.Empty<RuntimeArgument>(),
            new PrimitiveType(FuwenPrimitiveKind.String), intent);
    }
}
