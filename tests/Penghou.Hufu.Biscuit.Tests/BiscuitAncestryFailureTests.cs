using BiscuitSharp;
using Penghou.Hufu.Biscuit.Sqlite;
using Penghou.Hufu.Sqlite;
using static Penghou.Hufu.Biscuit.Tests.BiscuitIntegrationFixture;

namespace Penghou.Hufu.Biscuit.Tests;

public sealed class BiscuitAncestryFailureTests
{
    [Theory]
    [InlineData(BiscuitRegistryStatus.Unavailable, BiscuitFailureCode.AuthorizationUnavailable)]
    [InlineData(BiscuitRegistryStatus.Unknown, BiscuitFailureCode.InvalidCredential)]
    [InlineData(BiscuitRegistryStatus.Revoked, BiscuitFailureCode.AuthorityRevoked)]
    [InlineData(BiscuitRegistryStatus.Denied, BiscuitFailureCode.AuthorityDenied)]
    public async Task AncestorLookupPreservesTypedFailure(BiscuitRegistryStatus registryFailure, BiscuitFailureCode expected)
    {
        using var fixture = new BiscuitIntegrationFixture();
        await fixture.PublishAsync();
        var root = await fixture.IssueAsync();
        var grant = fixture.Snapshot.Layers[0].Grants[0];
        var child = await fixture.Service.AttenuateAsync(Context, root,
            new([AuthorityAction.ReadFile], grant.Scope, grant.Exclusions, grant.NotBefore, grant.ExpiresAt));
        Assert.True(child.IsSuccess);
        var registry = new FailedAncestor(fixture.Registry, Fingerprint(root), registryFailure);
        var service = new BiscuitAuthorityService(fixture, fixture.Keys, registry,
            new AuthorityStoreSnapshotSource(fixture.Store, Actor), fixture.Cedar,
            new BiscuitSqliteDecisionRecorder(fixture.Store, fixture.Registry),
            new BiscuitAuthorizerLimits(2000, 50, TimeSpan.FromMilliseconds(100)), fixture.Clock);

        var result = await service.VerifyAsync(child.Envelope!, fixture.Request());

        Assert.Equal(1, registry.FailureCalls);
        Assert.Equal(expected, result.FailureCode);
        Assert.False(result.IsAuthorized);
        Assert.Equal(expected == BiscuitFailureCode.AuthorizationUnavailable ? AuthorityStatus.Unavailable : AuthorityStatus.Deny,
            result.Decision!.Status);
    }

    private sealed class FailedAncestor(IBiscuitCredentialRegistry inner, string parent, BiscuitRegistryStatus status) : IBiscuitCredentialRegistry
    {
        internal int FailureCalls { get; private set; }
        public ValueTask<BiscuitRegistrationLookup> FindAsync(AuthorityStoreActor actor, string realm,
            AuthenticatedAuthorityContext context, string fingerprint, CancellationToken ct = default)
        {
            if (fingerprint != parent) return inner.FindAsync(actor, realm, context, fingerprint, ct);
            FailureCalls++;
            return ValueTask.FromResult(new BiscuitRegistrationLookup(status));
        }
        public ValueTask<BiscuitRegistryStatus> RegisterAsync(AuthorityStoreActor actor,
            BiscuitCredentialRegistration registration, CancellationToken ct = default) => inner.RegisterAsync(actor, registration, ct);
        public ValueTask<BiscuitRegistryStatus> CheckAsync(AuthorityStoreActor actor,
            BiscuitCredentialRegistration registration, DateTimeOffset now, CancellationToken ct = default) =>
            inner.CheckAsync(actor, registration, now, ct);
    }
}
