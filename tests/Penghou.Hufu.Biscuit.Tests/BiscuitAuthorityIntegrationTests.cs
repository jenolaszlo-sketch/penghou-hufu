using BiscuitSharp;
using Penghou.Hufu.Biscuit.Sqlite;
using Penghou.Hufu.Sqlite;
using static Penghou.Hufu.Biscuit.Tests.BiscuitIntegrationFixture;

namespace Penghou.Hufu.Biscuit.Tests;

public sealed class BiscuitAuthorityIntegrationTests
{
    [Theory]
    [InlineData(AuthorityAction.ReadFile)]
    [InlineData(AuthorityAction.ListDirectory)]
    [InlineData(AuthorityAction.ReadMetadata)]
    [InlineData(AuthorityAction.PatchFile)]
    [InlineData(AuthorityAction.Release)]
    [InlineData(AuthorityAction.WriteFile)]
    public async Task RegisteredCredentialRequiresRealCedarBiscuitAndBothDurableEvidenceStores(AuthorityAction action)
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var envelope=await fixture.IssueAsync(); var request=fixture.Request(action);
        var result=await fixture.Service.VerifyAsync(envelope,request);
        Assert.True(result.IsAuthorized,result.FailureCode.ToString());
        Assert.Equal(AuthorityStatus.Permit,result.Decision!.Status);
        var evidence=await fixture.Store.ReadDecisionAsync(Actor,AuthoritySubject.From(Context),result.VerificationId!);
        Assert.Equal(AuthorityReadStatus.Active,evidence.Status);
        Assert.Contains(Fingerprint(envelope),evidence.Entry!.Record.EvidenceJson);
        Assert.Contains("cleanAllow",evidence.Entry.Record.EvidenceJson);
        Assert.DoesNotContain(Convert.ToBase64String(envelope.GetTokenBytes()),evidence.Entry.Record.EvidenceJson);
    }

    [Theory]
    [InlineData("realm")]
    [InlineData("workflow")]
    [InlineData("activity")]
    [InlineData("audience")]
    public async Task HostBindingSubstitutionsCannotReuseRegisteredCredential(string field)
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var envelope=await fixture.IssueAsync();
        fixture.Binding=field switch {
            "realm"=>fixture.Binding with { Realm="another-realm" },
            "workflow"=>fixture.Binding with { WorkflowId="another-workflow" },
            "activity"=>fixture.Binding with { ActivityId="another-activity" },
            _=>fixture.Binding with { Audience="another-audience" }};
        Assert.False((await fixture.Service.VerifyAsync(envelope,fixture.Request())).IsAuthorized);
    }

    [Fact]
    public async Task AuthenticationAndResourceBindingAreRequired()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var envelope=await fixture.IssueAsync(); var request=fixture.Request();
        fixture.Authenticate=false;
        Assert.Equal(BiscuitFailureCode.WorkflowAuthorityDenied,(await fixture.Service.VerifyAsync(envelope,request)).FailureCode);
        fixture.Authenticate=true; fixture.BindResource=false;
        var result=await fixture.Service.VerifyAsync(envelope,request);
        Assert.Equal(BiscuitFailureCode.EnforcementPreconditionFailed,result.FailureCode);
        Assert.False(result.IsAuthorized); Assert.NotNull(result.VerificationId);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("offline")]
    [InlineData("alias")]
    [InlineData("unknown-key")]
    public async Task RejectsCredentialTamperingOfflineDerivationAndNoncanonicalBytes(string kind)
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var envelope=await fixture.IssueAsync();
        var bytes=envelope.GetTokenBytes(); var key="key-1";
        if (kind=="forged") bytes[bytes.Length/2]^=1;
        if (kind=="offline") {
            var publicKey=await fixture.Keys.FindVerificationKeyAsync(Realm,key);
            bytes=BiscuitToken.Parse(bytes,publicKey!).Attenuate(BiscuitBlock.Create("check if true;")).ToBytes();
        }
        if (kind=="alias") bytes=[..bytes,0x80,0x00];
        if (kind=="unknown-key") key="another-key";
        var result=await fixture.Service.VerifyAsync(new(key,bytes),fixture.Request());
        Assert.False(result.IsAuthorized); Assert.Null(result.VerificationId);
    }

    [Fact]
    public async Task RegisteredAttenuationNarrowsActionsScopeAndPreservesAncestorRevocation()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync();
        var child=await fixture.Service.AttenuateAsync(Context,root,new([AuthorityAction.ReadFile],
            new("workspace","src/narrow",AuthorityScopeKind.Subtree),[],Now,Now.AddMinutes(30)));
        Assert.True(child.IsSuccess,child.FailureCode.ToString());
        Assert.True((await fixture.Service.VerifyAsync(child.Envelope!,fixture.Request(path:"src/narrow/a.txt"))).IsAuthorized);
        Assert.Equal(BiscuitFailureCode.AuthorityConstraintFailed,
            (await fixture.Service.VerifyAsync(child.Envelope!,fixture.Request(AuthorityAction.PatchFile,"src/narrow/a.txt"))).FailureCode);
        Assert.Equal(BiscuitFailureCode.AuthorityConstraintFailed,
            (await fixture.Service.VerifyAsync(child.Envelope!,fixture.Request(path:"src/elsewhere.txt"))).FailureCode);
        Assert.Equal(BiscuitRegistryStatus.Recorded,await fixture.Registry.RevokeAsync(Actor,Realm,Context,Fingerprint(root),"revoked"));
        Assert.Equal(BiscuitFailureCode.AuthorityRevoked,
            (await fixture.Service.VerifyAsync(child.Envelope!,fixture.Request(path:"src/narrow/a.txt"))).FailureCode);
    }

    [Fact]
    public async Task ChildCannotDropIntersectingExclusionOrExtendLifetime()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync();
        foreach (var restriction in new[] {
            new BiscuitRestriction([AuthorityAction.ReadFile],new("workspace","src",AuthorityScopeKind.Subtree),[],Now,Now.AddMinutes(30)),
            new BiscuitRestriction([AuthorityAction.ReadFile],new("workspace","src/narrow",AuthorityScopeKind.Subtree),[],Now,Now.AddHours(2)) })
            Assert.Equal(BiscuitFailureCode.AuthorityConstraintFailed,
                (await fixture.Service.AttenuateAsync(Context,root,restriction)).FailureCode);
    }

    [Fact]
    public async Task CurrentMandatoryDenialsAndEveryLayerRemainMandatory()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync();
        await fixture.PublishAsync(fixture.CreateSnapshot("v2",denials:[new("workspace","src/file.txt",AuthorityScopeKind.Exact)]));
        var denied=await fixture.Service.VerifyAsync(root,fixture.Request());
        Assert.Equal(BiscuitFailureCode.PolicyDenied,denied.FailureCode);
        Assert.Equal(BiscuitComponentStatus.Deny,denied.CurrentAuthority);
        await fixture.PublishAsync(fixture.CreateSnapshot("v3",layers:[fixture.Snapshot.Layers[0],new("extra",[])]));
        Assert.Equal(BiscuitFailureCode.PolicyDenied,(await fixture.Service.VerifyAsync(root,fixture.Request())).FailureCode);
    }

    [Fact]
    public async Task ChangedGrantCannotKeepOldImmutableVersionMeaning()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync();
        var changed=fixture.Snapshot.Layers[0].Grants[0] with { Actions=[AuthorityAction.ReadFile] };
        await fixture.PublishAsync(fixture.CreateSnapshot("v2",layers:[new("workflow",[changed])]));
        Assert.Equal(BiscuitFailureCode.WorkflowAuthorityDenied,(await fixture.Service.VerifyAsync(root,fixture.Request())).FailureCode);
        Assert.False((await fixture.Service.IssueAsync(new(Context,"workflow","grant"))).IsSuccess);
        fixture.GrantVersion="grant-v2"; Assert.True((await fixture.Service.IssueAsync(new(Context,"workflow","grant"))).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingEitherRequiredEvidenceWriteCannotReturnPermit(bool core)
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync();
        fixture.FailCoreEvidence=core; fixture.FailRegistryEvidence=!core;
        var result=await fixture.Service.VerifyAsync(root,fixture.Request());
        Assert.False(result.IsAuthorized); Assert.Null(result.VerificationId);
        Assert.Equal(BiscuitFailureCode.AuthorizationUnavailable,result.FailureCode);
        Assert.Equal(AuthorityStatus.Unavailable,result.Decision!.Status);
        Assert.Equal(BiscuitFailureCode.AuthorizationUnavailable.ToString(),result.Decision.ReasonCode);
    }

    [Fact]
    public async Task FixedNativeFactBudgetProducesTypedBudgetFailureWithoutRetry()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync();
        var key=await fixture.Keys.FindVerificationKeyAsync(Realm,"key-1");
        var token=BiscuitToken.Parse(root.GetTokenBytes(),key!);
        var native=BiscuitAuthorizer.For(token).AddRule("derived($x) <- hufu_capability($x);")
            .AddPolicy("allow if true;").WithLimits(new(1,50,TimeSpan.FromMilliseconds(100))).Authorize();
        Assert.Equal(BiscuitFailureCode.AuthorizationBudgetExceeded,BiscuitProfile.Classify(native));
        Assert.Throws<ArgumentException>(()=>fixture.CreateService(limits:new(0,50,TimeSpan.FromMilliseconds(100))));
        Assert.Throws<ArgumentException>(()=>fixture.CreateService(limits:new(100,50,TimeSpan.FromTicks(1))));
    }

    [Fact]
    public async Task UnavailableRegistryCapacityAndPostBindingCancellationFailClosed()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        fixture.SetRegistryOptions(new() { MaxEntries=1 });
        Assert.False((await fixture.Service.IssueAsync(new(Context,"workflow","grant"))).IsSuccess);
        fixture.SetRegistryOptions(new());
        var root=await fixture.IssueAsync(); fixture.RegistryStatus=AuthorityStatus.Unavailable;
        Assert.Equal(BiscuitFailureCode.AuthorizationUnavailable,(await fixture.Service.VerifyAsync(root,fixture.Request())).FailureCode);
        fixture.RegistryStatus=AuthorityStatus.Permit;
        using var cancel=new CancellationTokenSource(); fixture.CancellationSource=cancel; fixture.CancelAfterBinding=true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await fixture.Service.VerifyAsync(root,fixture.Request(),ct:cancel.Token));
    }

    [Fact]
    public async Task KeyRotationPreservesOldVerificationUntilOrderedRetirement()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync();
        fixture.Keys.AddSigningKey(Realm,"key-2",BiscuitPrivateKey.Generate());
        var second=await fixture.IssueAsync();
        Assert.Equal("key-2",second.RootKeyId);
        Assert.True((await fixture.Service.VerifyAsync(root,fixture.Request())).IsAuthorized);
        Assert.Equal(BiscuitRegistryStatus.Recorded,await fixture.Registry.RetireKeyAsync(Actor,Realm,"key-1","retired"));
        Assert.Equal(BiscuitFailureCode.AuthorityRevoked,(await fixture.Service.VerifyAsync(root,fixture.Request())).FailureCode);
        Assert.True((await fixture.Service.VerifyAsync(second,fixture.Request())).IsAuthorized);
    }

    [Fact]
    public async Task CorruptedRegistrationIsUnavailableRatherThanUnknownOrPermitted()
    {
        using var fixture=new BiscuitIntegrationFixture(); await fixture.PublishAsync();
        var root=await fixture.IssueAsync();
        await fixture.ExecuteAsync("UPDATE hufu_biscuit_credentials SET hash='"+new string('0',64)+"'");
        Assert.Equal(BiscuitFailureCode.AuthorizationUnavailable,(await fixture.Service.VerifyAsync(root,fixture.Request())).FailureCode);
    }
}
