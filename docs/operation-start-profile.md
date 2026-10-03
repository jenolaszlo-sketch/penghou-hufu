# Co-located operation-start profile v1

Migration status, 2026-10-03: this is the **legacy experimental composition**.
The [neutral authority-extension plan](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-plan.md)
and [ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md) govern future
integration. Preserve this profile's qualified atomic-start guarantees while
ZA-5 determines retention or retirement. The proposed activity authorization
callback supplies workflow preflight, not this shared transaction. Do not extend
the current SQL coupling into the new `Penghou.Hufu.Workflow` translation adapter.

Status: Experimental implementation, updated 2026-10-02. Qualification is recorded in
[operation-start qualification](operation-start-qualification.md).
[ADR 0009](decisions/0009-colocated-operation-start.md) defines the selected order.
This is a start gate; complete governed Luban mutation hosts and typed terminal
outcome/recovery wiring remain pending.

## Composition and authority

The existing file-path `SqliteAuthorityStore` constructor remains a dedicated
authority database. The explicit `ISqliteAuthorityDatabase` constructor supports
a registered owner of one physical local SQLite file. A host must initialize and
authenticate that owner's other schemas. Hufu creates/verifies only `hufu_*`
tables, without changing the owner's global schema markers. No automatic database
merge, attached database, memory database or TEMP-table shadowing is supported.

`Penghou.Hufu.Zhinu.Sqlite` supplies the supported Zhinu owner and participant.
All Zhinu repositories and Hufu publication/revocation use that database owner.
The composition pins Zhinu schema version 5; older/future/corrupt owner
identity fails closed. WAL, FULL synchronous writes and bounded busy handling
apply to Hufu-owned connections; no throughput or power-loss hardware guarantee
is inferred from them. The initial owner profile requires pooling disabled.

`SqliteAuthorityOperationStartGate` registers one trusted participant at
construction. Agent data cannot choose a different participant. Core contracts
remain independent of Cedar, Zhinu, Luban and SQLite; the composition project
depends on the concrete adapters. These APIs remain experimental/unpackaged.

Every start reauthenticates the explicit host actor/session through
`IAuthorityStoreAuthorizer` with `StartOperation` and the complete immutable
start command. The gate must validate tenant/run membership, issuing rights,
semantic approval and provenance of the exact operation. No default permit gate
ships. A session identifier, a snapshot, a hash, or a stored decision confers no
authority by possession.

## Exact intent and runtime binding

The generic start command binds actor, stable operation identity, canonical
authority request, required decision command, expected authority sequence, and
bounded binding JSON plus SHA-256 identity. The earlier decision must be a
durably recorded Permit by the same authenticated actor identity for the exact
request and immutable current snapshot sequence. Unknown/malformed JSON,
conflicting identity, stale/current-context mismatch and missing evidence block
start. Native evaluator capture and issuer ceilings remain explicit host gates.

The Zhinu participant uses a closed versioned patch binding, retaining the
compiled document/node/plan identities, invocation, workspace/path, concrete
request identity, writer/namespace profile, locked object identity, before/after
versions and lengths, and exact Zhinu run/generation/step/revision/attempt/owners.
Source spelling and arbitrary caller JSON cannot select a wider effect. The
initial profile supports only the qualified HostControlled patch writer.
Provider and writer must both be `local-windows-ntfs-controlled-patch-v1`;
the canonical binding is limited to 24 KiB of UTF-8 bytes.

The host must derive these facts from a qualified locked-object provider and
trusted admitted plan. The adapter validates consistency; it cannot prove that
an arbitrary caller actually owns that native file handle or has approved the
complete plan. It performs no file I/O and is not yet an `IPatchExecutionHost` or
`IBatchPatchExecutionHost` implementation.

The same SQLite transaction checks an active workflow generation and exact plan
revision/fingerprint; running run/step; current and captured lease generation;
step identity/key/revision/attempt; worker ownership; and live run/step leases.
Quiescing, superseded, unbound legacy and mismatched states are not silently
accepted by this initial profile. The requested external operation must match
the registered run/step/provider, use the `Abandon` recovery intent, and can
transition only once to Running. Lease renewal requires a fresh exact binding.

## Transaction and replay

One immediate writer transaction validates Hufu state, acquires the runtime
handle, and inserts required Hufu start evidence. Authority snapshot expiry and
grant validity are rechecked; the participant supplies the earliest runtime
lease deadline, which is rechecked before commit. A failed check, capacity
limit, cancelled precommit call, or insert failure rolls back runtime acquisition
and start evidence together. No partially visible start is usable.

The start journal has separate tenant/operation identities from publication,
revocation and decision commands. It stores the exact binding/profile, actor and
original session, authority/evidence references and start time with a strict
bounded hashed codec. Exact retry by the same freshly authenticated actor
returns AlreadyStarted and never re-invokes the participant. Different intent or
participant profile returns Conflict. Existing receipt inspection is historical,
even when authority or runtime state subsequently changes.

Start entries/body bytes are separately capped using the configured
`MaxDecisionEntries` and `MaxStoredBytes`; they are not counted as new authority
events or evaluator decisions. These are encoded-ledger bounds, not filesystem
quotas. Counter/body-size reconciliation detects qualified inconsistency cases;
there is no silent pruning, external rollback anchor or administrator-proof
attestation. Production retention, revocation reserves and scale qualification
remain open.

Cancellation or response loss around commit can leave the outcome unknown to
the caller. Retry inspects durable start evidence and must never become a fresh
dispatch. SQLite/native calls do not claim hard cancellation interruption.

## Chosen revocation order and next gate

Revocation winning the database writer order blocks a fresh start. A start
committed first may attempt its operation; already-started work can finish after
revocation acknowledgement. This is **block new starts**, with no drain guarantee.
The check/commit does not transact the filesystem write or guarantee an unexpired
lease/authority throughout subsequent I/O. The provider and host must enforce
the admitted narrower operation profile and reconcile post-start uncertainty.

The next gate connects this start boundary to a real Luban single-patch host,
trusted complete-plan admission, concrete resource checks and exact terminal
Completed/NoMutation/Ambiguous evidence. Only then may completed receipt recovery
skip an operation. Batch admission/draining are separate later gates. The local
HostControlled namespace and native alias/confinement limits continue to apply.
