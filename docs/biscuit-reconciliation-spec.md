# AE-3 reconciliation spec: Biscuit as a carrier, not an authority engine

Status: **Specification only, 2026-10-12. No code.** This is the bounded adapter
correction required by the [inspection findings](biscuit-comparison-spec.md#inspection-findings-2026-10-12-source-review-no-code)
before the AE-3 comparison can be fair. Stop before implementation.

## Architectural rule

> **AE-3 compares carriers, not authority engines.**

After reconciliation, the two routes must differ only in how the Hufu child
grant/context is obtained; everything downstream is shared:

```text
Opaque route
worker -> opaque reference -> resolve Hufu child/context -> AE-1 -> provider

Biscuit route
worker -> Biscuit -> verify/map Hufu child/context       -> AE-1 -> provider
```

Everything after "resolve/map Hufu child/context" should be as identical as
practical: the same child authority, current parent policy, lifecycle, freshness
and evidence path.

## The three corrections

### 1. One containment definition

- Biscuit attenuation must use core
  `AuthorityDerivation.IsContainedBy(RequestedAuthority child, AuthorityGrant parent)`.
- Remove or delegate the independent `BiscuitProfile.IsSubset` as an authority
  semantic. It must not define its own exclusion or scope-intersection rule.
- A derived child that retains a **different grant ID** must be valid when core
  containment permits it. (Today `IsSubset` requires `parent.Id == child.Id`,
  which is incompatible with core derivation minting a distinct child grant.)
- No second interpretation of exclusions, scope containment or validity.

### 2. One lineage model

- Core derivation lineage (`IAuthorityDerivationStore`) remains authoritative.
- Biscuit block ancestry, fingerprints and revocation IDs become
  **credential-carrier evidence/mapping**, not a second authority graph.
- Every credential used by the comparison must resolve to the **exact Hufu child
  grant and its registered core lineage**.
- Unknown, mismatched, unregistered or substituted ancestry fails closed.

### 3. One current-authority path

- Biscuit verification proves **credential authenticity and attenuation only**.
- Actual authorization must then pass through the same **AE-1 qualified
  derived-authority composition** used by the opaque route:
  - child authority (`AuthorityRequest` against the child snapshot);
  - current parent policy (`ICurrentParentPolicy` / `LineageCurrentParentPolicy`);
  - lifecycle (`IExecutionLifecycleGate`);
  - optimistic read-set freshness revalidation (`authority.read-set-superseded`);
  - mandatory evidence.
- Biscuit signatures must never substitute for current Hufu authority.

## Mapping and evidence

- A versioned mapping binds a credential to the exact Hufu child grant identity,
  its core lineage (bound parent), worker/session, audience, generation/fence and
  operation, mirroring the opaque reference's bound facts.
- The authority binding, observed versions and decision evidence are the same as
  the opaque route, plus the credential-carrier identity (engine/mapping/key).
- No protected disclosure on any non-permit path; coarse external failures.

## Prohibitions (this slice)

Offline authorization; new-subject delegation; arbitrary audience exchange;
general onward delegation; new core public APIs unless unavoidable; package
publication; any change to the proven opaque-reference route; any change to
Hufu core authority semantics.

## Exit criterion

Both routes share one authority model (containment, lineage, current-authority),
differing only in carrier resolution. **Stop before implementing the
reconciliation or the comparison.** After the reconciliation lands and passes
the frozen AE-2 corpus on both routes, the AE-3 comparison (metrics + adopt/
defer/remove decision) can proceed as a separate milestone.
