# ADR 0004: Use LOP and typed effects; do not build a sandbox

Date: 2026-10-01.

Status: **Accepted direction; implementation pending.**

Ownership amendment: [ADR 0005](0005-luban-owns-typed-effects.md) selects the name
Penghou.Luban and establishes its separate project now. The temporary names and
incubation/extraction sequence below are historical; LOP and no-sandbox decisions
remain active. The effect design and source proposal moved to Luban.

## Decision

Prefer a small vocabulary of explicit effects over unrestricted command
execution. Fuwen provides control flow, Hufu provides authority, Zhinu provides
durable execution, and trusted effect providers perform the admitted operation.
The agent submits typed data, not executable code. This is the LOP direction
for routine source work.

Adopt the [typed-effect runtime design](../typed-effect-runtime.md), with working
name Penghou.Effects. Hufu initially owns its roadmap and documentation. Keep
effect request/result contracts independent of Hufu, Fuwen, Zhinu, MCP, and any
supervising agent, even if implementation is initially housed in Hufu's repo.
Extract an independent library once the contracts stabilize; do not create the
suggested package family merely to fill out a structure.

StrictEffects is the default planned agent surface: registered bounded effects
only, with arbitrary commands, scripts, executable payloads, and unknown effects
rejected. ApprovedTools is a distinct opt-in host mode for reviewed developer
adapters. A typed build/test request can still run project-controlled code and
does not acquire the strict-mode guarantee just by having a typed name.

Hufu and Penghou.Effects will not implement an OS/container sandbox. Any future
unrestricted execution is an explicit external-provider/host decision, outside
this implementation roadmap, with its actual guarantees recorded. Missing
capability returns a typed rejection; it never triggers a shell fallback.

## Qualifications adopted from review

- Strict effects restrict what can be requested through this boundary, not the
  powers of arbitrary code running elsewhere under the host's OS identity.
- Trust and version effect descriptors, authority mappings, providers, and
  transitive helper behavior. Agents cannot register executable handlers.
- Canonical strings alone do not enforce path scope. Authorization and mutation
  preconditions must remain bound to the actual object used.
- Git inspection must disable/reject configurable execution and implicit remote
  access. Qualify its implementation before admitting it to StrictEffects.
- Use explicit replay/reconciliation contracts rather than a Boolean replay-safe
  claim. Re-reading can return new data; a hash check alone cannot prove whether
  an interrupted mutation completed.
- Supervisor completion must bind the exact invocation and active fence, and
  cannot substitute for current authority or provider enforcement evidence.

## Supersession and scope

This supersedes ADR 0003's expectation that a Hufu-owned isolation provider
would follow, and narrows its generic capability-package direction into typed
effects. Store, credential-use, evidence, and analysis decisions remain in force.
No built-in sandbox is deferred to a later Hufu milestone: it is out of scope.
Existing confinement requirements apply only when such a guarantee is requested
from an external provider; they do not block strict typed workspace operations.

## References

- [Original proposal, preserved verbatim](../archive/typed-effect-runtime-proposal-2026-10-01.md)
- [Reviewed effect runtime and delivery gates](../typed-effect-runtime.md)
- [Local-first authority runtime](0003-local-first-authority-runtime.md)
- [Roadmap](../roadmap.md)
