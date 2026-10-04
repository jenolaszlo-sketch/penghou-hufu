# HA-0A staged reuse review

Review date: 2026-10-04. This records HA-0A disposition of the older `.tmp/hufu-completion` review baseline against the then-current tree (`aa12ab5`), ADR 0011, roadmap, handoff, and resource baseline. It is historical candidate evidence, not an inventory of the current workflow stage. HA-0A is complete; current implementation and gates are described in [the workflow authorizer boundary](workflow-authorizer.md) and [handoff](zhinu-authority-handoff.md). The candidate's 510 tests and package checks apply only to those revisions and cases.

## Decision

Disposition: do not bulk-install the old candidate. Keep the exact Zhinu preview.15 legacy profile intact. That snapshot predates `Penghou.Hufu.Workflow` and its neutral contract dependency; the current stage implements HA-1 after ZA-6. The table below records the earlier snapshot reuse decisions; current status and package boundaries are updated to present evidence.

| Group | Disposition | Reason / bounded next action |
|---|---|---|
| Bounded issuance | Implemented as a separately reviewed core change | `BoundedAuthorityIssuanceAuthorizer` adds an explicit `IAuthorityIssuanceTrustSource`, validates authenticated actor identity, issuer ceiling and exact approval binding, then composes existing store access policy. It is generic Hufu authority, not workflow logic. Carry its tests and implementation as one candidate; review denied/missing/mismatched identity and approval, replay, expiry and ceiling containment. Capacity and trust-source truth remain host obligations. |
| Bounded request admission | Implemented as a separately reviewed core change | `BoundedAuthorityRequestAuthorizer` limits executing and queued calls, fails unavailable on saturation/timeout, revalidates inner request/result identity and status, and releases capacity through cancellation/error paths. It has no workflow dependency. One instance must actually be shared by the host to bound a common native/store owner; per-call instances do not establish aggregate capacity. Treat constants and deployment capacity as policy choices, not universal safety guarantees. |
| Independent IO | Reuse as its own integration change | Candidate `Penghou.Hufu.IO` work stays optional and depends on neutral IO + Hufu contracts. Keep per-resource checks at provider access and preserve discovered-child/non-disclosure coverage. This is not replaced by workflow preflight or a generic outer wrapper. Reconcile against already published IO package versions before touching project references. |
| Luban v2 | Defer independently | The candidate's `HufuLanguageAuthorityProfile` and v2 tests expand semantic language authorization. Roadmap says v2 policy support is future work; current v2 rejection is qualified. Do not bundle this with workflow translation. If resumed, keep semantic admission distinct from IO checks, and require trusted profile/version mapping with no root fallback for dynamic inputs. |
| Single-patch host and outcome journal | Defer to an independently qualified host profile | Candidate files `src/Penghou.Hufu.Luban.Sqlite/HufuSinglePatchHost.cs` and `SqlitePatchOutcomeJournal.cs` introduce an optional host composition; they are not generic core and do not establish a production trusted host. The host's approval, command factory, locked object binding, provider and durable recovery need a concrete qualified composition. Keep the separate journal even if pursued; don't make it mandatory for Hufu, Luban or Zhinu. Preserve Reserved/Started/terminal ambiguity and ensure replay can inspect history but cannot dispatch again. |
| Package/API/CI tooling | Reuse mechanics, regenerate all candidate outputs | The candidate has `eng/Pack-HufuPackages.ps1`, `Inspect-HufuPackageSet.ps1`, `Test-HufuPackageSet.ps1`, shipped/unshipped API files, and CI/publish workflows. Their validation approach is reusable after the reviewed project graph is chosen. The old six-package set is not the final release set: it omits the new Workflow adapter and may reflect stale references/versioning. Recompute package IDs, dependency closure, API inventories, package inspection, fresh-cache consumers and CI matrix from the accepted tree. Publish only through the later HA-3 user-controlled gate. |
| Old aggregate install/completion snapshot | Reject as an installation source | ADR 0011, the handoff and roadmap explicitly prohibit bulk installation. Candidate versions do not capture today's exact published resource and Zhinu package baselines, neutral workflow contract, or corrected test graph. Use individual source files and evidence only after reviewing their diffs against current `aa12ab5`. |
| Old direct Zhinu coupling in host/tests and candidate package graph | Reject as new architecture | The direct runtime SQL/adapter profile is retained only as the frozen legacy integration and its regression evidence. The new adapter cannot depend on Zhinu runtime, SQL, or six-package candidate composition. Don't replace or silently weaken legacy atomic mutation-start semantics. |

## Current core reuse checkpoint

The two core candidates were individually reviewed and rewritten against the
current tree. Active cancellation no longer releases capacity while source,
evaluator or evidence work continues. Publication no longer bypasses the host's
operation policy; approval binds command, sequence, snapshot and expiry, and
current issuer authority/approval are reloaded after policy. The issuer ceiling
must come from authenticated host state, with conservative containment in each
layer. See [profile](core-admission-and-issuance.md) and
[new qualification](qualification/core-hardening.json): 73 focused cases per
framework, 700 full-suite cases and refreshed six-package qualification.
The table's older candidate descriptions are historical review context; the
new profile specifies the accepted behavior. No other old-snapshot group was
bulk-installed. The bounded [decision explanation slice](decision-explanations.md) is now implemented; telemetry remains future work.

## Candidate inventory for bounded extraction

The concrete candidate additions identified in `.tmp/hufu-completion` are:

- Core issuance/admission: `src/Penghou.Hufu/BoundedAuthorityIssuanceAuthorizer.cs`, `BoundedAuthorityRequestAuthorizer.cs`; tests `tests/Penghou.Hufu.Tests/AuthorityIssuanceProfileTests.cs`, `BoundedAuthorityRequestAuthorizerTests.cs`.
- IO integration: candidate source remains in `src/Penghou.Hufu.IO/` (`HufuResourceAuthorizer.cs`, `HufuWorkspaceAccess.cs`) with `tests/Penghou.Hufu.IO.Tests/`; it should be reviewed against current published IO/Luban baselines rather than copied as a package set.
- Luban v2: `src/Penghou.Hufu.Luban/HufuLanguageAuthorityProfile.cs`, changes to `HufuLanguageAuthorizer.cs`, and `HufuLanguageV2AuthorizerTests.cs`.
- Host journal: `src/Penghou.Hufu.Luban.Sqlite/HufuSinglePatchHost.cs`, `SqlitePatchOutcomeJournal.cs`, and `tests/Penghou.Hufu.Tests/HufuSinglePatchHostTests.cs`, `PatchOutcomeJournalTests.cs`.
- Package tooling: `eng/Pack-HufuPackages.ps1`, `Inspect-HufuPackageSet.ps1`, `Test-HufuPackageSet.ps1`; `src/**/PublicAPI.*.txt`; `.github/workflows/ci.yml`, `.github/workflows/publish.yml`, solution/project files and package docs. These are inputs for regeneration, not reusable release metadata.

Historical inventory only: the reviewed snapshot had no `Penghou.Hufu.Workflow` project and mixed legacy operation-start coverage into the core test project. Current staging implements Workflow and has completed HA-0B: legacy operation-start coverage is retained in a dedicated suite. The core/Cedar/Biscuit graph is Zhinu-independent.

## Required adapter correctness boundary (HA-1)

The adapter translates trusted host execution facts and immutable, versioned workflow declarations into Hufu requests. It must not decide that a workflow is authorized merely because its structure declares a requirement.

- Obtain authenticated actor/session, tenant, subject and execution binding through the host's trusted context channel. Cross-check all contract context identity fields against this binding; never construct trusted Hufu identity from caller-supplied requirement metadata, workflow history, activity labels or receipts.
- Treat declared requirements as claims to evaluate, not permission. Validate the supported schema/version, reject unknown or malformed requirements, bind the complete normalized requirement set to the exact activity/plan revision and invocation, and fail closed on missing or inconsistent fields. Do not infer broader scope from summaries, duplicate declarations, or an unrecognized version.
- Map identity and each requirement deterministically to Hufu's typed request/evaluator inputs. Bind the Hufu decision to exact tenant, actor, workflow/run/revision/activity/attempt and requirement-set identity. Do not let the adapter query workflow SQL or accept runtime state as proof of identity.
- Preserve closed result distinctions: permit only with mandatory decision evidence; deny remains deny; unavailable/evaluator/evidence errors remain unavailable/failure and can never fall back to allow. Map approval-required to a durable pending decision only when the contract/runtime supports that state; an activity's own request or an old approval receipt is not an approval. Resolve the exact approval through the authenticated host/approval path, then re-evaluate current authority.
- Every retry, resumed wait and fresh dispatch must perform fresh authorization against current Hufu authority and exact live execution context. Retain bounded decision/evidence references for explanation/recovery, but historical permits never authorize dispatch. Recheck runtime fencing/attempt binding after any asynchronous Hufu evaluation; if the context advanced, reject the result.
- Keep legacy atomic start profile semantics independent. An async workflow callback is preflight and cannot claim the SQLite writer-order guarantee for revocation versus real mutation start. Keep the qualified `Penghou.Hufu.Zhinu.Sqlite` package references until a separate ZA-5B equivalence/retirement decision.

## Package and dependency risks

The current `Penghou.Hufu.Workflow` project references Hufu core and the exact published `Penghou.Workflow.Abstractions` contract (plus framework libraries). It does not reference Zhinu, Zhinu.Sqlite, workflow SQL/runtime packages, Luban, IO, Cedar, Biscuit or a host UI. Hufu core/Cedar/Biscuit stay independent of every Zhinu package.

HA-0B is complete: legacy operation-start and process-worker regression coverage is isolated in a dedicated integration suite. This does not add Zhinu to the neutral adapter or remove legacy cases. Recompute the eventual Hufu package set and negative transitive-dependency checks from actual project references; old package tests are not evidence for the new adapter closure.

## Evidence limits and next bounded deliveries

The old report of 510 passing tests and isolated package consumers supports candidate-level behavior only. It does not prove trusted issuer/approver services, aggregate cross-process capacity, production host custody, resource enforcement at discovered accesses, durable workflow waits/replay, or an atomic effect-start boundary. Existing RA-4/RA-5C resource-package qualifications remain the baseline; RA-G1 and deferred VFS gates remain open. Current completion review keeps trusted host and mutation-start outcome qualification separate.

Current status: HA-0A/B and HA-1 are complete. Unit tests (52) and workflow integration tests (12) passed per .NET 8/10, and that workflow-phase full suite passed 554 total (277 per framework). The subsequent core-hardening suite passed 700 total; current explanation qualification passes 764 total (382 per framework), including 32 new explanation cases each. See [current evidence](qualification/decision-explanations.json). HA-2 fresh-cache candidate-package qualification passed on both frameworks; HA-3 source tooling is ready but remote CI and user-controlled Hufu publication remain pending. See [the final qualification ledger](qualification/workflow-authorization.json). The six-package proposed release contains no production host. Biscuit remains experimental/unpublished; Luban v2 and the host journal remain deferred. These current results do not retroactively expand what the older candidate review established.
