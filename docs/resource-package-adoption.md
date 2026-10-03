# Resource package adoption

Updated 2026-10-03. Hufu's normal build selects exact IO.Abstractions,
IO.Protocols, IO.Local and Penghou.Luban 0.1.0-preview.1 packages. Luban source is
absent from the normal solution; explicit UseLubanSource=true and
UsePenghouSource=true retain source development.

The [isolated candidate report](qualification/candidate-resource-packages.json)
records 93 Biscuit, 101 core and 19 IO cases per framework on Windows:
426 executed cases, no failures/skips. It uses the validated IO release-run
artifacts and the finalized Luban API candidate in a fresh package cache, without
IO or Luban source checkouts or original build outputs. This is real runtime
integration through package boundaries; it is not public-feed adoption.

The [producer release checkpoint](../../Penghou/docs/resource-package-release-handoff.md)
and [Luban release handoff](../../Penghou.Luban/docs/release-handoff.md) track
publication history. Both IO and Luban are now published; the current public-feed report below supersedes the earlier candidate-only checkpoint.

## Published resource-package qualification — 2026-10-03

Normal builds consume exact IO.Abstractions, IO.Protocols, IO.Local and Luban
0.1.0-preview.1 packages. A fresh isolated restore verified all four came from
NuGet.org, with no IO or Luban source projects. All 426 cases pass: 101 core,
93 Biscuit and 19 IO on each of .NET 8 and .NET 10, with no failures or skips.
See [public-feed evidence](qualification/public-resource-packages.json) and
[resource package adoption](resource-package-adoption.md). RA-5C is complete for
these dependencies; Hufu itself is not released. Explicit source switches remain
available for development. Luban v2 is deliberately rejected before authority
access. See the [current completion review](completion-review.md) for open work.

## Reproduce public-feed proof

Both producers are published. Run from Hufu:

    ./eng/Test-PublishedResourcePackages.ps1

The script defaults to IO/Luban packages from NuGet.org. It copies Hufu and the
still-required Zhinu source into an isolated temporary snapshot, verifies the
exact Biscuit candidate, creates a fresh package cache, checks all four resource
package sources and assets, runs the three suites on both frameworks, and writes
qualification/public-resource-packages.json only on complete success.

An explicitly supplied CandidateFeedPath instead writes separate candidate-only
evidence; it must never be used to close RA-5C. A development source build requires
both source switches and is likewise not package adoption evidence.

Zhinu remains a source dependency. BiscuitSharp preview.2 remains the exact
qualified unpublished CI artifact; its local feed/bootstrap is preserved.
Hufu itself is still uncommitted and unpublished. Unsupported Luban v2 is rejected
before authority access, covered by both read and diff regressions.

Public adoption does not close Hufu's concrete host identity/issuance/custody,
capacity profile or governed mutation start/outcome recovery gates. Preserve
per-resource checks, required evidence and explicit provider guarantees.
