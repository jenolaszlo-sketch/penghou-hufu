# Documentation guide

New to Hufu? Read the [project README](../README.md) for the goal, then [the overview](overview.md) for the request flow and trust boundaries. This index groups the deeper documents by the question they answer. **Profile documents and their linked evidence define delivered guarantees; the specification and roadmap include future work.**

## Understand the design

| Document | Use it for |
| --- | --- |
| [Overview](overview.md) | Plain-language execution flow, ownership and current scope. |
| [Authority, delegation and enforcement](authority-delegation-enforcement.md) | Canonical ownership model, execution lifetime, future Biscuit use and gateway boundaries. |
| [Model reconciliation](authority-model-review.md) | Resolved contradictions, source gaps and decisions still required before implementation. |
| [Architecture](architecture.md) | Detailed library boundaries, authority store and integration design. |
| [Workflow authority specification](workflow-authority-spec.md) | Intended security invariants, behavior and acceptance gates. |
| [Roadmap](roadmap.md) | Delivery order, open gates and the language-neutral direction. |
| [Authority/enforcement implementation plan](authority-enforcement-implementation-plan.md) | Concrete AE-1–AE-5 slices, HUFU-R1 contention/CI closure, dependencies and acceptance cases. Planning document; no new runtime qualification. |
| [Local-first runtime design](local-first-authority-runtime.md) | Longer-term analysis, brokers and evidence design. |
| [Decision records](decisions/) | Why the project chose its major boundaries. Start with [ADR 0001](decisions/0001-hufu-owns-workflow-authority.md), [ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md), and the [derived-authority decisions](decisions/0012-derived-authority-lineage.md). |

## Integrate an authority profile

| Document | Use it for |
| --- | --- |
| [Current authority profile](current-authority-profile.md) and [durable store](durable-authority-store.md) | Snapshot, revocation, evidence and SQLite store limits. |
| [Execution-lifecycle authority](execution-lifecycle-authority.md) | Generic suspend/resume/terminal lifecycle state, its durable SQLite store and the authenticated workflow-event translator (AE-1.2). |
| [Core admission and issuance](core-admission-and-issuance.md) | Bounded evaluation capacity, issuer ceiling and exact publication approval. |
| [Decision explanations](decision-explanations.md) and [optional telemetry](optional-telemetry.md) | What can be disclosed or observed without changing authorization. |
| [Workflow authorizer](workflow-authorizer.md) | Host services and requirements for the neutral workflow adapter. |
| [Resource package adoption](resource-package-adoption.md) and [Luban v2 read/diff](luban-v2-authorization.md) | Optional resource integration and its supported read/diff scopes. |
| [Governed single-patch host](single-patch-host.md) | Exact mutation approval, provider-side start, outcome journal and recovery. |
| [Local Windows host services](local-host-services.md) | Concrete one-operator, one-file application and its trust assumptions. |
| [Package release profile](package-release-profile.md) | Current published version (`0.1.0-preview.5`), package set and release evidence. |

## Explore other compositions

| Document | Status and use |
| --- | --- |
| [Plan host sample](../samples/Hufu.PlanHost/README.md) | Experimental admitted-plan start; it creates a run without executing it. |
| [Sandbox runner sample](../samples/Hufu.SandboxRunner/README.md) | Experimental end-to-end native execution diagnostic. |
| [Authority-mediated execution](sandbox-execution.md), [Zhinu composition](sandbox-execution-zhinu.md), [Fuwen composition](sandbox-execution-fuwen.md) | Frozen, unpublished implementation slices with explicit qualification limits. |
| [Biscuit profile](biscuit-integration-profile.md) and [qualification](biscuit-integration-qualification.md) | Optional experimental credential adapter; outside the published package set. |
| [Luban integration](luban-integration.md) and [AMLE guide](authority-mediated-language-execution.md) | Existing language-oriented integration and its historical design context. New Hufu work follows the [language-neutral roadmap amendment](roadmap.md). |

## Follow implementation and evidence

- [Current handoff](zhinu-authority-handoff.md) and [completion review](completion-review.md) record delivery checkpoints. Earlier version counts and work orders in these documents are historical; check the [release profile](package-release-profile.md) for the current package baseline.
- [Published package qualification records](qualification/) and the qualification links in each profile contain the exact source, platform and consumer evidence behind a claim.
- [Operation-start profile](operation-start-profile.md) and [qualification](operation-start-qualification.md) document the separate legacy SQLite composition. It is not the neutral workflow adapter's final-effect guarantee.
- [Document migration](document-migration.md) and [archived proposals](archive/) preserve provenance. Archived proposals do not describe the current implementation.

The published workflow contract comes from [Penghou](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-abstractions-plan.md); [Zhinu's authority-extension plan](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-plan.md) covers runtime integration. Hufu keeps its authority decisions and store semantics independent of that runtime.
