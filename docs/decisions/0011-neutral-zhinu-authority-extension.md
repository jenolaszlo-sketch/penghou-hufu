# ADR 0011: Product-neutral workflow authorization and optional Hufu adapter

Status: direction selected at the user's request, 2026-10-03. Implementation
and final public API design remain pending. The canonical cross-project
[delivery plan](../../../Penghou.Zhinu/docs/authority-extension-plan.md) and
[original proposal](../../../Penghou.Zhinu/docs/proposals/2026-10-03-authority-extension.md)
govern this migration, subject to the later product-neutral ownership/naming and sequential publication clarification in the Penghou plan.

## Decision

Penghou defines `IExecutionAuthorizer` and neutral execution context/result/
requirement/identity contracts in `Penghou.Workflow.Abstractions`, owned by the
[Penghou repository](../../../Penghou/docs/workflow-abstractions-plan.md).
Zhinu is one workflow runtime implementation. It invokes the
seam before protected activity user code, owns durable approval waits/retries/
resume and keeps its explicit no-provider allow-all default. A configured
provider failure cannot fall back to that default.

`Penghou.Hufu.Workflow` implements the contract with dependencies on Hufu core and
Penghou.Workflow.Abstractions only. It translates trusted execution context and versioned
declared requirements into Hufu decisions. Hufu retains policy, approvals,
delegation, revocation and decision provenance; Zhinu retains workflow state,
leases, attempts and persistence. Identity remains independently authenticated
by the host. Metadata and old receipts confer no current authority.

Hufu core, Cedar and Biscuit remain independent of every Zhinu package. The
current core already meets that project boundary; move Zhinu-specific tests
out of `Penghou.Hufu.Tests` so test dependencies express the same separation.
The new adapter must not query workflow SQL or depend on the full runtime.

Hufu.IO and Hufu.Luban remain separate integrations. Workflow preflight does
not replace semantic admission, real per-resource checks, discovered-resource
checks or the locked mutation-start boundary. Physical/virtual providers stay
behind IO contracts. Luban requires no workflow runtime change for this work.

## Relationship to ADR 0009

[ADR 0009](0009-colocated-operation-start.md) and its qualification describe the
existing experimental shared-SQLite start profile. Freeze it as legacy
integration evidence; it is not the default target package structure. Its
atomic revocation/start guarantees are not supplied by an asynchronous activity
preflight callback. ZA-5 must document neutral cooperation with equivalent
required guarantees or explicitly retain the isolated legacy profile until
retirement is justified. Never move direct workflow SQL into the new adapter
or silently remove an established effect guarantee.

The recent preview.15 package migration remains valid for the legacy profile;
it is not completion of this inversion. Existing six-package candidates and
public API inventories are candidate evidence only. Review the release set
after the new adapter and abstraction boundary are qualified.

## Consequences and acceptance

New abstractions extraction must preserve compatible public type identity where
possible, with forwarders/old-binary tests or documented preview breaks. Define
unavailable/error and durable approval transitions before freezing API. Every
retry/resume that executes protected work receives fresh authorization; record
bounded evidence and recheck runtime fences after asynchronous evaluation.
Historical reconstruction cannot reuse an allow to dispatch fresh work.

Follow WA-1/2/3 in Penghou first (contract design, implementation and user-run
publication), then ZA-2/3/4/6 in Zhinu, then HA-1/2/3 in Hufu, including
negative transitive-dependency tests, standalone builds, exact package-backed
consumers and .NET 8/10 tests. No code or dependency changes are made by this ADR.
The [activity queue](../../../Penghou.Zhinu/docs/authority-extension-activities.md)
adds early independent HA-0 staged-work/test isolation and ZA-5A legacy design
disposition. WA-2 contract candidates precede WA-3 publication; the workflow adapter uses exact published contracts after the completed Zhinu phase. HA-3 qualifies the separate Hufu release.
