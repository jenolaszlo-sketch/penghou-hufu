# Optional workflow authorization

`Penghou.Hufu.Workflow` implements `IExecutionAuthorizer` from the published
`Penghou.Workflow.Abstractions` 0.1.0-preview.2 contract. It depends only on that
contract and Hufu core. Zhinu 0.2.0-preview.1 is one tested consumer; another
engine can supply the same neutral context. The six-package Hufu release
candidate is described in the [release profile](package-release-profile.md).

## Trusted host composition

Construct `HufuExecutionAuthorizer` with a provider ID, host namespace, mapping
profile ID and these required services:

| Service | Host obligation |
| --- | --- |
| `IWorkflowAuthorityBindingSource` | Authenticate tenant/subject, resolve retained resource/scope references, preserve parent ceilings and bind the entire current context to canonical targets |
| `IAuthorityRequestAuthorizer` | Evaluate current Hufu authority and record each resource decision; the existing `CurrentAuthorityRequestAuthorizer` is usable |
| `IWorkflowApprovalCoordinator` | Apply explicit approval policy and validate approval custody, correlation, expiry and current semantic scope |
| `IWorkflowAuthorizationRecorder` | Durably record the exact aggregate outcome before returning its attributable evidence reference |

No trusted identity, approval implementation or production ledger is supplied
implicitly. These interfaces define the host's security boundary. Caller-provided
execution IDs, requirement declarations and historical evidence do not establish
authenticated identity or permission.

```csharp
IExecutionAuthorizer authorizer = new HufuExecutionAuthorizer(
    providerId: "hufu-workflow-v1",
    hostNamespace: "my-service",
    mappingId: "workspace-resources-v1",
    bindings: trustedBindings,
    authority: currentAuthority,
    approvals: trustedApprovals,
    recorder: durableWorkflowLedger);
```

The variables are host services implementing the interfaces above. Configure this
single effective authorizer in the engine's protected execution profile. Keep the
host registration and profile persistent across recovery; a missing authorizer
must not downgrade an already protected execution.

## Supported declaration profile

Each `ExecutionRequirement` uses schema `penghou.hufu.resource`, version `1`, a
logical resource reference and a non-null retained scope reference. The finite
capability vocabulary maps directly to Hufu actions:

| Capability | Hufu action |
| --- | --- |
| `read-file` | `ReadFile` |
| `list-directory` | `ListDirectory` |
| `read-metadata` | `ReadMetadata` |
| `patch-file` | `PatchFile` |
| `release` | `Release` |
| `write-file` | `WriteFile` |
| `process.execute` | `ExecuteProcess` |

Empty declarations, unknown schemas/versions/capabilities and unscoped
requirements return a recorded denial before activating the binding source.
Declare every protected callback, including compensation, loop predicates and
engine-generated helper steps. An engine profile with undeclared helper
callbacks is unsupported; step names never create implicit permission.

A trusted binding must contain exactly one ordered target per requirement, up to
the neutral contract's 64-item limit. It resolves references to a validated
workspace ID and canonical Hufu relative path. It authenticates tenant/subject
independently and binds Hufu `RunId`, `RevisionId` and `FenceId` to the exact
neutral execution ID, execution revision and fresh authorization request ID.
Its finite validity and digest cover the complete execution/parent/operation/
attempt/plan context, ordered declarations, host/mapping identity, authenticated
actor, resolved targets and expiry. The digest identifies evidence; it grants
no authority. Any mapping failure or mismatched binding fails closed.

## Decisions, approval and evidence

Every current resource decision must permit the exact request with recorded
evidence before approval is evaluated. A valid, recorded Hufu denial produces
`Denied`. Provider exceptions, inconsistent results, missing resource evidence,
unavailable mapping or approval, and expiry produce `Unavailable`. This adapter
uses `Unavailable` for infrastructure failures; engines must also fail closed
on the contract's `Error` outcome. Caller cancellation propagates.

Approval is typed: `NotRequired`, `Required`, `Approved`, `Denied` or
`Unavailable`. Only `Required` carries a pending approval request ID. The approval
result must name the exact current binding and have finite validity; denial
reason text is never interpreted as approval. `Approved` still follows fresh
Hufu authority checks. An exact wake is a request to reevaluate, not a permit.

Durable approval facts cannot simply reuse an old binding digest after recovery:
the fresh request and runtime revision may change. The trusted coordinator must
authenticate and revalidate those facts against the entire current semantic
operation, attempt, plan, declarations, resource targets, parent ceiling and
approval policy before issuing a result bound to the new digest. Changed
operation semantics require a new approval. The integration test fixture knows
Zhinu's revision format and distinguishes lease renewal from semantic step
restart; that fixture-specific parsing is not a generic production contract.
An alternative engine/host must define and qualify its own recovery semantics.

The aggregate recorder receives the context, trusted binding when available,
resource decisions, typed approval and proposed neutral result. That proposed
result has no `EvidenceId` yet: the recorder persists it and returns the
attributable record reference. An absent, malformed or failed acknowledgment
returns `Unavailable`; an outcome is never dispatchable without its required
evidence. A record persisted just before a failed response is historical evidence
and cannot authorize a later retry. The ledger records a proposed, time-bounded
evaluation rather than a dispatch receipt: if recording consumes its validity,
the stored proposal can say `Allowed` while the returned result is expired and
`Unavailable`. Runtime callback/start evidence establishes actual dispatch.
Timeout or cancellation can prevent an aggregate record from being acknowledged;
that absence blocks dispatch and must not be interpreted as a permit.

The default evaluation budget is 30 seconds, configurable above zero through
five minutes. It bounds all service waits, including a provider ignoring
cancellation and the aggregate ledger. Binding/approval expiry can shorten the
returned validity. `Allowed` and `ApprovalRequired` both carry finite expiry.
Budget exhaustion and a backward clock change invalidate a proposed permit or
pending approval. Hosts must supply clocks and services appropriate to their
deployment.

## Runtime and effect boundaries

The runtime owns durable pending approvals, exact wakes, claims, leases,
generation/revision fences and retries. It must recheck those facts after the
asynchronous authorizer returns and before callback dispatch. Replay of a
completed result does not dispatch or reauthorize that callback; any new callback
attempt obtains a fresh current decision. Compensation has its own declaration
and authority checks.

This is asynchronous callback preflight. Authority can change while later
services or a callback are running. Resource providers still enforce current
authority at access and actual mutation start. It does not contain native code
or provide an atomic transaction coupling policy, workflow dispatch and effects.
The frozen, separately tested `Penghou.Hufu.Zhinu.Sqlite` preview.15 composition
retains its distinct shared-database atomic start guarantee and remains
non-packable. Replacing it requires separate effect-boundary qualification.

## Qualification and remaining release gates

Unit tests cover mapping, all six actions, full-context binding, denial, typed
approval, evidence failure, cancellation, expiry and hung providers on .NET 8/10.
Separate integration tests exercise actual published Zhinu and SQLite packages,
including approval/store recreation, revocation, retry, compensation and replay.
The package integration script repeats those tests against freshly packed Hufu
packages in an empty cache; normal core tests contain no Zhinu dependency.

See the [handoff](zhinu-authority-handoff.md), [roadmap](roadmap.md) and qualification
records for actual completion evidence. Local package qualification, remote CI,
user-run publication and production host qualification are distinct gates.
