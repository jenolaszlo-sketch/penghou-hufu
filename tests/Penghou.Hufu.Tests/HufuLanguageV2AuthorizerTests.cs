using Penghou.Hufu;
using Penghou.Hufu.Luban;
using Penghou.IO.Abstractions;
using Penghou.Luban;
using Penghou.Luban.Changes;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class HufuLanguageV2AuthorizerTests
{
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");
    private static readonly WorkspaceId Workspace = new("hufu-v2-portable-workspace");
    private static readonly LanguageVersions V2 = HufuLanguageAuthorityProfile.ReadAndDiffV2.Versions;

    [Fact]
    public async Task StaticDiffPhasesAuthorizeBothInputsAndDeduplicateRepeatedPaths()
    {
        var document = Compile("diff src/before.txt proposal/after.txt");
        var invocation = Invocation();
        foreach (var phase in new[] { LanguageAuthorizationPhase.Preflight, LanguageAuthorizationPhase.EffectStart,
                     LanguageAuthorizationPhase.Release })
        {
            var authority = new RecordingAuthorizer();
            var authorizer = Create(document, invocation, authority);
            Assert.Equal(LanguageAuthorityStatus.Permit,
                (await authorizer.AuthorizeAsync(Request(document, invocation, phase))).Status);

            var pairs = authority.Requests.Select(r => (r.Action, r.RelativePath)).ToHashSet();
            Assert.Contains((AuthorityAction.ReadFile, "src/before.txt"), pairs);
            Assert.Contains((AuthorityAction.ReadFile, "proposal/after.txt"), pairs);
            Assert.Contains((AuthorityAction.Release, "src/before.txt"), pairs);
            Assert.Contains((AuthorityAction.Release, "proposal/after.txt"), pairs);
            Assert.Contains((AuthorityAction.ReadMetadata, ""), pairs);
            Assert.Contains((AuthorityAction.ReadMetadata, "src"), pairs);
            Assert.Contains((AuthorityAction.ReadMetadata, "proposal"), pairs);
            Assert.DoesNotContain(pairs, p => p.Action is AuthorityAction.WriteFile or AuthorityAction.PatchFile or AuthorityAction.ListDirectory);
            Assert.Equal(authority.Requests.Count, pairs.Count);
        }

        var repeated = Compile("diff same.txt same.txt");
        var repeatedAuthority = new RecordingAuthorizer();
        var repeatedAuthorizer = Create(repeated, invocation, repeatedAuthority);
        Assert.Equal(LanguageAuthorityStatus.Permit,
            (await repeatedAuthorizer.AuthorizeAsync(Request(repeated, invocation, LanguageAuthorizationPhase.Preflight))).Status);
        Assert.Equal(repeatedAuthority.Requests.Count, repeatedAuthority.Requests
            .Select(r => (r.Action, r.RelativePath)).Distinct().Count());
        Assert.Single(repeatedAuthority.Requests, r => r.Action == AuthorityAction.ReadFile && r.RelativePath == "same.txt");
    }

    [Fact]
    public async Task DiffAdmissionAndConcreteCallbacksHaveClosedExactScopes()
    {
        var document = Compile("diff src/before.txt proposal/after.txt");
        var invocation = Invocation();
        var authorizer = Create(document, invocation, new RecordingAuthorizer());
        var node = document.Statements[0][0];

        foreach (var path in new[] { "src/before.txt", "proposal/after.txt" })
        {
            var admission = await authorizer.AuthorizeAsync(Request(document, invocation,
                LanguageAuthorizationPhase.ResourceAccess, node, path, ResourceAction.ReadFile));
            Assert.Equal(LanguageAuthorityStatus.Permit, admission.Status);
            var read = await authorizer.AuthorizeAsync(Request(document, invocation,
                LanguageAuthorizationPhase.ResourceAccess, node, path, ResourceAction.ReadFile, "read-id"));
            Assert.Equal(LanguageAuthorityStatus.Permit, read.Status);
            var release = await authorizer.AuthorizeAsync(Request(document, invocation,
                LanguageAuthorizationPhase.Release, node, path, ResourceAction.ReadFile, "read-id"));
            Assert.Equal(LanguageAuthorityStatus.Permit, release.Status);
        }

        var metadata = await authorizer.AuthorizeAsync(Request(document, invocation,
            LanguageAuthorizationPhase.ResourceAccess, node, "src", ResourceAction.ReadMetadata, "meta-id"));
        Assert.Equal(LanguageAuthorityStatus.Permit, metadata.Status);

        var rejected = new[]
        {
            Request(document, invocation, LanguageAuthorizationPhase.ResourceAccess, node, "src/other.txt", ResourceAction.ReadFile, "id"),
            Request(document, invocation, LanguageAuthorizationPhase.ResourceAccess, node, "proposal/after.txt/child", ResourceAction.ReadFile, "id"),
            Request(document, invocation, LanguageAuthorizationPhase.ResourceAccess, node, "src-neighbor", ResourceAction.ReadMetadata, "id"),
            Request(document, invocation, LanguageAuthorizationPhase.ResourceAccess, node, "src/before.txt", ResourceAction.ListDirectory, "id"),
            Request(document, invocation, LanguageAuthorizationPhase.Release, node, "src/before.txt", ResourceAction.ReadFile),
            Request(document, invocation, LanguageAuthorizationPhase.ResourceAccess, node, "src/before.txt", (ResourceAction)999, "id"),
        };
        foreach (var request in rejected)
            Assert.Equal(LanguageAuthorityStatus.Deny, (await authorizer.AuthorizeAsync(request)).Status);
    }

    [Fact]
    public async Task IdentityFreeAdmissionCannotAuthorizeOtherStagesOrActions()
    {
        var document = Compile("read src/before.txt");
        var invocation = Invocation();
        var authorizer = Create(document, invocation, new RecordingAuthorizer());
        var node = document.Statements[0][0];
        var noIdentity = Request(document, invocation, LanguageAuthorizationPhase.ResourceAccess, node,
            "src/before.txt", ResourceAction.ReadFile);
        Assert.Equal(LanguageAuthorityStatus.Deny, (await authorizer.AuthorizeAsync(noIdentity)).Status);
        Assert.Equal(LanguageAuthorityStatus.Deny, (await authorizer.AuthorizeAsync(noIdentity with { Action = ResourceAction.ReadMetadata })).Status);
    }

    [Fact]
    public void ProfileSelectionIsClosedAndDefaultsRemainV1()
    {
        var v2Document = Compile("diff before.txt after.txt");
        var v1Document = Compile("read before.txt", new LanguageVersions());
        var invocation = Invocation();
        var untouched = new RecordingAuthorizer();

        Assert.Throws<ArgumentException>(() => new HufuLanguageAuthorizer(Context, invocation, v2Document, untouched));
        Assert.Throws<ArgumentException>(() => new HufuLanguageAuthorizer(Context, invocation, v2Document,
            HufuLanguageAuthorityProfile.ReadV1, untouched));
        Assert.Throws<ArgumentNullException>(() => new HufuLanguageAuthorizer(Context, invocation, v2Document,
            null!, untouched));

        var merge = Compile("merge base.txt ours.txt theirs.txt");
        Assert.Throws<ArgumentException>(() => new HufuLanguageAuthorizer(Context, invocation, merge,
            HufuLanguageAuthorityProfile.ReadAndDiffV2, untouched));
        Assert.Empty(untouched.Requests);

        var v1 = new HufuLanguageAuthorizer(Context, invocation, v1Document, untouched);
        Assert.NotNull(v1);
        Assert.Equal(new LanguageVersions(), HufuLanguageAuthorityProfile.ReadV1.Versions);
        Assert.Equal(V2, HufuLanguageAuthorityProfile.ReadAndDiffV2.Versions);
    }

    [Theory]
    [InlineData("find . *.txt | read")]
    [InlineData("find . *.txt | search needle")]
    public async Task DynamicNullScopesRemainUnavailableWithoutRootFallback(string source)
    {
        var document = Compile(source);
        var invocation = Invocation();
        var authority = new RecordingAuthorizer();
        var authorizer = Create(document, invocation, authority);
        var node = document.Statements.SelectMany(s => s).First(n => n.Stage is ReadStage { Path: null } or SearchStage { Root: null });

        var result = await authorizer.AuthorizeAsync(Request(document, invocation, LanguageAuthorizationPhase.Preflight, node));

        Assert.Equal(LanguageAuthorityStatus.Unavailable, result.Status);
        Assert.Empty(authority.Requests);
    }

    [Theory]
    [InlineData("deny", LanguageAuthorityStatus.Deny)]
    [InlineData("unavailable", LanguageAuthorityStatus.Unavailable)]
    [InlineData("missing-evidence", LanguageAuthorityStatus.Unavailable)]
    [InlineData("wrong-request", LanguageAuthorityStatus.Unavailable)]
    [InlineData("throw", LanguageAuthorityStatus.Unavailable)]
    public async Task AuthorityFailuresFailClosed(string mode, LanguageAuthorityStatus expected)
    {
        var document = Compile("diff before.txt after.txt");
        var invocation = Invocation();
        var authority = new RecordingAuthorizer(request => mode switch
        {
            "deny" => Evidence(request, AuthorityStatus.Deny),
            "unavailable" => Evidence(request, AuthorityStatus.Unavailable),
            "missing-evidence" => new AuthorityRequestAuthorization(request, AuthorityStatus.Permit),
            "wrong-request" => Evidence(request with { RelativePath = "forged.txt" }, AuthorityStatus.Permit),
            "throw" => throw new InvalidOperationException("test failure"),
            _ => Evidence(request, AuthorityStatus.Permit)
        });
        var result = await Create(document, invocation, authority).AuthorizeAsync(
            Request(document, invocation, LanguageAuthorizationPhase.Preflight));
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task ReleaseRevocationDeniesProtectedResultRelease()
    {
        var document = Compile("diff before.txt after.txt");
        var invocation = Invocation();
        var authority = new RecordingAuthorizer(request => Evidence(request,
            request.Action == AuthorityAction.Release ? AuthorityStatus.Deny : AuthorityStatus.Permit));
        var result = await Create(document, invocation, authority).AuthorizeAsync(
            Request(document, invocation, LanguageAuthorizationPhase.Release));
        Assert.Equal(LanguageAuthorityStatus.Deny, result.Status);
        Assert.Contains(authority.Requests, r => r.Action == AuthorityAction.Release);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAuthorityDispatch()
    {
        var document = Compile("diff before.txt after.txt");
        var invocation = Invocation();
        var authority = new RecordingAuthorizer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Create(document, invocation, authority)
            .AuthorizeAsync(Request(document, invocation, LanguageAuthorizationPhase.Preflight), cancellation.Token));
        Assert.Empty(authority.Requests);
    }

    [Fact]
    public async Task ForgedRequestBindingsAreRejectedBeforeAuthorityDispatch()
    {
        var document = Compile("diff before.txt after.txt");
        var invocation = Invocation();
        var authority = new RecordingAuthorizer();
        var authorizer = Create(document, invocation, authority);
        var node = document.Statements[0][0];
        var valid = Request(document, invocation, LanguageAuthorizationPhase.Preflight, node);
        var invalid = new[]
        {
            valid with { Invocation = invocation with { AttemptId = "other" } },
            valid with { DocumentIdentity = new string('a', 64) },
            valid with { NodeIdentity = new string('b', 64) },
            valid with { Descriptor = "files.merge-text" },
            valid with { DescriptorVersion = "forged" },
            valid with { Versions = new LanguageVersions() },
            valid with { Workspace = new WorkspaceId("other-workspace") },
            valid with { Stage = new DiffStage("elsewhere.txt", "after.txt") },
            valid with { Phase = (LanguageAuthorizationPhase)999 },
            valid with { ResourcePath = "outside.txt", Action = ResourceAction.ReadFile },
            valid with { Action = ResourceAction.ReadFile, ResourceRequestIdentity = new RequestIdentity("forged") },
        };

        foreach (var request in invalid)
            Assert.Equal(LanguageAuthorityStatus.Deny, (await authorizer.AuthorizeAsync(request)).Status);
        Assert.Empty(authority.Requests);
    }

    [Fact]
    public async Task AuthorityRequestDigestBindsPhasePathIdentityOptionsAndContext()
    {
        var firstDocument = Compile("diff before.txt after.txt");
        var options = TextChangeOptions.Default with { MaxWorkCells = TextChangeOptions.Default.MaxWorkCells - 1 };
        var changedOptions = LanguageCompiler.Compile(
            [[new DiffStage("before.txt", "after.txt", options)]], Workspace,
            new LanguageCompilerOptions(Versions: V2));
        Assert.True(changedOptions.Succeeded, string.Join(",", changedOptions.Diagnostics.Select(d => d.Code)));
        var invocation = Invocation();
        var first = CaptureOne(firstDocument, invocation, Context,
            Request(firstDocument, invocation, LanguageAuthorizationPhase.ResourceAccess, firstDocument.Statements[0][0],
                "before.txt", ResourceAction.ReadFile, "request-a"));
        var changedPhase = CaptureOne(firstDocument, invocation, Context,
            Request(firstDocument, invocation, LanguageAuthorizationPhase.Release, firstDocument.Statements[0][0],
                "before.txt", ResourceAction.ReadFile, "request-a"));
        var changedIdentity = CaptureOne(firstDocument, invocation, Context,
            Request(firstDocument, invocation, LanguageAuthorizationPhase.ResourceAccess, firstDocument.Statements[0][0],
                "before.txt", ResourceAction.ReadFile, "request-b"));
        var changedPath = CaptureOne(firstDocument, invocation, Context,
            Request(firstDocument, invocation, LanguageAuthorizationPhase.ResourceAccess, firstDocument.Statements[0][0],
                "after.txt", ResourceAction.ReadFile, "request-a"));
        var changedOptionsDigest = CaptureOne(changedOptions.Document!, invocation, Context,
            Request(changedOptions.Document!, invocation, LanguageAuthorizationPhase.ResourceAccess, changedOptions.Document!.Statements[0][0],
                "before.txt", ResourceAction.ReadFile, "request-a"));
        var swappedInputs = Compile("diff after.txt before.txt");
        var swappedInputsDigest = CaptureOne(swappedInputs, invocation, Context,
            Request(swappedInputs, invocation, LanguageAuthorizationPhase.ResourceAccess, swappedInputs.Statements[0][0],
                "before.txt", ResourceAction.ReadFile, "request-a"));
        var changedContext = CaptureOne(firstDocument, invocation,
            new("tenant-2", "subject", "run", "revision", "fence"),
            Request(firstDocument, invocation, LanguageAuthorizationPhase.ResourceAccess, firstDocument.Statements[0][0],
                "before.txt", ResourceAction.ReadFile, "request-a"));

        Assert.NotEqual(first, changedPhase);
        Assert.NotEqual(first, changedIdentity);
        Assert.NotEqual(first, changedPath);
        Assert.NotEqual(first, changedOptionsDigest);
        Assert.NotEqual(first, swappedInputsDigest);
        Assert.NotEqual(first, changedContext);
    }

    private static string CaptureOne(CompiledDocument document, EffectInvocation invocation,
        AuthenticatedAuthorityContext context, LanguageAuthorizationRequest request)
    {
        var authority = new RecordingAuthorizer();
        var result = Create(document, invocation, authority, context).AuthorizeAsync(request).AsTask().GetAwaiter().GetResult();
        Assert.Equal(LanguageAuthorityStatus.Permit, result.Status);
        return authority.Requests.First(r => r.Action == AuthorityAction.ReadFile).RequestIdentity;
    }

    private static HufuLanguageAuthorizer Create(CompiledDocument document, EffectInvocation invocation,
        RecordingAuthorizer authority, AuthenticatedAuthorityContext? context = null) =>
        new(context ?? Context, invocation, document, HufuLanguageAuthorityProfile.ReadAndDiffV2, authority);

    private static CompiledDocument Compile(string source, LanguageVersions? versions = null)
    {
        var compilation = LanguageCompiler.Compile(source, Workspace,
            new LanguageCompilerOptions(Versions: versions ?? V2));
        return compilation.Document ?? throw new Xunit.Sdk.XunitException("Expected compilation: " +
            string.Join(",", compilation.Diagnostics.Select(d => d.Code)));
    }

    private static LanguageAuthorizationRequest Request(CompiledDocument document, EffectInvocation invocation,
        LanguageAuthorizationPhase phase, CompiledNode? node = null, string? path = null,
        ResourceAction? action = null, string? identity = null)
    {
        node ??= document.Statements.SelectMany(s => s).First(n => n.Stage is DiffStage or ReadStage or FindStage or SearchStage);
        return new(invocation, document.Identity, node.Identity, node.Descriptor, LanguageProfile.DescriptorVersion,
            document.Versions, document.Workspace, node.Stage, phase, path, action,
            identity is null ? null : new RequestIdentity(identity));
    }

    private static EffectInvocation Invocation() => new("subject", "effect", "attempt");
    private static AuthorityRequestAuthorization Evidence(AuthorityRequest request, AuthorityStatus status) =>
        new(request, status, status == AuthorityStatus.Permit
            ? new AuthorityDecision(status, "test.permit", "snapshot-v1", "test-evaluator-v1", new string('a', 64))
            : new AuthorityDecision(status, "test.deny", "snapshot-v1", "test-evaluator-v1", new string('a', 64)), true);

    private sealed class RecordingAuthorizer(Func<AuthorityRequest, AuthorityRequestAuthorization>? respond = null) : IAuthorityRequestAuthorizer
    {
        public List<AuthorityRequest> Requests { get; } = [];

        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult((respond ?? (r => Evidence(r, AuthorityStatus.Permit)))(request));
        }
    }
}
