# Exact-operation decision explanations

Implemented 2026-10-04 as a bounded Hufu core profile, with optional capture in
`Penghou.Hufu.Cedar`. See [qualification](qualification/decision-explanations.json)
and the [release handoff](zhinu-authority-handoff.md). No workflow engine is
required, and existing authorization behavior is unchanged.

## Capture the actual evaluation

```csharp
using Penghou.Hufu;
using Penghou.Hufu.Cedar;

var evaluator = new CedarAuthorityEvaluator();
AuthorityDecisionExplanation capture = evaluator.EvaluateExplained(
    authenticatedSnapshot, exactRequest, evaluationInstant);

var reader = new AuthorityExplanationReader(hostExplanationAccessPolicy);
AuthorityExplanationRead read = await reader.ReadAsync(
    presentedViewer, capture, cancellationToken);
```

`EvaluateExplained` invokes the existing `EvaluateDetailed` once. It retains the
actual overall decision, each available layer classification, schema/entity/policy
digests and the normalized evaluation instant. Invalid or cross-context capture
arguments throw; no explanation is attached to a different snapshot/request.
Other evaluators can construct the core capture from their own authenticated
results. Construction and identifiers provide no proof of provenance.

An immutable explanation identity binds the profile, exact canonical request,
snapshot identity/version, actual decision and evaluator identity, instant,
optional schema/entity identities and ordered layer outcomes. Changes to those
inputs change the identity. Snapshot identity binds all its grants, exclusions,
mandatory denials, target context and expiry.

The typed facts explain each grant's action and scope match, validity interval,
active/future/expired state and matching local exclusions. Matching mandatory
denials remain separate. A local exclusion does not invalidate another grant's
coverage; each layer's captured evaluator outcome remains authoritative. Grant
facts describe typed snapshot predicates, not Cedar determining-policy IDs or
claims that a particular grant caused the result.

`CapturedLayerOutcomes` means all layer classifications were captured.
`SnapshotFactsOnly` explicitly marks missing or unevaluated layers, including
the early expired-snapshot path. Neither means complete lineage, execution
readiness or general policy analysis. Policy-error flags remain visible only in
authorized details; native errors and raw diagnostics are omitted. Unknown
reason text becomes the closed `Unknown` reason, never a guessed explanation.

## Independently authorize disclosure

The required `IAuthorityExplanationAccessPolicy` authenticates the viewer
through trusted host credentials/context and decides whether that viewer may
read this exact explanation. This permission is independent of execution
rights: an authorized auditor can inspect a denied operation without receiving
permission to perform it.

A valid access result requires `Permit`, the exact authenticated tenant/actor/
session, the exact explanation identity, a recognized disclosure level and a
future `ValidUntil`. The policy must authenticate these facts; echoing caller
fields is not authentication. Every read invokes it again. Cross-tenant or
malformed viewers deny before policy activation. Missing, mismatched, expired,
unknown or failed policy results disclose no projection. Explicit policy denial
returns `Denied`; infrastructure/validation failures return `Unavailable`.

- **Summary** is the default disclosure level. Its projection contains only
  `DecisionStatus` and null `Details`. It exposes no request/path, identity,
  policy reason, grant/layer count or evaluator diagnostics. Even summary access
  requires the independent policy.
- **Detailed** explicitly authorizes all typed facts in this profile, including
  the exact request, identities, scopes, matching exclusions/mandatory denials,
  validity intervals and layer outcomes. Hosts must grant this level only when
  those resources and policies may be disclosed to the viewer. Partial field
  disclosure is not supported by this version.

Successful disclosure returns `Disclosed` with a projection. This is a read
result, never an authority permit, start token or mandatory evidence receipt.
Serialize the returned projection; the capture is host-only and retains the
raw decision reason. It must not be serialized directly to an agent.

## Bounds and remaining scope

Snapshots retain the existing ceilings of eight layers, 128 grants and 128
exclusions/mandatory denials. Layer outcomes must match every snapshot layer in
order or be absent; unknown enums, invalid hashes and contradictory captured
permits reject. Nested fact collections are frozen.

The reader checks the default `System.Text.Json` UTF-8 projection encoding
against a one MiB ceiling, returning no partial projection if exceeded. It
rechecks access expiry and its finite evaluation budget after projection.
The budget defaults to 30 seconds and must be positive and at most five minutes.
It bounds asynchronous policy waits; it cannot terminate synchronous blocking
or work that ignores cancellation. External cancellation propagates. Clock
rewind, timeout and late expiry prevent disclosure.

The host separately authorizes evidence retrieval and custody before supplying
the capture. This API performs no historical store lookup, protected resource
access, policy installation, simulation or approval. It explains the evaluator
outcome at the supplied snapshot/instant; required-recording failures and later
resource/runtime checks may still block execution. Fresh current authority,
mandatory decision evidence and actual effect/start checks remain required.

Full lineage, missing execution requirements, sufficient-authority proposals,
state comparisons, counterfactual simulation and historical reconstruction with
pinned retained evidence remain roadmap work. Sending a projection to an AI
provider is a separately governed data release. The [bounded request telemetry slice](optional-telemetry.md)
is implemented separately; it exports no explanation data or protected correlation.
