# Registered Biscuit integration profile

Status: experimental local implementation, 2026-10-03. The optional adapter is
isolated in Penghou.Hufu.Biscuit and Penghou.Hufu.Biscuit.Sqlite; Hufu core does
not reference BiscuitSharp. Corrected Hufu.IO and real Local/Luban read consumers
are locally qualified on Windows x64 with candidate packages. Production host
and mutation guarantees, published IO adoption and adapter release remain open.
See the [current qualification](biscuit-integration-qualification.md).
## Purpose and decision rule

The adapter carries one registered Biscuit credential derived from one
immutable Hufu grant version. It does not replace current Hufu authority or
Cedar. A protected operation requires all of the following:

~~~text
Biscuit permits
AND the current Hufu snapshot and every Cedar authority layer permit
AND the trusted host binds the exact workload and resource
AND required verification evidence is durably recorded
AND the composed operation-start gate permits the exact start
~~~

VerifyAsync performs authorization preflight and returns a structured result
and verification identity. It does not dispatch an effect or produce a reusable
capability. A trusted broker must submit the exact request through the
co-located start gate and then enforce the provider boundary.

## Host and credential boundaries

The host supplies authentication, issuance and derivation approval, the current
authority snapshot, signing and verification keys, a registry, a Cedar
evaluator, exact resource binding, and required evidence recording. There is no
default permit host or default key. The host chooses the realm, workflow,
activity, audience, signer, evaluation budgets and provider. The adapter
validates these bindings on each use.

The first profile binds tenant, subject, run, workflow, revision, activity,
runtime fence, audience, grant, authority layer, grant version, realm and root
key ID. A credential represents one grant in one layer. Current authority,
mandatory denials and every Cedar layer remain independently required.
PatchFile maps to fs.patch; distinct WriteFile maps to fs.write. Mapping identity
v2 binds the fixed policy and the complete capability table. Neither mapping alone
qualifies a mutation provider.

The envelope takes an owned byte snapshot, exposes copies on export, and
redacts display output. BiscuitKeyRing is public and may be used by hosts, or replaced through IBiscuitKeyProvider. Hosts supply IBiscuitAuthorityHost. Signing uses an Ed25519 lease so rotation or
retirement cannot dispose a key during an active build. No service method
exports private-key material. Hosts remain responsible for protected key
storage, authentication and administrative retirement.

Attenuation accepts typed restrictions only. It retains subject and execution
bindings and proves the child grant is contained by the parent's effective
grant, including inherited exclusions, actions and validity. Unregistered
offline tokens and arbitrary Datalog are rejected.

## Verification, evidence and start

Verification authenticates the workload, verifies the envelope against the
one key selected by trusted realm and root-key ID, requires canonical bytes and
an exact registered token fingerprint, checks the complete derivation and
revocation chain, and evaluates current Hufu/Cedar decisions. Only after the
typed grant covers the exact request may the host bind its provider, object and
effect identity. Biscuit evaluation uses a fixed profile policy, centralized
bounded Datalog literals and explicit host-configured fact, iteration and time
limits. Only typed fact, iteration and time limit findings map to
AuthorizationBudgetExceeded; other runtime failures remain failures, not
permissions.

Both the ordinary Hufu decision store and the Biscuit registry must acknowledge
the verification evidence before VerifyAsync returns an authorized result.
These writes are sequential, not one cross-adapter transaction. A partial write
cannot produce an authorized result, and the start gate requires the matching
records before it can start.

BiscuitSqliteStartParticipant composes the Biscuit registry check with a
trusted runtime participant. It checks the exact request, decision identity,
resource start-binding hash, expected engine/mapping/evaluator identities,
grant validity, per-block revocations and realm-wide key retirement using the
same supplied SQLite writer transaction as the Hufu/Zhinu start. Those expected
identities are trusted composition inputs, never caller-selected values.
Revocation and key retirement use the same physical WAL database and immediate
writer ordering. The profile blocks new starts ordered after the tombstone
commits; a start committed earlier may finish. An AlreadyStarted receipt
never calls the runtime participant a second time.

This composition supplies a narrow exact-start check. It is not a resource
provider, filesystem sandbox, drain protocol, mutation transaction or
terminal-outcome recovery system. Providers still need to bind and check the
actual object used for I/O.

## Read composition and temporal validity

The host-selected BiscuitRequestAuthorizer implements IAuthorityRequestAuthorizer.
HufuLanguageAuthorizer retains exact compiled-document/invocation admission and
its concrete metadata/traversal/read/release checks. HufuResourceAuthorizer
supplies the separate neutral IO hook; HufuWorkspaceAccess rejects initial
failures before provider-session creation and retains the hook for discovered
children. Explicit Local composition lives in the test/host, not neutral Luban.
The tested read composition does not submit actual reads through the SQLite
start participant; it qualifies those read authorization components, not atomic
start-to-read ordering or the complete protected-operation boundary.

The generic current verifier, SQLite evidence rule and Biscuit verifier share
AuthoritySnapshot.HasUnchangedValidity. It checks snapshot expiry, clock rewind
and all grant active states across required asynchronous recording. An unrelated
grant transition may invalidate a Permit; a fresh decision is required. This
deliberate conservative rule does not authenticate state, bind a native object
or replace the actual resource/start boundary.

The [policy measurements](biscuit-budget-measurements.md) qualify the explicit
local read-host limits and expose four-worker full-preflight availability limits.
A production host owns authentication/custody, database-wide concurrency and
its overall cancellable deadline independently of native evaluation limits.
## Bounds and package source

The profile bounds envelopes and registered tokens to 64 KiB, Datalog source
to 64 KiB, derivation chains to 32 blocks and checks to 256. The SQLite
registry also has configured entry and serialized-metadata byte ceilings. SQLite page/index/WAL overhead is outside that byte accounting. These bounds do
not claim a native allocation limit, filesystem quota or production capacity
qualification.

The adapter references the real Penghou.Hufu.Cedar evaluator and the exact
unpublished BiscuitSharp 0.1.0-preview.2 package from CI run 36979786333,
source commit f89285702ebade4b7fe92e4bfb2f72080c8d72ab, package SHA-256
c5f0c94aa14cf82b68239ed49314a3cf468780337432cdff89975beed6ead3b1. The
repository's nuget.config maps BiscuitSharp only to the local
artifacts/biscuitsharp-feed; eng/Restore-BiscuitCandidate.ps1 verifies or
restores that pinned artifact. This is a development dependency pin, not a
published package source or stable restore contract. Nothing is published by
the restore script.

See the [qualification record](biscuit-integration-qualification.md) for
tested behavior and open gates. The governing design and wrapper handoff remain
in the sibling BiscuitSharp documentation.
