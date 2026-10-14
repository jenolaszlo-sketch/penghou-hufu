# Hufu authority and enforcement implementation plan

Status: Planned, 2026-10-12. This document turns the
[proposed work sequence](archive/authority-enforcement-sequence-2026-10-12.md)
into reviewable implementation slices. It refines the AE-1–AE-5 section of the
[roadmap](roadmap.md); [ADR 0014](decisions/0014-authority-delegation-enforcement.md)
and the [canonical authority model](authority-delegation-enforcement.md) govern
ownership and semantics. No runtime changes, qualification or publication are
claimed by this planning change.

## Baseline and scope

Source inspected: `c0ef75afda4ba2562e3252d4e54cd536e37f428d`, with a clean working
tree before this documentation change. The attachment refers to `02b070e`, an
earlier revision. Its reported green CI was not independently checked here and
does not establish contention behavior or qualification of the newer source.

Existing containment, lineage, exact Derive approval and idempotent publication
are implementation to preserve. The [source review](authority-model-review.md)
is a historical review against `3cca79a`; it identifies optional ancestor-check
composition and missing current-parent-policy
evaluation. Direct inspection confirms those gaps in
[CurrentAuthorityRequestAuthorizer](../src/Penghou.Hufu/AuthorityRequestAuthorizer.cs)
and [LineageAncestorLiveness](../src/Penghou.Hufu/AuthorityDerivation.cs).
Current containment, atomic child publication and exact issuance gating were
also checked in [core derivation](../src/Penghou.Hufu/AuthorityDerivation.cs),
[SQLite derivation](../src/Penghou.Hufu.Sqlite/SqliteAuthorityDerivation.cs) and
[bounded issuance](../src/Penghou.Hufu/BoundedAuthorityIssuanceAuthorizer.cs).
Other historical profile/CI findings remain reading context until the corresponding
implementation gate revalidates them against its own source and composition.
[SqliteAuthorityStore](../src/Penghou.Hufu.Sqlite/SqliteAuthorityStore.cs) still
sets driver timeout to one second and `PRAGMA busy_timeout=1000`; its public
options expose no busy timeout.

The newer [delegation visibility contract](../src/Penghou.Hufu/DelegationVisibility.cs)
and [SQLite store](../src/Penghou.Hufu.Sqlite/SqliteDelegationVisibilityStore.cs)
must remain intact. Observe/Cancel/Intervene authority is distinct from resource
authority; authorizing a control request does not prove a live execution handle
or fence. Historical observation may use its independently authorized visibility
binding after execution authority ends. It must not revive execution grants.

The [release profile](package-release-profile.md) records the seven published
preview.5 packages; local source version preview.6 is a candidate, not publication
evidence. Preserve neutral workflow ownership in
[ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md), the
[Zhinu plan](../../Penghou.Zhinu/docs/authority-extension-plan.md) and
[Penghou contract plan](../../Penghou/docs/workflow-abstractions-plan.md).
Core work introduces no Zhinu SQL/runtime, Biscuit, Gagamba or language dependency.
Standalone derived subjects use an authenticated host lifetime/binding without
requiring a workflow engine; workflow events are one optional source of facts.
Luban and Hufu.Ps remain parked. HOST-SERVICES-PRODUCT and legacy ZA-5B remain
separate obligations; this plan does not close them.

## Delivery order and dependencies

| Workstream | Start condition | Deliverable / exit gate |
| --- | --- | --- |
| AE-1: current derived authority | Ready now | Mandatory qualified derived-grant checks, authenticated lifecycle binding, current parent restrictions and non-revival evidence |
| HUFU-R1: contention and CI reconciliation | Ready as a bounded side track | Explicit bounded timeout semantics, real contention evidence, exact CI links and accurate CI/product status |
| AE-2: worker to host broker | AE-1 accepted; R1 settled before contention-sensitive qualification | One authenticated worker read path using an opaque reference, final resource checks, start/revocation order and controlled result release |
| AE-3: optional Biscuit comparison | AE-1 and AE-2 accepted | Same broker corpus with core-derived lineage; measured benefit and explicit adopt/defer decision |
| AE-4: restricted execution | AE-1 plus qualified Gagamba restricted-provider contract | Exact prepared manifest, immutable policy and single-use release/stop/recovery qualification |
| AE-5: one real gateway | AE-1 and AE-2 plus an actual consumer/provider | One A2A or MCP tool profile with exact operation mapping, credentials/retries and disclosure evidence |

AE-3 is optional and does not block AE-4 or AE-5. AE-4 waits on its provider;
it does not block a remote gateway that launches no untrusted local code.
AE-5 uses AE-4 when its actual tool executes such code and AE-3 only if the
comparison justifies that carrier. Do not turn this into a sequential requirement
to build every infrastructure layer before proving one operation.

Each row below should be an independent change with its own evidence. Authority
semantics and final provider ordering need primary review; bounded fixtures,
scaffolding and documentation can use Luna where available. No automatic package
publication, unrelated consumer migration or new package family is implied.

## AE-1: qualify current derived authority

### AE-1.0 — Freeze the use-time rule

Planned default: **evaluate the exact requested operation against the child's
fixed issued ceiling and every applicable current parent restriction**. Parent
version/sequence at derivation remains provenance, not a cached permission.
Parent expansion never expands the child. Parent narrowing or policy-only denial
can block use without rewriting descendants. A replacement parent context/grant
does not silently substitute for the bound ancestor.

Record this rule in an ADR amendment/profile before implementing. Pinning an
immutable parent version is a conservative alternative only if any changed
parent version invalidates child use until explicit re-admission; retaining an
old version while ignoring a current restriction is not an acceptable alternative.
Do not implement both strategies as loosely interchangeable defaults.

Keep [ADR 0013](decisions/0013-delegability-of-derived-authority.md): exact Derive
approval is an issuance check. Withdrawing delegability blocks new issuance,
not existing-child use by itself. Keep [ADR 0012](decisions/0012-derived-authority-lineage.md):
the same approved delegation/generation may reuse its idempotent child on retry,
with fresh use authorization. New generation/binding needs new explicit issuance.

Define which grant lineage contributes to a concrete Permit, the supported
depth, and deterministic work/lookup/evaluation limits. Start with the existing
qualified single-parent slice; do not silently add general nested delegation or
an authority DAG. Reject unsupported/missing/ambiguous lineage. A mixed
root/derived snapshot must not bypass ancestry or accidentally attribute a
root Permit to an unchecked child; broader contributor tracing needs its own proof.
The initial qualified route should reject ambiguous contributors instead of
silently treating every live ancestor as sufficient. If a conservative all-derived-
entries mode is retained for compatibility, label its possible false denials and
keep it outside the general contributor-qualified claim.
The concrete first case is the single derived child layer currently produced by
SQLite derivation, bound to one root parent; evaluate all applicable current
parent policy layers. Mixed contributors and deeper ancestry remain explicit
unsupported cases until their attribution/shape is qualified.

**Exit:** selected rule, typed denial/unavailable distinctions, supported lineage
shape and a table of policy/lifecycle transitions are documented; existing
approval, retry and root-only behavior remain explicit.

### AE-1.1 — Make derived consumption require the check

Change the qualified request-authorizer composition so a derived-grant Permit
cannot pass without the required lineage/current-parent checker. Preserve
existing public signatures where they can remain safe; do not preserve a
construction path that can silently claim derived-authority support while
omitting the required checker. A known derived grant reaching an incomplete
composition fails closed rather than silently claiming compatibility. Update public
API inventories and preview compatibility notes if a new interface/factory is
needed. The existing optional `IAncestorLiveness` implementation alone is not
the qualified current-policy checker.

Root-only compatibility requires trustworthy issuance/origin classification.
Removing a parent marker from externally supplied state must not turn a known
derived grant into a root grant or evade its stored lineage.

Resolve authenticated tenant/context-bound lineage and current parent snapshots.
Evaluate the same action/resource/payload under the applicable parent layers,
using an explicitly validated parent-context projection; do not copy a child
principal or child-context digest into a parent check without defining its meaning.
Parent permit cannot replace the child's independent permit. Keep containment,
ceilings and delegability distinct from operation-policy evaluation.

Reuse bounded evaluation/cancellation ownership from
[core admission and issuance](core-admission-and-issuance.md). Bound total ancestor
work, including asynchronous lookups, and detect cycles/corrupt or cross-tenant
links without treating legitimate shared references as proof of a cycle.

Evidence binds the child decision, exact operation, contributor/ancestor identities,
observed versions/sequences, evaluator/profile versions and original evaluated
time. Capture evaluation once; do not re-evaluate later just to manufacture
evidence. A failed mandatory record cannot return a usable Permit. Keep protected
context out of the closed telemetry queue and default explanation projection.

**Exit:** regression tests show a still-live parent with unchanged grant identity
and timestamps can deny a child through current policy alone; missing composition,
missing state or malformed lineage cannot yield an authorized request.

**Status 2026-10-12: AE-1.0 and AE-1.1 landed.** The selected rule is the
current-policy rule recorded in the [ADR 0014 amendment](decisions/0014-authority-delegation-enforcement.md#amendment-2026-10-12-ae-10-current-derived-authority-use-time-rule).
The chosen safe composition is `DerivedAuthorityRequestAuthorizer.Create`,
which requires `AuthorityDerivation.LineageCurrentParentPolicy`; an incomplete
composition denies a known derived grant with `authority.derived-checker-missing`.
Existing shipped constructor signatures are unchanged; the unshipped overload
gained an optional checker parameter because API back-compat rules forbid a
second overload with optional parameters. Qualified lineage shape is exactly
one direct root parent; unsupported shapes fail closed. Local results are in the
[profile qualification](authority-profile-qualification.md). AE-1.3
is deliberately not started.

### AE-1.2 — Bind trusted lifecycle facts to authority transitions

Use a host-side translator over the existing neutral workflow seam. A workflow
event is an input to authenticated policy, not an automatic permission change.
Bind source, tenant/subject, run/activity, delegation, generation, revision/fence,
event identity/order and expected authority sequence. Reject forged labels,
stale/out-of-order events, mismatched tenants and conflicting duplicate intent.
Apply transitions durably and idempotently; acknowledge only after required
authority state/evidence commits. Recovery replays the same intent, not new rights.

Recommended initial host policy:

| Event | Execution-authority result |
| --- | --- |
| Suspend/pause | Quiesce new uses under a distinct live lifecycle gate; do not misuse terminal store revocation if same-generation resume is intended |
| Resume | Authenticate the current binding and revalidate the existing fixed child; never increase rights or extend expiry automatically |
| Retry, same generation | Reuse exact derivation identity where legitimate; reauthorize each protected operation |
| Cancel/complete | Durably invalidate execution use for that binding; completion does not erase independently authorized historical visibility |
| New generation / superseded fence | Retire the old execution binding; new generation requires explicit new admitted authority |
| Duplicate event | Return the original transition receipt only; no widening or double transition |

Choose the smallest neutral contract extension only if an actual fact cannot be
carried safely today. Such a change returns to Penghou ownership/qualification;
never query workflow SQL from Hufu core or make the optional adapter depend on Zhinu.
Do not implement a second workflow event store or import a consumer identity as
authentication. Cancellation/Intervene additionally require the current runtime
handle/fence; visibility authorization alone cannot execute either action.

**Exit:** real trusted-adapter fixtures prove suspend/resume, cancel/complete,
duplicate events, stale fences and new generations, including process/store reopen.

**Status 2026-10-12: AE-1.2 landed.** Lifecycle state is generic Hufu authority
state: `Penghou.Hufu` owns `IExecutionLifecycleStore`/`IExecutionLifecycleGate`
and the binding/state/transition/receipt types; `Penghou.Hufu.Sqlite` owns the
durable atomic/idempotent implementation; `Penghou.Hufu.Workflow` owns
`WorkflowAuthorityEvent`, `IWorkflowAuthorityEventTrustSource` and the translator.
The gate is mandatory only in the qualified derived-authority composition and
never applies to root-only profiles. Suspend/Resume and terminal
Cancel/Complete/Supersede are implemented; no new-generation authority is
created and no workflow contract changed. See the
[lifecycle profile](execution-lifecycle-authority.md) and
[profile qualification](authority-profile-qualification.md).

### AE-1.3 — Qualify freshness and consumption

Specify the child/ancestor/lifecycle version read set used by a decision.
After asynchronous evaluation and mandatory recording, revalidate relevant
heads, validity and lifecycle binding; expiry or version movement fails closed.
Where a consistent store view is required, qualify it explicitly instead of
combining unrelated reads. A preflight authorizer is still not an atomic I/O
start gate: AE-2/providers must order actual start with current truth.

| Required case | Expected outcome |
| --- | --- |
| Scope/action/exclusion or policy-only parent narrowing | Affected child operation blocked despite live parent dates |
| Parent expansion | Child remains inside its originally issued ceiling |
| Only Derive/delegability approval withdrawn; resource policy remains live | New issuance blocked; existing child still requires its own Permit and live parent resource restrictions, with no retroactive revocation from the approval withdrawal alone |
| Parent expiry/revocation, child expiry | No new authorized use |
| Missing/unavailable lineage, replaced parent identity/context, unsupported depth | Fail closed; no historical/equivalent-grant fallback |
| Shared ancestry versus a real cycle | Shared root references are not cycles; unsupported deeper sharing is reported as unsupported, never falsely qualified as a cycle or a permitted DAG |
| Parent changes during lookup/evaluation/recording | No Permit attributed to a superseded captured read set |
| Same-generation exact retry | Original child identity can be reused; current denial still applies |
| Cancel/complete/new generation, including restart | Old execution authority cannot revive through replay or regrant |
| Root-only request, unrelated visibility observation | Existing qualified root/disclosure behavior preserved |
| Evidence/capacity/cancellation failure | No dispatch-capable authorization; owned work/capacity is accounted correctly |

Run core/Cedar/SQLite derivation and request-authorizer tests on .NET 8/10, then
the actual neutral workflow and IO consumers using the qualified composition.
Include an actual Cedar policy-layer regression, not only permissive mocks.
Separate final-resource/start guarantees from preflight evidence. Update profiles,
qualification records, packaged consumers and compatibility notes before claiming
general derived-authority support.

**AE-1 complete:** one qualified composition requires ancestry, enforces the selected
current-parent rule and trusted lifecycle policy, preserves fixed ceilings and
retry semantics, and has evidence for the complete case table.

**Status 2026-10-12: AE-1.3 landed.** The qualified composition uses optimistic
capture-and-revalidation, not a combined transactional read set. It captures the
child snapshot identity/version, the bound parent identity and snapshot
identity/version, the lifecycle binding/state/sequence, the evaluator identity
and the evaluated instant; after mandatory evidence recording it re-reads every
contributing head and fails closed with typed `authority.read-set-superseded`
(`Unavailable`, not `Deny`) on any movement. Lifecycle reads are versioned
(`ExecutionLifecycleCheckResult`, `ExecutionLifecycleStateRead`); the sequence is
a freshness token only. The final revalidation is the last awaited authority
operation before a Permit is returned, so this is preflight freshness, not
atomic provider-start ordering. See the
[freshness rule](current-authority-profile.md#derived-authority-freshness-ae-13)
and [qualification](authority-profile-qualification.md).

## HUFU-R1: contention and HOST-SERVICES-CI closure

1. **R1.0 — Reconcile evidence.** Inspect the actual CI runs for `02b070e` and the
   current candidate, job conclusions, source SHA and Windows full/separate-process
   coverage. A green run is one observation; it does not erase the recorded loaded-
   runner contention risk. Preserve historical failures and distinguish timing
   fixture changes from production semantics. Do not mark PRODUCT complete.
2. **R1.1 — Specify bounded timeout policy.** Recommended compatible starting
    point: add a validated store `BusyTimeout` option, preserve the one-second
    default, and use an explicitly configured longer budget for hosts/tests that
    need it. Start with whole-second values from 1 to 30 seconds, keeping driver
    `DefaultTimeout` and SQLite millisecond busy timeout consistent on every owned
    connection. The exact 1–30 second whole-second range is an
    implementation/qualification choice for the current SQLite store, not a
    Hufu authority invariant. No zero/infinite wait or silent repeated store call. A new production
    default needs its own behavior/release evidence. Do not mutate the legacy shared
    owner profile or weaken FULL durability to make tests pass.
3. **R1.2 — Prove outcomes.** Exercise four independent writers issuing the same
   derivation with an admitted longer budget: exactly one child/lineage publication,
   exact idempotent receipts and no duplicate grant. Also hold a writer lock past
   the selected budget and require typed Unavailable with complete rollback,
   followed by legitimate explicit retry with the same identity. Cover cancellation,
   independent processes, both runtimes and supported CI platforms. Time bounds
   are qualified with scheduler tolerance; native busy handling is not hard preemption.
4. **R1.3 — Close only evidenced gates.** Save exact run/job links and source/options/
   platform configuration; update roadmap, completion review and host handoff.
   Close HOST-SERVICES-CI only for the proved profile, retain PRODUCT routing/
   retention/deployment work, and keep genuine timeout Unavailable distinct from
   policy denial. Update public API inventories/package compatibility if applicable.

**R1 complete:** reproducible timeout/rollback/idempotency behavior and remote
evidence agree with the selected policy and all status documents. A caller may
retry an unavailable store explicitly within its own bounded deadline; the store
must not conceal retry or manufacture permission from cached state.

## AE-2: one worker to one host broker

**Spec 2026-10-12: scope frozen before implementation.** The AE-2
[broker authority profile](broker-authority-profile.md) records the frozen
shape: a new experimental, unpublished `Penghou.Hufu.Broker` project; a local
Windows named-pipe transport with host-issued worker-session bootstrap; an
opaque session-bound reference (not a signed token); a closed bounded DTO;
a concrete broker-local start ordering (not a general public interface);
AE-1 qualified authorization composed through `Penghou.Hufu.IO`; and distinct
start vs result-release evidence. No runtime change, qualification or
publication is claimed by the spec.

**Status 2026-10-12: AE-2.0/2.1 contract slice landed.** `Penghou.Hufu.Broker`
provides protocol v1 bounds, host-issued single-use bootstrap and
`BrokerSessionRegistry` authentication, the opaque session-bound
`BrokerAuthorityReferenceStore`, the closed `BrokerReadFileRequest` DTO, and
`BrokerReadRequestMapper` resolving a request into the canonical
`AuthorityRequest` under the authenticated session. Transport-neutral and
I/O-free (22 focused tests). Deferred to AE-2.2: named-pipe framing, broker-local
start ordering execution, provider read composed with the AE-1 qualified
authorization through Hufu.IO, and start/release evidence recording.

### AE-2.0 — Freeze the smallest useful profile

Planned default: a local Windows broker over one protected named-pipe transport,
two independently authenticated test workers, one host-registered workspace and
one exact bounded read (suggested maximum 64 KiB). The first qualification
profile uses a local Windows named-pipe transport. Broker authority semantics
remain transport-neutral; named pipes are an implementation/qualification
choice, not a Hufu architectural dependency. Use existing neutral Hufu/IO
contracts and an opaque authority reference first. No general RPC framework,
remote placement, mutation API, command frontend or sandbox dependency.

Freeze transport authentication, protocol version, message/concurrency/deadline
bounds and read/release semantics before code. Pipe ACL/OS user identity alone
does not distinguish two workers sharing that account: bind a host-issued worker
session through protected bootstrap and connection authentication. Claimed worker
IDs, PID labels and opaque references alone authenticate nothing. Keep bootstrap
material out of model-visible inputs, outputs, logs and telemetry.

If this transport cannot meet the selected host's threat model, choose one
protected alternative and record it before implementing; do not ship multiple
untested transports or fall back to anonymous connections. This first proof is
a cooperating-worker protocol, not hostile same-account bypass closure.

### AE-2.1 — Bind reference, request and recipient

Resolve the opaque reference only under the authenticated connection/session.
Bind tenant, child subject, broker audience, exact core-derived grant/delegation,
generation/fence, workspace/path, action, payload/limits and stable operation
identity. Caller input cannot choose a root, identity service, evaluator, credential
or wider parent context. Revoked/expired/unknown references and mismatched recipient
fail closed without disclosing protected state. Visibility references cannot
substitute for resource grants or vice versa.

Parse a closed bounded DTO and produce the exact canonical resource request.
Every retry requires fresh current authority. Define duplicate behavior explicitly:
changed intent with a reused operation identity conflicts; a read may be repeated
only as an explicitly reauthorized attempt, never through an old Permit. Cached
output replay requires its own current disclosure decision and cannot claim a
fresh resource observation. No successful response survives failed evidence.

### AE-2.2 — Enforce at provider start and result release

**Spec 2026-10-12: [AE-2.2 implementation spec](broker-ae22-implementation-spec.md).**
Frozen invariants and the ordered start/release sequence: one identity model over
the named pipe; a concrete broker-local arbiter keyed by the execution/authority
binding; AE-1 authorization inside the ordered boundary followed by mandatory
start evidence before admission; participating lifecycle/revocation mutations on
the same key; the real `HufuWorkspaceAccess`/`IWorkspaceReader` path; no lock
across file I/O; independent release authorization; separate start/release
evidence; response loss grants nothing; coarse failure disclosure; no hostile
containment claim. AE-2.2 implements the vertical; AE-2.3 exercises the race/
restart/response-loss corpus and closes AE-2. No code is claimed by this spec.

**Status 2026-10-12: AE-2.2 vertical landed.** `Penghou.Hufu.Broker` now
implements the concrete broker-local start arbiter, start/release evidence,
the `BrokerReadHandler` enforcement vertical over the real
`HufuWorkspaceAccess`/`IWorkspaceReader` path with the AE-1 qualified
authorization, and the protocol-v1 named-pipe server/client. Locally validated
with the real `Penghou.IO.Local` reader (35 focused tests, net8/net10). AE-2.3
(cross-process race/restart/response-loss corpus) is not started.

Compose AE-1's qualified current checker through Hufu.IO and the actual reader,
including required ancestry metadata, exact-target content and result disclosure.
There is no search or directory-discovery endpoint in this first proof. If the
reader internally probes or enumerates any ancestry/candidates, preserve those
individual checks without treating them as a qualified general listing API.
An outer RPC authorization
alone is insufficient. Preserve actual provider canonicalization, object binding,
platform and alias limitations; source cannot select a weaker profile.

Choose and implement one authoritative ordering for authority/lifecycle changes
and new broker starts. Recommended guarantee: revocation ordered first blocks a
new start; an already-recorded start may finish its admitted read internally.
Define independent result-release checks when authority changes after reading.
A snapshot lookup followed by I/O does not establish this order. If no suitable
arbiter exists, implement/qualify the narrow host arbiter; do not reuse activity
preflight as an atomic boundary or copy legacy workflow SQL into core.

Mandatory decisions/start evidence commit before governed dispatch; result/error
evidence binds the recipient and exact request without automatic content logging.
Recheck relevant state after asynchronous work. Partitions/unavailable current
authority or recorder block new starts and unauthorized disclosure. Define how
lost response, connection loss and process restart are reconciled without granting
rights or treating a read receipt as a future mutation receipt.

### AE-2.3 — Qualify the real consumption boundary

**Spec 2026-10-12: [AE-2.3 qualification spec](broker-ae23-qualification-spec.md).**
Cross-process/race/restart/lost-response/saturation/malformed-traffic corpus
that closes AE-2.

**Status 2026-10-12: AE-2.3 corpus landed; AE-2 complete for the cooperative-worker profile.**
The corpus (with a private cross-process worker harness) covers separate-process
reads, wrong-worker reference, revoked reference, unknown/reused bootstrap,
restart invalidation, fresh-evidence retry, mutation-first ordering, and
malformed/oversized traffic. One narrow server error-mapping defect (malformed
read frame now returns typed `InvalidRequest`) was fixed separately. 49 focused
broker tests pass on .NET 8/10. Hostile-worker containment remains AE-4.

Use separate worker/broker processes and a real qualified resource provider.
Prove A cannot use B's reference, a child cannot borrow parent-only scope/actions,
wrong tenant/audience/generation fails, stale/replayed bindings do not revive,
denial reaches provider content access, and denied candidate metadata/path/content
is not returned. Inspect provider/evidence traces rather than only RPC statuses.

Inject revocation before start, during required evidence, after read and before
release; expired sessions/grants; unavailable lineage/store/evidence; broker restart;
response loss; malformed/oversized messages; and capacity saturation. Validate
bounded buffers/deadlines and closed telemetry labels. Publish an example showing
the exact threat model and limits, not a general production broker claim.

**AE-2 complete:** one reproducible authenticated worker read through the broker
and real provider enforces current child/parent authority, start order, required
evidence and recipient disclosure. Hostile-worker containment remains an AE-4
provider/channel gate.

## AE-3: compare Biscuit on the same broker path

**Spec 2026-10-12: [Biscuit comparison spec](biscuit-comparison-spec.md).**
AE-2 now provides the control path. AE-3 is a comparison, not a feature: it
answers, with evidence on the identical AE-2 corpus, whether Biscuit's
portability/attenuation benefit justifies its complexity over the opaque
session-bound reference. A defer/remove decision is a valid outcome.
Specification only; no code or publication claimed. The bounded adapter
correction needed before a fair comparison is specified in the
[Biscuit reconciliation spec](biscuit-reconciliation-spec.md): one containment
definition, one lineage model, one current-authority (AE-1) path.

**Status 2026-10-12: AE-3B reconciliation landed.** `BiscuitProfile.IsSubset`
delegates to core `AuthorityDerivation.IsContainedBy`; the adapter adds
credential-only `VerifyCredentialAsync` and `BiscuitQualifiedRequestAuthorizer`,
which routes the protected operation through the AE-1 qualified authorizer so a
signature never authorizes on its own. Additive (existing behavior unchanged);
97 Biscuit tests pass on .NET 8/10. AE-3C (carrier comparison and
adopt/defer/remove decision) is not started.

1. Reconcile the experimental adapter's subset routine and registry ancestry with
   core `AuthorityDerivation`; preserve one definition of containment and exact
   Derive approval. Freeze versioned mappings for child grant, worker, audience,
   generation and operation. Token attenuation must not invent a new subject.
2. Keep the same AE-2 operation/provider/transport corpus. Add bounded signature/
   block/policy verification, registered descendants, online current checks and
   mandatory evidence. Test stolen/wrong-audience bindings, unregistered attenuation,
   expiry/revoked ancestry, unknown claims/mappings and unavailable dependencies.
3. Compare portability and actual attenuation use against the opaque route, plus
   payload size, verification cost, host state, operational/key burden and disclosure
   risk. Both paths must pass the same authority tests; signatures cannot weaken them.
4. Record adopt/defer/remove as an explicit decision. Keep the opaque route usable.
   No offline validity, arbitrary audience exchange, onward delegation, mutation
   recovery or package publication is implied by a successful read experiment.

**AE-3 complete:** a reproducible comparison justifies a decision, including a
valid decision to retain opaque references and stop Biscuit work.

## AE-4: qualified Gagamba restricted execution

**External prerequisite:** accepted provider contract and actual qualification
under [Gagamba's Linux-first restricted execution ADR](../../Gagamba/docs/adr/0003-ar7-linux-first-restricted-execution.md).
Published lifecycle execution cannot substitute for that prerequisite. This plan
does not start or redesign Gagamba implementation.

After that gate, implement one neutral authority-to-provider mapping with an
exact subset and execution binding. Authorize bounded preparation separately,
then the actual immutable effective manifest, then single-use release. Pin concrete
resources, placement/provider versions, digest, principal/delegation/generation,
limits, permitted channels and operation identity. Provider preparation is fact
generation, not approval. One activity supports 0..N independently bound executions.

Qualify release versus revocation/cancellation, duplicate releases, lost acknowledgements,
stop acknowledgement/failure, process/host restart and uncertain completion.
Changed authority does not mutate an existing OS policy: narrower/wider changed
execution needs a freshly prepared/admitted instance where required. Prove no
late launch, no reuse of retired generations and separately authorized output.
Keep historical visibility and execution receipts separately protected.

A broker-enabled restricted profile must explicitly prepare and qualify its channel
and bypass closure. Do not open networking or relax Gagamba's offline profile merely
to reuse AE-2. Update SandboxExecutionHost/adapters only to guarantees the provider
actually qualified; retain frozen lifecycle/legacy profiles and their compatibility.

**AE-4 complete:** one real prepared restricted execution passes manifest/release/
revocation/stop/recovery/output gates. Unsupported restrictions fail before launch.

## AE-5: one real A2A or MCP gateway

**Spec 2026-10-12: [AE-5A Marang MCP acceptance spec](marang-mcp-acceptance-spec.md).**
First slice uses Marang's real MCP boundary (`marang_delegate`). Acceptance
requires current Hufu authority via a new narrow
`DelegationAcceptanceOperation.Accept` (not `Derive`, not workspace
containment, no resource rights by implication); the Biscuit credential enters
via HTTP metadata and the gate runs at the top of `AcceptAsync` before durable
acceptance state. Specification only; no code.

**Status 2026-10-12: AE-5A core semantic landed.** `Penghou.Hufu` now defines
`DelegationAcceptanceOperation.Accept`, the bounded acceptance request,
evaluator/recorder seams, and `CurrentDelegationAcceptanceAuthorizer` (current
snapshot + optional lifecycle gate + mandatory evidence + freshness). Additive;
resource action/Cedar/Biscuit vocabularies unchanged; 7 focused tests on
.NET 8/10. Marang wiring and acceptance policy remain the next step.

**AE-5A.1b complete (2026-10-14).** The Hufu nine-package `0.1.0-preview.8`
release is published, including the two Biscuit packages (dependency
`BiscuitSharp 0.1.0-preview.2`), and verified from a fresh cache with zero
project references. **AE-5A.2 (Marang wiring) is unblocked** and is the next
workstream; it consumes the three packages from NuGet with no local feed and no
source reference.

Select one provider, one real tool schema/version and one bounded action based
on an actual consumer. Keep Qingniao/Marang protocol ownership; Hufu supplies
neutral authority rather than another A2A/MCP SDK. Reuse AE-2's authentication,
audience/reference mapping, revocation ordering, evidence and disclosure rules.

List every provider-internal action/resource/destination, delegated credential,
redirect/subtool behavior, limit and retry/idempotency guarantee. Map the exact
approved operation into controls the provider can enforce. Unknown or unsupported
restrictions reject; hiding a tool or denying shell in a request does not prove
that the downstream agent cannot use it. Hufu-unaware services receive authorized
requests or genuinely scoped downstream credentials, not a fabricated Hufu-aware
guarantee. Credential possession must not become unbound ambient authority.

Test the real provider route, token/reference theft, changed schema/arguments,
unexpected destinations/subtools, cancellation, revocation, retry/response loss
and result disclosure. Reads and mutations need distinct recovery claims; one
bounded read does not qualify mutation replay. Use AE-3 only after its value decision
and AE-4 if this action executes untrusted local code.

**AE-5 complete:** one actual consumer/tool path has exact documented controls,
provider evidence and refusal behavior. General gateways and organizational
credentials remain separate profiles.

## Evidence, review and handoff

For each implementation change record source SHA, dependency/package versions,
exact composition/options, test commands, framework/platform, counts/skips,
failure-injection/race outcomes and remaining limits. Remote CI records include
actual run/job URLs and the SHA qualified; package evidence is a separate step.
Update the affected profiles, roadmap, completion review and handoff together.
No historical result count or archived ADR status becomes current merely by reuse.

Run focused tests for each slice, then relevant core/provider/consumer suites once
the slice stabilizes. Use the existing CI graphs and platform qualifications;
do not run Windows-only IO evidence as though it qualified a portable profile.
Preserve current APIs, mandatory evidence, capacity/cancellation ownership,
visibility controls and independently usable cores. Release only after compatibility
and exact packaged consumers pass the affected boundary's gate.

## First implementation assignment

Start **AE-1.0 plus AE-1.1**, with this narrow outcome: a child read previously
allowed under a live parent is denied after a policy-only parent restriction,
while parent expansion does not widen the child. Add missing-checker and unavailable-
lineage cases, preserve root-only behavior and exact Derive approval, and capture
the selected current-policy rule/evidence once. Then proceed to lifecycle binding
and the AE-1 qualification table. HUFU-R1 can be assigned separately against
the shared store without overlapping those semantic edits.

Planning-input archive SHA-256:
`e786ec46c2106d1face68f3598ded04ca11b0d2fea0f9849420ab356f4c5e8a8`.
