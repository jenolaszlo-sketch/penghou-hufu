# Biscuit integration qualification

Updated 2026-10-03. Local Windows x64 qualification covers the optional
registered adapter, corrected IO resource-read boundary and Luban read consumer.
Production authentication/custody, governed mutations, cross-platform/AOT Hufu,
adapter publication remains open; IO/Luban public adoption is qualified in [the public report](qualification/public-resource-packages.json).

## Current verified source and dependencies

The missing Hufu.IO project and old constructor mismatch identified on 2026-10-02
are reconciled. Hufu.IO and the read hosts use explicit provider composition;
Hufu.Luban retains exact semantic admission and resource/release projection.
The resource correction's selected contracts are in
[ADR 0003](../../Penghou/docs/decisions/0003-replaceable-resource-providers.md);
the [corrective ledger](../../Penghou/docs/resource-abstractions-corrective-plan.md)
owns RA-5B publication and RA-5C adoption.

BiscuitSharp 0.1.0-preview.2 is the exact unpublished artifact from CI 36979786333,
commit f89285702ebade4b7fe92e4bfb2f72080c8d72ab. Its nupkg SHA-256 is
c5f0c94aa14cf82b68239ed49314a3cf468780337432cdff89975beed6ead3b1.
The three corrected IO packages resolve as exact [0.1.0-preview.1] candidates;
their restored archive identities are in the
[source inventory](biscuit-integration-source-manifest.json).

The global cache archives were hashed, and BISCUITSHARP_NATIVE_PATH was unset.
The IO cache metadata names a local temporary candidate feed, not NuGet.org.
Normal source mapping alone does not revalidate an existing cache. A fresh
machine needs the exact candidate feed, or later separately qualified published
packages. No fresh isolated-cache or published-package adoption is claimed here.
Hufu still has no committed HEAD or release; source hashes identify its local
prototype rather than attest a published artifact.

## Executed conformance

"dotnet test Penghou.Hufu.slnx -c Release --no-restore" passed on net8.0 and
net10.0 after the final fixes, with zero failures/skips:

| Test project | Cases per framework |
| --- | ---: |
| Penghou.Hufu.Biscuit.Tests | 93 |
| Penghou.Hufu.Tests | 99 |
| Penghou.Hufu.IO.Tests | 19 |

Total: 422 executed cases across the two frameworks. The start worker is a
supporting process, not an extra conformance suite.

The 93 Biscuit cases include the original 64 adapter/profile/start cases,
16 real Local/Luban read cases, six direct Hufu.IO/Biscuit resource cases,
three validity/mapping cases and four typed ancestor-failure cases.

- Real native signing, current real Cedar/all-layer decisions and required
  disk WAL/FULL SQLite evidence in both stores.
- Quoted/escaped/newline/CR/tab/Unicode literals, malformed controls/UTF-16,
  bounded envelopes, registered canonical bytes, typed attenuation/subsets,
  parent/child revocation, key rotation/leases/retirement and exact start binding.
- Real Local reads and metadata, excluded traversal candidates, content/release
  checks, current mandatory denial after preflight, revocation and required
  evidence failure before read or final release.
- Unicode case aliases, direct junction rejection, junction omission during
  enumeration and deterministic directory-to-junction substitution after
  Permit but before provider qualification. No inspect-to-open race or general
  confinement claim is established. DOS short-name rejection is exercised only
  when this volume supplies an 8.3 alias; otherwise the long-name exclusion applies.
- Direct Hufu.IO composition with registered Biscuit verification: complete
  listing records a denied excluded-child metadata decision; credential
  revocation between pages prevents a second provider dispatch; failed core or
  registry evidence and forged invocations open zero provider sessions.
- Snapshot/grant validity changes or clock rewind during required recording
  invalidate Permit. The shared temporal check deliberately compares all grant
  active states in the snapshot, matching the existing SQLite evidence rule.
  Even an unrelated grant transition requires a fresh decision; this is a
  conservative availability tradeoff, not narrower permit-basis tracking.
- Mapping identity v2 binds the profile, registered typed-grant revision, policy
  and every capability mapping, including distinct WriteFile/fs.write.
  Missing/unavailable/denied/revoked ancestor lookup states preserve their typed
  failure instead of all becoming InvalidCredential.

An early Biscuit revocation rejection has no attributable decision/evidence.
The strict Hufu.IO projection reports AuthorizationUnavailable and returns no
data; the detailed Biscuit service reason remains AuthorityRevoked. The adapter
does not weaken mandatory evidence requirements to manufacture a denial record.

## Policy and host measurements

The [budget record](biscuit-budget-measurements.md) and its two JSON reports
measure the exact production fact/policy builder at 1/8/32 blocks and 1/4 workers.
The explicit local read-host budgets remain 2000 facts, 50 iterations, 100 ms;
complete checks remain sequential per database in the locally qualified profile.
Four-worker full-host availability is not qualified: one net8.0 preflight was
unavailable under contention. Native time budgets do not cover full host latency
and do not interrupt synchronous native calls.

## Remaining gates and claim limits

The read hosts perform fresh semantic/resource/release verification but do not
submit actual file I/O through BiscuitSqliteStartParticipant. Atomic start-to-read
ordering against revocation is not established. The SQL start participant remains
a fixture. These read-component results do not prove
an actual governed mutation host, physical-object start binding, durable terminal
outcomes, response-loss recovery, exactly-once effects or an OS sandbox.
Logical provider/path evidence hashes are not locked native-file identity.

Open: production presenter authentication, issuer/approval/delegation ceilings,
protected key/credential custody, production capacity/busy-timeout sizing,
backup/rollback protection, committed source/
release governance, full consumer conformance/API freeze, and Hufu AOT or other
operating systems. VFS-1 through VFS-10 remain deferred. Capture-only preview is
distinct from simulated execution; simulated receipts cannot authorize real apply.

The independently qualified BiscuitSharp wrapper does not qualify these Hufu
boundaries or publish the optional adapters.
