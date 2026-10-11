# Authority model reconciliation

Reviewed 2026-10-11 against Hufu source at `3cca79a`, the existing uncommitted
portable-resources roadmap amendment, sibling design documents, and the
[supplied proposal](archive/authority-delegation-enforcement-proposal-2026-10-11.md).
This is a source/document review, not a new runtime qualification. The resulting
[model](authority-delegation-enforcement.md) and [ADR 0014](decisions/0014-authority-delegation-enforcement.md)
govern the terminology and design direction. No implementation changes are made.

## Resolved ambiguities and contradictions

| Wording or assumption | Resolution |
| --- | --- |
| Effective authority is "part of live workflow state" | It changes in response to trusted workflow facts, but Hufu owns authority state and Zhinu owns workflow state. Preserve ADR 0011's neutral seam. |
| Start, resume or suspension changes rights automatically | These are policy inputs. A host must specify and authenticate the resulting authority transition. Retry/resume always needs fresh authorization; it does not grant rights. |
| Any authority change requires a new delegation | Expansion or changed binding requires explicit new resolution. Revocation can invalidate existing authority immediately at a suitably ordered boundary without issuing another token. Same-generation grant reuse under ADR 0012 remains valid. |
| An activity has one sandbox | It has 0..N concrete executions. Each gets a distinct binding and selected subset. Existing native-host storage already allows several handles per activity. |
| Gagamba enforces the illustrated read/write/network grants today | Published providers qualify lifecycle behavior. The separate Linux restricted profile is a design/implementation direction with outstanding qualification; do not substitute the lifecycle provider. |
| A last authority check means launch is serialized with revocation | HG-1 performs two checks but has a check-to-launch interval. Its profile does not establish the shared-writer ordering of the single-patch host. |
| A Biscuit can automatically become a child for another worker | The current adapter preserves subject/execution binding and registers narrowed descendants. Rebinding subject/audience or creating a Hufu child needs approved issuance/exchange semantics. |
| Offline attenuation means offline current authority | Biscuit supports offline cryptographic restriction, but Hufu's current profile requires registration and online current-state checks. Disconnected use would need an explicit staleness decision. |
| Parent containment proves the actor may delegate | Containment and the actor's exact derivation approval are separate. Withdrawing delegability blocks new issuance; it is not retroactive child revocation. |
| Filtering gateway tools enforces "shell denied" downstream | A gateway controls its own route. It must prove downstream restrictions/bypass closure or refuse that profile; a tool menu does not contain an external agent. |
| Provider mapping alone determines the permitted sandbox | Providers prepare exact concrete facts; the trusted caller authorizes the resulting manifest before release. Placement cannot enlarge the delegation. |
| A denied-network sandbox can call any host broker | Only explicitly prepared/qualified channels are possible. The first worker/broker protocol proof does not require sandbox networking or change Gagamba's offline profile. |

## Implementation findings and delivery limits

| Evidence in current source | What it proves and what remains |
| --- | --- |
| [AuthorityDerivation](../src/Penghou.Hufu/AuthorityDerivation.cs), [SQLite derivation](../src/Penghou.Hufu.Sqlite/SqliteAuthorityDerivation.cs), [issuance authorizer](../src/Penghou.Hufu/BoundedAuthorityIssuanceAuthorizer.cs) | Typed containment, distinct child grant and lineage, atomic child publication, idempotent derivation identity and an explicit Derive gate exist. Preserve the issuance-time approval boundary in ADR 0013. This is not proof of arbitrary policy delegation. |
| [CurrentAuthorityRequestAuthorizer](../src/Penghou.Hufu/AuthorityRequestAuthorizer.cs) | Ancestor checking is optional constructor composition. A host using derived grants must wire and qualify it; the four-argument constructor alone does not establish ancestor-dependent revocation. |
| [LineageAncestorLiveness](../src/Penghou.Hufu/AuthorityDerivation.cs) | Resolves lineage and current ancestors and checks presence/validity. It does not re-evaluate every ancestor's policy layer for the child's concrete operation. AE-1 must define/prove parent-policy changes and version binding before claiming general current-effective-authority containment. |
| [SandboxExecutionHost](../src/Penghou.Hufu.Sandbox/SandboxExecutionHost.cs) | Captures a profile revision, checks ExecuteProcess before preparation and launch, and keeps a list of handles per activity. It does not derive a child grant, translate file/network grants, mutate a running policy or use Biscuit. Execution/grant labels in the binding are correlation, not proof that derivation occurred. |
| Same native host's launch and termination paths | Authorization, provider launch and handle registration are separate steps. No common durable revocation/release arbiter is implemented here. Qualify revocation during that interval and concurrent starts before making a stronger guarantee. |
| [SandboxExecutionStep](../src/Penghou.Hufu.Zhinu/SandboxExecutionStep.cs) | `RevokeAndTerminateAsync` marks the local activity and requests termination; it does not call the Hufu authority store to revoke a grant. Durable invalidation, stop acknowledgement/failure and recovery coordination remain host obligations. |
| [BiscuitAuthorityService](../src/Penghou.Hufu.Biscuit/BiscuitAuthorityService.cs), [BiscuitProfile](../src/Penghou.Hufu.Biscuit/BiscuitProfile.cs) | The prototype independently narrows typed restrictions, preserves the root binding and checks current Hufu/Cedar. It uses its own subset routine and registry chain, not core `AuthorityDerivation` plus the optional ancestor checker. AE-3 must reconcile semantics/lineage before general child-grant transport. |
| [Biscuit profile](biscuit-integration-profile.md), [qualification](biscuit-integration-qualification.md) | Locally qualified registered read composition and a separate SQLite start fixture exist. These do not qualify worker transport, arbitrary audiences, remote gateways or mutation recovery. |
| [Gagamba README](../../Gagamba/README.md), [restricted-execution ADR](../../Gagamba/docs/adr/0003-ar7-linux-first-restricted-execution.md) | The lifecycle/restricted-execution distinction is explicit. The newer ADR supplies the future prepare/own/release contract; Hufu's frozen adapter must not be described as already implementing it. |

These findings are gaps between the narrow implementations and a generalized
claim, not authorization to broaden the code in this documentation task. Existing
qualified profiles retain their limits. Implementation work should begin with
the corresponding acceptance case in AE-1, AE-2, AE-3 or AE-4.

## Documentation disposition

- **Entry points:** README, overview and documentation index link the canonical
  model; architecture explains the ownership and enforcement paths directly.
- **Specification and runtime design:** clarify state ownership, lifecycle
  events, conditional native execution and the optional registered Biscuit
  prototype. Older illustrative capability vocabulary remains proposed.
- **Execution profiles and sample:** distinguish execution domains from qualified
  restricted execution, 0..N cardinality, fresh decisions and host-driven stop.
- **Biscuit profile:** explain current binding/registration limits and the later
  worker-to-broker proof. Retain historical qualification evidence.
- **ADRs 0010–0013:** preserve original decisions with dated reading notes where
  the newer clarification or implementation checkpoint changes interpretation.
  ADR 0014 adds the cross-cutting decision; no historical body is deleted.
- **Roadmap:** preserve the portable-resources amendment and completed evidence.
  Add dependency-based gates for current authority, a broker proof, optional
  Biscuit, restricted execution and later gateways.

Sibling documents were read as owner evidence, not rewritten here. In particular,
[Qingniao's protocol boundaries](../../Penghou.Qingniao/docs/protocol-boundaries.md)
already keep MCP/A2A outside its core, while its older
[Hufu integration note](../../Penghou.Qingniao/docs/hufu-integration.md) still calls
Hufu a scaffold. That note needs an owner-side status update before it guides new
integration work. Older cross-project workflow plans also retain pre-publication
checkpoints; Hufu's [release profile](package-release-profile.md) controls its
current package status. Gagamba's newer restricted-execution ADR controls that
future path rather than older Windows-first research summaries.

## Decisions still required by implementation

| Decision | Recommended starting point | Gate |
| --- | --- | --- |
| First credential consumer | User-selected worker → host broker, one bounded read; existing opaque context/reference first, Biscuit comparison later | AE-2 / AE-3 |
| Workflow event invalidation | Explicit host policy for suspend/cancel/complete/new generation; no automatic permission increase or old-context revival | AE-1 |
| Parent change semantics | Require live applicable restrictions; select immutable version binding or current re-evaluation, with tests for policy-only changes | AE-1 |
| Freshness and partitions | Online fail-closed at the chosen broker boundary; define in-flight completion and observable revocation acknowledgement | AE-2 |
| Worker authentication and transport | Select one protected host-owned channel; bind tenant, recipient, audience, delegation and generation; no model-visible bearer tokens | AE-2 |
| Value added by Biscuit | Keep only if portable verification/attenuation improves the proved route over an opaque reference; do not introduce a mandatory token layer | AE-3 |
| Restricted-execution release/stop order | Bind exact prepared manifest and single-use release; prove races and stop failures against the provider's actual guarantees | AE-4 |
| A2A/MCP capability mapping | Choose one real provider and tool schema, including internal actions and result disclosure; unsupported restrictions reject | AE-5 |

This leaves deployment-specific choices open without leaving the authority
semantics ambiguous. None of these rows expands the public action vocabulary,
requires a new shared package or approves a release.
