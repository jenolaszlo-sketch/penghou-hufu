# Durable authority store profile v1

Status: Initial current-state publication/revocation and evidence slice
implemented and locally qualified, 2026-10-01. SQLite is optional and Hufu core
remains neutral. See [ADR 0008](decisions/0008-current-authority-store.md) and the
[qualification record](durable-authority-store-qualification.md).
This is not the complete approval/delegation lifecycle or a mutation-start host.

The store uses one current slot per tenant/subject/run. Immutable sequence events
retain each published snapshot or revocation, the original authenticated actor,
command, reason, and store time. Current revision/fence belongs to that slot:
looking up an older context never selects a historical grant. The current
pointer must match the latest event. A revoked slot is terminal; fresh execution
requires a new run identity. Revocation can tombstone an unissued run at expected
sequence zero. Context identities cannot be reinstated after advancement.

Every operation requires an explicit host `IAuthorityStoreAuthorizer`. It must
authenticate actor/session and validate tenant, issuer ceiling, runtime context,
read and evidence rights. The full immutable proposed snapshot or decision is
available to that gate. Identifiers are not bearer tokens. This store does not
implement identity services or infer issuer authority. Authenticated host gates
outside the SQLite transaction do not establish atomic external IAM revocation.

Publish/revoke use an immediate transaction with an expected store sequence;
clocks are not concurrency tokens. Snapshot versions are immutable per subject/
run. Exact command replay returns the original receipt without moving current
state; conflicting command reuse fails. Command identities are tenant-wide
across publication, revocation, and evidence. Intent binds actor identity, while
a fresh authenticated session may retry the same actor's exact command. The
original actor/session remains provenance and authorization runs again on replay.

Required decision records bind command, request, neutral decision, evaluated
instant, snapshot sequence, recorded instant, and bounded host-only JSON
evaluator evidence. Evidence cannot issue authority. Permit acknowledgement,
including duplicate recording, additionally checks current context/snapshot,
revocation, snapshot expiry, and unchanged grant validity set within the
recording transaction, repeated immediately before commit or duplicate
acknowledgement. Old
permits can still be inspected as history, but cannot satisfy the recorder after
revocation or state advancement. Non-permit evidence may reference an earlier
immutable snapshot. Malformed or inconsistent envelopes fail closed. The host
gate must validate trusted capture and reject unsupported evidence formats;
the store bounds opaque JSON rather than interpreting arbitrary evaluator formats.

The core source/recorder bindings have no permissive services. Source reads only
current context, with no historical fallback; it can return an expired current
snapshot for attributed denial, never permission. The recorder requires a host capture factory for the actual
evaluation instant and evidence; it does not invent these later. A Cedar host
can capture `EvaluateDetailed` and supply its native/schema/policy/entity/raw
diagnostics under its evidence format. Sensitive evidence requires host read and
retention controls. Raw secrets do not belong in these records.

Use a dedicated local SQLite file. The store recognizes its application/schema/
codec identity and rejects foreign or newer databases. SQLite transactions use
WAL, FULL synchronous durability, foreign keys, a bounded busy timeout, and no
connection pooling. Event/evidence bodies have hashes and strict bounded codecs;
these detect inconsistency, not a malicious administrator rewriting the database.
Restoring an old database backup requires host reconciliation with current
revocation/runtime facts; no external rollback-resistant anchor is supplied.

History pages are bounded and authorized separately. Failures have typed neutral
outcomes; cancellation propagates. No database error text or evaluator details
are exposed to the language runtime. The host owns the file/directory protection,
backup, key management, identity services, and evidence export.

The current API includes publication, revocation, current lookup, descending
history pages, required decision recording, and authorized decision lookup.
History accepts 1–32 entries and an exclusive sequence cursor, capped at 4 MiB
of encoded record bodies. Event/decision envelopes are capped at 2 MiB and
evaluator evidence at 256 KiB; duplicate JSON properties and malformed UTF-16
are rejected. Different formats require the host's explicit evidence policy.

Default growth limits are 10,000 authority events, 100,000 decisions, and 64 MiB
of encoded bodies. Configured maxima are 100,000 events, 1,000,000 decisions,
and 256 MiB. These are ledger limits, not total disk quotas: indexes, command
metadata, WAL and backups also occupy space. Capacity failure rolls back the
attempted command and does not acknowledge a failed revocation. Hosts must
provision sufficient capacity and handle refusal without reporting success or
silently pruning required evidence. A reserved revocation/retention policy is
a remaining production-host design gate.

Current-pointer/latest-event agreement, contiguous subject sequences, referenced
command/version/context identities, and ledger counter/body-size reconciliation
detect the qualified inconsistency cases. Initial integrity reconciliation
scans ledger counts/body lengths on each open. Larger-ledger throughput and
retention need qualification before production use. No hash makes a malicious
administrator's complete coherent rewrite or an old backup detectable.

The synchronous SQLite/native boundary does not claim hard cancellation
preemption. Cancellation is checked before dispatch and commit; a cancellation
or process failure around durable commit still requires exact-command replay
to discover the recorded outcome. A command receipt is never an operation token.
Time can advance after the final check; required recording does not establish
expiry or revocation ordering with subsequent provider I/O.

The dedicated profile orders authority state and required recording within one
store. An explicit shared-owner constructor and [co-located operation-start
profile](operation-start-profile.md) now add one transaction for current state,
actual Zhinu acquisition and start evidence. They choose block-new-starts
semantics, while earlier starts may finish. This does not serialize filesystem
I/O with revocation acknowledgement or provide draining; complete governed
mutation and terminal-outcome wiring remain open.
