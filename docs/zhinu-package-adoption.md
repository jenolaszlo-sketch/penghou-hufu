# Published Zhinu package adoption

Qualified 2026-10-03 on Windows with .NET 8 and .NET 10.
`Penghou.Hufu.Zhinu.Sqlite` now consumes exact
`Penghou.Zhinu` and `Penghou.Zhinu.Sqlite` `[0.1.0-preview.15]` packages.
The solution no longer includes sibling Zhinu source projects. Explicit
`-p:UseZhinuSource=true` remains available for development with a sibling
checkout; normal builds use packages.

`eng/Test-PublishedResourcePackages.ps1` copies only Hufu source into an isolated
directory and restores into a fresh cache. It verifies NuGet.org provenance and
archive hashes for Zhinu as well as IO/Luban, and rejects source assets for those
dependencies. All 426 cases passed, with none failed or skipped: 101 core,
93 Biscuit and 19 IO tests on each target framework. Core tests include actual
shared SQLite authority/runtime starts, revocation ordering, lease/generation
checks, replay and worker-process recovery tests.

See [machine-readable evidence](qualification/public-resource-packages-with-zhinu.json).
The earlier four-package evidence remains historical. BiscuitSharp preview.2
still uses its exact pinned unpublished artifact. This is dependency adoption
qualification, not Hufu publication or completion of the trusted production
approval/mutation host boundary. Hufu core remains independent of Zhinu.
