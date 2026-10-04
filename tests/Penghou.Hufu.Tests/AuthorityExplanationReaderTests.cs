using System.Text.Json;
using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>Host-trusted test fixtures for explanation capture and disclosure; actor fields never create permission.</summary>
public sealed class AuthorityExplanationReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthorityStoreActor Viewer = new("tenant", "auditor", "session-1");
    private static readonly AuthorityStoreActor OtherViewer = new("tenant", "other", "session-2");
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject-private", "run-private", "r7", "f4");

    [Fact]
    public async Task SummarySerializesOnlyOutcomeAndCannotLeakPathsIdsReasonsOrCounts()
    {
        var explanation = Explain(reasonCode: "secret-policy-denial-reason");
        var read = await Reader(Allow(explanation, AuthorityExplanationDetailLevel.Summary))
            .ReadAsync(Viewer, explanation);

        Assert.Equal(AuthorityExplanationReadStatus.Disclosed, read.Status);
        var projection = Assert.IsType<AuthorityExplanationProjection>(read.Projection);
        Assert.Null(projection.Details);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(projection));
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.True(json.RootElement.TryGetProperty("DecisionStatus", out _));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("Details").ValueKind);
        var serialized = json.RootElement.GetRawText();
        Assert.DoesNotContain("src/private/secret.txt", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("run-private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("grant-private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-policy-denial-reason", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Reason", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Count", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailedProjectionContainsOnlyTypedFactsAndMapsUnknownReasonToUnknown()
    {
        const string rawReason = "cedar-secret-native-message-that-must-not-escape";
        var explanation = Explain(reasonCode: rawReason, status: AuthorityStatus.Deny,
            outcome: AuthorityStatus.Deny, policyErrors: true);
        var read = await Reader(Allow(explanation, AuthorityExplanationDetailLevel.Detailed))
            .ReadAsync(Viewer, explanation);

        Assert.Equal(AuthorityExplanationReadStatus.Disclosed, read.Status);
        var details = Assert.IsType<AuthorityExplanationDetails>(Assert.IsType<AuthorityExplanationProjection>(read.Projection).Details);
        Assert.Equal(AuthorityExplanationReason.Unknown, details.Reason);
        Assert.Equal(AuthorityExplanationCoverage.CapturedLayerOutcomes, details.Coverage);
        Assert.Equal(Now.AddHours(1), details.SnapshotValidUntil);
        var layer = Assert.Single(details.Layers);
        Assert.Equal(AuthorityStatus.Deny, layer.Outcome!.Status);
        Assert.True(layer.Outcome.HadPolicyErrors);
        var grant = Assert.Single(layer.Grants);
        Assert.Equal("grant-private", grant.GrantId);
        Assert.Equal(Now.AddHours(-1), grant.NotBefore);
        Assert.Equal(Now.AddMinutes(30), grant.ExpiresAt);
        Assert.True(grant.ActionMatches);
        Assert.True(grant.ScopeMatches);
        Assert.Single(grant.MatchingExclusions);
        Assert.Single(details.MatchingMandatoryDenials);

        var serialized = JsonSerializer.Serialize(details);
        Assert.DoesNotContain(rawReason, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Raw", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Cedar", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOrMismatchedViewerAndExplanationBindingNeverDiscloses()
    {
        var explanation = Explain();
        var policy = Allow(explanation, AuthorityExplanationDetailLevel.Detailed);
        var reader = Reader(policy);

        Assert.Equal(AuthorityExplanationReadStatus.Denied,
            (await reader.ReadAsync(new("tenant", "", "session"), explanation)).Status);
        Assert.Equal(AuthorityExplanationReadStatus.Denied,
            (await reader.ReadAsync(new("other-tenant", Viewer.ActorId, Viewer.SessionId), explanation)).Status);
        Assert.Equal(0, policy.Calls);

        var wrongActor = new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
            new(AuthorityStatus.Permit, OtherViewer, current.Identity, AuthorityExplanationDetailLevel.Detailed, Now.AddMinutes(1))));
        var wrongIdentity = new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
            new(AuthorityStatus.Permit, Viewer, new string('a', 64), AuthorityExplanationDetailLevel.Detailed, Now.AddMinutes(1))));
        var wrongSession = new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
            new(AuthorityStatus.Permit, Viewer with { SessionId = "different-session" }, current.Identity,
                AuthorityExplanationDetailLevel.Detailed, Now.AddMinutes(1))));
        var missingActor = new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
            new(AuthorityStatus.Permit, null, current.Identity, AuthorityExplanationDetailLevel.Detailed, Now.AddMinutes(1))));
        var missingExpiry = new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
            new(AuthorityStatus.Permit, Viewer, current.Identity, AuthorityExplanationDetailLevel.Detailed, null)));
        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, (await Reader(wrongActor).ReadAsync(Viewer, explanation)).Status);
        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, (await Reader(wrongIdentity).ReadAsync(Viewer, explanation)).Status);
        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, (await Reader(wrongSession).ReadAsync(Viewer, explanation)).Status);
        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, (await Reader(missingActor).ReadAsync(Viewer, explanation)).Status);
        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, (await Reader(missingExpiry).ReadAsync(Viewer, explanation)).Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unknown-status")]
    [InlineData("unavailable")]
    [InlineData("expired")]
    [InlineData("unknown-detail")]
    [InlineData("throws")]
    public async Task MissingMalformedExpiredOrFailedAccessReturnsNoProjection(string mode)
    {
        var explanation = Explain();
        var policy = mode switch
        {
            "null" => new TestAccessPolicy((_, _, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(null)),
            "unknown-status" => new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
                new((AuthorityStatus)int.MaxValue, Viewer, current.Identity, AuthorityExplanationDetailLevel.Detailed, Now.AddMinutes(1)))),
            "unavailable" => new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
                new(AuthorityStatus.Unavailable, Viewer, current.Identity, AuthorityExplanationDetailLevel.Detailed, Now.AddMinutes(1)))),
            "expired" => new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
                new(AuthorityStatus.Permit, Viewer, current.Identity, AuthorityExplanationDetailLevel.Detailed, Now))),
            "unknown-detail" => new TestAccessPolicy((_, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
                new(AuthorityStatus.Permit, Viewer, current.Identity, (AuthorityExplanationDetailLevel)int.MaxValue, Now.AddMinutes(1)))),
            _ => new TestAccessPolicy((_, _, _) => throw new InvalidOperationException("trusted policy failure"))
        };

        var result = await Reader(policy).ReadAsync(Viewer, explanation);

        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, result.Status);
        Assert.Null(result.Projection);
    }

    [Fact]
    public async Task ExplicitAccessDenialReturnsDeniedWithoutProjection()
    {
        var explanation = Explain();
        var policy = new TestAccessPolicy((_, _, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(new(AuthorityStatus.Deny)));

        var result = await Reader(policy).ReadAsync(Viewer, explanation);

        Assert.Equal(AuthorityExplanationReadStatus.Denied, result.Status);
        Assert.Null(result.Projection);
    }

    [Theory]
    [InlineData("valid-until")]
    [InlineData("clock-rewind")]
    public async Task AccessExpiryAndClockDriftAcrossPolicyAwaitBlockProjection(string mode)
    {
        var explanation = Explain();
        var clock = new MutableClock(Now);
        var policy = new TestAccessPolicy((_, current, _) =>
        {
            clock.Now = mode == "clock-rewind" ? Now.AddTicks(-1) : Now.AddSeconds(2);
            return ValueTask.FromResult<AuthorityExplanationAccess?>(new(AuthorityStatus.Permit, Viewer,
                current.Identity, AuthorityExplanationDetailLevel.Detailed,
                mode == "valid-until" ? Now.AddSeconds(1) : Now.AddMinutes(3)));
        });

        var result = await Reader(policy, clock).ReadAsync(Viewer, explanation);

        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, result.Status);
        Assert.Null(result.Projection);
    }

    [Fact]
    public async Task CallerCancellationInterruptsHungAccessPolicy()
    {
        var explanation = Explain();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthorityExplanationAccess?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new TestAccessPolicy((_, _, _) =>
        {
            entered.TrySetResult();
            return new(release.Task);
        });
        using var cancellation = new CancellationTokenSource();
        var call = Reader(policy).ReadAsync(Viewer, explanation, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(2)));
        release.TrySetResult(AllowAccess(explanation));
    }

    [Fact]
    public async Task PreCancelledReaderDoesNotActivateAccessPolicy()
    {
        var explanation = Explain();
        var policy = Allow(explanation, AuthorityExplanationDetailLevel.Detailed);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Reader(policy).ReadAsync(Viewer, explanation, cancellation.Token).AsTask());

        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task FiniteBudgetBoundsHungAccessPolicy()
    {
        var explanation = Explain();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthorityExplanationAccess?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new TestAccessPolicy((_, _, _) =>
        {
            entered.TrySetResult();
            return new(release.Task);
        });
        var reader = new AuthorityExplanationReader(policy, TimeProvider.System, TimeSpan.FromMilliseconds(50));

        var call = reader.ReadAsync(Viewer, explanation).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult(AllowAccess(explanation));

        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, result.Status);
        Assert.Null(result.Projection);
    }

    [Fact]
    public async Task OversizedDetailedProjectionReturnsUnavailableWhileSummaryRemainsAvailable()
    {
        var wideWorkspace = new string('界', 256);
        var longPath = new string('界', 512);
        var grants = Enumerable.Range(0, 128).Select(i => new AuthorityGrant(
            new string('界', 250) + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            [AuthorityAction.ReadFile], new(wideWorkspace, longPath, AuthorityScopeKind.Subtree),
            [new(wideWorkspace, longPath, AuthorityScopeKind.Subtree)], Now.AddHours(-1), Now.AddMinutes(30))).ToArray();
        var snapshot = new AuthoritySnapshot(Context, "large-snapshot", [new("large-layer", grants)], [], Now.AddHours(1));
        var request = new AuthorityRequest(Context, AuthorityAction.ReadFile, wideWorkspace, longPath, "large-request");
        var explanation = new AuthorityDecisionExplanation(snapshot, request,
            new(AuthorityStatus.Deny, "authority.layer-denied", snapshot.Version, "hufu-test-evaluator-v1", snapshot.Identity),
            Now, [new("large-layer", AuthorityStatus.Deny)]);

        var detailed = await Reader(Allow(explanation, AuthorityExplanationDetailLevel.Detailed)).ReadAsync(Viewer, explanation);
        Assert.Equal(AuthorityExplanationReadStatus.Unavailable, detailed.Status);
        Assert.Null(detailed.Projection);

        var summary = await Reader(Allow(explanation, AuthorityExplanationDetailLevel.Summary)).ReadAsync(Viewer, explanation);
        Assert.Equal(AuthorityExplanationReadStatus.Disclosed, summary.Status);
        Assert.Null(summary.Projection!.Details);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(summary.Projection).Length < AuthorityExplanationReader.MaximumProjectionBytes);
    }

    [Fact]
    public void IdentityChangesWithExactRequestTimeDecisionLayerAndSchemaCapture()
    {
        var baseline = Explain();
        var changedRequest = Explain(request: Request("src/private/other.txt"));
        var changedTime = Explain(evaluatedAt: Now.AddTicks(1));
        var changedDecision = Explain(reasonCode: "cedar.policy-evaluation-error", status: AuthorityStatus.Deny,
            outcome: AuthorityStatus.Deny, policyErrors: true);
        var changedLayer = Explain(layerId: "other-layer");
        var changedSchema = Explain(schemaIdentity: new string('b', 64));
        var changedEntity = Explain(entityIdentity: new string('c', 64));

        Assert.Equal(baseline.Identity, Explain().Identity);
        Assert.NotEqual(baseline.Identity, changedRequest.Identity);
        Assert.NotEqual(baseline.Identity, changedTime.Identity);
        Assert.NotEqual(baseline.Identity, changedDecision.Identity);
        Assert.NotEqual(baseline.Identity, changedLayer.Identity);
        Assert.NotEqual(baseline.Identity, changedSchema.Identity);
        Assert.NotEqual(baseline.Identity, changedEntity.Identity);
    }

    [Fact]
    public void LayerOutcomeCaptureMustBeCompleteOrderedAndConsistent()
    {
        var snapshot = Snapshot([Layer("parent", Grant("parent-grant")), Layer("child", Grant("child-grant"))]);
        var request = Request();
        var noOutcomes = MakeExplanation(snapshot, request, AuthorityStatus.Deny, "authority.layer-denied", Now, []);
        Assert.Equal(AuthorityExplanationCoverage.SnapshotFactsOnly, noOutcomes.Coverage);

        Assert.Throws<ArgumentException>(() => MakeExplanation(snapshot, request, AuthorityStatus.Deny,
            "authority.layer-denied", Now, [new("parent", AuthorityStatus.Deny)]));
        Assert.Throws<ArgumentException>(() => MakeExplanation(snapshot, request, AuthorityStatus.Deny,
            "authority.layer-denied", Now, [new("child", AuthorityStatus.Deny), new("parent", AuthorityStatus.Deny)]));
        Assert.Throws<ArgumentException>(() => MakeExplanation(snapshot, request, AuthorityStatus.Permit,
            "authority.permitted", Now, [new("parent", AuthorityStatus.Permit), new("child", AuthorityStatus.Deny)]));
        Assert.Throws<ArgumentException>(() => MakeExplanation(snapshot, request, AuthorityStatus.Permit,
            "authority.permitted", Now, [new("parent", (AuthorityStatus)int.MaxValue), new("child", AuthorityStatus.Permit)]));

        var captured = MakeExplanation(snapshot, request, AuthorityStatus.Deny, "authority.layer-denied", Now,
            [new("parent", AuthorityStatus.Permit), new("child", AuthorityStatus.Deny)]);
        Assert.Equal(AuthorityExplanationCoverage.CapturedLayerOutcomes, captured.Coverage);

        var maximumLayers = Enumerable.Range(0, 8).Select(i => Layer("layer-" + i, Grant("grant-" + i))).ToArray();
        var boundedSnapshot = Snapshot(maximumLayers);
        var tooManyOutcomes = Enumerable.Range(0, 9).Select(i => new AuthorityLayerOutcome("layer-" + i, AuthorityStatus.Deny));
        Assert.Throws<ArgumentException>(() => MakeExplanation(boundedSnapshot, request, AuthorityStatus.Deny,
            "authority.layer-denied", Now, tooManyOutcomes));
    }

    [Fact]
    public void ExplanationFactsAndCollectionsAreImmutable()
    {
        var explanation = Explain();
        var layers = Assert.IsAssignableFrom<IList<AuthorityLayerExplanation>>(explanation.Layers);
        var grants = Assert.IsAssignableFrom<IList<AuthorityGrantExplanation>>(Assert.Single(explanation.Layers).Grants);
        var exclusions = Assert.IsAssignableFrom<IList<AuthorityScope>>(Assert.Single(grants).MatchingExclusions);
        var denials = Assert.IsAssignableFrom<IList<AuthorityScope>>(explanation.MatchingMandatoryDenials);

        Assert.True(layers.IsReadOnly);
        Assert.True(grants.IsReadOnly);
        Assert.True(exclusions.IsReadOnly);
        Assert.True(denials.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => layers.Add(Assert.Single(explanation.Layers)));
        Assert.Throws<NotSupportedException>(() => exclusions.Add(new("workspace", "other", AuthorityScopeKind.Exact)));
    }

    [Fact]
    public void ExplanationConstructionRejectsMismatchedCaptureAndInvalidHashes()
    {
        var explanation = Explain();
        var snapshot = Snapshot([Layer("layer-1", Grant("grant-private"))]);
        var request = Request();
        var goodDecision = Decision(snapshot, AuthorityStatus.Deny, "authority.layer-denied");
        Assert.Throws<ArgumentException>(() => new AuthorityDecisionExplanation(snapshot,
            request with { Context = Context with { FenceId = "other" } }, goodDecision, Now, [new("layer-1", AuthorityStatus.Deny)]));
        Assert.Throws<ArgumentException>(() => new AuthorityDecisionExplanation(snapshot, request,
            goodDecision with { SnapshotIdentity = new string('a', 64) }, Now, [new("layer-1", AuthorityStatus.Deny)]));
        Assert.Throws<ArgumentException>(() => new AuthorityDecisionExplanation(snapshot, request,
            goodDecision, Now, [new("layer-1", AuthorityStatus.Deny, "not-a-hash")]));
        Assert.Throws<ArgumentException>(() => new AuthorityDecisionExplanation(snapshot, request,
            goodDecision, Now, [new("layer-1", AuthorityStatus.Permit, HadPolicyErrors: true)]));
        Assert.NotNull(explanation);
    }

    [Fact]
    public void ReaderConstructorRejectsNullPolicyAndInvalidBudget()
    {
        var policy = Allow(Explain(), AuthorityExplanationDetailLevel.Summary);
        Assert.Throws<ArgumentNullException>(() => new AuthorityExplanationReader(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorityExplanationReader(policy,
            maximumEvaluationDuration: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorityExplanationReader(policy,
            maximumEvaluationDuration: TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(1))));
    }

    private static AuthorityExplanationReader Reader(IAuthorityExplanationAccessPolicy policy, TimeProvider? clock = null) =>
        new(policy, clock ?? new MutableClock(Now));

    // This fixture authorizes only explanation disclosure for the fixed test viewer; it never grants Hufu authority.
    private static TestAccessPolicy Allow(AuthorityDecisionExplanation explanation, AuthorityExplanationDetailLevel level) =>
        new((actor, current, _) => ValueTask.FromResult<AuthorityExplanationAccess?>(
            actor == Viewer && current.Identity == explanation.Identity
                ? new(AuthorityStatus.Permit, Viewer, explanation.Identity, level, Now.AddMinutes(5))
                : new(AuthorityStatus.Deny)));

    private static AuthorityExplanationAccess AllowAccess(AuthorityDecisionExplanation explanation) =>
        new(AuthorityStatus.Permit, Viewer, explanation.Identity, AuthorityExplanationDetailLevel.Detailed, Now.AddMinutes(5));

    private static AuthorityDecisionExplanation Explain(string reasonCode = "authority.layer-denied",
        AuthorityStatus status = AuthorityStatus.Deny, AuthorityStatus outcome = AuthorityStatus.Deny,
        DateTimeOffset? evaluatedAt = null, string layerId = "layer-1", string? schemaIdentity = null,
        string? entityIdentity = null, AuthorityRequest? request = null, bool policyErrors = false)
    {
        var snapshot = Snapshot([Layer(layerId, Grant("grant-private"))]);
        return MakeExplanation(snapshot, request ?? Request(), status, reasonCode, evaluatedAt ?? Now,
            [new(layerId, outcome, new string('d', 64), policyErrors)], schemaIdentity ?? new string('a', 64), entityIdentity ?? new string('b', 64));
    }

    private static AuthorityDecisionExplanation MakeExplanation(AuthoritySnapshot snapshot, AuthorityRequest request,
        AuthorityStatus status, string reasonCode, DateTimeOffset evaluatedAt, IEnumerable<AuthorityLayerOutcome> outcomes,
        string? schemaIdentity = null, string? entityIdentity = null) => new(snapshot, request,
            Decision(snapshot, status, reasonCode), evaluatedAt, outcomes, schemaIdentity, entityIdentity);

    private static AuthorityDecision Decision(AuthoritySnapshot snapshot, AuthorityStatus status, string reasonCode) =>
        new(status, reasonCode, snapshot.Version, "hufu-test-evaluator-v1", snapshot.Identity);

    private static AuthorityRequest Request(string path = "src/private/secret.txt") =>
        new(Context, AuthorityAction.ReadFile, "workspace-private", path, "request-private");

    private static AuthoritySnapshot Snapshot(IEnumerable<AuthorityLayer> layers) =>
        new(Context, "snapshot-v1", layers,
            [new("workspace-private", "src/private", AuthorityScopeKind.Subtree)], Now.AddHours(1));

    private static AuthorityLayer Layer(string id, AuthorityGrant grant) => new(id, [grant]);

    private static AuthorityGrant Grant(string id) => new(id, [AuthorityAction.ReadFile],
        new("workspace-private", "src", AuthorityScopeKind.Subtree),
        [new("workspace-private", "src/private", AuthorityScopeKind.Subtree)],
        Now.AddHours(-1), Now.AddMinutes(30));

    private sealed class TestAccessPolicy(Func<AuthorityStoreActor, AuthorityDecisionExplanation, CancellationToken,
        ValueTask<AuthorityExplanationAccess?>> authorize) : IAuthorityExplanationAccessPolicy
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public ValueTask<AuthorityExplanationAccess?> AuthorizeAsync(AuthorityStoreActor presentedActor,
            AuthorityDecisionExplanation explanation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return authorize(presentedActor, explanation, cancellationToken);
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
