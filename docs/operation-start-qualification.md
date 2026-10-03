# Co-located operation-start qualification

Date: 2026-10-02. Status: Local qualification of the experimental
[operation-start profile](operation-start-profile.md) under
[ADR 0009](decisions/0009-colocated-operation-start.md).
This extends the earlier [store qualification](durable-authority-store-qualification.md).
The complete governed Luban mutation host and terminal-outcome recovery remain open.

Environment: Windows x64, .NET SDK 10.0.401, adjacent Hufu/Luban/Penghou/Zhinu
checkouts. SQLite is pinned to Microsoft.Data.Sqlite 10.0.10 and the runtime
adapter pins Zhinu schema 5. Cedar uses the published cached CedarSharp 1.0.0
package/native identity recorded in the [earlier qualification](authority-profile-qualification.md).
All five Hufu source projects remain `IsPackable=false`; the separate process
worker is a private test executable, not a production identity service.

## Results

| Suite, Release | .NET 8 | .NET 10 |
| --- | --- | --- |
| ZhinuOperationStartTests | 22 passed, 0 failed, 0 skipped | 22 passed, 0 failed, 0 skipped |
| Complete Penghou.Hufu.Tests | 95 passed, 0 failed, 0 skipped | 95 passed, 0 failed, 0 skipped |

The complete suite includes the earlier 73 snapshot/Cedar/read/store cases.
Compilation reported no warnings. The independent shared-provider, Luban and
Zhinu suites were not rerun because their implementation source was unchanged;
the Hufu runs built their actual referenced sibling projects and used real
Zhinu repositories and SQLite files.

Reproduce after restoring the solution:

```powershell
dotnet test tests/Penghou.Hufu.Tests/Penghou.Hufu.Tests.csproj -c Release --no-restore
dotnet test tests/Penghou.Hufu.Tests/Penghou.Hufu.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ZhinuOperationStartTests
```

## Observations

- The fixture creates and activates a real Zhinu workflow generation, claims
  run/step leases and registers a Requested patch handle. It records a Permit
  from the real Cedar evaluator for the exact current immutable authority
  snapshot. Its authorizer/evidence projection and plan/object facts are test
  fixtures; this does not qualify a production approving host or locked file.
- A start commits authority validation, the runtime Requested-to-Running
  transition and mandatory Hufu start evidence in one immediate writer transaction.
  Missing evidence, revoked authority, changed run fence/owner, step attempt/
  owner/revision, a Quiescing generation and an independently acquired Running
  handle cannot become a fresh governed start.
- Expiry during participant execution and a late receipt-insert abort roll back
  runtime acquisition. Removing the injected insert failure allows an exact
  retry to start once. Required start evidence is not optional logging.
- Exact replay authenticates again, returns AlreadyStarted and never calls the
  participant, including after revocation. A committed worker response deliberately
  suppressed before the parent sees it is recovered as a receipt, never dispatch.
- Separate OS processes race start and revocation on the same database. A committed
  start may precede revocation; revocation winning the writer order prevents a
  later start. A transient unavailable start may be retried with the exact identity.
  Coordination has bounded deadlines and cleans up child processes on failure.
- A wrong live Zhinu schema, attached database and TEMP-table shadow are rejected
  before participant acquisition. Exhausting the separate start ledger rolls back
  the runtime claim. Changed binding intent with a reused operation identity
  returns Conflict without invoking the participant again. A memory owner and
  dedicated authority store cannot supply this shared start profile.

## Source provenance

| Source | SHA-256 of locally qualified bytes |
| --- | --- |
| src/Penghou.Hufu/AuthorityOperationStart.cs | 30ab31ff9c27cfbc0f85465d917684ba1d0c62ea8316f9f40433bb0a69bcc372 |
| src/Penghou.Hufu/AuthorityStore.cs | b8b297761da505eb92083326ca5ca3f96bef2eb9b7ef34796b85f9db48fbda8f |
| src/Penghou.Hufu.Sqlite/SqliteAuthorityStore.cs | 3326a4b486a37e6408805cd6942aa20176218890e4322f070ac22be3c8b59f4d |
| src/Penghou.Hufu.Sqlite/StoreCodec.cs | 2551c491a7d5c47ef8566fe7ed90abea84b617f09be54d5914469dc4f77f97d0 |
| src/Penghou.Hufu.Sqlite/SqliteAuthorityOperationStartGate.cs | 1d440662fed8b756e40d4a1456450c7f046956536160d634bbff5822ad12ba33 |
| src/Penghou.Hufu.Zhinu.Sqlite/Penghou.Hufu.Zhinu.Sqlite.csproj | 3fac7b4cc1a82ae4d21f6a13fe1318a433cd87195615fe322a898367f988e38f |
| src/Penghou.Hufu.Zhinu.Sqlite/ZhinuSqliteAuthorityDatabase.cs | 51fad0890d1e229807a0e3f96eca8c11d4d706772eacf6c33e8a7660dac436ec |
| src/Penghou.Hufu.Zhinu.Sqlite/ZhinuPatchStartBinding.cs | cacd7ddef257a445b06bd37016598ee6f5bde0e23acba2f44ad52b164d876a0d |
| tests/Penghou.Hufu.Tests/Penghou.Hufu.Tests.csproj | 8e8776ca7f64fce0f1980d7e5283ec7bcca04d49560b05bce9057e1ae4a143bd |
| tests/Penghou.Hufu.Tests/ZhinuOperationStartTests.cs | 493591feb68520c556e036cf480049e185720bac471de82c159abd675b44e8ef |
| tests/Penghou.Hufu.StartWorker.Tests/Penghou.Hufu.StartWorker.Tests.csproj | a0a985ea928e142e871dd4f85fba262e4756e3602b86799005b46ece38de1bd6 |
| tests/Penghou.Hufu.StartWorker.Tests/Program.cs | 8625e9b81b2c51dd94ea8426797c6f62de40bec3e4960713734902d245384b39 |

These identify local source bytes, including line endings. No commit, package,
push, signed attestation or production deployment was produced by this slice.

## Remaining gates and limits

This proves a narrow shared-database start boundary, not a filesystem transaction
or completed mutation. No test dispatches a protected patch through a governed
Luban host. Complete-plan approval, trusted locked-object/provider facts,
per-resource checks, typed Completed/NoMutation/Ambiguous evidence and exact
receipt recovery must be connected and qualified before shipping that path.

The profile chooses block-new-starts semantics. Earlier committed operations may
attempt or finish after revocation acknowledgement. It does not drain active
operations, retract disclosed data, guarantee validity throughout later I/O or
provide multi-file atomicity. The HostControlled NTFS namespace and native
alias/confinement limits still apply.

The lost-response fixture suppresses a committed response; it does not inject
abrupt process termination at every instruction or qualify power-loss/storage
hardware. Native cancellation is not a hard interruption guarantee. Trusted host
authentication, issuer ceilings, tenant/run membership, approved plan provenance,
provider selection and same-physical-file ownership remain required.

Unknown/corrupt state fails closed, but source hashes/counters are not an
administrator-proof or rollback-resistant attestation. Ledger growth, revocation
capacity reserves, safe migration/backup/retention, production Cedar evidence,
non-Windows profiles and larger-scale contention remain future host gates.
