# ADR 0007: Admit resolved mutation sets before a commit barrier

Status: Accepted design direction, 2026-10-01; integration pending.

Adopt Luban's reviewed
[preview/commit design](../../../Penghou.Luban/docs/preview-resolution-commit-barrier.md)
and shared [provider contract](../../../Penghou/docs/preview-commit-contract.md).
Static preflight blocks known denied effects before target I/O. Authorized
bounded discovery then resolves concrete proposed mutations. Hufu evaluates
every known mutation before the executor releases a commit barrier.

Admission pins exact semantic IR/plan/segment identity, dependencies, targets,
payloads, preconditions, coverage, limits, modes and provider versions. Any
known denial blocks the batch before proposed mutation. Partial grants do not
authorize an allowed prefix. A changed or smaller plan needs explicit new
admission; opaque later effects cannot weaken known mutation checks.

Preview resolution is actual read/discovery I/O, distinct from Hufu's pure
counterfactual simulation. WhatIf does not invoke requested mutations or
opaque/lazy tools. Unresolved coverage and unsupported guarantees remain
explicit; denial or unavailable authority never falls back to lazy execution.

The host/executor owns the barrier; Hufu owns authority and decision evidence.
Zhinu or another host owns durable execution. Every actual access still checks
current grants, revision/fence, exact resource and preconditions. The barrier
is not an atomic mutation transaction: post-start failure/revocation can leave
partial outcomes requiring receipts and reconciliation. Restart revalidates
old previews; it cannot reuse them as current authority.

This refines [ADR 0006](0006-shared-resource-boundary.md). All adapters, plan
resolution and barrier execution remain pending; Hufu does not implement a
second compiler, discovery executor or sandbox.
