using System.Collections.Concurrent;
using System.Threading.Channels;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class BoundedAuthorityRequestAuthorizerTests
{
    private static readonly AuthorityRequest Request = new(new("tenant", "subject", "run", "revision", "fence"),
        AuthorityAction.ReadFile, "workspace", "src/file.cs", "first");

    [Fact]
    public async Task ActiveAndQueuedLimitsAreIndependentAndQueueIsFifo()
    {
        var inner = new ControlledAuthorizer();
        var bounded = new BoundedAuthorityRequestAuthorizer(inner, 2, 2, TimeSpan.FromSeconds(10));
        var first = bounded.AuthorizeAsync(Request).AsTask();
        var second = bounded.AuthorizeAsync(WithId("second")).AsTask();
        Assert.Equal("first", await inner.NextEntry());
        Assert.Equal("second", await inner.NextEntry());
        var third = bounded.AuthorizeAsync(WithId("third")).AsTask();
        var fourth = bounded.AuthorizeAsync(WithId("fourth")).AsTask();
        var overflow = await bounded.AuthorizeAsync(WithId("overflow"));
        Assert.Equal(AuthorityStatus.Unavailable, overflow.Status);
        Assert.Equal(2, inner.Calls);
        inner.Release("first");
        Assert.True((await first).IsAuthorized);
        Assert.Equal("third", await inner.NextEntry());
        inner.Release("second");
        Assert.True((await second).IsAuthorized);
        Assert.Equal("fourth", await inner.NextEntry());
        inner.Release("third");
        inner.Release("fourth");
        Assert.All(await Task.WhenAll(third, fourth), result => Assert.True(result.IsAuthorized));
        Assert.Equal(2, inner.MaximumActive);
        Assert.DoesNotContain(inner.Tokens, token => token.CanBeCanceled);
    }

    [Fact]
    public async Task QueueTimeoutDoesNotCallInnerAndFreesQueueCapacity()
    {
        var inner = new ControlledAuthorizer();
        var clock = new ManualClock();
        var bounded = new BoundedAuthorityRequestAuthorizer(inner, 1, 1, TimeSpan.FromSeconds(1), clock);
        var first = bounded.AuthorizeAsync(Request).AsTask();
        Assert.Equal("first", await inner.NextEntry());
        var queued = bounded.AuthorizeAsync(WithId("timeout")).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        var timeout = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AuthorityStatus.Unavailable, timeout.Status);
        Assert.Equal(1, inner.Calls);
        var replacement = bounded.AuthorizeAsync(WithId("replacement")).AsTask();
        Assert.False(replacement.IsCompleted);
        inner.Release("first");
        Assert.True((await first).IsAuthorized);
        Assert.Equal("replacement", await inner.NextEntry());
        inner.Release("replacement");
        Assert.True((await replacement).IsAuthorized);
    }

    [Fact]
    public async Task ActiveCallerCancelsPromptlyButSlotBelongsToUnderlyingWork()
    {
        var inner = new ControlledAuthorizer();
        var bounded = new BoundedAuthorityRequestAuthorizer(inner, 1, 1, TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        var first = bounded.AuthorizeAsync(Request, caller.Token).AsTask();
        Assert.Equal("first", await inner.NextEntry());
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        var queued = bounded.AuthorizeAsync(WithId("queued")).AsTask();
        Assert.False(queued.IsCompleted);
        Assert.Equal(AuthorityStatus.Unavailable, (await bounded.AuthorizeAsync(WithId("overflow"))).Status);
        Assert.Equal(1, inner.Calls);
        inner.Release("first");
        Assert.Equal("queued", await inner.NextEntry());
        inner.Release("queued");
        Assert.True((await queued).IsAuthorized);
        Assert.Equal(1, inner.MaximumActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentAuthorizerNativeSourceOrRecorderRetainsSlotAfterCallerLeaves(bool blockSource)
    {
        var services = new CurrentServices(blockSource);
        var current = new CurrentAuthorityRequestAuthorizer(services, services, services);
        var bounded = new BoundedAuthorityRequestAuthorizer(current, 1, 1, TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        var first = bounded.AuthorizeAsync(Request, caller.Token).AsTask();
        await services.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        var queued = bounded.AuthorizeAsync(WithId("queued")).AsTask();
        Assert.False(queued.IsCompleted);
        Assert.Equal(AuthorityStatus.Unavailable, (await bounded.AuthorizeAsync(WithId("overflow"))).Status);
        Assert.Equal(1, services.SourceCalls);
        services.Release.TrySetResult();
        Assert.True((await queued.WaitAsync(TimeSpan.FromSeconds(5))).IsAuthorized);
        Assert.Equal(2, services.SourceCalls);
        Assert.DoesNotContain(services.Tokens, token => token.CanBeCanceled);
    }

    [Fact]
    public async Task CancellingQueuedCallerRemovesItWithoutInvokingInner()
    {
        var inner = new ControlledAuthorizer();
        var bounded = new BoundedAuthorityRequestAuthorizer(inner, 1, 1, TimeSpan.FromSeconds(10));
        var first = bounded.AuthorizeAsync(Request).AsTask();
        Assert.Equal("first", await inner.NextEntry());
        using var caller = new CancellationTokenSource();
        var cancelled = bounded.AuthorizeAsync(WithId("cancelled"), caller.Token).AsTask();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var replacement = bounded.AuthorizeAsync(WithId("replacement")).AsTask();
        Assert.False(replacement.IsCompleted);
        inner.Release("first");
        Assert.True((await first).IsAuthorized);
        Assert.Equal("replacement", await inner.NextEntry());
        inner.Release("replacement");
        Assert.True((await replacement).IsAuthorized);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CancellationRacingSlotHandoffNeverLeaksOrDuplicatesCapacity()
    {
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var inner = new ControlledAuthorizer(immediateAfterFirst: true);
            var bounded = new BoundedAuthorityRequestAuthorizer(inner, 1, 1, TimeSpan.FromSeconds(10));
            var first = bounded.AuthorizeAsync(Request).AsTask();
            Assert.Equal("first", await inner.NextEntry());
            using var caller = new CancellationTokenSource();
            var queued = bounded.AuthorizeAsync(WithId("queued"), caller.Token).AsTask();
            await Task.WhenAll(Task.Run(caller.Cancel), Task.Run(() => inner.Release("first")));
            Assert.True((await first).IsAuthorized);
            try { Assert.True((await queued).IsAuthorized); }
            catch (OperationCanceledException) { }
            Assert.True((await bounded.AuthorizeAsync(WithId("probe")).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsAuthorized);
            Assert.Equal(1, inner.MaximumActive);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong-request")]
    [InlineData("unknown-status")]
    [InlineData("bare-permit")]
    [InlineData("missing-evidence")]
    [InlineData("malformed-decision")]
    [InlineData("contradictory-denial")]
    [InlineData("unknown-decision")]
    [InlineData("throw")]
    [InlineData("provider-cancel")]
    public async Task InvalidOrFailedInnerCannotPermitAndSlotCanBeReused(string mode)
    {
        var inner = new FunctionAuthorizer(request => mode switch
        {
            "null" => null!,
            "wrong-request" => Permit(request with { RequestIdentity = "other" }),
            "unknown-status" => new(request, (AuthorityStatus)99),
            "bare-permit" => new(request, AuthorityStatus.Permit),
            "missing-evidence" => Permit(request) with { EvidenceRecorded = false },
            "malformed-decision" => Permit(request) with { Decision = Decision() with { SnapshotIdentity = "bad" } },
            "contradictory-denial" => Permit(request) with { Status = AuthorityStatus.Deny },
            "unknown-decision" => Permit(request) with { Decision = Decision() with { Status = (AuthorityStatus)99 } },
            "throw" => throw new InvalidOperationException(),
            _ => throw new OperationCanceledException()
        });
        var bounded = new BoundedAuthorityRequestAuthorizer(inner, 1, 0, TimeSpan.FromSeconds(1));
        Assert.Equal(AuthorityStatus.Unavailable, (await bounded.AuthorizeAsync(Request)).Status);
        inner.Function = Permit;
        Assert.True((await bounded.AuthorizeAsync(WithId("next"))).IsAuthorized);
    }

    [Theory]
    [InlineData(AuthorityStatus.Deny)]
    [InlineData(AuthorityStatus.Unavailable)]
    public async Task NonPermittingResultsRemainUnchanged(AuthorityStatus status)
    {
        var result = new AuthorityRequestAuthorization(Request, status);
        var bounded = new BoundedAuthorityRequestAuthorizer(new FunctionAuthorizer(_ => result), 1, 0, TimeSpan.FromSeconds(1));
        Assert.Same(result, await bounded.AuthorizeAsync(Request));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("action")]
    [InlineData("context")]
    [InlineData("path")]
    [InlineData("identity")]
    public async Task InvalidInputFailsBeforeProviderActivation(string mode)
    {
        var inner = new FunctionAuthorizer(Permit);
        var bounded = new BoundedAuthorityRequestAuthorizer(inner, 1, 0, TimeSpan.FromSeconds(1));
        var request = mode switch
        {
            "null" => null!,
            "action" => Request with { Action = (AuthorityAction)999 },
            "context" => Request with { Context = Request.Context with { TenantId = "" } },
            "path" => Request with { RelativePath = "../secret" },
            _ => Request with { RequestIdentity = "" }
        };
        Assert.Equal(AuthorityStatus.Deny, (await bounded.AuthorizeAsync(request)).Status);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task PreCancelledCallDoesNotActivateProvider()
    {
        var inner = new FunctionAuthorizer(Permit);
        var bounded = new BoundedAuthorityRequestAuthorizer(inner, 1, 0, TimeSpan.FromSeconds(1));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bounded.AuthorizeAsync(Request, caller.Token).AsTask());
        Assert.Equal(0, inner.Calls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(257, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 4097)]
    public void InvalidCapacityIsRejected(int executing, int queued) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedAuthorityRequestAuthorizer(new FunctionAuthorizer(Permit), executing, queued, TimeSpan.FromSeconds(1)));

    [Fact]
    public void InvalidTimeoutAndNullProviderAreRejected()
    {
        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromDays(1).Add(TimeSpan.FromTicks(1)) })
            Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedAuthorityRequestAuthorizer(new FunctionAuthorizer(Permit), 1, 0, timeout));
        Assert.Throws<ArgumentNullException>(() => new BoundedAuthorityRequestAuthorizer(null!, 1, 0, TimeSpan.FromSeconds(1)));
    }

    private static AuthorityRequest WithId(string id) => Request with { RequestIdentity = id };
    private static AuthorityDecision Decision() => new(AuthorityStatus.Permit, "allowed", "version", "test-policy", new string('a', 64));
    private static AuthorityRequestAuthorization Permit(AuthorityRequest request) => new(request, AuthorityStatus.Permit, Decision(), true);

    private sealed class FunctionAuthorizer(Func<AuthorityRequest, AuthorityRequestAuthorization> function) : IAuthorityRequestAuthorizer
    {
        public Func<AuthorityRequest, AuthorityRequestAuthorization> Function { get; set; } = function;
        public int Calls { get; private set; }
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(Function(request)); }
    }

    private sealed class ControlledAuthorizer(bool immediateAfterFirst = false) : IAuthorityRequestAuthorizer
    {
        private readonly Channel<string> entries = Channel.CreateUnbounded<string>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<AuthorityRequestAuthorization>> results = new();
        private int calls;
        private int active;
        private int maximumActive;
        public int Calls => Volatile.Read(ref calls);
        public int MaximumActive => Volatile.Read(ref maximumActive);
        public ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
        {
            Tokens.Enqueue(cancellationToken);
            var call = Interlocked.Increment(ref calls);
            var concurrent = Interlocked.Increment(ref active);
            int prior;
            while (concurrent > (prior = Volatile.Read(ref maximumActive)) &&
                Interlocked.CompareExchange(ref maximumActive, concurrent, prior) != prior) { }
            try
            {
                var result = new TaskCompletionSource<AuthorityRequestAuthorization>(TaskCreationOptions.RunContinuationsAsynchronously);
                results[request.RequestIdentity] = result;
                entries.Writer.TryWrite(request.RequestIdentity);
                if (call > 1 && immediateAfterFirst) return Permit(request);
                return await result.Task.ConfigureAwait(false);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task<string> NextEntry() => entries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void Release(string id) => results[id].TrySetResult(Permit(WithId(id)));
    }

    private sealed class CurrentServices(bool blockSource) : IAuthoritySnapshotSource, IAuthorityEvaluator, IAuthorityDecisionRecorder
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
        private int sourceCalls;
        public int SourceCalls => Volatile.Read(ref sourceCalls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        public async ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default)
        {
            Tokens.Enqueue(cancellationToken);
            Interlocked.Increment(ref sourceCalls);
            if (blockSource) { Entered.TrySetResult(); await Release.Task.ConfigureAwait(false); }
            return new(context, "v1", [new("layer", [new("grant", [AuthorityAction.ReadFile],
                new("workspace", "src", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddHours(1))])], [], now.AddHours(1));
        }
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset evaluatedAt) =>
            new(AuthorityStatus.Permit, "allowed", snapshot.Version, "test-policy", snapshot.Identity);
        public async ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            Tokens.Enqueue(cancellationToken);
            if (!blockSource) { Entered.TrySetResult(); await Release.Task.ConfigureAwait(false); }
            return true;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private DateTimeOffset now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                var timer = new Timer(this, callback, state);
                timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(TimeSpan duration)
        {
            Timer[] due;
            lock (gate)
            {
                now += duration;
                due = timers.Where(timer => timer.Deadline <= now).ToArray();
                foreach (var timer in due) timer.Deadline = DateTimeOffset.MaxValue;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class Timer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal DateTimeOffset Deadline = DateTimeOffset.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (disposed) return false;
                    if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Test timers are one-shot.");
                    Deadline = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now.Add(dueTime);
                    return true;
                }
            }
            public void Dispose() { lock (owner.gate) { disposed = true; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            internal void Fire() => callback(state);
        }
    }
}
