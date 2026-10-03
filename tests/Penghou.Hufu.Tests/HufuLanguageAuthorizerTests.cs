using System.Text;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Luban;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class HufuLanguageAuthorizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");

    [Fact]
    public void UnsupportedTextChangeV2IsRejectedBeforeAuthorityAccess()
    {
        using var workspace = new TestWorkspace();
        var compilation = LanguageCompiler.Compile("#!luban2\ndiff before.txt after.txt", workspace.Id,
            new LanguageCompilerOptions(Versions: new LanguageVersions("2", "2", "windows-text-change-v2", "local-windows-read-v1")));
        Assert.True(compilation.Succeeded, string.Join(",", compilation.Diagnostics.Select(d => d.Code)));

        var invocation = Invocation();
        var source = new SequencedSource(_ => throw new Xunit.Sdk.XunitException("Unsupported documents must not read authority."));
        var evaluator = new PermitEvaluator();
        var recorder = new RecordingRecorder();

        Assert.Throws<ArgumentException>(() => new HufuLanguageAuthorizer(Context, invocation,
            compilation.Document!, source, evaluator, recorder, new FixedTimeProvider(Now)));

        Assert.Equal(0, source.Calls);
        Assert.Empty(source.RequestedContexts);
        Assert.Empty(evaluator.Requests);
        Assert.Empty(recorder.Decisions);
    }

    [Fact]
    public void QualifiedV2DocumentIsRejectedBeforeAuthorityLookupOrEvidenceRecording()
    {
        using var workspace = new TestWorkspace();
        var versions = new LanguageVersions("2", "2", "windows-text-change-v2", "local-windows-read-v1");
        var compilation = LanguageCompiler.Compile("read note.txt", workspace.Id,
            new LanguageCompilerOptions(Versions: versions));
        Assert.True(compilation.Succeeded, string.Join(",", compilation.Diagnostics.Select(d => d.Code)));
        var source = new SequencedSource(_ => Snapshot("v2", [Grant(AllActions, Scope(workspace, ""))]));
        var recorder = new RecordingRecorder();
        var invocation = Invocation();

        Assert.Throws<ArgumentException>(() => new HufuLanguageAuthorizer(Context, invocation,
            compilation.Document!, source, new CedarAuthorityEvaluator(), recorder, new FixedTimeProvider(Now)));

        Assert.Empty(source.RequestedContexts);
        Assert.Empty(recorder.Decisions);
    }
    [Fact]

    public async Task LocalReadRunsThroughRealCedarSnapshotAndReturnsBoundedContent()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("public.txt", Encoding.UTF8.GetBytes("hello from Luban"));
        var (document, invocation, runtime, source, recorder) = Create(workspace, "read public.txt",
            [Grant([AuthorityAction.ReadFile, AuthorityAction.ReadMetadata, AuthorityAction.Release], Scope(workspace, ""))],
            new CedarAuthorityEvaluator());

        var result = await runtime.ExecuteAsync(invocation, document);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var content = Assert.IsType<FileContentValue>(Assert.Single(Assert.Single(result.Statements!).Values));
        Assert.Equal("hello from Luban", content.Content);
        Assert.Equal("public.txt", content.RelativePath);
        Assert.NotEmpty(source.RequestedContexts);
        Assert.Contains(recorder.Decisions, pair => pair.Decision.Status == AuthorityStatus.Permit);
        Assert.All(recorder.Decisions, pair => Assert.Equal(source.Snapshot.Identity, pair.Decision.SnapshotIdentity));
    }

    [Fact]
    public async Task DeniedReadAndSearchDoNotExposeProtectedContent()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("public/visible.txt", Encoding.UTF8.GetBytes("needle visible"));
        workspace.WriteBytes("private/secret.txt", Encoding.UTF8.GetBytes("needle hidden"));
        var grants = new[] { Grant(AllActions, Scope(workspace, ""), [Scope(workspace, "private", AuthorityScopeKind.Subtree)]) };

        var (readDocument, readInvocation, readRuntime, _, _) = Create(workspace, "read private/secret.txt", grants,
            new CedarAuthorityEvaluator());
        var deniedRead = await readRuntime.ExecuteAsync(readInvocation, readDocument);
        Assert.Equal(LanguageRunStatus.AuthorityDenied, deniedRead.Status);
        Assert.Null(deniedRead.Statements);

        var (searchDocument, searchInvocation, searchRuntime, _, _) = Create(workspace,
            "search needle . --include **/*.txt", grants, new CedarAuthorityEvaluator());
        var search = await searchRuntime.ExecuteAsync(searchInvocation, searchDocument);

        Assert.Equal(LanguageRunStatus.Succeeded, search.Status);
        var match = Assert.Single(Assert.Single(search.Statements!).Values);
        var visible = Assert.IsType<SearchMatchValue>(match);
        Assert.Equal("public/visible.txt", visible.RelativePath);
        Assert.Equal("needle visible", visible.Line);
        Assert.DoesNotContain(search.Statements!.SelectMany(s => s.Values).OfType<SearchMatchValue>(),
            value => value.RelativePath.Contains("private", StringComparison.OrdinalIgnoreCase) || value.Line.Contains("hidden", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WindowsCaseInsensitiveUnicodeAliasCannotReleaseExcludedFileContent()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("src/É.txt", Encoding.UTF8.GetBytes("excluded unicode secret"));
        var grants = new[]
        {
            Grant(AllActions, Scope(workspace, ""), [Scope(workspace, "src/É.txt", AuthorityScopeKind.Exact)])
        };
        var (document, invocation, runtime, _, _) = Create(workspace, "read src/é.txt", grants, new CedarAuthorityEvaluator());

        var result = await runtime.ExecuteAsync(invocation, document);

        Assert.NotEqual(LanguageRunStatus.Succeeded, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task FindDoesNotDiscloseExcludedNestedPaths()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("public/visible.txt", Encoding.UTF8.GetBytes("visible"));
        workspace.WriteBytes("private/secret.txt", Encoding.UTF8.GetBytes("secret"));
        var grants = new[] { Grant(AllActions, Scope(workspace, ""), [Scope(workspace, "private", AuthorityScopeKind.Subtree)]) };
        var (document, invocation, runtime, _, _) = Create(workspace, "find . **/*.txt", grants,
            new CedarAuthorityEvaluator());

        var result = await runtime.ExecuteAsync(invocation, document);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var paths = Assert.Single(result.Statements!).Values.OfType<FileReferenceValue>().Select(value => value.RelativePath).ToArray();
        Assert.Contains("public/visible.txt", paths);
        Assert.DoesNotContain(paths, path => path.Contains("private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("private/secret.txt", string.Join('\n', paths), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("find . **/*.txt | take 1 | count")]
    [InlineData("search needle . --include **/*.txt | take 1 | count")]
    public async Task PureFiltersConsumeOnlyTheAuthorizedView(string script)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("public/a.txt", Encoding.UTF8.GetBytes("needle one"));
        workspace.WriteBytes("public/b.txt", Encoding.UTF8.GetBytes("needle two"));
        workspace.WriteBytes("private/secret.txt", Encoding.UTF8.GetBytes("needle secret"));
        var (document, invocation, runtime, _, _) = Create(workspace, script,
            [Grant(AllActions, Scope(workspace, ""), [Scope(workspace, "private")])], new CedarAuthorityEvaluator());
        var result = await runtime.ExecuteAsync(invocation, document);
        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        Assert.Equal(1, Assert.IsType<CountValue>(Assert.Single(Assert.Single(result.Statements!).Values)).Count);
    }

    [Fact]
    public async Task CurrentSnapshotRevocationBeforeResourceReadBlocksContent()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("note.txt", Encoding.UTF8.GetBytes("must not be disclosed"));
        var permit = Snapshot("permit-v1", [Grant(AllActions, Scope(workspace, ""))]);
        var revoked = Snapshot("revoked-v2", [Grant([AuthorityAction.ReadMetadata, AuthorityAction.Release], Scope(workspace, ""))]);
        // Current state is fetched once per callback: preflight and effect
        // start precede the first concrete content authorization.
        var source = new SequencedSource(index => index <= 2 ? permit : revoked);
        var recorder = new RecordingRecorder();
        var compilation = Compile(workspace, "read note.txt");
        var invocation = Invocation();
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), new LocalWorkspaceProvider(workspace.Id, workspace.Root),
            new HufuLanguageAuthorizer(Context, invocation, compilation, source, new CedarAuthorityEvaluator(), recorder, new FixedTimeProvider(Now)));

        var result = await runtime.ExecuteAsync(invocation, compilation);

        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Statements);
        Assert.True(source.Calls >= 3);
        Assert.Contains(recorder.Decisions, item => item.Decision.SnapshotVersion == "revoked-v2" && item.Decision.Status == AuthorityStatus.Deny);
    }

    [Fact]
    public async Task ReleaseRevocationAfterReadPreventsAnyPublicStatementResult()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("note.txt", Encoding.UTF8.GetBytes("release controlled"));
        var permit = Snapshot("permit-v1", [Grant(AllActions, Scope(workspace, ""))]);
        var revoked = Snapshot("release-revoked", [Grant(AllActions.Where(action => action != AuthorityAction.Release).ToArray(), Scope(workspace, ""))]);
        // Preflight, effect start, content authorization, and root metadata
        // precede the first release callback for this one-component path.
        var source = new SequencedSource(index => index <= 4 ? permit : revoked);
        var recorder = new RecordingRecorder();
        var document = Compile(workspace, "read note.txt");
        var invocation = Invocation();
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), new LocalWorkspaceProvider(workspace.Id, workspace.Root),
            new HufuLanguageAuthorizer(Context, invocation, document, source, new CedarAuthorityEvaluator(), recorder, new FixedTimeProvider(Now)));

        var result = await runtime.ExecuteAsync(invocation, document);

        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Statements);
        Assert.Contains(recorder.Decisions, item => item.Decision.SnapshotVersion == "release-revoked" &&
            item.Request.Action == AuthorityAction.Release && item.Decision.Status == AuthorityStatus.Deny);
    }

    [Fact]
    public async Task NullOrThrowingAuthorityDependenciesFailClosedWithoutStatements()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("note.txt", Encoding.UTF8.GetBytes("private"));
        var document = Compile(workspace, "read note.txt");
        var invocation = Invocation();
        var snapshot = Snapshot("current", [Grant(AllActions, Scope(workspace, ""))]);

        var nullRuntime = Runtime(workspace, invocation, document, new SequencedSource(_ => null), new PermitEvaluator(), new RecordingRecorder());
        var nullResult = await nullRuntime.ExecuteAsync(invocation, document);
        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable, nullResult.Status);
        Assert.Null(nullResult.Statements);

        var sourceFailureRuntime = Runtime(workspace, invocation, document, new SequencedSource(_ => throw new IOException()),
            new PermitEvaluator(), new RecordingRecorder());
        var sourceFailure = await sourceFailureRuntime.ExecuteAsync(invocation, document);
        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable, sourceFailure.Status);
        Assert.Null(sourceFailure.Statements);

        var evaluatorFailureRuntime = Runtime(workspace, invocation, document, new SequencedSource(_ => snapshot),
            new ThrowingEvaluator(), new RecordingRecorder());
        var evaluatorFailure = await evaluatorFailureRuntime.ExecuteAsync(invocation, document);
        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable, evaluatorFailure.Status);
        Assert.Null(evaluatorFailure.Statements);

        var recorderFailureRuntime = Runtime(workspace, invocation, document, new SequencedSource(_ => snapshot),
            new PermitEvaluator(), new RecordingRecorder { Accept = false });
        var recorderFailure = await recorderFailureRuntime.ExecuteAsync(invocation, document);
        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable, recorderFailure.Status);
        Assert.Null(recorderFailure.Statements);
    }

    [Fact]
    public async Task ExpiredSnapshotIsRecordedAsDenyEvenIfEvaluatorWouldPermit()
    {
        using var workspace = new TestWorkspace();
        var expired = new AuthoritySnapshot(Context, "expired", [Layer("layer", [Grant(AllActions, Scope(workspace, ""))])],
            [], Now);
        var source = new SequencedSource(_ => expired);
        var evaluator = new PermitEvaluator();
        var recorder = new RecordingRecorder();
        var document = Compile(workspace, "read missing.txt");
        var invocation = Invocation();
        var authorizer = new HufuLanguageAuthorizer(Context, invocation, document, source, evaluator, recorder, new FixedTimeProvider(Now));
        var request = PreflightRequest(document, invocation, document.Statements[0][0]);

        var decision = await authorizer.AuthorizeAsync(request);

        Assert.Equal(LanguageAuthorityStatus.Deny, decision.Status);
        Assert.Empty(evaluator.Requests);
        var evidence = Assert.Single(recorder.Decisions);
        Assert.Equal(AuthorityStatus.Deny, evidence.Decision.Status);
        Assert.Equal("authority.snapshot-expired", evidence.Decision.ReasonCode);
        Assert.Equal("hufu-read-v1", evidence.Decision.EvaluatorIdentity);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("version")]
    [InlineData("evaluator")]
    [InlineData("status")]
    public async Task InconsistentEvaluatorPermitCannotReachRequiredEvidenceOrDispatch(string fault)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("note.txt", Encoding.UTF8.GetBytes("do not disclose"));
        var snapshot = Snapshot("current", [Grant(AllActions, Scope(workspace, ""))]);
        var source = new SequencedSource(_ => snapshot);
        var recorder = new RecordingRecorder();
        var document = Compile(workspace, "read note.txt");
        var invocation = Invocation();
        var runtime = Runtime(workspace, invocation, document, source, new InconsistentEvaluator(fault), recorder);
        var result = await runtime.ExecuteAsync(invocation, document);
        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable, result.Status);
        Assert.Null(result.Statements);
        Assert.Empty(recorder.Decisions);
    }

    [Fact]
    public async Task ForgedInvocationDocumentNodePhaseAndResourceBindingAreRejected()
    {
        using var workspace = new TestWorkspace();
        var snapshot = Snapshot("current", [Grant(AllActions, Scope(workspace, ""))]);
        var source = new SequencedSource(_ => snapshot);
        var evaluator = new PermitEvaluator();
        var recorder = new RecordingRecorder();
        var document = Compile(workspace, "read note.txt");
        var invocation = Invocation();
        var authorizer = new HufuLanguageAuthorizer(Context, invocation, document, source, evaluator, recorder, new FixedTimeProvider(Now));
        var node = document.Statements[0][0];
        var valid = PreflightRequest(document, invocation, node);

        var wrongInvocation = await authorizer.AuthorizeAsync(valid with { Invocation = invocation with { AttemptId = "different" } });
        var wrongDocument = await authorizer.AuthorizeAsync(valid with { DocumentIdentity = new string('a', 64) });
        var wrongNode = await authorizer.AuthorizeAsync(valid with { NodeIdentity = new string('b', 64) });
        var wrongDescriptor = await authorizer.AuthorizeAsync(valid with { DescriptorVersion = "2" });
        var wrongWorkspace = await authorizer.AuthorizeAsync(valid with { Workspace = new("other") });
        var wrongProfile = await authorizer.AuthorizeAsync(valid with { Versions = new(Provider: "unknown") });
        var wrongStage = await authorizer.AuthorizeAsync(valid with { Stage = new ReadStage("elsewhere.txt") });
        var wrongSemanticRoot = await authorizer.AuthorizeAsync(valid with { ResourcePath = "elsewhere.txt" });
        var nullNode = await authorizer.AuthorizeAsync(valid with { NodeIdentity = null! });
        var malformedPhase = await authorizer.AuthorizeAsync(valid with { Phase = (LanguageAuthorizationPhase)999 });
        var forgedPhaseResource = await authorizer.AuthorizeAsync(valid with
        {
            ResourcePath = "private/secret.txt", Action = ResourceAction.ReadFile,
            ResourceRequestIdentity = new RequestIdentity("forged")
        });
        var forgedResource = await authorizer.AuthorizeAsync(valid with
        {
            Phase = LanguageAuthorizationPhase.ResourceAccess,
            ResourcePath = "outside/secret.txt", Action = ResourceAction.ReadFile,
            ResourceRequestIdentity = new RequestIdentity("forged")
        });

        Assert.All(new[] { wrongInvocation, wrongDocument, wrongNode, wrongDescriptor, malformedPhase, forgedPhaseResource, forgedResource,
            wrongWorkspace, wrongProfile, wrongStage, wrongSemanticRoot, nullNode },
            result => Assert.Equal(LanguageAuthorityStatus.Deny, result.Status));
        Assert.Empty(source.RequestedContexts);
        Assert.Empty(evaluator.Requests);
        Assert.Empty(recorder.Decisions);
    }

    [Theory]
    [InlineData("find . *.txt | read")]
    [InlineData("find . *.txt | search needle")]
    public async Task DynamicPipelinedReadIsNotDowngradedToWorkspaceRootAccess(string script)
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("public.txt", Encoding.UTF8.GetBytes("do not fallback"));
        var compilation = LanguageCompiler.Compile(script, workspace.Id);
        Assert.True(compilation.Succeeded, string.Join(",", compilation.Diagnostics.Select(d => d.Code)));
        var invocation = Invocation();
        var source = new SequencedSource(_ => Snapshot("current", [Grant(AllActions, Scope(workspace, ""))]));
        var recorder = new RecordingRecorder();
        var runtime = Runtime(workspace, invocation, compilation.Document!, source, new PermitEvaluator(), recorder);

        var result = await runtime.ExecuteAsync(invocation, compilation.Document!);

        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable, result.Status);
        Assert.Null(result.Statements);
        Assert.NotEmpty(recorder.Decisions);
        Assert.All(recorder.Decisions, entry => Assert.Contains(entry.Request.Action,
            new[] { AuthorityAction.ListDirectory, AuthorityAction.ReadMetadata, AuthorityAction.Release }));
        Assert.DoesNotContain(recorder.Decisions, entry => entry.Request.Action == AuthorityAction.ReadFile);
        // The unknown dynamic read/search never gains a workspace-root read or effect result.
    }

    private static (CompiledDocument Document, EffectInvocation Invocation, LanguageRuntime Runtime,
        SequencedSource Source, RecordingRecorder Recorder) Create(TestWorkspace workspace, string source,
        IReadOnlyList<AuthorityGrant> grants, IAuthorityEvaluator evaluator)
    {
        var document = Compile(workspace, source);
        var invocation = Invocation();
        var snapshot = Snapshot("snapshot-v1", grants);
        var authoritySource = new SequencedSource(_ => snapshot);
        var recorder = new RecordingRecorder();
        return (document, invocation, Runtime(workspace, invocation, document, authoritySource, evaluator, recorder), authoritySource, recorder);
    }

    private static CompiledDocument Compile(TestWorkspace workspace, string source)
    {
        var compilation = LanguageCompiler.Compile(source, workspace.Id);
        return compilation.Document ?? throw new Xunit.Sdk.XunitException("Expected language source to compile: " +
            string.Join(",", compilation.Diagnostics.Select(d => d.Code)));
    }

    private static LanguageRuntime Runtime(TestWorkspace workspace, EffectInvocation invocation, CompiledDocument document,
        SequencedSource source, IAuthorityEvaluator evaluator, RecordingRecorder recorder) =>
        new(new WorkspaceReference(workspace.Id.Value), new LocalWorkspaceProvider(workspace.Id, workspace.Root),
            new HufuLanguageAuthorizer(Context, invocation, document, source, evaluator, recorder, new FixedTimeProvider(Now)));

    private static LanguageAuthorizationRequest PreflightRequest(CompiledDocument document, EffectInvocation invocation, CompiledNode node) =>
        new(invocation, document.Identity, node.Identity, node.Descriptor, LanguageProfile.DescriptorVersion,
            document.Versions, document.Workspace, node.Stage, LanguageAuthorizationPhase.Preflight);

    private static EffectInvocation Invocation() => new("subject", "effect", Guid.NewGuid().ToString("N"));
    private static readonly AuthorityAction[] AllActions = Enum.GetValues<AuthorityAction>();
    private static AuthorityScope Scope(TestWorkspace workspace, string path, AuthorityScopeKind kind = AuthorityScopeKind.Subtree) =>
        new(workspace.Id.Value, AuthorityValidation.NormalizePath(path), kind);
    private static AuthorityGrant Grant(IReadOnlyList<AuthorityAction> actions, AuthorityScope scope,
        IReadOnlyList<AuthorityScope>? exclusions = null) =>
        new("grant-" + scope.RelativePath + "-" + actions[0], actions, scope, exclusions ?? [], Now.AddDays(-1), Now.AddDays(1));
    private static AuthoritySnapshot Snapshot(string version, IReadOnlyList<AuthorityGrant> grants) =>
        new(Context, version, [Layer("run", grants)], [], Now.AddDays(1));
    private static AuthorityLayer Layer(string id, IReadOnlyList<AuthorityGrant> grants) => new(id, grants);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequencedSource(Func<int, AuthoritySnapshot?> get) : IAuthoritySnapshotSource
    {
        public AuthoritySnapshot Snapshot => get(Calls) ?? throw new InvalidOperationException("No current test snapshot.");
        public int Calls { get; private set; }
        public List<AuthenticatedAuthorityContext> RequestedContexts { get; } = [];
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default)
        {
            RequestedContexts.Add(context);
            return ValueTask.FromResult(get(++Calls));
        }
    }

    private sealed class PermitEvaluator : IAuthorityEvaluator
    {
        public List<AuthorityRequest> Requests { get; } = [];
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
        {
            Requests.Add(request);
            return new(AuthorityStatus.Permit, "test.permit", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
        }
    }

    private sealed class ThrowingEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now) =>
            throw new InvalidOperationException("test failure");
    }

    private sealed class InconsistentEvaluator(string fault) : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
        {
            var decision = new AuthorityDecision(AuthorityStatus.Permit, "test.permit", snapshot.Version,
                "test-evaluator-v1", snapshot.Identity);
            return fault switch
            {
                "snapshot" => decision with { SnapshotIdentity = new string('0', 64) },
                "version" => decision with { SnapshotVersion = "different" },
                "evaluator" => decision with { EvaluatorIdentity = "" },
                _ => decision with { Status = (AuthorityStatus)999 }
            };
        }
    }

    private sealed class RecordingRecorder : IAuthorityDecisionRecorder
    {
        public List<(AuthorityRequest Request, AuthorityDecision Decision)> Decisions { get; } = [];
        public bool Accept { get; init; } = true;
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            Decisions.Add((request, decision));
            return ValueTask.FromResult(Accept);
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hufu-language-auth-" + Guid.NewGuid().ToString("N"));
        internal WorkspaceId Id { get; } = new("hufu-test-workspace");
        internal string Root => _root;
        internal TestWorkspace() => Directory.CreateDirectory(_root);
        internal void WriteBytes(string relative, byte[] bytes)
        {
            var fullRoot = Path.GetFullPath(_root);
            var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test path escaped its temporary workspace.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}

