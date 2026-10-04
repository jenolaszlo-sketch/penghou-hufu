# Hufu and Penghou.Luban integration

Architecture update: [resource abstractions](../../Penghou/docs/resource-abstractions-architecture.md)
and [RA-1/RA-4 roadmap](roadmap.md) distinguish Hufu.Luban semantic admission from
a future optional Hufu.IO resource boundary. The current prototype below is not
a general resource decorator. HTTP contracts have a separate future domain owner.
Capture-only WhatIf remains unchanged; overlay execution is deferred VFS-5.

Status: V1 reads and explicit v2 read/diff authorization implemented, 2026-10-04;
the separate bounded [single-patch host](single-patch-host.md) is a preview.3 candidate. See [v2 usage and limits](luban-v2-authorization.md). The prototype includes optional current-state/evidence SQLite integration; it is not an atomic mutation-start protocol.
[ADR 0005](decisions/0005-luban-owns-typed-effects.md) selects Luban as the separate
effect-runtime owner. Read its [design](../../Penghou.Luban/docs/typed-effect-runtime.md)
and [implementation plan](../../Penghou.Luban/docs/implementation-plan.md) for
effect/provider details; this document keeps Hufu's obligations here.

## Shared pattern

This integration is the authority side of [Authority-Mediated Language Execution
(AMLE)](authority-mediated-language-execution.md). Read it alongside
[Luban's guide](../../Penghou.Luban/docs/authority-mediated-language-execution.md):
the language defines expressible effects, Hufu evaluates contextual authority,
and trusted providers enforce concrete resource access. Preflight cannot replace
runtime mediation; a workflow requirement or captured proposal is not a grant.
The profiles and joint gates below determine which parts are implemented.

## Ownership and dependencies

Follow Luban's [dependency-ordered delivery plan](../../Penghou.Luban/docs/implementation-plan.md)
and the [shared provider plan](../../Penghou/docs/implementation-plan.md).
The shared Windows read-only provider, migrated direct handlers, minimal typed
read language/static preflight, and capture-only Local WhatIf profile are
implemented in Luban. The preview captures immutable existing-file TextPatch payloads; WhatIf remains
read-only and `CanCommit` is false. Separate standalone Luban executors support
one exact-target patch and a complete ordered batch of 1–64 distinct exact-target
patches under the explicitly HostControlled Local NTFS namespace. Batch admission
and receipt inspection are supplied by the host; they are not Hufu governance or
a durable recovery store. The host must protect the root, drive, mount, and
directory namespace from untrusted actors. Neither profile provides general
filesystem confinement. Governed batch admission, durable recovery, and Hufu
batch commit/start integration remain pending. Independent single-target
governance is supplied by the separate Hufu.Luban.Sqlite candidate;
CedarSharp 1.0.0 is qualified; the Hufu.Cedar typed projection/evaluator
prototype and known-root read consumer pass the local Windows x64 qualification.
The optional [current-state/evidence store](durable-authority-store.md) is implemented; complete authority lifecycle, governed admission, and production integration remain open. Hufu now has a
read-language `ILanguageAuthorizer`
prototype over required host-supplied snapshot, evaluator, and decision-recorder
interfaces. It handles known-root Read/Find/Search and pure Take/Count; the explicit
`ReadAndDiffV2` profile adds v2 windows/context and fixed two-input diffs. Dynamic reads and searches with null roots remain
Unavailable until a reviewed static scope ceiling can be bound. The prototype
does not provide semantic batch admission or serialize revocation with Zhinu's
operation start; no shared authoritative transaction exists between the Hufu
current-state source and Zhinu journal. See the [current
authority prototype profile](current-authority-profile.md).

| Concern | Owner |
| --- | --- |
| Typed effect requests/results, trusted descriptors, provider discovery, bounded handlers | Luban |
| Shared bounded file/directory/web resource contracts and provider obligations | Penghou.IO.Abstractions in the Penghou repository |
| Concrete local/web resource I/O implementations | Separate provider projects in the Penghou repository |
| Grants, approvals, policy mapping, attenuation, revocation, authority persistence | Hufu |
| Mapping descriptor/resource requirements into Hufu checks and evidence | Host-selected Hufu/Luban integration adapter |
| Control flow and static effect requirements | Fuwen integration |
| Journal, dispatch, fencing, retries and outcome reconciliation | Zhinu or equivalent host runtime |
| Provider selection, identity, credentials and operation UI | Host; Marang supervisor or Guyabano local composition |

Luban and Hufu cores stay independent. Penghou.Hufu.Luban depends on both for
the implemented read and read/diff profiles. Optional Hufu.Luban.Sqlite adds
the bounded single-patch host without changing either core; broader capability
families and batch adapters remain planned.
No direct reference from Luban core to Cedar, workflow engines, MCP, or a specific
supervisor is needed. A registered effect is discoverable behavior, not authority.

## Host-supplied authorization check

[ADR 0006](decisions/0006-shared-resource-boundary.md) and
[Luban ADR 0003](../../Penghou.Luban/docs/decisions/0003-shared-resource-interfaces.md)
select shared resource interfaces in the
[Penghou repository](https://github.com/jenolaszlo-sketch/penghou). Migration is
implemented for Luban's Windows read slice; Hufu's read-language authorizer is
a narrow prototype and production governance remains pending.
A governed resource provider receives trusted effect/request context
and uses Hufu to authorize each concrete action. The compiler does not acquire
policy logic. Semantic effect admission and final I/O enforcement stay connected;
a generic before-write event cannot validate exact-effect approvals or prove
target binding. Read/metadata/list disclosure and web access also need checks.

Luban's first read-only slice requires an injected neutral `IEffectAuthorizer`.
There is no implicit permit implementation. A standalone host deliberately
supplies its own checker; a Hufu-governed host supplies an adapter that uses the
current Hufu subject, grants, scope, approval, and policy. Standalone local use
does not claim Hufu enforcement. The checker is a required part of host
composition, not a mutable agent-selected provider or a bearer token.

The optional `Penghou.Hufu.IO` project now supplies `HufuResourceAuthorizer`
and `HufuWorkspaceAccess` over the neutral resource contracts. The authorizer
binds a host-authenticated Hufu context and invocation to the selected workspace,
maps concrete read/list/metadata/patch/write actions, reads a current snapshot,
validates evaluator identity against that snapshot, and requires decision evidence
before allowing access. The facade checks a request before opening a provider and
passes the same authorizer and required mutation journal into the provider, which
must keep its candidate and locked-start checks. Unsupported action families fail
closed. No permissive provider or authorizer is supplied.

`WriteFile` has its own Hufu action and does not map to `PatchFile`. This adapter
does not implement a Hufu-backed `IResourceMutationJournal` or atomically bind
`IAuthorityOperationStartGate` to the physical object's locked mutation start.
The separate [Hufu.Luban.Sqlite host](single-patch-host.md) supplies this exact
start mapping for one approved captured patch, including revocation order,
evidence failure and ambiguous recovery. General Hufu.IO writes still require
a qualified host journal; this does not broaden the resource facade itself. The focused Hufu.IO suite also
uses the real Windows Local provider with a deterministic evaluator over Hufu
snapshots and a test-only mutation journal. It verifies bounded reads, excluded
candidate non-disclosure, paginated listing on one retained session, exact-version
conditional persistence, and current revocation at Local's final write check
before the test journal starts. This exercises the real neutral provider hooks;
the test journal is not a production Hufu authority transaction.

The implemented [read language profile](../../Penghou.Luban/docs/read-language-profile.md)
requires a separate neutral `ILanguageAuthorizer`: Preflight, EffectStart,
ResourceAccess and Release checks carry compiled document/node identity,
descriptor/profile versions, typed arguments, authenticated invocation and
workspace. Concrete resource identities remain distinct from semantic IR.
Release includes discarded values, no-match reads and empty directory
observations; readiness does not grant access. The Hufu read authorizer
prototype fetches a current snapshot and records each decision, but depends on
the host for authenticated current authority, evaluation, and evidence. It does
not itself persist grants or serialize revocation with operation start. The
optional `AuthorityStoreSnapshotSource` and `AuthorityStoreDecisionRecorder`
bind the current-state store to this read consumer with a required authenticated
actor and exact host evidence capture factory. Luban's
standalone batch contract is described in its [batch execution profile](../../Penghou.Luban/docs/batch-execution-profile.md);
its host-supplied admission and recovery are not a durable store and do not prove
Hufu enforcement.

The check must bind the effect kind/version, exact request inputs, workspace and
concrete relative target, authenticated invocation context, and current authority
state. Root/traversal checks do not authorize all descendants: Find and
SearchText recheck each traversed directory and candidate file before returning
names or content. Read checks the exact target before protected data access.
Denied or indeterminate checks release no protected result; exceptions or an
unavailable checker fail closed. Hosts preserve the distinction in evidence.

The first implementation can conservatively reject links/reparse points. It
must not claim race-proof containment from a path-string check. Hufu's stronger
resource-boundary guarantee requires a provider that binds check and actual use
to the same object, with tests for aliases, links and concurrent replacement.
Until then, only the provider's demonstrated narrower profile is admitted.

The authorizer is not a substitute for Hufu's durable admission and final
operation-start ordering. The narrow prototype now exists, while the production
mutation host and complete approval/delegation lifecycle remain pending. The
new [co-located SQLite start adapter](operation-start-profile.md) orders current
authority and actual Zhinu acquisition with mandatory start evidence; complete
plan admission, locked-object host wiring and terminal recovery are still needed. No default permit implementation is provided.

## Governed invocation

### Compiled language requests (narrow Hufu read-authorizer prototype)

Luban's [language specification](../../Penghou.Luban/docs/language-syntax-spec.md)
and [ADR 0002](../../Penghou.Luban/docs/decisions/0002-typed-surface-language.md)
introduce bounded typed dataflow and closed pure transforms. The minimal
Windows read-only parser/compiler, canonical IR, static preflight and buffered
pipeline executor are implemented. Broader syntax and Hufu integration remain
pending. Hufu admits trusted
lowered effects and requirements, not an opaque source string or a shell alias.
The compiler and host catalogue versions, semantic IR digest, schema/provider
profiles, bounds, payload hashes, and authenticated workspace bindings must be
pinned. Keep language, IR, and effect schema versions distinct. The current
direct-API JSON request digest remains distinct from the language IR digest.

Static analysis identifies effect kinds and scope ceilings; runtime-discovered
targets still require current authorization before metadata, content, or result
release. Each pipeline effect must pass its own check. A FileRef carries
provenance, never transferable authority. Filters cannot expand scope, acquire
authority, or bypass output-release rules. Cached values retain release checks.

Validate the whole document before effects start. Invalid syntax/types/profiles
cause zero effects, but runtime denial after earlier effects may leave a partial
outcome; parsing is not a multi-effect transaction. Exact approvals bind typed
semantics and preconditions, so alias spelling cannot widen an approved effect.
Source and diagnostics remain subject to host data-access policy.

### Runtime admission and dispatch

Before dispatch, preflight every pipeline node against trusted descriptors,
supported profiles, known targets, required authority/approvals and scope/limit
ceilings. If a known destination write is denied, block the pipeline before
upstream listing/reads and artifact production. Statically preflight authority state and
trusted mappings; it cannot perform unauthorized target I/O to discover facts.
Readiness is advisory and may become stale, never a transferable permit.

Dynamic files/destinations require bounded authorized discovery, then concrete
checks. Recheck current authority, revocation, approvals, budget/fence and
preconditions at actual effect/resource boundaries. Preserve cancellation and bounded intermediate values; the current read
profile buffers stages and does not implement lazy production/backpressure; avoid automatic durable artifacts
unless the runtime explicitly needs and authorizes them. Earlier successful
reads may precede a live denial, with zero unauthorized write and attributable
partial receipts. No pipeline transaction or universal zero-work promise.

### Capture-only preview and future commit barrier

[ADR 0007](decisions/0007-preview-resolution-commit-barrier.md) adopts
[Luban's preview design](../../Penghou.Luban/docs/preview-resolution-commit-barrier.md),
which now includes a capture-only Local profile. The [shared provider contract](../../Penghou/docs/preview-commit-contract.md)
describes future commit requirements. The implemented `PreviewRuntime.WhatIfAsync`
freezes explicit-authorized-view glob targets and admits every selected target
before any file-content read. It captures typed UTF-8 scalar-safe TextPatch payloads
for existing files, with exact offsets and replacement text. It retains no original
or proposed full-file bytes; observations expose only SHA, length, and provider
ResourceVersion. It is a real authorized read operation, not Hufu's effect-free
counterfactual simulation.

All-match selection is unavailable and returns unresolved before protected target
I/O. Required truncated selection returns an incomplete plan with an unresolved
node and no proposed prefix. An earlier unresolved patch from AllMatches,
incomplete selection, or repeated target blocks every later node with
`DependsOnUnresolvedEffect` and zero later-node I/O. Build, Test, and Git.Commit
plus dependent later nodes use `DependsOnOpaqueEffect`. Initial v1 uses ordered
segments without virtual state. Aggregate I/O, work, plan size, and
authorization-call budgets are enforced.

`CaptureComplete` records capture completion only. `CanCommit` is always false;
there is no writer callback, adapter, or write API in WhatIf. Luban separately
provides standalone single-target and narrow batch executor APIs. Hufu's adapter,
governed whole-plan admission, consistency probes, and commit/start integration
remain pending; standalone host admission and receipt inspection do not satisfy
those Hufu requirements. See Luban's [batch execution profile](../../Penghou.Luban/docs/batch-execution-profile.md).

Hufu admits every known mutation in the immutable plan/segment under pinned
authority versions before the executor permits any proposed mutation. Bind exact
semantic IR and resolved-plan identity, ordered nodes/dependencies, role-specific
target manifest, payload digests, preconditions, explicit selection/coverage,
limits, modes, approvals and trusted provider/catalogue profiles. Source spelling
and a preview receipt are not commit permission. Shared concrete request identity
must remain linked to its parent effect and admitted plan through host mappings.

Any known denied mutation or required incomplete coverage blocks the whole set.
Partial grants do not execute an allowed prefix; a smaller batch needs an explicit
revised plan and new admission. Authorized-only discovery is not proof of all-
match coverage. Freeze the exact manifest; changed targets, observations or
payloads require re-resolution/re-admission rather than rerunning a glob at commit.
Discovery depending on earlier writes or opaque state changes requires a supported
dependency model or separately admitted later segments, never guessed future state.

The barrier belongs to the executor and existing host journal. Before dispatch,
revalidate current authority/revision/fence and still-checkable batch preconditions,
and commit required start evidence. Continue individual live I/O/object/version
checks afterward. Known denial before the barrier gives zero proposed mutations;
post-start revocation or failure may leave partial outcomes. Do not claim atomic
multi-file commit, rollback or race closure. Restart revalidates plans and reconciles
uncertain started mutations without repeating completed effects. All integration
and barrier execution remain pending.

### Per-effect dispatch

1. Authenticate subject/tenant and select a qualified Luban provider with pinned
   descriptor, schema, mode, platform, limits, and recovery semantics.
2. Derive requirements from trusted descriptors and host resource bindings. Check
   both allowed effect kinds and complete resource authority at every applicable
   parent/run/activity/delegation layer. Reject unknown or incompatible contracts.
3. Bind exact request/preconditions, approvals, requirements/enforcers, provider,
   policy identities, and runtime fence to admission and a stable operation ID.
4. Commit mandatory intent/evidence, then serialize the final current-authority
   check and protected operation start at the documented runtime/provider boundary.
   Admission before a queue or supervisor wait cannot replace that final check.
5. Validate and correlate typed outcome/receipt with Hufu decision evidence and
   the runtime journal. Missing receipts or uncertain external outcomes require
   reconciliation, not new authority or a blind retry.

Execution requirements stay restrictive: unsupported handler guarantees block.
Credential references remain non-authorizing and secrets stay in trusted brokers.
Read/output release is governed even when a previous effect's result is cached.
Exact request identity, grant lineage, mapping/provider versions, start order,
and outcome remain attributable without a second independent evidence ledger.

## Modes and provider trust

StrictEffects is the default planned agent surface. No scripts or arbitrary
process fallback; missing effects produce structured planning evidence. Neither
project will build a sandbox. ApprovedTools admits actual broader native powers
only through explicit host policy. Do not relabel build/test execution as strict.

Marang supervisor completion must be authenticated and bound to the exact request,
provider, subject, resource, preconditions, revision/fence, and attempt. Stale or
cancelled completions cannot resume work; exact duplicates are idempotent.
A supervisor's success response alone does not prove boundary enforcement.
Use only qualified semantics, or a separately authorized candidate-patch path
with local application through a bounded provider.

## Hufu integration acceptance

Luban owns provider behavior tests; Hufu owns tests that connect those behaviors
to actual grants and revocation. Together demonstrate:

- Read/find/search and hash-bound patch within admitted scope; outside/excluded
  paths reject without unauthorized I/O and stale preconditions cause no mutation.
- Unknown effects, wrong tenants/subjects, missing registrations, incompatible
  versions, and delegated kind/scope escalation fail closed.
- Language aliases share semantic identity; altered payloads, limits, versions,
  workspace bindings, or preconditions cannot reuse exact approval. Every
  effect and discovered target in typed dataflow is checked independently.
- Known denied downstream effects block preflight before upstream I/O; live
  revocation or denied dynamic targets block at access even after readiness.
  Pipeline intermediates remain bounded and cancellable, with no implicit write.
- Authorized resolution with any denied concrete mutation blocks the complete
  known batch before writes; incomplete required coverage and partial grants do
  not execute prefixes. WhatIf never dispatches native/lazy tools or mutations.
- Changed manifests/payloads/observations invalidate exact plan admission; frozen
  targets cannot widen at commit. Restart and post-start partial outcomes retain
  receipts and reconcile uncertainty without treating preview as a permit.
- Requirement/evidence failures block dispatch, while optional telemetry loss
  does not erase committed authoritative records or stop otherwise valid work.
- A revoke/start race has one documented order; queued/supervisor work rechecks
  authority before start. Duplicate or ambiguous mutations are reconciled by the
  runtime without repeating committed effects.
- Supervisor stale/conflicting completions do not widen authority or revive work.

Do not advertise these guarantees before the joint integration passes. Hufu's
[roadmap](roadmap.md) tracks this adapter work; Luban's
[roadmap](../../Penghou.Luban/ROADMAP.md) tracks effect implementation.
