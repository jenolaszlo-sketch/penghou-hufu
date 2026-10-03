# Luban stabilized API consumer qualification

Qualified 2026-10-03 against the unpublished Luban `0.1.0-preview.1` candidate.
This record is candidate-package evidence, not NuGet.org adoption.

- Hufu.Luban uses exact `[0.1.0-preview.1]` PackageReference by default; explicit
  `UseLubanSource=true`/`LubanRoot` supports development from source.
- The original Hufu checkout passes **101 tests on each of net8.0 and net10.0**,
  Release, with `UseLubanSource=false` and `UsePenghouSource=false` and a fresh
  candidate-package cache. Assets list Luban as `type: package`; its source
  project is absent from the restored dependency closure.
- The staged independent Hufu.IO suite passes **19 tests per framework**. These
  exercise the existing provider integration; no IO policy implementation changed.
- Existing v1 admission/resource/release behavior remains qualified. Both a
  concurrent v2-read test and the new v2-diff test are retained. Unsupported v2
  documents are rejected before authority lookup/evaluation/evidence recording.

Luban owns TextPatch/PatchLimits under `Penghou.Luban.Changes`; the unused legacy
FilePatchRequest is removed. Hufu had no usage of those patch types, so no Hufu
semantic adapter change was needed. Its existing explicit provider/authorizer
composition remains intact. The rest of the Hufu working tree and concurrent
publishing work are preserved.

Luban's public API baseline and strict cross-framework package validation are
recorded in its [API stability policy](../../Penghou.Luban/docs/api-stability.md).
See the [consumer impact guide](../../Penghou.Luban/docs/consumer-impact.md) for
producer/consumer responsibilities. Publication/public-feed restore remain RA-5B
and RA-5C gates. Hufu v2 policy support and durable host implementations are Hufu
work, not Luban release prerequisites.


Current update: the exact published IO/Luban packages are now qualified against
NuGet.org with a fresh cache. See [public-package evidence](qualification/public-resource-packages.json).
The earlier candidate observations above are retained as history.
