# Execution-lifecycle authority

Status: implemented 2026-10-12 (AE-1.2), local qualification. This is generic
Hufu authority state, distinct from terminal grant revocation and from
visibility (Observe/Cancel/Intervene) authority. It does not create new
generation authority, does not change workflow contracts, and does not
generalize lifecycle gating to root-only profiles. The frozen derived-authority
rule is in the [ADR 0014 amendment](decisions/0014-authority-delegation-enforcement.md#amendment-2026-10-12-ae-10-current-derived-authority-use-time-rule).

## Ownership and flow

Workflow events are one source of authenticated lifecycle facts, not the
definition of lifecycle authority:

```text
Workflow event (adapter vocabulary)
        |
        v
Hufu.Workflow translator  -- authenticate + translate
        |
        v
ExecutionLifecycleTransition (neutral Hufu authority state)
        |
        v
Hufu core store/gate
        |
        v
qualified derived-authority use
```

| Layer | Owns |
| --- | --- |
| `Penghou.Hufu` | Generic lifecycle binding/state/transition/receipt types, `IExecutionLifecycleStore`, `IExecutionLifecycleGate`, states, ordering/replay semantics, fail-closed consumption rules |
| `Penghou.Hufu.Sqlite` | Durable, atomic, idempotent transition application, ordering, transition-identity replay, durable receipts and reopen/recovery |
| `Penghou.Hufu.Workflow` | `WorkflowAuthorityEvent`, `IWorkflowAuthorityEventTrustSource`, and the translator that authenticates a runtime event and maps it to a neutral transition |

`WorkflowAuthorityEvent` and its kinds stay in the adapter. The store persists
neutral transitions, never workflow events; the authenticated source event may
appear only as opaque `SourceEvidence`. No workflow contract changed and no
second workflow event store was introduced.

## State model

```text
Active
  |
  +-- Suspend --> Suspended
  |                 |
  |                 +-- Resume --> Active
  |
  +-- Cancel ------> Terminal
  +-- Complete ----> Terminal
  +-- Supersede ---> Terminal
```

- **Suspend** denies new protected uses and preserves the same
  generation/binding.
- **Resume** reopens use of that same binding only after fresh current
  authorization. It never restores an old Permit, widens authority, or extends
  expiry.
- **Cancel / Complete / Supersede** are terminal for execution use. Cancel and
  Complete are not separate core states: both produce `Terminal` while the
  receipt retains the typed reason (`Cancelled`, `Completed`, `Superseded`,
  `Expired`).
- **New generation / superseded fence** retires the old binding. The event does
  not create authority for the new generation; that requires explicit new
  admitted authority and is a new binding.

## Binding, ordering and replay

`ExecutionLifecycleBinding` is the stable execution identity (tenant, subject,
run). Generation and fence are carried on transitions/receipts for ordering and
supersession, and a new generation is a new run/binding. Ordering uses
`ExpectedSequence` compare-and-set; replay uses `TransitionId`.

- **Duplicate** with the same transition identity and the same intent returns
  the original durable receipt (`Replayed`).
- Same transition identity with **different intent** is a `Conflict`.
- **Stale/out-of-order** `ExpectedSequence` is `Stale`.
- Invalid state transitions (resume without suspend, double suspend) are
  `Conflict`; any transition on a terminal binding is `Terminated`.
- A store failure is `Unavailable`, never a permit.

## Consumption

The lifecycle gate is consulted only in the qualified derived-authority
composition (`DerivedAuthorityRequestAuthorizer.Create`), only for known
derived grants, and only after the child and current-parent checks would
otherwise permit. Root-only behavior is unchanged.

| Gate verdict | Result |
| --- | --- |
| No lifecycle fact, or `Active` | Live (ordinary authorization continues) |
| `Suspended` | Deny `authority.lifecycle-suspended` |
| `Terminal` | Deny `authority.lifecycle-terminal` |
| Store unavailable | `Unavailable` (no usable Permit) |

The gate is an additional restriction, not a permission source. Absence of any
lifecycle fact is live; a suspended or terminal binding cannot yield a usable
Permit.

The gate also returns the bound state and the **sequence** it observed
(`ExecutionLifecycleCheckResult`; the store exposes the versioned
`ExecutionLifecycleStateRead`). The sequence is a freshness token, never
permission: after mandatory evidence recording, the qualified composition
re-reads the lifecycle sequence and fails closed with
`authority.read-set-superseded` if it moved. This closes the window where a
Suspend commits after the lifecycle check but before authorization completes;
see the [derived-authority freshness rule](current-authority-profile.md#derived-authority-freshness-ae-13).

## What this slice does not claim

- No new-generation or higher authority is created by any event.
- No atomic provider-I/O start ordering (that remains AE-2/provider work).
- No lifecycle gating of root-only authority profiles.
- No second workflow event store, and no workflow SQL in Hufu core.
- Cancellation/Intervene still require the current runtime handle/fence;
  visibility authorization alone cannot execute either action.
