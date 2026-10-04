using Penghou.Hufu;
using Penghou.Hufu.Luban.Sqlite;
using Penghou.Luban.Execution;

namespace Hufu.LocalHost.Tests;

public sealed class HostServicesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Sid = "S-1-5-21-42";

    [Fact]
    public async Task StorePolicyRejectsSpoofedSidAndMismatchedContext()
    {
        var context = Context();
        var config = Configuration(context, LocalHostMode.Operator);
        var actor = config.Actor;
        var request = new AuthorityStoreAccessRequest(actor, AuthorityStoreOperation.ReadCurrent,
            AuthoritySubject.From(context), context);

        var spoofed = await new LocalHostStoreAuthorizer(config, new FakeIdentity("S-1-5-21-99"))
            .AuthorizeAsync(request);
        var impersonated = await new LocalHostStoreAuthorizer(config, new FakeIdentity(Sid, isImpersonating: true))
            .AuthorizeAsync(request);
        var otherContext = context with { RevisionId = "other-revision" };
        var mismatched = await new LocalHostStoreAuthorizer(config, new FakeIdentity(Sid))
            .AuthorizeAsync(request with { Context = otherContext });

        Assert.Equal(AuthorityStatus.Deny, spoofed.Status);
        Assert.Equal(AuthorityStatus.Deny, impersonated.Status);
        Assert.Equal(AuthorityStatus.Deny, mismatched.Status);
    }

    [Fact]
    public async Task StorePolicyRejectsNestedContextMismatchAndUnknownOperation()
    {
        var context = Context();
        var config = Configuration(context, LocalHostMode.Operator);
        var proposed = Snapshot(context, "proposal", AuthorityAction.ReadFile, Now.AddMinutes(30));
        var request = new AuthorityStoreAccessRequest(config.Actor, AuthorityStoreOperation.Publish,
            AuthoritySubject.From(context), context, "cmd", 0, WithContext(proposed, context with { RevisionId = "changed" }));
        var authorizer = new LocalHostStoreAuthorizer(config, new FakeIdentity(Sid));

        var mismatched = await authorizer.AuthorizeAsync(request);
        var unknown = await authorizer.AuthorizeAsync(request with { Operation = (AuthorityStoreOperation)999 });

        Assert.Equal(AuthorityStatus.Deny, mismatched.Status);
        Assert.Equal(AuthorityStatus.Deny, unknown.Status);
    }

    [Fact]
    public async Task WorkerStorePolicyAllowsOnlyBoundExecutionOperations()
    {
        var context = Context();
        var config = Configuration(context, LocalHostMode.Worker, "operation-one");
        var authorizer = new LocalHostStoreAuthorizer(config, new FakeIdentity(Sid));
        var current = new AuthorityStoreAccessRequest(config.Actor, AuthorityStoreOperation.ReadCurrent,
            AuthoritySubject.From(context), context);
        var publish = current with { Operation = AuthorityStoreOperation.Publish };
        var start = current with
        {
            Operation = AuthorityStoreOperation.StartOperation,
            StartCommand = new(config.Actor, "operation-two",
                new(context, AuthorityAction.PatchFile, "workspace", "file.txt", "request"),
                "decision", 1, "binding", "binding-json")
        };

        Assert.Equal(AuthorityStatus.Permit, (await authorizer.AuthorizeAsync(current)).Status);
        Assert.Equal(AuthorityStatus.Deny, (await authorizer.AuthorizeAsync(publish)).Status);
        Assert.Equal(AuthorityStatus.Deny, (await authorizer.AuthorizeAsync(start)).Status);
    }

    [Fact]
    public async Task WorkerJournalAllowsBoundInternalRecoveryReadButDeniesManagement()
    {
        var context = Context();
        var config = Configuration(context, LocalHostMode.Worker, "operation-one");
        var authorizer = new LocalHostPatchJournalAuthorizer(config, new FakeIdentity(Sid), new FixedClock(Now));
        var readOutcome = new PatchJournalAccessRequest(config.Actor, context, PatchJournalOperation.ReadOutcome,
            "operation-one", new string('a', 64));
        var approval = readOutcome with { Operation = PatchJournalOperation.Approve };
        var reconciliation = readOutcome with { Operation = PatchJournalOperation.ReconcileAmbiguous };
        var wrongOperation = readOutcome with { OperationId = "operation-two" };

        Assert.Equal(AuthorityStatus.Permit, (await authorizer.AuthorizeAsync(readOutcome)).Status);
        Assert.Equal(AuthorityStatus.Deny, (await authorizer.AuthorizeAsync(approval)).Status);
        Assert.Equal(AuthorityStatus.Deny, (await authorizer.AuthorizeAsync(reconciliation)).Status);
        Assert.Equal(AuthorityStatus.Deny, (await authorizer.AuthorizeAsync(wrongOperation)).Status);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("sequence")]
    [InlineData("snapshot")]
    [InlineData("expired")]
    public async Task IssuanceRejectsChangedApprovalFacts(string mutation)
    {
        var context = Context();
        var actor = Configuration(context, LocalHostMode.Operator).Actor;
        var proposal = Snapshot(context, "proposal", AuthorityAction.ReadFile, Now.AddMinutes(30));
        var ceiling = Snapshot(Context("issuer", "issuer-run", "issuer-revision", "issuer-fence"),
            "issuer-ceiling", AuthorityAction.ReadFile, Now.AddHours(1));
        var approval = new LocalHostIssuanceApproval(ceiling, proposal.Identity,
            mutation == "command" ? "different-command" : "publish-command",
            mutation == "sequence" ? 1 : 0,
            mutation == "expired" ? Now : Now.AddMinutes(10));
        if (mutation == "snapshot") approval = approval with { ApprovedSnapshotIdentity = "other-snapshot" };
        var source = new MutableIssuanceStateSource(_ => approval);
        var trust = new LocalHostIssuanceTrustSource(Configuration(context, LocalHostMode.Operator),
            new FakeIdentity(Sid), source, new FixedClock(Now));
        var policy = new AllowStorePolicy(actor);
        var authorizer = new BoundedAuthorityIssuanceAuthorizer(trust, policy, new FixedClock(Now));
        var request = PublishRequest(actor, context, proposal, "publish-command", 0);

        var result = await authorizer.AuthorizeAsync(request);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task IssuanceReloadsIndependentCeilingAndApprovalAfterPolicy()
    {
        var context = Context();
        var config = Configuration(context, LocalHostMode.Operator);
        var proposal = Snapshot(context, "proposal", AuthorityAction.ReadFile, Now.AddMinutes(30));
        var ceiling = Snapshot(Context("issuer", "issuer-run", "issuer-revision", "issuer-fence"),
            "issuer-ceiling", AuthorityAction.ReadFile, Now.AddHours(1));
        var source = new MutableIssuanceStateSource(call => new LocalHostIssuanceApproval(ceiling,
            proposal.Identity, call == 1 ? "publish-command" : "withdrawn-command", 0, Now.AddMinutes(10)));
        var trust = new LocalHostIssuanceTrustSource(config, new FakeIdentity(Sid), source, new FixedClock(Now));
        var policy = new AllowStorePolicy(config.Actor);
        var authorizer = new BoundedAuthorityIssuanceAuthorizer(trust, policy, new FixedClock(Now));

        var result = await authorizer.AuthorizeAsync(PublishRequest(config.Actor, context, proposal, "publish-command", 0));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(1, policy.Calls);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task IssuanceRejectsProposalOutsideIndependentIssuerCeiling()
    {
        var context = Context();
        var config = Configuration(context, LocalHostMode.Operator);
        var proposal = Snapshot(context, "proposal", AuthorityAction.WriteFile, Now.AddMinutes(30));
        var ceiling = Snapshot(Context("issuer", "issuer-run", "issuer-revision", "issuer-fence"),
            "issuer-ceiling", AuthorityAction.ReadFile, Now.AddHours(1));
        var source = new MutableIssuanceStateSource(_ => new(ceiling, proposal.Identity,
            "publish-command", 0, Now.AddMinutes(10)));
        var trust = new LocalHostIssuanceTrustSource(config, new FakeIdentity(Sid), source, new FixedClock(Now));
        var policy = new AllowStorePolicy(config.Actor);
        var authorizer = new BoundedAuthorityIssuanceAuthorizer(trust, policy, new FixedClock(Now));

        var result = await authorizer.AuthorizeAsync(PublishRequest(config.Actor, context, proposal, "publish-command", 0));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task EvidenceBindsExactDecisionTimeAndUsesDistinctOpaqueIds()
    {
        var factory = new LocalHostDecisionEvidenceFactory();
        var request = new AuthorityRequest(Context(), AuthorityAction.ReadFile, "workspace", "docs/file.txt", "request-1");
        var decision = new AuthorityDecision(AuthorityStatus.Permit, "allowed", "snapshot-v1", "cedar-v1", "snapshot-id");

        var first = await factory.CreateAsync(request, decision, Now);
        var second = await factory.CreateAsync(request, decision, Now);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(request, first.Request);
        Assert.Equal(decision, first.Decision);
        Assert.Equal(Now, first.EvaluatedAt);
        Assert.NotEqual(first.CommandId, second.CommandId);
        Assert.DoesNotContain("session", first.EvidenceJson, StringComparison.OrdinalIgnoreCase);
    }

    private static AuthorityStoreAccessRequest PublishRequest(AuthorityStoreActor actor,
        AuthenticatedAuthorityContext context, AuthoritySnapshot proposal, string commandId, long sequence) =>
        new(actor, AuthorityStoreOperation.Publish, AuthoritySubject.From(context), context,
            commandId, sequence, proposal);

    private static LocalHostServiceConfiguration Configuration(AuthenticatedAuthorityContext context,
        LocalHostMode mode, string? boundOperationId = null) =>
        LocalHostServiceConfiguration.Create(context.TenantId, "operator", context, Sid, mode, boundOperationId);

    private static AuthenticatedAuthorityContext Context(string subject = "subject", string run = "run",
        string revision = "revision", string fence = "fence") => new("tenant", subject, run, revision, fence);

    private static AuthoritySnapshot Snapshot(AuthenticatedAuthorityContext context, string version,
        AuthorityAction action, DateTimeOffset expiresAt) => new(context, version,
        [new AuthorityLayer("root", [new AuthorityGrant("grant", [action],
            new AuthorityScope("workspace", "", AuthorityScopeKind.Subtree), [], Now.AddMinutes(-1), expiresAt)])],
        [], expiresAt);

    private static AuthoritySnapshot WithContext(AuthoritySnapshot snapshot, AuthenticatedAuthorityContext context) =>
        new(context, snapshot.Version, snapshot.Layers, snapshot.MandatoryDenials, snapshot.ValidUntil);

    private sealed class FakeIdentity(string sid, bool isImpersonating = false) : ILocalHostIdentitySource
    {
        public LocalHostIdentityObservation Read() => new(true, sid, sid, isImpersonating);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableIssuanceStateSource(Func<int, LocalHostIssuanceApproval?> resolve)
        : ILocalHostIssuanceStateSource
    {
        public int Calls { get; private set; }
        public ValueTask<LocalHostIssuanceApproval?> ResolveCurrentAsync(AuthorityStoreActor actor,
            AuthenticatedAuthorityContext context, AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(resolve(Calls));
        }
    }

    private sealed class AllowStorePolicy(AuthorityStoreActor actor) : IAuthorityStoreAuthorizer
    {
        public int Calls { get; private set; }
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(new AuthorityStoreAuthorization(AuthorityStatus.Permit, actor));
        }
    }
}
