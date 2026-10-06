# Plan-authored execution intent (FZ-1)

Status: implementation slice, 2026-10-05. `Penghou.Hufu.Fuwen` composes a Fuwen
plan activity with the frozen HZ-1/HG-1 authority path. Not qualified, not
published, not a security boundary.

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
at `0.1.0-preview.3`; Zhinu core and Fuwen core gained no cross-dependency.

## Non-claims

No filesystem authority, VFS/WhatIf, network controls, quotas, or richer
isolation. The composition is not packed or published.
