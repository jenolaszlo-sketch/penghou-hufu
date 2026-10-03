# Local-first authority runtime

Status: Broad proposed behavior, 2026-10-01, with narrow current-snapshot/Cedar/read and optional current-state SQLite prototype APIs implemented. See the [current profile](current-authority-profile.md) and [store profile](durable-authority-store.md) for their limits.
[ADR 0003](decisions/0003-local-first-authority-runtime.md) records the direction.
This elaborates the [authority specification](workflow-authority-spec.md), whose
revocation, recovery, and confinement requirements continue to apply. Interface
names below are illustrative, not published contracts.

[ADR 0004](decisions/0004-typed-effects-over-sandbox.md) and the
[typed-effect design](typed-effect-runtime.md) refine the capability boundary:
use LOP with strict typed operations, and do not implement a sandbox. Any earlier
proposal to build an isolation provider is superseded.

## Authority store and lineage

The initial optional SQLite adapter implements `IAuthorityStore` for immutable
current snapshots, terminal revocation, and required decision evidence under
[ADR 0008](decisions/0008-current-authority-store.md). The broader lifecycle below
remains planned. Persist immutable grants and exclusions, parent/child
delegation, approvals, requirements, policy revisions, revocations, decisions,
and broker receipt references. Include tenant-bound principal/resource bindings
and the exact fact snapshots needed for attribution. The host remains the source
of identity and resource truth; Hufu is not a second mutable IAM catalogue.

The model is graph-shaped without requiring a graph database. Define traversal,
cycle rejection, depth limits, version checks, and transactions at the contract
level. Reject cross-tenant edges and unsupported lineage. A future graph adapter
must satisfy the same consistency and recovery tests as SQLite.

Authority evidence belongs to Hufu. Zhinu or another execution runtime retains
its operation journal, dispatch, and recovery ownership. Stable operation IDs
and authenticated receipt references link the stores without a competing
scheduler or an assumed cross-store transaction.

## Deterministic analysis and explanation

Produce structured reasons from the actual evaluator and trusted bindings:
applicable grants and lineage, local exclusions versus mandatory denials,
resource relationships, policy versions, missing requirements, and enforcement
readiness. Cedar determining-policy IDs alone are not the entire explanation;
Hufu must also explain its own layering, validity, and execution checks.

Support four views:

1. Explain an exact operation's result at a recorded authority snapshot.
2. Propose sufficient authority for that operation within current issuer and
   requester eligibility; never suggest bypassing a mandatory prohibition.
3. Compare two authority states and their effect requirements.
4. Simulate a proposed change without issuing grants or executing protected I/O.

For example, explain that a repository grant covers a file but an exclusion
removes it. Explanations themselves require access control and redaction; they
must not disclose hidden resources or confidential policy through denial text.

Simulations bind policy/schema/evaluator versions, entity snapshot, time,
resource universe, assumptions, and proposed changes. Report exact results only
for supported typed scopes or a fully specified finite query domain. For
unbounded resources, arbitrary predicates, or unavailable facts, return a scoped
result with explicit unknowns. Historical request replay demonstrates those
requests, not all reachable authority or safety of arbitrary future actions.
Never use an incomplete simulation as delegation-containment proof.

Reconstruct historical decisions using pinned evidence; distinguish this from
checking whether the operation is allowed now. A simulation or explanation is
not an admission receipt. Actual use rechecks current authority and resource
state. Retention gaps produce an explicit unavailable/incomplete explanation.

An optional AI explainer may summarize an authorized deterministic projection.
It cannot modify calculations, approve operations, or install policy. Sending
that projection to a model remains a separately governed data release.

## Execution contracts

An allow decision is usable only when the exact operation's execution contract
is satisfiable. A versioned contract identifies the operation, subject, resource
binding, inputs/artifact digest, approval, validity, and structured requirements.
Examples include redaction, a 500-row limit, an exact destination, an approved
credential purpose, and an existing budget reservation.

Each requirement identifies trusted enforcer/verifier contracts and the evidence
needed. Verify preconditions before dispatch; enforce continuous requirements
throughout execution; collect post-operation evidence afterward. Post-hoc
verification cannot substitute for preventing an unauthorized disclosure or
excess write. For redaction, bind verification to the exact released output.
For row limits, enforce the limit before releasing each affected portion.

Compose applicable requirements as restrictions. Conflicts, unsupported
versions, unavailable handlers, or missing proofs block execution. Handler
registration is host-controlled and version-bound. A caller's assertion that a
requirement was satisfied is not trusted evidence. Registered handlers still
need conformance tests; registration alone does not establish their guarantees.

Contracts and receipts are bound references, not reusable bearer permissions.
They cannot skip current revocation, expiry, resource preconditions, or fences.

## Capability abstractions and host composition

Penghou.Luban is the separate neutral typed request/result/provider project,
selected in [ADR 0005](decisions/0005-luban-owns-typed-effects.md). Hufu keeps
its [authority integration](luban-integration.md); Luban owns its own plan. Start
with bounded filesystem and qualified Git inspection effects, not generic process
execution. HTTP and approved developer adapters follow concrete demand.
Credential interfaces expose bounded use, not secret bytes. Ordinary local
implementations remain an explicit host choice without a Hufu protection claim.

```text
Penghou consumer -> neutral typed effect requests/results
Host composition -> ordinary local implementations
                 or Hufu-governed brokers -> Hufu authority + resource adapters
Hufu core -> neither consumer libraries nor host composition
```

A governed host selects its mode explicitly, validates required registrations,
and rejects missing governed implementations. No fallback from a failed or
missing Hufu broker to an unrestricted local implementation is allowed.
Trusted consumer code must route protected effects through the interfaces.
Direct file/socket/process APIs remain possible in arbitrary same-process code;
dependency injection does not remove those ambient powers.

Filesystem brokers bind authorization to the object actually accessed, covering
canonicalization, aliases, case rules, symlinks, junctions/reparse points,
alternate streams, and races between checking and opening. If a supported OS
cannot provide a required narrow guarantee, reject that operation/profile.

HTTP brokers authorize the actual destination, redirects, resolved endpoints,
proxy behavior, and data release. Credentials are attached only for the approved
audience and must not follow redirects to an unapproved destination. Validation
must remain connected to the endpoint used for the request.

Process brokers authorize admission, executable binding, structured arguments,
working directory, environment exposure, and credential policy. Process
mediation alone does not constrain descendant file or network access. Declare
whether an execution profile offers trusted mediation or tested containment.
Never report descendant effects as broker-covered merely because process start
was authorized.

The initial agent surface is StrictEffects: no arbitrary process, shell, script,
or project-code execution. ApprovedTools is a separate opt-in mode; typed names
do not contain the code such tools execute. Hufu and Luban will not build an
OS/container sandbox or an isolation provider. Existing external providers may
satisfy separately requested guarantees, or hosts may explicitly accept broader
native powers in a different mode. No requested containment is silently weakened.

## Local credential brokering and approvals

Resolve opaque credential references in trusted host infrastructure. Bind use
to subject, tenant, operation, audience, purpose, and validity. Possession of a
reference alone cannot authorize use. Keep secret bytes out of agent context,
workflow state, authority records, arguments, and ordinary logs. Sanitize broker
errors and response projections that could expose credentials.

Identity lifetime, credential lifetime, and authority lifetime are separate.
Renewing one does not renew the others. Revocation blocks new brokered uses;
already dispatched effects and independently issued credentials need their own
documented reconciliation/revocation behavior. SPIFFE and Vault integrations
are optional future host adapters, not local-first dependencies.

Initial approvals bind exact effects, resources, artifacts, destinations, and
expected versions. Support temporary elevation within the issuer's permitted
ceiling and explicit expiry. Changed inputs invalidate exact approval. Retain
authenticated actors and versioned decision records so future multi-person and
separation-of-duties rules are possible, without implementing them initially.

## Decision evidence and observability: foundational delivery

Design evidence with the first authority/store contracts, not after broker
implementation. Every admitted decision and governed operation must be durably
attributable, including denial and indeterminate outcomes when the evidence store
is available. For failed persistence, return an explicit failure and perform no
protected dispatch; do not claim a durable decision was recorded.

Record stable decision/request/operation IDs; tenant and authenticated subject;
lineage and grant versions; exact evaluator/schema/policy/fact versions; resource
and input bindings; approval and requirement identities; enforcer versions;
fence/start ordering; broker/provider IDs; outcome, uncertainty, and receipt
provenance. Evidence is authorized and minimized; sensitive payloads stay behind
protected references. Read/export/retention/deletion policy applies to evidence.

Commit mandatory start evidence before protected dispatch. Link it to the runtime
intent and later broker receipt idempotently. On crash after possible dispatch,
record or recover uncertainty and reconcile using the runtime/provider protocol.
Do not automatically repeat a potentially completed effect because its receipt
is missing. Outcome persistence failure after an effect is different from a
pre-dispatch failure; neither implies rollback.

Instrument through .NET tracing and metrics compatible with OpenTelemetry.
Hosts choose SDK/exporters and collectors; no collector or network service is
required locally. Cover evaluation latency, broker execution, approval waits,
revocation propagation where measurable, and typed failures. Traces correlate
to authorized decision/operation references. Metrics use bounded categories,
not raw paths, subject IDs, policy bodies, or operation IDs as labels.

Telemetry may be sampled or lost and never serves as authoritative evidence.
Exporter failure does not block work once mandatory local records commit.
Untrusted callers cannot select authority-bearing correlation identities. Apply
redaction and access controls to diagnostics as well as durable records.
Signed checkpoints and external anchoring remain later work; local records do
not claim resistance to a compromised host administrator.

## Conformance and incremental delivery

Test real brokers and state transitions from the first integration. Each broker
publishes its supported platform, capability versions, requirement handlers,
resource-binding behavior, revocation ordering, and mediation/isolation profile.
Tie claims to executed tests and explicitly report unsupported guarantees.

Start with local filesystem allow/exclude cases, alias/link and changed-resource
races, wrong tenant/workload, requirement failure, revocation/start ordering,
restart, duplicate requests, and ambiguous external completion. Add HTTP redirect
and credential-audience cases with the HTTP broker; test process admission and
actual isolation separately when those profiles exist. Include failed evidence
writes, unavailable exporters, redaction, and receipt idempotency.

Keep ordinary tests in the repository first. Publish reusable conformance helpers
in Penghou.Hufu.Testing with the first third-party adapter consumer. A process
admission test cannot establish sandbox confinement, and mocks alone cannot
prove filesystem or network enforcement. Bypass and unsupported-path tests are
required alongside successful operations.

## Deferred portable delegation

Biscuit remains a candidate optional transport, not a selected dependency or a
second source of grants. Before adoption, specify a restricted versioned Hufu
token profile derived from existing authority, provable attenuation mapping,
issuer/key trust, tenant/subject/audience binding, replay controls, and bounded
size/depth. Unknown token logic or unsupported mappings must reject use.

Any token check must remain within authority issued by Hufu and applicable
mandatory policy. Bearer-token risks need workload binding or an equivalent
protected mechanism. Online checks are required for immediate revocation;
offline verification is a separate, explicitly bounded-staleness mode. Tokens
must not silently make stale grants usable. This design does not claim complete
Cedar-to-Biscuit semantic translation or approve a transport implementation.

## Provenance

The [2026-10-01 proposal](archive/local-first-authority-runtime-proposal-2026-10-01.md)
is preserved verbatim as input. This reviewed design qualifies its simulation,
dependency-injection, process containment, persistence, and token guarantees.
See the [roadmap](roadmap.md) for implementation order and the narrow implemented
profiles. This broad design document does not establish a complete runtime,
isolation provider, or production conformance guarantee.
