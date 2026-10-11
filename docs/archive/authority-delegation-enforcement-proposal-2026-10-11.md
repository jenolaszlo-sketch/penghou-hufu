# Hufu Authority, Delegation and Enforcement Model

## 1. Purpose

Clarify the authority model across Hufu, Zhinu, Gagamba, Biscuit, A2A, MCP, brokers, and other delegated execution boundaries.

The main architectural distinction is:

> Hufu owns logical authority. Gagamba enforces OS-level execution boundaries. Biscuit carries delegated authority across trust, process, or service boundaries.

This distinction must be reflected consistently throughout Hufu documentation, ADRs, examples, terminology, integration guidance, and related architectural material.

This is primarily a design/documentation correction and clarification. Do not introduce implementation changes solely because of this specification unless an existing implementation demonstrably contradicts the model.

---

# 2. Core Model

Authority exists at multiple levels.

```text
Workflow authority
      ↓
Activity authority
      ↓
Execution / delegation authority
      ↓
Concrete enforcement
```

The important point is that these levels are related but are not the same thing.

A workflow activity may have logical authority that changes over time.

A particular sandboxed execution receives only a snapshot or delegated subset of that authority for the lifetime of that execution.

---

# 3. Hufu Owns Logical Authority

Hufu is the source of truth for what a workflow activity is currently authorized to do.

Example:

```text
Workflow W

Activity A
    read:  /repo
    write: /output
    network: none

Activity B
    read:  /repo
    write: none
    network: api.github.com
```

Activity authority may change because of:

- activity start;
- activity completion;
- suspension;
- resume;
- retry;
- workflow mutation;
- explicit grant;
- explicit revocation;
- human approval;
- delegation;
- compensation;
- policy changes;
- changed evidence or workflow state.

Therefore:

> Effective Hufu authority is part of live workflow state.

This authority is not inherently tied to one operating-system process.

---

# 4. Activity Authority Is Not an OS Security Boundary

Operating systems do not understand concepts such as:

```text
Workflow
Activity
Step
Checkpoint
Resume
Compensation
```

They understand enforcement constructs such as:

```text
process
process tree
access token
user / uid
namespace
cgroup
Job Object
sandbox profile
file descriptor
socket
filesystem ACL
```

Therefore multiple logical activities executing inside the same trusted .NET process cannot be assumed to have independent OS-level permissions merely because Hufu assigns them different authority.

Example:

```text
Zhinu host process

Activity A
Activity B
Activity C
```

If those activities directly call:

```text
File.Open(...)
Process.Start(...)
HttpClient.Send(...)
```

they exercise the ambient authority of the trusted host process unless those operations are mediated.

Hufu's logical activity boundary must therefore not be documented as if it were itself an OS isolation boundary.

---

# 5. Two Enforcement Modes

Hufu authority can be enforced in two main ways.

## 5.1 Mediated Operations

A trusted broker or abstraction checks current Hufu authority for every operation.

Example:

```text
Activity
   ↓
File abstraction / HTTP broker / tool broker
   ↓
Hufu authorization check
   ↓
Operation
```

Example:

```text
Activity A
    read:/repo

ReadFile("/repo/a.cs")
    → Hufu check
    → allowed
```

Later:

```text
Activity B
    read:/repo revoked

ReadFile("/repo/a.cs")
    → Hufu check
    → denied
```

In this model, authority can change immediately between operations because authorization is evaluated at the mediation point.

Potential examples include:

- Penghou filesystem abstractions;
- HTTP brokers;
- model/provider gateways;
- tool brokers;
- MCP gateways;
- A2A gateways;
- privileged host operations.

---

## 5.2 Sandboxed Execution

For untrusted or independently executing code, Hufu resolves the authority to be delegated and Gagamba creates an OS-enforced sandboxed execution.

```text
Zhinu Activity
      ↓
Hufu resolves effective authority
      ↓
Gagamba execution
      ↓
sandboxed process / process tree
```

The sandbox is an enforcement instance of delegated authority.

The sandbox is not the owner or source of that authority.

---

# 6. Gagamba Enforcement Lifetime

A Gagamba sandboxed execution is bound to an execution/process-tree lifetime.

Its effective OS permissions should be treated as fixed for that execution.

Example:

```text
Activity 17
authority:
    read:/repo
        ↓
Sandbox X
```

If Activity 17 later resumes with:

```text
read:/repo
write:/output
```

the system must not conceptually treat Sandbox X as having dynamically acquired the additional authority.

Instead:

```text
Activity 17 resumed
        ↓
Hufu resolves new authority
        ↓
Sandbox Y
    read:/repo
    write:/output
```

Core invariant:

> Authority may change during a workflow, but an already-running sandbox must not automatically gain new authority.

Changed authority should result in a new delegation and, where OS enforcement is required, a new sandboxed execution.

---

# 7. Activity-to-Sandbox Cardinality

Do not document the architecture as:

```text
1 Activity = 1 Sandbox
```

The correct relationship is:

```text
Activity
    ↓
0..N sandboxed executions
```

An activity may require:

- no sandbox;
- one sandbox;
- several sandboxed executions;
- a new sandbox after retry;
- a new sandbox after resume;
- separate sandboxes for independent tool invocations.

Example:

```text
Activity A
    ├─ Sandbox A1
    ├─ Sandbox A2
    └─ Sandbox A3
```

Each execution may receive a different delegated subset of the activity's total authority.

---

# 8. Delegation

Hufu must distinguish between:

```text
authority owned by an activity
```

and:

```text
authority delegated to a particular execution
```

An activity may own:

```text
read:/repo
write:/output
network:github.com
```

but launch an execution requiring only:

```text
read:/repo
```

The delegated execution must receive only the narrower authority.

Delegation must therefore be monotonic:

> Delegated authority may be equal to or narrower than the delegator's effective authority, but must never be broader.

---

# 9. Biscuit's Role

Biscuit should be treated as a portable representation of delegated authority.

Biscuit is not the source of truth for workflow authority.

Hufu remains the source of truth.

Conceptually:

```text
Hufu
   ↓
resolve current authority
   ↓
select delegated subset
   ↓
issue / attenuate Biscuit
   ↓
cross process/service/trust boundary
```

A Biscuit may identify or constrain concepts such as:

```text
workflow
activity
execution
subject
audience
tool
resource
operation
expiry
delegated capability
```

Exact schema is an implementation concern and should remain versionable.

---

# 10. When Biscuit Is Useful

Biscuit is most useful when authority crosses a boundary.

Examples:

```text
Activity → sandbox
Activity → A2A gateway
Activity → MCP gateway
Activity → remote worker
Sandbox → host broker
Sandbox → helper process
Gateway → delegated service
Service → further delegated helper
```

General rule:

> When authority leaves the immediate Hufu execution context, carry an explicit constrained delegation rather than relying on ambient trust.

---

# 11. Biscuit Does Not Replace OS Enforcement

Biscuit cannot prevent a process from using OS resources that the process can already directly access.

For example:

```text
File.Open(...)
socket(...)
Process.Start(...)
```

are enforced by the operating system, sandbox, or trusted mediation layer.

Therefore:

```text
Hufu
    decides authority

Biscuit
    carries delegated authority

Gagamba
    enforces process-level containment
```

These are complementary mechanisms.

---

# 12. Biscuit Does Not Replace Current Hufu State

A Biscuit may represent previously delegated authority, but Hufu may subsequently revoke or change authority.

Therefore, where current revocation matters, trusted brokers should evaluate both:

```text
1. Is the Biscuit cryptographically valid?
2. Is it intended for this audience/execution?
3. Does it authorize this operation?
4. Is the authority still valid according to current Hufu state?
5. Is the concrete resource/request within policy?
```

Do not document Biscuit as implying irrevocable authorization until expiry unless a specific subsystem deliberately chooses that semantic.

Hufu remains responsible for live policy and revocation.

---

# 13. A2A Gateway Model

An A2A gateway or agent proxy is a natural authority boundary.

Example:

```text
Zhinu activity
     ↓
Hufu
     ↓
Biscuit
     ↓
A2A Gateway
     ↓
Codex / Claude / Gemini / other agent
```

The A2A gateway should consume the delegated authority and determine what capabilities the downstream agent is allowed to exercise.

Example:

```text
Activity authority:

agent: codex
repo: read
generated: write
web-search: allowed
shell: denied
```

The gateway may expose:

```text
Codex                 allowed
repo-read             allowed
generated-write       allowed
web-search            allowed
shell                  denied
```

The downstream model/provider does not need to understand Hufu or Biscuit.

The gateway is responsible for translating Hufu authority into the capability surface exposed downstream.

---

# 14. MCP Gateway and MCP Server Model

MCP follows the same principle.

There are two cases.

## 14.1 Hufu-aware MCP server

A server under our control may understand Hufu/Biscuit delegation directly.

```text
Activity
   ↓ Biscuit
MCP Server
   ↓
Hufu/Biscuit authorization adapter
   ↓
Tool
```

Tool handlers ideally should not need to parse Biscuit themselves.

Instead, provide an authorization context such as:

```text
context.Authority.CanRead(path)
context.Authority.CanInvokeTool(name)
context.Authority.ActivityId
context.Authority.ExecutionId
```

Exact API is out of scope for this design clarification.

---

## 14.2 Hufu-unaware MCP server

Third-party MCP servers should not be expected to understand Hufu or Biscuit.

Use a trusted gateway:

```text
Activity
   ↓ Biscuit
Hufu-aware MCP Gateway
   ↓
ordinary MCP
   ↓
third-party MCP server
```

The gateway consumes the Biscuit, authorizes the operation, and only forwards operations permitted by Hufu.

Do not blindly forward Hufu credentials to arbitrary third-party services.

---

# 15. Boundary Principle

The architecture should consistently apply this rule:

> Every time authority crosses a process, trust, service, worker, agent, or broker boundary, the receiving trusted component must be able to determine what authority was delegated.

That may be achieved through:

- Biscuit;
- an equivalent scoped credential;
- trusted in-process context where no boundary was crossed;
- an adapter that converts Hufu authority into another authorization mechanism.

Biscuit is the preferred general mechanism where portable attenuation is useful, but architecture documentation should preserve abstraction between the authority model and the credential format.

---

# 16. Example End-to-End Flow

```text
Workflow W
    ↓
Activity A starts
    ↓
Hufu resolves:

    read:/repo
    write:/output
    agent:codex
    tool:web-search

    ↓
Activity delegates agent work
    ↓
Biscuit A1 issued:

    read:/repo
    agent:codex
    tool:web-search

    ↓
A2A Gateway
    ↓
Codex

Codex requests repo access
    ↓
trusted tool/MCP gateway
    ↓
delegated authority checked
    ↓
read permitted
```

Later:

```text
Activity A suspends

Hufu revokes:
    tool:web-search

Activity A resumes
    ↓
new delegation required
    ↓
Biscuit A2

    read:/repo
    agent:codex
    no web-search
```

Existing execution A1 must not automatically inherit the newly resolved authority.

Where current revocation is enforced online, A1 may also be rejected against current Hufu state.

---

# 17. Terminology

Use the following terminology consistently.

## Activity Authority

The effective logical authority currently assigned by Hufu to a workflow activity.

## Delegated Authority

A subset of authority granted to another execution, process, service, worker, agent, or broker.

## Execution Authority

Delegated authority attached to one concrete execution.

## Sandbox Policy

The OS-level enforcement configuration applied to a Gagamba execution.

## Biscuit

A portable, attenuable credential representing delegated authority.

## Current Authority

The authority presently considered valid by Hufu, including grants, revocations, policy, and workflow state.

Avoid using "sandbox permissions" and "activity permissions" interchangeably.

---

# 18. Required Invariants

The architecture and documentation should preserve these invariants.

### A1. Hufu is authoritative

Hufu owns the logical authority model.

### A2. Activity authority may change

Workflow progression, resume, revocation, approval, mutation, or other state transitions may change effective authority.

### A3. Sandboxes do not silently gain authority

An existing Gagamba execution does not automatically receive newly granted activity authority.

### A4. New authority means new delegation

Changed authority crossing an execution boundary requires a newly resolved delegation.

### A5. Delegation cannot amplify authority

A child execution/service cannot receive authority exceeding the delegator's effective authority.

### A6. Gagamba is an enforcement layer

Gagamba is not the source of workflow authorization truth.

### A7. Biscuit is a carrier

Biscuit represents delegated authority but does not replace Hufu's live authority state.

### A8. Mediated operations check authority

Where operations are brokered, authorization is evaluated by a trusted mediation point.

### A9. Ambient host authority is not activity authority

Code executing directly inside the trusted workflow host must not be considered isolated merely because it belongs to a different logical activity.

### A10. Third parties need not understand Hufu

Hufu-aware gateways may translate authority into the capability model understood by downstream systems.

---

# 19. Documentation and ADR Reconciliation

Astra must review the existing Hufu documentation and related Penghou documentation for statements that conflict with or obscure this model.

At minimum inspect:

- Hufu architecture documentation;
- Hufu authority/grant documentation;
- Biscuit integration documentation;
- Gagamba integration documentation;
- Zhinu/Hufu integration documentation;
- workflow authority documentation;
- sandbox documentation;
- MCP integration documentation;
- A2A/delegation documentation;
- relevant ADRs;
- diagrams;
- examples;
- terminology/glossary material;
- README descriptions;
- roadmap items that assume live sandbox-policy mutation.

Update or add ADRs where the clarified model represents an architectural decision rather than merely explanatory wording.

In particular, search for wording implying any of the following:

```text
activity permissions are OS permissions
Gagamba owns workflow authority
a sandbox dynamically follows activity authority
permissions are mutated inside one running sandbox
one activity always equals one sandbox
Biscuit is the source of truth
Biscuit replaces revocation checks
every downstream service must understand Hufu
```

Correct such statements.

Do not rewrite historical ADRs in a way that destroys decision history. If an existing ADR represents an obsolete model, supersede or amend it according to the repository's ADR conventions.

---

# 20. Documentation Diagram

Ensure the architecture is represented somewhere in canonical documentation approximately as:

```text
                    Zhinu
             workflow/activity state
                      |
                      v
                    Hufu
             live logical authority
                      |
          +-----------+-----------+
          |                       |
          v                       v
   mediated operations       delegation
          |                       |
   authority checked             Biscuit
      per request                 |
                                  |
                      +-----------+-----------+
                      |                       |
                      v                       v
                 A2A / MCP                Gagamba
                  gateways                sandbox
                      |                       |
                      v                       v
                 agents/tools          process tree
```

The exact diagram format may follow repository conventions.

---

# 21. Design Consequence

The larger system may accurately be described as:

> Permissions can change as workflows execute and activities run or resume.

Gagamba itself should instead be described as:

> Gagamba enforces the permissions delegated to a particular sandboxed execution. If workflow authority changes, the system may create a new execution with a different policy.

This distinction must remain explicit.

---

# 22. Non-Goals

This clarification does not require:

- dynamic mutation of a running Gagamba sandbox;
- one sandbox per activity;
- mandatory Biscuit use for every in-process call;
- teaching third-party MCP servers about Hufu;
- exposing internal Hufu credentials to external model providers;
- replacing OS sandboxing with capability tokens;
- replacing Hufu revocation with Biscuit expiry;
- requiring all mediated operations to launch a sandbox.

---

# 23. Requested Astra Deliverable

Review this specification against the current Hufu and related Penghou architecture.

Produce and apply the necessary documentation changes so the repository consistently reflects this model.

Specifically:

1. identify existing documents and ADRs affected;
2. identify statements that are inaccurate or ambiguous under this clarified model;
3. update architecture documentation and diagrams;
4. amend or supersede ADRs where required;
5. align Biscuit, Gagamba, Zhinu, MCP, A2A, broker, and delegation terminology;
6. preserve historical architectural decisions rather than rewriting history;
7. add missing ADRs if this clarification introduces a decision not currently recorded;
8. update examples so activity authority, delegated authority, and sandbox policy are visibly distinct;
9. note any implementation that currently contradicts the documented model;
10. do not make unrelated implementation changes.

The final result should make it difficult for a future reader to conclude that logical workflow activities themselves are OS security boundaries, or that Gagamba is expected to mutate the permissions of a running process whenever Hufu authority changes.