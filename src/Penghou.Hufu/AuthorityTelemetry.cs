using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace Penghou.Hufu;

/// <summary>Optional, lossy, bounded operational telemetry. Never authoritative evidence.</summary>
/// <remarks>
/// Share a host-owned instance. One background worker emits closed categories and timing only.
/// Disposal discards pending measurements without waiting for listeners. Synchronous listeners
/// that block cannot be terminated; they occupy only this worker and the finite queue.
/// Hosts own listener/exporter access, redaction, capacity and release policy.
/// </remarks>
public sealed class AuthorityTelemetry : IDisposable
{
    public const string SourceName = "Penghou.Hufu";
    public const string InstrumentationVersion = "1.0.0";
    public const string AuthorizationActivityName = "hufu.authorize";
    public const string AuthorizationCountName = "hufu.authorization.completed";
    public const string AuthorizationDurationName = "hufu.authorization.duration";
    public const string DroppedMeasurementsName = "hufu.telemetry.dropped";
    public const string EmissionFailuresName = "hufu.telemetry.emission_failures";
    public const int MaximumQueuedMeasurements = 4_096;
    private readonly Channel<Measurement> queue;
    private long dropped;
    private long emissionFailures;
    private int disposed;

    public AuthorityTelemetry(int maxQueuedMeasurements = 256)
    {
        if (maxQueuedMeasurements is < 1 or > MaximumQueuedMeasurements)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedMeasurements));
        queue = Channel.CreateBounded<Measurement>(new BoundedChannelOptions(maxQueuedMeasurements)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        // Neither caller baggage nor host authentication/context may flow to the export worker.
        if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(ExportAsync);
        else
        {
            using (ExecutionContext.SuppressFlow()) _ = Task.Run(ExportAsync);
        }
    }

    /// <summary>Measurements rejected after shutdown, at saturation, or discarded during shutdown.</summary>
    public long DroppedMeasurements => Interlocked.Read(ref dropped);
    /// <summary>Failed instrumentation emissions. A measurement may produce multiple emissions.</summary>
    public long EmissionFailures => Interlocked.Read(ref emissionFailures);

    internal void Record(AuthorityAction? action, Outcome outcome, DateTimeOffset startedAt, double durationSeconds)
    {
        var measurement = new Measurement(action, outcome, startedAt,
            double.IsFinite(durationSeconds) ? Math.Clamp(durationSeconds, 0, 86_400) : 0);
        if (Volatile.Read(ref disposed) != 0 || !queue.Writer.TryWrite(measurement))
            Interlocked.Increment(ref dropped);
    }

    /// <summary>Stops acceptance and discards pending data without waiting for export.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
    }

    private async Task ExportAsync()
    {
        try
        {
            using var source = new ActivitySource(SourceName, InstrumentationVersion);
            using var meter = new Meter(SourceName, InstrumentationVersion);
            var count = meter.CreateCounter<long>(AuthorizationCountName, "{authorization}");
            var duration = meter.CreateHistogram<double>(AuthorizationDurationName, "s");
            meter.CreateObservableCounter<long>(DroppedMeasurementsName, () => DroppedMeasurements, "{measurement}");
            meter.CreateObservableCounter<long>(EmissionFailuresName, () => EmissionFailures, "{failure}");
            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var measurement))
                {
                    if (Volatile.Read(ref disposed) != 0)
                    {
                        Interlocked.Increment(ref dropped);
                        continue;
                    }
                    var tags = new TagList
                    {
                        { "hufu.action", ActionName(measurement.Action) },
                        { "hufu.outcome", OutcomeName(measurement.Outcome) }
                    };
                    try { count.Add(1, tags); }
                    catch { Interlocked.Increment(ref emissionFailures); }
                    try { duration.Record(measurement.DurationSeconds, tags); }
                    catch { Interlocked.Increment(ref emissionFailures); }
                    EmitActivity(source, measurement, tags);
                }
            }
        }
        catch { Interlocked.Increment(ref emissionFailures); }
        finally
        {
            // A failed listener initialization must also close acceptance, never retain unbounded work.
            Interlocked.Exchange(ref disposed, 1);
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
        }
    }

    private void EmitActivity(ActivitySource source, Measurement measurement, TagList tags)
    {
        Activity? activity = null;
        // Emit a completed root observation. Never inherit ambient correlation or baggage.
        var previous = Activity.Current;
        try
        {
            Activity.Current = null;
            activity = source.StartActivity(AuthorizationActivityName, ActivityKind.Internal,
                default(ActivityContext), tags, startTime: measurement.StartedAt);
            if (activity is null) return;
            activity.SetTag("hufu.duration_seconds", measurement.DurationSeconds);
            activity.SetEndTime(measurement.StartedAt.AddSeconds(measurement.DurationSeconds).UtcDateTime);
        }
        catch { Interlocked.Increment(ref emissionFailures); }
        finally
        {
            try { activity?.Dispose(); }
            catch { Interlocked.Increment(ref emissionFailures); }
            Activity.Current = previous;
        }
    }

    private static string ActionName(AuthorityAction? action) => action switch
    {
        AuthorityAction.ReadFile => "read_file", AuthorityAction.ListDirectory => "list_directory",
        AuthorityAction.ReadMetadata => "read_metadata", AuthorityAction.PatchFile => "patch_file",
        AuthorityAction.Release => "release", AuthorityAction.WriteFile => "write_file", _ => "unknown"
    };
    private static string OutcomeName(Outcome outcome) => outcome switch
    {
        Outcome.Permit => "permit", Outcome.Deny => "deny", Outcome.Unavailable => "unavailable",
        Outcome.Cancelled => "cancelled", Outcome.Faulted => "faulted", _ => "invalid"
    };
    internal enum Outcome { Invalid, Permit, Deny, Unavailable, Cancelled, Faulted }
    private readonly record struct Measurement(AuthorityAction? Action, Outcome Outcome,
        DateTimeOffset StartedAt, double DurationSeconds);
}
