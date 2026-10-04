# Luban v2 read/diff authorization

Status: published in Hufu `0.1.0-preview.2`. This adapter consumes
published Luban and IO `0.1.0-preview.1` packages; it requires no Luban or Zhinu
source changes. [Qualification evidence](qualification/luban-v2-authorization.json)
records the local implementation checks. [Public release evidence](qualification/hufu-luban-v2-public-release.json)
confirms all six indexed packages, three-platform CI/publication and fresh .NET 8/10 consumers.
Preview.1 remains the compatibility baseline used to qualify this release.

## Host composition

Existing `HufuLanguageAuthorizer` constructors retain the v1 read profile and
reject v2 documents before calling authority services. Hosts explicitly select
the new closed profile when compiling and authorizing a v2 document:

```csharp
using Penghou.Hufu;
using Penghou.Hufu.Luban;
using Penghou.IO.Abstractions;
using Penghou.Luban;
using Penghou.Luban.Language;

var profile = HufuLanguageAuthorityProfile.ReadAndDiffV2;
var compiled = LanguageCompiler.Compile("diff before.txt after.txt",
    new WorkspaceId("workspace"),
    new LanguageCompilerOptions(Versions: profile.Versions));
if (!compiled.Succeeded) throw new InvalidOperationException("Invalid document.");

// These services and identities come from the authenticated host.
var current = new CurrentAuthorityRequestAuthorizer(source, evaluator, recorder, clock);
var authorizer = new HufuLanguageAuthorizer(context, invocation, compiled.Document!,
    profile, current);
// Inject this authorizer into LanguageRuntime with the selected trusted provider.
```

An evidenced `IAuthorityRequestAuthorizer` is mandatory. The example's `source`,
`evaluator`, `recorder`, `clock`, `context` and `invocation` are host inputs;
the adapter supplies no credentials or permissive checker. The profile fixes
language/IR `2`, catalogue `windows-text-change-v2`, provider
`local-windows-read-v1`, and descriptor version `1`.

## Supported shape and requirements

V2 supports known-root Read (including bounded line windows), Find, Search
(including bounded context), static two-path Diff, and pure Take/Count.
The entire document is checked at construction. Merge and all other unsupported
stages reject before authority activation. Dynamic reads/searches with null
roots return Unavailable rather than falling back to workspace-wide authority.
Compiler/provider bounds and safety rules remain in Luban and IO.

| Callback | Required authority |
| --- | --- |
| Diff preflight, effect-start and stage release | ReadFile and Release at each distinct input path; ReadMetadata at root and every ancestor including the input |
| Identity-free diff target admission | The same requirements for that exact input, before opening a provider reader |
| Concrete content access | ReadFile only at either exact compiled input; a valid resource request identity is required |
| Concrete metadata access | ReadMetadata only at an ancestor of either input (including root and the input); a valid resource request identity is required |
| Concrete release | The corresponding concrete action and Release at its actual path, with its resource request identity |

Duplicate action/path requirements are evaluated once per callback. No diff
callback grants directory listing, descendant reads, WriteFile or PatchFile.
Each mapped decision re-enters the host authorizer; current state and mandatory
evidence are required. Denial, missing evidence, mismatched results or unavailable
services block access or protected result release. Cancellation propagates.

The published Luban runtime sends target admission as `ResourceAccess` with
`ReadFile`, an exact input path and no resource request identity. The exception
for that shape is restricted to Diff; identity-free Read, metadata and concrete
release deny. Target admission is semantic permission before I/O, not proof of an
observed resource access.

V2 decision digests bind the exact version tuple, descriptor version, complete
Hufu context, invocation, compiled document/node identities, workspace, phase,
action, canonical path and resource request identity. Compiled identities bind
input roles and diff options. Existing v1 constructor signatures and decision
digest bytes are preserved; hosts must not treat either digest as a reusable grant.

## Release and evidence limits

Diff produces a read-only, untrusted candidate. Authorizing its reads or release
does not authorize applying that candidate. `LanguageAuthorizationRequest` does
not expose the FileChange operation identity, observations, content hashes,
outcome or candidate identity. Hufu therefore binds callback evidence to compiled
semantics and paths, not to the resulting diff object.

Successful execution retains identity-bearing resource release callbacks in
addition to stage release. The published runtime may issue only stage release on
failure after reading, and cancellation/exception paths may return without those
release callbacks. Hufu requires evidence for callbacks actually performed; it
does not promise unconditional per-resource release evidence on every outcome.
Hosts must preserve these limits when designing journals or disclosure policy.

Real filesystem qualification here uses the Windows Local provider. Portable
mapping and package-consumer checks run on Linux/macOS as well; they do not
qualify a Local filesystem provider on those platforms. There is no confinement
or atomic revocation/read transaction claim. A governed single-patch host with
locked-object start ordering and durable outcome recovery is the next independent
roadmap delivery.
