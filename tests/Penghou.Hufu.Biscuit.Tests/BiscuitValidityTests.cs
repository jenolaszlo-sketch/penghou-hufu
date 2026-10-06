using Penghou.Hufu.Biscuit.Sqlite;
using static Penghou.Hufu.Biscuit.Tests.BiscuitIntegrationFixture;

namespace Penghou.Hufu.Biscuit.Tests;

public sealed class BiscuitValidityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherAuthorityLayerExpiryDuringRequiredRecordingCannotReturnPermit(bool afterDurableWrite)
    {
        using var fixture = new BiscuitIntegrationFixture();
        var shortGrant = fixture.Snapshot.Layers[0].Grants[0] with { Id = "short", ExpiresAt = Now.AddSeconds(1) };
        var snapshot = fixture.CreateSnapshot("v1", [fixture.Snapshot.Layers[0], new("activity", [shortGrant])]);
        await fixture.PublishAsync(snapshot);
        var envelope = await fixture.IssueAsync();
        var recorder = new CrossingRecorder(
            new BiscuitSqliteDecisionRecorder(fixture.Store, fixture.Registry),
            () => fixture.Clock.Current = Now.AddSeconds(2), afterDurableWrite);
        var service = fixture.CreateService(recorder);
        var result = await service.VerifyAsync(envelope, fixture.Request());
        Assert.False(result.IsAuthorized);
        Assert.Equal(BiscuitFailureCode.AuthorizationUnavailable, result.FailureCode);
        Assert.Null(result.VerificationId);
    }

    [Fact]
    public void MappingIdentityIncludesProfileAndEveryCapabilityMapping()
    {
        var payload = string.Join("\n", BiscuitProfile.Identity, "registered-typed-grants-v1", BiscuitProfile.Policy,
            "ReadFile=fs.read\nListDirectory=fs.list\nReadMetadata=fs.metadata\nPatchFile=fs.patch\nRelease=data.release\nWriteFile=fs.write\nExecuteProcess=proc.execute");
        Assert.Equal("hufu-biscuit-mapping-v2:" + BiscuitProfile.Hash(BiscuitProfile.Utf8.GetBytes(payload)),
            BiscuitProfile.MappingIdentity);
        Assert.NotEqual("hufu-biscuit-mapping-v1:" + BiscuitProfile.Hash(BiscuitProfile.Utf8.GetBytes(BiscuitProfile.Policy)),
            BiscuitProfile.MappingIdentity);
    }

    private sealed class CrossingRecorder(IBiscuitDecisionRecorder inner, Action cross, bool afterWrite) : IBiscuitDecisionRecorder
    {
        public async ValueTask<bool> RecordAsync(BiscuitDecisionEvidence evidence, CancellationToken ct = default)
        {
            if (!afterWrite) cross();
            var recorded = await inner.RecordAsync(evidence, ct);
            if (afterWrite) cross();
            return recorded;
        }
    }
}
