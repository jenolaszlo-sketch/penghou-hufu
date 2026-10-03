# ADR 0005: Penghou.Luban owns typed effects

Date: 2026-10-01.

Status: **Accepted project name and ownership; runtime integration pending.**

## Decision

Establish Penghou.Luban as a separate sibling project now. It replaces the
Penghou.Effects working name and owns the typed-effect design, implementation
plan, neutral contracts, trusted catalogue, providers, and provider conformance.
Do not incubate the effect runtime in Hufu or duplicate its active roadmap here.

Move the reviewed design and verbatim source proposal into Luban, retaining
forwarding notes at their Hufu paths. Keep Hufu's authority-specific ADRs and
[integration requirements](../luban-integration.md) here. Prior ADR 0004's LOP,
StrictEffects, and no-sandbox decisions remain in force; its temporary project
name and incubation/extraction sequence are superseded.

Hufu owns policy evaluation, authority stores/lineage, grants, approvals,
revocation, execution requirements, and authority evidence. Its future Luban
adapter maps trusted effects into these contracts and coordinates admission with
the provider's final start boundary. Luban core references neither Hufu nor
Fuwen/Zhinu/host-specific identities. The adapter can reference both libraries;
neither core needs a circular dependency. Select adapter packaging with the first
integration instead of creating empty projects now.

Fuwen supplies control flow and effect declarations, Zhinu durable execution and
reconciliation, Qingniao attenuation, Marang supervisor routing, and Guyabano
local hosting. No responsibility moves merely because a common operation ID is
shared. Local usage without Hufu is explicit and carries no Hufu authority claim.

## Delivery

Sol scaffolds Luban and prepares its staged implementation plan in this task.
The scaffold is not runtime implementation. Hufu integration depends on tested
Luban provider semantics and CedarSharp evaluation as applicable; authority-store
and evidence work can proceed independently. No sandbox, remote publication,
package release, or broad refactoring of consumers is implied.

## References

- [Penghou.Luban](../../../Penghou.Luban/README.md)
- [Canonical effect design](../../../Penghou.Luban/docs/typed-effect-runtime.md)
- [Luban implementation plan](../../../Penghou.Luban/docs/implementation-plan.md)
- [Hufu integration boundary](../luban-integration.md)
- [LOP decision](0004-typed-effects-over-sandbox.md)
