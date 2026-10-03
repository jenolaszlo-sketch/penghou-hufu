# Original workflow authority proposal

Historical input, preserved for provenance. Superseded by [the Hufu specification](../workflow-authority-spec.md). The text below is the supplied proposal; it does not describe implemented behavior.

---

Workflow Authority, Delegation and Re-Admission
Status: Proposed cross-project architecture
Applies to: Penghou.Fuwen, Penghou.Zhinu, Penghou.Guihua, Penghou.Qingniao
Host consumers: Marang, Guyabano and other applications
Primary concern: Least-privilege authority for agent-authored, durable and evolvable workflows
1. Purpose
Penghou workflows may execute for long periods without an interactive user interface. Human approval therefore cannot be required for every command, tool call or individual side effect.
Instead, authority is established primarily when the workflow is designed and admitted.
The planning agent MUST identify the authority required by each activity. Fuwen MUST represent those requirements as part of the executable workflow. The host MUST decide which authority is actually granted to the workflow. Zhinu MUST enforce that authority at execution boundaries.
Delegated work MUST NOT acquire authority that the parent workflow does not possess.
If execution evidence causes Guihua to redesign the workflow and the new revision requires additional authority, the revised workflow MUST NOT use that authority until it has been explicitly granted. The agent may request the additional authority on behalf of the workflow.
The core rule is:
Agents may request authority. Hosts grant authority. Runtimes enforce authority. Delegation never creates authority.

2. Goals
The design MUST provide:
1. Authority declared before execution
   - Each activity identifies the capabilities and resources it expects to require.
   - The complete workflow can therefore expose its expected authority before admission.
2. Workflow-scoped grants
   - Authority is granted to a workflow run, not directly to an LLM or agent process.
   - The agent does not receive a transferable permission token.
3. Per-activity enforcement
   - An admitted workflow does not imply that every activity receives every granted capability.
   - Zhinu checks the activity's declared requirements against the workflow authority envelope before execution.
4. Attenuating delegation
   - Qingniao may delegate only authority already available to the invoking workflow/activity.
5. Safe workflow evolution
   - Guihua may propose any workflow revision.
   - It may not silently expand the workflow's effective authority.
6. Explicit authority requests
   - A revision requiring new rights produces a durable authority request.
   - The workflow waits before performing affected work.
7. Crash-safe authorization
   - Restart and replay MUST NOT lose, widen or reinterpret authority.
8. Revocation
   - A revoked grant prevents future authorized I/O even if an older workflow revision was previously admitted.
9. Auditability
   - The system can explain:
     - what authority was requested;
     - what was granted;
     - why an activity required it;
     - when it was exercised;
     - which revision introduced it;
     - who or what approved it.
10. Host independence
    - Fuwen, Zhinu, Guihua and Qingniao do not require a particular UI, authentication system or product host.
3. Non-goals
This feature does NOT:
- make Fuwen an authentication or IAM system;
- put credentials into workflow source;
- allow models to grant themselves permissions;
- require interactive confirmation before every operation;
- make an LLM reviewer a hard security boundary;
- define Marang or Guyabano's user-interface design;
- define organization-specific roles such as administrator/developer/operator;
- attempt automatic migration of every historical authority representation;
- authorize arbitrary dynamic behavior merely because an agent explains why it is needed.
The host remains responsible for trusted catalogues, credentials, authorization and admission policy, which is consistent with Fuwen's current boundary. GitHub
4. Terminology
4.1 Authority Requirement
A declarative statement describing authority an activity needs.
Examples:
repo.read:/src/**
repo.write:/src/**
process.execute:dotnet
network.connect:api.github.com
github.pull-request.read
github.pull-request.write
package.publish:nuget
secret.use:nuget-publish

A requirement is not permission.
It says:
This activity cannot correctly execute without this authority.

4.2 Resource Reference
A symbolic reference to a resource controlled by the host.
For example:
workspace.main
github.repository
nuget.production
database.reporting

Fuwen source MUST NOT contain credentials or unrestricted host handles.
The host resolves symbolic resources into concrete grantable resources during admission.
4.3 Authority Grant
A host-issued authorization permitting some operation against some resource under defined constraints.
Conceptually:
AuthorityGrant
    GrantId
    Capability
    ResourceIdentity
    Scope
    Constraints
    IssuedAt
    ExpiresAt?
    RevocationVersion
    PolicyIdentity

Example:
Capability: repo.write
Resource: workspace.main
Scope: /src/**

4.4 Authority Envelope
The complete authority available to one workflow run.
Conceptually:
WorkflowAuthorityEnvelope
    EnvelopeId
    WorkflowRunId
    WorkflowPlanFingerprint
    AdmittedRevision
    PolicyIdentity
    Grants[]
    Budgets[]
    Constraints[]
    Version

The envelope belongs to the workflow run, not the planning agent.
4.5 Activity Authority
The subset of the workflow authority envelope available to a particular activity.
The central invariant is:
ActivityEffectiveAuthority ⊆ WorkflowAuthority

4.6 Delegated Authority
The subset of activity authority passed to a Qingniao delegation.
The invariant becomes:
DelegatedAuthority
    ⊆ ActivityEffectiveAuthority
    ⊆ WorkflowAuthority

Delegation MUST NOT widen authority.
4.7 Authority Delta
The difference between the requirements of two workflow revisions.
Example:
revision 7 -> revision 8

unchanged:
    repo.read:/src/**
    process.execute:dotnet

added:
    package.publish:nuget
    secret.use:nuget-publish

removed:
    repo.write:/docs/**

The delta is the primary input to re-admission.
4.8 Authority Request
A durable request for rights not currently available to the workflow.
Conceptually:
AuthorityRequest
    RequestId
    WorkflowRunId
    BaseRevision
    ProposedRevision
    RequirementDelta
    Reason
    RequestingNode
    EvidenceReferences
    Status

Statuses might include:
Pending
Granted
PartiallyGranted
Denied
Expired
Withdrawn
Superseded

The agent can create the request.
The agent cannot approve it.
5. Core invariants
These invariants are normative.
INV-01: Requirements are not grants
A workflow declaring:
requires package.publish

does not grant package.publish.
INV-02: No authority through delegation
A workflow or activity cannot delegate authority it does not possess.
ChildAuthority ⊆ ParentAuthority

MUST always hold.
INV-03: Replanning does not imply reauthorization
A valid new workflow revision may still be unauthorized.
Plan validity and authority admission are independent concerns.
INV-04: New authority requires admission
No activity requiring newly introduced authority may begin until that authority has been granted.
INV-05: Authorization precedes side effects
Authority MUST be validated before any protected tool or resource I/O.
A failed authority check means:
protected I/O count = 0

INV-06: Runtime enforcement remains mandatory
Successful admission does not replace runtime checks.
Authority is checked:
workflow admission
        ↓
activity dispatch
        ↓
delegation creation
        ↓
protected host I/O

INV-07: Restart cannot increase authority
Crash recovery MUST reproduce the same or narrower effective authority.
A workflow cannot obtain additional authority because persisted state was incomplete or interpreted differently after restart.
INV-08: Wake-up does not grant authority
Signals, retries, supervisor actions, model messages or execution-provider responses cannot implicitly extend authority.
This matches Qingniao's existing principle that wake-up hints do not authorize work or extend budgets. GitHub
INV-09: Revocation prevents future use
Once the host considers a grant revoked, no new protected I/O may use that grant.
INV-10: Historical evidence and execution authority are separate
Revoking authority to access a live resource does not automatically define whether historical protected evidence may be read.
Recorded-run access is a separate host policy.
6. Architecture
                    Agent / Planner
                          |
                          v
                       Guihua
                designs / revises plan
                          |
                          | requirements per activity
                          v
                       Fuwen
              parses, validates, compiles
                          |
                          v
                 WorkflowPlan Revision
                 + AuthorityRequirements
                          |
                          v
                    Host Admission
          resolves resources + grants authority
                          |
                          v
                AdmissionReceipt
             + AuthorityEnvelope
                          |
                          v
                        Zhinu
              durable workflow execution
                          |
           +--------------+--------------+
           |                             |
           v                             v
    local activity                  Qingniao
                                bounded delegation
                                        |
                                        v
                              execution provider

During replanning:
Execution evidence
       |
       v
    Guihua
       |
       v
new Fuwen revision
       |
       v
authority diff
       |
       +------ no additional authority ------> continue
       |
       +------ additional authority ---------> request grant
                                                |
                                                v
                                           suspend safely
                                                |
                                     human/host decision
                                                |
                             +------------------+----------------+
                             |                                   |
                           grant                               deny
                             |                                   |
                             v                                   v
                        re-admit                          replan or stop
                             |
                             v
                          resume

7. Fuwen responsibilities
Fuwen currently defines typed workflow semantics, compilation, immutable plans, capability review, execution fingerprints and host admission boundaries. This proposal extends that existing model rather than introducing a separate policy language. GitHub
7.1 Fuwen MUST own requirement representation
Every executable activity MAY declare authority requirements.
Where an activity performs protected operations, its required authority MUST be statically representable.
Illustrative syntax only:
activity modify_source(...) -> result
requires {
    repo.read(workspace.main, "/src/**");
    repo.write(workspace.main, "/src/**");
    process.execute(toolchain.dotnet);
}

For inference:
infer analyze = infer profile
    ...
    requires {
        model.invoke(profile);
        tool.read(repository);
    }

For delegation:
delegate implement
    using coding_agent
    requires {
        repo.read(workspace.main, "/src/**");
        repo.write(workspace.main, "/src/**");
        process.execute(toolchain.dotnet);
    }

The precise grammar may differ. The semantics are normative.
7.2 Requirements form part of plan identity
Changing authority requirements changes workflow semantics.
Therefore modifying:
repo.read

to:
repo.read
repo.write

MUST change the plan/revision fingerprint.
Authority requirements MUST participate in canonicalization.
7.3 Fuwen MUST support authority summarization
The compiler SHOULD produce both:
PerNodeAuthorityRequirements

and:
WorkflowMaximumAuthorityRequirements

The latter is the union of all statically reachable requirements, accounting for bounded branches where possible.
This allows the host to present:
This workflow may require these rights.

before execution.
7.4 Broad requests should produce diagnostics
Fuwen SHOULD be capable of warning about unusually broad scopes.
Example:
repo.write:/**

when the activity appears to require only:
repo.write:/src/**

This is not necessarily a compilation error because only the planner may understand the true requirement.
But broad authority should be visible.
7.5 Fuwen MUST NOT contain concrete secrets
The DSL may reference:
credential:nuget-production

or a trusted catalogue entry.
It MUST NOT contain:
api-key = "..."

7.6 Compilation result
The compiled representation should conceptually contain:
WorkflowPlan
    Revision
    Fingerprint

    Nodes[]
        NodeId
        ActivityDescriptor
        RequiredAuthority[]
        ...

    RequiredAuthoritySummary[]

7.7 Admission receipt
The existing admission concept should be extended to bind authority.
Conceptually:
AdmissionReceipt
    WorkflowPlanFingerprint
    WorkflowRevision
    AuthorityEnvelopeId
    AuthorityEnvelopeVersion
    PolicyIdentity
    CatalogueIdentity
    IssuedAt

An admission receipt MUST NOT be reusable against a semantically different workflow revision unless explicitly revalidated.
8. Guihua responsibilities
Guihua currently owns planning, workflow mutation and revision integration around Fuwen plans. GitHub
Authority should become an explicit input to planning rather than an afterthought.
8.1 Initial planning
When Guihua asks a model to design a workflow, the model MUST be told:
1. which activities/tools are available;
2. the authority each requires;
3. what authority the workflow may potentially request;
4. that authority should be minimized;
5. that undeclared authority cannot be used later.
The planning prompt should make authority a first-class planning dimension alongside:
inputs
outputs
dependencies
capabilities
budgets
failure behavior
checkpoints

8.2 Planner output
For every activity Guihua should expect:
activity intent
required capabilities
required resources
scope
reason

The reason need not necessarily become executable DSL, but SHOULD be retained as planning provenance.
Example:
Node: apply-fix

Requirements:
    repo.read workspace.main /src/**
    repo.write workspace.main /src/**
    process.execute dotnet

Reason:
    inspect and modify implementation, then compile it

8.3 Least privilege
Guihua SHOULD prompt the planner to request the narrowest practical authority.
Bad:
filesystem.*
process.*
network.*

Preferred:
repo.read:/src/**
repo.write:/src/**
process.execute:dotnet

Authority quality may later become an evaluation signal.
9. Guihua workflow evolution
This is the most important change for Guihua.
A workflow revision has two independent questions:
Is this revision a valid plan?

Is this revision authorized to execute?

The first is primarily planning/compiler concern.
The second is host/runtime concern.
9.1 Revision comparison
When Guihua proposes a new plan revision, the integration layer MUST calculate:
AuthorityDelta =
    Requirements(newRevision)
    - Requirements(currentRevision)

Preferably it should also compare against the currently granted envelope, because a previous revision may already have obtained broader authority.
Therefore:
NewAuthorityNeeded =
    Requirements(newRevision)
    - CurrentAuthorityEnvelope

9.2 No new authority
If:
NewAuthorityNeeded = ∅

the revision may proceed through the normal revision-validation path.
No additional human authorization is required.
9.3 New authority required
If the revision adds uncovered requirements:
+ database.schema.modify
+ secret.use:migration

Guihua MUST NOT treat the revision as executable.
It SHOULD produce an authority request containing:
requested authority
affected activity
why the revised plan needs it
relevant execution evidence
current revision
proposed revision

9.4 Asking permission on behalf of the workflow
The user-facing concept is:
The planning agent asks the human for the additional authority on behalf of the workflow.

Architecturally, Guihua creates the request, while the host transports it to whatever approval surface exists.
That may be:
- Marang MCP response;
- Guyabano UI;
- REST endpoint;
- CLI;
- notification;
- future automatic policy reviewer.
Guihua itself MUST NOT know how approval is presented.
9.5 Denial
When additional authority is denied, Guihua MAY be asked to find another plan that operates inside the existing envelope.
Example:
Need:
    cloud.infrastructure.modify

Denied.

Replan using:
    existing repo.write
    CI configuration changes

This is an important capability.
A permission denial should be usable as planning evidence, rather than always terminating the entire goal.
10. Zhinu responsibilities
Zhinu is the durable workflow execution engine and already owns crash recovery, replay-safe completed steps, leases, fencing, retries, cancellation, signals and durable step boundaries. GitHub
Zhinu SHOULD be the primary runtime enforcement point for workflow authority.
10.1 Workflow start
A protected Fuwen workflow MUST NOT begin unless Zhinu receives a valid admission binding.
Conceptually:
StartWorkflow(
    WorkflowPlan plan,
    AdmissionReceipt admission,
    AuthorityEnvelope envelope)

Zhinu verifies:
plan fingerprint matches
revision matches
envelope matches admission
policy identity matches
required mandatory authority is satisfiable

10.2 Activity dispatch
Before executing a node:
required = node.RequiredAuthority
available = workflow.CurrentAuthorityEnvelope

if !Covers(available, required):
    do not invoke activity
    do not perform tool I/O
    transition to authority-required state

The runtime SHOULD also derive an activity-specific authority view:
effectiveAuthority =
    Intersect(
        WorkflowAuthority,
        NodeRequiredAuthority)

This prevents an activity from receiving unrelated grants merely because the workflow possesses them.
11. Runtime host-I/O enforcement
Zhinu's activity admission is not sufficient on its own.
Any trusted tool/resource adapter MUST recheck the grant when protected I/O actually occurs.
The desired model is:
1. Workflow admission
2. Node authority validation
3. Batch/tool proposal validation
4. Actual host I/O grant validation

This protects against:
- stale workflow state;
- revoked grants;
- malicious delegates;
- adapter bugs;
- unexpected dynamic arguments.
This should align with the FI-04 direction you already identified.
12. Durable authority state
Zhinu MUST persist enough information to prove which authority version applied to each execution attempt.
At minimum:
WorkflowRunId
PlanRevision
PlanFingerprint
AuthorityEnvelopeId
AuthorityEnvelopeVersion
PolicyIdentity

For protected steps, evidence SHOULD include:
RequiredAuthority
EffectiveGrantReferences
GrantVersions
ExecutionAttempt
Result

13. Waiting for authority
Insufficient authority is not an ordinary execution failure.
A new durable blocked condition is needed conceptually:
WaitingForAuthority

This MAY be implemented initially through an existing durable wait/suspension primitive rather than adding a new top-level Zhinu workflow state.
The persisted reason MUST remain typed:
AuthorityRequired
    AuthorityRequestId
    Revision
    MissingRequirements[]

No affected activity may execute while waiting.
14. Approval and resume
When approval arrives:
AuthorityRequest
      +
Host decision
      ↓
new AuthorityEnvelope version
      +
new AdmissionReceipt/re-admission evidence
      ↓
Zhinu resumes

Resume MUST use expected-state fencing.
For example:
expected WorkflowRunId
expected revision
expected AuthorityRequestId
expected current envelope version

A stale approval MUST NOT modify a newer workflow state.
This integrates naturally with Zhinu's existing fencing philosophy. GitHub
15. Revocation
Zhinu and the host adapter MUST treat grant revocation differently from plan revision.
If grant G7 is revoked:
future protected operation using G7
    -> deny

even if:
workflow admission originally included G7

Therefore grant validity is runtime data, not only immutable admission metadata.
16. In-flight revocation
External operations cannot always be undone.
If authority is revoked while an operation is already in flight:
1. prevent further protected calls;
2. request cancellation where the provider supports it;
3. do not pretend external work was rolled back;
4. persist that revocation occurred during the operation;
5. reconcile resulting evidence conservatively.
This is especially important for delegated execution.
17. Qingniao responsibilities
Qingniao owns a single bounded delegated unit of work and already separates delegation identity, provider execution, supervision, budgets and immutable evidence. GitHub
It MUST NOT become a second workflow-level authorization system.
Its job is authority attenuation and enforcement around delegation.
17.1 Delegation submission
A delegation request SHOULD carry an authority context:
DelegationAuthority
    ParentWorkflowRunId
    ParentNodeId
    AuthorityEnvelopeId
    AuthorityEnvelopeVersion
    DelegatedGrantReferences[]

The actual representation should remain provider-neutral.
17.2 Delegation invariant
Before accepting delegated work:
requestedDelegatedAuthority
    ⊆ parentActivityEffectiveAuthority

must hold.
Otherwise Qingniao rejects the delegation before provider execution.
18. Execution provider isolation
The delegated actor should receive only what it needs.
For a coding agent:
Workflow envelope:
    repo.read:/src/**
    repo.write:/src/**
    repo.read:/docs/**
    github.issue.read
    process.execute:dotnet

Delegated node:
    repo.read:/src/**
    repo.write:/src/**
    process.execute:dotnet

The execution actor SHOULD NOT receive:
github.issue.read
repo.read:/docs/**

merely because the workflow possesses those permissions.
19. Delegated agent discovers missing authority
A delegated actor may discover during execution that the original plan is insufficient.
Example:
Task:
    fix compilation issue

Actor discovers:
    package restore requires private registry credential

The actor MUST NOT obtain or use that credential itself.
Instead it returns/surfaces a typed request such as:
AdditionalAuthorityRequired
    secret.use:private-nuget
    network.connect:private-registry

Qingniao converts this into supervision/evidence.
The parent workflow then decides whether Guihua should revise the plan and request additional workflow authority.
20. Qingniao supervision
Qingniao currently supports typed supervision and NeedsSupervisor / WaitingForSupervisor states. GitHub
For V1, additional authority can reuse that infrastructure provided the reason is explicit:
SupervisorReason.AuthorityRequired

The supervision action MUST NOT itself create authority.
For example:
Approve

cannot mean:
give the agent whatever permissions it requested

Instead the host must first issue/update the actual authority envelope.
Then supervision resumes the delegation using that new authoritative state.
21. Delegation must not smuggle authority
Arguments and artifacts can indirectly expose privileged resources.
Therefore Qingniao must treat the following as part of authority review:
tool handles
credential references
filesystem roots
remote resource handles
environment variables
MCP server access
execution-provider configuration

A child cannot receive an unrestricted resource handle if its delegated scope permits only a narrow subset.
22. Budget authority
Budgets should fit the same architecture but remain distinct from ordinary capabilities.
The workflow authority envelope may contain:
ModelSpendBudget
TokenBudget
ToolCallBudget
WallClockBudget
ComputeBudget
ExternalServiceBudget

An activity cannot delegate more budget than is available to it:
DelegatedBudget ≤ ParentRemainingBudget

This mirrors the authority attenuation rule.
23. Fuwen and budgets
Fuwen describes requirements such as:
maximum model calls
maximum requested model cost
bounded fan-out
tool limits

where these can be known.
The host supplies the actual admitted budget.
If an exact strict provider cost ceiling cannot be guaranteed, strict cost admission remains unavailable for that path rather than being silently downgraded.
This preserves the direction of X-01 in the current inference register.
24. Replanning example
Initial workflow:
Revision 1

inspect
    requires repo.read:/src/**

modify
    requires repo.read:/src/**
             repo.write:/src/**

test
    requires repo.read:/src/**
             process.execute:dotnet

Workflow admitted with exactly those rights.
Execution reaches test.
Tests reveal a schema migration is required.
Guihua proposes:
Revision 2

inspect
modify
create-migration
apply-migration
test

New requirements:
+ repo.write:/migrations/**
+ database.schema.modify:development

Runtime comparison:
Revision 2 requirements
        -
Current workflow authority
        =
repo.write:/migrations/**
database.schema.modify:development

Guihua emits:
AuthorityRequest AR-17

Reason:
Tests demonstrate the required data shape cannot be supported
without a schema migration.

Requested:
    repo.write:/migrations/**
    database.schema.modify:development

Affected nodes:
    create-migration
    apply-migration

Zhinu persists:
WaitingForAuthority(AR-17)

No migration code or database I/O occurs.
25. Human approval example
Guyabano or Marang presents:
Workflow revision 2 needs additional access.

Requested:
  Write access: /migrations/**
  Modify schema: development database

Reason:
  The revised implementation requires a database migration.

Approve
Deny

The human approves.
The host creates:
AuthorityEnvelope v2

containing the additional grants.
The revised plan is re-admitted against envelope v2.
Zhinu resumes.
26. Denial and replanning example
Human denies:
database.schema.modify

That becomes evidence:
AR-17 denied
Constraint:
    database schema may not be changed

Guihua may now attempt:
Revision 3:
    implement compatibility layer

which remains inside the original authority envelope.
This is preferable to treating permission denial as automatically fatal.
27. Initial admission UX
The workflow should normally request authority once before it begins.
For example:
Workflow requests:

Repository
  Read /src/**
  Write /src/**
  Read /tests/**

Processes
  Execute dotnet

Network
  api.openai.com

Models
  coding-profile

Maximum model budget
  $3.00

The human approves the workflow envelope, not 40 individual tool calls.
That is the mechanism that avoids Antigravity-style approval fatigue.
28. Preauthorized flexibility
A host MAY allow the user to grant a broader envelope than the exact initial plan requires.
For example:
Current plan requires:
    repo.write:/src/project-a/**

User grants:
    repo.write:/src/**

A later workflow revision that writes /src/project-b/** would then require no additional authorization because it remains inside the already granted envelope.
This is useful for balancing autonomy against least privilege.
However:
Planner requirements and granted authority remain separate.

The planner should still describe only what the current revision actually expects to use.
29. Authority request vs workflow revision
An authority request does not make the proposed revision active.
Likewise, granting authority does not automatically activate arbitrary planner output.
The sequence SHOULD remain:
propose revision
      ↓
validate revision
      ↓
calculate authority delta
      ↓
obtain missing authority
      ↓
admit exact revision
      ↓
activate revision

This avoids a grant unintentionally approving unrelated plan changes.
30. Recovery semantics
Crash points MUST be tested around every authority boundary.
Important cases:
crash after authority request persisted
crash after human approval but before envelope update
crash after envelope update but before admission receipt
crash after admission but before activity dispatch
crash after activity authority check but before I/O
crash after external I/O but before evidence commit
crash while a grant is revoked

Recovery MUST never infer permission from incomplete state.
When uncertain:
fail closed / wait for reconciliation

rather than:
assume previously authorized

31. Persistent identity rules
Authority records SHOULD bind to stable identities:
WorkflowRunId
WorkflowRevision
PlanFingerprint
NodePath / NodeIdentity
NodeGeneration
AuthorityEnvelopeId
AuthorityEnvelopeVersion
GrantId
PolicyIdentity

For fan-out, item identity MUST also participate where required.
This prevents authorization for one fan-out item from accidentally becoming authorization for another.
32. Workflow mutation
Guihua MUST preserve stable node identity where semantics remain unchanged.
If an activity's authority-sensitive semantics change materially, the new node/revision MUST not accidentally inherit stale execution admission.
Examples that should force renewed evaluation:
resource changes
scope widens
tool changes
effect classification changes
credential changes
provider changes where policy depends on provider

33. Authority comparison
Comparison cannot be simple string equality.
The system needs semantic containment.
For example:
grant:
    repo.write:/src/**

requirement:
    repo.write:/src/service/Foo.cs

=> covered

But:
requirement:
    repo.write:/tests/FooTests.cs

=> not covered

Each capability family therefore needs a trusted containment evaluator owned by the host/catalogue layer.
Fuwen SHOULD NOT attempt to understand arbitrary IAM semantics itself.
34. Capability descriptors
A capability descriptor should ideally identify:
Capability type
Resource kind
Operation/effect class
Scope grammar
Containment evaluator
Delegatability
Revocability
Audit requirements

Example:
repo.write
    resource-kind: repository
    effect: write
    delegatable: true
    scope: path-pattern

Potentially:
secret.use
    effect: privileged
    delegatable: restricted

Some capabilities MAY explicitly forbid delegation.
35. Delegation policy
Not every valid parent permission should necessarily be delegatable.
Therefore:
CanDelegate(grant, targetActor, requestedScope)

is distinct from:
CanUse(grant, currentActivity)

Example:
A host may permit the local runtime to use a signing credential but prohibit passing access to an external coding-agent provider.
36. Provider trust
Qingniao provider selection SHOULD participate in delegation authorization.
For example:
repo.read public source
    -> local agent allowed
    -> remote trusted agent allowed

secret.use production-signing-key
    -> local signing service allowed
    -> remote model agent forbidden

This gives the host a way to encode trust boundaries without putting them into Fuwen syntax.
37. Evidence
Every authority-sensitive event SHOULD emit immutable evidence.
Examples:
WorkflowAuthorityRequested
WorkflowAuthorityGranted
WorkflowAuthorityDenied
WorkflowAuthorityRevoked
WorkflowReadmitted
ActivityAuthorityValidated
ActivityAuthorityRejected
DelegatedAuthorityBound
AuthorityExpansionRequested

Evidence should include references rather than secret material.
38. Hongxian integration
Hongxian is not required to implement authority decisions, but when present it should record the narrative/evidence trail.
Conceptually:
Revision 4 proposed
Revision 4 requested capability X
Human granted X
Revision 4 admitted under envelope v3
Node A exercised X
Grant X revoked
Node B blocked before I/O
Revision 5 proposed without X
Revision 5 resumed

This makes authority part of the explainable execution history.
39. Public API shape
Exact API names are non-normative, but a shared conceptual model could include:
public sealed record AuthorityRequirement(
    CapabilityId Capability,
    ResourceReference Resource,
    AuthorityScope Scope);

public sealed record AuthorityGrantReference(
    GrantId GrantId,
    long Version);

public sealed record WorkflowAuthorityEnvelope(
    AuthorityEnvelopeId Id,
    long Version,
    PolicyIdentity Policy,
    IReadOnlyList<AuthorityGrantReference> Grants);

public sealed record AuthorityDelta(
    IReadOnlyList<AuthorityRequirement> Added,
    IReadOnlyList<AuthorityRequirement> Removed);

public sealed record AuthorityRequest(
    AuthorityRequestId Id,
    WorkflowRunId Workflow,
    PlanRevision BaseRevision,
    PlanRevision ProposedRevision,
    IReadOnlyList<AuthorityRequirement> Requested,
    string Reason);

Actual contracts should probably live at the lowest layer that can remain host-neutral.
40. Component ownership
Concern	Owner
DSL representation of required authority	Fuwen
Compile-time validation	Fuwen
Plan fingerprint inclusion	Fuwen
Authority requirement summary	Fuwen
Planner asks for appropriate authority	Guihua
Least-privilege planning guidance	Guihua
Revision authority diff	Guihua/Fuwen integration
Additional-authority request generation	Guihua
Actual authorization policy	Host
Concrete resource/credential resolution	Host
Workflow authority envelope	Host + Zhinu boundary
Durable binding to run/revision	Zhinu
Per-activity enforcement	Zhinu
Recheck before actual host I/O	Host adapter
Delegation attenuation	Qingniao
Delegated actor capability enforcement	Qingniao + provider adapter
Delegated missing-authority reporting	Qingniao
Human approval UX	Marang / Guyabano / host
Audit narrative	Hongxian when present


41. What must not happen
The implementation MUST prevent the following patterns.
Agent self-authorization
"I need access, therefore I have access."

Forbidden.
Delegation escalation
Parent has repo.read.
Child receives repo.write.

Forbidden.
Revision escalation
Revision 2 adds publish capability.
Runtime silently continues.

Forbidden.
Command-level prompt dependence
Can I run git?
Can I run dotnet?
Can I edit this file?
Can I read that file?

This should not be the normal architecture.
Grant by model explanation
The model says this command is safe.

Not sufficient.
Recovery escalation
Grant state missing after restart.
Assume previous workflow had access.

Forbidden.
42. Interaction with auto-review
An automatic semantic reviewer can be added later, but it should operate inside host policy, not replace it.
For example:
authority request
      ↓
host policy
      |
      +-- preauthorized -> grant
      |
      +-- eligible for auto-review -> reviewer
      |
      +-- human-required -> human

Even an auto-review approval produces a normal host grant.
The runtime should not care whether the authority decision came from:
human
static policy
organization policy
automatic reviewer

It only accepts a valid grant.
This keeps probabilistic reasoning outside the hard enforcement boundary.
43. Suggested implementation sequence
I would not build every advanced case at once.
Phase 1 should establish the invariant.
1. Fuwen per-activity authority requirements.
2. Requirement canonicalization/fingerprinting.
3. Workflow-level requirement summary.
4. Host-issued authority envelope.
5. Zhinu activity enforcement.
6. Fenced persistence of envelope identity/version.
7. Qingniao subset delegation.
8. Zero-I/O tests for missing authority.
Phase 2 should add evolution.
9. Guihua authority-aware authoring.
10. Revision authority diff.
11. Durable AuthorityRequest.
12. waiting/suspension semantics.
13. host approval and re-admission.
14. denial fed back into replanning.
Phase 3 should harden runtime behavior.
15. revocation;
16. host-I/O recheck;
17. delegation restrictions by provider trust;
18. crash matrix;
19. protected recorded-output policy;
20. concurrent/fan-out authority proof.
Phase 4 can add higher-level policy.
21. preauthorized expansion classes;
22. automatic semantic review;
23. organization policy packs;
24. authority-quality evaluation;
25. learning from accepted/denied authority requests.
44. Required tests
At minimum the cross-project suite should prove:
planner declares required authority
compiler preserves it
plan fingerprint changes when it changes
workflow cannot start without required admission
activity cannot exceed its declared authority
activity cannot exceed workflow authority
child cannot exceed parent authority
delegation cannot widen authority
wrong resource causes zero I/O
wrong scope causes zero I/O
revoked grant causes zero new I/O
replay does not reacquire authority
workflow revision with no authority delta continues
revision with authority delta stops before protected I/O
approval binds the exact revision/request
stale approval is rejected
denial can trigger replanning
revised plan inside existing envelope can resume
crash around admission cannot widen authority
fan-out items cannot borrow unauthorized grants from siblings

The strongest invariant test should probably be:
For every denied authority path, protected side-effect count is exactly zero.

45. Acceptance criteria
This feature is complete enough for initial use when the following scenario passes end to end:
1. Guihua authors a Fuwen workflow.
2. Each protected activity declares its authority requirements.
3. Fuwen compiles the workflow and exposes an authority summary.
4. The host grants a narrower explicit authority envelope.
5. Zhinu durably starts the admitted workflow.
6. Activities execute without interactive prompts while they remain inside the envelope.
7. Qingniao receives only an explicitly attenuated subset for delegated work.
8. The delegated actor cannot use an undelegated capability.
9. Execution evidence causes Guihua to propose a new revision requiring additional access.
10. Zhinu stops before the first newly privileged operation.
11. An authority request is exposed to the host.
12. A human grants the additional access.
13. The exact workflow revision is re-admitted.
14. Execution resumes after restart without reacquiring or widening authority.
15. Audit evidence shows the complete request, grant, revision and use chain.
46. Architectural decision
I would capture the main decision as an ADR:
Authority is workflow-scoped, activity-declared, host-granted, runtime-enforced and delegation-attenuating.

And the companion rule:
Workflow evolution may change what authority is required, but it may never silently change what authority has been granted.

That gives you a clean division across the stack:
Guihua
    decides what work should be done
    and what access it believes that work requires

Fuwen
    makes those requirements explicit,
    typed and immutable

Host
    decides what authority the workflow receives

Zhinu
    durably enforces that authority per activity

Qingniao
    delegates only a bounded subset of it

Guihua
    may later ask for more on behalf of the workflow,
    but cannot grant it