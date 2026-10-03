# ADR 0006: Govern shared resource interfaces beneath Luban

Status: Accepted design direction, 2026-10-01; Hufu adapter pending; Luban Windows read migration implemented.

Refined by [ADR 0007](0007-preview-resolution-commit-barrier.md): the earlier
advisory preview is static preflight. Authorized read-only resolution, immutable
resolved plans and whole-known-set admission before an executor commit barrier
are added stages. Live final checks and post-start partial-outcome limits remain.

## Decision

Use shared `Penghou.IO.Abstractions` contracts from the separate
[Penghou repository](https://github.com/jenolaszlo-sketch/penghou) for bounded
file, directory, and web operations. A host-selected integration supplies Hufu
authority to resource providers. Hufu core remains independent of Luban language
syntax and resource implementation details. Luban retains semantic effect
requests, compiler, catalogue and handlers; shared I/O does not replace exact
effect admission, approvals, durability, or output-release controls.

Carry authenticated host invocation, effect/request identity, scope ceilings,
limits, and applicable grant/fence bindings to the concrete resource boundary.
Checks cover reads, metadata, existence, candidate disclosure, writes, network
use, and both ends of copy/move. Source cannot select a checker or backend.
Interfaces/decorators do not prove filesystem target binding or confinement.

Preflight all pipeline nodes before execution using trusted requirements, known
targets, current authority, profile support, and limits. A known denied write
blocks upstream work. Preflight produces readiness information, not a grant or
durable permission token. Recheck current authority at effect start and each
resource access. Dynamic targets require bounded authorized discovery and fresh
checks; preflight cannot promise all future targets will pass. Keep intermediate
work lazy, cancellable and bounded. No implicit multi-effect transaction.

Allowed listing and reads cannot confer destination-write authority. `gc` is a
planned Luban read alias, not PowerShell. Pipeline values carry provenance, never
authority. Host composition requires the governed provider; an alternate local
implementation must never silently replace it. Standalone use is explicit and
does not claim Hufu enforcement. Neither project implements a sandbox.

## Delivery

The shared project now supplies a canonical request codec and qualified Windows
read-only Local provider, and Luban's read migration is implemented. Hufu policy
adapters, textual pipelines, mutations and web providers remain pending. Integration tests must prove early blocking for
known denials and final blocking for revoked or newly discovered denied targets.
No broad existing-consumer refactor or package publication is implied.

See [Hufu/Luban integration](../luban-integration.md) and
[Luban ADR 0003](../../../Penghou.Luban/docs/decisions/0003-shared-resource-interfaces.md).
