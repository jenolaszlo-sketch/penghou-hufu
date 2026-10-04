# Penghou.Hufu contributor guidance

Current Hufu/Zhinu work must first read [ADR 0011](docs/decisions/0011-neutral-zhinu-authority-extension.md),
the [canonical neutral-seam plan](../Penghou.Zhinu/docs/authority-extension-plan.md)
and [handoff](docs/zhinu-authority-handoff.md). They supersede the older integration
work order. Read [Penghou's workflow contract plan](../Penghou/docs/workflow-abstractions-plan.md)
for product-neutral ownership and WA-1/2/3 publication before Zhinu, then Hufu.
The implemented adapter is Penghou.Hufu.Workflow and references only Hufu plus
the published Penghou.Workflow.Abstractions 0.1.0-preview.2. Published Zhinu
0.2.0-preview.1 is used only by separate integration tests. Read the current
handoff and qualification records for HA-2/3 release status; local qualification
does not imply remote CI or user-run publication. Preserve independently usable cores and resource checks; do not
bulk-install the earlier completion staging tree or treat activity preflight as
the legacy atomic mutation-start guarantee.

Read the [resource-abstractions architecture](../Penghou/docs/resource-abstractions-architecture.md)
for RA-1/RA-4 and deferred VFS authority work. Resource interception belongs in
optional Hufu.IO integration; semantic admission remains a separate Hufu.Luban
obligation. Preserve discovered-resource checks and the real mutation-start
boundary; an outer decorator alone cannot prove them. Capture-only preview and
future simulated execution are distinct modes. Handoffs cite RA/VFS gates and
separate current implementation evidence from planned guarantees.

Read the specification, architecture, roadmap, and decisions under `docs/`,
and [core admission and issuance](docs/core-admission-and-issuance.md), including
its qualification record. Active caller cancellation retains evaluation
capacity until owned work completes. Publication composes operation policy
with independently authenticated current issuer ceiling and exact command,
sequence, snapshot and expiry approval, reloaded after asynchronous policy.
Do this before changing behavior or public contracts. Read the
[explanation disclosure profile](docs/decision-explanations.md).
Preserve the single evaluator capture, informational typed facts, partial coverage
and mandatory exact actor/explanation/expiry disclosure policy. Default summaries
must expose only the outcome; raw capture and diagnostics remain host-only. Keep the authority boundary
explicit: a caller's request or workflow structure alone must not grant
permission to advance work. Record the evidence needed to explain each
authority decision and preserve it across recovery and replay.

Treat identity, policy, and provider responses as external inputs requiring
validation. Fail closed when required evidence is missing or inconsistent.
Avoid ambient authority, implicit privilege escalation, and secrets in durable
records or diagnostics. Add focused tests when behavior is implemented, including
denial, recovery, and replay cases. Do not publish packages or claim API
stability until the contracts and implementation are reviewed.
