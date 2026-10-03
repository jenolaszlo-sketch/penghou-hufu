# Hufu architecture and ownership

Current integration direction (2026-10-03): [ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md)
and the [neutral authority-extension plan](../../Penghou.Zhinu/docs/authority-extension-plan.md)
supersede earlier coupling and delivery order below. The
[Penghou-owned workflow contracts](../../Penghou/docs/workflow-abstractions-plan.md)
live in Penghou.Workflow.Abstractions. Zhinu implements execution and Hufu implements authorization in an optional
adapter depending only on that contract package and Hufu core. Concrete SQLite
start coordination remains a separately qualified legacy integration until ZA-5
resolves its disposition. Resource enforcement and Hufu policy stay independent
of workflow execution. The new seam is planned, not implemented.

[Authority-Mediated Language Execution (AMLE)](authority-mediated-language-execution.md)
names this shared architectural direction: Luban expresses and executes bounded
semantic operations, Hufu mediates contextual authority, and trusted resource
providers perform access. Semantic admission, concrete resource/start checks and
result release remain distinct obligations. The guide links both products and
separates the proposed pattern from current qualified profiles.


Status: Design plus narrow M1, optional current-state SQLite and co-located operation-start prototypes, updated 2026-10-02. The separate library and name **Penghou.Hufu** were selected by the user. The current bounded snapshot contracts and Luban read authorizer are prototype APIs; the broader boundaries below remain design unless called out explicitly.

## Purpose

Hufu decides, records, and validates what authority an execution may exercise. It provides reusable grant, request, decision, attenuation, revocation, and authority-store mechanics. It is embedded by a host initially; a separately deployed authorization service is optional future infrastructure.

Models and planners request authority. Authenticated host policy issues grants. Hufu evaluates and records those decisions. Zhinu and trusted resource adapters enforce them at execution boundaries. Hufu's domain model alone is not an operating-system sandbox or an IAM provider.

## Project shape

The core and Luban consumer projects target .NET 8 and .NET 10. The current prototype exposes bounded authenticated-context, authority-snapshot, request/decision, snapshot-source, evaluator, and decision-recorder contracts, plus a read-profile authorizer. It has no trusted default evaluator, complete issuing service, or production host. Optional current-state publication/revocation and evidence persistence are implemented behind a required host authorizer; see the [store profile](durable-authority-store.md). See the [current authority prototype profile](current-authority-profile.md). Do not treat these contracts alone as enforcement.

Current core/Cedar/Luban/SQLite/composition projects and planned testing package:

```text
Penghou.Hufu          Domain contracts, evaluation, attenuation, store contracts
Penghou.Hufu.Cedar    Implemented typed mapping/evaluation through CedarSharp
Penghou.Hufu.Luban    Implemented known-root read-language authority consumer
Penghou.Hufu.Sqlite   Implemented current-state/evidence prototype adapter
Penghou.Hufu.Biscuit  Experimental registered online Biscuit profile
Penghou.Hufu.Biscuit.Sqlite  Experimental registration/revocation/evidence/start composition
Penghou.Hufu.Zhinu.Sqlite  Experimental co-located runtime operation-start adapter
Penghou.Hufu.Testing  Reusable store/evaluator integration conformance suite
```

Testing helpers belong in a public package only when there is a real external adapter consumer; ordinary repository tests need no package. SQLite must remain optional. Neither the core nor its contracts depend on a particular workflow engine, provider, UI, identity service, or database. Avoid a separate Abstractions package until a concrete dependency-cycle or distribution requirement justifies it.

## Local-first runtime and capability boundary

[ADR 0003](decisions/0003-local-first-authority-runtime.md) and the
[runtime design](local-first-authority-runtime.md) add deterministic analysis,
versioned execution requirements, capability brokers, and foundational evidence
and observability. SQLite remains the initial optional authority store.

[ADR 0005](decisions/0005-luban-owns-typed-effects.md) establishes
[Penghou.Luban](../../Penghou.Luban/README.md) as the independent owner of
[typed effects](../../Penghou.Luban/docs/typed-effect-runtime.md), refining
ADR 0004's temporary ownership. Requests/results stay independent of Hufu and
workflow engines. Luban owns effect providers and their plan; Hufu retains
[authority integration](luban-integration.md). Penghou.Hufu.Luban depends on both
for the known-root read profile without either core referencing the other. Consumers use explicit operations,
and a governed host selects qualified providers. No raw process escape hatch.

Luban also owns a bounded [surface language](../../Penghou.Luban/docs/language-syntax-spec.md)
and compiler for finite typed dataflow and closed pure filters. Hufu's adapter
consumes trusted lowered effects and requirements with exact semantic IR,
catalogue/schema/provider versions, bounds, payload hashes, and workspace
bindings; it never treats an alias or source string as permission. Scope ceilings
are admission constraints, not grants to all discovered descendants. Every
effect and concrete target still passes current authorization. Its minimal Windows read-only parser/compiler, canonical IR, static preflight
and buffered pipeline executor are implemented. Broader syntax and governance
remain pending. Full-document
validation precedes effects but does not make multiple effects atomic.

[ADR 0006](decisions/0006-shared-resource-boundary.md) selects
`Penghou.IO.Abstractions` resource contracts from the
[Penghou repository](https://github.com/jenolaszlo-sketch/penghou). A Hufu adapter
governs concrete bounded I/O while Luban retains semantic effect admission.
Preflight the whole pipeline against known requirements and targets before
upstream work, then recheck at effect start and concrete resource access.
Readiness is not permission; dynamic targets and changed authority require live
checks. The shared project has a qualified Windows read-only Local provider.
Hufu's known-root read-authorizer is a prototype; governed whole-plan and
production resource-boundary adapters remain pending.

[ADR 0007](decisions/0007-preview-resolution-commit-barrier.md) selects
[Luban preview resolution](../../Penghou.Luban/docs/preview-resolution-commit-barrier.md)
and [resource-provider barrier requirements](../../Penghou/docs/preview-commit-contract.md).
Luban now implements Local-only capture: freeze explicit-authorized-view glob
targets, admit every selected target before content reads, and capture immutable
TextPatch payloads with protected observation identity. All-match, truncation,
repeated-target, and deferred-tool outcomes remain unresolved as specified. An
unresolved earlier patch node blocks all later nodes with
`DependsOnUnresolvedEffect` and zero later I/O; deferred tools use
`DependsOnOpaqueEffect` for dependent nodes. Initial v1 uses ordered segments
without virtual state.
`CaptureComplete` is not `CanCommit`, which is always false. WhatIf has no writer
callback or API. Luban separately implements standalone one-target and narrow
1–64 exact-target batch executors under the explicitly HostControlled Local NTFS
namespace. Batch admission and recovery inspection are supplied by the host; no
durable store or Hufu adapter is included. The host must protect the root, drive,
mount, and directory namespace from untrusted actors; this is not general
filesystem confinement. Hufu whole-plan admission, governed execution, and
durable recovery remain pending. See Luban's [batch execution profile](../../Penghou.Luban/docs/batch-execution-profile.md).
This capture performs real authorized reads and differs from effect-free
counterfactual policy simulation. Batch execution is sequential and non-atomic;
post-start failures may leave partial outcomes and do not imply a transaction.

Execution contracts bind registered requirement enforcers to exact operations.
Unsupported requirements block execution. Deterministic analysis explains actual
decisions and bounded hypothetical changes; unknown analysis cannot prove wider
scope coverage. An optional AI summary cannot alter authority.

StrictEffects is the default planned agent mode: a finite registered vocabulary,
trusted authority mappings, bounded handlers, and rejection of arbitrary shell,
scripts, and process requests. Fuwen supplies control flow; Zhinu journals effects
and reconciles retries. ApprovedTools is an explicit host choice with the actual
native-code risk recorded. Dependency injection alone does not constrain other
same-process code. Hufu and Luban will not implement a sandbox. Any external
provider confinement requirement must be met or rejected without downgrade.
Credentials remain inside trusted brokers behind non-authorizing references.

## Selected policy integration

[ADR 0002](decisions/0002-use-cedarsharp-for-policy-evaluation.md) selects the
official Cedar engine through the independent CedarSharp wrapper. CedarSharp
1.0.0 is implemented, published, and qualified on its documented Windows Server
2025 x64, Ubuntu 24.04 x64, and macOS 15 ARM64 CI environments with .NET 8 and
.NET 10. The intended dependency is
`Penghou.Hufu.Cedar -> CedarSharp -> Rust bridge -> Cedar`. Hufu core stays
backend-neutral; it does not reference CedarSharp or expose Cedar types. The
Hufu.Cedar `CedarAuthorityEvaluator` and fixed mapping prototype are implemented,
and pass local Windows x64 Cedar/read integration tests. Required durable recording is available through the optional store and a host capture factory. Complete Cedar evidence format/provenance and operation-start integration remain host qualification gates.
Production Hufu authority enforcement is not implemented. The read authorizer
consumes an injected evaluator and a host source that authenticates and returns
current authority; the source is not supplied by a default implementation.

CedarSharp preserves upstream authorization semantics and complete diagnostics.
The Hufu adapter validates policies and authentic request/entity data, binds
engine/schema/policy/entity identities, and blocks on evaluation diagnostics or
boundary failures. Cedar's skip-on-error behavior must not let Hufu treat an
Allow-with-errors response as usable authority.

Cedar handles policy evaluation; Hufu retains grant validity, scope attenuation,
current-authority checks, effect approval, and durable decision provenance.
SQLite supplies the initial optional current-state/evidence prototype store. Cedar is not a database
or resource sandbox and does not replace the final protected-I/O boundary.

Evaluate distinct parent/run/activity/delegation authorization layers as required
restrictions: a child must pass every applicable layer. Do not union their permit
policies into a bundle that lets one layer authorize work another layer does not.
Preserve mandatory prohibitions throughout the lineage. For complete-grant
containment, begin with provable typed scope rules; unsupported comparisons
remain indeterminate and block admission. Sampling concrete authorization
requests is not proof of containment.

Optional future CedarSharp.Analysis/SymCC may assist admission-time containment
under explicit schema and subject assumptions. Solver unknown/timeout or an
unsupported feature cannot grant authority, and the first Hufu integration does
not require a solver. See the [CedarSharp handoff](../../CedarSharp/docs/implementation-handoff.md).

## Generic execution subjects

Core authority subjects carry tenant, execution identity, parent lineage, and authenticated workload binding. They do not require a Fuwen plan or Zhinu run type. Exact subject and admission contracts will be designed with the first broker integration.

Adapters bind those subjects to a workflow/run/revision/node/item, a standalone delegation, or a host background operation. An opaque workflow fingerprint is a binding input; Hufu does not interpret the graph or decide which revision should execute. Grant references identify records and never authorize by possession alone.

## Responsibility map

| Boundary | Responsibility |
| --- | --- |
| Hufu core | Typed requirements/grants/constraints, evaluation orchestration, attenuation, explanations, validity and revocation semantics |
| Penghou.Luban | Neutral typed request/results, planned surface language/compiler and typed IR, trusted effect catalogue, bounded providers and conformance; no policy engine or scheduler |
| Penghou.IO.Abstractions | Shared bounded file/directory/web contracts and resource authorization boundary; no language, policy store or scheduler |
| Hufu.Cedar adapter | Trusted mapping, exact version bindings, Cedar invocation, strict diagnostic handling, composition of authority layers |
| CedarSharp and official Cedar | Generic managed/native binding and upstream policy parsing, validation, evaluation, and diagnostics |
| Hufu authority store | Durable requests, authenticated decision provenance, grants and versions, envelope versions, revocation state, authority admission bindings, idempotent decision records |
| Host | Authentication, approver authority, organization policy, resource/credential resolution, trusted catalogue/evaluators, effect and data-release policy, UI |
| Fuwen | DSL and immutable plan representation, requirement summaries, compiler identity, structural validation |
| Guihua | Proposals, authority-aware planning, request rationale, compliant alternatives |
| Zhinu | Active revision/epoch, execution leases/fences, durable activation, operation journal, scheduling, recovery |
| Qingniao | Selected-provider execution, bounded supervision, delegation context enforcement through Hufu contracts |
| Effect providers and external execution environment | Final authenticated checks and bounded effects; external isolation only if explicitly selected and qualified |
| Hufu-governed brokers | Exact execution requirements, credential use, final resource checks, and attributable receipts; isolation only when separately qualified |
| Existing budget service | Atomic reservations and settlement; Hufu references accounts and requires budget proof where applicable |
| Hongxian/Siming adapters | Optional evidence projection and narrative, without becoming authority or execution state |

Hufu owns its store semantics even when the host provides its physical database. Zhinu may reference Hufu records, but does not reconstruct live grant state from workflow history. Hufu may reference execution identities, but does not hold a second mutable copy of the active workflow graph or its scheduler state.

## Authority-store contract

The persistent store must support immutable request/decision/envelope versions, conditional writes, idempotent command identities, monotonic revocation state, authenticated record provenance, and durable decision audit. A timestamp alone is not a concurrency token.

Store grant lineage, exclusions, exact requirements and fact snapshots, approval
versions, decisions, and broker receipt references. Principal/resource bindings
refer to authenticated host truth rather than a second independently mutable
identity catalogue. Graph-shaped data does not require a graph database; all
future stores must prove tenant isolation, lineage integrity, and ordering.

The initial [ADR 0008](decisions/0008-current-authority-store.md) slice installs a
complete immutable current snapshot or terminal revocation in one expected-
sequence transaction. It separately records bounded attributed evaluator
evidence; recording does not move current authority. Its host authorizer must
authenticate the actor and issuer ceiling on every call, including exact replay.
This does not implement pending requests or infer approver/issuer authority.

The broader lifecycle must still prove the following transactions within the authority boundary:

1. Record or replay an identical request by command identity; reject conflicting reuse.
2. Decide the exact pending request under expected version and authenticated approver context.
3. Install the resulting grant/envelope version without exposing a partially applied decision as usable authority.
4. Revoke or expire authority with a documented order relative to operation-start authorization.
5. Recover in-progress decision application without reissuing broader grants or duplicating effects.

Do not require a distributed transaction between Hufu and Zhinu. Persist the authority decision first; Zhinu then validates exact references and activates through its own expected-state fence. If activation becomes stale, the decision remains history and does not attach itself to another revision. Any granted but unused authority remains bound to its original subject and conditions.

Final operation-start coordination is an integration contract, not a claim that two independent store reads form an atomic check. The initial [co-located SQLite start profile](operation-start-profile.md) identifies one shared-file writer transaction for current authority, actual Zhinu runtime acquisition, and mandatory start evidence. It chooses block-new-starts semantics; full governed mutation/outcome wiring remains open. A short-lived token alone cannot provide immediate revocation.

## Policy extensions

Hufu defines a closed evaluation result such as covered, not covered, or indeterminate, with structured reasons. The host registers trusted capability-family evaluators and resource binders. Pin their contract versions in admission; unknown required fields or changed semantics reject new affected execution.

Those extension contracts normalize resources and prove typed scope relations;
they do not define a second unrestricted policy language. Hufu.Cedar maps their
trusted facts into the selected Cedar schema. Policy input is host-controlled,
versioned data; model-generated proposals cannot install their own policies.

The host establishes mandatory deny rules and the issuer's maximum authority. Hufu cannot infer a universal filesystem, database, cloud IAM, or data-classification policy. Multiple grants may cover separate operations, but cannot be combined field-by-field to fabricate a broader permission.

## Evidence and observability

Durable evidence is part of the first store/operation contracts. Commit mandatory
start evidence before protected dispatch and reconcile uncertain outcomes through
the runtime journal. A missing receipt does not prove an effect never occurred.
Evidence writes are idempotent and correlate exact authority, requirements,
enforcers, approval, operation, and outcome under host read/retention policies.

Use lightweight .NET tracing and metrics compatible with OpenTelemetry; hosts
select exporters. Sampled telemetry is separate from authoritative evidence.
Collector/exporter failures do not prevent locally evidenced execution. Keep
secrets and sensitive arguments out of telemetry and bound metric cardinality.
Signed checkpoints and external anchoring are deferred.

## First complete integration

Prove one local host with a small set of brokered repository operations. Cover admission, a permitted operation, an out-of-scope denial, durable restart, revocation, and a raced stale context. Include deterministic explanations, enforced execution requirements, opaque credential-use boundaries where applicable, durable evidence failures, and optional telemetry. Then bind that path into Zhinu and Fuwen; introduce Qingniao when delegation is actually confined.

Prove the StrictEffects source-inspect/patch scenario first, including unknown
shell rejection, exact preconditions, result bounds, Git helper restrictions if
Git is supported, and durable local/supervisor completion. Broader native work
requires explicit host/provider admission. No sandbox construction is planned;
any externally claimed confinement needs evidence for that actual boundary.

See the [behavioral specification](workflow-authority-spec.md) for the full invariants and the [roadmap](roadmap.md) for delivery gates.

## Optional registered Biscuit profile

[ADR 0010](decisions/0010-registered-biscuit-profile.md) adopts the experimental
[registered online profile](biscuit-integration-profile.md). A registered
credential represents one immutable Hufu grant version in one authority layer.
Verification requires authenticated host bindings, registered canonical token
bytes, current Hufu/Cedar permission, exact resource binding, and required
evidence. Biscuit preflight does not dispatch effects.

The optional Penghou.Hufu.Biscuit adapter references the real Hufu Cedar adapter
and the repository-pinned unpublished BiscuitSharp candidate. The separate
Penghou.Hufu.Biscuit.Sqlite composition stores credential lineage, immutable
grant-version meaning, revocation and key-retirement tombstones, and verification
evidence. Its start participant checks registry state in the same writer
transaction as the Hufu/Zhinu start. This orders new starts against revocation
and key retirement; an earlier committed start may complete. AlreadyStarted is
a receipt and cannot dispatch again.

Neither adapter is required by Hufu core. These components provide no real
resource consumer, provider confinement, mutation outcome recovery, production
key custody, or default host authorization. See the
[qualification record](biscuit-integration-qualification.md).
