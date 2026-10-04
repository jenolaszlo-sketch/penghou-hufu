using Penghou.Hufu.Cedar;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class AuthorityExplanationCedarTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");
    private static readonly AuthorityRequest Request = new(Context, AuthorityAction.ReadFile, "workspace", "src/private/file.txt", "invocation");
    private static AuthorityScope Scope(string path) => new("workspace", path, AuthorityScopeKind.Subtree);
    private static AuthorityGrant Grant(string id, AuthorityScope scope, IReadOnlyList<AuthorityScope>? exclusions = null,
        IReadOnlyList<AuthorityAction>? actions = null, DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new(id, actions ?? [AuthorityAction.ReadFile], scope, exclusions ?? [], start ?? Now.AddHours(-1), end ?? Now.AddHours(1));
    private static AuthoritySnapshot Snapshot(IEnumerable<AuthorityLayer> layers, IEnumerable<AuthorityScope>? denials = null,
        DateTimeOffset? expiry = null) => new(Context, "snapshot-v1", layers, denials ?? [], expiry ?? Now.AddHours(1));

    [Fact]
    public void RealCedarCaptureBindsVersionsAndKeepsGrantFactsDistinctFromLayerDecision()
    {
        var snapshot = Snapshot([new("parent", [Grant("source", Scope("src"))]), new("activity", [])]);
        var evaluator = new CedarAuthorityEvaluator();
        var explanation = evaluator.EvaluateExplained(snapshot, Request, Now);

        Assert.Equal(evaluator.Evaluate(snapshot, Request, Now), explanation.Decision);
        Assert.Equal(AuthorityStatus.Deny, explanation.Decision.Status);
        Assert.Equal(AuthorityExplanationReason.LayerDenied, explanation.Reason);
        Assert.Equal(AuthorityExplanationCoverage.CapturedLayerOutcomes, explanation.Coverage);
        Assert.Matches("^[a-f0-9]{64}$", explanation.Identity);
        Assert.Matches("^[a-f0-9]{64}$", explanation.SchemaIdentity!);
        Assert.Matches("^[a-f0-9]{64}$", explanation.EntityIdentity!);
        var parent = explanation.Layers[0];
        Assert.Equal(AuthorityStatus.Permit, parent.Outcome!.Status);
        Assert.Matches("^[a-f0-9]{64}$", parent.Outcome.PolicyIdentity!);
        var grant = Assert.Single(parent.Grants);
        Assert.True(grant.ActionMatches);
        Assert.True(grant.ScopeMatches);
        Assert.Equal(AuthorityGrantValidity.Active, grant.Validity);
        Assert.Equal(AuthorityStatus.Deny, explanation.Layers[1].Outcome!.Status);
        Assert.Empty(explanation.Layers[1].Grants);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExclusionAndAlternativeGrantRemainSeparateFromMandatoryDenial(bool mandatory)
    {
        var excluded = Grant("broad", Scope("src"), [Scope("src/private")]);
        var alternate = Grant("alternate", Scope("src/private"));
        var snapshot = Snapshot([new("run", [excluded, alternate])], mandatory ? [Scope("src/private")] : []);

        var explanation = new CedarAuthorityEvaluator().EvaluateExplained(snapshot, Request, Now);

        Assert.Equal(mandatory ? AuthorityStatus.Deny : AuthorityStatus.Permit, explanation.Decision.Status);
        Assert.Single(explanation.Layers[0].Grants[0].MatchingExclusions);
        Assert.Empty(explanation.Layers[0].Grants[1].MatchingExclusions);
        Assert.Equal(mandatory ? 1 : 0, explanation.MatchingMandatoryDenials.Count);
        Assert.Equal(explanation.Decision.Status, explanation.Layers[0].Outcome!.Status);
    }

    [Theory]
    [InlineData("future", AuthorityGrantValidity.NotYetValid)]
    [InlineData("expired", AuthorityGrantValidity.Expired)]
    public void InactiveGrantExplainsItsExactInterval(string state, AuthorityGrantValidity validity)
    {
        var grant = Grant("grant", Scope("src"), start: state == "future" ? Now.AddMinutes(1) : Now.AddHours(-1),
            end: state == "future" ? Now.AddHours(1) : Now);
        var explanation = new CedarAuthorityEvaluator().EvaluateExplained(Snapshot([new("run", [grant])]), Request, Now);

        Assert.Equal(AuthorityStatus.Deny, explanation.Decision.Status);
        var fact = Assert.Single(explanation.Layers[0].Grants);
        Assert.Equal(validity, fact.Validity);
        Assert.Equal(grant.NotBefore, fact.NotBefore);
        Assert.Equal(grant.ExpiresAt, fact.ExpiresAt);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("workspace")]
    [InlineData("path")]
    public void UnsupportedGrantForThisRequestDoesNotInventCoverage(string mismatch)
    {
        var scope = mismatch switch
        {
            "workspace" => new AuthorityScope("other", "src", AuthorityScopeKind.Subtree),
            "path" => Scope("src/private-other"),
            _ => Scope("src")
        };
        var actions = mismatch == "action" ? new[] { AuthorityAction.PatchFile } : [AuthorityAction.ReadFile];
        var explanation = new CedarAuthorityEvaluator().EvaluateExplained(Snapshot([new("run", [Grant("grant", scope, actions: actions)])]), Request, Now);

        Assert.Equal(AuthorityStatus.Deny, explanation.Decision.Status);
        var fact = Assert.Single(explanation.Layers[0].Grants);
        Assert.Equal(mismatch != "action", fact.ActionMatches);
        Assert.Equal(mismatch == "action", fact.ScopeMatches);
    }

    [Fact]
    public void ExpiredSnapshotPreservesActualDenialAndExplicitPartialCoverage()
    {
        var snapshot = Snapshot([new("run", [Grant("grant", Scope("src"))])], expiry: Now);
        var explanation = new CedarAuthorityEvaluator().EvaluateExplained(snapshot, Request, Now);

        Assert.Equal(AuthorityStatus.Deny, explanation.Decision.Status);
        Assert.Equal(AuthorityExplanationReason.SnapshotExpired, explanation.Reason);
        Assert.Equal(Now, explanation.SnapshotValidUntil);
        Assert.Equal(AuthorityExplanationCoverage.SnapshotFactsOnly, explanation.Coverage);
        Assert.Null(explanation.Layers[0].Outcome);
    }

    [Fact]
    public void SameExactCaptureIsDeterministicAndTimeNormalizationPreservesIdentity()
    {
        var snapshot = Snapshot([new("run", [Grant("grant", Scope("src"))])]);
        var evaluator = new CedarAuthorityEvaluator();
        var first = evaluator.EvaluateExplained(snapshot, Request, Now);
        var again = evaluator.EvaluateExplained(snapshot, Request, Now.ToOffset(TimeSpan.FromHours(8)));

        Assert.Equal(first.Identity, again.Identity);
        Assert.Equal(first.Decision, again.Decision);
        Assert.Equal(TimeSpan.Zero, again.EvaluatedAt.Offset);
    }

    [Fact]
    public void CrossContextRequestCannotBecomeAnExplanationForTheOtherSnapshot()
    {
        var snapshot = Snapshot([new("run", [Grant("grant", Scope("src"))])]);
        Assert.Throws<ArgumentException>(() => new CedarAuthorityEvaluator().EvaluateExplained(
            snapshot, Request with { Context = Context with { TenantId = "other-tenant" } }, Now));
    }
}
