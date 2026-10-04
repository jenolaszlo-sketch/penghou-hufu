# Core request admission and authenticated issuance

Implemented 2026-10-04 in `Penghou.Hufu`, with no workflow-engine dependency.
These are optional compositions of existing authority interfaces. They require
host authentication, operation policy and mandatory decision evidence. See
[qualification](qualification/core-hardening.json) and the
[release handoff](zhinu-authority-handoff.md) for the tested tree and release gates.

## Bound a shared authority evaluator

Wrap the host's current-snapshot authorizer once for the evaluator/store owner:

```csharp
IAuthorityRequestAuthorizer current = new CurrentAuthorityRequestAuthorizer(
    snapshotSource, evaluator, decisionRecorder);
IAuthorityRequestAuthorizer admitted = new BoundedAuthorityRequestAuthorizer(
    current, maxExecuting: 4, maxQueued: 32,
    admissionTimeout: TimeSpan.FromSeconds(2));
```

The numbers are deployment choices. Executing capacity must be between 1 and
256; queued capacity between 0 and 4096. Admission timeout is positive and at
most one day. Share the wrapper across callers using the protected owner;
creating a wrapper per request defeats aggregate admission. Limits apply to
this instance in this process.

Queued requests enter in FIFO order. Saturation or queue expiry returns
`Unavailable` before invoking the inner authorizer. Malformed requests deny
before admission. Inner exceptions, malformed results, mismatched request
identity, and permits without required matching evidence become `Unavailable`.
Valid denials and unavailable results retain their status. The wrapper does
not manufacture evidence or permission.

Cancellation stops the caller's wait promptly. A queued cancellation removes
the waiter; an active cancellation keeps the execution slot until the inner
task actually completes. The inner provider receives `CancellationToken.None`
so that a cancellable wrapper inside it cannot report completion while its
native/store/evidence work is still running. Cancellation does not kill active
work. A permanently hung evaluation retains its slot. The inner authorizer
must await the actual work it owns; detached work cannot be bounded by this API.
Hosts own native deadlines, service health and cross-process capacity.
Admission timeout applies only to waiting in the queue.

## Authenticate and approve publication

Supply a host-owned `IAuthorityIssuanceTrustSource` and an existing
`IAuthorityStoreAuthorizer` operation policy:

```csharp
IAuthorityStoreAuthorizer gate = new BoundedAuthorityIssuanceAuthorizer(
    issuanceTrustSource, operationPolicy,
    maximumEvaluationDuration: TimeSpan.FromSeconds(30));
IAuthorityStore store = new SqliteAuthorityStore(databasePath, gate);
```

`AuthenticateAsync` receives the presented actor and full access request. The
host authenticates the session through its credential/context channel and
independently resolves current issuer authority and approval. For publication
it returns an `AuthorityIssuancePrincipal` containing:

- The exact authenticated tenant, actor and session.
- An independently authenticated, current non-revoked `IssuerCeiling`.
- `ApprovedSnapshotIdentity`, `ApprovedCommandId`, `ApprovedExpectedSequence`
  and a future `ApprovalValidUntil`, all from trusted approval state.

The snapshot identity includes the exact target context, layers, grants,
exclusions and expiry. Approval therefore binds those facts as well as the
command and expected sequence. Approval withdrawal or expiry prevents new
publication and replay. An old publication receipt is historical evidence.

Actor/session identifiers and constructed snapshots are data, not credentials.
Returning the proposal as its own ceiling provides no independent authority;
the profile rejects equal ceiling/proposal identities. Different identities
alone also prove nothing: the trust source must authenticate provenance, issuer
eligibility and current revocation state. The operation policy decides whether
the authenticated actor may perform the particular store operation. All
operations, including publication, pass through this policy. A valid ceiling
and approval never bypass it. Other operations require authenticated actor and
operation policy, but do not require issuance approval fields.

Publication validates the subject/context/proposal binding before service
activation. It checks authenticated actor identity, approval and containment,
then awaits operation policy. Afterwards it calls the trust source again and
rechecks current actor, ceiling, approval and time before returning permit.
Both responses must bind the exact presented actor. Infrastructure failures,
malformed responses, expiry, unknown operations and missing policy permits deny.
External caller cancellation propagates.

The evaluation budget defaults to 30 seconds and must be positive and at most
five minutes. It bounds asynchronous service waits, including a provider that
ignores cancellation. It does not terminate synchronous blocking calls or
underlying work that ignores cancellation; hosts must bound that work separately.
A backward clock or elapsed budget also prevents a permit.

## Conservative containment

The profile supports the existing six typed actions and canonical Exact/Subtree
workspace scopes. For each proposed grant/action, one currently active grant
in **every** issuer layer must contain its workspace/path scope and full grant
validity interval. Any ceiling exclusion overlapping the proposed scope causes
denial. Every ceiling mandatory denial must remain equally or more broadly
represented in the proposal. Tenant and snapshot expiry cannot widen.

This is a sufficient bounded containment check. It deliberately rejects
proposals requiring union, split-scope coverage, exclusion subtraction or more
general policy proofs. Delegation, approval UI, credential custody and generalized
policy analysis remain future host/design work.

## Execution and persistence boundary

The SQLite store applies this gate before writing and reauthenticates replay.
Its existing transaction owns target-sequence CAS, idempotency and terminal
revocation. Reloading issuer state after policy closes that asynchronous stale
capture; it does not serialize a subsequent parent revocation with publication
across independent authority slots or stores. A host requiring that ordering
needs a separately qualified atomic composition.

Neither profile grants resource access or starts a workflow operation. Continue
fresh resource checks and runtime fences at their actual boundaries. The neutral
[workflow adapter](workflow-authorizer.md) remains preflight; the legacy atomic
start profile retains its separate guarantee and regression suite.

## Qualification and next work

Tests cover capacity/FIFO/expiry, cancellation handoff races, real
current-authorizer source/evidence work, exact approval, conservative containment,
policy composition, revocation during policy, and SQLite publication/replay.
Fresh package consumers exercise both new APIs on .NET 8/10; the package-only
workflow integration is requalified against the new core. CI adds portable core
security tests on Linux alongside the Windows full suite. Remote results require
a pushed revision; local results are recorded separately.

The bounded [decision explanation profile](decision-explanations.md) is now
implemented and separately qualified. Optional bounded telemetry is the next
independent core delivery. Complete issuance/delegation services and
the governed mutation host remain separate roadmap gates.
