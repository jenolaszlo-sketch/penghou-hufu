# ADR 0010: implement an optional registered Biscuit profile

Date: 2026-10-02.

Status: **Experimental implementation selected; consumer qualification pending.**

## Context

Hufu's reviewed [Biscuit integration specification](../../../biscuit-sharp/docs/hufu-integration-spec.md)
selected an optional, online, registered credential profile. The implementation
must preserve current Hufu/Cedar authority and bind each permit to authenticated
context, exact resource identity, required evidence and a durable operation
start. BiscuitSharp remains outside Hufu core.

## Decision

Implement the optional adapter in Penghou.Hufu.Biscuit with a separate
Penghou.Hufu.Biscuit.Sqlite composition. The first provides the closed
hufu-biscuit-v1 mapping, immutable grant and derivation registrations,
defensive bounded envelopes, signing-key leases, typed attenuation and
verification preflight. It consumes the existing real Cedar evaluator and a
host-selected BiscuitSharp engine.

The SQLite adapter stores bounded registration, grant-version meaning,
verification evidence, per-block revocations and retired-key tombstones. Its
start participant composes those checks with a trusted runtime participant in
the same immediate transaction as Hufu/Zhinu current authority and operation
start. Required core and Biscuit verification evidence must both be acknowledged
before a verification can return Permit. A verification result never dispatches.

Use only the exact unpublished BiscuitSharp preview.2 package pinned by
eng/Restore-BiscuitCandidate.ps1; do not treat this as a published dependency
contract. The core project remains independent of BiscuitSharp and SQLite.

## Consequences and open gate

The adapter is a local experimental prototype. Host authentication, issuer
ceilings, approval policy, key custody, Cedar configuration, resource binding,
registry authorization and runtime participant composition remain trusted host
responsibilities. The start policy orders new starts against revocation and
key retirement in the shared physical SQLite database. Starts committed before
a tombstone may complete, and AlreadyStarted cannot redispatch.

The local test suite exercises the real Cedar and Biscuit engines and disk
SQLite stores, but its runtime participant is a SQL fixture. Windows Local/Luban and Hufu.IO reads are locally qualified against candidate
packages; alias/junction boundaries have narrow evidence. Actual governed mutation
and terminal outcome recovery remain open. Complete the real consumer and enforcement
conformance gate before freezing or publishing the optional adapter.

See the [profile](../biscuit-integration-profile.md) and
[qualification](../biscuit-integration-qualification.md).
