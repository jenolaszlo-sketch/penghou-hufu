# Penghou.Hufu

Current integration follows the [product-neutral workflow contracts](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-abstractions-plan.md)
and [runtime plan](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-plan.md).
`Penghou.Workflow.Abstractions` 0.1.0-preview.2 is published, and Zhinu
0.2.0-preview.1 passed its remote CI and publication gates; exact evidence is in
[the qualification record](docs/qualification/zhinu-public-release.json).
The optional `Penghou.Hufu.Workflow` adapter is implemented against Hufu and the
neutral contract, without a workflow-engine dependency. All six Hufu packages
are published at `0.1.0-preview.2`, including explicit
[Luban v2 read/diff authorization](docs/luban-v2-authorization.md).
[Public-package and three-platform CI evidence](docs/qualification/hufu-luban-v2-public-release.json)
records the release; the original preview.1 evidence remains historical. See [the authorizer boundary](docs/workflow-authorizer.md),
[ADR 0011](docs/decisions/0011-neutral-zhinu-authority-extension.md), and the
[current handoff](docs/zhinu-authority-handoff.md).

Penghou.Hufu is the proposed reusable authority library and authority-store
boundary for workflows, delegated agents, and background operations. Its purpose
is to make execution permissions explicit, durable, and auditable across hosts.
The project has a narrow M1 implementation: bounded authority snapshot/request
contracts and a Luban read-language authorizer that requires host-supplied
current snapshots, an evaluator, and attributable decision recording. This is
not a production authorization host. An
optional SQLite prototype now supplies current-state publication, terminal
revocation, and required durable decision evidence through explicit host gates.
Hufu remains pre-release; no production workflow host is shipped.

Core also supplies optional bounded request admission and an authenticated
issuance gate. Admission limits a shared evaluator's active work and queue;
issuance composes host operation policy with current issuer authority and exact
command approval. See the [usage and security profile](docs/core-admission-and-issuance.md)
for cancellation, containment and host obligations.

Structured [decision explanations](docs/decision-explanations.md) capture the
actual evaluator outcome with bounded typed grant/layer facts. A separate host
policy controls summary or detailed disclosure; summaries hide policy and
resource details. Explanations are informational and confer no execution rights.

Optional [bounded telemetry](docs/optional-telemetry.md) observes request preflight
through a finite queue and one background worker. Closed categories and timing
contain no request/path/identity or diagnostic payload. Host-selected listeners
cannot replace required evidence or change authorization results.

The optional `Penghou.Hufu.IO` project adapts current Hufu decisions to the
neutral `IResourceAuthorizer` hook and supplies a request-gated workspace facade.
It keeps provider-side candidate and mutation-start hooks in the composition and
has no permissive default. Its conditional `WriteFile` permission is separate
from patch permission. A production Hufu-to-`IResourceMutationJournal` adapter
and qualification of the real locked mutation start remain pending. Normal builds consume exact published IO/Luban 0.1.0-preview.1 packages.
A fresh isolated public-feed restore passes 426 tests across both frameworks.
See [package adoption](docs/resource-package-adoption.md) and the
[remaining-work review](docs/completion-review.md).

## Authority-Mediated Language Execution

Hufu and [Luban](https://github.com/jenolaszlo-sketch/penghou-luban/blob/main/README.md) express the proposed
**Authority-Mediated Language Execution (AMLE)** pattern: an agent requests effects
through a constrained semantic language, and each protected effect is checked
against contextual authority before a trusted resource adapter performs it.
Luban supplies the language and runtime; Hufu supplies authority mediation;
Penghou.IO supplies resource abstractions and providers. Hufu is optional in
Luban, but host-supplied authorization is required.

Read the [AMLE guide](docs/authority-mediated-language-execution.md) and
[Luban's complementary guide](https://github.com/jenolaszlo-sketch/penghou-luban/blob/main/docs/authority-mediated-language-execution.md)
for the shared pattern, evidence and future simulation direction. Current WhatIf
is capture-only; complete governed mutation integration remains pending. AMLE
does not replace OS isolation for opaque native execution.

The design is recorded in:

- [Published Zhinu package adoption](docs/zhinu-package-adoption.md)
- [Workflow authority specification](docs/workflow-authority-spec.md)
- [Current authority prototype profile](docs/current-authority-profile.md)
- [Core request admission and authenticated issuance](docs/core-admission-and-issuance.md)
- [Exact-operation decision explanations](docs/decision-explanations.md)
- [Optional bounded authorization telemetry](docs/optional-telemetry.md)
- [Durable authority store profile](docs/durable-authority-store.md)
- [Co-located operation-start profile](docs/operation-start-profile.md)
- [Decision: current authority and evidence transactions](docs/decisions/0008-current-authority-store.md)
- [Architecture](docs/architecture.md)
- [Roadmap](docs/roadmap.md)
- [Decision: Hufu owns workflow authority](docs/decisions/0001-hufu-owns-workflow-authority.md)
- [Decision: use CedarSharp for policy evaluation](docs/decisions/0002-use-cedarsharp-for-policy-evaluation.md)
- [Decision: local-first authority runtime](docs/decisions/0003-local-first-authority-runtime.md)
- [Local-first runtime design](docs/local-first-authority-runtime.md)
- [Decision: LOP typed effects; no sandbox implementation](docs/decisions/0004-typed-effects-over-sandbox.md)
- [Luban v2 read/diff authorization and usage](docs/luban-v2-authorization.md)
- [Penghou.Luban integration](docs/luban-integration.md)
- [Decision: Luban owns typed effects](docs/decisions/0005-luban-owns-typed-effects.md)
- [Luban's canonical effect design](https://github.com/jenolaszlo-sketch/penghou-luban/blob/main/docs/typed-effect-runtime.md)
- [Original proposal](docs/archive/original-proposal.md)

The proposed scope includes requirements, grants, envelopes, approval requests
and decisions, delegation attenuation, revocation, and durable authority state.
Hosts supply identity, policy, resource resolution, credentials, and approval UI.
Hufu does not own workflow scheduling, execution recovery, or budget accounting.
Published contracts preserve their API baseline; broader integrations remain explicit roadmap work.
The local-first direction adds deterministic authority analysis, structured
execution requirements, host-selected capability brokers, and durable decision
evidence with optional OpenTelemetry export. The separate Penghou.Luban project
keeps typed requests/results independent of Hufu. StrictEffects exposes a bounded
operation vocabulary and rejects arbitrary shell/process requests. Fuwen supplies
control flow and Zhinu supplies durability. Hufu and Luban will not implement
a sandbox; broader native execution is a separate explicit host/provider choice.
SQLite current-state/evidence storage is implemented as a narrow optional
prototype. Host-authenticated publication is not a complete issuance, approval,
or delegation service. An optional Hufu/Zhinu adapter now commits authority
validation, runtime acquisition and required start evidence in one shared-file
SQLite transaction. Complete governed mutation hosts, terminal outcome recovery,
those lifecycles and reusable conformance helpers remain roadmap work.

Policy evaluation is selected through `Penghou.Hufu.Cedar`, backed by the
independent [CedarSharp wrapper](../CedarSharp/README.md) and official Cedar.
CedarSharp 1.0.0 is implemented, published, and qualified by its documented CI
matrix. Hufu core remains independent of CedarSharp. A fixed-schema Cedar
evaluator and known-root read consumer are locally qualified on Windows x64; the read
authorizer requires an `IAuthorityEvaluator` and provides no default permit
implementation.

See the [documentation index](docs/README.md) for status and provenance.

Normal builds use exact published IO and Luban preview.1 packages; see [resource package delivery](docs/resource-package-adoption.md) for candidate/source development and the public-feed qualification command.

Licensed under [Apache License 2.0](LICENSE).

## Optional Biscuit integration

- [Registered Biscuit integration profile](docs/biscuit-integration-profile.md)
- [Biscuit integration qualification](docs/biscuit-integration-qualification.md)
- [ADR 0010: optional registered Biscuit profile](docs/decisions/0010-registered-biscuit-profile.md)

The optional experimental adapter lives in Penghou.Hufu.Biscuit, with a
separate SQLite registry and start participant in Penghou.Hufu.Biscuit.Sqlite.
It preserves current Hufu/Cedar checks and requires trusted host services, both
evidence stores, and a composed exact-start gate. Local tests exercise native
Biscuit/Cedar, SQLite start ordering and real Windows Local/Luban and Hufu.IO reads.
Production host and governed mutation guarantees remain open; see the qualification. The adapter is unpublished
and does not make Biscuit a core dependency.
