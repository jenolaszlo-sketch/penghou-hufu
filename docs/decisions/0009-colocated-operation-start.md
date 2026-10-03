# ADR 0009: co-located Hufu/Zhinu operation starts

Status: Accepted for the experimental local SQLite profile, 2026-10-01.
Full governed mutation composition and terminal-outcome qualification remain open.

## Context

Sequential Hufu lookup/recording and Zhinu acquisition leave a gap in which
authority, runtime generation or the executing step can change. Process-local
locks cannot order other processes. Independent WAL databases cannot provide a
single crash-atomic commit for both records through ordinary sequential calls.

## Decision

Add an explicit shared-owner constructor to `SqliteAuthorityStore`. Its trusted
`ISqliteAuthorityDatabase` owns one physical disk database and verifies the other
schemas; Hufu initializes/validates only its own namespace. The existing dedicated
database constructor stays isolated and cannot construct the new start gate.

Use `Penghou.Hufu.Zhinu.Sqlite` as the composition project. Hufu and Zhinu cores
remain independent. The adapter uses the real `IZhinuSqliteDatabase` owner,
checks its supported live schema identity, and registers a closed runtime
participant with `SqliteAuthorityOperationStartGate`.

Hufu authenticates each full start intent, then opens one immediate transaction.
It checks current authority/expected sequence and exact required durable Permit
evidence. The registered participant validates the exact Zhinu generation,
revision, step, attempt, owner and live lease facts and performs the one
Requested-to-Running transition through that same connection/transaction.
Hufu writes its mandatory start record and rechecks authority time validity and
the participant's earliest runtime deadline before one commit.

The participant must perform bounded SQL only. It must not commit, open another
database, perform target I/O, or call external policy. A registered participant is
trusted host code, not an agent-selected callback. The shared profile rejects
attached databases, memory databases, and TEMP objects shadowing runtime tables.

Operation identity is stable and tenant-bound in a separate start-journal
namespace. An exact duplicate authenticates again and returns AlreadyStarted,
never Started or a new participant acquisition. Changed intent/profile conflicts.
A lost committed response therefore cannot become a second dispatch.

## Revocation and recovery

Choose **block new starts** semantics. Current-authority revocation, publication,
Zhinu generation changes and starts use the same physical database writer
serialization. Revocation winning that order blocks a fresh start; a start that
committed first may attempt its already-started operation. A file write is not
part of the SQLite transaction. This does not promise no I/O after revocation
acknowledgement, cancel an in-flight write, or drain earlier starts.

After commit, missing/uncertain terminal evidence requires reconciliation.
Running state, a start record or a target hash does not establish completion or
safe retry. The existing Zhinu journal remains runtime-owned; exact typed
completion/NoMutation/Ambiguous mapping and governed Luban host wiring are the
next delivery gate. Stronger drain-before-acknowledgement semantics require
tracking/reconciling every earlier operation and cannot be inferred from this
start transaction.

## Consequences

The profile requires a newly composed shared database or explicit reviewed
migration; it never auto-merges separate authority/runtime files or copies grants
from an old backup. Host authentication, tenant/run membership, issuer ceilings,
exact semantic approval, trusted locked-object facts and provider selection remain
required. The binding hash is identity, never approval or physical-object proof.

This is an optional experimental adapter, not a new scheduler, distributed
transaction, sandbox, complete authority lifecycle or production mutation host.
See the [profile](../operation-start-profile.md) and
[roadmap](../roadmap.md).
