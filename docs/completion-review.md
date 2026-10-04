# Hufu completion review

Current review: 2026-10-04. Read the [handoff](zhinu-authority-handoff.md),
[roadmap](roadmap.md), [workflow boundary](workflow-authorizer.md) and
[core admission/issuance profile](core-admission-and-issuance.md) first.
They supersede the older repository/host-first queue. Historical qualification
records remain evidence for their actual revisions.

## Verified checkpoint

All six preview.2 packages are indexed on NuGet. See [public metadata, three-platform
CI/publication and fresh-consumer evidence](qualification/hufu-luban-v2-public-release.json).
Earlier preview.1 and [local v2 evidence](qualification/luban-v2-authorization.json)
remain historical qualification records. The v2 implementation qualifies 882 distinct regression cases (441 per framework):
full solution 880, then the complete final core suite after one new case. The
counts below describe earlier checkpoints.

- Published dependencies are exact IO/Luban preview.1 and neutral Workflow
  preview.2. Zhinu 0.2.0-preview.1 passed remote CI/publication; seven exact public
  packages are qualified in [the record](qualification/zhinu-public-release.json).
- Hufu core/Cedar/Biscuit are Zhinu-independent. HA-0B isolates the frozen legacy
  preview.15 atomic-start regression suite without removing its 22 cases per TFM.
- HA-1 implements optional Hufu.Workflow with only core and neutral contract
  dependencies: trusted host binding, explicit typed approval and required
  aggregate evidence. Its 52 unit and 12 real runtime integration cases pass
  per TFM, including fresh-cache package-only integration.
- Shared bounded request admission and finite typed-scope authenticated issuance
  are individually reviewed and implemented. Their 73 new cases per framework
  cover actual outstanding source/evidence work, cancellation races, exact
  approvals, containment, policy composition and SQLite publication/replay.
- Earlier telemetry regression: 816 passed, 408 per framework;
  zero failed/skipped. 26 new telemetry cases per framework cover
  mandatory recorder failures, exact result/exception/cancellation preservation,
  bounded loss/shutdown and blocked/throwing listener privacy/isolation. See
  [current telemetry evidence](qualification/optional-telemetry.json).
- The earlier explanation checkpoint passed 764 cases, 382 per framework.
  The 32 new explanation cases per framework cover actual Cedar capture,
  redacted summaries, exact disclosure binding, expiry, immutable data and bounds.
  Six release package/symbol pairs, API inventories, isolated standalone consumers
  and package-only workflow integration are locally requalified. See
  [earlier explanation evidence](qualification/decision-explanations.json). The
  [700-case core checkpoint](qualification/core-hardening.json) and earlier
  [554-case workflow record](qualification/workflow-authorization.json) are preserved.

## Current work order

| Activity | State and completion criterion |
| --- | --- |
| HA-0A/B, HA-1 | Complete and locally qualified; preserve independent graphs and frozen legacy coverage |
| HA-2 | Fresh candidate consumers and package-only workflow integration pass on .NET 8/10 |
| Core admission/issuance | Implemented and locally qualified; actual authenticated services and aggregate host/process capacity remain host gates |
| HA-3 | Initial six preview.1 packages published; three-platform CI passed at a131216 with the recorded Windows retry |
| Luban v2 read/diff | Published preview.2; three-platform CI/publication and fresh public-package consumers passed; governed mutation hosting remains separate |
| Core explanations | Bounded exact-operation typed-path slice implemented and locally qualified; lineage/requirements and retained historical reconstruction remain future extensions |
| Optional telemetry | Bounded request slice implemented and locally qualified; broker/approval/revocation metrics and protected correlation remain separate host work |
| ZA-5B | Separate replacement/retirement decision preserving the frozen legacy final-start guarantee |

The [Luban v2 read/diff adapter](luban-v2-authorization.md) is now implemented
in preview.2. The single-patch host/outcome journal remains a separate delivery. No other old `.tmp/hufu-completion` group was bulk-installed. That
snapshot's 510-case report does not qualify the current release graph.

## Remaining scope

The new trust-source interface does not authenticate credentials by itself.
One concrete host still needs authenticated issuer/approval/custody services,
actual shared capacity, exact resource binding, final atomic mutation start
and durable outcome reconciliation. Current issuance rechecks after policy
awaits; it does not order parent revocation and publication across stores.
Workflow preflight does not supply those effect-boundary guarantees.

The optional Biscuit profile remains experimental and unpublished, with its
exact pinned wrapper artifact outside this release. General delegation,
simulation/VFS, batches, stronger revocation-drain guarantees and broader
platform/AOT qualification remain follow-ups. These core changes create no
new dependency or implementation work for Luban or Zhinu.
