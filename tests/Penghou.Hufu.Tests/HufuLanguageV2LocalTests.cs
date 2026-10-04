using System.Text;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Luban;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Hufu.Tests;

// Real published Local/Luban enforcement is a Windows profile. Portable mapping
// cases live separately in HufuLanguageV2AuthorizerTests and run on all CI OSes.
public sealed class HufuLanguageV2LocalTests
{
    [Theory]
    [InlineData("read src/before.txt", "old\n")]
    [InlineData("read src/before.txt --start-line 2 --line-count 1", "two\n")]
    public async Task ExplicitV2ReadsReturnOnlyAuthorizedWholeInputOrLineWindow(string script, string expected)
    {
        using var host = new Host(script);
        host.Write("src/before.txt", script.Contains("--start-line", StringComparison.Ordinal) ? "one\ntwo\nthree\n" : "old\n");
        var result = await host.Run();
        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var value = Assert.Single(Assert.Single(result.Statements!).Values);
        if (value is FileWindowValue window)
        {
            Assert.Equal(expected, window.Content);
            Assert.True(window.CompleteInput);
            Assert.True(window.CompleteWindow);
            Assert.Equal(2, window.StartLine);
        }
        else Assert.Equal(expected, Assert.IsType<FileContentValue>(value).Content);
        Assert.Equal(1, host.Provider.SuccessfulReads);
        Assert.Equal(0, host.Provider.WriterOpens);
        Assert.NotEmpty(host.Recorder.Decisions);
    }

    [Theory]
    [InlineData("diff src/before.txt proposals/after.txt")]
    [InlineData("diff src/before.txt proposals/after.txt | count")]
    [InlineData("diff src/before.txt proposals/after.txt | take 0 | count")]
    public async Task RealDiffAndDiscardedResultsReadBothInputsAndRequireTheirRelease(string script)
    {
        using var host = new Host(script);
        host.Write("src/before.txt", "old\n"); host.Write("proposals/after.txt", "new\n");
        var result = await host.Run();
        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var value = Assert.Single(Assert.Single(result.Statements!).Values);
        if (value is DiffValue diff)
        {
            Assert.True(diff.IsUntrustedCandidate);
            Assert.NotEmpty(diff.Edits);
            Assert.NotEqual(diff.BeforeSha256, diff.AfterSha256);
        }
        else Assert.Equal(script.Contains("take 0", StringComparison.Ordinal) ? 0 : 1, Assert.IsType<CountValue>(value).Count);
        Assert.Equal(2, host.Provider.SuccessfulReads);
        Assert.Equal(0, host.Provider.WriterOpens);
        Assert.Equal("old\n", host.Read("src/before.txt"));
        Assert.Equal("new\n", host.Read("proposals/after.txt"));
        foreach (var path in new[] { "src/before.txt", "proposals/after.txt" })
        {
            Assert.Contains(host.Trace.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess &&
                r.ResourcePath == path && r.Action == ResourceAction.ReadFile && r.ResourceRequestIdentity is not null);
            Assert.Contains(host.Trace.Requests, r => r.Phase == LanguageAuthorizationPhase.Release &&
                r.ResourcePath == path && r.Action == ResourceAction.ReadFile && r.ResourceRequestIdentity is not null);
            Assert.Contains(host.Recorder.Decisions, d => d.Request.Action == AuthorityAction.Release && d.Request.RelativePath == path);
        }
        Assert.DoesNotContain(host.Recorder.Decisions, d => d.Request.Action is AuthorityAction.PatchFile or AuthorityAction.WriteFile);
    }

    [Theory]
    [InlineData("src/before.txt")]
    [InlineData("proposals/after.txt")]
    public async Task EitherDeniedInputBlocksTheWholeDocumentBeforeAnyProviderSession(string denied)
    {
        using var host = new Host("diff src/before.txt proposals/after.txt");
        host.Write("src/before.txt", "old secret"); host.Write("proposals/after.txt", "new secret");
        host.Source.Snapshot = host.Snapshot(exclusion: denied);
        var result = await host.Run();
        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Statements);
        Assert.Equal(0, host.Provider.ReaderOpens);
        Assert.Contains(host.Recorder.Decisions, d => d.Decision.Status == AuthorityStatus.Deny && d.Request.RelativePath == denied);
    }

    [Theory]
    [InlineData(AuthorityAction.ReadFile)]
    [InlineData(AuthorityAction.ReadMetadata)]
    [InlineData(AuthorityAction.Release)]
    public async Task EveryReadMetadataAndDisclosurePermissionIsMandatoryBeforeI_o(AuthorityAction missing)
    {
        using var host = new Host("diff src/before.txt proposals/after.txt");
        host.Source.Snapshot = host.Snapshot(actions: Host.ReadActions.Where(a => a != missing).ToArray());
        var result = await host.Run();
        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Statements);
        Assert.Equal(0, host.Provider.ReaderOpens);
    }

    [Fact]
    public async Task RepeatedDiffInputSharesOneContentObservationWithoutGrantingMutation()
    {
        using var host = new Host("diff src/before.txt src/before.txt");
        host.Write("src/before.txt", "same\n");
        var result = await host.Run();
        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var diff = Assert.IsType<DiffValue>(Assert.Single(Assert.Single(result.Statements!).Values));
        Assert.Empty(diff.Edits); Assert.Equal(diff.BeforeSha256, diff.AfterSha256);
        Assert.Equal(1, host.Provider.SuccessfulReads);
        Assert.Equal(0, host.Provider.WriterOpens);
    }

    [Theory]
    [InlineData("target-admission")]
    [InlineData("access")]
    [InlineData("result-release")]
    [InlineData("resource-release")]
    public async Task CurrentRevocationAtEachLiveBoundaryBlocksAllPublicResults(string boundary)
    {
        using var host = new Host("diff src/before.txt proposals/after.txt | take 0 | count");
        host.Write("src/before.txt", "old secret"); host.Write("proposals/after.txt", "new secret");
        host.Trace.BeforeAuthorize = r =>
        {
            var reached = boundary switch
            {
                "target-admission" => r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile &&
                    r.ResourceRequestIdentity is null,
                "access" => r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile &&
                    r.ResourceRequestIdentity is not null,
                "result-release" => r.Phase == LanguageAuthorizationPhase.Release && r.ResourceRequestIdentity is null,
                _ => r.Phase == LanguageAuthorizationPhase.Release && r.ResourceRequestIdentity is not null
            };
            if (reached) host.Source.Snapshot = host.Snapshot("revoked", actions: [AuthorityAction.ReadMetadata]);
        };
        var result = await host.Run();
        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status); Assert.Null(result.Statements);
        Assert.Contains(host.Recorder.Decisions, d => d.Decision.SnapshotVersion == "revoked" && d.Decision.Status == AuthorityStatus.Deny);
        Assert.Equal(boundary is "target-admission" or "access" ? 0 : 2, host.Provider.SuccessfulReads);
        if (boundary == "target-admission") Assert.Equal(0, host.Provider.ReaderOpens);
        Assert.Equal(0, host.Provider.WriterOpens);
    }

    [Theory]
    [InlineData("preflight")]
    [InlineData("access")]
    [InlineData("release")]
    public async Task MissingRequiredEvidenceFailsClosedAtEachBoundary(string boundary)
    {
        using var host = new Host("diff src/before.txt proposals/after.txt");
        host.Write("src/before.txt", "old secret"); host.Write("proposals/after.txt", "new secret");
        host.Recorder.Accept = () => boundary switch
        {
            "preflight" => host.Trace.Current?.Phase != LanguageAuthorizationPhase.Preflight,
            "access" => !(host.Trace.Current?.Phase == LanguageAuthorizationPhase.ResourceAccess && host.Trace.Current.ResourceRequestIdentity is not null),
            _ => host.Trace.Current?.Phase != LanguageAuthorizationPhase.Release
        };
        var result = await host.Run();
        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable, result.Status); Assert.Null(result.Statements);
        Assert.Equal(boundary == "release" ? 2 : 0, host.Provider.SuccessfulReads);
        Assert.Equal(0, host.Provider.WriterOpens);
    }

    private sealed class Host : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        internal static readonly AuthorityAction[] ReadActions = [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata, AuthorityAction.Release];
        private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hufu-v2-local-" + Guid.NewGuid().ToString("N"));
        private readonly WorkspaceId _workspace = new("v2-workspace");
        private readonly EffectInvocation _invocation = new("subject", "effect", "attempt");
        private readonly CompiledDocument _document;
        private readonly LanguageRuntime _runtime;
        internal MutableSource Source { get; }
        internal Recorder Recorder { get; } = new();
        internal TracingAuthorizer Trace { get; }
        internal CountingProvider Provider { get; }
        internal Host(string script)
        {
            Directory.CreateDirectory(_root);
            var compiled = LanguageCompiler.Compile(script, _workspace,
                new LanguageCompilerOptions(Versions: HufuLanguageAuthorityProfile.ReadAndDiffV2.Versions));
            Assert.True(compiled.Succeeded, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
            _document = compiled.Document!;
            Source = new(Snapshot());
            var current = new CurrentAuthorityRequestAuthorizer(Source, new CedarAuthorityEvaluator(), Recorder, new Clock());
            Trace = new(new HufuLanguageAuthorizer(Context, _invocation, _document, HufuLanguageAuthorityProfile.ReadAndDiffV2, current));
            Provider = new(new LocalWorkspaceProvider(_workspace, _root));
            _runtime = new(new WorkspaceReference(_workspace.Value), Provider, Trace);
        }
        internal AuthoritySnapshot Snapshot(string version = "current", string? exclusion = null, AuthorityAction[]? actions = null) =>
            new(Context, version, [new("layer", [new("grant", actions ?? ReadActions, new(_workspace.Value, "", AuthorityScopeKind.Subtree),
                exclusion is null ? [] : [new(_workspace.Value, exclusion, AuthorityScopeKind.Exact)], Now.AddHours(-1), Now.AddHours(1))])], [], Now.AddHours(1));
        internal Task<LanguageRunResult> Run() => _runtime.ExecuteAsync(_invocation, _document).AsTask();
        internal void Write(string path, string text)
        {
            var physical = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
            File.WriteAllBytes(physical, Encoding.UTF8.GetBytes(text));
        }
        internal string Read(string path) => File.ReadAllText(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        public void Dispose() => Directory.Delete(_root, recursive: true);
        private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    }

    private sealed class MutableSource(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        internal AuthoritySnapshot Snapshot { get; set; } = snapshot;
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult<AuthoritySnapshot?>(Snapshot);
    }
    private sealed class Recorder : IAuthorityDecisionRecorder
    {
        internal List<(AuthorityRequest Request, AuthorityDecision Decision)> Decisions { get; } = [];
        internal Func<bool> Accept { get; set; } = () => true;
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        { Decisions.Add((request, decision)); return ValueTask.FromResult(Accept()); }
    }
    private sealed class TracingAuthorizer(ILanguageAuthorizer inner) : ILanguageAuthorizer
    {
        internal List<LanguageAuthorizationRequest> Requests { get; } = [];
        internal LanguageAuthorizationRequest? Current { get; private set; }
        internal Action<LanguageAuthorizationRequest>? BeforeAuthorize { get; set; }
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request, CancellationToken cancellationToken = default)
        { Current = request; Requests.Add(request); BeforeAuthorize?.Invoke(request); return inner.AuthorizeAsync(request, cancellationToken); }
    }
    private sealed class CountingProvider(IWorkspaceProvider inner) : IWorkspaceProvider
    {
        internal int ReaderOpens { get; private set; }
        internal int WriterOpens { get; private set; }
        internal int SuccessfulReads { get; private set; }
        public WorkspaceId Workspace => inner.Workspace;
        public WorkspaceProviderCapabilities Capabilities => inner.Capabilities;
        public IWorkspaceReaderSession OpenReader(IResourceAuthorizer authorizer, WorkspaceReaderOptions? options = null)
        { ReaderOpens++; return new Reader(inner.OpenReader(authorizer, options), this); }
        public IWorkspaceConditionalWriter OpenWriter(IResourceAuthorizer authorizer, IResourceMutationJournal journal, WorkspaceWriterOptions? options = null)
        { WriterOpens++; throw new Xunit.Sdk.XunitException("Read/diff authorization must never open a writer."); }
        private sealed class Reader(IWorkspaceReaderSession inner, CountingProvider owner) : IWorkspaceReaderSession
        {
            public async ValueTask<ResourceResult<FileReadResult>> ReadFileAsync(FileReadRequest request, CancellationToken cancellationToken = default)
            { var result = await inner.ReadFileAsync(request, cancellationToken); if (result.Succeeded) owner.SuccessfulReads++; return result; }
            public ValueTask<ResourceResult<FileMetadata>> GetFileMetadataAsync(FileMetadataRequest request, CancellationToken cancellationToken = default) => inner.GetFileMetadataAsync(request, cancellationToken);
            public ValueTask<ResourceResult<DirectoryPage>> ListDirectoryAsync(DirectoryListRequest request, CancellationToken cancellationToken = default) => inner.ListDirectoryAsync(request, cancellationToken);
            public void Dispose() => inner.Dispose();
        }
    }
}
