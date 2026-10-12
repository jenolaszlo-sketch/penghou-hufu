# AE-2.2 implementation spec: authenticated start and real read

Status: **Specification only, 2026-10-12. No code.** This narrows the AE-2.2
boundary in the [broker authority profile](broker-authority-profile.md). It
freezes the invariants and the start/release sequence before implementation.
**AE-2.2 does not claim AE-2 complete:** the adversarial/restart/response-loss
qualification corpus is AE-2.3. Stop before AE-2.3.

## Frozen invariants

1. **One identity model.** Named-pipe session establishment uses the existing
   `BrokerSessionRegistry` bootstrap/session model. Pipe ACLs protect endpoint
   access only; the broker session remains the logical authenticated worker
   identity. Transport must not create a second identity model.
2. **Concrete broker-local arbiter.** Keyed by the already-frozen
   execution/authority binding. No public/general arbiter abstraction yet.
3. **AE-1 runs inside the ordered start boundary**, followed by mandatory start
   evidence, before start is marked admitted.
4. **Participating mutations acquire the same key.** Lifecycle/revocation
   commits that invalidate the binding go through the same ordering key.
5. **Real provider path only.** Route through `HufuWorkspaceAccess` and the real
   `IWorkspaceReader` profile; an outer broker authorization without provider
   checks does not qualify AE-2.
6. **No lock across file I/O.** The arbiter establishes start order only; it is
   released before the provider read.
7. **Release is independently authorized after the read** (current authority +
   recipient/session binding) before bytes leave the broker.
8. **Start and release evidence are separate artifacts.** Start proves
   admission; release proves disclosure authorization; neither proves delivery.
9. **Response loss grants nothing.** A prior result never becomes reusable
   authorization; an identical retry gets fresh authorization and fresh release.
10. **No protected disclosure on failure.** No canonical path, content,
    existence, provider metadata, parent-policy detail or internal authority
    state.
11. **No hostile-worker containment claim.** AE-2 proves the authenticated
    cooperating-worker protocol and enforcement path, not same-account bypass
    closure.

## Components

| Component | Kind | Responsibility |
| --- | --- | --- |
| `BrokerPipeServer` | new, broker-local | Named-pipe endpoint framing (v1, one request per message, bounds), connection accept, bootstrap handshake |
| `BrokerSessionRegistry` | existing (AE-2.0/2.1) | Authenticate the presented bootstrap over the connection; return the session; bind the session to that connection for its lifetime |
| `BrokerAuthorityBinding` | new record | Tenant + subject + delegation + generation/fence; the arbiter key |
| `BrokerStartArbiter` | new, broker-local concrete | Per-binding ordered entry for start admission and participating mutations |
| `BrokerReadHandler` | new | Orchestrates resolve → map → ordered AE-1 → start evidence → provider read → release check → release evidence → response |
| `BrokerStartEvidence`, `BrokerReleaseEvidence`, `IBrokerEvidenceRecorder` | new | Distinct typed artifacts and host recorder |
| `HufuWorkspaceAccess` + `IWorkspaceReader` | existing | Real provider read with provider-side per-resource/content checks |

`BrokerAuthorityBinding` is derived from the resolved `BrokerAuthorityReference`;
caller input never supplies it.

## Ordered start boundary

```text
acquire binding arbiter (BrokerAuthorityBinding)
resolve authenticated session + reference (session-bound, from AE-2.1)
map exact canonical request (BrokerReadRequestMapper)
run AE-1 qualified authorization (current child + parent + lifecycle + freshness)
record mandatory start evidence -> receipt required
mark start admitted
release arbiter
perform provider read (HufuWorkspaceAccess + real IWorkspaceReader; no arbiter held)
perform independent release authorization (current authority + recipient binding)
record mandatory release evidence -> receipt required
return response
```

- The arbiter critical section covers the final AE-1 authorization and the
  **committed start evidence**, not the provider read.
- If mandatory start evidence fails, no provider read occurs.
- The digest/identity in start evidence is the exact canonical operation digest
  captured by the mapper.

## Participating mutation boundary

```text
acquire same binding arbiter (same BrokerAuthorityBinding)
commit lifecycle/revocation transition (durable)
release arbiter
```

Only mutations that pass through the qualified host/lifecycle path participate.
A store write outside this path is explicitly outside the ordering guarantee.

Outcomes:

```text
mutation commits first  -> later start authorization (inside the arbiter) observes
                           the change and denies; no provider read
start admitted first    -> mutation commits after release; the admitted read may
                           finish internally; release re-checks and denies disclosure
                           if authority changed
```

## Provider path

- Compose the AE-1 qualified `IAuthorityRequestAuthorizer` into an
  `IResourceAuthorizer` (`HufuResourceAuthorizer`) and drive
  `HufuWorkspaceAccess` over the real `IWorkspaceReader`.
- The broker supplies the canonical `HostInvocation`/`ResourceBinding` derived
  from the resolved reference; the caller cannot select provider, evaluator,
  workspace, path or wider context.
- Provider-side checks (per-resource authorization, candidate/file-content
  checks, exclusions) are preserved; outer broker authorization alone is
  insufficient.

## Release

- After the read, independently re-authorize: current AE-1 authority for the
  exact request plus recipient/session/audience binding.
- Only on release Permit may bounded result bytes leave the broker, still capped
  by `BrokerProtocol.MaximumReadResultBytes` and the reference `MaximumBytes`.
- On release denial or unavailable, return a coarse failure and disclose nothing
  protected.

## Evidence

Separate typed artifacts, both bound to operation ID, authenticated
session/recipient, reference identity (or protected digest), authority binding,
exact canonical operation digest, timestamp and coarse outcome.

| Artifact | Records | Does not record |
| --- | --- | --- |
| `BrokerStartEvidence` | start admission under the ordered current decision | the result or any bytes |
| `BrokerReleaseEvidence` | disclosure authorization for a specific recipient | delivery |

A recorded authorization can be superseded before return and must not be read as
delivered authorization (see the
[freshness rule](current-authority-profile.md#recorded-evidence-is-evaluation-evidence-not-delivered-authorization)).

## Response loss and retry

- The broker makes no promise that a response was delivered.
- A lost response grants no replay or resumption right: an identical retry runs
  fresh AE-1 authorization and fresh release authorization.
- `OperationId` is correlation/idempotency only; changed intent reusing it is
  `Conflict`.

## Failure disclosure

Coarse external classes only: `InvalidRequest`, `Unauthorized`, `Unavailable`,
`DeadlineExceeded`, `CapacityExceeded`, `OperationFailed`. No path/existence/
content/metadata/policy/parent/internal-state detail on any non-permit path.

## Out of scope for AE-2.2 (AE-2.3)

- The full adversarial cross-process corpus and race matrix as first-class
  cases, including process restart and response-loss reconciliation.
- A claim of AE-2 completion. AE-2.2 produces the vertical and its evidence
  types; AE-2.3 exercises the boundary and races and closes AE-2.

## Qualification to be added in AE-2.3 (not AE-2.2)

```text
revocation commits -> start attempts        => no provider read
start admitted -> revocation commits        => internal read may finish
revocation after read -> release check      => bytes not disclosed
worker B uses worker A reference             => rejected before provider
authority changes during AE-1 evaluation    => AE-1 stale-read-set handling
evidence failure before admission            => no provider read
provider denial/content check failure        => no protected result
response lost after release authorization    => no implied delivery/replay right
```

## Stop point

Implement AE-2.2 (framing + real read + start ordering + start/release evidence
types). **Stop before AE-2.3**; do not claim AE-2 complete until the AE-2.3
corpus exercises the actual cross-process boundary and race matrix.
