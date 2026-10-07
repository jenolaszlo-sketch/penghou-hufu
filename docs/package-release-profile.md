# Hufu package release profile

Current release: seven packages at `0.1.0-preview.5`, carrying the corrected
additive derived-authority surface. It is compatible with the `preview.3`
baseline (`PackageValidationBaselineVersion` stays `0.1.0-preview.3`) and adds
`ParentGrantId`, `ApprovedDerivation`, `DerivationCommand`, and the optional
ancestor-liveness authorizer overload **without** changing the preview.3
constructors, `Deconstruct` overloads, or positional contract.

Published from `e207b29` (tag `v0.1.0-preview.5`) via
[publication run 37664251201](https://github.com/jenolaszlo-sketch/penghou-hufu/actions/runs/37664251201)
(validation green on Windows/Ubuntu/macOS, then all seven nupkg/snupkg pushed).
All seven published packages were verified to match the qualified CI artifacts
entry-by-entry (ignoring the NuGet signature), and a fresh-cache restore from
nuget.org confirms the additive surface: `AuthorityGrant` keeps the six-argument
constructor plus a `ParentGrantId` init property, and
`CurrentAuthorityRequestAuthorizer` keeps the four-argument constructor plus a
five-argument overload.

### Incident: preview.4 was a failed partial release — do not use it

`0.1.0-preview.4` is **not** a completed release:

- A publish dispatch built from `4b8baa3` (the commit *before* the additive
  fix) published **only `Penghou.Hufu`** at `0.1.0-preview.4`, then aborted on
  a publish-workflow argument-splatting bug (fixed separately in `b50e455`).
- That published package carried the **superseded positional derived-authority
  API** (the widened `AuthorityGrant`/`AuthorityIssuancePrincipal`/
  `AuthorityStoreAccessRequest` constructors), not the additive surface.
- The other six packages were never published at preview.4.
- NuGet versions are immutable, so the package cannot be overwritten. It was
  **not** adopted as the compatibility baseline and **must be unlisted**
  (pending: unlisting requires a nuget.org operator credential, which the CI
  trusted-publisher flow does not hold; it must be done manually via the
  package page or an API key).
- The strict verifier (`eng/Verify-HufuPublishedPackages.ps1`) refuses to
  publish preview.4 bytes that differ from what is already public, so the
  correct recovery was to bump the version (preview.5), not to revert the fix.

Forensic marker `partial-preview.4-publish` (annotated, non-release) points at
`4b8baa3`, the tree that produced the published preview.4 package. The release
tag `v0.1.0-preview.4` was deleted; do not recreate it.

The prior published release was seven packages at `0.1.0-preview.3`, including the optional
`Penghou.Hufu.Luban.Sqlite` [single-patch host](single-patch-host.md). Remote CI and
publication validation passed on Windows, Ubuntu and macOS at `3206c44`. Exact
public package contents match the publication artifacts apart from NuGet signatures;
fresh NuGet-only consumers and all 80 patch integration cases pass on .NET 8/10
in [the release checkpoint](qualification/single-patch-public-release.json).
Local evidence is [recorded separately](qualification/single-patch-host.json).
Preview.3 is immutable. The following preview.2 checkpoint remains historical evidence.

The published preview.2 set is `Penghou.Hufu`, `Penghou.Hufu.Cedar`,
`Penghou.Hufu.Sqlite`, `Penghou.Hufu.IO`, `Penghou.Hufu.Luban` and
`Penghou.Hufu.Workflow`. Each targets .NET 8 and .NET 10. All six preview.2
packages are published and indexed; [public release evidence](qualification/hufu-luban-v2-public-release.json)
records exact metadata/hashes, three-platform CI/publication and fresh consumers.
The original preview.1 record remains historical. Each future release needs a new
version and qualification, then user-controlled publication from `main`.

The workflow adapter references only Hufu and exact
`Penghou.Workflow.Abstractions` `0.1.0-preview.2`. Published Zhinu
`0.2.0-preview.1` is used by the separate workflow integration tests, not by any
release package. Core, Cedar and Biscuit remain independent of Zhinu. The
frozen legacy SQLite start adapter stays non-packable with exact preview.15
dependencies and a dedicated regression suite. It preserves a different atomic
start guarantee; asynchronous preflight does not replace it.

Biscuit and Biscuit.Sqlite remain experimental and outside this release set.
Their regression tests require the hash-pinned unpublished BiscuitSharp
preview.2 artifact through `eng/Restore-BiscuitCandidate.ps1`; released Hufu
packages restore exclusively from public NuGet dependencies. The explicit
[Luban v2 read/diff profile](luban-v2-authorization.md) is published in preview.2. The older staged single-patch proposal was replaced by the independent
[preview.3 host](single-patch-host.md). Its earlier deferral was recorded under
[the reuse review](ha-0a-review.md).

Public API inventories are regenerated from this reviewed source and enforced
with PublicApiAnalyzers. Published preview.1/2/3 APIs are now shipped;
future additions stay in the unshipped inventory until publication. Preview.2
was validated against preview.1; the preview.3 release validated the six existing
packages against preview.2 and introduced the seventh without a baseline.

## Public API evolution rule (earned at preview.4)

For positional public records, adding an **optional primary-constructor
parameter is still a compatibility break**: it replaces the primary
constructor and the compiler-generated `Deconstruct`, changing the binary and
source surface even though the new parameter has a default. Prefer **additive
body properties** when the new state does not need to redefine the positional
contract:

```csharp
public sealed record AuthorityGrant(/* original positional members */)
{
    public string? ParentGrantId { get; init; }   // additive, not positional
}
```

This preserves the previously shipped constructor and `Deconstruct`,
positional matching, `with { ... }`, and source/binary consumers, while the
new member stays additive (a body-level `init` property still participates in
record equality). For a construction-time dependency on a class, prefer an
**additional constructor overload** over widening the existing one, keeping
only the widest overload carrying optional parameters (PublicApiAnalyzers
RS0026/RS0027), so the original constructor signature survives.

Compatibility suppressions are a last resort. Each should mark a deliberate,
reasoned incompatibility, not a mechanical fix for avoidable signature drift;
removing the drift is preferred before publishing.
All seven packages now use published preview.3 for future compatibility validation. Strict target-framework package validation, package contents and
dependency checks, isolated fresh-cache consumers and separate integration tests
qualify the candidate. Local checks do not establish production host identity,
approval custody, resource enforcement or cross-process admission capacity.

The core release includes [decision explanations](decision-explanations.md).
Core owns the immutable typed capture and mandatory disclosure policy; Cedar
captures one real evaluation. Fresh standalone consumers exercise both APIs and
redacted summary disclosure on both frameworks. CI also runs the explanation
security and Cedar capture tests on Linux and macOS, alongside the Windows full suite.

Core also includes [optional bounded telemetry](optional-telemetry.md). Fresh
package consumers exercise actual closed-category metric emission and preserve
fail-closed authorization after telemetry shutdown. Linux and macOS CI include portable
telemetry tests; Windows retains full regression. No SDK/exporter package is added.

CI never publishes. The input-free **Publish to NuGet** workflow requires `main`,
repeats release checks for the selected commit, and publishes those same verified
artifacts. NuGet identity setup is repository-specific; the user's configured
trusted publishing identity and `NUGET_USER` secret in the `nuget` environment
are required before publication. Never republish a version with changed contents.

## CI platform coverage

Both CI and publication validation run on Windows, Ubuntu and `macos-15`
(ARM64), with .NET 8 and .NET 10. Windows runs the full solution. Linux and
macOS run portable core admission, issuance, explanation, telemetry and
Luban v2 authority-mapping tests,
Workflow unit tests and integration against the published Zhinu runtime,
plus the portable SQLite patch journal suite.
All three platforms inspect the seven current release package/symbol pairs, run fresh
standalone package consumers and run package-only workflow and patch integration.
Windows also runs the non-packable [local host](local-host-services.md) suite and
separate-process smoke qualification against exact public preview.3 dependencies.
Its remote CI for this source addition is pending; it introduces no package or
version bump. Linux/macOS do not qualify the Windows token/ACL host profile.
Package-only patch tests exercise the actual controlled Windows writer on Windows;
Linux/macOS run journal approval/start/recovery against the candidate packages.
Publication waits for every validation job; it remains an input-free manual
dispatch from `main`.

macOS additionally runs native Cedar evaluation, portable SQLite authority-store
tests and the experimental Biscuit suite (including real Cedar/Biscuit evaluation,
registry evidence and atomic operation-start tests). It restores the same
hash-pinned BiscuitSharp candidate as Windows; Biscuit is still outside the
release set. An explicit architecture check rejects non-ARM64 macOS runners.
The two SQLite Local/Luban read cases and the Biscuit Local/Luban resource-read
classes stay in the Windows full suite because those providers use Windows
native profiles. This matrix does not qualify Intel Macs or a macOS Local/Luban
filesystem provider. A successful macOS run is required before claiming native
Hufu consumer qualification there. The three-platform run
[37174721639](https://github.com/jenolaszlo-sketch/penghou-hufu/actions/runs/37174721639)
succeeded at `a131216`; Windows needed one failed-job retry for an existing
timing-sensitive Biscuit concurrency case. This evidence qualifies that revision,
not subsequent candidates.

## Initial-release recovery (completed)

Recovery run
[37173078830](https://github.com/jenolaszlo-sketch/penghou-hufu/actions/runs/37173078830)
published all six original preview.1 packages and their symbols. Public-feed
inspection confirms their original `3023ddb` source metadata. The version check
below is retained for retries; preview.2 uses newly validated artifacts.


The first publication [run 37171589215](https://github.com/jenolaszlo-sketch/penghou-hufu/actions/runs/37171589215)
passed Windows and Ubuntu validation and uploaded Hufu core preview.1 plus its
symbols. It then stopped because the CLI automatically uploaded symbols alongside
the primary package and the workflow uploaded the same symbols a second time.
The remaining five packages were not reached.

Primary uploads now use `--no-symbols`; each symbol package is uploaded explicitly
once, after its hash check. Symbols use `--skip-duplicate` for retryable 409 responses. Existing primary
packages are skipped only after exact public-content verification; an unexpected
primary-package collision fails before symbols are sent.

For `0.1.0-preview.1`, an input-free main dispatch downloads the original six-package
artifact from that pinned run/source `3023ddb3fb9d50e01db9a8887f15f02e90cde209`
and requalifies its package contents, standalone consumers and workflow integration.
Recovery cannot replace the already-published core or pair it with rebuilt symbols.
The artifact must remain available; recovery fails if it has expired. Future versions
use their own current, validated artifacts. Increment the version before changing
released code; the preview.1 recovery deliberately stays on its original release.

## Preview.3 symbols recovery (2026-10-05)

The first preview.3 publication, run 37197831939 at `3206c44`, is the immutable
release. A later dispatch at `cabb1c9` rebuilt preview.3, skipped the existing
primary packages and uploaded different PDBs. NuGet subsequently rejected the
symbols because their identities did not match the original DLLs. Successful
CLI upload does not establish asynchronous symbol-server validation success.

The original seven package/symbol pairs are still available as
`release-packages-3206c4447add6ef0821fb70f118b638598d9aac0` in
[the original publication](https://github.com/jenolaszlo-sketch/penghou-hufu/actions/runs/37197831939).
All seven original package contents match the public NuGet packages, excluding
NuGet's signature entry. All 14 original portable PDB identities and SHA-256
checksums match those DLLs. The later rebuilt core symbols reproduce the rejection
against the public DLLs.

An input-free **Publish to NuGet** dispatch from `main` now recovers the original
preview.3 artifact, requalifies it, compares its entries to public NuGet, skips
the already-published primary packages and uploads only their original matching
symbols. The user controls this dispatch. No version bump or package-code change
is required for restoring those symbols. If original artifacts expire, fail closed;
use a newly qualified version rather than guess or rebuild old symbols.

`Verify-HufuPackageSymbols.ps1` checks every PDB against its DLL's CodeView
identity and portable PDB checksum, and rejects extra or missing PDBs.
`Test-HufuSymbolValidation.ps1` exercises correct, swapped-framework, extra, missing and
altered PDB fixtures during release packing. `Verify-HufuPublishedPackages.ps1`
checks immutable public contents and validates symbols against public DLLs before
any upload. Publication is serialized, and primary-package conflicts do not use
`--skip-duplicate` to proceed with unverified symbols. Future changed release
contents still need a new version. This tooling change does not claim NuGet has
accepted the recovered symbols; confirm its asynchronous validation after dispatch.

## Preview.4 candidate (prepared, not yet published)

`Directory.Build.props` is at `0.1.0-preview.4`; publication remains the separate
manual `main` dispatch. Preview.4 adds first-class derived authority: child grants
with parent lineage, a Hufu-owned containment proof, an atomic derive-and-publish
store operation, an exact-tuple delegability approval (`DerivedAuthorityApproval`)
behind a new `AuthorityStoreOperation.Derive`, and ancestor-liveness gating at
admission. The child's effectiveness depends on ancestor liveness at use time;
delegability is an issuance-time right. See
[ADR 0012](decisions/0012-derived-authority-lineage.md) and
[ADR 0013](decisions/0013-delegability-of-derived-authority.md).

The feature extends the primary constructors of `AuthorityGrant`,
`AuthorityIssuancePrincipal`, `AuthorityStoreAccessRequest` and
`CurrentAuthorityRequestAuthorizer` (and their `Deconstruct` methods). Package
validation against the published preview.3 baseline reports these as intentional
preview breaking changes, recorded in
`src/Penghou.Hufu/CompatibilitySuppressions.xml` (seven members across both
target frameworks). No public API is removed; the change is additive but replaces
the previous record constructors.

Local validation: all seven packages pack against the preview.3 baseline, and a
fresh package-only consumer exercises the full lifecycle — derive a child, read
allowed in scope, read denied out of scope, revoke the parent, read denied as
ancestor-revoked, and issuance history preserved. The three-platform CI plus
symbol, package-set, and fresh-consumer qualification run in the manual publish
workflow before any upload.
