using Xunit;

namespace Penghou.Hufu.Tests;

/// <summary>
/// The AE-5A acceptance operation: current-authority acceptance with optional
/// lifecycle gating, mandatory evidence and freshness. Acceptance never confers
/// resource rights by implication.
/// </summary>
public sealed class DelegationAcceptanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");

    private static DelegationAcceptanceRequest Request() =>
        new(Context, "acceptance-key", "workspace", "provider", "g0", new string('a', 64));

    [Fact]
    public async Task CurrentAuthorityPermit_RecordsEvidence()
    {
        var recorder = new Recorder();
        var authorizer = new CurrentDelegationAcceptanceAuthorizer(Source(ValidUntil: Now.AddHours(1)),
            new Evaluator(AuthorityStatus.Permit), recorder, new Clock(Now));

        var result = await authorizer.AuthorizeAsync(Request());

        Assert.True(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Permit, result.Status);
        Assert.Equal(Request(), recorder.Request);
        Assert.NotNull(recorder.Decision);
    }

    [Fact]
    public async Task EvaluatorDenial_IsDenied()
    {
        var authorizer = new CurrentDelegationAcceptanceAuthorizer(Source(Now.AddHours(1)),
            new Evaluator(AuthorityStatus.Deny, "test.denied"), new Recorder(), new Clock(Now));
        var result = await authorizer.AuthorizeAsync(Request());
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("test.denied", result.Decision!.ReasonCode);
    }

    [Fact]
    public async Task SuspendedLifecycle_DeniesAcceptance()
    {
        var authorizer = new CurrentDelegationAcceptanceAuthorizer(Source(Now.AddHours(1)),
            new Evaluator(AuthorityStatus.Permit), new Recorder(), new Clock(Now),
            new Gate(ExecutionLifecycleVerdict.Suspended));
        var result = await authorizer.AuthorizeAsync(Request());
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Deny, result.Status);
        Assert.Equal("authority.lifecycle-suspended", result.Decision!.ReasonCode);
    }

    [Fact]
    public async Task UnavailableLifecycle_IsUnavailable()
    {
        var authorizer = new CurrentDelegationAcceptanceAuthorizer(Source(Now.AddHours(1)),
            new Evaluator(AuthorityStatus.Permit), new Recorder(), new Clock(Now),
            new Gate(ExecutionLifecycleVerdict.Unavailable));
        var result = await authorizer.AuthorizeAsync(Request());
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task EvidenceFailure_BlocksAcceptance()
    {
        var authorizer = new CurrentDelegationAcceptanceAuthorizer(Source(Now.AddHours(1)),
            new Evaluator(AuthorityStatus.Permit), new Recorder { Result = false }, new Clock(Now));
        var result = await authorizer.AuthorizeAsync(Request());
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task MissingSnapshot_IsUnavailable()
    {
        var authorizer = new CurrentDelegationAcceptanceAuthorizer(new NullSource(),
            new Evaluator(AuthorityStatus.Permit), new Recorder(), new Clock(Now));
        var result = await authorizer.AuthorizeAsync(Request());
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
    }

    [Fact]
    public void Request_RejectsMalformedDigest()
    {
        Assert.Throws<ArgumentException>(() => new DelegationAcceptanceRequest(Context, "key", "workspace",
            "provider", "g0", "not-a-digest"));
    }

    private static StubSource Source(DateTimeOffset ValidUntil) =>
        new(new AuthoritySnapshot(Context, "v1", [new AuthorityLayer("workflow", [Grant()])], [], ValidUntil));

    private static AuthorityGrant Grant() =>
        new("grant", [AuthorityAction.ReadFile], new AuthorityScope("workspace", "", AuthorityScopeKind.Subtree),
            [], Now.AddMinutes(-1), Now.AddHours(1));

    private sealed class StubSource(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<AuthoritySnapshot?>(snapshot);
    }

    private sealed class NullSource : IAuthoritySnapshotSource
    {
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<AuthoritySnapshot?>(null);
    }

    private sealed class Evaluator(AuthorityStatus status, string reason = "test.permitted") : IDelegationAcceptanceEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, DelegationAcceptanceRequest request, DateTimeOffset now) =>
            new(status, reason, snapshot.Version, "test-acceptance-evaluator-v1", snapshot.Identity);
    }

    private sealed class Recorder : IDelegationAcceptanceRecorder
    {
        public bool Result { get; set; } = true;
        public DelegationAcceptanceRequest? Request { get; private set; }
        public AuthorityDecision? Decision { get; private set; }
        public ValueTask<bool> RecordAsync(DelegationAcceptanceRequest request, AuthorityDecision decision, CancellationToken ct = default)
        {
            Request = request;
            Decision = decision;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class Gate(ExecutionLifecycleVerdict verdict) : IExecutionLifecycleGate
    {
        public ValueTask<ExecutionLifecycleCheckResult> CheckAsync(ExecutionLifecycleBinding binding, DateTimeOffset evaluatedAt, CancellationToken ct = default) =>
            ValueTask.FromResult(new ExecutionLifecycleCheckResult(verdict, null, 0));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
