# ADR 0012: First-class derived authority lineage in Hufu core

Date: 2026-10-07.

Status: **Proposed.** Direction accepted; no implementation until the proof
slice below. In particular: no parent pointers in Qingniao, no Biscuit types
in Qingniao, no cascading descendant mutation, no generic authority DAG, no
nested delegation, no new activity identity, no Guihua changes.

## Context

The Marang fs.read slice proved a real consumer can use Hufu admission with
Hufu learning nothing about the consumer. The Qingniao/Hufu issuance survey
then found the next missing abstraction, and it is one coherent abstraction
rather than several: Qingniao has no parent authority, no requested-authority
fields, and no attenuation primitives, while Hufu core has no derivation
constructor, no subset proof, no parent linkage in its records, and no
cascade semantics outside Biscuit tokens.

Biscuit has already proven the attenuation semantics core needs
(`AttenuateAsync`, `IsSubset` over actions/scope/exclusions/validity, chain
re-proof, liveness-gated usability). But making Qingniao delegation depend on
Biscuit would let one credential technology define Hufu's authority
architecture. The dependency must run from Hufu semantics down to Biscuit
representation, never the reverse.

## Decision

Derivation moves into Hufu core. A derived grant is a first-class
`AuthorityGrant` with a distinct identity and explicit parent lineage.
Biscuit remains an optional credential/transport representation of grants
whose lineage core already understands, and should eventually call core
subset logic rather than maintaining a semantically independent
implementation. There is one definition of attenuation in Penghou.

### Derivation request and result

Conceptually:

```csharp
AuthorityGrantDerivationRequest
{
    ParentGrantId
    ChildSubject
    RequestedActions
    RequestedScope
    RequestedExclusions
    NotBefore
    ExpiresAt

    // stable caller-owned identity
    DerivationIdentity
}

AuthorityGrantDerivation
{
    ParentGrantId
    ChildGrant
    DerivationIdentity
    Evidence
}
```

The important point is that **Hufu creates the child grant**. Qingniao does
not construct an `AuthorityGrant` and ask Hufu whether it happens to fit.
The operation means: given authority P, derive the strongest requested
authority C that is valid under P, or reject it.

For v1, reject rather than silently shrink. If Qingniao asks for `write`
under a `read`-only parent, Hufu returns a denial stating that the requested
child authority is not contained by the parent — it does not quietly create
a read-only child. Silent attenuation would make the execution differ from
the requested delegation.

### Containment predicate

Core owns one authoritative predicate over grants:

```csharp
IsContainedBy(child, parent)
```

covering:

```text
child.actions     ⊆ parent.actions
child.scope       ⊆ parent.scope
child.exclusions  ⊇ parent-required exclusions
child.validity    ⊆ parent.validity
```

### Ancestor liveness, not cascading mutation

A derived grant is usable if and only if it is itself live and every
ancestor in its grant lineage is live. Revoking P does not mutate C: C
remains historically present, but admission using C fails because ancestor P
is no longer live. The record can then truthfully say `issued` with
`effective: false (ancestor revoked)` instead of rewriting history as if C
never existed.

### Child identity binding

The child grant binds to the existing stable Qingniao `DelegationId`
rather than a prematurely invented activity identity. If Qingniao later
grows multiple activities inside a delegation, that will be the evidence
for adding an activity identity. Not now.

```text
Supervisor authority
        |
        | derive
        v
Grant C bound to DelegationId D
        |
        v
Qingniao executes D → Marang operation → fresh Hufu admission using C
```

### Retry discipline

Grant derivation is idempotent on `(parent grant, delegation id, generation,
requested authority)`: an ordinary attempt retry reuses the same child
grant, while a semantic re-execution (new generation) may derive a new one.
The old grant expires or is revoked per lifecycle policy. This fits
Qingniao's existing identity discipline instead of creating another
lifecycle: same delegation plus same generation plus retry reuses one grant
lineage; a new generation starts a new derived grant.

## Frozen for this checkpoint

1. Core `AuthorityGrant` is the semantic authority object.
2. Derived grants have distinct IDs and explicit parent lineage.
3. Hufu, not the consumer, proves containment.
4. Descendant usability depends on ancestor liveness.
5. Qingniao retry identity determines grant reuse and replacement.

## Explicitly not in this checkpoint

Parent pointers in Qingniao before the Hufu contract exists; Biscuit types
in Qingniao; cascading mutation of descendants; a generic authority DAG;
nested delegation trees; a new activity identity; Guihua changes. The
current Qingniao single-level delegation model is a constraint to use, not
a limitation to remove yet.

## Proof slice (next, not this ADR)

Host/supervisor grant P → derive child grant C for DelegationId D → C is
narrower than P → Marang fs.read using D/C succeeds → revoke P → the same
child C fails on next read. Success means no consumer needed to understand
lineage internals, which places Hufu's lifecycle boundary where it belongs.

## Consequences

Hufu gains a derivation concept its store, records, and evaluators must all
respect: lineage fields, subset proof, liveness-gated usability, and
idempotent derivation identity. Biscuit keeps working unchanged in the short
term but acquires a migration direction toward core subset logic. Qingniao
gains a design target for its requested-authority and parent-linkage fields
without writing them yet. The Marang adapter pattern (fresh admission per
operation) transfers unchanged; only the grant behind it becomes derived.

## Implementation notes (proof slice)

Two deliberate limitations, recorded so they read as deferred decisions
rather than oversights:

- **Derivation authorization is not integrated with issuance approval.**
  Containment is always proven and the issuing actor is always recorded, but
  nothing here decides whether possession of authority implies authority to
  delegate it. A grant permitting `read src/**` does not necessarily permit
  deriving a child carrying it for another subject. That delegability
  question is the next integration pressure point (likely Qingniao's), not
  part of this proof.
- **Requested child validity is explicit; inheritance is unsupported.**
  Callers supply `NotBefore`/`ExpiresAt`, which must already fit inside the
  parent window. There is no "inherit the parent bounds" semantics, keeping
  the derivation identity complete and free of hidden defaults.
