# Local Windows host services

Status: bounded local operator profile implemented; local .NET 8/10 qualification
is recorded in [the evidence](qualification/local-host-services.json). Remote CI
for this source change is pending. The host is a non-packable application under
[`samples/Hufu.LocalHost`](../samples/Hufu.LocalHost), consuming exact public
Hufu `0.1.0-preview.3` and IO/Luban `0.1.0-preview.1` packages. It introduces no
workflow-engine dependency, shared contract, package version or publication.

## Trust and identity

This is a concrete implementation of HOST-SERVICES for one local Windows
operator making governed typed calls. It uses the Windows token, not a supplied
username or caller-selected tenant, actor, context or issuer. Every store,
issuance and journal authorization rechecks the configured SID, rejects thread
impersonation, and revalidates namespace custody. Each command has a fresh
session identity; durable actor identity remains the tenant and operator SID.

The console is a **trusted operator management channel**. An agent integration
may expose only typed preview/apply calls with a host-bound operation ID. It
must not give the agent the management console, configuration, raw provider,
database, or arbitrary process execution. The same Windows SID cannot distinguish
a human from arbitrary code running as that account. Operator/worker modes are
trusted application routing, not independent OS identities or process isolation.
The profile trusts the operator, Windows, SYSTEM and local Administrators.
It does not claim to contain arbitrary code under those identities or prevent
their check/use races. This follows [ADR 0003](decisions/0003-local-first-authority-runtime.md).

The worker store policy permits current reads, mandatory decision recording and
starts for its bound operation. It denies publication, revocation and history
management. Worker journal access supports only the bound operation's approval
read, reservation, completion and internal replay check. Operator journal approval
and withdrawal additionally require an explicit one-shot scoped capability
bound to the exact command and admission. No permissive service is supplied.

## Issuance and custody

`init` allocates a previously absent dedicated directory beneath an existing
safe local NTFS parent, preferably a direct child of the operator's user profile.
It refuses existing roots, UNC/non-NTFS paths and reparse ancestors. It does not
repair arbitrary repository paths or change existing user ACLs.

The namespace separates `control/` from `workspace/`. Closed ACLs give FullControl
only to the configured SID, SYSTEM and Administrators. Object owners must belong
to that same trusted set; newly provisioned root directories explicitly use the
operator SID, while Windows can assign Administrators to files created by an
elevated token. Parent ownership and mutation permissions are checked, with the
OS volume root treated as trusted infrastructure. Reopen validates, without ACL
repair. Custody checks include SQLite sidecars and reject links, unexpected ACLs,
untrusted owners, more than 256 descendant entries or depth beyond 12.

`host.json` is protected host state. It records an independently defined local
operator ceiling, a narrower exact-target proposal, and exact publication
command/snapshot/sequence/expiry approval. Initialization is an explicit trusted
local bootstrap, not authority inferred from the worker request. The ceiling is
valid for one hour; operational authority is valid for 15 minutes. Publication
approval is valid for five minutes and is reloaded before and after operation
policy by the existing bounded issuance composition. Bootstrap consumes the
approval and erases its seed bytes after successful publication.

The proposal grants content read/patch/write/release only for one canonical
lowercase exact file, plus exact metadata and metadata-release scopes for its
ancestors. Metadata release does not grant ancestor content read. Files are
UTF-8, at most 1 MiB; targets have at most eight components and 256 characters.
Reserved Windows names, uppercase aliases and traversal are rejected.

One `host.lock` lease serializes all commands across processes, with no waiting
queue. A competing command fails closed. There are at most 64 protected drafts;
each control JSON record is at most 2 MiB. These are explicit local capacity
limits, not fleet-wide quotas or a general server scheduling system. Published
SQLite defaults also bound authority events to 10,000, decision entries to 100,000
and authority record bodies to 64 MiB; the patch journal allows 10,000 entries and
64 MiB of record bodies. Exhaustion fails closed. These are record-accounting
limits, not physical disk/WAL quotas.

## Operator flow

Build with `dotnet build samples/Hufu.LocalHost/Hufu.LocalHost.csproj -c Release`.
Run `dotnet samples/Hufu.LocalHost/bin/Release/net10.0-windows/Hufu.LocalHost.dll help`
for syntax; .NET 8 is also supported.

| Command | Channel and behavior |
| --- | --- |
| `init ROOT TARGET` | Operator; initial UTF-8 bytes on stdin; creates the private namespace and exact authority |
| `activate ROOT` | Operator; resumes/finalizes the original interrupted bootstrap |
| `preview ROOT OP OFFSET DELETE_LENGTH` | Worker; replacement UTF-8 bytes on stdin; captures without a proposed write and persists the frozen admission identity |
| `review ROOT OP` | Operator; shows complete JSON-escaped before/after content, versions, path and admission identity, at most 8 KiB per side |
| `approve ROOT OP EXACT_ADMISSION_ID [SECONDS]` | Operator; requires complete review generation and exact confirmation; expiry is 1â€“300 seconds, capped by authority expiry |
| `apply ROOT OP` | Worker; recaptures and rechecks exact approval, current authority, final object binding and durable start before dispatch |
| `inspect ROOT OP` | Operator; returns durable outcome state, without raw journal/evidence disclosure |
| `withdraw ROOT OP` | Operator; withdraws this exact approval |
| `revoke ROOT` | Operator; terminally revokes the configured authority |

Stdin preserves exact bytes; shell pipelines can add newlines that become part
of the content or replacement. Payloads do not belong in command-line arguments.
Ordinary preview results contain facts and digests, not file content. Complete
review is deliberately operator-only and JSON-escaped to avoid terminal control
characters. Generating review does not prove a human read it; the trusted operator's
exact hash confirmation is the approval decision. Oversized reviews cannot be
approved in this console profile and require another reviewed host UI.

Changing the file, patch payload or captured semantics requires a new preview and
operation identity. Existing drafts cannot be silently replaced. Approval intent
is persisted before journal append; a retry reuses its original command and
expiry instead of extending permission after response loss. Durable outcomes
block redispatch. New decision records retain exact evaluator/request/time facts
without replacement content or credentials. Protected drafts and review output
contain content and require operator custody; the sample has no automatic export
or retention cleanup. When the finite session expires or draft capacity is reached,
retain the old evidence and explicitly initialize another new namespace. Do not
delete a database to reset authority or recycle an operation identity.

## Recovery and limits

An interrupted bootstrap with protected state can resume only within its original
approval window and with absent or exact initial seed bytes. A partial/changed
seed is never overwritten. If publication committed but its response/finalization
was lost, `activate` can finalize the exact current sequence-one publication
while its operational authority is still active, without republishing or treating
history as new authority. Once that authority expires, initialize another new
namespace; finalization does not renew or reactivate it. Workers remain blocked
until bootstrap finalization. A crash before protected configuration exists needs
another new namespace; there is no arbitrary repair command.

The published [single-patch profile](single-patch-host.md) owns mutation start,
revocation ordering and terminal Completed/NoMutation/Ambiguous semantics.
`inspect` exposes recovery state. This console has no manual certainty override;
ambiguous outcomes require operator investigation and a separately designed
reconciliation workflow. It does not promise filesystem/database atomicity,
exactly-once external effects, batches or revocation drain.

Run `dotnet test tests/Hufu.LocalHost.Tests/Hufu.LocalHost.Tests.csproj -c Release`
and `./eng/Test-LocalHost.ps1`. The latter uses disposable fixture content and
separate processes to verify denial, complete review, approval, write, reopen,
replay refusal and revocation on both frameworks. It is qualification automation,
not an automatic approval policy for real work. Cleanup is confined to its
generated GUID scope under UserProfile. Windows CI and publication validation run
both checks; Linux/macOS retain the portable package/journal suites.

This closes **HOST-SERVICES-LOCAL** for this bounded application. Remote CI,
product-host integration in Guyabano/Marang, external credentials, organizational
issuers, multiple operators and retention/management services remain separate
gates. Neither this sample nor interface types qualify those broader services.
