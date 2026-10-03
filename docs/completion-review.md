# Hufu completion review

Current review: 2026-10-03, reconciled with the neutral Zhinu authority plan.
Use [ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md), the
[canonical plan](../../Penghou.Zhinu/docs/authority-extension-plan.md),
[current activities](../../Penghou.Zhinu/docs/authority-extension-activities.md)
and [handoff](zhinu-authority-handoff.md). This current work list replaces the
earlier repository/host-first priority table; historical qualification records
remain evidence for their actual versions.

## Verified checkpoint

- Hufu has a configured repository. Core/Cedar/Biscuit have no Zhinu reference;
  legacy integration tests still bring Zhinu into the main core test graph.
- Normal builds consume exact IO/Luban preview.1 and the optional legacy
  Zhinu/Sqlite preview.15 packages. The installed source-free migration passed
  426 cases across .NET 8/10 with no failed/skipped tests. See
  [Zhinu adoption](zhinu-package-adoption.md) and its
  [public-package evidence](qualification/public-resource-packages-with-zhinu.json).
- Separate uninstalled staging contains bounded issuance/request admission,
  opt-in Luban v2, single-patch host/outcome journal and six-package/API/CI
  tooling. Its combined tests passed 510 cases, and isolated net8/net10 package
  consumers passed build and recorded-deny execution. Review/reuse those changes
  under HA-0A; do not bulk-install the old snapshot or call its tests acceptance
  of the new workflow seam.
- Penghou.Workflow.Abstractions `0.1.0-preview.2` is published. Source commit
  `5a76b7c` passed all seven CI jobs in [run 37115430526](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37115430526);
  all four publication jobs passed in [run 37116694209](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37116694209).
  Exact public package contents match CI apart from the repository signature,
  and fresh-cache NuGet-only consumers pass on .NET 8/10. See the [package
  qualification record](../../Penghou/docs/workflow-public-package-qualification.json).
  Zhinu ZA-3A/3B/4 are locally qualified in candidate `0.2.0-preview.1`;
  the 1,017 runtime tests and isolated seven-package consumer passed on both
  supported TFMs, and the unchanged preview.15 compatibility suite passed on
  .NET 8/10. Zhinu commit/push and NuGet publication with remote CI remain
  pending. Hufu.Workflow adapter implementation and Hufu publication/CI
  qualification have not occurred. Keep the older Hufu
  staging snapshot on hold; preserve HA-0A/B cleanup and the frozen legacy-profile
  decision.

## Current work order

| Activity | State and completion criterion |
| --- | --- |
| HA-0A | Ready independently: reconcile generic staged changes and review the bounded initial package set, API inventories and CI tooling. Hufu core progress does not wait for Penghou.Workflow.Abstractions publication |
| HA-0B | Ready independently: separate existing Zhinu operation-start/process-worker tests, preserve their regressions, and qualify core test/build/package graphs without Zhinu |
| ZA-1A/B/C/D contribution | Implemented and locally qualified in Zhinu candidate `0.2.0-preview.1`; see the current qualification record. Commit/push and ZA-6 publication with remote CI are pending |
| ZA-5A/B | Preserve the frozen legacy SQLite profile decision; any ZA-5B replacement/retirement requires explicit evidence preserving required final-start guarantees |
| HA-1 | After the completed Zhinu phase ZA-6: build the optional translation adapter with only Hufu + Penghou.Workflow.Abstractions dependencies, including trusted approval mapping |
| HA-2 | After runtime and adapter candidates: qualify translation, denied starts, revoked retries, approval resume, compensation, fencing, evidence and process/replay recovery in the separate integration suite |
| HA-3 | After the completed Zhinu phase ZA-6 and HA-2 integration: consume exact published abstraction packages, inspect transitive graphs, qualify fresh-cache consumers/CI and the reviewed release set. The user performs Hufu publication |

The [Penghou workflow contract plan](../../Penghou/docs/workflow-abstractions-plan.md)
records completed WA-1/2/3 delivery. Zhinu ZA-2 source adoption, including the
fresh seven-package consumer graph, is complete; see the [adoption evidence](../../Penghou.Zhinu/docs/workflow-package-adoption.md).
ZA-3A/3B/4 and the unchanged preview.15 compatibility suite are locally
qualified. Commit/push and user-publish Zhinu `0.2.0-preview.1` through ZA-6
with remote CI. Hufu
HA-1/2/3 follows that publication; the adapter and Hufu release remain pending.
The activity queue carries
precise dependencies and deliverables. Candidate development and public adoption
are different gates. Independent Hufu core,
IO and Luban integrations must not acquire a dependency on completing Zhinu's
runtime or its publication.

## Separate qualification and deferred work

Trusted issuer/approval/custody services remain host responsibilities until one
concrete composition is qualified. Staged guards/components do not prove those
external facts. Production governed single-patch hosting still needs real exact
approval, locked callback/runtime binding, final atomic start and durable outcome
reconciliation. Activity preflight does not replace these effect boundaries.

Bounded request admission is staged and tested, but capacity across publication,
revocation, starts and all processes remains a separate host gate. The older
four-worker full-host unavailable result is retained in the budget evidence;
mandatory evidence cannot be bypassed to hide contention.

BiscuitSharp preview.2 remains an exact pinned unpublished artifact. Its optional
distribution gate is independent of the published IO/Luban/Zhinu adoption and
of the proposed neutral adapter. General delegation, VFS/WhatIf, batches, stronger
revocation-drain guarantees and broader platform/AOT qualification remain scoped
follow-ups. Luban language completion is not reopened by these Hufu activities.
