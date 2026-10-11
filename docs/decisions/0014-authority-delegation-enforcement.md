# ADR 0014: Separate authority, delegation credentials and enforcement

Date: 2026-10-11.

Status: **Design direction selected; broader integrations remain gated.**
Requested as a documentation/design clarification. No code or public API change
is made by this decision.

## Context

Hufu has reusable authority and derivation contracts, a registered Biscuit
prototype, and experimental workflow/native-execution compositions. The supplied
[proposal](../archive/authority-delegation-enforcement-proposal-2026-10-11.md)
connects these pieces but risks conflating workflow state, current authority,
credential attenuation and a running execution's OS policy.

The [review](../authority-model-review.md) also finds stronger proposed guarantees
than the current source supplies. In particular, native lifecycle providers do
not implement file/network isolation, and online authorization alone is not an
atomic revocation/start gate.

## Decision

1. Hufu owns authority semantics and state under authenticated host policy.
   Zhinu owns workflow state. Workflow transitions are trusted decision inputs;
   they never grant rights merely by occurring.
2. An activity can have 0..N executions or use brokers without an execution
   domain. Same-process activities do not acquire separate OS identities.
3. Grant derivation, credential representation/attenuation, and preparation of
   an enforcement policy are distinct. Derivation requires both containment and
   the actor's permission to derive. Current applicable ancestor restrictions
   remain relevant at use; wider parent rights never enlarge an existing child.
4. The initial restricted-execution policy is immutable. Preparation produces
   exact concrete facts that the trusted caller authorizes before single-use
   release. Changed rights or bindings require explicit re-resolution; changed
   OS policy requires a new execution. The provider must refuse unsupported
   restrictions and cannot substitute lifecycle management for confinement.
5. Revocation can deny future uses of an existing grant/token without issuing
   replacements. State freshness, start ordering, in-flight completion and
   domain termination are separate profile obligations. No global instantaneous
   revocation claim follows from a final lookup or a stop request.
6. Biscuit is optional. Its later purpose is portable constrained delegation
   across a concrete trust boundary. The first selected proof is worker → host
   broker with online current authority, after the same broker works without
   Biscuit. Preserve registration and online checks in the existing profile;
   offline descendants, audience exchange and disconnected verification require
   separate decisions. Hufu core remains credential-format independent.
7. A2A/MCP gateways enforce their actual routes and map authority into supported
   downstream controls. A filtered tool menu is not evidence of remote process
   restrictions. Internal credentials remain within trusted infrastructure.

The [canonical model](../authority-delegation-enforcement.md) defines terminology,
examples and receiver obligations. The [roadmap](../roadmap.md) defines acceptance
gates and records the selected first consumer.

## Relationship to prior decisions

- Preserve [ADR 0011](0011-neutral-zhinu-authority-extension.md): neutral workflow
  preflight remains separate from final resource/start enforcement.
- Preserve [ADR 0012](0012-derived-authority-lineage.md): identical delegation and
  generation can reuse an idempotent child grant on retry. Fresh authorization
  does not universally require fresh issuance.
- Preserve [ADR 0013](0013-delegability-of-derived-authority.md): delegability is
  checked at issuance; its withdrawal does not retroactively revoke a child.
  Child validity and ancestor liveness remain independent obligations.
- Refine [ADR 0010](0010-registered-biscuit-profile.md): its registered same-binding
  prototype is not yet the general portable carrier. Reconcile its containment
  and lineage with core before extending it to another subject or audience.
- Retain the language-neutral roadmap amendment: Luban is optional and parked
  for new language work. Earlier StrictEffects/LOP descriptions do not make a
  language mandatory or forbid separately qualified native execution.
- Preserve existing single-patch and legacy start guarantees; no generalized
  diagram replaces their exact ordering contracts.

## Consequences

Documentation can describe a coherent target without claiming the target is
already implemented. Host and provider qualification stays explicit. Code gaps
are recorded before stronger claims are made; existing APIs are not changed by
wording alone. Historical ADRs, source proposals and evidence remain intact.

The first broker proof needs no new sandbox capability. A future combination
with restricted execution requires a supported prepared broker channel and its
own bypass/termination evidence. Offline revocation semantics, arbitrary nested
delegation, new action families and broad gateway support remain deferred.
