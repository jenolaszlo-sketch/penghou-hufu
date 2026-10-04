# Handoff: authority extension inversion

Updated 2026-10-04. Current gate status and evidence supersede the older resume prompts and snapshot inventories below.

## Current status

Penghou.Workflow.Abstractions `0.1.0-preview.2` is published. Zhinu
`0.2.0-preview.1` has passed its remote CI and publication gates; the exact
seven-package metadata and evidence are recorded in the [qualification record](qualification/zhinu-public-release.json).
The source is `2f02a2e91d87e6429fd17a3819308301ab91f17c`; CI run
[37137640422](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/37137640422)
and publication run
[37138352675](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/37138352675)
succeeded. WA-1/2/3, ZA-2, and ZA-6 are complete.

For Hufu, HA-0A/B and HA-1 are complete. The public adapter and its host boundary are described in [workflow-authorizer.md](workflow-authorizer.md). The earlier telemetry checkpoint passed 816 tests total, 408 per framework: core 210, Biscuit 93, IO 19, legacy integration 22, Workflow 52, and workflow integration 12. The bounded request telemetry slice adds 26 cases per framework; see [profile](optional-telemetry.md) and [current qualification](qualification/optional-telemetry.json). The earlier bounded explanation slice adds 32 cases per framework; see [profile](decision-explanations.md) and [explanation qualification](qualification/decision-explanations.json). The independently reviewed core admission/issuance profiles add 73 cases per framework; see [the profile](core-admission-and-issuance.md) and [earlier core evidence](qualification/core-hardening.json). The earlier 554/700/764-case records are preserved as historical evidence. HA-2 passed fresh-cache candidate-package qualification on both frameworks; HA-3 is complete for the six published `0.1.0-preview.1` packages; [public release and three-platform CI evidence](qualification/hufu-public-release.json) records the exact source and Windows retry. Preview.2 is now published with explicit [Luban v2 read/diff support](luban-v2-authorization.md); [public release evidence](qualification/hufu-luban-v2-public-release.json) confirms exact package metadata and successful three-platform CI/publication; no production host is shipped.

CI and release validation now also include `macos-15` ARM64 on .NET 8/10.
The [release profile](package-release-profile.md#ci-platform-coverage) defines
portable/native coverage and Windows-only resource exclusions. Record successful
remote macOS validation before claiming native Hufu consumer qualification.

## Reading order

1. [Penghou workflow contract ownership/publication plan](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-abstractions-plan.md), then the [canonical Zhinu plan](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-plan.md) and its current activity queue.
2. [Optional telemetry](optional-telemetry.md) and [current evidence](qualification/optional-telemetry.json), then [Decision explanations](decision-explanations.md) and [explanation evidence](qualification/decision-explanations.json), then [core admission and issuance](core-admission-and-issuance.md) and [earlier core qualification](qualification/core-hardening.json), then [ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md), the [Hufu roadmap](roadmap.md), and the [adapter boundary](workflow-authorizer.md).
3. Current execution/store semantics and the [resource baseline](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/resource-abstractions-architecture.md).
4. Existing operation-start profiles as regression evidence, not as the new default integration architecture.

## Published neutral contract checkpoint

`Penghou.Workflow.Abstractions` `0.1.0-preview.2` is published. Source commit
`5a76b7c` passed all seven CI jobs in [run 37115430526](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37115430526);
all four publication jobs passed in [run 37116694209](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37116694209).
Exact package contents match CI apart from the repository signature, and
fresh-cache NuGet-only consumers pass on .NET 8/10. See the [contract manual](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-authorization-contract.md),
[release handoff](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-package-release-handoff.md), and
[qualification record](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-public-package-qualification.json).

## Legacy compatibility boundary

Zhinu preview.15 remains the exact package baseline for the frozen
`Penghou.Hufu.Zhinu.Sqlite` adapter. It remains non-packed and its 22 regression
cases per framework now live in a dedicated legacy integration suite. The suite
is separate from the neutral workflow/core graph. Preserve the adapter and its
atomic mutation-start profile; any retirement or replacement remains ZA-5B and
requires evidence preserving that guarantee. The new workflow adapter is an
authorization preflight and does not claim to serialize final effects with
revocation.

## Historical review baseline: `.tmp/hufu-completion`

The earlier staged implementation at
`C:/Users/Laszlos/source/repos/Solo/.tmp/hufu-completion` was not bulk-installed,
committed, or published. Its recorded 510 tests and candidate package checks
apply only to those earlier revisions. The old snapshot predates the current
`Penghou.Hufu.Workflow` adapter and its neutral contract dependency; its review
findings are historical reuse/disposition evidence, not a current inventory.
See [the HA-0A review](ha-0a-review.md). Do not run the old bulk installer or
publish that snapshot unchanged.

The former resume prompt directing work to verify/publish ZA-6 and defer Hufu
HA-1 has been retired: ZA-6 is complete and HA-1 is implemented. Resume from the
current gates above, not that historical prompt.

## Current constraints and deferred work

- Keep Hufu core, Cedar and Biscuit free of Zhinu dependencies. The optional workflow adapter depends on Hufu and the exact published neutral contract only; no Zhinu runtime/SQL, Luban, IO, Cedar, Biscuit or host UI dependency is allowed.
- Preserve required aggregate decision evidence, trusted host binding, typed approval outcomes, fail-closed behavior and fresh authorization for each retry or resumed dispatch. Historical permits do not authorize a new dispatch.
- Keep resource access authorization separate from workflow preflight. Existing resource/Luban/Biscuit profiles remain independently governed.
- Biscuit remains experimental and unpublished, with its pinned artifact outside the Hufu release set. Luban v2 read/diff is published in preview.2; the single-patch host journal remains deferred. No production workflow host is included in this release.
- HA-2 package-backed qualification passed locally on both frameworks. HA-3 initial publication is complete. Preview.2 three-platform CI and user-controlled publication are complete; future releases require their own qualification.

## Core checkpoint and next independent work

Bounded request admission and authenticated issuance are implemented and locally
qualified. They remain optional core compositions, with no Zhinu or other
integration dependency. Preserve active-capacity accounting through caller
cancellation, exact command approval and fresh issuer/approval reload after
operation policy. CI now runs the portable core security subset on Linux and macOS as
well as the Windows full suite. Initial three-platform qualification is
recorded in public release evidence. Preview.2 also passed all three CI and
publication-validation jobs at `a08fe64`; its six public packages are qualified.

The bounded typed-path explanation profile is implemented and locally qualified,
with exact capture and separately authenticated/redacted disclosure. Preserve
its informational status and explicit partial coverage. The [bounded request
telemetry slice](optional-telemetry.md) is also implemented. Preserve its finite
queue/worker, closed labels and independence from mandatory evidence/results.
Broader broker/approval/revocation telemetry and protected correlation remain
host/integration gates; lineage/requirements and historical reconstruction
remain separate explanation extensions. Broader Luban v2 mappings, the production single-patch host
and outcome journal are still held in separate qualified deliveries. Do not
bulk-install the old completion snapshot or infer authenticated services from
the new interface types.

## Ready-to-use post-publication resume prompt

> Initial HA-0A/B, HA-1, HA-2 and HA-3 and LUBAN-V2-READ-DIFF/RELEASE are complete.
> All six Hufu preview.2 packages are published and indexed. Read
> qualification/hufu-luban-v2-public-release.json for source a08fe64, successful
> three-platform CI/publication and fresh public-package consumers; the earlier
> local proof remains historical. Read luban-v2-authorization.md, the roadmap,
> completion review and package release profile. Preserve v1 defaults, exact
> v2 input scopes, current state, mandatory evidence and callback evidence limits.
> The next independent delivery is HOST-PATCH: review the single-patch host/outcome
> journal proposal against actual locked-object start, revocation ordering,
> mandatory durable evidence, outcome recovery and ambiguous-result handling.
> Do not infer mutation permission or candidate-object authorization from read/diff.
> Keep semantic admission separate from concrete resource checks. Never bulk-install
> older staging candidates, add a workflow-engine dependency to Hufu.Workflow,
> or replace the frozen legacy atomic-start profile without boundary qualification.
> Before changing released implementation, choose a new package version and use
> preview.2 as the compatibility baseline. Publication remains user-controlled.
