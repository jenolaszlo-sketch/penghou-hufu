using BiscuitSharp;
using Penghou.Hufu;
using Penghou.Hufu.Biscuit;

namespace Penghou.Hufu.Biscuit.Tests;

public sealed class BiscuitProfileTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("plain text")]
    [InlineData("a quote: \" and a slash: \\")]
    [InlineData("first line\nsecond line")]
    [InlineData("carriage\rreturn and tab\there")]
    [InlineData("Unicode café ☕ 漢字 😀")]
    [InlineData("x\"); injected(\"yes")]
    public void StringLiteral_RoundTripsAsOneJoinedValueThroughPinnedBiscuitEngine(string value)
    {
        using BiscuitPrivateKey key = BiscuitPrivateKey.Generate(BiscuitKeyAlgorithm.Ed25519);
        string literal = DatalogLiteral.String(value);
        BiscuitToken token = BiscuitTokenBuilder.Create()
            .AddFact($"value({literal})")
            .Build(key);

        BiscuitAuthorizationResult result = BiscuitAuthorizer.For(token)
            .AddFact($"observed({DatalogLiteral.String(value)})")
            .AddPolicy("allow if value($value), observed($value);")
            .Authorize();

        Assert.True(result.IsAuthorized, string.Join("; ", result.Errors.Select(error => error.Message)));
        Assert.Equal(BiscuitDecision.Allow, result.Decision);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StringLiteral_RejectsUnsupportedControlsMalformedUtf16AndOversizedUtf8()
    {
        Assert.Throws<ArgumentException>(() => DatalogLiteral.String("before\u0001after"));
        Assert.Throws<ArgumentException>(() => DatalogLiteral.String("unpaired \uD800"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DatalogLiteral.String(new string('x', 2049)));
    }

    [Fact]
    public void IsSubset_RequiresInheritedExclusionWhenTheNarrowedScopeIntersectsIt()
    {
        AuthorityScope rootScope = new("workspace", "repo", AuthorityScopeKind.Subtree);
        AuthorityScope inheritedExclusion = new("workspace", "repo/private", AuthorityScopeKind.Subtree);
        AuthorityGrant parent = Grant(rootScope, [inheritedExclusion]);
        AuthorityGrant childWithoutExclusion = Grant(
            new("workspace", "repo/private/secret.txt", AuthorityScopeKind.Exact), []);

        Assert.False(BiscuitProfile.IsSubset(parent, childWithoutExclusion));

        AuthorityGrant childWithExclusion = Grant(
            childWithoutExclusion.Scope,
            [new("workspace", "repo/private/secret.txt", AuthorityScopeKind.Exact)]);
        Assert.True(BiscuitProfile.IsSubset(parent, childWithExclusion));
    }

    [Fact]
    public void IsSubset_AcceptsDisjointNarrowingAndPreservesTheExactScopeAndGrantBounds()
    {
        AuthorityGrant parent = Grant(
            new("workspace", "repo", AuthorityScopeKind.Subtree),
            [new("workspace", "repo/private", AuthorityScopeKind.Subtree)],
            [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata],
            Start,
            End);
        AuthorityGrant child = Grant(
            new("workspace", "repo/public/readme.txt", AuthorityScopeKind.Exact),
            [],
            [AuthorityAction.ReadFile],
            Start.AddDays(1),
            End.AddDays(-1));

        Assert.True(BiscuitProfile.IsSubset(parent, child));
        Assert.Equal(new AuthorityScope("workspace", "repo/public/readme.txt", AuthorityScopeKind.Exact), child.Scope);
        Assert.Equal("workspace", child.Scope.WorkspaceId);
        Assert.Equal("repo/public/readme.txt", child.Scope.RelativePath);
        Assert.Equal(AuthorityScopeKind.Exact, child.Scope.Kind);
        Assert.Equal(new[] { AuthorityAction.ReadFile }, child.Actions);
        Assert.Equal(Start.AddDays(1), child.NotBefore);
        Assert.Equal(End.AddDays(-1), child.ExpiresAt);
    }

    [Fact]
    public void IsSubset_RejectsChangingWorkspaceOrWideningActionOrValidity()
    {
        AuthorityGrant parent = Grant(
            new("workspace", "repo", AuthorityScopeKind.Subtree), [],
            [AuthorityAction.ReadFile], Start, End);

        Assert.False(BiscuitProfile.IsSubset(parent, Grant(
            new("other-workspace", "repo/public", AuthorityScopeKind.Subtree), [],
            [AuthorityAction.ReadFile], Start, End)));
        Assert.False(BiscuitProfile.IsSubset(parent, Grant(
            new("workspace", "repo/public", AuthorityScopeKind.Subtree), [],
            [AuthorityAction.ReadFile, AuthorityAction.PatchFile], Start, End)));
        Assert.False(BiscuitProfile.IsSubset(parent, Grant(
            new("workspace", "repo/public", AuthorityScopeKind.Subtree), [],
            [AuthorityAction.ReadFile], Start.AddDays(-1), End)));
        Assert.False(BiscuitProfile.IsSubset(parent, Grant(
            new("workspace", "repo/public", AuthorityScopeKind.Subtree), [],
            [AuthorityAction.ReadFile], Start, End.AddDays(1))));
    }

    [Theory]
    [InlineData(BiscuitEvaluationFailureReason.FactLimitExceeded, BiscuitFailureCode.AuthorizationBudgetExceeded)]
    [InlineData(BiscuitEvaluationFailureReason.IterationLimitExceeded, BiscuitFailureCode.AuthorizationBudgetExceeded)]
    [InlineData(BiscuitEvaluationFailureReason.TimeLimitExceeded, BiscuitFailureCode.AuthorizationBudgetExceeded)]
    [InlineData(BiscuitEvaluationFailureReason.ExpressionError, BiscuitFailureCode.AuthorizationFailure)]
    [InlineData(BiscuitEvaluationFailureReason.UnexpectedQueryResult, BiscuitFailureCode.AuthorizationFailure)]
    [InlineData(BiscuitEvaluationFailureReason.Other, BiscuitFailureCode.AuthorizationFailure)]
    [InlineData(null, BiscuitFailureCode.AuthorizationFailure)]
    public void Classify_MapsOnlyTypedBudgetReasonsToBudgetExceeded(
        BiscuitEvaluationFailureReason? reason,
        BiscuitFailureCode expected)
    {
        BiscuitAuthorizationResult result = EvaluationFailure(reason);

        Assert.Equal(expected, BiscuitProfile.Classify(result));
    }

    [Fact]
    public void Classify_OrdinaryCheckAndPolicyDenialsRemainDistinctFromRuntimeFailures()
    {
        Assert.Equal(BiscuitFailureCode.AuthorityConstraintFailed, BiscuitProfile.Classify(
            new(BiscuitDecision.Deny, [new("a check failed", Code: "failed_check")])));
        Assert.Equal(BiscuitFailureCode.AuthorityDenied, BiscuitProfile.Classify(
            new(BiscuitDecision.Deny, [new("no policy matched", Code: "no_matching_policy")])));
        Assert.Equal(BiscuitFailureCode.AuthorityDenied, BiscuitProfile.Classify(
            new(BiscuitDecision.Deny, [new("deny policy matched", Code: "deny_policy_matched")])));
        Assert.Equal(BiscuitFailureCode.AuthorityDenied, BiscuitProfile.Classify(
            new(BiscuitDecision.Deny, [new("fact limit exceeded", Code: "other")])));
    }

    [Fact]
    public void Classify_MixedBudgetAndNonBudgetEvaluationFailuresFailsAsAuthorizationFailure()
    {
        BiscuitAuthorizationResult result = new(BiscuitDecision.Deny,
        [
            new BiscuitAuthorizationError("budget", Code: "evaluation_failure")
            { EvaluationFailureReason = BiscuitEvaluationFailureReason.FactLimitExceeded },
            new BiscuitAuthorizationError("runtime", Code: "evaluation_failure")
            { EvaluationFailureReason = BiscuitEvaluationFailureReason.Other },
        ]);

        Assert.Equal(BiscuitFailureCode.AuthorizationFailure, BiscuitProfile.Classify(result));
    }

    [Fact]
    public void Envelope_SnapshotsInputAndExportBytesAndRedactsDisplay()
    {
        byte[] input = [0x01, 0x02, 0xFE, 0xFF];
        BiscuitEnvelope envelope = new("root-key", input);
        input[0] = 0x77;

        byte[] exported = envelope.GetTokenBytes();
        Assert.Equal(new byte[] { 0x01, 0x02, 0xFE, 0xFF }, exported);
        exported[1] = 0x88;
        Assert.Equal(new byte[] { 0x01, 0x02, 0xFE, 0xFF }, envelope.GetTokenBytes());
        Assert.Equal(4, envelope.TokenLength);
        Assert.Contains("redacted", envelope.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(input), envelope.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyRing_RotationUsesNewKeyAndRetainsOldPublicVerificationKey()
    {
        using var ring = new BiscuitKeyRing();
        ring.AddSigningKey("realm", "key-1", BiscuitPrivateKey.Generate());
        using IBiscuitSigningKeyLease oldLease = (await ring.AcquireSigningKeyAsync("realm"))!;
        BiscuitToken oldToken = oldLease.Build(BiscuitTokenBuilder.Create().AddFact("old_key(\"old\");"));

        ring.AddSigningKey("realm", "key-2", BiscuitPrivateKey.Generate());
        using IBiscuitSigningKeyLease newLease = (await ring.AcquireSigningKeyAsync("realm"))!;

        Assert.Equal("key-1", oldLease.KeyId);
        Assert.Equal("key-2", newLease.KeyId);
        BiscuitPublicKey? oldPublicKey = await ring.FindVerificationKeyAsync("realm", "key-1");
        Assert.NotNull(oldPublicKey);
        Assert.True(oldLease.PublicKey.Encoded.SequenceEqual(oldPublicKey.Encoded));
        Assert.Equal(oldToken, BiscuitToken.Parse(oldToken.ToBytes(), oldPublicKey));
    }

    [Fact]
    public async Task KeyRing_RetirementAndRingDisposalKeepOutstandingLeaseAliveUntilReleased()
    {
        var ring = new BiscuitKeyRing();
        ring.AddSigningKey("realm", "key-1", BiscuitPrivateKey.Generate());
        IBiscuitSigningKeyLease lease = (await ring.AcquireSigningKeyAsync("realm"))!;
        BiscuitTokenBuilder builder = BiscuitTokenBuilder.Create().AddFact("lease_alive(\"yes\");");

        ring.RetireSigningKey("realm", "key-1");
        Assert.Null(await ring.AcquireSigningKeyAsync("realm"));
        Assert.NotNull(await ring.FindVerificationKeyAsync("realm", "key-1"));
        Assert.NotNull(lease.Build(builder));

        ring.Dispose();
        Assert.Null(await ring.FindVerificationKeyAsync("realm", "key-1"));
        Assert.NotNull(lease.Build(builder));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.Build(builder));
    }

    [Fact]
    public void KeyRing_RejectedDuplicateAddLeavesTheCandidateKeyOwnedAndUsableByCaller()
    {
        using var ring = new BiscuitKeyRing();
        ring.AddSigningKey("realm", "key-1", BiscuitPrivateKey.Generate());
        using BiscuitPrivateKey candidate = BiscuitPrivateKey.Generate();

        Assert.Throws<ArgumentException>(() => ring.AddSigningKey("realm", "key-1", candidate));
        BiscuitToken token = BiscuitTokenBuilder.Create().AddFact("caller_still_owns_key(\"yes\");").Build(candidate);

        Assert.NotNull(BiscuitToken.Parse(token.ToBytes(), candidate.PublicKey));
    }

    [Fact]
    public async Task KeyRing_LeaseExposesOnlyPublicKeyInformationAndNoPrivateKeyExportMethod()
    {
        using var ring = new BiscuitKeyRing();
        ring.AddSigningKey("realm", "key-1", BiscuitPrivateKey.Generate());
        using IBiscuitSigningKeyLease lease = (await ring.AcquireSigningKeyAsync("realm"))!;

        Assert.DoesNotContain(typeof(BiscuitPrivateKey), typeof(IBiscuitSigningKeyLease)
            .GetProperties().Select(property => property.PropertyType));
        Assert.DoesNotContain(typeof(IBiscuitSigningKeyLease).GetMethods()
            .Select(method => method.Name), name => name.Contains("Export", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("redacted", lease.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static BiscuitAuthorizationResult EvaluationFailure(BiscuitEvaluationFailureReason? reason) =>
        new(BiscuitDecision.Deny,
        [new BiscuitAuthorizationError("evaluation finding", Code: "evaluation_failure")
        { EvaluationFailureReason = reason }]);

    private static AuthorityGrant Grant(
        AuthorityScope scope,
        IReadOnlyList<AuthorityScope> exclusions,
        IReadOnlyList<AuthorityAction>? actions = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null) =>
        new("grant", actions ?? [AuthorityAction.ReadFile], scope, exclusions,
            notBefore ?? Start, expiresAt ?? End);
}
