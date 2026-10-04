# Current authority profile

Status: Experimental implementation, 2026-10-01. This is a narrow M1 foundation
and Luban read integration, extended by an optional current-state/evidence
SQLite prototype described in the [store profile](durable-authority-store.md).
The approval/delegation lifecycle, governed mutation admission, and production
authority runtime remain pending. Six preview.1 packages are published; API
compatibility is checked against that baseline while the library remains prerelease.

The optional [core admission and issuance profiles](core-admission-and-issuance.md)
now provide shared evaluation capacity and finite typed-scope publication
checks. Their exact limits and current evidence are documented separately.

The bounded [decision explanation profile](decision-explanations.md) captures
actual evaluator outcomes and typed snapshot facts. A separate authenticated
host policy authorizes disclosure; explanation results do not authorize I/O.

Optional [authorization telemetry](optional-telemetry.md) records closed categories
and timing through a finite worker queue. It neither changes authorization nor
substitutes for mandatory evidence; broker/runtime instrumentation remains open.

## Supplied implementation

`Penghou.Hufu` supplies immutable authority snapshots, typed actions and path
scopes, structural scope containment, neutral decisions, and required host
snapshot/evidence interfaces. It has no Cedar, workflow, database, or resource
provider dependency. Constructing a snapshot or context does not confer rights.
The host must authenticate identity and resolve current authority from its
authoritative state.

`Penghou.Hufu.Cedar` consumes the published CedarSharp **1.0.0** package. It
projects typed grants into a fixed schema and policy/entity bundle; callers
cannot supply Cedar text, arbitrary entity parents, or a second policy store.
The adapter validates every policy layer and the complete request before
evaluation. Native identity mismatches, bridge failure, invalid projections,
policy diagnostics, and Allow-with-errors block permission.

`Penghou.Hufu.Luban` supplies `HufuLanguageAuthorizer`. It binds one trusted
invocation to one compiled read document and requires a
host-selected IAuthorityRequestAuthorizer. The convenience overload composes
IAuthoritySnapshotSource, IAuthorityEvaluator, and IAuthorityDecisionRecorder
through CurrentAuthorityRequestAuthorizer. The optional Biscuit verifier uses
the same asynchronous seam. There is no default permissive implementation.
Missing current state, inconsistent decisions, unavailable evaluation, or failed
required evidence blocks dispatch or release.

## Authority semantics

- Context binds tenant, subject, run, revision, and runtime fence. Requests must
  exactly match the snapshot context. These strings identify authenticated host
  facts; the library does not authenticate their origin.
- Each of one to eight authority layers must independently permit the request.
  An empty layer denies. Grants within a layer are alternatives; a grant-local
  exclusion can be covered by another grant in that layer.
- Mandatory denials are Cedar forbid rules in every layer and cannot be
overridden by any grant. They apply to all six current actions.
- Exact scopes match one canonical path. Subtree scopes match that path and
  descendants at whole path-segment boundaries in the same workspace. An
  exclusion must be structurally contained in its grant scope.
- Grant validity is `NotBefore <= now < ExpiresAt`; snapshots expire at
  `ValidUntil`. Required recording must preserve snapshot expiry and every grant active state;
  even an unrelated validity transition invalidates the captured Permit.
  Neither an old permit nor successful preflight is a capability
  for subsequent I/O.

The action set is ReadFile, ListDirectory, ReadMetadata, PatchFile, Release, and
WriteFile. PatchFile is available for policy evaluation only: this integration
does not admit or execute patches. WriteFile is reserved for the optional Hufu.IO
conditional byte-write profile; it does not imply PatchFile authority or operation
start ordering.

Canonical resource paths are bounded relative paths with `/` separators and
ASCII A-Z folded to lowercase; other Unicode is preserved exactly. Traversal,
absolute paths, empty interior segments, wildcards, invalid UTF-16, and trailing
dot/space segments are rejected. Empty path means the workspace root. Generic
typed path validation is not filesystem resolution. The Luban adapter also uses
the qualified Windows workspace-path validation; the shared Local provider
remains responsible for actual resource identity and link/case handling. It now
checks native final paths for admitted spelling before attribute/content use;
Unicode case aliases and DOS short names fail closed. This is not confinement.

Snapshots defensively copy caller collections. Current ceilings are eight
layers, 128 grants and 128 exclusions across the snapshot, 128 mandatory denials,
six distinct actions per grant, 16 path segments, 512 UTF-16 path code units /
2048 UTF-8 bytes, and a one MiB canonical snapshot encoding. Tokens are at most
256 UTF-16 code units / 1024 UTF-8 bytes. A rejected bound does not widen scope.

## Luban read mapping

Existing constructors select `HufuLanguageAuthorityProfile.ReadV1`. The new
explicit `ReadAndDiffV2` profile supports v2 bounded reads and static diffs; see
[usage, mapping and callback limits](luban-v2-authorization.md). The mapping
below describes v1 and unchanged known-root read/find/search behavior.

The supported catalogue/provider versions are Luban's current
`windows-read-v1` / `local-windows-read-v1` profile. Static Read, Find, and Search
effects can be followed by the existing pure Take/Count filters. Dynamic Read
or Search input pipelines return Unavailable during preflight, before I/O,
until their scope-ceiling mapping is qualified. Unknown profiles and forged
document, node, descriptor, stage, invocation, or workspace bindings deny.

Semantic preflight, effect-start, and release checks conservatively require:

| Stage | Known scope requirements |
| --- | --- |
| Read | ReadFile at the file, ReadMetadata at root and each ancestor including the file, Release at the file |
| Find | ListDirectory at its root, ReadMetadata at root and each ancestor including that root, Release at its root |
| Search | ListDirectory and ReadFile at its root, ReadMetadata at root and each ancestor including that root, Release at its root |

The Search root requirement is deliberately conservative: exact child-only
read grants do not satisfy it. It is not a grant to discovered descendants.
Concrete resource checks independently authorize their actual path and action;
metadata probes, content reads, directory lists, and output release retain the
same exclusions. Retained concrete paths also receive Release checks before
buffered results are exposed. A parent grant with an excluded child must not
disclose that child's path or content.

Each callback fetches a current snapshot. Each distinct mapped requirement
produces a decision with the exact snapshot identity, snapshot version, evaluator
identity, and a digest binding invocation/document/node/workspace/phase/action/
path/resource-request identity. The required recorder must acknowledge the
decision before it can be used. Recording unavailable or denied decisions
does not make them permits.

## Evidence and qualification boundary

Snapshot and evaluator identities use SHA-256 over versioned deterministic
encodings. Resource UIDs bind tenant, workspace, and path; subject UIDs also bind
run, revision, and fence. Only trusted request ancestry is projected into Cedar.
Engine ABI, Cedar SDK/language, bridge, features, target, Rust compiler, native
binary digest, and mapping/schema identities are reflected in evaluator identity.
Compatibility checks pin ABI 1, SDK 4.13.0, language 4.5, bridge 0.1.0, and the
datetime/decimal/ipaddr feature set and supported wrapper targets.

`EvaluateDetailed` retains raw Cedar results, policy/request validation,
native identity, and schema/policy/entity/snapshot digests for a trusted host.
These details are not agent-visible diagnostics. The neutral recorder contract
alone is not a complete durable audit store: a production host must additionally
retain the evaluated instant, authenticated issuance/current-state provenance,
and the detailed evaluator evidence needed for explanation and replay. The
optional SQLite adapter now persists immutable snapshots and bounded host-captured evidence; it provides no cryptographic attestation. See the [store qualification](durable-authority-store-qualification.md).

The fixed, bounded policy vocabulary avoids arbitrary caller policy evaluation.
Native Cedar calls are synchronous; these APIs do not claim to interrupt native
evaluation at a hard timeout. Consumer qualification is currently local Windows
x64 on .NET 8 and .NET 10; wrapper CI coverage is not Hufu consumer qualification
on other operating systems. See the [qualification record](authority-profile-qualification.md).

## Next protected-operation gate

Fetching Hufu state and subsequently acquiring a Zhinu operation handle is not
an atomic start protocol. The host must serialize current authority, revocation,
revision/fence validation, and durable operation start at one authoritative
boundary, then check the actual provider object before I/O. Returning Permit
from this read adapter does not prove that no I/O can occur after a concurrent
revocation acknowledgement.

Zhinu's captured operation generation must equal both the requested generation
and current run generation at acquire. Its restart regression is repaired, but
that alone does not establish Hufu authority ordering. Luban batch execution
continues to require its real durable host protocol and HostControlled namespace;
this read adapter is not an implementation of that host.

The current-state/evidence store and a narrow [co-located Hufu/Zhinu start
transaction](operation-start-profile.md) are implemented. The read adapter's
sequential source/recorder calls do not use that transaction automatically.
The next implementation step connects a real governed single-patch host and
exact terminal-outcome recovery to the start gate. Historical decision replay must remain separate
from current permission. No sandbox implementation, arbitrary shell fallback,
or broad mutation readiness is supplied by this slice.
