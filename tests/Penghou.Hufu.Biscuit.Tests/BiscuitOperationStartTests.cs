using Penghou.Hufu.Sqlite;
using static Penghou.Hufu.Biscuit.Tests.BiscuitIntegrationFixture;

namespace Penghou.Hufu.Biscuit.Tests;

public sealed class BiscuitOperationStartTests
{
    private const string BindingJson = """{"provider":"test-provider","object":"object-1","effect":"effect-1"}""";
    private static string BindingHash => BiscuitProfile.Hash(BiscuitProfile.Utf8.GetBytes(BindingJson));
    private static AuthorityOperationStartCommand Start(BiscuitIntegrationFixture fixture,AuthorityRequest request,
        BiscuitVerificationResult verification,string? id=null) =>
        new(Actor,id ?? Guid.NewGuid().ToString("N"),request,verification.VerificationId!,fixture.Sequence,BindingHash,BindingJson);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrKeyRetirementBlocksFreshStartButReceiptReplayNeverRedispatches(bool retireKey)
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync(); var request=fixture.Request(AuthorityAction.PatchFile);
        var verification=await fixture.Service.VerifyAsync(root,request,BindingHash);
        Assert.True(verification.IsAuthorized,verification.FailureCode.ToString());
        var participant=new RuntimeParticipant(); var gate=fixture.StartGate(request,participant);
        var command=Start(fixture,request,verification);
        Assert.Equal(AuthorityOperationStartStatus.Started,(await gate.StartAsync(command)).Status);
        Assert.Equal(1,participant.Calls); Assert.Equal(1,await fixture.RuntimeCountAsync());
        if (retireKey) await fixture.Registry.RetireKeyAsync(Actor,Realm,"key-1","retired");
        else await fixture.Registry.RevokeAsync(Actor,Realm,Context,Fingerprint(root),"revoked");
        Assert.Equal(AuthorityOperationStartStatus.AlreadyStarted,(await gate.StartAsync(command)).Status);
        Assert.Equal(AuthorityOperationStartStatus.StaleRuntime,
            (await gate.StartAsync(command with { OperationId=Guid.NewGuid().ToString("N") })).Status);
        Assert.Equal(1,participant.Calls); Assert.Equal(1,await fixture.RuntimeCountAsync());
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("evaluator")]
    [InlineData("binding")]
    [InlineData("request")]
    [InlineData("missing-registry")]
    [InlineData("core-only")]
    [InlineData("corrupt-evidence")]
    public async Task ExactStartEvidenceAndTrustedProfileAreMandatory(string kind)
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync(); var request=fixture.Request(AuthorityAction.PatchFile);
        if (kind=="core-only") fixture.FailRegistryEvidence=true;
        var verification=await fixture.Service.VerifyAsync(root,request,BindingHash);
        var participant=new RuntimeParticipant();
        var gate=fixture.StartGate(request,participant,engine:kind=="engine" ? "wrong-engine" : null,
            evaluator:kind=="evaluator" ? "wrong-evaluator" : null);
        var command=Start(fixture,request,verification);
        if (kind=="binding") {
            var json="""{"provider":"other-provider"}""";
            command=command with { BindingJson=json,BindingIdentity=BiscuitProfile.Hash(BiscuitProfile.Utf8.GetBytes(json)) };
        }
        if (kind=="request") command=command with { Request=request with { RelativePath="src/another.txt" } };
        if (kind=="missing-registry") await fixture.ExecuteAsync("DELETE FROM hufu_biscuit_verifications");
        if (kind=="corrupt-evidence") await fixture.ExecuteAsync("UPDATE hufu_biscuit_verifications SET hash='"+new string('0',64)+"'");
        if (kind=="core-only") {
            Assert.False(verification.IsAuthorized);
            using var connection=await fixture.OpenAsync(); using var sql=connection.CreateCommand();
            sql.CommandText="SELECT command_id FROM hufu_decisions LIMIT 1";
            command=command with { DecisionCommandId=(string)(await sql.ExecuteScalarAsync())! };
        }
        var outcome=await gate.StartAsync(command);
        Assert.NotEqual(AuthorityOperationStartStatus.Started,outcome.Status);
        Assert.NotEqual(AuthorityOperationStartStatus.AlreadyStarted,outcome.Status);
        Assert.Equal(0,participant.Calls); Assert.Equal(0,await fixture.RuntimeCountAsync());
    }

    [Theory]
    [InlineData("runtime-deny")]
    [InlineData("required-evidence")]
    [InlineData("expiry-before-commit")]
    public async Task FailedStartRollsBackRuntimeAcquisition(string kind)
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync(); var request=fixture.Request(AuthorityAction.PatchFile);
        var verification=await fixture.Service.VerifyAsync(root,request,BindingHash);
        Assert.True(verification.IsAuthorized);
        var participant=new RuntimeParticipant();
        if (kind=="runtime-deny") participant.Status=AuthorityStatus.Deny;
        if (kind=="expiry-before-commit") participant.OnStart=()=>fixture.Clock.Current=Now.AddHours(2);
        if (kind=="required-evidence") {
            // Initialize the lazy start namespace, then fail its mandatory insert after acquisition.
            var initialize=Start(fixture,request,verification);
            Assert.Equal(AuthorityOperationStartStatus.Started, (await fixture.StartGate(request,new RuntimeParticipant()).StartAsync(initialize)).Status);
            await fixture.ExecuteAsync("CREATE TRIGGER reject_start BEFORE INSERT ON hufu_operation_starts BEGIN SELECT RAISE(ABORT,'required evidence failed'); END");
        }
        var result=await fixture.StartGate(request,participant).StartAsync(Start(fixture,request,verification));
        Assert.NotEqual(AuthorityOperationStartStatus.Started,result.Status);
        Assert.Equal(1,participant.Calls); Assert.Equal(kind=="required-evidence" ? 1 : 0,await fixture.RuntimeCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAcknowledgementBlocksLaterStartsWhileEarlierCommittedStartMayFinish(bool retire)
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync(); var request=fixture.Request(AuthorityAction.PatchFile);
        var verification=await fixture.Service.VerifyAsync(root,request,BindingHash);
        Assert.True(verification.IsAuthorized);
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        using var authenticated=new ManualResetEventSlim();
        fixture.OnRegistryAuthorization=access => {
            if (access.Operation is BiscuitRegistryOperation.Revoke or BiscuitRegistryOperation.RetireKey) authenticated.Set();
        };
        var participant=new RuntimeParticipant { OnStart=()=> {
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }};
        var gate=fixture.StartGate(request,participant); var command=Start(fixture,request,verification);
        var start=Task.Run(async()=>await gate.StartAsync(command));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var revoke=Task.Run(async()=>retire ?
                await fixture.Registry.RetireKeyAsync(Actor,Realm,"key-1","retired") :
                await fixture.Registry.RevokeAsync(Actor,Realm,Context,Fingerprint(root),"revoked"));
            Assert.True(authenticated.Wait(TimeSpan.FromSeconds(10)));
            release.Set();
            Assert.Equal(AuthorityOperationStartStatus.Started,(await start).Status);
            Assert.Equal(BiscuitRegistryStatus.Recorded,await revoke);
            Assert.Equal(AuthorityOperationStartStatus.StaleRuntime,
                (await gate.StartAsync(command with { OperationId=Guid.NewGuid().ToString("N") })).Status);
            Assert.Equal(AuthorityOperationStartStatus.AlreadyStarted,(await gate.StartAsync(command)).Status);
            Assert.Equal(1,participant.Calls); Assert.Equal(1,await fixture.RuntimeCountAsync());
        }
        finally { release.Set(); await start; }
    }

    [Fact]
    public async Task CurrentAuthorityAdvanceInvalidatesEarlierPermitAtStart()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync(); var request=fixture.Request(AuthorityAction.PatchFile);
        var verification=await fixture.Service.VerifyAsync(root,request,BindingHash);
        var command=Start(fixture,request,verification);
        await fixture.PublishAsync(fixture.CreateSnapshot("v2"));
        var participant=new RuntimeParticipant();
        var result=await fixture.StartGate(request,participant).StartAsync(command);
        Assert.Equal(AuthorityOperationStartStatus.StaleAuthority,result.Status);
        Assert.Equal(0,participant.Calls);
    }
}
