# Worker-to-host broker authority profile (AE-2)

Status: **Specification frozen 2026-10-12; AE-2.0/2.1 contracts implemented, experimental and unpublished.**
This is the AE-2 scope frozen before implementation. The frozen values below are
locked. No qualification or publication is claimed.

**Implementation status (2026-10-12).** The first contract slice landed in the
new `Penghou.Hufu.Broker` project (non-packable, no core/IO dependency beyond
Hufu core): `BrokerProtocol` v1 bounds; host-issued single-use bootstrap and
`BrokerSessionRegistry` authentication; the opaque session-bound
`BrokerAuthorityReferenceStore`; the closed `BrokerReadFileRequest` DTO; and
`BrokerReadRequestMapper`, which resolves the reference under the authenticated
session into the exact canonical `AuthorityRequest`. This slice is
transport-neutral and performs no I/O. Deferred to AE-2.2: named-pipe framing,
the broker-local start arbiter execution, provider read through `Penghou.Hufu.IO`
with the AE-1 qualified authorization, and `BrokerStartEvidence`/
`BrokerReleaseEvidence` recording and result release.

This document refines the
[authority/enforcement implementation plan](authority-enforcement-implementation-plan.md);
[ADR 0014](decisions/0014-authority-delegation-enforcement.md), the
[canonical model](authority-delegation-enforcement.md) and the
[current authority profile](current-authority-profile.md) govern the semantics.
No runtime change, qualification or publication is claimed here.

## Purpose

AE-1 proves Hufu can reach a fresh, current derived-authority decision. AE-2
must prove that decision actually governs a real operation at a broker/provider
boundary: one authenticated worker, one host broker, one bounded read through a
real workspace reader. Broker authority semantics are transport-neutral; a local
Windows named pipe is the implementation/qualification choice for this profile,
not a Hufu architectural dependency.

## Shape

```text
worker
  |
  | authenticated named-pipe session
  | opaque session-bound reference
  v
Penghou.Hufu.Broker
  |
  | bounded canonical request
  | broker-local start ordering
  v
Penghou.Hufu.IO
  |
  | AE-1 qualified derived authorization
  v
real workspace reader
```

## Ownership and project shape

| Component | Owns | Does not own |
| --- | --- | --- |
| `Penghou.Hufu.Broker` (new, experimental, unpublished) | Worker session bootstrap/authentication, named-pipe transport, protocol version, reference resolution, closed bounded DTO, broker-local start ordering, start/release evidence orchestration | Canonical resource authorization (Hufu.IO), core authority (Hufu core), provider I/O (reader) |
| `Penghou.Hufu.IO` (unchanged scope) | Canonical resource authorization adapter and provider access front door | Transport, sessions, references |
| Hufu core | The qualified derived-authority decision (AE-1) | Transport, sessions, reference storage |

Do not fold transport/session concerns into `Penghou.Hufu.IO`; it stays
transport-neutral. Do not split `Broker.Client`/`Broker.Protocol` out yet unless
implementation pressure forces it.

## Session authentication and transport

- One protected local Windows named pipe, one frozen protocol version.
- Pipe ACLs protect transport access, but **logical worker identity comes from a
  host-issued worker-session bootstrap and connection handshake**. PID labels,
  claimed worker IDs, and a pipe connection alone are not authentication.
- A host-issued bootstrap secret is delivered to the worker over a protected
  channel (for example an inherited handle or a non-model-visible source). The
  worker presents it on connect; the host binds a session to tenant/subject/
  audience and a finite expiry. Keep bootstrap material out of model-visible
  inputs, outputs, logs and telemetry.
- No anonymous fallback and no second untested transport.
- This is a **cooperating-worker profile**. Hostile same-account bypass closure
  remains an AE-4 provider/channel gate.

## Opaque session-bound reference

- An opaque host-issued reference resolves **only** under the authenticated
  session/connection, never from the reference string alone.
- The reference binds at least: session, tenant, child subject, broker audience,
  delegation, generation/fence, expiry, and the authority context.
- Caller input cannot select a root, identity service, evaluator, credential or
  wider parent context. Visibility references cannot substitute for resource
  references or vice versa.
- Knowing the reference string must not be sufficient (no bearer semantics).
- Not a signed token. Attestable credential transport is AE-3 (Biscuit).

## Closed bounded request

- Parse a closed versioned DTO: protocol version, reference, requested action,
  and bounded payload/limit fields. Unknown fields/shapes reject.
- The request `WorkspaceId`/`RelativePath` must **exactly** match the bound
  reference target. This profile has no narrowing semantics, and the canonical
  request is built from the **reference** target, never from caller fields, so a
  request can never substitute or reinterpret the authorized target.
- Produce one canonical `ResourceAuthorizationRequest`/`FileReadRequest` for the
  bound target. No search or directory-discovery endpoint in this first proof;
  if the reader internally probes ancestry/candidates, its individual checks are
  preserved but do not become a qualified general listing API.
- Message, concurrency and deadline bounds are frozen before code. Every retry
  requires fresh current authority (AE-1). Changed intent reusing an operation
  identity conflicts; a read may be repeated only as an explicitly reauthorized
  attempt, never through an old Permit.

`OperationId` is an **idempotency/correlation identity, not an authorization
capability**. Reusing it with changed intent is `Conflict`; replaying an
identical read still requires fresh authority and fresh release authorization.
A previous successful response must never be replayed as though it were a new
resource observation.

## Broker-local start ordering

A concrete, broker-local ordering point — **not** a general public
`IStartOrderingArbiter` yet. One narrow rule:

```text
revocation/lifecycle commit wins first  -> later start blocked
start admission wins first              -> bounded read may finish internally
```

- The ordering point covers the **authoritative start decision and start
  evidence**, not the whole file read. Do not hold a global lock across I/O.
- Key it as narrowly as practical, likely by execution/authority binding.
- Only mutations that go through the qualified host path participate in this
  ordering guarantee. Direct, unmediated writers are out of scope.
- Do not reuse activity preflight as an atomic boundary or copy legacy workflow
  SQL into core.

Start order:

```text
acquire binding arbiter
resolve session-bound reference
perform final AE-1 authorization
commit mandatory start evidence
mark start admitted
release arbiter
perform provider read
perform independent release authorization
commit release evidence
return response
```

Mutation order (same binding arbiter):

```text
acquire same binding arbiter
commit revocation/lifecycle transition
release arbiter
```

This gives the guarantee a precise meaning without pretending to provide
transactional ordering with arbitrary external store writers.

## Start and release are separate decisions

```text
authorize start
order/admit start
record start evidence
perform read
re-check current authority for recipient disclosure
record release evidence
return result
```

- If authority changes after the read starts, the broker may let the internal
  read finish, but that does **not** imply the bytes may be returned.
- On release denial, disclose no protected path, content, canonical target,
  candidate metadata, or existence detail beyond the minimum external failure
  class.

## Evidence model

**Start evidence and result-release evidence are distinct artifacts.**

| Artifact | Proves | Does not prove |
| --- | --- | --- |
| Start receipt | The operation was admitted under an ordered current decision | That any bytes were read or delivered |
| Release receipt | A particular result was authorized for a particular recipient | That the recipient received it (delivery) |

Neither proves delivery, which matters for response-loss and retry cases.
Mandatory decision/start evidence commits before governed dispatch; result/error
evidence binds the recipient and exact request without automatic content
logging. A failed mandatory write blocks dispatch/disclosure. A recorded
authorization can be superseded before return and must not be read as delivered
authorization (see the [freshness rule](current-authority-profile.md#recorded-evidence-is-evaluation-evidence-not-delivered-authorization)).

## Failure and recovery

- Unavailable current authority/recorder/ordering blocks new starts and
  unauthorized disclosure.
- Recheck relevant state after asynchronous work.
- Define lost response, connection loss and process restart reconciliation
  without granting rights and without treating a read receipt as a future
  mutation receipt.
- Cached output replay requires its own current disclosure decision and cannot
  claim a fresh resource observation.

## Qualification plan (AE-2.3)

- Separate worker and broker processes and a real qualified reader/provider.
- Prove A cannot use B's reference; a child cannot borrow parent-only
  scope/actions; wrong tenant/audience/generation fails; stale/replayed bindings
  do not revive; denial reaches provider content access; denied candidate
  metadata/path/content is not returned.
- Inject revocation before start, during required evidence, after read and
  before release; expired sessions/grants; unavailable lineage/store/evidence;
  broker restart; response loss; malformed/oversized messages; capacity
  saturation.
- Inspect provider/evidence traces, not only transport statuses. Validate bounded
  buffers/deadlines and closed telemetry labels.
- Publish an example showing the exact threat model and limits, not a general
  production broker claim.

## Non-goals

Hostile same-account containment (AE-4); Biscuit/credential transport (AE-3);
restricted execution (AE-4); A2A/MCP (AE-5); mutation API; remote placement;
general RPC framework; sandbox dependency; general public ordering interface;
a second workflow event store; any dependency from Hufu core on the broker.

## Frozen values (locked 2026-10-12)

**Protocol and bounds.** Protocol `v1`; one request per message; bounded request
body 16 KiB; bounded read result 64 KiB; bounded connections/sessions per worker
and bounded concurrent reads per session; explicit request deadline supplied by
the host with a hard maximum. Oversized, unknown-version, or unknown-field
requests fail before authority resolution. No streaming in AE-2.

**Bootstrap and session.** The host creates a short-lived, single-use,
high-entropy bootstrap secret through a protected out-of-band launch/bootstrap
path. A successful handshake consumes it and establishes a broker session
containing at least `SessionId`, tenant, subject, logical worker identity,
broker audience, created/expiry times, protocol version, and authenticated
connection binding. PID is not identity. Session IDs may appear in protected
evidence; bootstrap secrets never do.

**Ordering key and participating mutations.** Key the arbiter by the narrow
execution-authority binding (tenant + subject + delegation + generation/fence),
not by path and not globally. Participating mutations are the lifecycle/
revocation operations that can invalidate that binding and that pass through the
qualified broker/host composition; a store write outside this path is explicitly
outside the ordering guarantee. Start admission includes the final AE-1 decision
plus committed start evidence before releasing the arbiter.

**Evidence and disclosure.** Separate typed `BrokerStartEvidence` and
`BrokerReleaseEvidence`, both bound to operation ID, authenticated
session/recipient, opaque-reference identity or protected digest, authority
binding, exact canonical operation digest, timestamp and outcome. Start evidence
records admission, not result; release evidence records disclosure
authorization, not delivery. Never log file content automatically. External
failures stay coarse: `InvalidRequest`, `Unauthorized`, `Unavailable`,
`DeadlineExceeded`, `CapacityExceeded`, `OperationFailed`. On denial disclose no
path existence, canonical path, policy reason, parent identity, content,
candidate metadata or internal store state.

**DTO schema.** One closed `ReadFile` request only. Fields: `ProtocolVersion`,
`OperationId`, `AuthorityReference`, `WorkspaceId`, `RelativePath`, `MaxBytes`,
`Deadline`. Tenant, subject, worker identity, audience, delegation and generation
come from authenticated session/reference resolution, never caller-supplied
fields. No arbitrary headers/options, no search/list/glob, no absolute path, no
provider selection, no evaluator selection. `MaxBytes` is `1..65536`; reject
trailing/unknown semantic fields deterministically.
