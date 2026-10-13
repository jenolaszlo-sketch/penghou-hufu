# AE-3 specification: compare Biscuit against the proven opaque broker path

Status: **Specification only, 2026-10-12. No code, no publication.** AE-2 is
complete for the cooperative-worker profile ([profile](broker-authority-profile.md)),
so the control path Biscuit is meant to improve now exists. This milestone is a
**comparison**, not a feature: it answers, with evidence, whether Biscuit's
portability/attenuation benefit justifies its complexity over the opaque
session-bound reference.

## The question AE-3 must answer

> On the identical AE-2 broker corpus, does a Biscuit credential carrier provide
> enough portability and real attenuation benefit to justify its added
> verification cost, host/registry state, key lifecycle and disclosure risk —
> or is the opaque session-bound reference already the better default?

A decision to **defer or remove** Biscuit is a valid, successful AE-3 outcome.

## Prerequisites and constraints

- **Reconcile semantics with core.** The experimental adapter's subset routine
  and registry ancestry must be reconciled with core `AuthorityDerivation`:
  one definition of containment and the exact Derive approval (ADR 0012/0013).
  Token attenuation must never invent a new subject or widen authority.
- **Pinned unpublished dependency.** The prototype references an unpublished
  BiscuitSharp preview via a local feed. AE-3 does not publish it and must not
  make Hufu core depend on Biscuit; the adapter stays optional and isolated.
- **Both paths, same tests.** Biscuit must pass the same authority corpus as the
  opaque route. A signature never substitutes for a current Hufu/Cedar decision,
  registered lineage, evidence, or the start/release boundary.

## What is compared

| Dimension | How measured |
| --- | --- |
| Portability | Can a credential be verified at a boundary that does not share the opaque reference store? |
| Real attenuation use | Is offline holder-side attenuation actually needed by any real consumer, or only by the prototype? |
| Payload size | Credential/envelope bytes vs opaque reference bytes on the same operation. |
| Verification cost | Latency/CPU/host-state per operation vs the opaque path. |
| Host/registry state | Additional durable state (registry, revocation, key retirement) and its ordering. |
| Key/operational burden | Signing/verification key custody, rotation, retirement. |
| Disclosure risk | What the credential reveals if leaked, and whether it becomes bearer authority. |
| Complexity delta | Code, dependencies and failure modes added over the opaque route. |

## Required comparison corpus

Reuse the AE-2 operation/provider/transport corpus
([AE-2.2](broker-ae22-implementation-spec.md),
[AE-2.3](broker-ae23-qualification-spec.md)) unchanged, and add the adversarial
credential cases:

- stolen / wrong-audience / wrong-recipient bindings;
- unregistered offline attenuation;
- expiry and revoked ancestry;
- unknown claims/mappings;
- unavailable registry/key/authority dependencies;
- signature verification failure and malformed credentials;
- replayed credential reuse across operations or recipients.

Both routes must produce the same permit/deny/unavailable outcomes and the same
no-protected-disclosure behavior; signatures may not weaken any check.

## Evidence to record

Per operation, for both routes: exact request, route identity, verification
identity, observed authority binding and versions, verification duration and
sizes, evidence receipts, and the decision. Published outputs are comparisons,
not new security claims.

## Decision and outcomes

Record one explicit decision with its evidence:

- **Adopt** — Biscuit is the carrier for a specific, demonstrated portable
  boundary (and the opaque route remains available).
- **Defer** — no real consumer needs portability yet; retain the prototype as
  experimental and keep the opaque route as default.
- **Remove** — the opaque route is the better default; retire the prototype.

## Non-goals

Offline current-authority validity; arbitrary audience exchange; onward
delegation; mutation recovery; package publication; any change to Hufu core or
the AE-2 broker contract; any claim that possession of a token grants current
permission.

## Exit criterion

A reproducible comparison on the frozen AE-2 corpus justifies one recorded
decision (including defer/remove). **Stop before implementation of any chosen
carrier until that decision is recorded and reviewed.**

## Inspection findings (2026-10-12, source review, no code)

Reviewed `Penghou.Hufu.Biscuit` (`BiscuitProfile`, `BiscuitAuthorityService`,
`BiscuitCredentialRegistration`, `BiscuitKeyRing`, `BiscuitContracts`,
`BiscuitRequestAuthorizer`) against the core-derived authority path. The adapter
is a **credential carrier with its own subset, ancestry and current-authority
composition** — not "already too divergent to be a fair candidate", but it needs
a **bounded reconciliation** before the comparison is fair.

| Area | Core-derived path | Biscuit adapter | Delta / reconciliation |
| --- | --- | --- | --- |
| Containment | `AuthorityDerivation.IsContainedBy(RequestedAuthority, AuthorityGrant)`: new child grant id; overlap-based exclusion rule | `BiscuitProfile.IsSubset(parent, child)`: requires `parent.Id == child.Id`; intersection-based exclusion rule | **Two definitions.** Unify on core containment; drop or forward the adapter routine. |
| Ancestry | `IAuthorityDerivationStore` lineage: child grant id → parent grant id + parent context | Registry chain via token-block `ParentFingerprint`, `RevocationIds`, `GrantVersionIdentity`, `AuthorityScopeId` (`LoadChainAsync`) | **Different lineage models.** Reconcile registry ancestry with core lineage so both resolve the same ancestry. |
| Subject | Child `AuthorityGrant` bound to `ChildContext` (tenant/subject/run/revision/fence) | `BiscuitWorkloadBinding(Context, Realm, WorkflowId, ActivityId, Audience)`; token binds subject/run/workflow/revision/activity/fence/audience; attenuation keeps subject fixed | Additive (`Realm`/`WorkflowId`/`ActivityId`); must not invent a subject or rebind identity. |
| Audience | Host-side `BrokerAuthorityReference.Audience` + authenticated session | `hufu_audience` fact inside the credential + registration binding | Same concept, different carrier — this is the portability axis of the comparison. |
| Key handling | None (opaque host reference) | `BiscuitKeyRing`/`IBiscuitKeyProvider`: Ed25519 signing leases, `RootKeyId`, realm key selection, retired-key verification, `RetireKey` | **Added crypto + key-custody lifecycle and an unpublished BiscuitSharp dependency.** The main cost side. |
| Current authority | AE-1 qualified: child → ancestor liveness → **current-parent policy** → lifecycle → read-set freshness revalidation | `Applicable` (root grant still present and identical by `SameGrant`) + Cedar `EvaluateDetailed` over the current snapshot + `Covers(EffectiveGrant)` + `HasUnchangedValidity` | **Not equivalent.** Biscuit lacks AE-1's current-parent-policy re-evaluation, lifecycle gate, and `authority.read-set-superseded` revalidation. Must compose the same checks or it compares different semantics. |

**Conclusion.** The comparison is viable, but only after reconciling
containment to the core definition and composing the AE-1 current-parent,
lifecycle and freshness checks on the Biscuit route. That is a bounded adapter
correction, not a redesign. Until then, Biscuit and the opaque route would be
compared on different authority semantics, which the spec forbids.

The bounded correction is specified separately in the
[Biscuit reconciliation spec](biscuit-reconciliation-spec.md): one containment
definition, one lineage model, one AE-1 current-authority path — carriers, not
authority engines.
