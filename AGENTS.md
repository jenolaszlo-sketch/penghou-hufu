# Penghou.Hufu contributor guidance

Current Hufu/Zhinu work must first read [ADR 0011](docs/decisions/0011-neutral-zhinu-authority-extension.md),
the [canonical neutral-seam plan](../Penghou.Zhinu/docs/authority-extension-plan.md)
and [handoff](docs/zhinu-authority-handoff.md). They supersede the older integration
work order. Read [Penghou's workflow contract plan](../Penghou/docs/workflow-abstractions-plan.md)
for product-neutral ownership and WA-1/2/3 publication before Zhinu, then Hufu.
The planned adapter is Penghou.Hufu.Workflow and references only Hufu plus
Penghou.Workflow.Abstractions. Preserve independently usable cores and resource checks; do not
bulk-install the earlier completion staging tree or treat activity preflight as
the legacy atomic mutation-start guarantee.

Read the [resource-abstractions architecture](../Penghou/docs/resource-abstractions-architecture.md)
for RA-1/RA-4 and deferred VFS authority work. Resource interception belongs in
optional Hufu.IO integration; semantic admission remains a separate Hufu.Luban
obligation. Preserve discovered-resource checks and the real mutation-start
boundary; an outer decorator alone cannot prove them. Capture-only preview and
future simulated execution are distinct modes. Handoffs cite RA/VFS gates and
separate current implementation evidence from planned guarantees.

Read the specification, architecture, roadmap, and decisions under `docs/`
before changing behavior or public contracts. Keep the authority boundary
explicit: a caller's request or workflow structure alone must not grant
permission to advance work. Record the evidence needed to explain each
authority decision and preserve it across recovery and replay.

Treat identity, policy, and provider responses as external inputs requiring
validation. Fail closed when required evidence is missing or inconsistent.
Avoid ambient authority, implicit privilege escalation, and secrets in durable
records or diagnostics. Add focused tests when behavior is implemented, including
denial, recovery, and replay cases. Do not publish packages or claim API
stability until the contracts and implementation are reviewed.
