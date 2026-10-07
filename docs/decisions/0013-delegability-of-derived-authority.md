# ADR 0013: Delegability of Derived Authority

Date: 2026-10-07.

Status: **Proposed.** No implementation until the Qingniao proof requires it.
In particular: no Qingniao request fields yet, no `Derive` store operation
yet, no approval-tuple changes yet, no Biscuit changes.

## Context

ADR 0012 established derived authority lineage: Hufu creates child grants,
proves containment, and gates usability on ancestor liveness. The Marang
fs.read slice and the Hufu derivation proof validated admission, containment,
idempotency, and revocation. Two read-only surveys then isolated the one
remaining missing concept: nothing decides whether a supervisor holding
parent authority may cause a child grant to exist for a delegation.

Containment determines whether a child could be derived from a parent;
delegability determines whether this authenticated actor may cause that
derivation to occur.

Those are separate dimensions. A grant permitting `read src/**` does not
necessarily permit deriving a child carrying it for another subject, and no
existing Hufu mechanism — issuance trust, operation policy, ceiling
containment, Biscuit attenuation — answers the second question.

## Decision

Delegability is an independent issuance rule evaluated at derivation time,
not a property of grants, not a property of actions, and not merged into
publication containment. `ReadFile` continues to mean "may read a file,"
never "may read and possibly delegate reading."

### Actor authentication and delegation labels stay separate

```text
Authenticated actor:
    established by Hufu trust source

Qingniao supplied:
    SupervisorIdentity
    DelegationId
    Generation
    ParentGrantId
    RequestedAuthority
```

The Qingniao identities are correlation and binding data. None of them
authenticates the caller. The trust source authenticates the invoking actor;
the labels identify whose delegation the derivation serves and which parent
it derives from.

### The new operation is explicitly Derive

A future `AuthorityStoreOperation.Derive` carries an authorization decision
based on:

```text
authenticated actor
parent grant
delegation identity
generation
canonical requested-authority hash
```

It remains distinct from `Publish`. Publication containment answers whether
a snapshot may be published under the publisher's own authority;
derivation approval answers whether this actor may mint this child from
this parent for this delegation.

### Delegability approval is exact, never open-ended

Approval binds the full tuple above. Approvals resembling "may derive from
P" are rejected as a shape: they quietly grant an open-ended authority
minting capability, and a later audit could not distinguish which children
were intended. Exact-tuple approval plus the derivation-identity digest
already recorded for idempotency gives every issuance a checkable,
replayable justification.

### Containment remains a separate check

Even with approval, Hufu must still prove requested child authority is
contained by the parent. Approval without containment would let an approved
but overbroad request through; containment without approval would let any
holder of readable state mint authority. The complete derivation gate is:

```text
authenticate actor
        ↓
actor allowed to invoke Derive?
        ↓
exact derivation approval valid?
        ↓
parent freshly live?
        ↓
requested C contained by P?
        ↓
durable/idempotent derivation
```

Each failure has a different meaning (unauthenticated caller, unauthorized
operation, stale approval, dead parent, overbroad request), so each must
remain separately diagnosable rather than collapsing into one refusal.

### Freshness discipline at commit

Approval and parent-grant state are subject to fresh reads immediately
before the derivation commits, following the same double-fetch discipline
already used for sensitive publication. The forbidden shape is:

```text
approval checked
parent checked
... state changes ...
child issued
```

without revalidation. Stale approval or a revoked parent discovered at
commit time fails the derivation rather than issuing against dead state.

### The admission fence carries no authority approval

A workflow admission attestation and an authority-issuance approval are
different security artifacts with different verifiers, lifetimes, and
meanings. Combining them would create exactly the ambient-authority
coupling Hufu exists to avoid. Derivation approval travels through the
issuance/derivation channel only.

## Explicitly not in this decision

Qingniao request fields (data-only, when the proof needs them); the
`Derive` operation and approval-tuple implementation; Biscuit changes;
nested delegation; generic delegation policy analysis; approval UI;
credential custody. The Qingniao single-level delegation model remains the
constraint to use.

## Consequences

When the Qingniao proof requires it, Hufu gains a delegability rule beside
`CanPublish`: same gate structure, separate predicate, exact-tuple
approval, freshness rechecks. Qingniao gains data-only fields to express
supervisor, delegation, generation, parent, and requested authority without
deciding anything. Until then, derivation remains available only through
the existing proof-slice mechanics, and no consumer may infer a right to
delegate from mere possession of authority.
