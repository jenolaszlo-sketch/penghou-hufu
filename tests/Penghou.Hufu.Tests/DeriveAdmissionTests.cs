using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>
/// Derive admission: an authenticated actor may cause a child grant to exist
/// only when an exact derivation approval is present. Containment says the
/// child fits the parent; delegability says this actor may create it.
/// </summary>
public sealed class DeriveAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Supervisor =
        new("tenant", "supervisor", "run-sup", "rev", "fence");
    private static readonly AuthenticatedAuthorityContext Child =
        new("tenant", "delegation", "run-child", "rev", "fence");
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");

    [Fact]
    public async Task Derive_with_exact_approval_issues_child()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database, time);
        var store = database.Open(time, new BoundedAuthorityIssuanceAuthorizer(
            new TestTrustSource(Principal(Approval())), new AllowAllPolicy(), time));

        var result = await store.DeriveAsync(Command(), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Permit, result.Status);
        Assert.NotNull(result.Grant);
        Assert.Equal("grant-parent", result.Grant!.ParentGrantId);
        Assert.Equal(Actor, (await store.GetLineageAsync(result.Grant.Id, CancellationToken.None))!.Actor);
    }

    [Fact]
    public async Task Derive_without_approval_is_denied()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database, time);
        var store = database.Open(time, new BoundedAuthorityIssuanceAuthorizer(
            new TestTrustSource(new AuthorityIssuancePrincipal(Actor)), new AllowAllPolicy(), time));

        var result = await store.DeriveAsync(Command(), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Null(result.Grant);
        Assert.Equal("authority.derivation-not-authorized", result.ReasonCode);
    }

    [Fact]
    public async Task Approval_for_different_authority_does_not_match()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database, time);
        // Approval binds src/service; the request asks for src/other, which is
        // still contained by the parent, so only the exact-tuple approval can
        // reject it. A weakened gate would issue a child here.
        var store = database.Open(time, new BoundedAuthorityIssuanceAuthorizer(
            new TestTrustSource(Principal(Approval())), new AllowAllPolicy(), time));

        var result = await store.DeriveAsync(
            Command(requested: AuthorityAt("src/other")), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Null(result.Grant);
        Assert.Equal("authority.derivation-not-authorized", result.ReasonCode);
    }

    [Fact]
    public async Task Approval_for_different_generation_does_not_match()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database, time);
        var store = database.Open(time, new BoundedAuthorityIssuanceAuthorizer(
            new TestTrustSource(Principal(Approval(generation: "gen-1"))), new AllowAllPolicy(), time));

        var result = await store.DeriveAsync(Command(generation: "gen-2"), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Null(result.Grant);
        Assert.Equal("authority.derivation-not-authorized", result.ReasonCode);
    }

    [Fact]
    public async Task Approval_for_different_parent_does_not_match()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database, time);
        var store = database.Open(time, new BoundedAuthorityIssuanceAuthorizer(
            new TestTrustSource(Principal(Approval(parentGrantId: "grant-parent"))), new AllowAllPolicy(), time));

        var result = await store.DeriveAsync(
            Command(parentGrantId: "grant-other"), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Null(result.Grant);
        Assert.Equal("authority.derivation-not-authorized", result.ReasonCode);
    }

    [Fact]
    public async Task Approval_removed_on_reload_is_denied()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database, time);
        // First fetch sees the approval; the reload after operation policy does
        // not. Freshness discipline must reject the derivation.
        var trust = new TestTrustSource(Principal(Approval()));
        trust.Enqueue(Principal(Approval()));
        trust.Enqueue(new AuthorityIssuancePrincipal(Actor));
        var store = database.Open(time, new BoundedAuthorityIssuanceAuthorizer(trust, new AllowAllPolicy(), time));

        var result = await store.DeriveAsync(Command(), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Null(result.Grant);
        Assert.Equal("authority.derivation-not-authorized", result.ReasonCode);
    }

    [Fact]
    public async Task Approval_expired_is_denied()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database, time);
        var expired = new AuthorityIssuancePrincipal(
            Actor, ApprovalValidUntil: Now.AddMinutes(-1), ApprovedDerivation: Approval());
        var store = database.Open(time, new BoundedAuthorityIssuanceAuthorizer(
            new TestTrustSource(expired), new AllowAllPolicy(), time));

        var result = await store.DeriveAsync(Command(), CancellationToken.None);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Null(result.Grant);
    }

    [Fact]
    public async Task Issued_child_survives_later_approval_removal()
    {
        using var database = new TemporaryDatabase();
        var time = new MutableTimeProvider(Now);
        await PublishParentAsync(database, time);
        var trust = new TestTrustSource(Principal(Approval()));
        var store = database.Open(time, new BoundedAuthorityIssuanceAuthorizer(trust, new AllowAllPolicy(), time));

        var issued = await store.DeriveAsync(Command(generation: "gen-1"), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Permit, issued.Status);
        Assert.NotNull(issued.Grant);

        // Delegability is an issuance-time right. Withdrawing the approval
        // blocks new derivations but never rescinds an already-issued child;
        // the child's effectiveness is governed by ancestor liveness, not by
        // the approval that authorized its creation.
        trust.Fallback = new AuthorityIssuancePrincipal(Actor);

        var later = await store.DeriveAsync(Command(generation: "gen-2"), CancellationToken.None);
        Assert.Equal(AuthorityStatus.Deny, later.Status);
        Assert.Null(later.Grant);

        var state = await store.ReadCurrentAsync(Actor, Child, CancellationToken.None);
        Assert.Equal(AuthorityReadStatus.Active, state.Status);
    }

    private static RequestedAuthority Authority() => AuthorityAt("src/service");

    private static RequestedAuthority AuthorityAt(string path) => new(
        [AuthorityAction.ReadFile],
        new AuthorityScope("workspace", path, AuthorityScopeKind.Subtree),
        [],
        Now.AddMinutes(-1),
        Now.AddDays(1));

    private static AuthorityDerivationCommand Command(
        string generation = "gen-1",
        string parentGrantId = "grant-parent",
        RequestedAuthority? requested = null) =>
        new(Actor, Supervisor, parentGrantId, Child, "delegation-1", generation, requested ?? Authority(), "req-1");

    private static DerivedAuthorityApproval Approval(
        string generation = "gen-1",
        string parentGrantId = "grant-parent",
        RequestedAuthority? requested = null) =>
        new(parentGrantId, "delegation-1", generation,
            AuthorityDerivation.RequestedAuthorityHash(requested ?? Authority()));

    private static AuthorityIssuancePrincipal Principal(DerivedAuthorityApproval approval) =>
        new(Actor, ApprovalValidUntil: Now.AddHours(1), ApprovedDerivation: approval);

    private static async Task PublishParentAsync(TemporaryDatabase database, TimeProvider time)
    {
        var store = database.Open(time, new AllowAllPolicy());
        var snapshot = new AuthoritySnapshot(Supervisor, "v1",
            [new AuthorityLayer("run", [
                new AuthorityGrant("grant-parent", [AuthorityAction.ReadFile],
                    new AuthorityScope("workspace", "src", AuthorityScopeKind.Subtree), [],
                    Now.AddMinutes(-1), Now.AddDays(1))])],
            [], Now.AddDays(1));
        var published = await store.PublishAsync(
            new AuthorityPublishCommand("publish-parent", Actor, snapshot, 0), CancellationToken.None);
        Assert.Equal(AuthorityMutationStatus.Applied, published.Status);
    }

    private sealed class TestTrustSource(AuthorityIssuancePrincipal? fallback) : IAuthorityIssuanceTrustSource
    {
        private readonly Queue<AuthorityIssuancePrincipal?> _queued = new();
        public AuthorityIssuancePrincipal? Fallback { get; set; } = fallback;

        public void Enqueue(AuthorityIssuancePrincipal? principal) => _queued.Enqueue(principal);

        public ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(
            AuthorityStoreActor presentedActor,
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(_queued.Count > 0 ? _queued.Dequeue() : Fallback);
    }

    private sealed class AllowAllPolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), "hufu-derive-admission-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(DirectoryPath, "authority.db");

        public SqliteAuthorityStore Open(TimeProvider time, IAuthorityStoreAuthorizer authorizer)
        {
            Directory.CreateDirectory(DirectoryPath);
            return new SqliteAuthorityStore(DatabasePath, authorizer, time);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
