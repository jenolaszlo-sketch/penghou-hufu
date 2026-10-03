# Authority document consolidation

Date: 2026-09-28.

Penghou.Hufu is now the canonical home for this authority design. The original user attachment is preserved in place; this repository includes an archived copy for a self-contained design history.

| Prior material | New location | Treatment |
| --- | --- | --- |
| `Solo/artifacts/workflow-authority/workflow-authority-revised-spec.md` | [workflow-authority-spec.md](workflow-authority-spec.md) | Relocated and updated for Hufu ownership; prior location becomes a forwarding document. |
| User attachment, `Pasted text.txt`, titled “Workflow Authority, Delegation and Re-Admission” | [archive/original-proposal.md](archive/original-proposal.md) | Original proposal body retained under an archive preface; superseded. |
| Discussion of a reusable authority library and its own store | [architecture.md](architecture.md) and [ADR 0001](decisions/0001-hufu-owns-workflow-authority.md) | Captured as the new library boundary and proposed implementation design. |
| Delivery stages from the authority proposal | [roadmap.md](roadmap.md) | Expanded into Hufu-specific implementation gates without claiming implementation. |

The existing workflow-evolution proposal, Fuwen budget ADR, and Guihua/Qingniao architecture reviews remain in their owning projects. They cover broader responsibilities, contain project-specific history, and should not be moved into Hufu. The authority specification links to those records as supporting context.

Sibling-checkout references in the specification resolve in the current local repository layout. They are explicitly local evidence references, not invented remote repository URLs. Standalone readers can understand Hufu's proposed behavior without them; a future remote publication can replace those references with verified permalinks.

## Typed effects moved to Penghou.Luban, 2026-10-01

[ADR 0005](decisions/0005-luban-owns-typed-effects.md) separates effect ownership
from authority ownership. The reviewed `docs/typed-effect-runtime.md` moved to
[Luban](../../Penghou.Luban/docs/typed-effect-runtime.md), updated to its selected
name/ownership. The verbatim source proposal moved to
[Luban's archive](../../Penghou.Luban/docs/archive/typed-effect-runtime-proposal-2026-10-01.md)
without rewriting its historical names. Both old Hufu paths retain forwarding
notes so prior links continue to work.

Hufu's LOP ADR and broader authority documents stay here. The active Hufu roadmap
now tracks its [Luban integration](luban-integration.md), while Luban owns effect
implementation and provider conformance. This migration does not implement the
runtime or authorize package publication.
