using System.Diagnostics;
using System.Diagnostics.Metrics;
using Xunit;

namespace Penghou.Hufu.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthorityTelemetryCollection
{
    public const string Name = "Authority telemetry global listeners";
}

[Collection(AuthorityTelemetryCollection.Name)]
public sealed class AuthorityTelemetryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<string?> Ambient = new();
    private static readonly AuthenticatedAuthorityContext Context = new("tenant-private", "subject-private", "run-private", "revision", "fence");

    [Fact]
    public async Task DecoratorForwardsOnceAndReturnsSameResultIncludingMalformedResults()
    {
        foreach (var returned in new AuthorityRequestAuthorization?[]
        {
            new(Request(), AuthorityStatus.Permit), // malformed permit: must still be returned verbatim
            null
        })
        {
            using var telemetry = new AuthorityTelemetry();
            var request = Request();
            var cancellation = new CancellationTokenSource();
            var calls = 0;
            AuthorityRequest? received = null;
            CancellationToken receivedToken = default;
            var authorizer = new TelemetryAuthorityRequestAuthorizer(new Stub((r, ct) =>
            {
                calls++; received = r; receivedToken = ct;
                return ValueTask.FromResult(returned!);
            }), telemetry);

            var result = await authorizer.AuthorizeAsync(request, cancellation.Token);
            Assert.Equal(1, calls);
            Assert.Same(request, received);
            Assert.Equal(cancellation.Token, receivedToken);
            Assert.Same(returned, result);
        }
    }

    [Fact]
    public async Task DecoratorPreservesThrownExceptionAndCallerCancellation()
    {
        using var telemetry = new AuthorityTelemetry();
        var request = Request();
        var expected = new InvalidOperationException("private exception text");
        var throwing = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) => throw expected), telemetry);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () => await throwing.AuthorizeAsync(request));
        Assert.Same(expected, actual);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new OperationCanceledException("private cancellation text", cancellation.Token);
        var cancelling = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) => throw cancelled), telemetry);
        var actualCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await cancelling.AuthorizeAsync(request, cancellation.Token));
        Assert.Same(cancelled, actualCancellation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMandatoryRecorderCannotBeOverriddenByTelemetry(bool throws)
    {
        using var telemetry = new AuthorityTelemetry();
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = CreateMeterListener((instrument, _, tags, _) =>
        {
            if (instrument.Name == AuthorityTelemetry.AuthorizationCountName)
                observed.TrySetResult(Tag(tags, "hufu.outcome"));
        });
        var source = new CountingSource();
        var evaluator = new CountingEvaluator();
        var recorder = new FailingRecorder(throws);
        var core = new CurrentAuthorityRequestAuthorizer(source, evaluator, recorder);
        var wrapped = new TelemetryAuthorityRequestAuthorizer(core, telemetry);

        var result = await wrapped.AuthorizeAsync(Request());
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, evaluator.Calls);
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(AuthorityStatus.Unavailable, result.Status);
        Assert.False(result.IsAuthorized);
        Assert.False(result.EvidenceRecorded);
        Assert.Equal("unavailable", await observed.Task.WaitAsync(Timeout));
    }

    [Theory]
    [InlineData(0, "read_file")]
    [InlineData(1, "list_directory")]
    [InlineData(2, "read_metadata")]
    [InlineData(3, "patch_file")]
    [InlineData(4, "release")]
    [InlineData(5, "write_file")]
    [InlineData(99, "unknown")]
    public async Task EveryActionMapsToAClosedActionCategory(int actionValue, string expectedAction)
    {
        using var telemetry = new AuthorityTelemetry();
        var observed = NewMeasurementSignal();
        using var listener = CreateMeterListener((instrument, value, tags, _) =>
        {
            if (instrument.Name == AuthorityTelemetry.AuthorizationDurationName)
                observed.TrySetResult((Tag(tags, "hufu.action"), Tag(tags, "hufu.outcome"), Convert.ToDouble(value)));
        });
        var request = Request() with { Action = (AuthorityAction)actionValue };
        var wrapped = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) =>
            ValueTask.FromResult(new AuthorityRequestAuthorization(Request(), AuthorityStatus.Deny))), telemetry);

        await wrapped.AuthorizeAsync(request);
        var measurement = await observed.Task.WaitAsync(Timeout);
        Assert.Equal(expectedAction, measurement.Action);
        Assert.Equal(actionValue == 0 ? "deny" : "invalid", measurement.Outcome);
        Assert.InRange(measurement.Duration, 0, 86_400);
    }

    [Theory]
    [InlineData("permit", "permit")]
    [InlineData("deny", "deny")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("faulted", "faulted")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("invalid", "invalid")]
    public async Task OutcomesMapToClosedTelemetryCategories(string suppliedOutcome, string expectedOutcome)
    {
        using var telemetry = new AuthorityTelemetry();
        var observed = NewMeasurementSignal();
        using var listener = CreateMeterListener((instrument, value, tags, _) =>
        {
            if (instrument.Name == AuthorityTelemetry.AuthorizationDurationName)
                observed.TrySetResult((Tag(tags, "hufu.action"), Tag(tags, "hufu.outcome"), Convert.ToDouble(value)));
        });
        var request = Request();
        var decision = new AuthorityDecision(AuthorityStatus.Permit, "reason", "v1", "test", new string('a', 64));
        var decorator = new TelemetryAuthorityRequestAuthorizer(new Stub((_, ct) => suppliedOutcome switch
        {
            "permit" => ValueTask.FromResult(new AuthorityRequestAuthorization(request, AuthorityStatus.Permit, decision, true)),
            "deny" => ValueTask.FromResult(new AuthorityRequestAuthorization(request, AuthorityStatus.Deny)),
            "unavailable" => ValueTask.FromResult(new AuthorityRequestAuthorization(request, AuthorityStatus.Unavailable)),
            "faulted" => throw new InvalidOperationException("secret-fault-text"),
            "cancelled" => throw new OperationCanceledException("secret-cancel-text", ct),
            _ => ValueTask.FromResult<AuthorityRequestAuthorization>(null!)
        }), telemetry);

        if (suppliedOutcome == "cancelled")
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await decorator.AuthorizeAsync(request, cancellation.Token));
        }
        else if (suppliedOutcome == "faulted")
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await decorator.AuthorizeAsync(request));
        else
            _ = await decorator.AuthorizeAsync(request);

        var measurement = await observed.Task.WaitAsync(Timeout);
        Assert.Equal("read_file", measurement.Action);
        Assert.Equal(expectedOutcome, measurement.Outcome);
        Assert.InRange(measurement.Duration, 0, 86_400);
    }

    [Fact]
    public async Task NoListenerAndDisposedTelemetryDoNotChangeAuthorization()
    {
        var telemetry = new AuthorityTelemetry();
        telemetry.Dispose();
        telemetry.Dispose();
        var expected = new AuthorityRequestAuthorization(Request(), AuthorityStatus.Deny);
        var calls = 0;
        var decorator = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) =>
        { calls++; return ValueTask.FromResult(expected); }), telemetry);

        Assert.Same(expected, await decorator.AuthorizeAsync(Request()));
        Assert.Equal(1, calls);
        Assert.True(telemetry.DroppedMeasurements >= 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuthorityTelemetry.MaximumQueuedMeasurements + 1)]
    public void QueueCapacityRejectsValuesOutsideDocumentedBounds(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorityTelemetry(capacity));

    [Fact]
    public async Task SlowAndThrowingListenersCannotDelayOrChangeCallerResult()
    {
        var entered = NewSignal();
        var secondEntered = NewSignal();
        var release = NewSignal();
        var callbackNumber = 0;
        var listener = CreateMeterListener((instrument, value, _, _) =>
        {
            if (instrument.Name != AuthorityTelemetry.AuthorizationCountName) return;
            if (Interlocked.Increment(ref callbackNumber) == 1) entered.TrySetResult();
            else secondEntered.TrySetResult();
            if (!release.Task.Wait(Timeout)) throw new TimeoutException("listener test barrier expired");
            throw new InvalidOperationException("private listener exception");
        });
        using (listener)
        using (var telemetry = new AuthorityTelemetry())
        {
            var expected = new AuthorityRequestAuthorization(Request(), AuthorityStatus.Deny);
            var decorator = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) => ValueTask.FromResult(expected)), telemetry);
            try
            {
                var call = decorator.AuthorizeAsync(Request()).AsTask();
                Assert.Same(expected, await call.WaitAsync(Timeout));
                await entered.Task.WaitAsync(Timeout);
            }
            finally { release.TrySetResult(); }
            await decorator.AuthorizeAsync(Request()).AsTask().WaitAsync(Timeout);
            await secondEntered.Task.WaitAsync(Timeout);
            await WaitUntil(() => telemetry.EmissionFailures >= 2);
        }
    }

    [Fact]
    public async Task CapacityOneDropsAtSaturationAndDisposeNeverWaitsForBlockedWorker()
    {
        var entered = NewSignal();
        var release = NewSignal();
        using var listener = CreateMeterListener((instrument, _, _, _) =>
        {
            if (instrument.Name != AuthorityTelemetry.AuthorizationCountName) return;
            entered.TrySetResult();
            release.Task.Wait(Timeout);
        });
        var telemetry = new AuthorityTelemetry(1);
        var result = new AuthorityRequestAuthorization(Request(), AuthorityStatus.Deny);
        var decorator = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) => ValueTask.FromResult(result)), telemetry);
        try
        {
            await decorator.AuthorizeAsync(Request()).AsTask().WaitAsync(Timeout);
            await entered.Task.WaitAsync(Timeout);
            await decorator.AuthorizeAsync(Request()).AsTask().WaitAsync(Timeout); // occupies the sole queue slot
            await decorator.AuthorizeAsync(Request()).AsTask().WaitAsync(Timeout); // rejected immediately
            Assert.True(telemetry.DroppedMeasurements >= 1);
            var dispose = Task.Run(telemetry.Dispose);
            await dispose.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            release.TrySetResult();
            telemetry.Dispose();
        }
    }

    [Theory]
    [InlineData("sample")]
    [InlineData("stopped")]
    public async Task ThrowingTraceListenersCannotChangeAuthorizationAndWorkerContinues(string failurePoint)
    {
        var second = NewSignal();
        var calls = 0;
        using var metrics = CreateMeterListener((instrument, _, _, _) =>
        {
            if (instrument.Name == AuthorityTelemetry.AuthorizationCountName && Interlocked.Increment(ref calls) == 2)
                second.TrySetResult();
        });
        using var traces = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AuthorityTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => failurePoint == "sample"
                ? throw new InvalidOperationException("private sample failure") : ActivitySamplingResult.AllData,
            ActivityStopped = _ => throw new InvalidOperationException("private exporter failure")
        };
        ActivitySource.AddActivityListener(traces);
        using var telemetry = new AuthorityTelemetry();
        var expected = new AuthorityRequestAuthorization(Request(), AuthorityStatus.Deny);
        var authorizer = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) => ValueTask.FromResult(expected)), telemetry);
        Assert.Same(expected, await authorizer.AuthorizeAsync(Request()).AsTask().WaitAsync(Timeout));
        await WaitUntil(() => telemetry.EmissionFailures >= 1);
        Assert.Same(expected, await authorizer.AuthorizeAsync(Request()).AsTask().WaitAsync(Timeout));
        await second.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task MeasurementsUseClosedCategoriesAndNoRequestOrAmbientCorrelationData()
    {
        Ambient.Value = "ambient-secret";
        using var telemetry = new AuthorityTelemetry();
        var measurement = new TaskCompletionSource<(string Action, string Outcome, double Duration, string? Ambient, string[] Tags)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = CreateMeterListener((instrument, value, tags, _) =>
        {
            if (instrument.Name == AuthorityTelemetry.AuthorizationDurationName)
                measurement.TrySetResult((Tag(tags, "hufu.action"), Tag(tags, "hufu.outcome"), Convert.ToDouble(value), Ambient.Value,
                    tags.ToArray().Select(pair => pair.Key + "=" + pair.Value).ToArray()));
        });
        using (listener)
        {
            listener.Start();
            var request = Request();
            var result = new AuthorityRequestAuthorization(request, AuthorityStatus.Deny,
                new AuthorityDecision(AuthorityStatus.Deny, "secret-reason", "snapshot-secret", "evaluator", new string('a', 64)));
            var decorator = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) => ValueTask.FromResult(result)), telemetry);
            await decorator.AuthorizeAsync(request);
            var seen = await measurement.Task.WaitAsync(Timeout);
            Assert.Equal("read_file", seen.Action);
            Assert.Equal("deny", seen.Outcome);
            Assert.InRange(seen.Duration, 0, 86_400);
            Assert.Null(seen.Ambient);
            Assert.Equal(new[] { "hufu.action", "hufu.outcome" }, seen.Tags.Select(tag => tag[..tag.IndexOf('=')]).Order().ToArray());
            Assert.DoesNotContain("private", string.Join("|", seen.Tags), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-reason", string.Join("|", seen.Tags), StringComparison.OrdinalIgnoreCase);
        }
        Ambient.Value = null;
    }

    [Fact]
    public async Task ActivityIsRootAndCarriesOnlyClosedCategoriesAndBoundedDuration()
    {
        var seen = new TaskCompletionSource<(Activity? Parent, string? ParentId, string? Action, string? Outcome,
            double Duration, double SpanDuration, string? Ambient, string[] Tags, string[] TagObjects,
            string[] Baggage, int Events, int Links)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AuthorityTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == AuthorityTelemetry.AuthorizationActivityName)
                    seen.TrySetResult((activity.Parent, activity.ParentId, activity.GetTagItem("hufu.action") as string,
                        activity.GetTagItem("hufu.outcome") as string,
                        Convert.ToDouble(activity.GetTagItem("hufu.duration_seconds")), activity.Duration.TotalSeconds, Ambient.Value,
                        activity.Tags.Select(tag => tag.Key + "=" + tag.Value).ToArray(),
                        activity.TagObjects.Select(tag => tag.Key + "=" + tag.Value).ToArray(),
                        activity.Baggage.Select(item => item.Key + "=" + item.Value).ToArray(),
                        activity.Events.Count(), activity.Links.Count()));
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("private-parent").AddBaggage("secret-key", "secret-value").Start();
        Ambient.Value = "ambient-secret";
        using var telemetry = new AuthorityTelemetry();
        var decorator = new TelemetryAuthorityRequestAuthorizer(new Stub((_, _) =>
            ValueTask.FromResult(new AuthorityRequestAuthorization(Request(), AuthorityStatus.Deny))), telemetry);
        try
        {
            await decorator.AuthorizeAsync(Request());
            var observation = await seen.Task.WaitAsync(Timeout);
            Assert.Null(observation.Parent);
            Assert.Null(observation.ParentId);
            Assert.Equal("read_file", observation.Action);
            Assert.Equal("deny", observation.Outcome);
            Assert.InRange(observation.Duration, 0, 86_400);
            Assert.InRange(observation.SpanDuration, 0, 86_400);
            Assert.Null(observation.Ambient);
            Assert.DoesNotContain("private", string.Join("|", observation.Tags), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", string.Join("|", observation.Tags), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { "hufu.action", "hufu.duration_seconds", "hufu.outcome" },
                observation.TagObjects.Select(tag => tag[..tag.IndexOf('=')]).Order().ToArray());
            Assert.Empty(observation.Baggage);
            Assert.Equal(0, observation.Events);
            Assert.Equal(0, observation.Links);
            Assert.Same(parent, Activity.Current);
        }
        finally { Ambient.Value = null; }
    }

    private delegate void MeasurementCallback(Instrument instrument, object? value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state);

    private static MeterListener CreateMeterListener(MeasurementCallback callback)
    {
        // MeterListener measurement callback overloads are generic; use a boxed bridge for both instruments.
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == AuthorityTelemetry.SourceName) current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => callback(instrument, value, tags, state));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) => callback(instrument, value, tags, state));
        listener.Start();
        return listener;
    }

    private static string Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var pair in tags) if (pair.Key == key) return Convert.ToString(pair.Value) ?? "";
        return "";
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<(string Action, string Outcome, double Duration)> NewMeasurementSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopAt = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= stopAt) throw new TimeoutException("telemetry worker did not recover after listener failure");
            await Task.Delay(10);
        }
    }
    private static AuthorityRequest Request() => new(Context, AuthorityAction.ReadFile, "workspace-private", "secret/path.txt", "request-private");

    private sealed class Stub(Func<AuthorityRequest, CancellationToken, ValueTask<AuthorityRequestAuthorization>> invoke) : IAuthorityRequestAuthorizer
    {
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default) => invoke(request, cancellationToken);
    }

    private sealed class CountingSource : IAuthoritySnapshotSource
    {
        internal int Calls;
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var snapshot = new AuthoritySnapshot(context, "v1", [new AuthorityLayer("layer", Array.Empty<AuthorityGrant>())],
                Array.Empty<AuthorityScope>(), DateTimeOffset.UtcNow.AddHours(1));
            return ValueTask.FromResult<AuthoritySnapshot?>(snapshot);
        }
    }
    private sealed class CountingEvaluator : IAuthorityEvaluator
    {
        internal int Calls;
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
        {
            Interlocked.Increment(ref Calls);
            return new AuthorityDecision(AuthorityStatus.Permit, "would-permit", snapshot.Version, "test", snapshot.Identity);
        }
    }
    private sealed class FailingRecorder(bool throws) : IAuthorityDecisionRecorder
    {
        internal int Calls;
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (throws) throw new InvalidOperationException("secret-recorder-text");
            return ValueTask.FromResult(false);
        }
    }
}
