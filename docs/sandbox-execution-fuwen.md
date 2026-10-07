# Plan-authored execution intent (FZ-1)

Status: implementation slice, 2026-10-05. **FZ-1 frozen (tag `arch-fz-1`)**;
`Penghou.Hufu.Fuwen` composes a Fuwen plan activity with the frozen HZ-1/HG-1
authority path. Not qualified, not published, not a security boundary.

## Architectural checkpoint

The agent-to-effect authority chain is proven end to end, and each layer owns a
distinct question:

```text
agent-authored intent           Fuwen: what does the plan intend?
        |
Fuwen admission + identity      (immutable plan + fingerprint)
        |
Zhinu durable execution         Zhinu: what durable activity/attempt is running?
        |
Hufu contextual authority       Hufu: is this subject/activity authorized?
        |
trusted invocation profile      Sandbox profile: what exact invocation shape is permitted?
        |
Gagamba negotiated guarantees   Gagamba: can this host provide the guarantees?
        |
native execution domain
```

The plan never acquires authority merely by describing an effect. FZ-1, HG-1,
and HZ-1 are frozen together; the next pressure should come from a real
constrained tool consumer (Luban) that requires filesystem/process/HTTP
authority, not from another infrastructure milestone.

## Chain

```text
Fuwen plan (neutral ActivityExecutionIntent)
        |
        v
Zhinu durable activity (Fuwen.Zhinu adapter)
        |
        v
Penghou.Hufu.Fuwen  (this composition: IActivityExecutor)
        |
        v
Penghou.Hufu.Sandbox  ->  Gagamba.ExecutionRuntime  ->  native domain
```

Fuwen carries **intent, never authority**. The composition resolves the logical
execution profile to a trusted Hufu profile and authorizes; the plan can never
choose the executable, arguments, working directory, environment allow-list,
guarantee ceiling, authority request identity, grant revision, trusted profile
revision, or any provider handle.

## Fuwen side (neutral, versioned)

- `ActivityExecutionIntent(Profile, Required, Preferred)` and
  `ExecutionGuarantee(Capability, Minimum)` live on `ActivityNode`; the
  vocabulary is Fuwen-owned and names no provider, Hufu grant, or Gagamba type.
- It is a first-class field (not an ordinary argument), carried into the
  canonical plan and **execution fingerprint**, and requires the
  `fuwen-ir/v3-execution-intent` IR version. The descriptor/catalogue defines the
  allowed ceiling; the node requests within it; admission binds the exact
  request transitively through the execution fingerprint. Plans without an
  intent keep `fuwen-ir/v1` and their existing fingerprints.
- The Fuwen→Zhinu adapter carries the intent unchanged into
  `ActivityExecutionRequest.ExecutionIntent` and the request identity; the
  adapter does not interpret it.

## Composition rules (`Penghou.Hufu.Fuwen`)

- The logical profile name resolves to a trusted sandbox invocation; the plan
  never supplies `ProfileId`/`ProfileRevision`.
- Neutral capability ids map through an explicit, allow-listed translation
  table (`NeutralGuaranteeMap`); **unknown id fails closed**.
- Required and preferred guarantees preserve their distinction during mapping.
- Over the trusted profile ceiling ⇒ `RequirementNotAuthorized`.
- The host cannot provide a permitted guarantee ⇒ `GuaranteeUnavailable`.
- Retry preserves the Fuwen intent but receives fresh authorization through the
  frozen HZ/HG path (the authority request identity is derived from the
  attempt's Fuwen operation identity; a new attempt is a new operation).

## Audit correlation

`Fuwen PlanRevision → ActivityNode/execution fingerprint → Zhinu step
(attempt/revision) → Hufu AuthorizationRequestId → trusted
ProfileId/ProfileRevision → SandboxExecutionBinding → Gagamba negotiated
guarantees → durable outcome`. The composition's authority-request identity
carries the Fuwen operation key together with the resolved profile id/revision,
so the chain is reconstructable without reopening the plan.

## Verification

- `Penghou.Hufu.Fuwen.Tests` (8): allowed intent executes; unknown profile
  refused; unknown capability id fails closed; over-ceiling ⇒ PolicyRejected
  (`RequirementNotAuthorized`) with **no provider work**; permitted-but-
  unavailable ⇒ ProviderError (`GuaranteeUnavailable`); every attempt gets fresh,
  distinct authorization; the authority-request identity correlates the Fuwen
  operation key with the profile id/revision; the public request exposes no
  downstream-controlled authority (executable/environment/ceiling/…).
- End-to-end (1): a real Fuwen plan with a neutral intent is admitted, registered
  through `Penghou.Fuwen.Zhinu`, driven by `WorkflowEngine`, resolved by the
  composition, executed through the sandbox, and completes with the sandbox
  output while the real execution domain ran.

## Dependencies

`Penghou.Fuwen`, `Penghou.Fuwen.Compiler`, `Penghou.Fuwen.Zhinu`
(`0.1.0-preview.12`) and `Penghou.Zhinu`/`Penghou.Zhinu.Sqlite`
(`0.2.0-preview.2`), plus the local `Penghou.Hufu.Sandbox`. Gagamba stays frozen
at `0.1.0-preview.4` (preview.4 carries the macOS `not running` completion
fix proven by this consumer); Zhinu core and Fuwen core gained no
cross-dependency.

## First consumer

`samples/Hufu.SandboxRunner` is the first runnable consumer of this frozen
chain: one admitted plan (neutral intent `diagnostic.whoami`, requiring
`execution.unit-termination` at Partial) runs through a real Zhinu engine,
per-attempt pinned authorization, and a genuine Gagamba domain, printing one
JSON audit record. It adds no infrastructure and takes no Luban dependency.

## Default platform

After four independent consumers with zero architectural expansion, the
frozen chain is no longer awaiting validation: it is the default platform.
New work assumes the existing abstractions are sufficient until a real
feature proves otherwise. Consumer selection optimizes for usefulness
first — removing a real manual or debugging step, usable today, built
mostly from existing public surfaces, connecting capabilities into a more
complete workflow — and only then for whether a boundary is pressured.

Stated simply: Penghou's existing architecture is the default platform.
Build useful things on it. Change infrastructure only when a useful thing
proves it cannot be built correctly with what exists.

## Admitted-plan start (consumer #9)

`AdmittedPlanStarter` closes the operator workflow's `start` gap: the
smallest trustworthy way to create a Zhinu run from an admitted Fuwen plan.
It composes existing public surfaces only:

1. `WorkflowDefinitionDocument.LoadVerified` verifies the presented canonical
   bytes against the claimed execution fingerprint (integrity, not approval);
2. `WorkflowAdmissionService` re-admits the plan against the **host's own**
   trusted catalogue and policy, producing a fresh in-process receipt;
3. the fresh receipt's claims are compared, ordinally, to the presented
   claims (catalogue revision, resolved-descriptor fingerprint, policy
   revision, grant fingerprint) — any difference refuses;
4. `FuwenZhinuWorkflowFactory` registers the definition through the frozen
   Fuwen-Zhinu boundary (it independently re-verifies the definition
   fingerprint and binds the provider-runtime identity);
5. a Zhinu run is created with `PlanStartProvenance` metadata binding it to
   the execution fingerprint, plan revision, receipt fingerprint, catalogue
   revision, resolved-descriptor fingerprint, and policy/grant fingerprints.

Fail-closed, with stable reason codes: `integrity-failed`,
`admission-failed`, `catalogue-mismatch`, `descriptor-mismatch`,
`policy-mismatch`, `grant-mismatch`, `unsupported-node`, `missing-intent`,
`registration-failed`, `invalid-input`, `start-failed`. No fallback to a
"best available" catalogue revision.

### The security distinction this preserves

```text
plan                 declares intent and descriptor references
admission            proves that exact plan was accepted against trusted revisions
catalogue-owning host supplies the actual trusted activity definitions
Zhinu                receives the resulting executable workflow definition
```

The plan never becomes authority or executable definition by being
submitted: the host re-admits against its own catalogue and refuses on any
revision mismatch, and it never deserializes executable behavior from the
plan. The start operation is a **host** capability, not a generic CLI
command — a catalogue-free CLI cannot own trusted definitions, which is
exactly the boundary that kept `runs start` unbuilt. Starting returns a run
ID and provenance; execution belongs to workers. Started runs then flow
through the existing `wait`/`show`/`external-ops`/`evidence`/`cancel`/
`restart` operator surfaces unchanged.

The execution slice this host starts is activity-with-intent plus returns;
plans using context, inference, conditionals, fan-out, repetition,
checkpoints, or waits are refused before a run is created rather than
failing mid-execution.

## Non-claims

No filesystem authority, VFS/WhatIf, network controls, quotas, or richer
isolation. The composition is not packed or published.
