# Hufu implementation roadmap

## Current priority: product-neutral workflow authorization - 2026-10-03

This section supersedes older integration ordering. The user's new boundary is
recorded in [ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md) and the
canonical [Zhinu delivery plan](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-plan.md).
Read the [Penghou workflow contract plan](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-abstractions-plan.md)
and [handoff](zhinu-authority-handoff.md) before resuming staged work.
Penghou WA-1/2/3 are complete; `Penghou.Workflow.Abstractions` `0.1.0-preview.2`
is published and its package/consumer qualification is recorded in the
[qualification record](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-public-package-qualification.json).
Zhinu ZA-2 exact published-package source adoption, including fresh seven-package
consumer closure, is complete; see the [adoption evidence](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/workflow-package-adoption.md).
ZA-3A/3B/4 and the unchanged preview.15 compatibility suite are locally
qualified in `0.2.0-preview.1`; remote CI and user-run ZA-6 publication with
remote CI are next. Hufu workflow integration
(HA-1/2/3) follows that publication; its adapter and release remain pending.
Use the [activity queue](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-activities.md)
for ready/blocked/held work and acceptance. Independent Hufu activities can
proceed while Zhinu qualification continues.

- [x] **ZA-0:** record the source proposal, dependency inventory and revised plan.
- [ ] **HA-0A:** reconcile/reuse the independent staged changes in bounded,
  reviewed deliveries; preserve the qualified original package migration.
- [ ] **HA-0B:** split legacy runtime/process integration tests out of the core
  suite now, preserving regressions and proving standalone core test/build graphs.
- [ ] **HA-1:** implement optional `Hufu.Workflow` against Hufu core and the neutral
  Penghou.Workflow.Abstractions contract only, after the completed Zhinu phase ZA-6.
- [ ] **HA-2:** after the HA-0B split, qualify package-backed adapter/runtime
  translation, exact identity, revocation, approval resume/retry, evidence and
  standalone/transitive package isolation on .NET 8/10.
- [x] **ZA-5A:** retain the frozen legacy SQLite profile decision.
- [ ] **ZA-5B:** any replacement/retirement requires evidence preserving final
  mutation-start guarantees.
- [ ] **HA-3:** qualify the adapter's exact published package graph, release set,
  API inventories and CI/consumers, then publish Hufu separately.

Hufu core/Cedar/Biscuit already have no Zhinu dependency. The concrete SQLite
adapter and test graph require correction; do not move runtime SQL into the new
adapter. IO/Luban integrations stay independent. The previously staged
completion work is reusable evidence, not an instruction to bulk-install or
publish the old package graph. Historical checkboxes/counts below are retained.

## Published Zhinu package qualification - 2026-10-03

The optional Hufu.Zhinu.Sqlite adapter consumes exact Zhinu and Zhinu.Sqlite
preview.15 packages. Normal solution builds require no sibling source checkout.
A fresh isolated package restore passes all 426 existing integration cases across
.NET 8/10. See [adoption](zhinu-package-adoption.md) and
[evidence](qualification/public-resource-packages-with-zhinu.json).
This closes the optional Zhinu distribution gap; trusted production host and
Hufu release gates remain separate.

## Published resource-package qualification — 2026-10-03

Normal builds consume exact IO.Abstractions, IO.Protocols, IO.Local and Luban
0.1.0-preview.1 packages. A fresh isolated restore verified all four came from
NuGet.org, with no IO or Luban source projects. All 426 cases pass: 101 core,
93 Biscuit and 19 IO on each of .NET 8 and .NET 10, with no failures or skips.
See [public-feed evidence](qualification/public-resource-packages.json) and
[resource package adoption](resource-package-adoption.md). RA-5C is complete for
these dependencies; Hufu itself is not released. Explicit source switches remain
available for development. Luban v2 is deliberately rejected before authority
access. See the [current completion review](completion-review.md) for open work.

## Earlier Luban API consumer qualification — 2026-10-03

The final Luban API candidate is qualified through an exact PackageReference:
101 Hufu tests pass per framework with a fresh package cache and no Luban source
project. V1 remains supported; v2 read and diff fail closed before authority access.
See [consumer qualification](luban-api-consumer-qualification.md).
Public-feed restoration is now qualified above; Hufu v2 policy support is separate
future work and does not block completion of the Luban language baseline.

## Current registered Biscuit qualification — 2026-10-03

The missing Hufu.IO/constructor discrepancy is resolved. Read hosts now use
explicit providers; 93 Biscuit, 101 existing Hufu and 19 IO cases pass per framework.
See the [current qualification](biscuit-integration-qualification.md) and
[measurements](biscuit-budget-measurements.md). The local read consumer is
sequential per database; four-worker full-host availability remains open.
IO/Luban publication and Hufu public-package adoption are qualified above.
## Resource capability integration correction — 2026-10-02

Canonical direction: [resource-abstractions architecture](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/resource-abstractions-architecture.md).
The existing Hufu.Luban language authorizer and SQLite start adapters do not
complete a provider-independent resource decorator.

- [x] **RA-4 bounded adapter:** Hufu.IO resource interception and its 19-case
  integration suite are implemented and public-package qualified. Production
  mutation start/outcome hosting remains open; keep semantic admission separate.
- [x] **RA-5C:** consume exact published IO/Luban packages and qualify the three
  integration suites with a fresh NuGet.org resource-package cache and no
  Penghou/Luban source checkout; see the public qualification above.
- [ ] Resolve RA-G1: initial denial invokes the provider zero times; discovered
  child resources and prepared mutations require neutral cooperation at their
  actual access/start boundaries. An outer request-only check is insufficient.
- [ ] Prove exact immutable request binding, denied child non-disclosure,
  current authority, revocation between calls and explicit in-flight semantics,
  required evidence failure, and conditional mutation start/outcome behavior.
- [ ] **VFS-4/6, deferred:** record attempts and decisions outside the provider
  and compare them with Fuwen declarations; report coverage and uncertainty.
- [ ] **VFS-5, deferred:** distinguish enforcement from observation mode. Observation
  uses a separately authorized simulation envelope, including real base reads.
- [ ] **VFS-7, deferred:** simulated approval/receipts cannot authorize real apply;
  bind fresh authority and starts to the exact frozen delta and current versions.

This is documentation of intended work, not a new enforcement claim. Preserve the
current governed-start gates while converging on RA contracts; no production VFS
or authority-lifecycle bypass is introduced. Every handoff cites the canonical gates.

Status: Updated 2026-10-02. Checkboxes describe completion, not intent. A bounded current-snapshot/read-authorizer prototype exists; it requires trusted host-provided authentication, evaluation and evidence. An optional SQLite current-state/evidence prototype is implemented; no complete issuance service or production operation-start/revocation fence is supplied.

Policy-backend direction is selected in [ADR 0002](decisions/0002-use-cedarsharp-for-policy-evaluation.md):
use official Cedar through the independent CedarSharp 1.0.0 wrapper, which is
implemented, published, and qualified on its documented CI matrix. The
Penghou.Hufu.Cedar's fixed-schema evaluator and known-root read consumer pass
the narrow local Windows x64 qualification. Durable enforcement remains pending.

[ADR 0003](decisions/0003-local-first-authority-runtime.md) selects the local-first
runtime direction. Evidence and observability are foundational. Prioritize store
and lineage contracts, deterministic explanations, execution requirements, and
one local filesystem broker. [ADR 0004](decisions/0004-typed-effects-over-sandbox.md)
selects LOP. [ADR 0005](decisions/0005-luban-owns-typed-effects.md) establishes
[Penghou.Luban](https://github.com/jenolaszlo-sketch/penghou-luban/blob/main/README.md) as the separate effect-runtime
owner. Its [roadmap](https://github.com/jenolaszlo-sketch/penghou-luban/blob/main/ROADMAP.md) and
[implementation plan](https://github.com/jenolaszlo-sketch/penghou-luban/blob/main/docs/implementation-plan.md) own
effect-provider delivery, while Penghou owns shared resource I/O providers.
Hufu tracks [authority integration](luban-integration.md).
See the [current authority prototype profile](current-authority-profile.md) for
its exact supported surface and limits.
Neither project will build a sandbox. StrictEffects rejects arbitrary native
execution; broader tool/provider modes are explicit.

## M0: project and design home

- [x] Select the name Penghou.Hufu and separate library boundary.
- [x] Create a minimal .NET 8 / .NET 10 core project and solution.
- [x] Consolidate the authority specification, original proposal, architecture, and ADR here.
- [x] Record CedarSharp as the planned policy-engine integration in ADR 0002.
- [x] Record the local-first runtime direction and reviewed expansion in ADR 0003.
- [x] Adopt typed effects and remove owned sandbox implementation through ADR 0004.
- [x] Establish Penghou.Luban ownership and move its effect design/source proposal out of Hufu under ADR 0005.
- [ ] Complete the first implementation API/schema review with one concrete host and resource broker.

The initial license follows Fuwen's reusable-library convention: Apache-2.0. No package publication or remote repository is implied by local setup.

## M1: contracts and deterministic evaluation

The [revised Luban delivery plan](https://github.com/jenolaszlo-sketch/penghou-luban/blob/main/docs/implementation-plan.md)
starts with shared request identity and a real Windows read-only provider, then
migrates its existing handlers before language/preview/mutation work. Hufu and
CedarSharp do not block that neutral slice. Hufu contracts/store work can run
independently; real governed effect/batch admission follows qualified providers
and Hufu's own authority/Cedar/store gates. Controlled standalone test policies
are not a substitute for these production integration gates. See the
[shared provider plan](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/implementation-plan.md).

- [ ] Define generic execution subject, immutable authority records, exact admission bindings, and typed decision outcomes.
- [x] Implement bounded current authority snapshot/request/decision contracts and a fail-closed known-root Luban read-authorizer prototype with required snapshot-source, evaluator, and decision-recorder interfaces. This does not authenticate snapshot construction, persist authority, or serialize Zhinu operation start with revocation.
- [x] Implement the fixed-schema typed-grant `CedarAuthorityEvaluator` prototype with closed layer projections and fail-closed diagnostic handling; the Windows x64 read/store/start composition [passes 95 tests](operation-start-qualification.md) on each runtime; production governed host guarantees remain open.
- [ ] Define capability-family evaluator and resource-binding contracts with pinned versions.
- [ ] Implement grant coverage and attenuation without ambient or bearer-reference authority.
- [x] Keep Hufu core free of Cedar dependencies; specify the Hufu.Cedar adapter boundary and trusted schema mapping for the current typed path profile.
- [x] Implement the current typed path-profile Hufu.Cedar evaluator with exact engine/schema/policy/entity identities and full diagnostic handling against CedarSharp 1.0.0. Production durable evidence and admission remain separate gates.
- [x] Prove default deny, mandatory forbid, hierarchy exceptions, cross-tenant rejection, and blocking of Allow-with-errors for the current typed projection, including a real native Allow-with-errors fixture.
- [ ] Compose parent/run/activity/delegation checks as restrictions, without permit-union escalation; prove supported scope containment and reject unknown comparisons.
- [ ] Define authenticated issuer/approver authority, tenant isolation, mandatory-deny precedence, and explanation redaction.
- [ ] Prove unknown/unsupported contracts fail closed and grants cannot be combined to manufacture rights.
- [ ] Specify versioned execution contracts and trusted requirement handlers, with precondition/continuous/post-operation evidence and rejection of unsupported requirements.
- [ ] Define structured deterministic explanations and authorized projections of lineage, exclusions, policy versions, and missing requirements.
- [ ] Design decision/start/receipt evidence identities and failure semantics together with store and broker contracts.
- [ ] Review Luban's neutral descriptor/provider contract and define Hufu's trusted authority mapping, version bindings, and integration adapter; keep workflow-specific IDs outside both cores.
- [ ] Implement a Hufu-backed replacement for Luban's required neutral effect checker after Hufu's authority contracts exist; bind exact invocation and recheck every traversed resource, not just a root grant.
- [ ] Define StrictEffects default composition and explicit ApprovedTools boundaries; unknown effects and shell/process payloads reject without fallback.

Gate: deterministic evaluator tests plus a real host/broker consumer; no arbitrary-process security claims.

Dependency: CedarSharp 1.0.0 supplies the independently qualified authorization,
validation, diagnostics, and version-discovery surface documented in its
[verification ledger](../../CedarSharp/docs/verification.md). Hufu contracts
and store work can proceed independently; production Cedar-backed enforcement
still requires the Hufu.Cedar adapter and Hufu's authority/resource-boundary
gates. A custom general policy language is not the fallback for an unavailable
adapter or authority runtime.

## M2: durable authority store

- [x] Specify the initial current-slot transaction, tenant-wide idempotency, expected-sequence, terminal revocation, bounded storage, and reopen semantics in [ADR 0008](decisions/0008-current-authority-store.md). Broader approval/delegation application and operation-start recovery remain open.
- [x] Implement the initial optional Penghou.Hufu.Sqlite adapter for immutable current snapshots, revision/fence advancement, revocation tombstones, authorized bounded history, and attributable decision evidence. See the [store profile](durable-authority-store.md) and [qualification](durable-authority-store-qualification.md); this is not a governed mutation-start host.
- [ ] Persist requests, decisions, grants, envelope versions, revocation, and authority admission bindings.
- [ ] Persist/refer to exact Cedar policy, schema, and entity snapshots with provenance; avoid an independently mutable shadow policy store.
- [ ] Persist tenant-bound lineage, exclusions, approval/requirement versions, decision evidence, and authenticated broker receipt references; test invalid/cyclic/cross-tenant lineage.
- [ ] Add lightweight OpenTelemetry-compatible tracing and bounded metrics with host-selected export; test redaction and exporter independence from mandatory evidence.
- [ ] Prove denied/indeterminate decision attribution, failed pre-dispatch evidence blocking, idempotent receipts, and uncertainty after possible external completion.
- [ ] Exercise crash/reopen, duplicate commands, conflicts, partial decision application, and tenant isolation.
- [ ] Extract reusable store conformance helpers into Penghou.Hufu.Testing only when a second adapter/consumer benefits.

Gate: durable state never widens authority on replay; inspectable provenance and typed failures survive restart.

## M3: complete protected-operation boundary

- [ ] Integrate Luban's planned authorized preview resolution and immutable ResolvedEffectPlan under [ADR 0007](decisions/0007-preview-resolution-commit-barrier.md), distinct from static preflight and effect-free policy simulation.
- [ ] Admit the complete known mutation set before the executor commit barrier; pin exact targets, payloads, versions, dependencies and coverage. Prove any known denied mutation or required incomplete coverage gives zero proposed writes, with no partial-grant prefix or silent lazy downgrade.
- [ ] Prove WhatIf never invokes requested mutations or opaque/lazy tools, frozen manifests cannot expand at commit, and changed observations or state-dependent later segments require new resolution/admission.
- [ ] Revalidate authority/revision/fence and available batch preconditions at the barrier, then every actual I/O. Qualify restart and post-start partial/ambiguous outcomes without claiming a multi-file transaction.

- [ ] Integrate shared Penghou.IO.Abstractions resource contracts through a host-selected Hufu adapter; retain semantic effect admission and exact identity through all concrete I/O checks. Windows read-provider migration is implemented under ADR 0006; Hufu governance, mutations and web remain pending.
- [ ] Preflight complete pipelines against known targets/authority before upstream work; test denied downstream writes block early and stale previews never bypass final revocation/resource checks. Bound and cancel intermediate production for dynamic targets.

- [ ] Integrate one trusted resource broker with authenticated execution context and final I/O checks.
- [ ] Integrate Luban's qualified Read/Find/SearchText/ApplyPatch slice with real Hufu grants, exclusions, execution requirements, and evidence; effect handlers stay in Luban and shared resource I/O providers stay in Penghou.
- [ ] Prove root-allowed/child-excluded search and find results never disclose the child path or content, and denied or unavailable checkers perform no unauthorized protected I/O.
- [ ] Admit Git inspection only after Luban's helper/network/output qualification passes, and prove Hufu read exclusions apply across both Git and file effects.
- [ ] Bind Luban's limits, preconditions, and platform/recovery guarantees to admission; reject unsupported guarantees rather than recreating provider logic here.
- [ ] Integrate Luban's planned versioned typed IR when available: pin semantic identity, trusted compiler/catalogue/schema/provider versions, payloads, limits, and workspace bindings; prove aliases cannot widen exact approval and every pipeline effect/discovered target is authorized.
- [ ] Coordinate mutation outcomes with Luban receipts and the runtime journal; neither a hash nor ReplaySafe flag proves safe retry.
- [ ] Test canonical paths, aliases, case rules, links/junctions, and changed-resource races against the actual object used.
- [ ] Enforce structured requirements and report the exact handler versions/evidence; reject unsupported guarantees without dispatch.
- [ ] Add local opaque credential-use bindings as required by the first credentialed operation; keep raw secrets outside authority records and agent context.
- [x] Implement the experimental [co-located SQLite start profile](operation-start-profile.md) under [ADR 0009](decisions/0009-colocated-operation-start.md): actual Zhinu generation/step/lease checks, current authority, acquisition and mandatory evidence in one writer transaction. Exact replay never redispatches. This supplies block-new-starts semantics, not drain-before-acknowledgement or filesystem transactions.
- [ ] Connect a real governed Luban single-patch host, exact semantic admission, final resource checks and terminal Completed/NoMutation/Ambiguous recovery to that start gate; qualify provider/start races and response loss before claiming a governed mutation release.
- [ ] Bind data-release and exact-effect approvals where the supported operations require them.
- [ ] Reference existing budget reservations; do not create a second accounting ledger.
- [ ] Publish typed provider profiles and tests proving supported strict effects, unknown-shell rejection, and no alternate execution route in the configured agent surface.
- [ ] Reject unsupported execution guarantees; sandbox construction and arbitrary-process execution are outside this milestone and the Hufu implementation roadmap.

Gate: demonstrate permitted I/O, denied zero unauthorized I/O, revocation, stale-context rejection, and crash-safe reconciliation through the actual boundary. Earlier milestones alone are not a secure execution release.

### M3.3: optional registered Biscuit adapter prototype

- [x] Implement the experimental Penghou.Hufu.Biscuit profile and optional SQLite registration/revocation/evidence/start composition. The 93 local Biscuit tests per target framework exercise real Cedar and Biscuit evaluation, both evidence stores, SQLite revocation/key-retirement ordering and a SQL-fixture runtime participant. See the [profile](biscuit-integration-profile.md), [qualification](biscuit-integration-qualification.md) and [ADR 0010](decisions/0010-registered-biscuit-profile.md).
- [x] M3.4 read slice: qualify real Windows Hufu/Luban and Hufu.IO/Biscuit reads with current Cedar, both evidence stores, metadata/traversal/content/release, excluded children, revocation and qualified alias/junction checks.
- [x] Measure the actual fixed-policy/local-host workload at 1/8/32 blocks and 1/4 workers; retain explicit local 2000-fact/50-iteration/100-ms limits and sequential complete checks per database.
- [ ] Complete published IO adoption, concrete host authentication/custody/capacity, full supported consumer/API conformance and adapter freeze. Exact physical-object mutation start/outcome recovery and drain guarantees remain separate gates.

The M3.3 checkbox records implementation and local prototype evidence only. The read slice is locally qualified; complete M3.4 and publication remain open. This adapter is unpublished and does not delay or satisfy the real governed Luban operation-start/outcome gate.

## M4: Penghou workflow integration

- [ ] Fuwen representation, canonical identity, requirement summaries, and catalogue bindings.
- [ ] Zhinu exact admission references, durable activation and waits, recovery, and operation journal integration.
- [ ] Guihua authority-aware proposals, typed requests, and compliant denial-aware alternatives.
- [ ] Qingniao attenuated subject/provider binding and parent revocation behavior.
- [ ] Host approval surface with exact deltas, partial grants, staged work, and idempotent decisions.
- [ ] Support exact-effect approval, bounded temporary elevation, expiry, and exact resource/artifact/destination bindings; retain actors needed for future governance.
- [ ] Deliver authority-state diffs and bounded counterfactual simulation with versions, query domain, assumptions, and explicit completeness/unknown results.
- [ ] Prove simulations do not mutate authority or execute effects; historical replay never substitutes for containment proof or current admission.
- [ ] Fuwen declares effect kinds/versions and supplies control flow; Qingniao attenuates both effect kinds and resource authority.
- [ ] Guyabano hosts local StrictEffects; Marang forwards to qualified supervisors with durable pending requests, authenticated exact-result bindings, final start checks, and stale/conflicting completion rejection.
- [ ] Zhinu reconciles effect identities, duplicate completion acknowledgements, retained read artifacts, and ambiguous mutations without repeating completed effects.

Typed-effects gate: find/search/read and patch only src/** without a shell;
reject out-of-scope/stale mutations and unsupported commands; survive restart
without duplicate mutations; inspect the complete authority/effect evidence chain.

Gate: the end-to-end migration example in the specification survives restart, partial approval, re-admission, and revocation without double spending or stale activation.

## M5: broader autonomy

- [ ] Additional provider profiles and storage adapters based on actual consumers.
- [ ] Concurrent fan-out and nested delegation with shared-budget proof.
- [ ] Standing bounded effect policies, isolated candidate preparation, and exact commit approval.
- [ ] Optional Hongxian/Siming projections and improved operator explanations.
- [ ] Automatic review within deterministic eligibility rules; learned suggestions never self-install policy.
- [ ] Evaluate optional CedarSharp.Analysis containment proofs with explicit assumptions and fail-closed handling of unsupported/unknown/timeout results.
- [ ] Integrate later Luban effect families only as its qualified providers become available, with explicit Hufu authority and broader native-tool admission where applicable.
- [ ] Reuse Luban provider conformance results and maintain Hufu-specific authority/revocation integration tests; avoid duplicating its package or implementation roadmap.
- [ ] Evaluate graph storage, SPIFFE/Vault host adapters, multi-person/separation-of-duties approvals, and signed or externally anchored evidence only with a concrete use case.
- [ ] Add optional AI explanations over authorized deterministic projections without changing decisions or installing policy.
- [x] Finalize the optional Hufu/BiscuitSharp integration specification and
  source-reviewed handoff (2026-10-02, BiscuitSharp commit 69ff07a); preserve the
  original proposal and explicit amendments. See the
  [specification](../../biscuit-sharp/docs/hufu-integration-spec.md),
  [handoff](../../biscuit-sharp/docs/hufu-integration-handoff.md) and
  [integration ADR](../../biscuit-sharp/docs/decisions/0003-hufu-integration-profile.md).

The separate [ADR 0010](decisions/0010-registered-biscuit-profile.md) and
[qualification record](biscuit-integration-qualification.md) now track the
experimental M3.3 adapter and SQLite start composition. M3.4 real-consumer
qualification remains open; the earlier wrapper-only policy checks remain
separate BiscuitSharp evidence. This work remains optional and does not satisfy
the governed Luban operation-start/outcome gate. Hufu core stays independent of
BiscuitSharp.

Independent old-revision branch continuation and deployment as a network service remain optional. They must not delay the first complete protected-operation integration.

Luban owns the planned [surface language](https://github.com/jenolaszlo-sketch/penghou-luban/blob/main/docs/language-syntax-spec.md),
including bounded typed pipelines and closed pure filters. Hufu consumes its
trusted lowered requirements and does not implement a second parser or executor.

Out of scope: implementing OS/container sandboxes, arbitrary scripting, shell
byte-stream pipelines, interactive shells, or PTYs. Any future UnrestrictedProcess route is
an explicit external-provider/host integration with separately reviewed actual
guarantees, never a Hufu-owned sandbox project or an automatic fallback.

## Workflow contract checkpoint - 2026-10-03

Penghou.Workflow.Abstractions `0.1.0-preview.2` is published. Source commit
`5a76b7c` passed all seven CI jobs in [run 37115430526](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37115430526);
all four publication jobs passed in [run 37116694209](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37116694209).
Exact package contents match CI apart from the repository signature; fresh-cache
NuGet-only consumers pass on .NET 8/10. See the [release checkpoint](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-package-release-handoff.md)
and [qualification record](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-public-package-qualification.json).
WA-1/2/3 and Zhinu ZA-2 source adoption are complete, including the fresh
seven-package consumer graph. ZA-3A/3B/4 are locally qualified and pushed; remote CI and
user-run ZA-6 publication with remote CI are next. Hufu HA-1/2/3 follows that
publication. Preserve independent HA-0A/B cleanup, keep the older staged Hufu
snapshot on hold, and retain the frozen legacy-profile decision. Contract package
evidence does not replace the Zhinu runtime qualification or authorize effects.

## Current next activities

Verify remote CI, then user-publish Zhinu candidate `0.2.0-preview.1` through **ZA-6**; Hufu **HA-1/2/3** follows publication.
ZA-2 exact published-package source adoption and fresh seven-package consumer
closure are complete, and ZA-3A/3B/4 plus the unchanged preview.15 compatibility
suite are locally qualified. Preserve independent **HA-0A/B** cleanup and the frozen
legacy-profile decision; keep the older staged Hufu snapshot on hold. Luban
**LW-1** is optional neutral-host integration, and its language core stays
independent of workflow authorization.
See the [activity queue](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-activities.md).

## Historical host gate - separate qualification stream

The optional current-state/evidence store and legacy co-located Hufu/Zhinu start
transaction are implemented. Their block-new-starts profile allows earlier
committed starts to finish after revocation acknowledgement. The separate
production host stream qualifies complete-plan approval, locked-object binding,
final resource checks and durable terminal recovery. Staged single-patch/journal
components are reviewed under HA-0A; they do not establish a production trusted
host. Batches and stronger revocation-drain guarantees remain later gates.
Sequential Hufu lookup followed by standalone Zhinu acquisition is still not
an atomic substitute. This stream is not the neutral adapter's immediate queue.

Current exact-package and isolated consumer evidence is recorded in [resource package adoption](resource-package-adoption.md). Public-feed qualification is complete for IO/Luban; Hufu release remains open.
