# Typed-effect design moved to Penghou.Luban

The canonical [typed-effect runtime design](../../Penghou.Luban/docs/typed-effect-runtime.md)
and [implementation plan](../../Penghou.Luban/docs/implementation-plan.md) now live
in **Penghou.Luban**, the selected name replacing Penghou.Effects.

Luban owns typed effects, trusted descriptors, providers, and their conformance.
Hufu owns authority decisions, grants, revocation, execution requirements, and
attributable authority evidence. See [Luban integration](luban-integration.md) and
[ADR 0005](decisions/0005-luban-owns-typed-effects.md) for this relationship.

This file intentionally remains as a forwarding note so prior links still work.
The source proposal moved to Luban with its historical wording preserved.
