# Hufu completion review

Reviewed 2026-10-03 against the current checkout, public resource packages and
the existing authority/operation-start specifications. This review supersedes
older pending IO/Luban adoption entries; older test and probe records remain
historical evidence.

## Completed dependency gate

Hufu normal builds already select exact `[0.1.0-preview.1]` PackageReferences.
Hufu.IO consumes IO.Abstractions/Protocols; local test hosts consume IO.Local;
Hufu.Luban consumes Luban. Source composition is an explicit development opt-in.
No version or runtime code change was necessary to select the new publications.

`eng/Test-PublishedResourcePackages.ps1` restored all four resource packages from
NuGet.org into a fresh isolated cache, checked package asset types and actual
restore sources, and ran all three suites on both frameworks. The results are
101 core, 93 Biscuit and 19 IO cases per framework: **426 passed, zero failed or
skipped**. See [machine-readable evidence](qualification/public-resource-packages.json).
This closes Hufu's IO/Luban public-adoption gate (RA-5C).

Hufu.IO is implemented, including initial denial without provider access,
per-child checks, current revocation, required evidence, retained read sessions
and conditional write hooks. Tests of these components do not establish a
complete governed mutation host. Cedar evaluation, SQLite current snapshots,
revocation/evidence and a co-located operation-start prototype are also present.

## Remaining work in recommended order

| Priority | Gap and current evidence | Concrete completion criterion |
| --- | --- | --- |
| 1 | Reproducible repository/release boundary: current tree is untracked, has no remote and no `.github` CI. Hufu core and most adapters are non-packable; Hufu.IO alone enables packing. No frozen public API baseline is supplied. | Register the intended source repository, select the initial supported profile and package set, review/freeze its API and schemas, add Windows .NET 8/10 CI plus appropriate neutral builds, and verify isolated package consumption before publication. |
| 2 | Authority lifecycle is still host-supplied: authenticated contexts and bounded snapshots exist, but no complete issuer/approval/delegation service or protected credential custody is supplied. | Qualify one concrete trusted host with authenticated presenter/issuer binding, grant ceilings and attenuation, expiry, tenant isolation, revocation and required evidence. Bind approvals to exact requests; unknown cases fail closed. |
| 3 | Real protected mutation boundary is unfinished. The start transaction and locked-file binding contracts are components; read hosts and test journals do not supply production terminal outcomes/recovery. | Qualify one governed Luban single-patch host through complete-plan admission, actual locked-object binding, current authority/start transaction, durable Completed/NoMutation/Ambiguous outcomes, restart and response-loss recovery. Completed/uncertain effects must not be dispatched again. Keep batches a separate later gate. |
| 4 | Production capacity/availability is unqualified. Measurements support sequential complete preflight per database; one net8 four-worker full-host case returned AuthorizationUnavailable at the configured busy timeout. | Declare and measure a bounded supported concurrency profile, database ownership/busy timeout, end-to-end deadlines and failure behavior. Keep evidence mandatory; do not hide contention with permissive caches or automatic larger-budget retries. |

Priority 2 contains required host integration and a broader reusable lifecycle
service. The initial release must explicitly say which part it supplies rather
than require every future deployment model. A read-only initial Hufu profile can
defer priority 3 if its scope and claims explicitly exclude governed mutations;
a release claiming governed mutation support must complete that gate.

## Optional integrations and distribution follow-up

- Hufu.Luban deliberately supports only the default v1 profile. Its constructor
  rejects v2 before authority access; read and diff regressions prove this.
  Supporting v2 diff/merge/line/search admission is a useful next adapter feature,
  with versioned mappings and exact resource/budget checks. It does not block a
  v1-only Hufu release or reopen Luban's completed implementation.
- The optional Zhinu adapter still references sibling source. Choose its package
  migration separately; Hufu core does not depend on Zhinu.
- BiscuitSharp preview.2 still comes from the pinned unpublished artifact and
  local bootstrap feed. Its optional adapter needs a reproducible published
  dependency before claiming ordinary public-feed-only distribution. IO/Luban
  adoption is complete even though this separate dependency remains a candidate.
- Cross-platform/AOT Hufu qualification, workflow orchestration integrations,
  VFS simulation, richer observability and additional providers remain scoped
  follow-up work. They should not silently become gates for a bounded initial
  profile. Wrapper-level AOT/platform evidence does not qualify Hufu hosts.

## Handoff

Use this review, [roadmap](roadmap.md), [authority profile](current-authority-profile.md),
[operation-start profile](operation-start-profile.md) and
[Biscuit qualification](biscuit-integration-qualification.md) together. Preserve
the published IO/Luban references and evidence. Name the selected Hufu profile,
owner and acceptance tests before implementation; do not mistake package
adoption or a start fixture for production authority/host completion.
