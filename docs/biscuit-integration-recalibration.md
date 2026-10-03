# Biscuit integration recalibration

Updated 2026-10-03. The 2026-10-02 missing-project/constructor discrepancy is
resolved: Hufu.IO is present and read hosts use explicit provider composition.
Do not restart RA-1 design or reproduce the completed migration.

The selected [resource ADR](../../Penghou/docs/decisions/0003-replaceable-resource-providers.md)
and [corrective ledger](../../Penghou/docs/resource-abstractions-corrective-plan.md)
govern RA-1 through RA-5. Hufu.IO owns resource interception, Hufu.Luban retains
semantic admission, IO.Protocols owns the shared codec/profile and Local owns
physical access. Preserve discovered-resource and locked mutation-start hooks.
VFS remains deferred.

## Current evidence

- BiscuitKeyRing is public with explicit ownership and signing leases.
- Asynchronous Biscuit verification composes with Hufu.Luban and Hufu.IO.
- 93 Biscuit, 101 existing Hufu and 19 IO cases pass on each of net8.0/net10.0,
  with no failures/skips: 426 total executed cases. Native Biscuit, current real
  Cedar, both disk SQLite evidence stores and real Local reads are exercised.
- Grant/snapshot validity across recording is shared with the existing SQLite
  rule; mapping v2 includes the complete capability table; ancestor registry
  failure propagation preserves typed unavailability.
- Both frameworks have fixed-policy and complete-host measurements at 1/8/32
  blocks and 1/4 workers. Four-worker complete-host availability remains open;
  the qualified local read composition is sequential per shared database.
- BiscuitSharp uses exact qualified but unpublished preview.2. Corrected IO
  uses exact local preview.1 candidates. Restored archive hashes and local/sibling
  source hashes identify the tested inputs; Hufu still has no committed HEAD.

See [qualification](biscuit-integration-qualification.md),
[measurements](biscuit-budget-measurements.md) and the
[source inventory](biscuit-integration-source-manifest.json).

## Next delivery

1. Coordinate RA-5B corrected IO publication and RA-5C published-package
   adoption with the resource delivery owner; candidate-cache tests are
   intermediate. Requalify the actual published artifacts and refresh identities.
2. Supply/qualify the concrete host's presenter authentication, issuance/
   approval ceilings, credential/key custody, database concurrency and overall
   timeout/capacity profile. Public keyring visibility is a utility, not custody.
3. Complete supported consumer conformance and API/source review before adapter
   freeze or publication. Hufu has no repository HEAD/remote release identity.
4. Qualify the actual governed mutation host, exact physical-object start
   binding, required terminal outcomes and recovery separately. The SQLite start
   participant remains a fixture; read qualification does not close that gate.

No production VFS, sandbox, drain guarantee, multi-file atomicity, exactly-once
effects or cross-platform/AOT Hufu support is claimed. Capture-only preview and
simulated execution remain distinct; simulated receipts cannot authorize real apply.

## Historical review

The initial 2026-10-02 review found that the resource ledger's claimed Hufu.IO
source/tests were absent from this checkout. A fresh source build failed on both
frameworks because read hosts retained the removed physical-root constructors.
Earlier 63/95 and later 80/95/19 counts describe those intermediate snapshots.
The migrated source and final 93/99/19 passes above supersede those findings.
BiscuitSharp wrapper qualification at f89285702ebade4b7fe92e4bfb2f72080c8d72ab /
CI 36979786333 is independent and unchanged; preview.2 remains unpublished.

Current exact-package and isolated consumer evidence is recorded in [resource package adoption](resource-package-adoption.md). Public-feed qualification remains pending the producer releases.
