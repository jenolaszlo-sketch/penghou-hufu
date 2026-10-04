# Optional bounded authorization telemetry

Implemented in Hufu core without an OpenTelemetry SDK, exporter, collector,
workflow engine or policy-backend dependency. Hosts choose their tracing and
metrics listeners. This is a bounded request-preflight slice, not broker,
approval-wait, revocation-propagation or mutation-outcome instrumentation.

## Opt in at the host boundary

```csharp
using Penghou.Hufu;

using var telemetry = new AuthorityTelemetry(maxQueuedMeasurements: 256);
IAuthorityRequestAuthorizer current = new CurrentAuthorityRequestAuthorizer(
    snapshotSource, evaluator, mandatoryDecisionRecorder);
IAuthorityRequestAuthorizer admitted = new BoundedAuthorityRequestAuthorizer(
    current, maxExecuting: 4, maxQueued: 32,
    admissionTimeout: TimeSpan.FromSeconds(2));
IAuthorityRequestAuthorizer observed = new TelemetryAuthorityRequestAuthorizer(
    admitted, telemetry);
```

Share one telemetry owner and admitted authorizer for the host's protected
service. Creating owners per request defeats aggregate bounds and creates more
workers/instruments. The host subscribes its existing SDK or BCL listeners to
`AuthorityTelemetry.SourceName`, `Penghou.Hufu`, version `1.0.0`. No SDK or
network service is initialized by Hufu. .NET provides the underlying
[ActivitySource tracing](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs)
and [Meter instrumentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation).

The decorator invokes the inner authorizer exactly once with the original
request and cancellation token, then returns its exact result or rethrows its
exception. It performs no authorization, evidence write, approval or dispatch.
Invalid inner results remain the inner provider's error; telemetry observes
them as `invalid` and cannot repair or grant them. Compose a trusted fail-closed
authorizer/admission gate as shown above.

## Finite metadata and instrumentation

Only a closed action category, closed outcome category, UTC start instant and
monotonic elapsed duration enter the queue. No request/result object,
tenant/subject/session/run/operation identity, request digest, path, policy,
reason text, exception, credential, explanation, ambient activity context or
baggage is retained. The worker suppresses caller execution-context flow.

| Instrument | Kind | Meaning |
| --- | --- | --- |
| `hufu.authorization.completed` | Counter, `{authorization}` | Completed observations processed by the worker, including faults and cancellation |
| `hufu.authorization.duration` | Histogram, `s` | Caller-visible elapsed preflight time, including queue waits when wrapped as above |
| `hufu.telemetry.dropped` | Observable counter, `{measurement}` | Rejected or discarded measurements; also available as `DroppedMeasurements` |
| `hufu.telemetry.emission_failures` | Observable counter, `{failure}` | Instrumentation emissions that threw on the worker; also available as `EmissionFailures` |
| `hufu.authorize` | Internal root activity | A completed observation with the same closed categories and elapsed timing |

Count/duration instruments have exactly two labels: `hufu.action` and
`hufu.outcome`. Actions are `read_file`, `list_directory`, `read_metadata`,
`patch_file`, `release`, `write_file`, or `unknown`. Outcomes are `permit`,
`deny`, `unavailable`, `cancelled`, `faulted`, or `invalid`: at most 42 label
combinations. Loss/failure counters have no labels. Activities add only
`hufu.duration_seconds`, use fresh root correlation and contain no inherited
parent, baggage, exception events or status-description text.

`permit` describes a returned matching, evidenced preflight permit; it is not
permission for an effect. A permit lacking the existing `IsAuthorized` contract
is `invalid`. Caller-token cancellation is `cancelled`; other exceptions,
including an unrelated cancellation exception, are `faulted`. Timing uses
`Stopwatch` and is clamped to zero through 86,400 seconds. UTC timestamps are
placement hints; span end is derived from the capped monotonic duration.
Cancellation observes the caller's completed wait, not native-work termination
or admission-slot release. The existing admission owner still holds active
capacity until its actual inner work completes.

## Capacity, loss and shutdown

The queue holds 1 through 4,096 measurements (default 256), with at most one
additional measurement being emitted. Producers only try to enqueue; a full
queue drops the new observation and increments an atomic counter. There is no
unbounded queue, task per observation, retry, exporter wait or retained payload.

One worker emits all instruments. Listener callbacks run outside authorization;
their ordinary exceptions are contained and counted. A slow or permanently
blocked synchronous listener occupies only that worker and finite queue.
Other observations can be lost; authority calls never wait for export.
Hosts own listener isolation, availability and process-wide capacity.

`Dispose` is idempotent and nonblocking. It stops acceptance, completes the
queue and discards pending records as the worker proceeds. Already executing
emissions may finish after disposal; shutdown does not promise a drain or flush.
Future records count as dropped and never change the inner authorization result.
A blocked listener cannot be forcibly terminated by this API. If listener
initialization fails, the worker closes acceptance and discards pending data.

Optional telemetry may be sampled, absent or lost. Its counts are not total
audit counts, proof of provenance or mandatory evidence receipts. A failed
required recorder still blocks authorization exactly as it did before. Exporter
loss cannot satisfy or erase that obligation. Host-chosen listeners can add
their own data; the host must independently authorize export and enforce
redaction/access controls for its entire telemetry pipeline. This profile
deliberately leaves correlation to protected evidence for a future independently
authorized host composition.

## Remaining work

Broker effects, approval waits, revocation propagation, authenticated evidence
correlation and outcome reconciliation remain separate host/integration gates.
This slice closes only request-preflight telemetry. See the [roadmap](roadmap.md)
and [qualification record](qualification/optional-telemetry.json).
