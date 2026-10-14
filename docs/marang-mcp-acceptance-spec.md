# AE-5A spec: Marang MCP delegated-authority acceptance proof

Status: **Specification frozen 2026-10-12; AE-5A core semantic landed.** This is
the narrow first slice of [AE-5](authority-enforcement-implementation-plan.md):
prove that `marang_delegate` acceptance requires **current Hufu authority**, not
merely a valid delegated credential.

**Implementation status (2026-10-12).** The Hufu-side neutral contract landed in
`Penghou.Hufu/DelegationAcceptance.cs`: `DelegationAcceptanceOperation.Accept`,
`DelegationAcceptanceRequest`, `IDelegationAcceptanceEvaluator`,
`IDelegationAcceptanceRecorder`, `IDelegationAcceptanceAuthorizer`, and
`CurrentDelegationAcceptanceAuthorizer` (current snapshot + optional lifecycle
gate + mandatory evidence + freshness). Additive: the resource action
vocabulary, Cedar schema and Biscuit mapping are unchanged. 7 focused tests pass
on .NET 8/10. **Pending:** the Marang `marang_delegate` wiring (credential via
HTTP metadata, an acceptance evaluator/policy, the gate at the top of
`AcceptAsync`) and the end-to-end qualification table below.

**AE-5A.1b complete (2026-10-14).** The Hufu nine-package `0.1.0-preview.8`
release is published, including `Penghou.Hufu.Biscuit` and
`Penghou.Hufu.Biscuit.Sqlite` (dependency `BiscuitSharp 0.1.0-preview.2`), and
verified from a fresh cache. The public package boundary required by AE-5A now
exists, so **AE-5A.2 (Marang wiring) is unblocked**: Marang consumes
`Penghou.Hufu`, `Penghou.Hufu.Biscuit` and `Penghou.Hufu.Biscuit.Sqlite` at
`0.1.0-preview.8` from NuGet with no local feed and no source reference.

## Architectural rule

```text
credential verification   != authorization
Accept authority          != resource authority
Accept authority          != Derive authority
accepting work            != permission to perform every operation inside it
```

A valid delegated credential may describe/carry authority. `marang_delegate`
succeeds only if Hufu says that authority is still current and sufficient to
accept the bounded work at acceptance time. An accepted delegation must later
independently authorize `ReadFile`, `ExecuteProcess`, etc.; **`Accept` never
confers resource rights by implication.**

## The acceptance mapping (decision B)

Add **one narrow Hufu semantic**:

```text
DelegationAcceptanceOperation.Accept
```

- Do **not** alias it to `AuthorityStoreOperation.Derive`. `Derive` means "may
  create/attenuate authority"; `Accept` means "may cause this bounded delegated
  work to be accepted." Related, but not the same operation.
- Do **not** infer it from workspace/resource authority. Possession of authority
  over a workspace does not imply permission to start delegated execution there.
- Do **not** let acceptance proof confer execution/resource authority.

### Acceptance subject

The authorization subject is the acceptance action **plus** the bounded
delegation identity — not merely the workspace:

```text
operation: Accept

subject:
  caller (tenant + subject)
  acceptance identity (acceptance key / deterministic delegation id)
  workspace ceiling          (a constraint on what is accepted, not the authority)
  provider
  generation
  bounded request shape (budget/objective-shape digest; no raw objective content)
```

The workspace is a bound on what is being accepted; it is not itself the
authority.

### Evaluation projection

`Accept` is evaluated by the same current-authority engine and discipline as
AE-1 (current snapshot; ancestor/current-parent policy for derived authority;
lifecycle gate; optimistic read-set freshness; mandatory evidence), but as a
**distinct operation** with its own action in the fixed policy schema. The
acceptance identity, provider and bounded request shape are bound into the
request identity/digest so the decision is attributable to the exact acceptance
attempt. Unknown/unsupported projection rejects. (The single
representation choice — a dedicated schema action mapped from
`DelegationAcceptanceOperation.Accept` — is frozen at implementation; it must not
reuse `Derive` or a resource action.)

## Boundary and placement

```text
MCP request
  |
  | API-key authentication (host identity)
  v
host workspace ceiling (coarse host policy)
  |
  | Biscuit credential verification  (AE-3B; credential-only, NOT authorization)
  v
Hufu: DelegationAcceptanceOperation.Accept against the exact bounded acceptance request
  |
  | current-authority decision (AE-1 qualified composition)
  v
record authority-decision evidence
  |
  v
_descriptors.EnsureAsync(...)
  |
  v
normal existing acceptance protocol (visibility, local acceptance, agreement check)
```

- The credential enters via **HTTP metadata on `/mcp`**, placed in
  `HttpContext.Items` beside the caller identity — never as a `marang_delegate`
  tool argument.
- The gate runs at the **top of `MarangDelegationCatalog.AcceptAsync`, before
  `_descriptors.EnsureAsync`** (and before visibility publish and any in-memory
  acceptance mutation). Tampered/invalid credentials and inadequate/revoked
  authority fail before any durable acceptance state exists. It is not only in
  the MCP tool.

## Raw objective vs authorization

The objective text is opaque work content. Authorization binds the **bounded
request shape** (provider, workspace ceiling, generation, budget and a digest of
the exact request), not the objective's semantics. The host keeps objective
content out of default explanation/telemetry output.

## Evidence

One combined, Hufu-owned acceptance-authority artifact, referenced by identity
(the Marang descriptor must stay **authority-free by construction**):

- acceptance identity (acceptance key / delegation id) and generation;
- credential identity (engine/mapping/key and token fingerprint);
- Hufu acceptance decision identity (snapshot identity/version, evaluator
  identity, reason code);
- exact bounded acceptance-request digest;
- evaluated instant and outcome.

Mandatory evidence failure blocks acceptance. The descriptor/visibility records
never absorb authority; they reference the decision.

## Why a fresh check at acceptance (not staleness)

Acceptance runs a **fresh** current-authority check. A revocation between
credential creation and `marang_delegate` → **denied**, no acceptance. A
revocation **after** acceptance does not retroactively un-accept
([ADR 0014](decisions/0014-authority-delegation-enforcement.md)); the
delegation's later operations must independently re-check current authority.
Acceptance is an admission decision, not a stored permission.

## Minimum qualification cases

| Case | Expected |
| --- | --- |
| Valid credential + current authority | accepted |
| Valid credential + revoked authority | denied |
| Valid credential + narrowed parent (no longer contained) | denied |
| Attenuated valid child, sufficient | accepted |
| Tampered credential | rejected before Hufu decision and before any state mutation |
| Valid signature but insufficient authority | denied |
| Replay / idempotent retry (`requestKey`) | preserves existing acceptance semantics (deterministic delegation id; conflicting content conflicts) |
| `Accept` succeeded, later `ReadFile`/`ExecuteProcess` | independently authorized; `Accept` confers no resource right |

## Non-goals

`status`, `result`, `cancel`, `intervene`, `inspect`, artifacts, all MCP tools,
A2A, a generic MCP authorization framework, new gateway/SDK ownership, package
publication, and any change to the opaque-reference route or core resource
semantics beyond the single `DelegationAcceptanceOperation.Accept` semantic.

## Exit criterion

`marang_delegate` acceptance is gated by `DelegationAcceptanceOperation.Accept`
through the AE-1 qualified current-authority composition, placed before durable
acceptance state, with the qualification table above passing, and evidence
proving both credential validity and independent current authority.

## AE-5A closure (2026-10-14)

```text
AE-5A: CLOSED

AE-5A.1b
  Hufu 0.1.0-preview.8 published as nine public packages
  public-only package qualification complete

AE-5A.2
  Marang commit 0bce996
  Hufu Accept gate mandatory and before all acceptance-side state
  credential transported via HTTP metadata
  evaluator/recorder integration qualified
  public NuGet-only dependency graph proven
  AE-5A end-to-end qualification table complete

Carried residuals:
  - Marang.Server default host has no live Hufu authority source and therefore
    fails closed until one is composed
  - Biscuit cryptographic/registry verification is host-supplied
  - unrelated pre-existing README.md / docs/roadmap.md checkout edits remain
```

The default host being non-operational for acceptance is intentional and
fail-closed, not a bypass or defect: it is a host-composition residual, not
unfinished acceptance-authority semantics. Likewise the envelope-shape verifier
is non-cryptographic by design; signature/registry trust stays with the
host-supplied verifier. Do not reopen AE-5A to wire the default host — that is
a separate host-composition work item.
