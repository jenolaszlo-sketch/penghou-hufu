# Workflow-authorized execution (HZ-1A)

Status: implementation slice, 2026-10-05. `Penghou.Hufu.Zhinu` composes a Zhinu
durable activity with `Penghou.Hufu.Sandbox` (and Gagamba transitively). Not
qualified, not published, not a security boundary. It is the first durable
consumer of `SandboxExecutionHost`.

## Dependency direction

```text
Zhinu durable activity (IWorkflowStep)
        |
        v
Penghou.Hufu.Zhinu   (this composition)
        |
        v
Penghou.Hufu.Sandbox  ->  Gagamba.ExecutionRuntime  ->  native provider
```

Zhinu core stays Hufu-free; Gagamba is frozen at `0.1.0-preview.3`. The
composition references `Penghou.Zhinu` and `Penghou.Hufu.Sandbox` and adds
nothing to either core.

## Durable lifecycle

`SandboxExecutionStep` builds the idempotency key
`sandbox:{StepExecutionId}:{Attempt}:{Revision}` and, **before any effect**,
registers a `WorkflowExternalOperation` (`Requested`) whose payload is the
correlation record:

```text
workflow/step/attempt/revision -> authorization request id, authority revision/fence
                               -> ProfileId/ProfileRevision
                               -> requested + negotiated guarantees
                               -> OwnerDeathCleanupSufficient
```

Then: authorize -> prepare -> re-authorize -> launch -> `Acquire` (`Running`)
-> `WaitForCompletionAsync` -> `Complete`/`Fail`. The window between the durable
`Requested` record and the durable `Running` record is the **uncertain crash
window**: a crash there may have launched a process, so recovery treats it as
uncertain external state, never as "nothing happened".

The correlation record persists **no provider handle** — no Gagamba
`ExecutionHandle`, PID, job object, cgroup path, or launchd label. Those are
ephemeral provider state, not durable authority.

## Recovery is capability-driven

`SandboxRecovery.Plan` classifies each non-terminal operation by the negotiated
`OwnerDeathCleanup` recorded for that execution (never by OS name):

- sufficient (native Full, e.g. Windows) -> `RetryFreshAttempt` (fresh
  authorization, fresh profile validation, fresh execution);
- insufficient (Linux/macOS native) -> `Abandon` (reconcile; never relaunch).

This applies to both `Requested` and `Running` states. It is the first genuine
consumer of the capability model: automatic crash recovery is offered exactly
where the platform guarantees the prior domain is gone after owner death.

## Cleanup, cancellation and revocation

Cleanup uses a **separate bounded token**; the workflow cancellation token stops
normal work but never aborts termination or completion (`Terminate` then
`WaitForCompletionAsync` on the cleanup token). Workflow cancellation throws
`OperationCanceledException`; **revocation is independent** — the host calls
`RevokeAndTerminateAsync`, the running step observes `Terminated`, records
`AuthorityRevoked`, and returns a `Revoked` outcome distinct from
`WorkflowCancelled`.

## Authorization layers stay separate

The step uses two distinct checks that are never collapsed: the sandbox host's
profile-scoped preflight authorization and its immediate pre-launch
re-authorization (TOCTOU). A prior admit is never reused as the launch
authority. A retry is new authority: fresh `AuthorizationRequestId`, fresh
preparation, fresh execution — no previous binding, authorization request, or
completion handle crosses an attempt boundary.

## Findings (consumer pressure on the ecosystem)

- **GP-3 Amendment 2 was required** (see `execution-domain` in Gagamba): the
  workflow needed to observe execution completion, which the frozen SPI lacked.
- **Zhinu's external-operation journal models `Cancelled` but exposes no
  transition to it.** HZ-1A therefore records cancellation/revocation as
  `Failed` with the reason in the durable operation evidence, rather than
  extending Zhinu core. A future `Cancelled` transition is a Zhinu decision.
- **The two authorization layers are distinct**; neither substitutes for the
  other.

## Acceptance coverage (`tests/Penghou.Hufu.Zhinu.Tests`)

normal completion; non-zero exit becomes execution failure with code preserved;
workflow cancellation terminates and awaits completion; authority revocation
terminates independently; cancellation vs revocation distinguishable in durable
evidence; fresh authorization per retry; profile/authority revisions correlated
durably; recovery follows `OwnerDeathCleanup` for `Requested` and `Running`;
terminal operations not recovered; no provider handle in the correlation. The
uncertain `Requested -> launched -> crash` window is treated by the same rule.

## Non-claims

No filesystem authority, VFS/WhatIf, network controls, quotas, constructed
owner-death, or richer isolation. The worked pass is the durable/crash window,
not new sandbox capability.
