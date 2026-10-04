# Governed single-patch host

`Penghou.Hufu.Luban.Sqlite` is an optional host adapter for Luban's separate
`SinglePatchExecutor`. It connects semantic admission, current resource checks,
exact approval, the provider's locked-object start and durable outcome recovery.
It depends on Hufu.Luban and Hufu.Sqlite; it has no workflow-engine dependency.
This adapter is published at preview.3. Local qualification is recorded in
[single-patch evidence](qualification/single-patch-host.json). Three-platform CI
and publication passed at `3206c44`; all seven exact public packages, fresh
NuGet-only consumers and 80 patch integration cases pass in
[the release checkpoint](qualification/single-patch-public-release.json). Local regression passes 962
cases, including 40 new cases per framework (15 portable journal and 25 actual
Windows writer cases). Seven package/symbol pairs and fresh package-only
patch/workflow consumers pass on both frameworks.

The bounded profile accepts one complete captured exact-target patch, at most
1 MiB before and after, with a canonical lowercase Windows workspace path. The
qualified writer is `local-windows-ntfs-controlled-write-v1`, supplied by the
published Penghou.IO.Local HostControlled provider. It is distinct from the
frozen legacy `local-windows-ntfs-controlled-patch-v1` workflow composition.
The new SQLite participant profile is `hufu-sqlite-single-patch-start-v1`.
The journal is portable; this concrete filesystem execution profile is Windows
NTFS only. Batch, arbitrary write, dynamic targets and native tools are outside
this host profile.

## Composition

Supply an authenticated `AuthorityStoreActor` and exact
`AuthenticatedAuthorityContext`, including revision and fence. Context and
session identifiers are facts, not credentials. Three required host services
have no permissive default:

- `IAuthorityStoreAuthorizer` authenticates every authority read, evidence write
  and start. Issuance additionally needs current issuer ceilings and exact
  publication approval; use the [issuance profile](core-admission-and-issuance.md).
- `IPatchJournalAuthorizer` authenticates each approval, revocation, reservation,
  completion, inspection and reconciliation, including replay. Approval policy
  sees the full frozen admission and must decide whether that effect is approved.
- `IHufuPatchDecisionEvidenceFactory` captures the exact request, evaluator
  decision and supplied actual evaluation instant into a uniquely identified
  `AuthorityDecisionRecord`. Missing or mismatched evidence prevents permission.

Construct both the authority store and host around the same registered journal
owner. A store using its independent file-path constructor cannot provide this
shared start transaction. These variables represent host-supplied authenticated
services and a current snapshot already published through the issuance gate:

```csharp
using Penghou.Hufu.Luban.Sqlite;
using Penghou.Hufu.Sqlite;
using Penghou.IO.Local;
using Penghou.Luban;
using Penghou.Luban.Execution;
using Penghou.Luban.Resolution;

var journal = new SqlitePatchOutcomeJournal(databasePath, journalPolicy, clock);
var store = new SqliteAuthorityStore(journal, storePolicy);
var host = new HufuSinglePatchHost(actor, context, store, evaluator, evidenceFactory, journal);
var provider = new LocalWorkspaceProvider(workspaceId, controlledRoot,
    LocalPatchNamespace.HostControlled);

// document is compiled from one bounded FilePatchStage.
var capture = await new PreviewRuntime(new WorkspaceReference(workspaceId.Value),
    provider, host.CreatePreviewAuthorizer(invocation, document))
    .WhatIfAsync(invocation, document, cancellationToken);
if (capture.Status != PreviewRunStatus.Succeeded) return;
var admission = HufuSinglePatchHost.PrepareAdmission(context, document,
    capture.Plan!, operationId);

// Host policy reviews the full frozen admission before recording exact approval.
var approval = await journal.ApproveAsync(new PatchApprovalCommand(approvalCommandId,
    actor, context, admission, approvalExpiresAt), cancellationToken);
if (approval.Status is not (PatchApprovalStatus.Recorded or PatchApprovalStatus.Replayed)) return;
var result = await new SinglePatchExecutor(new WorkspaceReference(workspaceId.Value),
    provider, host).ExecuteAsync(document, capture.Plan!, operationId, cancellationToken);
var outcome = await host.InspectAsync(admission, cancellationToken);
```

Capture uses current ReadFile, ancestor ReadMetadata and Release permission,
with durable decision evidence. It is read-only, does not require mutation rights,
and leaves `CanCommit` false. Approval is a separate exact, expiring record.
Replaying an expired or revoked approval never reactivates it. Operation IDs are
single-use within a tenant; a changed plan needs a fresh operation and approval.

## Start and revocation order

Admission checks the full immutable document/plan, materialized payload,
observation, exact write-request identity and active approval before opening a
writer. Each concrete provider callback rechecks current authority and approval.
Mutation requires separate ReadFile, PatchFile, WriteFile and Release permission,
plus ReadMetadata for the root, ancestors and target.

At the provider's exclusive retained file handle, the host validates the exact
native start: object identity, namespace profile, invocation, workspace/path,
request identity, before/after versions and byte lengths. All final permissions
must be evidenced against one snapshot sequence, identity and version. A durable
Reserved intent captures those facts. Reservation grants no dispatch permission.

The registered start gate then uses one immediate SQLite transaction to check
the current authority sequence/context and evidence, the exact Reserved intent,
the persisted approval's target facts, active approval and expiry. The same
transaction changes the patch outcome to Started and writes Hufu's generic start
evidence. Expiry is checked again before commit. Only a newly committed Started
receipt with matching journal evidence lets the provider perform its first write.
Replay and inspection never return a new dispatch permission.

Revocation and start use the same database writer order. A revocation that wins
blocks start; a start that wins may finish. This is block-new-starts semantics,
without a revocation-drain guarantee. The authenticated context binds revision
and fence; this adapter does not invent workflow leases or engine state.

## Outcomes and recovery

Filesystem mutation and SQLite completion are separate commits. The provider
reports the actual outcome while retaining the file handle. The host records it
before checking fresh Release permission for acknowledgement. If Release was
revoked after the write, the public result is ambiguous even when the journal
correctly retains Completed. Required evidence failure also prevents a successful
public response; it cannot undo a write that already happened.

| State | Meaning and allowed recovery |
| --- | --- |
| NotFound | No outcome reservation; current approval and authority are still required |
| Reserved | Intent recorded; no committed start has been acknowledged |
| Started | Start committed; a terminal outcome has not been recorded |
| Completed | Matching provider completion observed the proposed version |
| NoMutation | Matching provider completion reported no mutation, optionally with the original version |
| Ambiguous | Uncertain outcome; no automatic dispatch or retry |
| Unavailable | Authentication, storage or integrity checks failed; no permission or reliable absence claim |

Lost start responses may leave Started without a filesystem write. Lost
completion responses may leave Completed while the caller sees ambiguity.
Reopening the journal preserves both cases and cannot trigger a second dispatch.
`InspectAsync` requires independent history authorization. It returns evidence,
not permission. `ReconcileAmbiguousAsync` may conservatively move Reserved or
Started to Ambiguous; it cannot promote a target-hash match to Completed or
NoMutation. Its deterministic evidence identifier does not assert that a
Reserved operation committed a start. Late completion cannot overwrite a
reconciled or conflicting terminal outcome. Operator repair and any fresh
operation require an explicit host decision.

## Storage and host obligations

The owner verifies its closed `hp_*` schema, canonical bounded records, checksums
and capacity accounting on a dedicated disk-backed SQLite file. It rejects memory
or attached databases, TEMP shadowing, unknown journal schema and a participant
connection to another physical database. WAL, FULL synchronous mode, disabled
pooling and bounded busy handling apply; hardware power-loss durability is not
implied. Each approval/outcome row reserves 32 KiB so terminal growth cannot run
out of the already admitted journal byte budget. Default capacity is 10,000 rows
and 64 MiB reserved bytes, whichever is reached first. Approval plus reservation
uses two rows. Hufu's separate evidence/start budgets also apply. There is no
automatic pruning, archival or capacity expansion.

Checksums detect accidental corruption; they do not authenticate a database
against a privileged writer. Protect the database, WAL/SHM, paths and backups
from untrusted access, preferably outside the writable workspace. Protect the
workspace root, drive, mount and directory namespace as required by Local's
HostControlled profile. Avoid exposing raw journal records to agents; authorize
their disclosure separately. No payload bytes or bearer credentials are stored
in patch records, but paths, identities, versions and evidence remain sensitive.

Only the trusted host may compose the registered owner, store policies, evaluator
and real provider. Public low-level reservation/participant APIs are infrastructure
hooks, not an agent tool surface or evidence that a native handle is held.
Authentication, production issuer/approval services, filesystem confinement,
process containment and authenticated database custody remain host obligations.
The neutral workflow adapter remains preflight-only, and the frozen legacy
workflow start profile remains independently qualified.

## Concrete local host services

The non-packable [Windows host application](local-host-services.md) consumes this
published preview.3 adapter and supplies real operator-token authentication,
protected exact approval/issuance state, bounded namespace custody and separate
worker/management routing. Its local proof and pending remote CI are recorded
separately. This does not change the package or qualify broader product services.
