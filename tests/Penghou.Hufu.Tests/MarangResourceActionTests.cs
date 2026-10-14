using Penghou.Hufu.Cedar;
using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>
/// AE-5B.1: the four Marang resource actions are distinct authority meanings
/// over the existing <see cref="CurrentAuthorityRequestAuthorizer"/> operation.
/// Neither acceptance nor derivation authorizes resources; actions and
/// resources do not substitute; stale permits are never reused.
/// </summary>
public sealed class MarangResourceActionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Context =
        new("marang", "tester", "run", "g0", "accept");

    private static readonly AuthorityAction[] MarangActions =
    [
        AuthorityAction.MarangReadDelegation,
        AuthorityAction.MarangReadResult,
        AuthorityAction.MarangIntervene,
        AuthorityAction.MarangCancel,
    ];

    [Theory]
    [InlineData(AuthorityAction.MarangReadDelegation)]
    [InlineData(AuthorityAction.MarangReadResult)]
    [InlineData(AuthorityAction.MarangIntervene)]
    [InlineData(AuthorityAction.MarangCancel)]
    public async Task MarangAction_PermitsWhenGrantedOnTheExactResource(AuthorityAction action)
    {
        var path = ResourceFor(action);
        var snapshot = Snapshot("v1", [Grant("g", [action], Scope("workspace", "delegation"))]);
        var recorder = new Recorder();
        var authorizer = new CurrentAuthorityRequestAuthorizer(
            new Source(snapshot), new CedarAuthorityEvaluator(), recorder, new Clock(Now));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Context, action, "workspace", path, "request-1"));

        Assert.True(result.IsAuthorized);
        Assert.Equal("v1", result.Decision!.SnapshotVersion);
        Assert.Equal(snapshot.Identity, result.Decision.SnapshotIdentity);
        Assert.Equal(1, recorder.Calls);
    }

    [Theory]
    // Read/read split: the two reads do not substitute.
    [InlineData(AuthorityAction.MarangReadDelegation, AuthorityAction.MarangReadResult)]
    [InlineData(AuthorityAction.MarangReadResult, AuthorityAction.MarangReadDelegation)]
    // Read/write split.
    [InlineData(AuthorityAction.MarangReadDelegation, AuthorityAction.MarangIntervene)]
    [InlineData(AuthorityAction.MarangReadDelegation, AuthorityAction.MarangCancel)]
    [InlineData(AuthorityAction.MarangReadResult, AuthorityAction.MarangCancel)]
    // Write/write split.
    [InlineData(AuthorityAction.MarangIntervene, AuthorityAction.MarangCancel)]
    [InlineData(AuthorityAction.MarangCancel, AuthorityAction.MarangIntervene)]
    // File verbs are not Marang meanings, in either direction.
    [InlineData(AuthorityAction.ReadFile, AuthorityAction.MarangReadDelegation)]
    [InlineData(AuthorityAction.MarangReadDelegation, AuthorityAction.ReadFile)]
    [InlineData(AuthorityAction.Release, AuthorityAction.MarangCancel)]
    [InlineData(AuthorityAction.MarangCancel, AuthorityAction.Release)]
    [InlineData(AuthorityAction.PatchFile, AuthorityAction.MarangIntervene)]
    [InlineData(AuthorityAction.MarangIntervene, AuthorityAction.PatchFile)]
    [InlineData(AuthorityAction.WriteFile, AuthorityAction.MarangCancel)]
    public async Task ActionSubstitution_Denies(AuthorityAction granted, AuthorityAction requested)
    {
        var snapshot = Snapshot("v1", [Grant("g", [granted], Scope("workspace", "delegation"))]);
        var authorizer = new CurrentAuthorityRequestAuthorizer(
            new Source(snapshot), new CedarAuthorityEvaluator(), new Recorder(), new Clock(Now));

        var result = await authorizer.AuthorizeAsync(
            new AuthorityRequest(Context, requested, "workspace", ResourceFor(requested), "request-1"));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task WrongResource_Denies()
    {
        var snapshot = Snapshot("v1",
            [Grant("g", [AuthorityAction.MarangReadDelegation], Scope("workspace", "delegation/aaa/supervision", AuthorityScopeKind.Exact))]);
        var authorizer = new CurrentAuthorityRequestAuthorizer(
            new Source(snapshot), new CedarAuthorityEvaluator(), new Recorder(), new Clock(Now));

        var result = await authorizer.AuthorizeAsync(new AuthorityRequest(
            Context, AuthorityAction.MarangReadDelegation, "workspace", "delegation/bbb/supervision", "request-1"));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
    }

    [Fact]
    public async Task AcceptancePermit_DoesNotAuthorizeAResource()
    {
        // One snapshot with no Marang resource grant. Acceptance permits by its
        // own policy; the resource authorizer over the same current authority
        // must still deny.
        var snapshot = Snapshot("v1",
            [Grant("g", [AuthorityAction.ReadFile], Scope("workspace", "unrelated.txt", AuthorityScopeKind.Exact))]);
        var source = new Source(snapshot);
        var clock = new Clock(Now);

        var acceptanceRecords = new List<(DelegationAcceptanceRequest, AuthorityDecision)>();
        var acceptance = new CurrentDelegationAcceptanceAuthorizer(
            source,
            new PermitEvaluator(),
            new AcceptanceRecorder(acceptanceRecords),
            clock);
        var resourceRecorder = new Recorder();
        var resource = new CurrentAuthorityRequestAuthorizer(
            source, new CedarAuthorityEvaluator(), resourceRecorder, clock);

        var acceptanceResult = await acceptance.AuthorizeAsync(new DelegationAcceptanceRequest(
            Context, "acceptance-1", "workspace", "provider", "g0", new string('a', 64)));
        var resourceResult = await resource.AuthorizeAsync(new AuthorityRequest(
            Context, AuthorityAction.MarangCancel, "workspace", "delegation/aaa/supervision", "request-1"));

        Assert.True(acceptanceResult.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, resourceResult.Status);
        Assert.False(resourceResult.IsAuthorized);
        Assert.Single(acceptanceRecords);
        Assert.Equal(1, resourceRecorder.Calls);
    }

    [Fact]
    public async Task ResourceGrant_DoesNotAdmitADelegation()
    {
        // Bidirectional separation: a Marang resource grant must not satisfy
        // acceptance when the acceptance policy denies.
        var snapshot = Snapshot("v1",
            [Grant("g", [AuthorityAction.MarangReadDelegation], Scope("workspace", "delegation"))]);
        var source = new Source(snapshot);
        var acceptance = new CurrentDelegationAcceptanceAuthorizer(
            source, new DenyEvaluator(), new AcceptanceRecorder([]), new Clock(Now));
        var resource = new CurrentAuthorityRequestAuthorizer(
            source, new CedarAuthorityEvaluator(), new Recorder(), new Clock(Now));

        var acceptanceResult = await acceptance.AuthorizeAsync(new DelegationAcceptanceRequest(
            Context, "acceptance-1", "workspace", "provider", "g0", new string('a', 64)));
        var resourceResult = await resource.AuthorizeAsync(new AuthorityRequest(
            Context, AuthorityAction.MarangReadDelegation, "workspace", "delegation/aaa/supervision", "request-1"));

        Assert.False(acceptanceResult.IsAuthorized);
        Assert.True(resourceResult.IsAuthorized);
    }

    [Fact]
    public async Task DerivedOnlyGrant_WithoutLineageComposition_Denies()
    {
        // Derivation authority never leaks into a resource Permit through an
        // incomplete composition: known derived grants deny without the
        // qualified current-parent checker.
        var grant = Grant("derived", [AuthorityAction.MarangCancel], Scope("workspace", "delegation")) with
        {
            ParentGrantId = "parent",
        };
        var snapshot = Snapshot("v1", [grant]);
        var authorizer = new CurrentAuthorityRequestAuthorizer(
            new Source(snapshot), new CedarAuthorityEvaluator(), new Recorder(), new Clock(Now));

        var result = await authorizer.AuthorizeAsync(new AuthorityRequest(
            Context, AuthorityAction.MarangCancel, "workspace", "delegation/aaa/supervision", "request-1"));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task NarrowedAuthority_DeniesTheNextAttempt()
    {
        var covering = Snapshot("v1", [Grant("g", [AuthorityAction.MarangCancel], Scope("workspace", "delegation"))]);
        var narrowed = Snapshot("v2", [Grant("g", [AuthorityAction.MarangReadDelegation], Scope("workspace", "delegation"))]);
        var source = new SequencedSource(covering, narrowed);
        var authorizer = new CurrentAuthorityRequestAuthorizer(
            source, new CedarAuthorityEvaluator(), new Recorder(), new Clock(Now));
        Func<ValueTask<AuthorityRequestAuthorization>> attempt = () => authorizer.AuthorizeAsync(new AuthorityRequest(
            Context, AuthorityAction.MarangCancel, "workspace", "delegation/aaa/supervision", Guid.NewGuid().ToString("N")));

        Assert.True((await attempt()).IsAuthorized);
        var second = await attempt();
        Assert.Equal(AuthorityStatus.Deny, second.Status);
        Assert.False(second.IsAuthorized);
    }

    [Fact]
    public async Task EvidenceFailure_AfterPermit_FailsClosed()
    {
        var snapshot = Snapshot("v1", [Grant("g", [AuthorityAction.MarangReadResult], Scope("workspace", "delegation"))]);
        var authorizer = new CurrentAuthorityRequestAuthorizer(
            new Source(snapshot), new CedarAuthorityEvaluator(), new RefusingRecorder(), new Clock(Now));

        var result = await authorizer.AuthorizeAsync(new AuthorityRequest(
            Context, AuthorityAction.MarangReadResult, "workspace", "delegation/aaa/result", "request-1"));

        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
        Assert.False(result.IsAuthorized);
    }

    private static string ResourceFor(AuthorityAction action) => action switch
    {
        AuthorityAction.MarangReadDelegation => "delegation/aaa/supervision",
        AuthorityAction.MarangReadResult => "delegation/aaa/result",
        AuthorityAction.MarangIntervene => "delegation/aaa/checkpoint/bbb",
        AuthorityAction.MarangCancel => "delegation/aaa/supervision",
        _ => "delegation/aaa/supervision",
    };

    private static AuthoritySnapshot Snapshot(string version, IReadOnlyList<AuthorityGrant> grants) =>
        new(Context, version, [new AuthorityLayer("run", grants)], [], Now.AddHours(1));

    private static AuthorityGrant Grant(string id, IReadOnlyList<AuthorityAction> actions, AuthorityScope scope) =>
        new(id, actions, scope, [], Now.AddMinutes(-1), Now.AddHours(1));

    private static AuthorityScope Scope(string workspace, string path,
        AuthorityScopeKind kind = AuthorityScopeKind.Subtree) => new(workspace, path, kind);

    private sealed class Source(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<AuthoritySnapshot?>(snapshot);
    }

    private sealed class SequencedSource(params AuthoritySnapshot[] snapshots) : IAuthoritySnapshotSource
    {
        private int _index;
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<AuthoritySnapshot?>(snapshots[Math.Min(_index++, snapshots.Length - 1)]);
    }

    private sealed class Recorder : IAuthorityDecisionRecorder
    {
        internal int Calls { get; private set; }
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RefusingRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken ct = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PermitEvaluator : IDelegationAcceptanceEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, DelegationAcceptanceRequest request, DateTimeOffset now) =>
            new(AuthorityStatus.Permit, "test-accept-permit", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
    }

    private sealed class DenyEvaluator : IDelegationAcceptanceEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, DelegationAcceptanceRequest request, DateTimeOffset now) =>
            new(AuthorityStatus.Deny, "test-accept-denied", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
    }

    private sealed class AcceptanceRecorder(List<(DelegationAcceptanceRequest, AuthorityDecision)> records) : IDelegationAcceptanceRecorder
    {
        public ValueTask<bool> RecordAsync(DelegationAcceptanceRequest request, AuthorityDecision decision, CancellationToken ct = default)
        {
            records.Add((request, decision));
            return ValueTask.FromResult(true);
        }
    }
}
