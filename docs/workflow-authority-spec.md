# Penghou.Hufu: workflow authority, delegation, and re-admission

Status: **Normative proposed architecture; narrow current-snapshot/read-authorizer and optional SQLite prototypes implemented, updated 2026-10-02.**

Authority home: **Penghou.Hufu**. Integrates with Penghou.Fuwen, Penghou.Zhinu, Penghou.Guihua, and Penghou.Qingniao. Host consumers include Marang, Guyabano, and other applications. Hongxian/Siming integration remains optional.

This is the normative behavioral specification for Penghou.Hufu. The library boundary is recorded in ADR 0001. A narrow prototype implements bounded authority snapshot contracts and a Luban known-root read authorizer; most behavior below remains a proposed contract, and the optional [store profile](durable-authority-store.md) supplies current-state/evidence persistence, and the experimental [co-located operation-start profile](operation-start-profile.md) supplies an atomic Hufu/Zhinu start boundary. Complete grant issuance, governed mutation hosts and terminal-outcome recovery remain pending. The original proposal is retained in docs/archive/original-proposal.md, and the original attachment is unchanged. Normative MUST/SHOULD/MAY statements below describe the proposed contract. API names are illustrative.

## Review verdict

The [local-first runtime design](local-first-authority-runtime.md) and
[ADR 0003](decisions/0003-local-first-authority-runtime.md) elaborate the store,
deterministic analysis, execution contracts, capability interfaces, and evidence.
They are implementation plans, not claims of existing enforcement.
[ADR 0004](decisions/0004-typed-effects-over-sandbox.md) and the
[typed-effect design](typed-effect-runtime.md) select LOP: constrain executable
effects instead of accepting arbitrary programs. Hufu and Luban will not build
a sandbox. StrictEffects rejects shell/process requests; required external
provider guarantees are never silently downgraded.

[ADR 0005](decisions/0005-luban-owns-typed-effects.md) establishes Penghou.Luban
as the separate effect-runtime owner. The detailed design moved there; Hufu's
[Luban integration requirements](luban-integration.md) remain in this project.

**Adopt the architecture, with the corrections below.** Workflow-scoped grants, per-activity attenuation, durable re-admission, and host-owned decisions are a strong foundation for useful agent autonomy. They avoid repetitive command approvals without giving a planner unrestricted access.

The original draft is not yet a sufficient security contract. Its strongest requirements are sometimes deferred until phase 3, and several examples imply guarantees that declared capabilities alone cannot provide.

| Priority | Gap in the supplied draft | Required correction |
| --- | --- | --- |
| Release blocker | Activity checks do not contain arbitrary code. `process.execute:dotnet` can run project-defined commands. | Reject it in StrictEffects. Broader tool/provider modes require explicit admission of actual powers; no containment claim from an executable allowlist. |
| Release blocker | Host-I/O checks, revocation, provider restrictions, and crash cases arrive after initial enforcement. | Make them admission prerequisites for every execution path claiming these guarantees. Unsupported paths must be rejected. |
| Release blocker | A revision can have the same permissions but a different consequential effect. | Separate capability grants from approval of intent and exact effects; every revision still needs admission. |
| Release blocker | Denied cloud modification is followed by a suggested CI workaround. | Denial must cover the prohibited effect across routes. CI, webhooks, and privileged services cannot serve as alternate authorization paths. |
| Release blocker | `DelegatedBudget <= ParentRemainingBudget` is insufficient for concurrent children. | Reserve shared capacity atomically; preserve reservations across retries, replans, and ambiguous outcomes. |
| Release blocker | “No new I/O after revocation” lacks a distributed ordering contract. | Define the operation-start boundary, revocation acknowledgement, partitions, and in-flight semantics. |
| Important | Resource aliases, policy versions, actual provider identity, and grant composition are underspecified. | Bind resolved resource identities and evaluator versions; reject unknown or unenforceable constraints. |
| Important | Narrower initial grants and partial approval conflict with requiring all authority at start. | Support explicit staged admission; never silently skip required work. |
| Important | Read access plus network/model invocation can disclose protected data. | Authorize data release to named destinations separately from reading it. |
| Important | A grant reference can become a transferable capability if its possession is sufficient. | Treat references as identifiers; authenticate callers and bind use to the execution context. |
| Opportunity | Approval UX is mostly a permission list. | Show concrete effects, exact targets, duration, spend, alternatives, and the smallest sufficient decision. |

The revised design retains the planning and execution boundaries while extracting reusable authority mechanics and durable authority state into Penghou.Hufu. See [architecture](architecture.md) and [ADR 0001](decisions/0001-hufu-owns-workflow-authority.md).

The selected policy-backend direction is [ADR 0002](decisions/0002-use-cedarsharp-for-policy-evaluation.md): official Cedar through the independent CedarSharp wrapper and a Penghou.Hufu.Cedar adapter. CedarSharp 1.0.0 is implemented, published, and qualified on its documented CI environments. The typed Hufu.Cedar mapping/evaluator prototype now exists; the local Windows x64 read/store/start composition [passes 95 tests](operation-start-qualification.md) on each runtime; production governed host integration remains pending. The read-authorizer prototype accepts a required evaluator interface; it supplies no default policy evaluator. The normative authority guarantees below remain Hufu/host/runtime obligations; adopting a policy engine does not implement them by itself.

## 1. Decision and scope

> Agents propose work and request authority. Hosts grant authority and approve governed effects. Runtimes and resource boundaries enforce it. Delegation, replay, and replanning never create authority.

Authority is scoped to a tenant and workflow run, narrowed for each activity and delegation, and checked against current host policy at use time. Human interaction is required only where host policy requires a new decision. Existing standing authorization can cover many revisions and operations.

Four questions remain distinct:

1. **Plan validity:** is the graph well formed and executable in principle?
2. **Capability authority:** may this execution perform this operation against this resource?
3. **Intent/effect authorization:** is this particular purpose or consequential change approved?
4. **Execution readiness:** are provider guarantees, data-release rules, budgets, dependencies, and recovery conditions satisfied?

A successful check in one category MUST NOT imply success in the others.

Non-goals include replacing host IAM, proving arbitrary program safety, using a model as a hard security boundary, promising exactly-once external effects, or specifying a particular host UI. This proposal does not expand existing project V1 release commitments. Existing security defects should still be corrected independently; richer workflow evolution remains staged future work.

## 2. Threat model and enforcement perimeter

Treat model output, planner rationale, tool arguments, retrieved documents, repository content, build scripts, provider messages, and generated artifacts as untrusted inputs. A prompt injection may propose a valid-looking operation inside an otherwise legitimate workflow.

The trusted computing base consists of host policy and identity services, the authoritative grant/decision store, trusted catalogue bindings, durable runtime fencing, resource brokers/adapters, and any isolation mechanism relied upon for enforcement. A compromised component in this base can invalidate the associated guarantee. This design does not claim protection from a compromised host administrator or kernel.

Every effect advertised as governed MUST cross an enforceable boundary. In
StrictEffects, untrusted agent input is typed data interpreted by trusted bounded
handlers; the agent cannot install handlers, execute scripts, or call arbitrary
host APIs through that surface. An arbitrary in-process activity with direct
host APIs cannot be confined by a context object. It must be trusted host code,
be rejected from strict execution, or use a separately admitted external provider.
Broader modes MUST state their actual powers and cannot claim strict containment.

Protected operations include reads, writes, model/provider submissions, network transmission, credential use, process creation, artifact access, and indirect requests to privileged services. Authorization management itself is protected control-plane work: grant, revoke, approve, activate, inspect protected history, and cancel require appropriately authenticated actors.

## 3. Core invariants

- **Requirements never grant rights.** Omitted authority means none, not inheritance of the host's credentials.
- **Complete mediation.** Dispatch checks supplement checks at the actual protected boundary.
- **Attenuation.** Activity authority is contained by run authority; delegated authority is contained by its parent context and host delegation policy.
- **Current validity.** Expiry, revocation, mandatory policy restrictions, and execution fences apply to every new protected operation.
- **Exact admission.** An admission binds the exact revision and execution-relevant contracts; a grant is not permission to activate arbitrary planner output.
- **No route around denial.** A prohibited effect stays prohibited when expressed through another tool, child, CI job, or workflow revision.
- **Recovery cannot amplify.** Missing or incompatible state cannot restore authority, erase consumption, or reinterpret old grants.
- **No silent downgrade.** Unsupported containment, isolation, revocation, budget, or provider guarantees cause a typed admission rejection.
- **Uncertainty is explicit.** Unknown authorization is not approval; unknown external completion is not a safe retry.
- **Denied operation means zero unauthorized resource I/O.** Trusted authorization lookups and audit writes may still occur. Earlier authorized operations in the same run need not have a zero count.

## 4. Typed requirements and trusted descriptors

Fuwen MUST represent protected-operation requirements in the immutable plan. The representation includes capability type/version, symbolic resource, scope, conditions, and any bounded dynamic binding. It MUST distinguish activity-use authority from authority eligible for delegation.

Trusted activity/tool descriptors define actual operation/effect classes, argument-to-resource mappings, required capabilities, and supported enforcement. The planner's declarations MUST cover these trusted requirements. Planner rationale and self-classification as “read-only” are not authoritative.

Static representation means a finite, reviewable upper bound or a declared late-binding rule. It does not require knowing every filename at compile time. A discovered file may be bound at runtime if the broker proves it is inside the admitted resource/scope. Unknown containment blocks the operation and returns a typed reason; it does not authorize a wildcard.

Compilation produces:

- Per-node requirements and delegation ceilings.
- A conservative maximum over reachable branches, including bounded repeats/fan-out.
- Explicit stage or conditional requirements, separate from the maximum.
- Diagnostics for broad access, incomplete bindings, unsupported capability versions, and enforcement requirements.

Only a proven unreachable branch may be omitted from the maximum. Repetition and fan-out also affect cumulative budgets and operation limits even when capability names do not change.

All execution-relevant requirement semantics participate in canonical plan identity. Catalogue, scope evaluator, tool implementation contract, and provider bindings are pinned by the admission receipt. Unknown required fields or versions MUST reject execution, rather than be silently ignored.

### Typed-effect vocabulary

Effect kinds and versions are a separate restriction from resource authority.
Both delegated effect sets and their resource scopes MUST be contained by their
parents. Trusted descriptors derive authority; agent-supplied risk/mapping claims
cannot weaken it. Neutral request/result types contain no workflow-engine
dependencies; adapters add runtime identity and fencing to invocation envelopes.

Effects contain no user-supplied executable predicates, unrestricted script
strings, loops, shell pipelines, or command substitutions. Luban's
[surface language](../../Penghou.Luban/docs/language-syntax-spec.md) lowers finite
typed dataflow and closed pure filters to versioned typed requests; this amends
the earlier blanket pipeline ban. Fuwen/Zhinu supply workflow control flow.
Admission pins semantic IR and trusted catalogue/schema/provider versions,
payloads, bounds, and host workspace bindings. Static scope ceilings never
authorize runtime-discovered descendants; every effect and concrete target
requires its own current check. The minimal Windows read-only parser/compiler,
canonical IR, static preflight and read executor are implemented. A narrow Hufu
current-snapshot read-authorizer prototype is also present; durable/governed Hufu
integration and broader syntax remain pending. Providers advertise
versions, limits, mode, platform, and reconciliation guarantees; discovery alone
never grants authority. Unknown operations return typed failures without fallback.

### Preview resolution and batch admission

Luban's current Local-only capture profile is implemented: it captures immutable
existing-file TextPatch payloads after all selected explicit-authorized-view
targets are admitted, retains no full-file bytes, and never exposes a writer.
`CaptureComplete` is not commit permission; `CanCommit` is always false. Hufu's
governed adapter and commit barrier are not implemented. Luban separately
provides standalone single-target and narrow exact-target batch executors whose
admission and recovery snapshots come from the host, not Hufu. See the [Luban
batch execution profile](../../Penghou.Luban/docs/batch-execution-profile.md).

The future [Luban preview/commit design](../../Penghou.Luban/docs/preview-resolution-commit-barrier.md),
accepted by [ADR 0007](decisions/0007-preview-resolution-commit-barrier.md),
separates static preflight from actual authorized bounded discovery. Known
downstream denial blocks before target reads. Resolution performs separately
authorized observations and captures proposed mutations; it is not effect-free
policy simulation. WhatIf MUST NOT invoke requested mutations or opaque/lazy tools.
A separate standalone Luban executor supports one exact-target patch, and a
separate batch executor supports complete ordered manifests of 1–64 distinct
exact-target patches, under the explicitly HostControlled Local NTFS namespace.
Batch admission and receipt inspection are host-supplied, not Hufu governance or
a durable store. The host must protect the root, drive, mount, and directory
namespace from untrusted actors; this is not general filesystem confinement.
Hufu governance, its batch-admission adapter, and durable recovery remain
pending. Batch execution is sequential and non-atomic, with no automatic retry
for uncertain or NoMutation outcomes. An unresolved earlier patch blocks all later nodes with `DependsOnUnresolvedEffect`
and zero later I/O. Deferred opaque tools use `DependsOnOpaqueEffect` for dependent
later nodes. Initial profile v1 uses ordered segments without virtual state.

Where the qualified filesystem profile resolves the complete mutation set,
Hufu MUST admit every proposed mutation before the executor releases its commit
barrier. Any known denial or required incomplete coverage blocks that batch
with zero proposed mutations. Exact IR/plan/segment identity, targets, payloads,
preconditions, dependencies, selection coverage, limits and provider versions
bind admission. Partial grants MUST NOT execute an allowed prefix. Changed or
smaller plans need explicit re-admission; frozen target manifests cannot silently
expand through fresh traversal at commit.

Unresolved effects retain explicit requirements and qualified provider/mode
constraints. Denial, unavailable authority, unsupported guarantees or incomplete
required discovery MUST NOT downgrade to lazy execution. State-changing opaque
effects require new resolution/admission of dependent later segments. Authorized-
view discovery is not proof of all-match coverage or absence of excluded paths.

The host/executor owns durable barrier/start coordination. Every actual access
still checks current authority, revision/fence, concrete resource and version
preconditions. The barrier is not an atomic transaction or a race solution;
post-start failure or revocation may leave partial outcomes requiring receipts
and reconciliation. Restart revalidates previews rather than treating them as
permits. No runtime adapter or barrier is implemented by this specification.

### Execution contract prerequisite

A usable authorization MUST bind structured execution requirements to the exact
operation, resource/input versions, authenticated subject, approval, and validity.
Every requirement needs a trusted, versioned enforcer or verifier and defined
evidence. Missing handlers, unsupported versions, conflicting constraints, or
unproven requirements block execution. Preconditions are verified before use;
continuous limits are enforced during use. Post-operation evidence MUST NOT
substitute for preventing unauthorized effects or disclosure. Redaction proof,
for example, binds the exact output that will be released.

The contract adds execution readiness to Cedar's decision; it does not change
Cedar semantics, grant rights, or bypass current revocation and fencing. See the
[runtime design](local-first-authority-runtime.md#execution-contracts).

## 5. Resource identity and containment

Resource references such as `workspace.main` are aliases. Admission resolves them to tenant-bound, stable identities and records a binding digest. A repository grant identifies the repository and relevant branch/workspace; a database grant identifies the environment and database; a publication grant identifies the registry, package, and destination policy.

Rebinding an alias or changing an authority-sensitive provider, credential purpose, or resource classification requires revalidation. A changed resource-content version is handled by operation preconditions where needed; it is not confused with resource identity.

Each capability family has a trusted, versioned evaluator:

```text
Covers(grant, concreteOperation, executionContext)
    -> Covered | NotCovered(reason) | Indeterminate(reason)
```

Indeterminate fails closed for the affected operation. Scope comparison MUST account for constraints, not just capability names or textual patterns. A request for multiple operations may be covered by multiple grants, but fields from different grants MUST NOT be combined into a synthetic broader grant. Each operation needs a complete valid authorization proof under the family's composition rules. Mandatory host denials override positive grants.

Filesystem enforcement MUST define case, separator, traversal, symlink/reparse-point, alternate-stream, and mount/alias handling on supported platforms. Validation must remain bound to the actual object used; a string-prefix check followed by an independently resolved open is insufficient. Where narrow path guarantees cannot be enforced, require stronger isolation or reject the path.

Network enforcement MUST identify the permitted service/destination and account for redirects, DNS changes, proxies, and private/metadata endpoints as applicable. `network.connect:api.github.com` alone does not define which repository, operation, or data disclosure is authorized.

## 6. Grants, envelopes, receipts, and identities

### Selected evaluator integration

Hufu.Cedar MUST map authenticated, tenant-scoped facts into an exact versioned
Cedar policy/schema/entity bundle. Policies and request data MUST pass the
required validation before use. Hufu MUST block affected operations on any
evaluation diagnostic error or bridge failure, even when Cedar returns Allow;
CedarSharp preserves the original decision and diagnostics without changing
upstream semantics. Record engine/language/features and mapping versions with
the admission identities below.

Each applicable parent/run/activity/delegation layer MUST independently constrain
the operation. Do not merge their permit sets in a way that allows one layer to
override another layer's absent authority. Mandatory prohibitions persist through
delegation. Grant-local exclusions and mandatory denials MUST remain distinct.
Use proven typed scope containment initially; symbolic analysis is optional and
unknown/unsupported results cannot authorize delegation.

### Authority records

Keep three distinct records:

| Record | Purpose |
| --- | --- |
| Authority grant | Host decision permitting an operation/resource/scope under explicit conditions. |
| Run authority envelope | Immutable versioned selection of grants and ceilings available to one tenant/run. |
| Admission receipt | Binding of an exact plan revision to one envelope version and the contracts needed to execute it. |

The envelope MUST NOT carry an independently mutable “current plan fingerprint.” The receipt binds plan to envelope; the runtime's fenced activation record identifies the active receipt.

A grant records issuer, tenant, beneficiary/run, capability and evaluator version, concrete resource binding, scope, validity interval, constraints, delegatability, provenance, and revocation identity. Host policy MUST verify that its issuer or approving actor has authority to grant the requested rights. A workflow MUST NOT choose an arbitrary credential principal to expand its permissions.

An admission binds at least tenant/run, plan revision/fingerprint, envelope ID/version/digest, catalogue and resource-binding digests, policy/evaluator versions, selected execution profile, approved intent/effect policy, and budget-account references. Runtime attempts additionally bind node identity/generation, fan-out item identity, attempt, operation, and execution fence.

Records MUST be authenticated through the trusted store or an integrity-protected message protocol. A hash alone is not issuer authentication. Grant IDs and envelope references are not bearer credentials: the broker MUST authenticate the invoking workload and validate its bound run, activity, audience, and lineage. The model receives neither reusable infrastructure credentials nor unrestricted host handles.

Across process or machine boundaries, any internal execution credential MUST be audience-restricted and bound to the authorized workload context; use proof of possession or an equivalent protected channel where necessary. Such credentials remain in trusted runtime infrastructure, outside model-visible context.

## 7. Admission and effective authority

The host supports two explicit modes:

- **Complete admission:** all declared reachable requirements must be covered before execution starts.
- **Staged admission:** the plan includes durable gates; admitted stages may execute while later stages wait for the missing authority or effect decision.

A narrower grant does not make an otherwise unsatisfied complete plan executable. It produces rejection or explicit staged admission. Required nodes cannot be silently omitted; an alternative branch must be part of a valid admitted plan.

At use time, an operation must satisfy the intersection of current valid run grants, the activity declaration, any delegation restrictions, and mandatory host policy. Intent/effect authorization, data release, provider enforcement, budget reservation, and the current execution fence are additional mandatory checks.

An admission receipt pins the meaning of prior approval, but cannot freeze out later revocation or mandatory policy restriction. Incompatible policy/evaluator changes require re-admission before further affected I/O. Existing history remains attributable to the original versions.

## 8. Process, tool, and provider execution

Consumer libraries SHOULD use small Hufu-independent capability interfaces with
ordinary local implementations. A governed host selects Hufu-bound brokers and
MUST reject missing governed registrations rather than fall back to unrestricted
implementations. Dependency injection does not prevent arbitrary code from
calling operating-system APIs directly.

StrictEffects is the planned default: registered bounded effects only, rejecting
arbitrary shell/process requests and adapters that execute project-controlled
code. A qualified Git inspection implementation may use a trusted native backend
only if configurable helpers, implicit network access, and other unadmitted
effects are prevented. Effect names alone do not determine execution risk.

ApprovedTools is explicit host opt-in to reviewed developer adapters with actual
native powers recorded. Hufu and Luban MUST NOT implement an OS/container
sandbox. UnrestrictedProcess is reserved for separate external-provider/host
integration, with explicit acceptance of actual guarantees and no automatic
fallback. Requested confinement that a provider cannot meet MUST be rejected.

Replace illustrative `process.execute:dotnet` grants with a trusted execution profile that defines the toolchain binding, permitted invocation shape, workspace mounts, scratch/output locations, child-process policy, network policy, credential exposure, and resource limits.

Running a build executes repository-controlled behavior. MSBuild's `Exec` task can invoke commands through the operating-system shell; therefore allowing `dotnet` is not a confinement guarantee. This design conclusion follows from the documented behavior in [Microsoft's MSBuild Exec reference](https://learn.microsoft.com/en-us/visualstudio/msbuild/exec-task?view=visualstudio).

For an external native-execution profile claiming confinement to workflow
authority, untrusted builds/programs MUST run without unapproved ambient
credentials, host home-directory access, unrestricted sockets, or access to the
authorization store. Descendants need the same or narrower isolation. Toolchain
and dependency-cache access, output locations, and restore/network use require
explicit profiles. Hosts may accept broader native powers only in a different
explicit mode; that mode MUST NOT claim envelope confinement. StrictEffects
requires no such provider because it rejects these workloads.

Remote providers MUST declare enforceable properties: workspace isolation, tool mediation, egress, credential handling, cancellation, operation reconciliation, and any budget guarantee relied upon. A provider's textual promise or model prompt is not enforcement. Unsupported required properties reject admission.

Where an external coding provider cannot enforce write scopes, a host MAY use a constrained input snapshot and receive a candidate patch. A trusted local adapter validates and applies only authorized changes. This is a distinct execution mode with separately authorized input disclosure; it does not claim the remote actor was locally sandboxed.

## 9. Data release, secrets, and indirect effects

Permission to read data does not automatically permit transmitting it to a model, remote agent, log sink, or artifact service. The host MUST authorize both access to the source and release to the concrete destination/provider under its data policy. Minimize submitted data and prohibit raw secrets in prompts, DSL, evidence, and ordinary artifacts.

Model profiles bind relevant provider/data-handling properties. Routing or fallback to a materially different destination requires equivalent proven authorization or re-admission; it cannot silently inherit the previous route's approval.

`secret.use` means a trusted broker may perform a bounded operation with a credential. It does not mean returning the credential bytes to an activity. Secret disclosure, if supported at all, is a separate high-risk capability.

Local hosts MAY resolve opaque credential references without a remote secret
service. References MUST be bound to current tenant/subject, audience, purpose,
and operation; possession alone never authorizes use. Keep raw secrets outside
the authority store, workflow state, prompts, and ordinary diagnostics. Identity,
credential, and authority expiry/renewal remain separate. HTTP redirects MUST
NOT forward credentials to an unapproved audience.

CI definitions, repository hooks, deployment manifests, IAM changes, and privileged service requests can exercise downstream authority. Trusted descriptors and host policy MUST classify these effects, and downstream systems MUST enforce their own authorization. A denial of infrastructure modification cannot be bypassed by changing CI to perform it. General semantic detection of every indirect effect is not promised; hosts must protect privileged trigger paths and avoid granting combinations they cannot contain.

Data already disclosed cannot be recalled by later revocation. Derived artifacts and recorded outputs therefore have independent access, retention, and onward-release policies. The MVP can use conservative whole-artifact classifications and allowlisted destinations; arbitrary program-wide information-flow analysis is not required.

## 10. Intent and consequential effects

The host MAY grant standing intent authorization for routine work, such as editing a named repository and running isolated tests. This enables unattended execution within the approved purpose.

For governed effects, the host MUST require either an applicable standing effect policy or an exact effect approval. Examples include production deployment, publication, destructive mutation, external communication, signing, and privilege changes. Hosts decide which categories are governed; the agent cannot downgrade the classification.

Prefer **prepare -> inspect -> commit** where possible. An approval can bind an immutable artifact digest, exact target and recipient, desired operation, expected resource version, limits, expiry, and idempotency identity. Changed effect inputs invalidate that approval. An unchanged capability set does not preserve consent for a different artifact, recipient, environment, or purpose.

This is not a mandatory prompt before every write. Standing authorization can cover a bounded class of effects. Capability grants and effect approvals remain distinct records, even when presented together in one user decision.

The initial local implementation SHOULD support temporary elevation within the
authenticated issuer's ceiling and explicit expiry. Multi-person approval and
separation-of-duties policies remain extensions; record authenticated actors and
exact decision versions so those policies can be introduced without ambiguous
historical attribution.

## 11. Revision admission, activation, and waiting

Guihua proposes revisions; Fuwen validates their representation; the host determines authority and effect decisions; Zhinu owns durable activation.

For every revision, compute two outputs:

- An explanatory change summary: added/removed requirements, changed effects, resources, providers, conditions, and budgets.
- A coverage decision against **currently valid** grants and host policy, including effect approval and enforcement readiness.

Requirement difference is not ordinary set subtraction and is not an executable authorization proof. A semantically unchanged authority set may still require a new effect decision. Every revision receives exact admission, but re-admission SHOULD be automatic when all existing authorizations still apply.

```text
propose -> validate -> evaluate current coverage and effect policy
                      |                         |
                   satisfied                 missing decision
                      |                         |
                 exact admission       durable request and wait
                      |                         |
                 fenced activation <--- revalidate after decision
```

Default cutover occurs at a durable safe checkpoint. Activation uses compare-and-swap over expected active revision, envelope version, request identity, and execution epoch. Old workers cannot start new protected operations under a superseded fence. In-flight operations are reconciled before conflicting replacement work starts; old results never become new-revision results merely because node names match.

Initially, suspend the affected execution at a checkpoint and keep the old revision authoritative until cutover. Independent old-revision branches may continue only when the host/runtime proves disjoint effects and dependency safety. This optimization is not required for the first release and must not mix nodes from unactivated plans.

Use existing wait/suspension primitives with typed reasons where possible. Distinguish missing authority, expiry, revocation, policy denial, unsupported enforcement, indeterminate authorization, budget exhaustion, and ambiguous external completion. Repeated automatic retries or repeated approval prompts are not an appropriate response to a durable denial.

## 12. Authority requests and approval processing

An authority request is immutable, idempotent, and bound to the tenant/run, base and proposed revision fingerprints, current envelope version, affected nodes, concrete requested authority, desired effects, evidence references, expiry, and a canonical request digest. Planner rationale is attributed untrusted explanation, separate from host-derived facts.

Use orthogonal state:

```text
Request:  Pending -> Decided | Withdrawn | Expired | Superseded
Decision: Granted | PartiallyGranted | Denied
Application: NotApplied | Applied | Stale | Failed
```

These are conceptual records, not a requirement to add multiple public enums. A grant decision does not imply activation. Current capability availability is derived from live grant status, not a permanent `Granted` label.

The host authenticates the approver, checks their decision authority, binds the decision to the request digest, and persists the decision before reporting success. Duplicate delivery is idempotent; stale or conflicting commands cannot modify newer workflow state. Grant issuance, receipt creation, and activation must use durable fenced transitions or an equivalent recoverable protocol. Cross-store distributed transactions are not assumed.

Partial approval issues only the approved subset. It does not activate uncovered mandatory work. The run can continue through previously admitted stages, wait, or use a newly admitted alternative. A changed request requires a new decision; a stale approval is retained as history and never silently applied to the latest plan.

Denial records its scope: this request, this resource/effect, or a host policy restriction. Guihua may propose genuinely compliant alternatives. It MUST NOT reinterpret a denial as an invitation to obtain the same prohibited effect through another route. Equivalent denied requests should be suppressed under a host-defined context/expiry rule; material changes can justify a new request.

## 13. Delegation

Qingniao MUST validate delegation before provider submission. Its context binds the parent run/activity/generation/item, envelope version, execution fence, selected provider/profile, restricted grants, budget reservation, and expiry. The provider is selected through host policy; Qingniao resolves and enforces that selection.

Delegation requires both `CanUse` and `CanDelegate`. Child rights MUST be contained by current parent rights, the child declaration, provider trust rules, and any depth/fan-out restrictions. Every broker request authenticates that context. Caller-supplied parent IDs are not sufficient proof.

Parent revocation, expiry, cancellation, or supersession blocks new descendant operations. Regranting the parent does not automatically revive a cancelled or stale child. Continuing or restarting delegated work requires explicit revalidation and a new valid binding when identities changed.

Handles, environment variables, mounts, MCP connections, artifacts, and callback endpoints are part of delegation review. Shared mutable workspaces need per-operation attribution or isolation; sharing a directory does not share authority. Fan-out children cannot borrow sibling credentials or reservations.

A child may report `AdditionalAuthorityRequired` with structured requirements and evidence. This is a proposal, never a grant. Supervisor wake-up hints cannot expand authority or budget. Resumption validates the actual new admission, rather than interpreting an “Approve” message as permission.

## 14. Budgets and quotas

Capabilities are reusable permissions; budgets and quotas are consumable allowances. Keep these concepts separate but evaluate them together before protected work.

For each finite shared budget dimension, maintain a durable host-owned account:

```text
settled usage + outstanding reservations <= admitted limit
```

Reservations MUST be atomic across siblings, parent execution, planning, repair, evaluation, and delegation where they share an allowance. A parent cannot spend capacity reserved for a child. Child allocations are carved out of the parent account, not copied counters. Per-activity limits further narrow the shared allowance.

Use stable operation IDs for reservation and settlement. Retries, forks, replans, and run restarts do not reset consumption; a genuinely new allowance needs a host decision. Unknown or potentially committed usage retains its conservative reservation until reconciliation. Cancellation does not prove a provider did not charge.

Strict cost admission requires a trusted maximum charge and enforceable provider contract. Without one, reject strict admission or expose a separately selected advisory mode; never silently relabel estimates as a hard cap. Include currency and pricing revision where applicable. Distinguish elapsed deadlines from summed compute duration so concurrent work is accounted for correctly.

Apply the same reservation principle to one-time effects and operation quotas. Reuse Fuwen's existing conservative aggregate-budget direction instead of inventing another ledger in Guihua or Qingniao.

## 15. Revocation and operation-start ordering

The normative guarantee is: **after revocation is acknowledged by the authoritative enforcement service, no operation ordered after it at that service may start using the revoked grant.**

The service MUST serialize the final grant/fence check with recording authorization for a specific operation start. A previously obtained dispatch approval or reusable cached permit is insufficient. An operation ordered before revocation is in flight even if its external completion occurs later; this boundary must be visible in evidence and operator documentation.

Immediate distributed revocation requires online enforcement at the final start boundary, or a protocol with equivalent ordering. Offline leases can provide only a stated bounded revocation delay. They are a separately advertised mode and cannot satisfy an immediate-revocation requirement. Under partition or unavailable authoritative state, affected new operations wait or fail closed.

Streams and long-running providers must define whether further chunks/actions are additional protected operations. Prevent further calls, cancel or terminate where supported, record cancellation uncertainty, and reconcile results. No revocation claim implies external rollback or retraction of already disclosed data.

Expiry uses trusted time and a defined skew policy. Grant renewal requires a new host-issued version and current validation; restart does not renew a grant. Old signed receipts remain historical evidence, not permission to ignore current revocation.

## 16. Durable operations and recovery

Protected operations have durable identities and evidence linking their exact inputs, admission, fence, grant proof, reservation, and provider operation ID. Record a durable execution intent before dispatch and record the outcome afterward through the existing runtime operation boundary.

Differentiate:

- Never submitted / known not started: retry may be admitted as new work.
- Completed with durable evidence: reuse the recorded result under its read policy; do not repeat the external operation.
- Possibly submitted / outcome unknown: reconcile through provider status or idempotency facilities; do not blindly replay a non-idempotent effect.

Idempotency keys MUST bind the same effect inputs; changed arguments cannot reuse a key to disguise a new action. Compensation is a separate potentially failing effect requiring authority. Reserve or preauthorize cleanup only through an explicit host policy; revocation must not create an undocumented cleanup bypass.

Effect mutation preconditions MUST remain bound to the object actually changed;
a separate hash check followed by a raceable write is insufficient. Define
overwrite, multi-target atomicity, and partial-outcome behavior explicitly, and
reject unsupported forms. Replay contracts MUST describe reconciliation rather
than relying on a Boolean ReplaySafe flag. Re-reading mutable state is a new
observation, not reproduction of an old result; exact historical results need
protected persisted content and authorized release.

Supervisor effects MUST bind an authenticated provider to the exact invocation,
request digest, subject, resource, preconditions, revision/fence, and attempt.
Persist pending requests; enforce current authority at provider start. Unknown,
stale, cancelled, or conflicting completions MUST NOT resume work. A matching
duplicate receives an idempotent acknowledgement; a late receipt may be retained
only for historical reconciliation. A structured response alone does not prove
the supervisor enforced the claimed resource boundary.

Crash tests cover request persistence, decision recording, grant issuance, receipt creation, activation, reservation, operation-start authorization, external submission, outcome persistence, revocation, and cancellation. Recovery either reconstructs a valid binding or waits for reconciliation. Historical schemas may remain inspectable even when they are no longer executable.

## 17. Audit and evidence

Security decisions and operation-start records MUST be durably attributable. Optional narrative export to Hongxian/Siming can use a durable outbox; an unavailable narrative sink need not stop work when the authoritative local record has committed. If mandatory execution evidence cannot be committed, new protected execution stops.

Record who requested and decided, exact tenant/run/revision/node/item/attempt, request digest, resource binding, grants and versions, policy/evaluator identity, effect approval, budget reservation, decision code, execution fence, provider operation ID, and outcome or uncertainty. Audit entries reference protected payloads rather than copying secrets or sensitive arguments into general logs.

Evidence integrity, read permissions, retention, and deletion policy belong to the host/store boundary. Models may consume authorized projections, but evidence cannot issue grants. Access to historical results and their export remains separately authorized after live-resource revocation.

### Foundational evidence and operational visibility

Decision/start evidence MUST include the exact execution requirements, handler
versions, lineage, input bindings, and authenticated broker receipt references.
Hufu owns authority evidence; the runtime retains dispatch/journal/recovery
ownership. Stable operation IDs link them idempotently. Persist mandatory start
evidence before dispatch. If a receipt cannot be persisted after a possible
effect, preserve/recover uncertainty and reconcile; missing evidence never proves
that repeating the effect is safe.

Instrument evaluation, broker execution, approval waits, revocation propagation
where measurable, and typed failures through OpenTelemetry-compatible tracing
and metrics. Hosts choose exporters; local use requires no collector. Telemetry
MUST remain separate from authoritative records and cannot provide authority.
Exporter failure does not block execution when mandatory evidence committed.
Redact diagnostics, bound metric cardinality, and authorize evidence access.
Signed checkpoints or external anchoring are deferred.

## 18. Host experience and useful autonomy

### Deterministic authority analysis

Provide structured reasons for decisions, sufficient-authority proposals within
eligible scope, and comparisons of authority states. Reconstruct lineage,
exclusions, trusted resource relationships, relevant versions, and failed
execution requirements from authorized evidence. Do not leak hidden resources
or confidential policies through explanations.

Counterfactual simulations MUST use an isolated temporary authority state and
MUST NOT install grants or execute protected effects. Bind the query domain,
snapshot/time, versions, and assumptions; disclose whether results are exact for
that domain or incomplete. Arbitrary future resources/policies cannot be assumed
fully enumerable. Replay of known requests is not containment proof. Unknown
analysis cannot authorize delegation. Historical explanation and present-time
authorization remain distinct; actual execution revalidates current authority.

Optional AI summaries are informational projections only. They cannot modify
deterministic results, approve work, or install policy; provider submission of
evidence is itself governed data release.

### Approval experience

Use one understandable initial approval for routine activity, then request only meaningful deltas. The host SHOULD support preflight, staged admission, standing bounded policies, expiry renewal notices, denial-aware alternatives, and an explanation of exactly why an operation is blocked.

Approval presentation MUST derive targets, scopes, and effects from trusted bindings. Separate these facts from planner-generated rationale. Show the smallest sufficient decision by default:

```text
Revision 8 needs one additional decision

Current authority: edit source; run isolated tests
Requested: create migration files; apply migration to development/orders
Effect: add nullable column customer_note; no production access
Scope: this run and the inspected migration digest; expires in 30 minutes
Spend: $1 additional allowance, $4 total run ceiling

Options:
  Approve these additions
  Allow migration files only; keep database application blocked
  Deny and seek a compatible implementation
  Cancel the run
```

The preview is valid only if the host can verify the migration effect from an exact prepared artifact or trusted adapter; otherwise display the uncertainty instead of asserting “no destructive changes.” Approval of file generation and approval of later application can occur in separate stages if the migration does not yet exist.

Offer broader standing authority as an explicit separate choice with its scope and expiry visible. Never preselect broad privilege escalation. Explain what continues, what waits, and whether external work is already in flight. Notifications are idempotent and bounded; do not repeatedly ask about unchanged denied work.

Automatic semantic reviewers may supply evidence inside a deterministic eligibility envelope. They MUST NOT decide access outside that envelope, override a mandatory denial, or turn a human-required action into an automatic one. Learning from past approvals may suggest a future policy but cannot install it.

## 19. Component ownership

| Concern | Owner |
| --- | --- |
| DSL requirement representation, summaries, canonical plan identity | Fuwen using Hufu contracts |
| Shared authority contracts, evaluation orchestration, attenuation, durable grants/requests/decisions/revocation | Hufu |
| Trusted Cedar mapping, exact evaluator versions, strict diagnostic handling | Hufu.Cedar adapter |
| General-purpose managed/native binding and upstream policy evaluation | CedarSharp + official Cedar |
| Typed effect requests/results, descriptors and bounded providers | Separate Penghou.Luban project; neutral contracts and provider conformance |
| Exact execution requirements and authority evidence | Hufu; trusted brokers enforce requirements and return receipts |
| Trusted capability/effect descriptors and containment policy | Host/catalogue through Hufu extension contracts |
| Authority-aware proposals, explanations, compliant alternatives | Guihua |
| Identity, grants, effect approvals, resources, credentials, data-release policy | Host |
| Durable authority request/decision records and authority admission bindings | Hufu authority store |
| Applying authority decisions to workflow activation and exact execution bindings | Zhinu through host/Hufu integration |
| Activity dispatch checks, fences, operation journal and recovery | Zhinu |
| Final resource checks and actual isolation/credential mediation | Trusted host adapters/brokers and execution environment |
| Delegation attenuation and provider-context binding | Qingniao plus provider adapter |
| Provider/model routing mechanics and invocation provenance | Existing host/Baize integration, under admitted constraints |
| Shared budget account and atomic reservation service | Host/runtime boundary; callers consume bounded allocations |
| Human approval and operator surfaces | Marang, Guyabano, or another host |
| Optional attributed evidence export and narrative | Hongxian/Siming adapters |

Penghou.Hufu owns the reusable authority domain and authority-store contract, rather than only sharing record types. Its core has no dependency on Fuwen, Zhinu, Guihua, Qingniao, host UI, or a concrete database. Workflow-specific bindings live in integration adapters. Fuwen does not become IAM; Qingniao does not become workflow admission; Guihua does not own execution state; Hufu does not own workflow scheduling or the budget ledger.

## 20. Delivery sequence and acceptance gates

### Stage A: secure execution foundation

Support one host, one trusted broker, and a small capability set. Implement typed requirements, trusted descriptor validation, exact admission, concrete resource binding, activity attenuation, actual boundary enforcement, identity/fencing, revocation/expiry, protected evidence, and crash-safe operation handling. Reuse existing budget and journal contracts.

Start with SQLite-backed lineage/evidence, deterministic decision explanations,
structured execution requirements, one local filesystem broker through neutral
interfaces, and optional operational telemetry. Test failed evidence writes and
real alias/link races. Deliver the StrictEffects find/search/read/patch scenario
without shell execution, then qualified Git inspection. Sandbox construction is
out of scope. Advertise only tested effect/provider profiles.

Only advertise the paths proven by conformance tests. Arbitrary process execution, remote delegates, fan-out, or strict cost can remain unsupported until their enforcement requirements pass. Do not release a “secure authority” feature whose protection is only a workflow-entry check.

### Stage B: durable evolution and operator experience

Add authority/effect diffs, durable requests, idempotent decisions, staged/partial admission, safe cutover, compliant denial-aware replanning, effect previews, and exact resumption across restart. Prefer checkpoint cutover before independent-branch continuation.

### Stage C: wider execution and autonomy

Enable additional provider profiles, nested delegation, fan-out and shared accounts, standing effect policies, provider routing, and optional evidence integration as each passes its guarantees. Advanced semantic review and learned policy suggestions follow; neither is required for the core architecture.

### Required conformance suite

| Boundary | Required proof |
| --- | --- |
| Representation | Requirement/condition changes affect identity; unknown required versions reject; omissions never yield ambient rights. |
| Resource enforcement | Wrong tenant, resource, path, dynamic argument, redirect, or stale binding yields zero unauthorized I/O. |
| Execution modes | StrictEffects rejects shell, script, arbitrary process and project-code requests. Separately selected external containment must prove its claimed limits; no owned sandbox milestone. |
| Control plane | Forged references, wrong caller/audience, stale approval, tampered request, and unauthorized approver are rejected. |
| Delegation | Child cannot exceed parent or borrow sibling rights; parent revocation and fence changes invalidate descendant use. |
| Effects | Same capability with a different destination/artifact cannot reuse exact effect approval; CI cannot bypass a denied effect. |
| Data | Allowed reads do not permit unauthorized model/provider/log disclosure. |
| Revision | Covered revisions re-admit automatically where policy permits; partial grants never activate uncovered work; cutover rejects old workers. |
| Budgets | Racing children cannot overspend; planning/repair consume the shared account; ambiguity retains reservations; restarts do not reset limits. |
| Recovery | Every crash point preserves or narrows authority; ambiguous non-idempotent effects reconcile rather than blindly repeat. |
| Revocation | A raced start and revoke have one documented order; post-acknowledgement starts deny; partitions cannot silently authorize offline work. |
| UX | Duplicate notifications/decisions are harmless; blocked reason, exact requested delta, approver, and resulting state are inspectable. |
| Requirements | Unsupported/conflicting handlers block before dispatch; continuous limits prevent excess effects; proof binds exact released data. |
| Evidence | Failed mandatory start writes prevent dispatch; receipt replay is idempotent; uncertain outcomes reconcile; exporter loss does not erase authority records. |
| Analysis | Simulations have no effects or grant mutations; scope/unknowns are explicit; historical replay cannot prove general containment or current permission. |
| Composition | Governed hosts reject missing brokers; ordinary local implementations never silently replace governed ones; process mediation is not labeled containment. |
| Typed effects | Versions/mappings are trusted; traversal and output are bounded; Git inspection cannot execute configured helpers or reveal excluded paths; unknown effects fail closed. |
| Supervisor | Exact authenticated completions bind to current invocation/fence; duplicates are idempotent; stale/cancelled results cannot revive work. |

Use deterministic model-based/state-machine tests for ordering and races, plus real sandbox/broker integration tests for advertised confinement. Mock zero-I/O assertions alone do not prove process isolation. Unsupported paths must have explicit rejection tests.

Publish each broker's tested platform/capability/requirement profile. Begin with
repository conformance tests and extract reusable third-party helpers when a
real adapter consumer needs them. Optional future Biscuit transport must follow
the restricted, Hufu-derived profile and revocation requirements in the
[runtime design](local-first-authority-runtime.md#deferred-portable-delegation);
it is not a selected local-core dependency.

End-to-end acceptance: admit a bounded source-edit/test workflow; delegate a subset; discover a migration need; prepare a revision; show an exact authority/effect request; apply a partial grant without running uncovered database work; approve the prepared migration; activate the exact revision; restart without widening or double-spending; revoke before a later operation; prove it is blocked and that the full chain is inspectable.

The initial LOP acceptance scenario is narrower: find/search/read source and
apply a hash-bound patch within src/**; deny outside writes and unregistered
shell requests; restart without duplicating a completed mutation; retain exact
effect evidence. The broader scenario above needs separately admitted test/tool
and database capabilities and is not a prerequisite for strict workspace effects.

## 21. Evidence and compatibility notes

This review checked the supplied draft and the following local architecture records. It did not independently rerun their implementation tests or establish package-release status:

- [Existing workflow-evolution proposal](../../Solo/artifacts/workflow-v2/evidence-driven-workflow-evolution-v2.md): deferred V2 scope, explicit activation, identity distinctions, and component ownership.
- [Fuwen aggregate-budget ADR](../../Penghou.Fuwen/docs/decisions/0011-aggregate-inference-budgets-reserve-unknown-usage.md): conservative reservations, unknown usage, and restart/concurrency semantics.
- [Fuwen inference hardening plan](../../Penghou.Fuwen/docs/inference-hardening-v2-prep-plan-2026-09-27.md): distinguishes current partial foundations from open resource-grant and provider guarantees.
- [Guihua boundary review](../../Penghou.Guihua/docs/architecture-boundary-review-2026-09-28.md): shared budget and durable transition boundaries; recommendations are not implementation claims here.
- [Qingniao architecture](../../Penghou.Qingniao/docs/architecture.md) and [adapter-authority ADR](../../Penghou.Qingniao/docs/decisions/0014-in-memory-execution-state-and-adapter-authority.md): bounded delegation and limits of in-memory proof infrastructure.
- [Zhinu README](../../Penghou.Zhinu/README.md): durable execution/fencing and at-least-once interrupted delegates; these do not imply exactly-once external effects.

External standards inform boundary choices without requiring OAuth inside Penghou:

- [RFC 9396](https://www.rfc-editor.org/rfc/rfc9396.html) provides a precedent for structured, fine-grained authorization requests and rejection of unknown or invalid authorization detail. This proposal adopts that design principle, not its wire format.
- [RFC 9700](https://www.rfc-editor.org/info/rfc9700/) recommends audience restriction and sender-constraining of access tokens. Those principles inform cross-boundary runtime credentials here; the workflow's references remain non-authorizing identifiers.

The unlinked “GitHub” markers in the supplied draft are replaced by inspectable references above. Before implementation, each repository should map these proposed contracts to its current source and conformance tests; no prior roadmap assertion should be treated as proof that the security boundary already exists.
