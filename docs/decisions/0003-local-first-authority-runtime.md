# ADR 0003: Build a local-first authority runtime around Cedar

Date: 2026-10-01.

Status: **Accepted design direction; implementation pending.**

Amendment: [ADR 0004](0004-typed-effects-over-sandbox.md) supersedes the generic
capability direction below with a typed-effect runtime and removes owned sandbox
implementation from the roadmap. The earlier isolation-provider expectation is
historical; the other authority/evidence decisions remain in force.

## Context

Cedar evaluates policy. Useful local agent authority also needs durable state,
explanations, execution requirements, broker enforcement, and operation evidence.
Penghou libraries must remain usable independently of Hufu. The first useful
integration should not require a remote identity service or a complete sandbox.

## Decision

Adopt the [local-first runtime design](../local-first-authority-runtime.md):

- Use an optional SQLite authority store with explicit lineage and versioned
  facts; retain a replaceable store contract without requiring a graph database.
- Make durable decision evidence and lightweight observability foundational.
- Provide deterministic explanations, state diffs, and bounded simulation;
  distinguish exact supported analysis from incomplete exploration.
- Bind structured execution requirements to the exact operation. Unsupported
  requirements block execution; registered handlers must prove enforcement.
- Introduce a small Hufu-independent capability package with ordinary local
  implementations and Hufu-governed alternatives selected by the host. Final
  package/API names require the first consumer design review.
- Deliver one local filesystem broker and its conformance tests first. Process
  mediation is explicitly separate from OS containment. An optional isolation
  provider follows; requests requiring unavailable isolation remain unsupported.
- Resolve opaque credential references only inside trusted host infrastructure.
- Support exact-effect approval, temporary elevation within issuer authority,
  and expiry locally; defer organizational approval governance.
- Keep AI explanations, graph storage, SPIFFE/Vault integrations, signed audit
  anchoring, and Biscuit transport optional future extensions.

This supplements ADRs 0001 and 0002 without changing Cedar's role or transferring
workflow scheduling, execution recovery, or budget accounting to Hufu.

## Required qualifications

Dependency injection is integration, not a security boundary against arbitrary
code in the same process or operating-system account. Governed hosts cannot
silently fall back to unrestricted implementations when a broker is missing.

Simulation cannot promise to enumerate every consequence of arbitrary policies,
future resources, or arbitrary programs. Results disclose scope, versions,
assumptions, and completeness; incomplete analysis cannot prove containment.

Mandatory operation-start evidence commits before dispatch. An external effect
and a local receipt are not assumed atomic; uncertain outcomes require
reconciliation rather than an exactly-once claim or blind retry.

## Consequences

Hufu is usable first for trusted local code making explicitly brokered calls.
Its claims are limited to capabilities with boundary-level test evidence.
Existing untrusted-process requirements in the behavioral specification remain
in force. A full sandbox can be deferred without weakening those requirements.

The capability boundary adds a dependency direction to maintain, but consumers
need not take a dependency on Hufu. Extract only interfaces exercised by the
first real integration; do not scaffold unused provider packages.

## References

- [Original supplied proposal, preserved verbatim](../archive/local-first-authority-runtime-proposal-2026-10-01.md)
- [Authority ownership](0001-hufu-owns-workflow-authority.md)
- [CedarSharp integration](0002-use-cedarsharp-for-policy-evaluation.md)
- [Roadmap](../roadmap.md)
