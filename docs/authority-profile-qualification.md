# Authority profile qualification

Date: 2026-10-01. This records the earlier 44-test M1 qualification; the later
[store qualification](durable-authority-store-qualification.md) extends Hufu to
73 tests per runtime and supplies optional current-state/evidence persistence.
The test/source provenance below is retained for the earlier review.

Status: Local qualification of the narrow
[current authority profile](current-authority-profile.md); production authority
store, grant lifecycle, governed mutations, and atomic start/revocation remain
pending at that checkpoint. Later [store](durable-authority-store-qualification.md) and [operation-start](operation-start-qualification.md) qualifications extend it; original results and source hashes below remain historical. No package was published by this work.

Environment: Windows 11 Home Single Language x64, build 10.0.26200. Release
builds and tests ran on .NET 8 and .NET 10. Native Cedar was loaded from the
published, cached CedarSharp 1.0.0 package, rather than unpublished wrapper source.

## Results

| Suite | .NET 8 | .NET 10 | Scope |
| --- | --- | --- | --- |
| Penghou.Hufu.Tests | 44 passed, 0 skipped | 44 passed, 0 skipped | Typed snapshots, real Cedar, actual Luban/Local read consumer |
| Penghou.IO.Tests | 86 passed, 0 skipped | 86 passed, 0 skipped | Shared request codec, native read/patch profiles, added alias regressions |
| Penghou.Luban.Tests | 167 passed, 0 skipped | 167 passed, 0 skipped | Existing language, read, capture, single/batch execution regression suites |
| Penghou.Zhinu.Sqlite.Tests | 375 passed, 0 skipped | 375 passed, 0 skipped | Captured-generation acquisition fix and existing SQLite regressions |

The Zhinu audit also ran its 14 focused external-operation tests on both
frameworks. The test counts above are repository suite totals, not a claim that
every test is new or every roadmap guarantee has been delivered.

Reproduce the Hufu, shared-provider, and Luban results in adjacent checkouts with:

```powershell
dotnet test tests/Penghou.Hufu.Tests/Penghou.Hufu.Tests.csproj -c Release
dotnet test ../Penghou/tests/Penghou.IO.Tests/Penghou.IO.Tests.csproj -c Release
dotnet test ../Penghou.Luban/tests/Penghou.Luban.Tests/Penghou.Luban.Tests.csproj -c Release
```

## Qualified observations

- Snapshots freeze caller-owned collections, reject invalid scopes/actions/time
  intervals and malformed UTF-16, and bind authority facts into exact identities.
  The default neutral authority status is Unavailable.
- Real native Cedar proves default deny, exact/subtree and workspace separation,
  Unicode literal projection, local exclusions versus mandatory prohibitions,
intersection of authority layers, all six typed actions, context mismatch,
  and exact expiry/not-before boundaries. Trusted details include successful
  policy validation, complete request checks, and raw diagnostics.
- A separate native Cedar fixture returns raw Allow with a policy evaluation
  error. The same internal classification used by the production evaluator
  rejects it as Deny; no caller raw-policy API was added to Hufu.
- Real Local/Luban reads and searches disclose allowed content. Find/search omit
  excluded child paths/content; pure Take/Count sees only the authorized view.
  Current-state changes before resource access or release block buffered results.
- Missing/throwing snapshot sources, failed evaluators, rejected required
  recording, expired snapshots even with a permissive test evaluator, forged
  semantic/resource bindings, unknown profiles, and unsupported dynamic Read/
  Search pipelines fail closed. Test-only host implementations are not shipping
  authority sources or durable stores.
- A regression initially demonstrated successful release of `src/É.txt` through
  `src/é.txt` despite an exclusion. The shared reader now validates native final
  paths on metadata/component handles and the actual content handle before
  reading. It preserves only ASCII case equivalence; non-ASCII case aliases and
  DOS short names cannot silently reinterpret admitted relative paths. Correct
  Unicode spellings and ASCII case variants still work.
- Native DOS alias generation is enabled on this host: a probe returned
  `SENSIT~1.TXT` for `Sensitive report document.txt`. The short-name regression
  exercised that actual alias and blocked both read and metadata disclosure.
  On a volume without generated aliases, the test verifies the unchanged long
  spelling; it does not claim native alias coverage on that environment.
- The provider fix preserves extended-length Windows paths. Luban's existing
  long-name/glob-work exhaustion regression passes after the fix. Opening native
  metadata handles uses the extended drive/UNC spelling, while comparison
  preserves the original admitted relative spelling instead of expanding DOS
  aliases via `Path.GetFullPath`.
- Zhinu rejects acquisition of a Requested operation from generation N after
  restart even when the caller supplies current generation N+1. Both the
  captured operation generation and current run generation must match; rejection
  preserves the original unowned Requested row.

## Provenance

Cedar identity: ABI 1, SDK 4.13.0, language 4.5, bridge 0.1.0, Rust 1.94.0,
target `x86_64-pc-windows-msvc`, features datetime/decimal/ipaddr. The packaged
native binary digest is
`77de6976a7ae5b961920bb6843d39b32f3faf285f16b723581680e9c06c03895`.

| Source | SHA-256 of locally qualified source bytes |
| --- | --- |
| src/Penghou.Hufu/AuthorityModel.cs | b7aa5f7c551bb76f0b4b57b04111fb104ee56122361ec8cc5bc28aeeac511765 |
| src/Penghou.Hufu.Cedar/CedarAuthorityEvaluator.cs | 6615f740726812b4d047a0425a620babed3f87ff6adcd752533fc81b7b6a69a8 |
| src/Penghou.Hufu.Luban/HufuLanguageAuthorizer.cs | 29efd7cb345257000fcb93bb1a367daeb6e7555bb372e809b6f93d0c2850b253 |
| ../Penghou/src/Penghou.IO.Local/LocalWorkspaceReader.cs | d9ae4d26e67a55e3543873714b26490db5d62f58fc4a1549dbbd152ed74daa87 |

These identify this local review; they are not signed attestations or admission
receipts. Line-ending changes alter source-byte digests.

## Open gates

The later optional [SQLite store](durable-authority-store.md) supplies current
publication/revocation and durable evidence, extending this earlier record.
No complete production issuer/approval/delegation lifecycle, semantic batch
admission, or atomic Hufu/Zhinu start boundary exists. Sequential snapshot lookup and journal acquisition do not prove
no I/O after revocation acknowledgement. The read provider remains path based;
replacement races, hard links, mount changes, and hostile namespace control are
not confinement guarantees. Case-sensitive-directory enablement remains
unverified on this host. UNC shares, non-Windows Hufu consumers, other native
targets, synchronous native interruption, and general filesystem confinement
are not qualified by these local results.

## AE-1.0/AE-1.1 current derived-authority qualification (2026-10-12)

Local qualification of the frozen use-time rule and the required
current-parent checker. Source revision and results recorded here; this is a
focused slice, not a general nested-delegation or broker qualification. The
rule is frozen in the [ADR 0014 amendment](decisions/0014-authority-delegation-enforcement.md#amendment-2026-10-12-ae-10-current-derived-authority-use-time-rule)
and described in the [current authority profile](current-authority-profile.md).

Environment: Windows x64, .NET SDK 10.0.401, .NET 8 and .NET 10 test targets,
published CedarSharp 1.0.0 for the Cedar regression. Focused suite:
`CurrentDerivedAuthorityTests` plus the existing derivation/derivation-admission
suites.

| Suite (focused filter) | .NET 8 | .NET 10 |
| --- | --- | --- |
| CurrentDerivedAuthorityTests + AuthorityDerivationTests + DeriveAdmissionTests + CurrentAuthorityRequestAuthorizerTests | 46 passed, 0 failed, 0 skipped | 46 passed, 0 failed, 0 skipped |

Qualified observations:

- A child read permitted under a live, identity-stable parent is denied after a
  policy-only parent narrowing that keeps the parent grant live with unchanged
  identity and validity dates (`authority.parent-policy-denied`). The child
  snapshot is byte-identical before and after.
- The same narrowing is proved through the real Cedar policy layer; the
  captured parent evaluation binds the stored parent version/identity and the
  real `hufu-cedar-v1` evaluator identity, and is projected into the stored
  parent context rather than the child context.
- Parent expansion does not widen the existing child: scope newly added to the
  parent is denied by the child's fixed ceiling before any parent check.
- A known derived grant through an incomplete composition (ancestor liveness
  without the parent-policy checker, or the root-only constructors) denies with
  `authority.derived-checker-missing`; root-only use through the same
  composition is unchanged.
- Revoked ancestry denies (`authority.ancestor-revoked`); unavailable lineage
  store or current parent evaluation yields typed `Unavailable` with no
  recording; missing, mismatched, self-parenting, cross-tenant, grandchild, and
  multiple-distinct-parent lineage all deny with the corresponding typed reason
  and no parent-state lookup where unsupported.
- A qualified Permit records both the parent and child evaluations through the
  mandatory recorder; failed mandatory recording yields no usable Permit and
  `Unavailable`.
- Derive approval withdrawal blocks new issuance while the already-issued
  child remains usable only while it and current parent policy still permit;
  same-generation retry reuses the idempotent child identity and re-authorizes
  each use, so a new parent denial applies to the retry.

Unqualified by this record: general nested delegation, DAG ancestry, mixed
root/derived contributor snapshots, lifecycle-event binding (AE-1.2),
freshness/consumption qualification (AE-1.3), worker/broker transport (AE-2),
Biscuit, Gagamba, and gateways.

## AE-1.2 execution-lifecycle qualification (2026-10-12)

Local qualification of generic execution-lifecycle authority state and its
authenticated workflow-event translation. Environment: Windows x64, .NET SDK
10.0.401, .NET 8 and .NET 10 targets, real file-backed SQLite.

Focused suites:

| Suite | .NET 8 | .NET 10 | Scope |
| --- | --- | --- | --- |
| ExecutionLifecycleTests | passed, 0 skipped | passed, 0 skipped | Gate semantics, suspend/resume/terminal, replay, stale, fail-closed consumption |
| SqliteExecutionLifecycleStoreTests | passed, 0 skipped | passed, 0 skipped | Durable apply, replay/conflict/stale, reopen/recovery |
| WorkflowAuthorityLifecycleTranslatorTests (within Penghou.Hufu.Workflow.Tests) | passed, 0 skipped | passed, 0 skipped | Auth/forgery, event mapping, duplicate/stale, supersession |

Qualified observations:

- Suspend denies a derived use (`authority.lifecycle-suspended`); Resume
  restores the same binding; Cancel/Complete/Supersede are terminal
  (`authority.lifecycle-terminal`). Absence of any lifecycle fact is live.
- Duplicate transitions replay the original durable receipt; the same
  transition identity with different intent conflicts; stale/out-of-order
  sequence is rejected; invalid transitions conflict and terminal binds cannot
  be reopened.
- State and receipts survive store reopen; a fresh instance recovers state and
  replays the original receipt.
- A lifecycle store failure yields no usable Permit (`Unavailable`).
- The gate applies only to known derived grants in the qualified composition;
  root-only use is unchanged.
- The workflow translator authenticates through `IWorkflowAuthorityEventTrustSource`,
  rejects forged labels as `Conflict`, returns `Unavailable` when authentication
  is unavailable, and maps Suspend/Resume/Cancel/Complete/Supersede to neutral
  transitions without introducing workflow vocabulary into core.

Unqualified by this record: new-generation authority creation (events never
create rights; a new generation is a new binding needing explicit authority),
atomic provider-I/O start ordering, lifecycle gating of root-only profiles,
visibility authority, and any broker/Biscuit/Gagamba integration.

## AE-1.3 derived-authority freshness qualification (2026-10-12)

Local qualification of optimistic capture-and-revalidation over the derived
decision read set. Environment: Windows x64, .NET SDK 10.0.401, .NET 8 and .NET
10 targets.

Focused suite: `DerivedAuthorityFreshnessTests` (8 tests) plus the extended
lifecycle versioned-read tests (gate sequence, SQLite `GetStateReadAsync`).

| Case (injected during mandatory recording) | Expected | Result |
| --- | --- | --- |
| Unchanged child/parent/lifecycle read set | Permit | Permit |
| Child snapshot moves (re-published) | Superseded | `authority.read-set-superseded` / `Unavailable` |
| Child revoked | Superseded | `authority.read-set-superseded` / `Unavailable` |
| Parent snapshot moves | Superseded | `authority.read-set-superseded` / `Unavailable` |
| Parent becomes denied | Superseded | `authority.read-set-superseded` / `Unavailable` |
| Lifecycle sequence moves | Superseded | `authority.read-set-superseded` / `Unavailable` |
| Lifecycle becomes Suspended | Superseded | `authority.read-set-superseded` / `Unavailable` |
| Permit recorded, head moves, revalidation fails | Superseded | recorded `Permit` evidence exists; delivered result `Unavailable`; usable Permit never returned |

Qualified observations:

- A Permit is returned only when every contributing version captured during
  authorization is observed unchanged again after mandatory evidence recording.
- A changed contributing head yields typed `authority.read-set-superseded`,
  classified as `Unavailable` (not `Deny`): retryable freshness failure, not a
  policy refusal.
- Lifecycle reads are versioned; the sequence is a freshness token, not a grant.
- The final revalidation is the last awaited authority operation before Permit;
  only synchronous clock/validity checks follow.
- The recorded decision is **evaluation evidence**, not delivered authorization.
  A recorded `Permit` can be superseded before the call returns: the durable
  evidence then says `Permit` while the caller receives
  `authority.read-set-superseded` / `Unavailable`. A host decides dispatch from
  the returned result, never from the mere presence of a recorded `Permit`, and
  historical reconstruction must not replay a recorded `Permit` as a fresh
  authorization.

Unqualified by this record: cross-store transactional consistency (the re-read
is independent reads, not one simultaneous snapshot), atomic provider-start
ordering, and any stronger freshness profile a future consumer may require. The
revalidation detects intervening mutation of the relevant heads; it does not
prove all stores shared one exact state at one instant.
