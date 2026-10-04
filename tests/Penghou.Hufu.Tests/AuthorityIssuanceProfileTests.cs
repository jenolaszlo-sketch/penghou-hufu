using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>Trusted-boundary fixtures for the bounded publication profile; these do not model production authentication.</summary>
public sealed class AuthorityIssuanceProfileTests
{
    private static readonly DateTimeOffset InitialTime = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly AuthorityStoreActor Actor = new("tenant", "issuer", "session");
    private static readonly AuthenticatedAuthorityContext Target = new("tenant", "workflow", "run", "r1", "f1");
    private static readonly AuthenticatedAuthorityContext Issuer = new("tenant", "issuer", "issuer-run", "ir1", "if1");

    [Fact]
    public async Task ExactApprovedContainedSnapshotRunsBothFreshTrustChecksAndPublishPolicy()
    {
        var proposed = Snapshot(Target, "proposed", [Grant("proposal", "workspace", "src/app", [AuthorityAction.ReadFile])]);
        var ceiling = Snapshot(Issuer, "issuer-ceiling", [Grant("ceiling", "workspace", "src", [AuthorityAction.ReadFile])]);
        var trust = TrustSource.Returning(Principal(proposed, ceiling));
        AuthorityStoreAccessRequest? observed = null;
        var policy = new OperationPolicy(request => { observed = request; return Permit(request.Actor); });

        var result = await Authorizer(trust, policy).AuthorizeAsync(Request(proposed));

        Assert.Equal(AuthorityStatus.Permit, result.Status);
        Assert.Equal(Actor, result.Actor);
        Assert.Equal(2, trust.Calls);
        Assert.Equal(1, policy.Calls);
        Assert.Equal(Request(proposed), observed);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("sequence")]
    [InlineData("snapshot")]
    [InlineData("approval-expired")]
    public async Task ApprovalMustBindExactCommandSnapshotSequenceAndExpiry(string fault)
    {
        var proposal = Proposal();
        var principal = Principal(proposal.Proposed, proposal.Ceiling) with
        {
            ApprovedSnapshotIdentity = fault == "snapshot" ? new string('a', 64) : proposal.Proposed.Identity,
            ApprovalValidUntil = fault == "approval-expired" ? InitialTime : InitialTime.AddMinutes(1)
        };
        var request = Request(proposal.Proposed) with
        {
            CommandId = fault == "command" ? "different-command" : "publish-1",
            ExpectedSequence = fault == "sequence" ? 5 : 4
        };
        var policy = new OperationPolicy(_ => Permit(Actor));

        var result = await Authorizer(TrustSource.Returning(principal), policy).AuthorizeAsync(request);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task PublishRequestMustCarryExactSnapshotContextAndValidCommandSequenceBeforeTrust()
    {
        var proposal = Proposal();
        var trust = TrustSource.Returning(Principal(proposal.Proposed, proposal.Ceiling));
        var policy = new OperationPolicy(_ => Permit(Actor));
        var wrongContext = Request(proposal.Proposed) with { Context = Target with { FenceId = "other-fence" } };
        var invalidCommand = Request(proposal.Proposed) with { CommandId = " " };
        var invalidSequence = Request(proposal.Proposed) with { ExpectedSequence = null };

        Assert.Equal(AuthorityStatus.Deny, (await Authorizer(trust, policy).AuthorizeAsync(wrongContext)).Status);
        Assert.Equal(AuthorityStatus.Deny, (await Authorizer(trust, policy).AuthorizeAsync(invalidCommand)).Status);
        Assert.Equal(AuthorityStatus.Deny, (await Authorizer(trust, policy).AuthorizeAsync(invalidSequence)).Status);
        Assert.Equal(0, trust.Calls);
        Assert.Equal(0, policy.Calls);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("workspace")]
    [InlineData("action")]
    [InlineData("ceiling-expired")]
    [InlineData("ceiling-exclusion")]
    [InlineData("mandatory-denial")]
    [InlineData("split-layers")]
    public async Task PublicationCannotExceedCeilingActionsScopesTimesOrLayers(string fault)
    {
        var proposal = Proposal();
        var proposed = proposal.Proposed;
        var ceiling = proposal.Ceiling;
        if (fault == "scope")
            proposed = Snapshot(Target, "proposed", [Grant("proposal", "workspace", "private", [AuthorityAction.ReadFile])]);
        if (fault == "workspace")
            proposed = Snapshot(Target, "proposed", [Grant("proposal", "other-workspace", "src/app", [AuthorityAction.ReadFile])]);
        if (fault == "action")
            proposed = Snapshot(Target, "proposed", [Grant("proposal", "workspace", "src/app", [AuthorityAction.WriteFile])]);
        if (fault == "ceiling-expired")
            ceiling = Snapshot(Issuer, "issuer-ceiling", [Grant("ceiling", "workspace", "src", [AuthorityAction.ReadFile])], validUntil: InitialTime);
        if (fault == "ceiling-exclusion")
            ceiling = Snapshot(Issuer, "issuer-ceiling", [Grant("ceiling", "workspace", "src", [AuthorityAction.ReadFile],
                exclusions: [new("workspace", "src/app", AuthorityScopeKind.Subtree)])]);
        if (fault == "mandatory-denial")
            ceiling = Snapshot(Issuer, "issuer-ceiling", [Grant("ceiling", "workspace", "src", [AuthorityAction.ReadFile])],
                denials: [new("workspace", "src/app/private", AuthorityScopeKind.Subtree)]);
        if (fault == "split-layers")
        {
            proposed = Snapshot(Target, "proposed", [
                new AuthorityLayer("layer-a", [GrantEntry("g-a", "workspace", "src/app", [AuthorityAction.ReadFile])]),
                new AuthorityLayer("layer-b", [GrantEntry("g-b", "workspace", "src/app", [AuthorityAction.ReadFile])])]);
            ceiling = Snapshot(Issuer, "issuer-ceiling", [
                new AuthorityLayer("ceiling-a", [GrantEntry("c-a", "workspace", "src", [AuthorityAction.ReadFile])]),
                new AuthorityLayer("ceiling-b", [GrantEntry("c-b", "workspace", "private", [AuthorityAction.ReadFile])])]);
        }
        var result = await Authorizer(TrustSource.Returning(Principal(proposed, ceiling)), new OperationPolicy(_ => Permit(Actor)))
            .AuthorizeAsync(Request(proposed));
        Assert.Equal(AuthorityStatus.Deny, result.Status);
    }

    [Fact]
    public async Task ProposedSnapshotCannotServeAsItsOwnCeilingEvenWhenClonedWithEqualIdentity()
    {
        var proposal = Proposal();
        var clonedProposal = Snapshot(Target, proposal.Proposed.Version, proposal.Proposed.Layers,
            proposal.Proposed.ValidUntil, proposal.Proposed.MandatoryDenials);
        Assert.NotSame(proposal.Proposed, clonedProposal);
        Assert.Equal(proposal.Proposed.Identity, clonedProposal.Identity);
        var principal = Principal(proposal.Proposed, clonedProposal);

        var result = await Authorizer(TrustSource.Returning(principal), new OperationPolicy(_ => Permit(Actor)))
            .AuthorizeAsync(Request(proposal.Proposed));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
    }

    [Fact]
    public async Task BroaderProposedMandatoryDenialIsAValidAuthorityAttenuation()
    {
        var proposed = Snapshot(Target, "proposed", [Grant("proposal", "workspace", "src/app", [AuthorityAction.ReadFile])],
            denials: [new("workspace", "src", AuthorityScopeKind.Subtree)]);
        var ceiling = Snapshot(Issuer, "issuer-ceiling", [Grant("ceiling", "workspace", "src", [AuthorityAction.ReadFile])],
            denials: [new("workspace", "src/secrets", AuthorityScopeKind.Subtree)]);

        var result = await Authorizer(TrustSource.Returning(Principal(proposed, ceiling)), new OperationPolicy(_ => Permit(Actor)))
            .AuthorizeAsync(Request(proposed));

        Assert.Equal(AuthorityStatus.Permit, result.Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-actor")]
    [InlineData("wrong-session")]
    [InlineData("throws")]
    public async Task UnauthenticatedOrMalformedPrincipalFailsClosed(string fault)
    {
        var proposal = Proposal();
        var principal = fault switch
        {
            "wrong-tenant" => Principal(proposal.Proposed, proposal.Ceiling) with { Actor = Actor with { TenantId = "other" } },
            "wrong-actor" => Principal(proposal.Proposed, proposal.Ceiling) with { Actor = Actor with { ActorId = "other" } },
            "wrong-session" => Principal(proposal.Proposed, proposal.Ceiling) with { Actor = Actor with { SessionId = "other-session" } },
            _ => Principal(proposal.Proposed, proposal.Ceiling)
        };
        var trust = fault == "throws" ? new TrustSource((_, _) => throw new InvalidOperationException("host auth failed"))
            : TrustSource.Returning(fault == "null" ? null : principal);
        var policy = new OperationPolicy(_ => Permit(Actor));

        var result = await Authorizer(trust, policy).AuthorizeAsync(Request(proposal.Proposed));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(0, policy.Calls);
    }

    [Theory]
    [InlineData(AuthorityStatus.Deny, "actor")]
    [InlineData(AuthorityStatus.Unavailable, "same")]
    [InlineData(AuthorityStatus.Permit, "wrong-actor")]
    [InlineData(AuthorityStatus.Permit, "null-actor")]
    [InlineData((AuthorityStatus)int.MaxValue, "same")]
    public async Task OperationPolicyMustPermitExactAuthenticatedActor(AuthorityStatus status, string actorMode)
    {
        var proposal = Proposal();
        AuthorityStoreActor? policyActor = actorMode switch
        {
            "wrong-actor" => Actor with { ActorId = "other" },
            "null-actor" => null,
            _ => Actor
        };
        var policy = new OperationPolicy(_ => new(status, policyActor));
        var trust = TrustSource.Returning(Principal(proposal.Proposed, proposal.Ceiling));

        var result = await Authorizer(trust, policy).AuthorizeAsync(Request(proposal.Proposed));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(1, policy.Calls);
        Assert.Equal(1, trust.Calls);
    }

    [Fact]
    public async Task PublishCannotBypassExistingOperationPolicy()
    {
        var proposal = Proposal();
        var trust = TrustSource.Returning(Principal(proposal.Proposed, proposal.Ceiling));
        var policy = new OperationPolicy(_ => new(AuthorityStatus.Deny));

        var result = await Authorizer(trust, policy).AuthorizeAsync(Request(proposal.Proposed));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(1, policy.Calls);
        Assert.Equal(1, trust.Calls);
    }

    [Fact]
    public async Task NullOperationPolicyResponseFailsClosed()
    {
        var proposal = Proposal();
        var policy = new OperationPolicy(_ => null!);

        var result = await Authorizer(TrustSource.Returning(Principal(proposal.Proposed, proposal.Ceiling)), policy)
            .AuthorizeAsync(Request(proposal.Proposed));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(1, policy.Calls);
    }

    [Fact]
    public async Task UnknownOperationIsRejectedBeforeTrustAndPolicy()
    {
        var proposal = Proposal();
        var trust = TrustSource.Returning(Principal(proposal.Proposed, proposal.Ceiling));
        var policy = new OperationPolicy(_ => Permit(Actor));
        var unknown = Request(proposal.Proposed) with { Operation = (AuthorityStoreOperation)int.MaxValue };

        var result = await Authorizer(trust, policy).AuthorizeAsync(unknown);

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(0, trust.Calls);
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task NonPublishOperationsRequireAuthenticatedActorAndOperationPolicyWithoutApproval()
    {
        var proposal = Proposal();
        var trust = TrustSource.Returning(Principal(proposal.Proposed, proposal.Ceiling));
        AuthorityStoreAccessRequest? observed = null;
        var policy = new OperationPolicy(request => { observed = request; return Permit(Actor); });
        var read = Request(proposal.Proposed) with
        {
            Operation = AuthorityStoreOperation.ReadCurrent,
            CommandId = null,
            ExpectedSequence = null,
            ProposedSnapshot = null
        };

        var result = await Authorizer(trust, policy).AuthorizeAsync(read);

        Assert.Equal(AuthorityStatus.Permit, result.Status);
        Assert.Equal(Actor, result.Actor);
        Assert.Equal(1, trust.Calls);
        Assert.Equal(1, policy.Calls);
        Assert.Equal(read, observed);
    }

    [Fact]
    public void NullServicesAndOutOfRangeEvaluationDurationsAreRejected()
    {
        var policy = new OperationPolicy(_ => Permit(Actor));
        var trust = TrustSource.Returning(null);
        Assert.Throws<ArgumentNullException>(() => new BoundedAuthorityIssuanceAuthorizer(null!, policy));
        Assert.Throws<ArgumentNullException>(() => new BoundedAuthorityIssuanceAuthorizer(trust, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedAuthorityIssuanceAuthorizer(trust, policy,
            maximumEvaluationDuration: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedAuthorityIssuanceAuthorizer(trust, policy,
            maximumEvaluationDuration: TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(1))));
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotActivateTrustedServices()
    {
        var proposal = Proposal();
        var trust = TrustSource.Returning(Principal(proposal.Proposed, proposal.Ceiling));
        var policy = new OperationPolicy(_ => Permit(Actor));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Authorizer(trust, policy).AuthorizeAsync(Request(proposal.Proposed), cancellation.Token).AsTask());

        Assert.Equal(0, trust.Calls);
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task RechecksCurrentCeilingAndApprovalAfterAsynchronousPolicy()
    {
        var proposal = Proposal();
        var original = Principal(proposal.Proposed, proposal.Ceiling);
        var trust = new TrustSource((call, _) => ValueTask.FromResult<AuthorityIssuancePrincipal?>(call == 1 ? original : null));
        var policy = OperationPolicy.Paused();
        var call = Authorizer(trust, policy).AuthorizeAsync(Request(proposal.Proposed)).AsTask();
        await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, trust.Calls);

        policy.Release();
        var result = await call.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(2, trust.Calls);
        Assert.Equal(1, policy.Calls);
    }

    [Theory]
    [InlineData("approval-expiry")]
    [InlineData("ceiling-expiry")]
    [InlineData("clock-rewind")]
    public async Task ClockOrValidityChangesAcrossPolicyAwaitCannotPermit(string fault)
    {
        var proposal = Proposal(proposedValidUntil: InitialTime.AddMinutes(1),
            ceilingValidUntil: InitialTime.AddMinutes(2), approvalValidUntil: InitialTime.AddMinutes(2));
        var clock = new FixedClock(InitialTime);
        var policy = new OperationPolicy(_ =>
        {
            clock.Now = fault == "clock-rewind" ? InitialTime.AddTicks(-1) : InitialTime.AddMinutes(3);
            return Permit(Actor);
        });
        var trust = TrustSource.Returning(Principal(proposal.Proposed, proposal.Ceiling, proposal.ApprovalValidUntil));

        var result = await Authorizer(trust, policy, clock, maximumEvaluationDuration: TimeSpan.FromMinutes(5))
            .AuthorizeAsync(Request(proposal.Proposed));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal(fault == "clock-rewind" ? 1 : 2, trust.Calls);
    }

    [Fact]
    public async Task CallerCancellationInterruptsHungTrustWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthorityIssuancePrincipal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trust = new TrustSource((_, _) => { entered.TrySetResult(); return new ValueTask<AuthorityIssuancePrincipal?>(release.Task); });
        var proposal = Proposal();
        using var cancellation = new CancellationTokenSource();
        var call = Authorizer(trust, new OperationPolicy(_ => Permit(Actor)))
            .AuthorizeAsync(Request(proposal.Proposed), cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(2)));
        release.TrySetResult(Principal(proposal.Proposed, proposal.Ceiling));
    }

    [Fact]
    public async Task EvaluationBudgetBoundsHungTrustProvider()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthorityIssuancePrincipal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trust = new TrustSource((_, _) => { entered.TrySetResult(); return new ValueTask<AuthorityIssuancePrincipal?>(release.Task); });
        var proposal = Proposal();
        var authorizer = Authorizer(trust, new OperationPolicy(_ => Permit(Actor)), TimeProvider.System,
            maximumEvaluationDuration: TimeSpan.FromMilliseconds(50));

        var call = authorizer.AuthorizeAsync(Request(proposal.Proposed)).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult(Principal(proposal.Proposed, proposal.Ceiling));

        Assert.Equal(AuthorityStatus.Deny, result.Status);
    }

    private static BoundedAuthorityIssuanceAuthorizer Authorizer(IAuthorityIssuanceTrustSource trust,
        IAuthorityStoreAuthorizer policy, TimeProvider? clock = null, TimeSpan? maximumEvaluationDuration = null) =>
        new(trust, policy, clock ?? new FixedClock(InitialTime), maximumEvaluationDuration);

    private static (AuthoritySnapshot Proposed, AuthoritySnapshot Ceiling, DateTimeOffset ApprovalValidUntil) Proposal(
        DateTimeOffset? proposedValidUntil = null, DateTimeOffset? ceilingValidUntil = null,
        DateTimeOffset? approvalValidUntil = null)
    {
        var proposed = Snapshot(Target, "proposed", [Grant("proposal", "workspace", "src/app", [AuthorityAction.ReadFile])],
            validUntil: proposedValidUntil ?? InitialTime.AddHours(1));
        var ceiling = Snapshot(Issuer, "issuer-ceiling", [Grant("ceiling", "workspace", "src", [AuthorityAction.ReadFile])],
            validUntil: ceilingValidUntil ?? InitialTime.AddHours(1));
        return (proposed, ceiling, approvalValidUntil ?? InitialTime.AddMinutes(30));
    }

    private static AuthorityIssuancePrincipal Principal(AuthoritySnapshot proposed, AuthoritySnapshot ceiling,
        DateTimeOffset? approvalValidUntil = null) => new(Actor, ceiling, proposed.Identity, 4, "publish-1",
            approvalValidUntil ?? InitialTime.AddMinutes(30));

    private static AuthorityStoreAccessRequest Request(AuthoritySnapshot snapshot) =>
        new(Actor, AuthorityStoreOperation.Publish, AuthoritySubject.From(snapshot.Context), snapshot.Context,
            "publish-1", 4, snapshot);

    private static AuthoritySnapshot Snapshot(AuthenticatedAuthorityContext context, string version,
        IEnumerable<AuthorityLayer> layers, DateTimeOffset? validUntil = null, IEnumerable<AuthorityScope>? denials = null) =>
        new(context, version, layers, denials ?? [], validUntil ?? InitialTime.AddHours(1));

    private static AuthorityLayer Grant(string id, string workspace, string path, IReadOnlyList<AuthorityAction> actions,
        IReadOnlyList<AuthorityScope>? exclusions = null, DateTimeOffset? notBefore = null, DateTimeOffset? expiresAt = null) =>
        new("layer-" + id, [GrantEntry(id, workspace, path, actions, exclusions, notBefore, expiresAt)]);

    private static AuthorityGrant GrantEntry(string id, string workspace, string path, IReadOnlyList<AuthorityAction> actions,
        IReadOnlyList<AuthorityScope>? exclusions = null, DateTimeOffset? notBefore = null, DateTimeOffset? expiresAt = null) =>
        new(id, actions, new(workspace, path, AuthorityScopeKind.Subtree), exclusions ?? [],
            notBefore ?? InitialTime.AddHours(-1), expiresAt ?? InitialTime.AddMinutes(30));

    private static AuthorityStoreAuthorization Permit(AuthorityStoreActor actor) => new(AuthorityStatus.Permit, actor);

    private sealed class TrustSource(Func<int, AuthorityStoreAccessRequest, ValueTask<AuthorityIssuancePrincipal?>> resolve)
        : IAuthorityIssuanceTrustSource
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(AuthorityStoreActor presentedActor,
            AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return resolve(Interlocked.Increment(ref calls), request);
        }
        internal static TrustSource Returning(AuthorityIssuancePrincipal? principal) =>
            new((_, _) => ValueTask.FromResult(principal));
    }

    private sealed class OperationPolicy(Func<AuthorityStoreAccessRequest, AuthorityStoreAuthorization> authorize)
        : IAuthorityStoreAuthorizer
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(authorize(request));
        }
        internal static PausedPolicy Paused() => new();
    }

    private sealed class PausedPolicy : IAuthorityStoreAuthorizer
    {
        private int calls;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls => Volatile.Read(ref calls);
        internal void Release() => release.TrySetResult();
        public async ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Permit(request.Actor);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
