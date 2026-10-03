# ADR 0001: Hufu owns the reusable authority domain and store

Date: 2026-09-28.

Status: **Accepted direction: separate library named Penghou.Hufu.** Specific APIs, schemas, storage choices, and enforcement implementations remain proposed until reviewed and tested.

## Context

The workflow-authority proposal spans Fuwen, Guihua, Zhinu, and Qingniao. Grant evaluation, attenuation, approval requests, revocation, and durable authority state are cohesive responsibilities that can also serve standalone delegations and background operations. Reimplementing these mechanics in each consumer risks inconsistent enforcement and recovery semantics.

The user selected a separate Penghou library named Hufu and requested consolidation of its design documents into the new project.

## Decision

Penghou.Hufu owns host-neutral authority contracts, evaluation orchestration, delegation attenuation, and authority-store semantics. It initially runs as an embedded .NET library. The host supplies identity, policy, concrete resource binders, trusted evaluators, credentials, and decision surfaces.

Hufu's store records grants, envelopes, approval requests and decisions, revocation, authority admission bindings, and durable authorization provenance. It does not own workflow scheduling, active revisions, execution recovery, or budget settlement. Those remain with their existing components.

Fuwen represents Hufu requirements in compiled plans. Guihua proposes work and missing-authority requests. Zhinu activates admitted work and owns execution fences and operation history. Qingniao binds and enforces attenuated delegation. Resource brokers and isolation environments enforce actual I/O boundaries.

Core Hufu subjects are generic execution identities with tenant and lineage bindings. Workflow-specific references are provided by adapters. Core Hufu must not reference Fuwen, Guihua, Zhinu, Qingniao, a host UI, or a concrete persistence package.

The initial repository contains only the core scaffold and design material. SQLite and reusable testing adapters are planned; they are created with their first real implementation. No API is advertised as providing enforcement until the corresponding tests and integration boundary exist.

## Consequences

- Shared authority behavior has one owner and a reusable conformance suite.
- Hosts can choose storage without moving policy or credentials into workflow source.
- Authority and workflow persistence need explicit idempotency, conditional writes, and recovery integration; a shared library does not remove distributed-ordering problems.
- Hufu remains useful without a workflow engine and does not become another IAM or scheduling system.
- This decision supersedes the earlier suggestion to avoid a separate library merely for shared records: the extracted responsibility now includes a coherent authority domain and durable store.

## Alternatives

Keeping the logic inside each consumer was rejected because it duplicates decisions and revocation semantics. Making Zhinu the authority owner was rejected because standalone consumers should not require a workflow engine. Starting with a mandatory network service was deferred because embedded deployment is enough to prove the first integration.

## Related documents

- [ADR 0002: CedarSharp policy evaluation](0002-use-cedarsharp-for-policy-evaluation.md) selects the policy backend while preserving this authority-domain boundary.
- [Architecture](../architecture.md)
- [Behavioral specification](../workflow-authority-spec.md)
- [Roadmap](../roadmap.md)
