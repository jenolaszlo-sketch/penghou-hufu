# AE-2.3 qualification spec: cross-process, race and adversarial corpus

Status: **Specification frozen 2026-10-12; corpus implemented and locally validated. AE-2 complete for the cooperative-worker profile.**
This closes AE-2 against the AE-2.2 vertical
([spec](broker-ae22-implementation-spec.md)). It is a qualification/test
milestone: prove the existing vertical survives real process boundaries, races,
restart, lost responses, saturation and malformed/adversarial traffic. No new
architecture was introduced.

**Qualification result (2026-10-12).** The corpus landed in
`Penghou.Hufu.Broker.Tests` with a private cross-process worker harness
(`Penghou.Hufu.BrokerWorkerHarness`). Cases covered: separate worker process
authorized read; second worker cannot use the first worker's reference;
revoked reference before read; unknown bootstrap; single-use bootstrap reuse;
restart invalidating old session/reference; identical retry re-authorizing with
fresh start/release evidence; mutation-first blocking a handler start
(revocation wins) with no provider access; unknown protocol version; unknown
field; oversized frame (connection closed, no provider access). One narrow
defect was found and fixed in its own change: a malformed read frame closed the
connection instead of returning typed `InvalidRequest` (server error mapping
only; no architecture change). 49 focused broker tests pass on .NET 8 and .NET 10.

Unqualified by this record: hostile same-account containment (AE-4), Biscuit
(AE-3), restricted execution (AE-4) and gateways (AE-5).

## Purpose

AE-2.2 proves the cooperative-worker vertical in-process. AE-2.3 proves it
against the boundary it claims to govern: separate worker and broker processes,
concurrent authority changes, restarts, response loss, capacity limits and
hostile *traffic* (still not hostile same-account containment). No new
architecture is introduced; any contract gap discovered here returns to its
owning milestone rather than being patched here.

## Frozen invariants under test

| Case | Expected |
| --- | --- |
| Revocation/lifecycle commits first, then start attempts | No provider read; coarse `Unauthorized` |
| Start admitted first, then revocation commits | Internal read may finish; release re-check denies disclosure if authority moved |
| Revocation after read, before release | Bytes not disclosed |
| Worker B presents worker A's reference | Rejected before any provider access |
| Authority changes during AE-1 evaluation | AE-1 `authority.read-set-superseded` handling; no usable permit |
| Mandatory start evidence fails before admission | No provider read |
| Provider denial / content-check failure | No protected result |
| Response lost after release authorization | No implied delivery or replay right; identical retry gets fresh authorization and fresh release |
| Same `OperationId`, changed intent | `Conflict` |
| Oversized / unknown-version / unknown-field request | `InvalidRequest` before authority resolution |
| Bootstrap reuse or replay | Rejected; single-use |

## Corpus

### A. Real process boundary
- Separate worker process and broker process over the named pipe; inspect
  provider/evidence traces, not only transport statuses.
- Prove worker A's reference cannot be used by worker B; wrong
  tenant/subject/audience/generation fails; a child cannot borrow parent-only
  scope.
- Prove denial reaches provider content access (no bytes, no path/content/
  existence/candidate/policy/parent/internal-state disclosure).

### B. Races (first-class, not edge cases)
- Deterministic interleavings for the frozen race matrix, driving the
  `BrokerStartArbiter` boundary and the independent release check.
- Assert the arbiter holds the binding only across AE-1 + start evidence, never
  across the provider read.

### C. Restart and recovery
- Broker process restart mid-operation; worker reconnect with a fresh
  bootstrap.
- Authority/lifecycle/reference/session store reopen; admitted read vs a new
  start after restart.
- A recorded start receipt is historical evidence and never authorizes a new
  start; a recorded release receipt is never a delivery proof.

### D. Lost response / retry
- Response loss after admission and after release; assert no resumption right.
- Identical retry runs fresh AE-1 authorization and fresh release authorization.

### E. Saturation and bounds
- Bounded connections/sessions per worker and concurrent reads per session;
  exceeding a bound yields `CapacityExceeded` and nothing protected.
- Bounded request/result sizes and deadlines; no unbounded queue or key growth.

### F. Malformed and adversarial traffic
- Oversized frame, truncated frame, unknown protocol version, unknown JSON
  fields, wrong reference/action/workspace/path, replayed reference, reused
  bootstrap secret, deadline beyond maximum, non-Windows platform guard.

## Evidence and disclosure rules

- Start and release evidence remain distinct and are inspected; neither is
  treated as delivery.
- No protected disclosure on any non-permit path (coarse external classes only).
- Closed telemetry labels; no protected context in telemetry or default output.

## Exit criteria (AE-2 complete)

- The corpus passes on separate processes with a real qualified provider.
- No protected disclosure on any failure; typed internal outcomes preserved.
- Restart, lost-response and saturation behavior match the frozen rules.
- Profiles, qualification records and compatibility notes updated to describe
  only what is proved.

## Non-goals

Hostile same-account bypass closure (AE-4 provider/channel gate); Biscuit
(AE-3); restricted execution (AE-4); A2A/MCP (AE-5); any new broker
architecture or contract beyond what AE-2.2 froze.

## Stop point

Produce and review this corpus spec, then implement the corpus. Do not add new
architecture. AE-2 is not complete until this corpus passes.
