# ADR 0008: current authority transactions and separate decision evidence

Status: Accepted for the initial experimental implementation, 2026-10-01.
Broader approval/delegation and runtime start semantics remain design gates.

## Context

The current read authorizer requires fresh snapshots and attributable evidence.
An in-memory host fixture cannot prove persistence, concurrent publication,
revocation, restart, or that replay cannot restore an older grant. Cedar evaluates
trusted facts; it supplies neither a current-authority database nor identity.

## Decision

Implement optional `Penghou.Hufu.Sqlite` behind neutral `IAuthorityStore`.
Use a dedicated database, one current tenant/subject/run slot, immutable
sequence events, complete frozen snapshots, and terminal revocation tombstones.
Publish/revoke use expected-sequence immediate transactions. A context can
advance under the host gate, but a previously advanced context cannot return.
Fresh execution after revocation requires a new run.

Require host authentication and issuer/read/evidence policy on every operation,
including exact replay. Persist the gate-verified actor/session. Tenant-wide
command identities bind intent and actor identity; a newly authenticated session
of that same actor may retry without rewriting original provenance.

Keep decision receipts separate from authority sequences. Capture the actual
evaluation instant and bounded trusted evaluator evidence through an explicit
host factory. Required permit recording and exact duplicate acknowledgement
check current context/snapshot, revocation, snapshot expiry, and grant validity
transitions. Historical permit inspection never updates authority. Persisting a
decision is not protected-operation start.

Use strict bounded codecs, hashes, application/schema/codec identity, consistency
checks, authorized bounded history, cancellation, and typed failures. Capacity
failure rolls back the whole attempted command. No silent pruning or fallback
to an older snapshot is allowed. Host secrets must stay outside durable records.

## Consequences and next gate

Hufu core remains independent of SQLite, Cedar, Luban, and Zhinu. An issuer gate
must prove the proposed snapshot stays within its trusted ceiling; constructing
records or knowing a session ID grants nothing. This slice provides publication,
not a full approval or delegation service.

Reopen/transaction tests qualify local behavior, not power-loss hardware, an
adversarial database administrator, or backup rollback resistance. Initial
integrity checks scan ledger counts/body sizes; larger-ledger performance needs
qualification before production deployment. Limits count encoded bodies and
entries, not total filesystem space including indexes, WAL, and backups.

The next host contract must serialize current authority and runtime revision/
fence truth with durable protected-operation start. A SQLite read or required
record followed by Zhinu AcquireAsync cannot provide atomic revocation ordering.
Define already-started operation and drain/acknowledgement semantics before
connecting governed single/batch mutation executors.

See the [implemented profile](../durable-authority-store.md),
[qualification](../durable-authority-store-qualification.md), and
[roadmap](../roadmap.md).

ADR 0009 now supplies that narrow [co-located SQLite start contract](0009-colocated-operation-start.md). Governed mutation hosts and exact terminal-outcome recovery remain open.
