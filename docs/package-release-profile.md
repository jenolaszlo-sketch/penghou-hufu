# Initial Hufu package release profile

The reviewed release set is `Penghou.Hufu`, `Penghou.Hufu.Cedar`,
`Penghou.Hufu.Sqlite`, `Penghou.Hufu.IO`, `Penghou.Hufu.Luban` and
`Penghou.Hufu.Workflow`. Each targets .NET 8 and .NET 10. The checked-in version
is `0.1.0-preview.1`; all six are published together by the user from `main`.

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
packages restore exclusively from public NuGet dependencies. Luban v2 and the
older single-patch host/journal candidate are independently deferred under
[the reuse review](ha-0a-review.md).

Public API inventories are regenerated from this reviewed source and enforced
with PublicApiAnalyzers. Shipped inventories remain empty before initial
publication. Strict target-framework package validation, package contents and
dependency checks, isolated fresh-cache consumers and separate integration tests
qualify the candidate. Local checks do not establish production host identity,
approval custody, resource enforcement or cross-process admission capacity.

The core release includes [decision explanations](decision-explanations.md).
Core owns the immutable typed capture and mandatory disclosure policy; Cedar
captures one real evaluation. Fresh standalone consumers exercise both APIs and
redacted summary disclosure on both frameworks. CI also runs the explanation
security and Cedar capture tests on Linux, alongside the Windows full suite.

Core also includes [optional bounded telemetry](optional-telemetry.md). Fresh
package consumers exercise actual closed-category metric emission and preserve
fail-closed authorization after telemetry shutdown. Linux CI includes portable
telemetry tests; Windows retains full regression. No SDK/exporter package is added.

CI never publishes. The input-free **Publish to NuGet** workflow requires `main`,
repeats release checks for the selected commit, and publishes those same verified
artifacts. NuGet identity setup is repository-specific; the user's configured
trusted publishing identity and `NUGET_USER` secret in the `nuget` environment
are required before publication. Never republish a version with changed contents.

## Initial-release recovery

The first publication [run 37171589215](https://github.com/jenolaszlo-sketch/penghou-hufu/actions/runs/37171589215)
passed Windows and Ubuntu validation and uploaded Hufu core preview.1 plus its
symbols. It then stopped because the CLI automatically uploaded symbols alongside
the primary package and the workflow uploaded the same symbols a second time.
The remaining five packages were not reached.

Primary uploads now use `--no-symbols`; each symbol package is uploaded explicitly
once, after its hash check. Both uploads use `--skip-duplicate` for retryable 409
responses. Other failures still fail publication.

For `0.1.0-preview.1`, an input-free main dispatch downloads the original six-package
artifact from that pinned run/source `3023ddb3fb9d50e01db9a8887f15f02e90cde209`
and requalifies its package contents, standalone consumers and workflow integration.
Recovery cannot replace the already-published core or pair it with rebuilt symbols.
The artifact must remain available; recovery fails if it has expired. Future versions
use their own current, validated artifacts. Increment the version before changing
released code; the preview.1 recovery deliberately stays on its original release.
