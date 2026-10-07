using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class AuthorityDerivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Supervisor =
        new("tenant", "supervisor", "run-sup", "rev", "fence");
    private static readonly AuthenticatedAuthorityContext Child =
        new("tenant", "delegation", "run-child", "rev", "fence");
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");

    [Fact]
    public async Task DeriveContainedAuthority_IssuesDistinctChild()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        var parent = await PublishParentAsync(store);

        var result = await store.DeriveAsync(Derive("req-1", "gen-1",
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1))), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Permit, result.Status);
        Assert.NotNull(result.Grant);
        Assert.NotEqual(parent.Id, result.Grant.Id);
        Assert.Equal(parent.Id, result.Grant.ParentGrantId);
        Assert.Equal(parent.Id, result.ParentGrantId);
        Assert.True(AuthorityValidation.Contains(parent.Scope, result.Grant.Scope));
        var lineage = await store.GetLineageAsync(result.Grant.Id, CancellationToken.None);
        Assert.NotNull(lineage);
        Assert.Equal("delegation-1", lineage.DelegationId);
        Assert.Equal("gen-1", lineage.Generation);
        Assert.Equal(result.DerivationIdentity, lineage.DerivationIdentity);
        var current = await store.ReadCurrentAsync(Actor, Child, CancellationToken.None);
        Assert.Equal(AuthorityReadStatus.Active, current.Status);
    }

    [Fact]
    public async Task DeriveOverreachingAuthority_IsDeniedWithoutIssuance()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);

        var result = await store.DeriveAsync(Derive("req-1", "gen-1",
            new RequestedAuthority([AuthorityAction.ReadFile, AuthorityAction.WriteFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1))), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Null(result.Grant);
        Assert.Equal("authority.derivation-not-contained", result.ReasonCode);
        var current = await store.ReadCurrentAsync(Actor, Child, CancellationToken.None);
        Assert.Equal(AuthorityReadStatus.NotFound, current.Status);

        var retry = await store.DeriveAsync(Derive("req-2", "gen-1",
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1))), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Permit, retry.Status);
        Assert.NotNull(retry.Grant);
    }

    [Fact]
    public async Task RepeatSameDerivation_ReturnsSameLogicalGrant()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));

        var first = await store.DeriveAsync(Derive("req-1", "gen-1", requested), CancellationToken.None);
        var second = await store.DeriveAsync(Derive("req-2", "gen-1", requested), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Permit, first.Status);
        Assert.Equal(AuthorityStatus.Permit, second.Status);
        Assert.Equal(first.Grant!.Id, second.Grant!.Id);
        Assert.Equal(first.DerivationIdentity, second.DerivationIdentity);
    }

    [Fact]
    public async Task ConcurrentSameDerivation_ProducesExactlyOneChild()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database.Open(new AllowAllAuthorizer(), time));
        using var barrier = new Barrier(4);
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
        {
            var store = database.Open(new AllowAllAuthorizer(), time);
            barrier.SignalAndWait();
            return await store.DeriveAsync(
                Derive($"req-{index}", "gen-1", requested), CancellationToken.None);
        })));

        Assert.All(results, result => Assert.Equal(AuthorityStatus.Permit, result.Status));
        Assert.Single(results.Select(result => result.Grant!.Id).Distinct());
        Assert.Single(results.Select(result => result.DerivationIdentity).Distinct());
    }

    [Fact]
    public async Task ReplayAfterRecovery_ReturnsExistingIssuance()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var first = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(first);
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        var issued = await first.DeriveAsync(Derive("req-1", "gen-1", requested), CancellationToken.None);

        var reopened = database.Open(new AllowAllAuthorizer(), time);
        var replayed = await reopened.DeriveAsync(Derive("req-2", "gen-1", requested), CancellationToken.None);

        Assert.Equal(issued.Grant!.Id, replayed.Grant!.Id);
        Assert.Equal(issued.DerivationIdentity, replayed.DerivationIdentity);
    }

    [Fact]
    public async Task NewGeneration_ProducesNewChildGrant()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));

        var first = await store.DeriveAsync(Derive("req-1", "gen-1", requested), CancellationToken.None);
        var second = await store.DeriveAsync(Derive("req-2", "gen-2", requested), CancellationToken.None);

        Assert.NotEqual(first.Grant!.Id, second.Grant!.Id);
        Assert.NotEqual(first.DerivationIdentity, second.DerivationIdentity);
        Assert.NotNull(await store.GetLineageAsync(first.Grant.Id, CancellationToken.None));
        Assert.NotNull(await store.GetLineageAsync(second.Grant.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ParentRevocation_MakesChildIneffective()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        var child = await DeriveChildAsync(store, "gen-1");
        var admitted = Admission(store, time);

        var before = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);
        Assert.True(before.IsAuthorized);

        var revoked = await store.RevokeAsync(
            new AuthorityRevokeCommand("revoke-1", Actor, Supervisor, 1, "test.revoked"),
            CancellationToken.None);
        Assert.Equal(AuthorityMutationStatus.Applied, revoked.Status);

        var after = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-2"),
            CancellationToken.None);
        Assert.False(after.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, after.Status);
        Assert.Equal("authority.ancestor-revoked", after.Decision!.ReasonCode);
    }

    [Fact]
    public async Task ParentRevocation_DoesNotRewriteChildHistory()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        var child = await DeriveChildAsync(store, "gen-1");
        await store.RevokeAsync(
            new AuthorityRevokeCommand("revoke-1", Actor, Supervisor, 1, "test.revoked"),
            CancellationToken.None);

        var lineage = await store.GetLineageAsync(child.Grant!.Id, CancellationToken.None);
        Assert.NotNull(lineage);
        Assert.Equal(child.Grant.Id, lineage.ChildGrantId);
        Assert.Equal("delegation-1", lineage.DelegationId);
        var state = await store.ReadCurrentAsync(Actor, Child, CancellationToken.None);
        Assert.Equal(AuthorityReadStatus.Active, state.Status);
    }

    [Fact]
    public async Task ChildRevocation_DoesNotAffectParent()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        var child = await DeriveChildAsync(store, "gen-1");
        var admitted = Admission(store, time);

        var revoked = await store.RevokeAsync(
            new AuthorityRevokeCommand("revoke-child", Actor, Child, 1, "test.revoked"),
            CancellationToken.None);
        Assert.Equal(AuthorityMutationStatus.Applied, revoked.Status);

        var parentUse = await admitted.AuthorizeAsync(
            new AuthorityRequest(Supervisor, AuthorityAction.ReadFile, "workspace", "src/a.cs", "op-parent"),
            CancellationToken.None);
        Assert.True(parentUse.IsAuthorized);

        var childUse = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-child"),
            CancellationToken.None);
        Assert.False(childUse.IsAuthorized);
    }

    [Fact]
    public async Task DerivedMarkerWithoutLineage_FailsClosedAsLineageUnavailable()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        var forged = new AuthoritySnapshot(Child, "forged-v1",
            [new AuthorityLayer("forged", [
                new AuthorityGrant("grant-forged", [AuthorityAction.ReadFile],
                    new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                    Now.AddMinutes(-1), Now.AddDays(1)) { ParentGrantId = "grant-parent" }])],
            [], Now.AddDays(1));
        var published = await store.PublishAsync(
            new AuthorityPublishCommand("publish-forged", Actor, forged, 0),
            CancellationToken.None);
        Assert.Equal(AuthorityMutationStatus.Applied, published.Status);
        var admitted = Admission(store, time);

        var result = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-forged"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("authority.lineage-unavailable", result.Decision!.ReasonCode);
    }

    [Fact]
    public async Task MarangPattern_ReadSucceedsThenAncestorRevocationDenies()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        await DeriveChildAsync(store, "gen-1");
        var admitted = Admission(store, time);

        var allowed = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);
        Assert.True(allowed.IsAuthorized);

        var outside = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/other/b.cs", "op-2"),
            CancellationToken.None);
        Assert.False(outside.IsAuthorized);
        Assert.Equal("test.layer-denied", outside.Decision!.ReasonCode);

        await store.RevokeAsync(
            new AuthorityRevokeCommand("revoke-1", Actor, Supervisor, 1, "test.revoked"),
            CancellationToken.None);

        var denied = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-3"),
            CancellationToken.None);
        Assert.False(denied.IsAuthorized);
        Assert.Equal("authority.ancestor-revoked", denied.Decision!.ReasonCode);
    }

    [Fact]
    public async Task DeriveRacingParentRevocation_NeverYieldsUsableChildOfRevokedParent()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        using var barrier = new Barrier(2);

        var deriveTask = Task.Run(async () =>
        {
            var racing = database.Open(new AllowAllAuthorizer(), time);
            barrier.SignalAndWait();
            return await racing.DeriveAsync(Derive("req-race", "gen-1", requested), CancellationToken.None);
        });
        var revokeTask = Task.Run(async () =>
        {
            var racing = database.Open(new AllowAllAuthorizer(), time);
            barrier.SignalAndWait();
            return await racing.RevokeAsync(
                new AuthorityRevokeCommand("revoke-race", Actor, Supervisor, 1, "test.revoked"),
                CancellationToken.None);
        });
        var derived = await deriveTask;
        var revoked = await revokeTask;

        Assert.Equal(AuthorityMutationStatus.Applied, revoked.Status);
        if (derived.Status == AuthorityStatus.Permit)
        {
            Assert.NotNull(derived.Grant);
            var admitted = Admission(store, time);
            var use = await admitted.AuthorizeAsync(
                new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-race"),
                CancellationToken.None);
            Assert.False(use.IsAuthorized);
            Assert.Equal("authority.ancestor-revoked", use.Decision!.ReasonCode);
        }
        else
        {
            Assert.Equal(AuthorityStatus.Unavailable, derived.Status);
            Assert.Null(derived.Grant);
        }
    }

    [Fact]
    public async Task ReplayAfterParentRevocation_ReturnsSameIneffectiveChild()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store);
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        var issued = await store.DeriveAsync(Derive("req-1", "gen-1", requested), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Permit, issued.Status);
        await store.RevokeAsync(
            new AuthorityRevokeCommand("revoke-1", Actor, Supervisor, 1, "test.revoked"),
            CancellationToken.None);

        var replayed = await store.DeriveAsync(Derive("req-2", "gen-1", requested), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Permit, replayed.Status);
        Assert.Equal(issued.Grant!.Id, replayed.Grant!.Id);
        Assert.Equal(issued.DerivationIdentity, replayed.DerivationIdentity);
        var admitted = Admission(store, time);
        var use = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-replay"),
            CancellationToken.None);
        Assert.False(use.IsAuthorized);
        Assert.Equal("authority.ancestor-revoked", use.Decision!.ReasonCode);
    }

    [Fact]
    public void ChangedDerivationInputs_ProduceDifferentIdentities()
    {
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        var baseline = AuthorityDerivation.DerivationIdentity("grant-parent", "delegation-1", "gen-1", requested);

        Assert.NotEqual(baseline, AuthorityDerivation.DerivationIdentity("grant-other", "delegation-1", "gen-1", requested));
        Assert.NotEqual(baseline, AuthorityDerivation.DerivationIdentity("grant-parent", "delegation-2", "gen-1", requested));
        Assert.NotEqual(baseline, AuthorityDerivation.DerivationIdentity("grant-parent", "delegation-1", "gen-2", requested));
        Assert.NotEqual(baseline, AuthorityDerivation.DerivationIdentity("grant-parent", "delegation-1", "gen-1",
            new RequestedAuthority([AuthorityAction.ReadFile, AuthorityAction.ReadMetadata],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1))));
        Assert.NotEqual(baseline, AuthorityDerivation.DerivationIdentity("grant-parent", "delegation-1", "gen-1",
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1))));
    }

    [Fact]
    public void Containment_RequiresActionsScopeExclusionsAndValidity()
    {
        var parent = new AuthorityGrant("grant-parent",
            [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata],
            new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        var narrowed = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddHours(1));
        Assert.True(AuthorityDerivation.IsContainedBy(narrowed, parent));

        Assert.False(AuthorityDerivation.IsContainedBy(
            new RequestedAuthority([AuthorityAction.WriteFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddHours(1)), parent));
        Assert.False(AuthorityDerivation.IsContainedBy(
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "other", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddHours(1)), parent));

        var restricted = new AuthorityGrant("grant-restricted",
            [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree),
            [new AuthorityScope("workspace", "src/secret", AuthorityScopeKind.Subtree)],
            Now.AddMinutes(-1), Now.AddDays(1));
        Assert.False(AuthorityDerivation.IsContainedBy(
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddHours(1)), restricted));
        Assert.True(AuthorityDerivation.IsContainedBy(
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddHours(1)), restricted));
        Assert.True(AuthorityDerivation.IsContainedBy(
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree),
                [new AuthorityScope("workspace", "src/secret", AuthorityScopeKind.Subtree)],
                Now.AddMinutes(-1), Now.AddHours(1)), restricted));
        Assert.False(AuthorityDerivation.IsContainedBy(
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddDays(1), Now.AddDays(2)), parent));
    }

    private static AuthorityDerivationCommand Derive(string requestIdentity, string generation, RequestedAuthority requested) =>
        new(Actor, Supervisor, "grant-parent", Child, "delegation-1", generation, requested, requestIdentity);

    [Fact]
    public void DerivationRequest_RejectsMalformedInput()
    {
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        Assert.Throws<ArgumentException>(() => new AuthorityDerivationCommand(
            Actor, Supervisor, "  ", Child, "delegation-1", "gen-1", requested, "req-1"));
    }

    private static async Task<AuthorityGrantDerivationResult> DeriveChildAsync(
        SqliteAuthorityStore store, string generation)
    {
        var result = await store.DeriveAsync(Derive("req-init", generation,
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1))), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Permit, result.Status);
        return result;
    }

    private static async Task<AuthorityGrant> PublishParentAsync(SqliteAuthorityStore store)
    {
        var grant = new AuthorityGrant("grant-parent", [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        var snapshot = new AuthoritySnapshot(Supervisor, "v1",
            [new AuthorityLayer("run", [grant])],
            [], Now.AddDays(1));
        var published = await store.PublishAsync(
            new AuthorityPublishCommand("publish-parent", Actor, snapshot, 0),
            CancellationToken.None);
        Assert.Equal(AuthorityMutationStatus.Applied, published.Status);
        return grant;
    }

    private static CurrentAuthorityRequestAuthorizer Admission(SqliteAuthorityStore store, TimeProvider time)
    {
        var evaluator = new CoverEvaluator();
        var source = new AuthorityStoreSnapshotSource(store, Actor);
        return new CurrentAuthorityRequestAuthorizer(
            source,
            evaluator,
            new AllowRecorder(),
            time,
            new AuthorityDerivation.LineageAncestorLiveness(source, store));
    }

    private sealed class AllowAllAuthorizer : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, Actor));
    }

    private sealed class AllowRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(
            AuthorityRequest request,
            AuthorityDecision decision,
            CancellationToken cancellationToken = default) => new(true);
    }

    private sealed class CoverEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(
            AuthoritySnapshot snapshot,
            AuthorityRequest request,
            DateTimeOffset now)
        {
            var covered = snapshot.Layers
                .SelectMany(layer => layer.Grants)
                .Any(grant => grant.Actions.Contains(request.Action) &&
                    grant.NotBefore <= now && now < grant.ExpiresAt &&
                    AuthorityValidation.Contains(grant.Scope,
                        new AuthorityScope(request.WorkspaceId,
                            AuthorityValidation.NormalizePath(request.RelativePath), AuthorityScopeKind.Exact)));
            return new AuthorityDecision(
                covered ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                covered ? "test.permitted" : "test.layer-denied",
                snapshot.Version,
                "test-evaluator-v1",
                snapshot.Identity);
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), "hufu-derivation-tests-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(DirectoryPath, "authority.db");

        public SqliteAuthorityStore Open(IAuthorityStoreAuthorizer authorizer, TimeProvider time)
        {
            Directory.CreateDirectory(DirectoryPath);
            return new SqliteAuthorityStore(DatabasePath, authorizer, time);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
