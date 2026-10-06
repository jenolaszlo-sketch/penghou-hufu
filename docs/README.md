# Penghou.Hufu design documents

Start with [Penghou's workflow contract plan](../../Penghou/docs/workflow-abstractions-plan.md),
then the [runtime integration plan](../../Penghou.Zhinu/docs/authority-extension-plan.md),
[ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md) and
[handoff](zhinu-authority-handoff.md). They supersede earlier integration order;
historical implementation/qualification entries below remain evidence for their
stated versions, not acceptance of the new seam.
The [activity queue](../../Penghou.Zhinu/docs/authority-extension-activities.md)
is the current source for ready work, dependencies, held work and close-out evidence.

Read the [Authority-Mediated Language Execution (AMLE) guide](authority-mediated-language-execution.md) for the shared pattern, complementary Luban/Hufu roles, and current implementation limits.


Hufu is a reusable authority library and store boundary. Seven preview.3 packages are public, covering current authority/evidence, Luban read/diff, neutral workflow preflight and governed single-patch start/outcomes. The non-packable local Windows host adds actual operator identity, protected exact approval/issuance and bounded custody for one exact file. Explicit host routing and qualification remain required; broader product and organizational services are open.

| Document | Purpose | Status |
| --- | --- | --- |
| [Local Windows host services](local-host-services.md) | Concrete OS identity, protected issuance/approval, bounded custody and operator manual | 80 local cases and separate-process CLI passed; remote CI/product routing pending |
| [Governed single-patch host](single-patch-host.md) | Exact admission, final resource checks, co-located start and durable recovery | Seventh preview.3 package published and exact-public-package qualified |
| [Luban v2 read/diff](luban-v2-authorization.md) | Explicit host profile, exact scopes, usage and evidence limits | Published preview.2; governed mutation host remains separate |
| [Package release profile](package-release-profile.md) | Published baseline, compatibility, CI and input-free publication | Seven preview.3 packages published; public-feed and CI evidence recorded |
| [Optional telemetry](optional-telemetry.md) | Bounded isolated preflight observations, privacy and host export obligations | Implemented request slice; broader broker/approval/revocation instrumentation remains open |
| [Decision explanations](decision-explanations.md) | Exact evaluator capture and separately authorized summary/details | Bounded typed-path profile; see linked qualification and limits |
| [Core admission and issuance](core-admission-and-issuance.md) | Shared evaluator capacity, current authenticated issuer ceiling and exact command approval | Implemented; local .NET 8/10 and candidate-package qualification in linked record |
| [Zhinu package adoption](zhinu-package-adoption.md) | Exact public preview.15 dependencies without a source checkout | 426 cases passed across .NET 8/10 |
| [Current completion review](completion-review.md) | Published dependency adoption and prioritized remaining Hufu gates | V2 packages published; production host gates remain open |
| [Architecture](architecture.md) | Library boundary, dependencies, store ownership, and integration | Proposed design |
| [ADR 0001](decisions/0001-hufu-owns-workflow-authority.md) | Decision to establish Hufu as a separate library | Library boundary selected; API/storage details proposed |
| [ADR 0002](decisions/0002-use-cedarsharp-for-policy-evaluation.md) | Official Cedar through the qualified independent CedarSharp 1.0.0 wrapper and Hufu adapter | Wrapper qualified; Typed Cedar/read profile locally qualified; production authority runtime pending |
| [ADR 0003](decisions/0003-local-first-authority-runtime.md) | Local-first authority runtime, neutral capability boundary, evidence, and staged enforcement | Design direction selected; implementation pending |
| [ADR 0004](decisions/0004-typed-effects-over-sandbox.md) | LOP typed effects; no owned sandbox; refines ADR 0003 | Direction selected; implementation pending |
| [ADR 0005](decisions/0005-luban-owns-typed-effects.md) | Separate Penghou.Luban ownership and documentation migration | Name/boundary selected; runtime integration pending |
| [Luban integration](luban-integration.md) | Hufu authority mapping, admission/start boundary, evidence and joint gates | Read authorizer and separate runtime start prototype; governed mutation host pending |
| [Authority-mediated execution (HG-1)](sandbox-execution.md) | Adapter joining Hufu authority (ExecuteProcess) to an external execution provider (Gagamba) | HG-1 frozen (tag `arch-hg-1`); not qualified or published |
| [Workflow-authorized execution (HZ-1A)](sandbox-execution-zhinu.md) | Zhinu durable activity over the sandbox host: durable correlation, capability-driven recovery, cancellation vs revocation | Implementation slice; not qualified or published |
| [Typed effect runtime](../../Penghou.Luban/docs/typed-effect-runtime.md) | Luban's canonical catalogue, modes, providers, replay and conformance design | Moved to Luban; old Hufu path forwards |
| [Local-first runtime design](local-first-authority-runtime.md) | Store/lineage, deterministic analysis, execution contracts, brokers, credentials, evidence, and conformance | Broad design remains proposed; see the limited current-authority prototype above |
| [Authority specification](workflow-authority-spec.md) | Security invariants, behavior, UX, and acceptance gates | Normative design; only a narrow prototype is implemented |
| [Current authority prototype profile](current-authority-profile.md) | Current snapshot, evaluation, evidence, and Luban read-authorizer limits | Narrow Windows x64 read profile locally qualified; production host gates remain |
| [Earlier qualification](authority-profile-qualification.md) | Earlier native/Cedar/read evidence and exact remaining limits | 44 Hufu, 86 shared-provider, 167 Luban, 375 Zhinu SQLite tests per runtime |
| [Durable authority store profile](durable-authority-store.md) | Current slot, publication/revocation, replay, required evidence and capacity semantics | Optional SQLite prototype |
| [ADR 0008](decisions/0008-current-authority-store.md) | Current authority transactions and evidence separation | Initial store implemented; see ADR 0009 for the narrow shared-file start gate |
| [Store qualification](durable-authority-store-qualification.md) | Local SQLite/Cedar/Luban evidence and remaining host gates | Current-state/evidence slice locally qualified |
| [ADR 0009](decisions/0009-colocated-operation-start.md) | Shared-file authority/runtime start transaction and block-new-starts semantics | Experimental composition implemented |
| [Operation-start profile](operation-start-profile.md) | Exact intent, runtime participant, atomic journaling, replay and remaining host gates | Co-located SQLite prototype |
| [Operation-start qualification](operation-start-qualification.md) | Real runtime/Cedar transaction tests and supported limits | Local start-boundary evidence |
| [Registered Biscuit integration profile](biscuit-integration-profile.md) | Optional online registered tokens, host bindings, evidence and co-located start checking | Experimental adapter and SQLite composition; Windows read consumer locally qualified |
| [Biscuit integration qualification](biscuit-integration-qualification.md) | Pinned wrapper candidate, local test evidence and remaining enforcement gates | 93 Biscuit cases per framework; real Windows reads and SQL start fixture |
| [ADR 0010](decisions/0010-registered-biscuit-profile.md) | Adopt the optional registered Biscuit profile and its remaining gate | Experimental implementation; consumer qualification pending |
| [Roadmap](roadmap.md) | Concrete delivery order and completion evidence | Core prototype underway; durable and production gates open |
| [Document migration](document-migration.md) | Provenance and distinction from adjacent project documents | Migration record |
| [Original proposal](archive/original-proposal.md) | Historical user-supplied input | Superseded; retained unchanged below its archive preface |
| [Local-first proposal, 2026-10-01](archive/local-first-authority-runtime-proposal-2026-10-01.md) | User-supplied runtime expansion, preserved verbatim | Historical input; qualified by ADR 0003 and the reviewed runtime design |
| [Typed-effect proposal, 2026-10-01](../../Penghou.Luban/docs/archive/typed-effect-runtime-proposal-2026-10-01.md) | User-supplied LOP/effect runtime direction, preserved verbatim in Luban | Historical input; old Hufu archive path forwards |

Start with the architecture and ADR, then use the specification for behavioral requirements. The roadmap must not mark a security guarantee complete without boundary-level evidence.
The optional [Biscuit profile](biscuit-integration-profile.md) describes the
experimental adapter and its exact limits. Its [qualification record](biscuit-integration-qualification.md)
records local evidence without implying real-provider or production enforcement.

Current Biscuit integration resume point: [2026-10-02 recalibration](biscuit-integration-recalibration.md). Its final qualification supersedes historical checkout/build findings and counts above.

The reconciled resource migration and fresh candidate-package checks are recorded at the top of [the recalibration record](biscuit-integration-recalibration.md). IO and initial Hufu publication are complete; production mutation-host gates remain open.
