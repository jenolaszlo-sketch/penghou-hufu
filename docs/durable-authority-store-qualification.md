# Durable authority store qualification

Date: 2026-10-01. Status: Local qualification of the initial optional SQLite
[current-state/evidence profile](durable-authority-store.md). The complete
authority lifecycle remained open at that checkpoint. Later start-gate work is
recorded in [operation-start qualification](operation-start-qualification.md);
this earlier source provenance and result count are retained as historical evidence.

Environment: Windows x64, .NET SDK 10.0.401, adjacent Hufu/Luban/Penghou
checkouts. `Microsoft.Data.Sqlite` is pinned to 10.0.10; core does not reference
SQLite. The Cedar consumer still uses published CedarSharp 1.0.0 and the native
identity recorded in the [earlier qualification](authority-profile-qualification.md).
All four Hufu projects remain unready for packaging (`IsPackable=false`).

## Results

| Suite | .NET 8 | .NET 10 |
| --- | --- | --- |
| SqliteAuthorityStoreTests within the full run | 29 passed, 0 failed, 0 skipped | 29 passed, 0 failed, 0 skipped |
| Complete Penghou.Hufu.Tests, Release | 73 passed, 0 failed, 0 skipped | 73 passed, 0 failed, 0 skipped |

The full suite includes the earlier 44 snapshot/Cedar/Luban tests. The new store
suite exercises a real file-backed SQLite store, not an in-memory substitute.
The independent shared-provider, Luban and Zhinu results in the earlier record
were not rerun here because their source was unchanged in this slice. Building
and testing Hufu rebuilt its actual sibling project dependencies.

Reproduce from the Hufu checkout after restoring its solution:

```powershell
dotnet test tests/Penghou.Hufu.Tests/Penghou.Hufu.Tests.csproj --configuration Release --no-restore -m:1
```

## Qualified observations

- A complete immutable snapshot persists across connection/store reopening.
  Exact replay returns original provenance, including original session, without
  advancing current state. A newly authenticated session of the same actor can
  retry; changed intent, versions, actor identity or command kind cannot reuse it.
- Concurrent independent store instances exercise actual SQLite expected-
  sequence ordering for competing publishers and publication versus revocation.
  One applies and the stale competitor conflicts; no partially installed grant
  becomes current.
- Revocation can tombstone an unissued run and is terminal. Older revision/fence
  reads cannot fall back to history, and advancing a context prevents its later
  reinstatement through a new command/version. Replaying old publication leaves
  the new head unchanged.
- Every valid operation, including replay/history/decision lookup, authenticates
  before database access. Cross-tenant callers, deny/unavailable/unknown gate
  results, actor mismatch and gate failure cannot create a usable store grant.
- Required permit evidence binds exact request, decision, actual captured
  evaluated instant and immutable snapshot sequence. Authority advancement,
  revocation, snapshot expiry and grant validity transitions block fresh or
  duplicate permit recording. A second time check immediately before commit or
  duplicate acknowledgement also rejects permits expiring during recording;
  a failed new write rolls back completely and a failed replay preserves its
  original audit receipt. Historical permits remain readable audit data;
  historical denial recording cannot update current authority.
- Invalid timestamps, malformed/oversized/duplicate-property evidence, conflicting
  decision identities, and event/decision/body capacity limits fail closed.
  Authorized history uses bounded exclusive cursors; cancellation propagates.
- A future/foreign database is rejected without being initialized as an empty
  authority store. Corrupt body/hash, rolled-back current pointer, deleted
  predecessor, and altered capacity counters yield Unavailable. Referenced
  command/version/context consistency is checked in the implementation; these
  checks are not a tamper-proof attestation.
- Early event-insert and late command-insert aborts roll back all attempted
  authority/head/index/counter changes. After removing the injected late failure,
  retrying the same command applies once at sequence one.
- A real Cedar/Luban/Windows Local read succeeds through the durable current
  source and required recorder. The test captures the original
  `EvaluateDetailed` result/instant rather than re-evaluating later. Exhausted
  decision capacity blocks the real read before protected results are released.
  Its authentication gate and evidence capture decorator are test fixtures,
  not shipped production identity/evidence services.

## Source provenance

| Source | SHA-256 of locally qualified bytes |
| --- | --- |
| src/Penghou.Hufu/AuthorityStore.cs | 383b114005287c2764590068544e9c29c8adc118c42ae6fa4cccd1f37bb9ee05 |
| src/Penghou.Hufu.Sqlite/SqliteAuthorityStore.cs | 1dee31f9e9336fb1ecc9d3191f39bcc0b37a0d144962fa1e5b238ed592f026c7 |
| src/Penghou.Hufu.Sqlite/StoreCodec.cs | cffa1321bbb2a6e4457a1f12eef23c17d3e10613f76b89c361dbfe3b202bda69 |
| tests/Penghou.Hufu.Tests/SqliteAuthorityStoreTests.cs | 8318e017bf4ba8f0361af4f5598f16fc8b480935a881b5147560a083b9f3300f |

These identify local source bytes; line endings affect them. No package, commit,
push or signed attestation was produced by this slice.

## Remaining gates

Qualification covers connection reopening and transactional failure injection,
not abrupt process termination, power loss, all filesystem/storage hardware,
non-Windows consumers, or rollback-resistant backups. Large-ledger performance,
capacity reserved for revocation, safe retention/export, and the full production
Cedar evidence format need host qualification. Authentication/issuer ceilings,
approvals/delegation, external IAM revocation, and live runtime-fence validation
are required host/lifecycle responsibilities.

Required decision recording precedes read dispatch/release, but does not
serialize protected I/O with revocation acknowledgement. At this earlier checkpoint, the next integration
must provide one authoritative runtime start gate with explicit already-started
work/draining semantics. Sequential Hufu lookup/recording and Zhinu acquisition
are insufficient. Governed batch/single mutations remain pending; the shared
HostControlled namespace and native provider limits still apply.
