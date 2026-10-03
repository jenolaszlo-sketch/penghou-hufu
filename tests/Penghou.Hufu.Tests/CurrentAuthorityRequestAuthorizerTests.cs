using Penghou.Hufu.Cedar;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class CurrentAuthorityRequestAuthorizerTests
{
    [Theory]
    [InlineData("grant-expiry")]
    [InlineData("grant-activation")]
    [InlineData("clock-rewind")]
    [InlineData("unchanged")]
    public async Task RequiredRecordingCannotReleasePermitAcrossValidityChanges(string scenario)
    {
        var instant = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var context = new AuthenticatedAuthorityContext("tenant", "subject", "run", "revision", "fence");
        var grant = new AuthorityGrant("grant", [AuthorityAction.ReadFile],
            new("workspace", "", AuthorityScopeKind.Subtree), [],
            instant.AddMinutes(-1), scenario == "grant-expiry" ? instant.AddSeconds(1) : instant.AddHours(1));
        var grants = new List<AuthorityGrant> { grant };
        if (scenario == "grant-activation")
            grants.Add(grant with { Id = "later", NotBefore = instant.AddSeconds(1) });
        var snapshot = new AuthoritySnapshot(context, "v1", [new("workflow", grants)], [], instant.AddHours(1));
        var clock = new Clock(instant);
        var recorder = new Recorder(() => clock.Now = scenario switch
        {
            "clock-rewind" => instant.AddSeconds(-1),
            "unchanged" => instant.AddMilliseconds(1),
            _ => instant.AddSeconds(2)
        });
        var authorizer = new CurrentAuthorityRequestAuthorizer(new Source(snapshot), new CedarAuthorityEvaluator(), recorder, clock);
        var request = new AuthorityRequest(context, AuthorityAction.ReadFile, "workspace", "file.txt", "request");
        var result = await authorizer.AuthorizeAsync(request);
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(scenario == "unchanged" ? AuthorityStatus.Permit : AuthorityStatus.Unavailable, result.Status);
        Assert.Equal(scenario == "unchanged", result.IsAuthorized);
    }

    private sealed class Source(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<AuthoritySnapshot?>(snapshot);
    }
    private sealed class Recorder(Action after) : IAuthorityDecisionRecorder
    {
        internal int Calls { get; private set; }
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken ct = default)
        {
            Calls++; after(); return ValueTask.FromResult(true);
        }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
