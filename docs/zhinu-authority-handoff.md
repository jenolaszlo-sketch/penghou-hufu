# Handoff: authority extension inversion

Updated 2026-10-03 after the package publication checkpoint. Reconcile the
current activity queue and evidence before acting on older resume prompts.

## Reading order

1. [Penghou workflow contract ownership/publication plan](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-abstractions-plan.md),
   then [canonical cross-project plan](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-plan.md)
   and its archived original proposal.
   Use the [current activity queue](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/authority-extension-activities.md)
   to choose work and record ready/held status.
2. [ADR 0011](decisions/0011-neutral-zhinu-authority-extension.md).
3. [Hufu roadmap](roadmap.md), [Zhinu roadmap](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/ROADMAP.md),
   current execution/store semantics and [resource baseline](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/resource-abstractions-architecture.md).
4. Existing start profile/qualification as regression evidence, not the new
   default integration architecture.

## Neutral package delivery checkpoint

Penghou.Workflow.Abstractions `0.1.0-preview.2` is published. Source commit
`5a76b7c` passed all seven CI jobs in [run 37115430526](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37115430526);
all four publication jobs passed in [run 37116694209](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37116694209).
Exact package contents match CI apart from the repository signature, and
fresh-cache NuGet-only consumers pass on .NET 8/10. See the [contract manual](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-authorization-contract.md),
[release handoff](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-package-release-handoff.md) and
[qualification record](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-public-package-qualification.json).
WA-1/2/3 and Zhinu ZA-2 source adoption are complete, with the frozen legacy
profile decision retained. The fresh seven-package consumer closure is recorded
in the [adoption evidence](https://github.com/jenolaszlo-sketch/penghou-zhinu/blob/main/docs/workflow-package-adoption.md).
Zhinu ZA-3A/3B/4 are locally qualified in `0.2.0-preview.1`; 1,017 runtime
tests passed (506 on .NET 8 and 511 on .NET 10), and the isolated seven-package
consumer passed on both TFMs; the unchanged preview.15 compatibility suite also
passed on .NET 8/10. Next verify remote CI, then user-publish through ZA-6. Hufu HA-1/2/3
follows that publication. Keep HA-0A/B cleanup independent, preserve the
legacy-profile decision, and keep the older staged Hufu snapshot on hold. The
Hufu adapter and publication remain pending.
Older design prompts below are historical inputs; do not restart closed design.

## Exact checkpoint

Zhinu preview.15 is published. The Hufu original working tree already selects
exact Zhinu and Zhinu.Sqlite preview.15 packages with explicit source opt-in;
the solution has no unconditional sibling Zhinu projects. A fresh isolated
restore passed 426 existing cases across .NET 8/10. That adoption and the architecture handoff are committed in
`56aa1c3`; preserve the exact preview.15 legacy references until the separately
qualified integration phase. Hufu core is already
Zhinu-independent; the concrete SQLite adapter and mixed test graph are the
migration targets.

Separate staged implementation exists at
`C:/Users/Laszlos/source/repos/Solo/.tmp/hufu-completion`. It has **not** been
bulk-installed, committed or published. It contains bounded issuance/request
concurrency, opt-in Luban v2, a single-patch host/outcome journal, six package/API
baselines and CI/manual publishing definitions. Combined tests passed 510 cases
(143 core, 93 Biscuit, 19 IO per framework). Six package candidates passed
strict framework validation, inspection and isolated net8/net10 consumer builds
and recorded-deny execution. These are component/candidate checks, not proof of
the proposed workflow authorization seam or a production trusted host.

The earlier `Solo/.tmp/Install-HufuCompletion.ps1` and completion resume file
are superseded as an execution queue. **Do not run the bulk installer or publish
those candidates unchanged.** Preserve the staged source and review each change
against this plan. No runtime edits were made for this documentation pivot.

## Reuse and revised work order

- Keep Hufu generic authority/evidence/store work, the independent IO/Luban
  adapters and their tests. Do not add workflow concepts to their core APIs.
- Reuse package/API tooling after selecting the corrected project/test/package
  graph. Recompute baselines only for reviewed changes; no blanket suppressions.
- Keep the single-patch journal/host components under their independent host
  profile. They must not make a Zhinu database mandatory for Hufu or Luban.
- Freeze the concrete `Hufu.Zhinu.Sqlite` composition and its regression tests.
  Separate tests under **HA-0B** without waiting for the new adapter. Preserve
  the frozen legacy profile decision under **ZA-5A**; any replacement/retirement
  is **ZA-5B** with evidence. An allow/deny hook does not replace its atomic
  start transaction.
- Start independent **HA-0A** staged-work review/reuse and **HA-0B** test isolation.
  Hufu core/package tooling does not wait for a new Zhinu package publication.
- **ZA-1A/B/C/D design and Zhinu runtime qualification are complete:** source
  inventory, callback coverage, bounded context, closed outcomes, downgrade
  protection, approval and fencing evidence are retained. Read the current
  contract/manual rather than reopening those gates.
- **WA-1/2/3 and ZA-2 source adoption are complete:** the exact published
  contract source/package adoption and fresh seven-package consumer closure are
  qualified. Verify remote CI, then user-publish `0.2.0-preview.1` through **ZA-6**. Only after
  ZA-6 implement/qualify/release Hufu workflow translation
  **HA-1/2/3** against exact published contracts. Hufu core remains independent.

## Ready-to-use resume prompt

> Continue product-neutral workflow authorization. Read Penghou's
> docs/workflow-abstractions-plan.md and workflow-package-release-handoff.md first.
> WA-1/2/3 are complete; `Penghou.Workflow.Abstractions` 0.1.0-preview.2 is
> published and contract package/consumer qualification passed. Zhinu ZA-2
> exact published-package source adoption and fresh seven-package consumer
> closure are complete. ZA-3A/3B/4 are locally qualified. The unchanged preview.15 compatibility
> check passed. Verify remote CI, then user-publish Zhinu `0.2.0-preview.1`
> through ZA-6; Hufu HA-1/2/3 follows publication. The Hufu
> adapter and Hufu publication remain pending. HA-0A/B cleanup remains independent; keep
> the older Hufu staging snapshot on hold and preserve the frozen legacy-profile
> decision. Read the canonical Zhinu authority-extension plan and Hufu ADR 0011.
> Keep Hufu core independent and make the adapter depend only on Hufu and
> Penghou.Workflow.Abstractions. Preserve resource checks and document the legacy
> atomic-start disposition. Qualify denied/failed/approval-required dispatch,
> retry, durable resume, revocation, evidence, compatibility and transitive
> package isolation on both supported frameworks. Never equate package or
> historical authorization evidence with runtime acceptance/fresh permission.
> Luban LW-1 is optional neutral-host integration; keep the language core
> independent of workflow authorization. Update WA/ZA/HA gates with evidence.
