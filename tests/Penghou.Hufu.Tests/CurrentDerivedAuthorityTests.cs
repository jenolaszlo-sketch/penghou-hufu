using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>
/// AE-1.1 focused proof: a derived child stays inside its fixed issued
/// ceiling and additionally obeys the current restrictions of the single
/// bound root parent. Parent expansion never widens the child; a policy-only
/// parent narrowing denies an unchanged live child; incomplete compositions
/// and unsupported lineage shapes fail closed; root-only use is unchanged.
/// </summary>
public sealed class CurrentDerivedAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Supervisor =
        new("tenant", "supervisor", "run-sup", "rev", "fence");
    private static readonly AuthenticatedAuthorityContext Child =
        new("tenant", "delegation", "run-child", "rev", "fence");
    private static readonly AuthenticatedAuthorityContext OtherTenantParent =
        new("other", "supervisor", "run-sup", "rev", "fence");
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");

    // A. Primary proof: policy-only parent narrowing denies an unchanged live child.

    [Fact]
    public async Task PolicyOnlyNarrowing_DeniesUnchangedLiveChild()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        var issued = await DeriveChildAsync(store, "gen-1", "src/service");
        var recorder = new CountingRecorder();
        var admitted = Qualified(store, time, new CoverEvaluator(), recorder);

        var before = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);
        Assert.True(before.IsAuthorized);

        // Policy-only change: same grant identity, same live validity window,
        // same parent context; only the covered scope narrows. The parent
        // stays live and the child stays byte-identical.
        await PublishParentAsync(store, "v2", "src/other", expectedSequence: 1);

        var parent = await store.ReadCurrentAsync(Actor, Supervisor, CancellationToken.None);
        Assert.Equal(AuthorityReadStatus.Active, parent.Status);
        Assert.Equal("v2", parent.Record!.Snapshot!.Version);
        var liveChild = await store.ReadCurrentAsync(Actor, Child, CancellationToken.None);
        Assert.Equal(AuthorityReadStatus.Active, liveChild.Status);
        Assert.Equal(issued.Grant!.Id, SingleDerivedGrantId(liveChild.Record!.Snapshot!));

        var after = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-2"),
            CancellationToken.None);
        Assert.False(after.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, after.Status);
        Assert.Equal("authority.parent-policy-denied", after.Decision!.ReasonCode);
    }

    // H. The same narrowing proved through the real Cedar policy layer.

    [Fact]
    public async Task PolicyOnlyNarrowing_ProvedThroughRealCedarPolicy()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        await DeriveChildAsync(store, "gen-1", "src/service");
        var recorder = new CountingRecorder();
        var admitted = Qualified(store, time, new CedarAuthorityEvaluator(), recorder);

        var before = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);
        Assert.True(before.IsAuthorized);

        await PublishParentAsync(store, "v2", "src/other", expectedSequence: 1);

        var after = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-2"),
            CancellationToken.None);
        Assert.False(after.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, after.Status);
        Assert.Equal("authority.parent-policy-denied", after.Decision!.ReasonCode);

        // The captured parent evaluation used the stored parent context (not
        // the child context) and the current v2 parent snapshot.
        var parentEvaluation = Assert.Single(recorder.Calls, call =>
            call.Request.Context == Supervisor && call.Decision.Status == AuthorityStatus.Deny);
        var parentSnapshot = (await store.ReadCurrentAsync(Actor, Supervisor, CancellationToken.None)).Record!.Snapshot!;
        Assert.Equal("v2", parentEvaluation.Decision.SnapshotVersion);
        Assert.Equal(parentSnapshot.Identity, parentEvaluation.Decision.SnapshotIdentity);
        Assert.StartsWith("hufu-cedar-v1:", parentEvaluation.Decision.EvaluatorIdentity, StringComparison.Ordinal);
        Assert.Equal(new AuthorityRequest(Supervisor, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-2"),
            parentEvaluation.Request);
    }

    // B. Parent expansion never widens the existing child.

    [Fact]
    public async Task ParentExpansion_DoesNotWidenChild()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        await DeriveChildAsync(store, "gen-1", "src/service");
        var admitted = Qualified(store, time, new CoverEvaluator(), new CountingRecorder());

        await PublishParentAsync(store, "v2", "", expectedSequence: 1);

        var inside = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-inside"),
            CancellationToken.None);
        Assert.True(inside.IsAuthorized);

        // The parent now covers src/other, but the child's fixed ceiling does
        // not: the child evaluator denies before any parent check could help.
        var outside = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/other/b.cs", "op-outside"),
            CancellationToken.None);
        Assert.False(outside.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, outside.Status);
        Assert.Equal("test.layer-denied", outside.Decision!.ReasonCode);
    }

    // C. A known derived grant through an incomplete composition must not Permit.

    [Fact]
    public async Task DerivedGrantWithoutQualifiedChecker_DoesNotPermit()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        await DeriveChildAsync(store, "gen-1", "src/service");
        var source = new AuthorityStoreSnapshotSource(store, Actor);
        var evaluator = new CoverEvaluator();
        var incomplete = new CurrentAuthorityRequestAuthorizer(source, evaluator, new CountingRecorder(), time,
            new AuthorityDerivation.LineageAncestorLiveness(source, store));

        var derived = await incomplete.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);
        Assert.False(derived.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, derived.Status);
        Assert.Equal("authority.derived-checker-missing", derived.Decision!.ReasonCode);

        // Root-only use through the same incomplete composition is unchanged.
        var root = await incomplete.AuthorizeAsync(
            new AuthorityRequest(Supervisor, AuthorityAction.ReadFile, "workspace", "src/a.cs", "op-root"),
            CancellationToken.None);
        Assert.True(root.IsAuthorized);
    }

    // Evidence: Permit binds child + parent evaluations; failed mandatory
    // recording yields no usable Permit.

    [Fact]
    public async Task QualifiedPermit_RecordsChildAndParentEvaluations()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        var issued = await DeriveChildAsync(store, "gen-1", "src/service");
        var recorder = new CountingRecorder();
        var admitted = Qualified(store, time, new CoverEvaluator(), recorder);

        var result = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal(2, recorder.Calls.Count);
        var parentCall = recorder.Calls[0];
        var childCall = recorder.Calls[1];
        var parentSnapshot = (await store.ReadCurrentAsync(Actor, Supervisor, CancellationToken.None)).Record!.Snapshot!;
        var childSnapshot = (await store.ReadCurrentAsync(Actor, Child, CancellationToken.None)).Record!.Snapshot!;
        Assert.Equal(Supervisor, parentCall.Request.Context);
        Assert.Equal(AuthorityStatus.Permit, parentCall.Decision.Status);
        Assert.Equal("v1", parentCall.Decision.SnapshotVersion);
        Assert.Equal(parentSnapshot.Identity, parentCall.Decision.SnapshotIdentity);
        Assert.Equal("test-evaluator-v1", parentCall.Decision.EvaluatorIdentity);
        Assert.Equal(Child, childCall.Request.Context);
        Assert.Equal(AuthorityStatus.Permit, childCall.Decision.Status);
        Assert.Equal(childSnapshot.Identity, childCall.Decision.SnapshotIdentity);
        Assert.Equal(issued.Grant!.Id, SingleDerivedGrantId(childSnapshot));
    }

    [Fact]
    public async Task FailedParentEvidenceRecording_YieldsNoUsablePermit()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        await DeriveChildAsync(store, "gen-1", "src/service");
        var admitted = Qualified(store, time, new CoverEvaluator(), new CountingRecorder(result: false));

        var result = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
    }

    // D. Missing/unavailable lineage fails closed under the qualified composition.

    [Fact]
    public async Task RevokedParent_DeniesUnderQualifiedComposition()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        await DeriveChildAsync(store, "gen-1", "src/service");
        var admitted = Qualified(store, time, new CoverEvaluator(), new CountingRecorder());

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
    public async Task UnavailableLineageStore_YieldsUnavailableNotPermit()
    {
        var childSnapshot = ChildSnapshot("grant-child", "grant-parent");
        var source = new StubSource { Resolve = _ => childSnapshot };
        var derivations = new StubDerivations { Throw = true };
        var evaluator = new StubEvaluator((snapshot, _) => PermitFor(snapshot));
        var recorder = new CountingRecorder();
        var authorizer = new CurrentAuthorityRequestAuthorizer(source, evaluator, recorder, new MutableTimeProvider(Now),
            ancestorLiveness: null,
            parentPolicy: new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task MismatchedLineage_FailsClosedAsMalformed()
    {
        var childSnapshot = ChildSnapshot("grant-child", "grant-parent");
        var requested = TestRequestedAuthority();
        var source = new StubSource { Resolve = _ => childSnapshot };
        // Stored lineage names a different parent than the evaluated grant.
        var derivations = new StubDerivations
        {
            Resolve = _ => new AuthorityGrantLineage("deriv-1", "grant-child", Child,
                "grant-other", Supervisor, "delegation-1", "gen-1", requested, Actor, Now)
        };
        var evaluator = new StubEvaluator((snapshot, _) => PermitFor(snapshot));
        var authorizer = new CurrentAuthorityRequestAuthorizer(source, evaluator, new CountingRecorder(),
            new MutableTimeProvider(Now), ancestorLiveness: null,
            parentPolicy: new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("authority.lineage-malformed", result.Decision!.ReasonCode);
    }

    [Fact]
    public async Task SelfParentLineage_FailsClosedAsMalformed()
    {
        var grant = new AuthorityGrant("grant-child", [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1))
        {
            ParentGrantId = "grant-child"
        };
        var childSnapshot = new AuthoritySnapshot(Child, "child-v1", [new AuthorityLayer("derived", [grant])], [], Now.AddDays(1));
        var source = new StubSource { Resolve = _ => childSnapshot };
        var derivations = new StubDerivations
        {
            Resolve = _ => new AuthorityGrantLineage("deriv-1", "grant-child", Child,
                "grant-child", Supervisor, "delegation-1", "gen-1", TestRequestedAuthority(), Actor, Now)
        };
        var evaluator = new StubEvaluator((snapshot, _) => PermitFor(snapshot));
        var authorizer = new CurrentAuthorityRequestAuthorizer(source, evaluator, new CountingRecorder(),
            new MutableTimeProvider(Now), ancestorLiveness: null,
            parentPolicy: new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("authority.lineage-malformed", result.Decision!.ReasonCode);
    }

    [Fact]
    public async Task CrossTenantLineage_IsUnsupportedWithoutParentLookup()
    {
        var childSnapshot = ChildSnapshot("grant-child", "grant-parent");
        var source = new StubSource { Resolve = _ => childSnapshot };
        var derivations = new StubDerivations
        {
            Resolve = _ => new AuthorityGrantLineage("deriv-1", "grant-child", Child,
                "grant-parent", OtherTenantParent, "delegation-1", "gen-1", TestRequestedAuthority(), Actor, Now)
        };
        var evaluator = new StubEvaluator((snapshot, _) => PermitFor(snapshot));
        var authorizer = new CurrentAuthorityRequestAuthorizer(source, evaluator, new CountingRecorder(),
            new MutableTimeProvider(Now), ancestorLiveness: null,
            parentPolicy: new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("authority.lineage-unsupported", result.Decision!.ReasonCode);
        // Only the child snapshot was resolved; no parent state was consulted.
        Assert.Equal(new[] { Child }, source.Calls);
    }

    [Fact]
    public async Task GrandchildLineage_IsUnsupported()
    {
        var childSnapshot = ChildSnapshot("grant-child", "grant-mid");
        var midGrant = new AuthorityGrant("grant-mid", [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1))
        {
            ParentGrantId = "grant-root"
        };
        var midSnapshot = new AuthoritySnapshot(Supervisor, "parent-v1", [new AuthorityLayer("run", [midGrant])], [], Now.AddDays(1));
        var source = new StubSource
        {
            Resolve = context => context == Supervisor ? midSnapshot : childSnapshot
        };
        var derivations = new StubDerivations
        {
            Resolve = _ => new AuthorityGrantLineage("deriv-1", "grant-child", Child,
                "grant-mid", Supervisor, "delegation-1", "gen-1", TestRequestedAuthority(), Actor, Now)
        };
        var evaluator = new StubEvaluator((snapshot, _) => PermitFor(snapshot));
        var authorizer = new CurrentAuthorityRequestAuthorizer(source, evaluator, new CountingRecorder(),
            new MutableTimeProvider(Now), ancestorLiveness: null,
            parentPolicy: new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("authority.lineage-unsupported", result.Decision!.ReasonCode);
    }

    [Fact]
    public async Task MultipleDistinctParents_AreUnsupported()
    {
        var first = new AuthorityGrant("grant-one", [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/one", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1))
        {
            ParentGrantId = "grant-parent"
        };
        var second = new AuthorityGrant("grant-two", [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/two", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1))
        {
            ParentGrantId = "grant-parent-2"
        };
        var childSnapshot = new AuthoritySnapshot(Child, "child-v1", [new AuthorityLayer("derived", [first, second])], [], Now.AddDays(1));
        var source = new StubSource { Resolve = _ => childSnapshot };
        var requested = TestRequestedAuthority();
        var derivations = new StubDerivations
        {
            Resolve = id => new AuthorityGrantLineage("deriv-" + id, id, Child,
                id == "grant-one" ? "grant-parent" : "grant-parent-2", Supervisor,
                "delegation-1", "gen-1", requested, Actor, Now)
        };
        var evaluator = new StubEvaluator((snapshot, _) => PermitFor(snapshot));
        var authorizer = new CurrentAuthorityRequestAuthorizer(source, evaluator, new CountingRecorder(),
            new MutableTimeProvider(Now), ancestorLiveness: null,
            parentPolicy: new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/one/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("authority.lineage-unsupported", result.Decision!.ReasonCode);
        Assert.Equal(new[] { Child }, source.Calls);
    }

    [Fact]
    public async Task MissingLineage_FailsClosedAsLineageUnavailable()
    {
        var childSnapshot = ChildSnapshot("grant-child", "grant-parent");
        var source = new StubSource { Resolve = _ => childSnapshot };
        var derivations = new StubDerivations { Resolve = _ => null };
        var evaluator = new StubEvaluator((snapshot, _) => PermitFor(snapshot));
        var authorizer = new CurrentAuthorityRequestAuthorizer(source, evaluator, new CountingRecorder(),
            new MutableTimeProvider(Now), ancestorLiveness: null,
            parentPolicy: new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("authority.lineage-unavailable", result.Decision!.ReasonCode);
    }

    [Fact]
    public async Task UnavailableParentEvaluation_YieldsUnavailable()
    {
        var childSnapshot = ChildSnapshot("grant-child", "grant-parent");
        var parentSnapshot = new AuthoritySnapshot(Supervisor, "parent-v1",
            [new AuthorityLayer("run", [
                new AuthorityGrant("grant-parent", [AuthorityAction.ReadFile],
                    new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
                    Now.AddMinutes(-1), Now.AddDays(1))])],
            [], Now.AddDays(1));
        var source = new StubSource
        {
            Resolve = context => context == Supervisor ? parentSnapshot : childSnapshot
        };
        var derivations = new StubDerivations
        {
            Resolve = _ => new AuthorityGrantLineage("deriv-1", "grant-child", Child,
                "grant-parent", Supervisor, "delegation-1", "gen-1", TestRequestedAuthority(), Actor, Now)
        };
        // The child evaluator permits, but current parent evaluation is
        // unavailable: no Permit may result, and the failure stays typed.
        var evaluator = new StubEvaluator((snapshot, _) => snapshot.Context == Supervisor
            ? new AuthorityDecision(AuthorityStatus.Unavailable, "test.parent-unavailable",
                snapshot.Version, "test-evaluator-v1", snapshot.Identity)
            : PermitFor(snapshot));
        var recorder = new CountingRecorder();
        var authorizer = new CurrentAuthorityRequestAuthorizer(source, evaluator, recorder,
            new MutableTimeProvider(Now), ancestorLiveness: null,
            parentPolicy: new AuthorityDerivation.LineageCurrentParentPolicy(source, derivations, evaluator));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
        Assert.Empty(recorder.Calls);
    }

    // E. Derive approval withdrawal blocks new issuance, not existing live-child use.

    [Fact]
    public async Task DeriveApprovalWithdrawal_BlocksIssuanceButNotLiveChildUse()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database.Open(new AllowAllPolicy(), time), "v1", "src", expectedSequence: 0);
        var trust = new TestTrustSource(Principal(Approval("gen-1")));
        var store = database.Open(new BoundedAuthorityIssuanceAuthorizer(trust, new AllowAllPolicy(), time), time);
        var recorder = new CountingRecorder();
        var admitted = Qualified(store, time, new CoverEvaluator(), recorder);

        var issued = await store.DeriveAsync(Command("gen-1", "req-1"), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Permit, issued.Status);

        var before = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);
        Assert.True(before.IsAuthorized);

        trust.Fallback = new AuthorityIssuancePrincipal(Actor);
        var later = await store.DeriveAsync(Command("gen-2", "req-2"), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Deny, later.Status);
        Assert.Null(later.Grant);

        // The already-issued child still obeys its own and current parent
        // resource policy: both still permit, so use still succeeds.
        var after = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-3"),
            CancellationToken.None);
        Assert.True(after.IsAuthorized);
    }

    // F. Same-generation retry reuses the child identity; each use is freshly
    // authorized, so a new parent denial applies to the retry.

    [Fact]
    public async Task SameGenerationRetry_ReusesChildButFreshDenialApplies()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        var requested = new RequestedAuthority([AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        var first = await store.DeriveAsync(Derive("req-1", "gen-1", requested), CancellationToken.None);
        var retry = await store.DeriveAsync(Derive("req-2", "gen-1", requested), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Permit, first.Status);
        Assert.Equal(first.Grant!.Id, retry.Grant!.Id);
        Assert.Equal(first.DerivationIdentity, retry.DerivationIdentity);
        var admitted = Qualified(store, time, new CoverEvaluator(), new CountingRecorder());

        var use = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-1"),
            CancellationToken.None);
        Assert.True(use.IsAuthorized);

        await PublishParentAsync(store, "v2", "src/other", expectedSequence: 1);
        var replayed = await store.DeriveAsync(Derive("req-3", "gen-1", requested), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Permit, replayed.Status);
        Assert.Equal(first.Grant.Id, replayed.Grant!.Id);

        var retryUse = await admitted.AuthorizeAsync(
            new AuthorityRequest(Child, AuthorityAction.ReadFile, "workspace", "src/service/a.cs", "op-retry"),
            CancellationToken.None);
        Assert.False(retryUse.IsAuthorized);
        Assert.Equal("authority.parent-policy-denied", retryUse.Decision!.ReasonCode);
    }

    // G. Root-only behavior is unchanged under both compositions.

    [Fact]
    public async Task RootOnlyUse_UnchangedUnderBothCompositions()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        var store = database.Open(new AllowAllAuthorizer(), time);
        await PublishParentAsync(store, "v1", "src", expectedSequence: 0);
        var source = new AuthorityStoreSnapshotSource(store, Actor);
        var evaluator = new CoverEvaluator();
        var request = new AuthorityRequest(Supervisor, AuthorityAction.ReadFile, "workspace", "src/a.cs", "op-root");

        var incomplete = new CurrentAuthorityRequestAuthorizer(source, evaluator, new CountingRecorder(), time);
        var incompleteResult = await incomplete.AuthorizeAsync(request, CancellationToken.None);
        Assert.True(incompleteResult.IsAuthorized);

        var recorder = new CountingRecorder();
        var qualified = DerivedAuthorityRequestAuthorizer.Create(source, evaluator, recorder, store, time);
        var qualifiedResult = await qualified.AuthorizeAsync(request, CancellationToken.None);
        Assert.True(qualifiedResult.IsAuthorized);
        // No derived grants: no parent lookup and a single child evidence record.
        Assert.Single(recorder.Calls);
    }

    private static CurrentAuthorityRequestAuthorizer Qualified(
        SqliteAuthorityStore store, TimeProvider time, IAuthorityEvaluator evaluator, CountingRecorder recorder) =>
        DerivedAuthorityRequestAuthorizer.Create(new AuthorityStoreSnapshotSource(store, Actor), evaluator, recorder, store, time);

    private static async Task PublishParentAsync(SqliteAuthorityStore store, string version, string scopePath, long expectedSequence)
    {
        var grant = new AuthorityGrant("grant-parent", [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", scopePath, AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1));
        var snapshot = new AuthoritySnapshot(Supervisor, version,
            [new AuthorityLayer("run", [grant])], [], Now.AddDays(1));
        var published = await store.PublishAsync(
            new AuthorityPublishCommand("publish-parent-" + version, Actor, snapshot, expectedSequence),
            CancellationToken.None);
        Assert.Equal(AuthorityMutationStatus.Applied, published.Status);
    }

    private static AuthorityDerivationCommand Derive(string requestIdentity, string generation, RequestedAuthority requested) =>
        new(Actor, Supervisor, "grant-parent", Child, "delegation-1", generation, requested, requestIdentity);

    private static async Task<AuthorityGrantDerivationResult> DeriveChildAsync(
        SqliteAuthorityStore store, string generation, string scopePath)
    {
        var result = await store.DeriveAsync(Derive("req-init-" + generation, generation,
            new RequestedAuthority([AuthorityAction.ReadFile],
                new AuthorityScope("workspace", scopePath, AuthorityScopeKind.Subtree), [],
                Now.AddMinutes(-1), Now.AddDays(1))), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Permit, result.Status);
        Assert.NotNull(result.Grant);
        return result;
    }

    private static string SingleDerivedGrantId(AuthoritySnapshot snapshot)
    {
        var grants = snapshot.Layers.SelectMany(layer => layer.Grants).ToArray();
        var derived = Assert.Single(grants);
        Assert.NotNull(derived.ParentGrantId);
        return derived.Id;
    }

    private static AuthoritySnapshot ChildSnapshot(string grantId, string parentGrantId)
    {
        var grant = new AuthorityGrant(grantId, [AuthorityAction.ReadFile],
            new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
            Now.AddMinutes(-1), Now.AddDays(1))
        {
            ParentGrantId = parentGrantId
        };
        return new AuthoritySnapshot(Child, "child-v1", [new AuthorityLayer("derived", [grant])], [], Now.AddDays(1));
    }

    private static RequestedAuthority TestRequestedAuthority() => new([AuthorityAction.ReadFile],
        new AuthorityScope("workspace", "src/service", AuthorityScopeKind.Subtree), [],
        Now.AddMinutes(-1), Now.AddDays(1));

    private static AuthorityDecision PermitFor(AuthoritySnapshot snapshot) =>
        new(AuthorityStatus.Permit, "test.permitted", snapshot.Version, "test-evaluator-v1", snapshot.Identity);

    private static AuthorityDerivationCommand Command(string generation, string requestIdentity) =>
        new(Actor, Supervisor, "grant-parent", Child, "delegation-1", generation, TestRequestedAuthority(), requestIdentity);

    private static DerivedAuthorityApproval Approval(string generation) =>
        new("grant-parent", "delegation-1", generation,
            AuthorityDerivation.RequestedAuthorityHash(TestRequestedAuthority()));

    private static AuthorityIssuancePrincipal Principal(DerivedAuthorityApproval approval) =>
        new(Actor, ApprovalValidUntil: Now.AddHours(1)) { ApprovedDerivation = approval };

    private sealed class AllowAllAuthorizer : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, Actor));
    }

    private sealed class AllowAllPolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }

    private sealed class TestTrustSource(AuthorityIssuancePrincipal? fallback) : IAuthorityIssuanceTrustSource
    {
        public AuthorityIssuancePrincipal? Fallback { get; set; } = fallback;

        public ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(
            AuthorityStoreActor presentedActor,
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(Fallback);
    }

    private sealed class CountingRecorder(bool result = true) : IAuthorityDecisionRecorder
    {
        public readonly List<(AuthorityRequest Request, AuthorityDecision Decision)> Calls = [];
        public ValueTask<bool> RecordAsync(
            AuthorityRequest request,
            AuthorityDecision decision,
            CancellationToken cancellationToken = default)
        {
            lock (Calls) Calls.Add((request, decision));
            return ValueTask.FromResult(result);
        }
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

    private sealed class StubSource : IAuthoritySnapshotSource
    {
        public Func<AuthenticatedAuthorityContext, AuthoritySnapshot?>? Resolve;
        public readonly List<AuthenticatedAuthorityContext> Calls = [];
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add(context);
            return ValueTask.FromResult(Resolve?.Invoke(context));
        }
    }

    private sealed class StubDerivations : IAuthorityDerivationStore
    {
        public Func<string, AuthorityGrantLineage?>? Resolve;
        public bool Throw;
        public ValueTask<AuthorityGrantDerivationResult> DeriveAsync(
            AuthorityDerivationCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<AuthorityGrantLineage?> GetLineageAsync(string childGrantId, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("lineage store unavailable");
            return ValueTask.FromResult(Resolve?.Invoke(childGrantId));
        }
    }

    private sealed class StubEvaluator(Func<AuthoritySnapshot, AuthorityRequest, AuthorityDecision> resolve) : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now) =>
            resolve(snapshot, request);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), "hufu-derived-policy-tests-" + Guid.NewGuid().ToString("N"));
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
