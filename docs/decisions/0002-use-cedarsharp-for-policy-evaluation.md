# ADR 0002: Use official Cedar through CedarSharp for policy evaluation

Date: 2026-09-28.

Status: **Accepted direction; CedarSharp 1.0.0 implemented and qualified; Hufu typed Cedar projection and read authorizer locally qualified on Windows x64; production authority runtime pending.**

## Context

Hufu needs hierarchical authority, explicit exceptions, mandatory denials,
schema-checked policy input, and explanations. Cedar provides a suitable policy
language and engine. Owning a small .NET binding follows existing native-wrapper
practice while allowing Hufu to retain its own authority lifecycle and store.

CedarSharp has completed its initial managed/native implementation and published
version 1.0.0. Its release record qualifies Windows Server 2025 x64, Ubuntu
24.04 x64, and macOS 15 ARM64. CI runs managed suites on .NET 8 and .NET 10.
Hufu integration consumes that independently maintained package; Hufu-specific
typed mapping and read consumer are now locally qualified; production enforcement remains pending.

## Decision

Use this dependency direction:

```text
Host / Fuwen / Zhinu / Qingniao integration
                  |
              Hufu contracts
                  |
           Penghou.Hufu.Cedar
                  |
              CedarSharp
                  |
         owned Rust bridge / C ABI
                  |
       official cedar-policy engine
```

CedarSharp is independent of Penghou. It owns managed/native integration and
preserves upstream decisions, diagnostics, and version identity. Hufu.Cedar
maps typed authority inputs into trusted Cedar entities, actions, context,
schema, and policy bundles. Hufu core remains free of CedarSharp/native types.

Hufu owns grants, approval requests/decisions, envelopes, revocation state,
attenuation, authority-store semantics, and provenance. SQLite remains the
initial proposed persistent adapter. Cedar supplies evaluation, not grant
issuance, durable workflow activation, budget settlement, or resource isolation.

## Required integration semantics

- Validate policies against the exact admitted schema and validate the supplied
  request/entity data. Sources of resource ancestry, tenant identity, and context
  must be authenticated host data rather than agent assertions.
- Require explicit permission and preserve mandatory forbid rules. Distinguish
  a grant-local exclusion from a mandatory prohibition across grants.
- Preserve Cedar's raw decision and diagnostics for audit. Hufu blocks use on
  any evaluation error, incomplete required data, incompatible native identity,
  or bridge failure, even if Cedar's raw decision is Allow.
- Bind engine/language/features, adapter mapping, schema, policies, and relevant
  entity snapshot versions to admission. Engine or policy changes cannot silently
  reinterpret a prior receipt. Current expiry/revocation/fence checks still apply.
- Enforce each authority layer as a restriction. Unioning parent and child permit
  sets cannot substitute for requiring both layers to authorize the operation.
- Prove delegation containment using supported typed scopes initially. Ordinary
  authorization of sample requests does not prove all child rights are covered.
  Unknown containment rejects admission. Future symbolic analysis is optional
  and requires explicit assumptions, solver limits, and counterexamples.
- Keep one authoritative Hufu policy/decision lifecycle; Cedar inputs are exact
  versioned projections, not a separately edited source of authority.

## Delivery gate

CedarSharp 1.0.0 has completed the wrapper gate; see its [verification
record](../../../CedarSharp/docs/verification.md) for release evidence and the
qualified CI environments. The current Hufu.Cedar typed path projection and
Luban read consumer prove hierarchy exceptions, tenant/context isolation, layer
intersection, Allow-with-errors rejection, version mapping, expiry, and current
read/release denial on local Windows x64. See the [qualification record](../authority-profile-qualification.md).
Durable issuance, delegation lifecycle, and authority/start runtime gates remain open.

The Hufu read-authorizer prototype consumes an injected `IAuthorityEvaluator`;
the fixed-schema `CedarAuthorityEvaluator` implementation is now present but
passes the local Windows x64 qualification documented in the [qualification record](../authority-profile-qualification.md). The prototype
does not supply a complete trusted issuance lifecycle or the
operation-start/revocation protocol. The optional current-state/evidence store
is now implemented under [ADR 0008](0008-current-authority-store.md). This ADR does not itself add a package
dependency or qualify a Hufu adapter. The published wrapper supplies a
qualified tested surface. The current Hufu.Cedar package dependency is pinned to
1.0.0; qualification is limited to the documented typed read profile.

## Consequences and alternatives

We reuse Cedar semantics and maintain the narrower binding/mapping layers.
This supersedes the tentative preference to start with a bespoke general Hufu
policy evaluator. Hufu still implements its domain-specific scope containment
and durable authority mechanics. Casbin and CedarDotNet are not selected runtime
dependencies. Future backend changes require equivalent conformance and a new
decision rather than silent substitution.

## References

- [CedarSharp ADR](../../../CedarSharp/docs/decisions/0001-own-cedar-binding.md)
- [CedarSharp implementation handoff](../../../CedarSharp/docs/implementation-handoff.md)
- [Hufu authority ownership ADR](0001-hufu-owns-workflow-authority.md)
- [Cedar authorization semantics](https://docs.cedarpolicy.com/auth/authorization.html)
- [Cedar schema validation](https://docs.cedarpolicy.com/policies/validation.html)
- [Cedar symbolic analysis](https://github.com/cedar-policy/cedar/tree/main/cedar-policy-symcc)
