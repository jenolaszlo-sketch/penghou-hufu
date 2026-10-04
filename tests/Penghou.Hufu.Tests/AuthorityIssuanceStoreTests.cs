using Penghou.Hufu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class AuthorityIssuanceStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly AuthorityStoreActor Actor = new("tenant", "issuer", "authenticated-session");
    private static readonly AuthenticatedAuthorityContext Target = new("tenant", "agent", "run", "revision", "fence");

    [Fact]
    public async Task PublishAndReopenReplayRequireCurrentIssuerAuthorityAndPreserveReceipt()
    {
        using var database = new Database();
        var snapshot = Snapshot(Target, "proposal", "src/app");
        var command = new AuthorityPublishCommand("approved-command", Actor, snapshot, 0);
        var trust = new HostTrust(command);
        var policy = new HostPolicy();
        var authorizer = new BoundedAuthorityIssuanceAuthorizer(trust, policy, new Clock());

        var applied = await database.Open(authorizer).PublishAsync(command);
        var reopened = database.Open(authorizer);
        var replay = await reopened.PublishAsync(command);

        Assert.Equal(AuthorityMutationStatus.Applied, applied.Status);
        Assert.Equal(AuthorityMutationStatus.Replayed, replay.Status);
        Assert.Equal(applied.Record! with { Snapshot = null }, replay.Record! with { Snapshot = null });
        Assert.Equal(applied.Record.Snapshot!.Identity, replay.Record.Snapshot!.Identity);
        Assert.Equal(4, trust.PublicationCalls);
        Assert.Equal(2, policy.PublicationCalls);

        trust.IssuerRevoked = true;
        var deniedReplay = await reopened.PublishAsync(command);
        Assert.Equal(AuthorityMutationStatus.Denied, deniedReplay.Status);
        Assert.Null(deniedReplay.Record);
        Assert.Equal(5, trust.PublicationCalls);
        Assert.Equal(2, policy.PublicationCalls);
        var history = await reopened.ReadHistoryAsync(Actor, AuthoritySubject.From(Target), 8);
        Assert.Equal(AuthorityReadStatus.Active, history.Status);
        Assert.Single(history.Records);
        Assert.Equal(applied.Record with { Snapshot = null }, history.Records[0] with { Snapshot = null });
        Assert.Equal(applied.Record.Snapshot.Identity, history.Records[0].Snapshot!.Identity);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("sequence")]
    [InlineData("snapshot")]
    [InlineData("session")]
    [InlineData("policy")]
    public async Task UnapprovedOrPolicyDeniedPublicationCannotAppendHistory(string fault)
    {
        using var database = new Database();
        var snapshot = Snapshot(Target, "proposal", "src/app");
        var approved = new AuthorityPublishCommand("approved-command", Actor, snapshot, 0);
        var policy = new HostPolicy { DenyPublication = fault == "policy" };
        var authorizer = new BoundedAuthorityIssuanceAuthorizer(new HostTrust(approved), policy, new Clock());
        var command = fault switch
        {
            "command" => approved with { CommandId = "other-command" },
            "sequence" => approved with { ExpectedSequence = 1 },
            "snapshot" => approved with { Snapshot = Snapshot(Target, "other-proposal", "src/app") },
            "session" => approved with { Actor = Actor with { SessionId = "untrusted-session" } },
            _ => approved
        };

        var store = database.Open(authorizer);
        var result = await store.PublishAsync(command);
        Assert.Equal(AuthorityMutationStatus.Denied, result.Status);
        Assert.Null(result.Record);
        var history = await store.ReadHistoryAsync(Actor, AuthoritySubject.From(Target), 8);
        Assert.Equal(AuthorityReadStatus.Active, history.Status);
        Assert.Empty(history.Records);
    }

    private static AuthoritySnapshot Snapshot(AuthenticatedAuthorityContext context, string version, string path) =>
        new(context, version, [new("layer", [new("grant", [AuthorityAction.ReadFile],
            new("workspace", path, AuthorityScopeKind.Subtree), [], Now.AddHours(-1), Now.AddMinutes(30))])],
            [], Now.AddHours(1));

    // Explicit trusted fixture: actor credentials and approvals originate in host-owned state.
    // The proposed snapshot is never used to derive the issuer's ceiling.
    private sealed class HostTrust(AuthorityPublishCommand approved) : IAuthorityIssuanceTrustSource
    {
        private readonly AuthoritySnapshot ceiling = Snapshot(
            new("tenant", "issuer", "issuer-run", "issuer-revision", "issuer-fence"), "issuer-ceiling", "src");
        internal bool IssuerRevoked { get; set; }
        internal int PublicationCalls { get; private set; }
        public ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(AuthorityStoreActor presentedActor,
            AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Operation == AuthorityStoreOperation.Publish) PublicationCalls++;
            if (presentedActor != Actor) return ValueTask.FromResult<AuthorityIssuancePrincipal?>(null);
            var principal = request.Operation == AuthorityStoreOperation.Publish
                ? new AuthorityIssuancePrincipal(Actor, IssuerRevoked ? null : ceiling, approved.Snapshot.Identity,
                    approved.ExpectedSequence, approved.CommandId, Now.AddMinutes(10))
                : new AuthorityIssuancePrincipal(Actor);
            return ValueTask.FromResult<AuthorityIssuancePrincipal?>(principal);
        }
    }

    private sealed class HostPolicy : IAuthorityStoreAuthorizer
    {
        internal bool DenyPublication { get; init; }
        internal int PublicationCalls { get; private set; }
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Operation == AuthorityStoreOperation.Publish) PublicationCalls++;
            return ValueTask.FromResult(request.Operation == AuthorityStoreOperation.Publish && DenyPublication
                ? new AuthorityStoreAuthorization(AuthorityStatus.Deny)
                : new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Database : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "hufu-issuance-" + Guid.NewGuid().ToString("N"));
        internal Database() => Directory.CreateDirectory(root);
        internal SqliteAuthorityStore Open(IAuthorityStoreAuthorizer authorizer) =>
            new(Path.Combine(root, "authority.db"), authorizer, new Clock());
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
