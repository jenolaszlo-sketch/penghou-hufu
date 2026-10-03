using Penghou.Hufu;
using Penghou.Hufu.IO;
using Penghou.IO.Abstractions;
using Xunit;

namespace Penghou.Hufu.IO.Tests;

public sealed class HufuWorkspaceAccessTests
{
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");
    private static readonly WorkspaceId Workspace = new("workspace");

    [Fact]
    public async Task AllowedReadUsesProviderAndRecordsCurrentDecision()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        var request = fixture.ReadRequest("src/a.txt");

        var result = await fixture.Access.ReadFileAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(1, fixture.Provider.OpenReaderCalls);
        Assert.Equal(1, fixture.Provider.ReaderCalls);
        Assert.Single(fixture.Recorder.Requests);
        Assert.Equal(AuthorityAction.ReadFile, Assert.Single(fixture.Recorder.Requests).Action);
    }

    [Fact]
    public async Task InitialDenyDoesNotOpenOrCallProvider()
    {
        var fixture = new Fixture(AuthorityStatus.Deny);

        var result = await fixture.Access.ReadFileAsync(fixture.ReadRequest("private/a.txt"));

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Equal(0, fixture.Provider.OpenReaderCalls);
        Assert.Equal(0, fixture.Provider.ReaderCalls);
    }

    [Fact]
    public async Task DisposedAccessRejectsReadAndWriteBeforeAuthorizationOrProvider()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        var read = fixture.ReadRequest("src/a.txt");
        var write = fixture.WriteRequest("src/a.txt");
        fixture.Access.Dispose();

        var readResult = await fixture.Access.ReadFileAsync(read);
        var writeResult = await fixture.Access.WriteFileAsync(write);

        Assert.Equal(ResourceFailureKind.ProviderFailure, readResult.Failure);
        Assert.Equal(ResourceFailureKind.ProviderFailure, writeResult.Failure);
        Assert.Empty(fixture.Recorder.Requests);
        Assert.Equal(0, fixture.Provider.OpenReaderCalls);
        Assert.Equal(0, fixture.Provider.OpenWriterCalls);
    }

    [Fact]
    public async Task DisposeDuringWriteAuthorizationPreventsWriterFactoryCall()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        HufuWorkspaceAccess? access = null;
        var authorizer = new DisposeDuringPermitAuthorizer(() => access!.Dispose());
        access = new HufuWorkspaceAccess(fixture.Provider, authorizer, fixture.Journal);

        var result = await access.WriteFileAsync(fixture.WriteRequest("src/a.txt"));

        Assert.Equal(ResourceFailureKind.ProviderFailure, result.Failure);
        Assert.Equal(0, fixture.Provider.OpenWriterCalls);
    }

    [Fact]
    public async Task WorkspaceMismatchDoesNotAuthorizeOrOpenProvider()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);

        var result = await fixture.Access.ReadFileAsync(fixture.ReadRequest("src/a.txt", new("other-workspace")));

        Assert.Equal(ResourceFailureKind.InvalidRequest, result.Failure);
        Assert.Empty(fixture.Recorder.Requests);
        Assert.Equal(0, fixture.Provider.OpenReaderCalls);
    }

    [Fact]
    public async Task CancellationAfterAuthorizerReturnsPermitPreventsProviderOpen()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        using var cancellation = new CancellationTokenSource();
        var authorizer = new CancelAfterPermitAuthorizer(cancellation);
        using var access = new HufuWorkspaceAccess(fixture.Provider, authorizer, fixture.Journal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            access.ReadFileAsync(fixture.ReadRequest("src/a.txt"), cancellation.Token).AsTask());

        Assert.Equal(0, fixture.Provider.OpenReaderCalls);
    }

    [Fact]
    public async Task RevokedCurrentSnapshotDeniesNextCallBeforeProvider()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        var first = fixture.ReadRequest("src/a.txt");
        Assert.True((await fixture.Access.ReadFileAsync(first)).Succeeded);
        fixture.Evaluator.Status = AuthorityStatus.Deny;
        var second = fixture.ReadRequest("src/b.txt");

        var result = await fixture.Access.ReadFileAsync(second);

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Equal(1, fixture.Provider.OpenReaderCalls);
    }

    [Fact]
    public async Task MissingMandatoryDecisionEvidenceBlocksBeforeProvider()
    {
        var fixture = new Fixture(AuthorityStatus.Permit) { Recorder = new TestRecorder { Acknowledge = false } };
        fixture.ResetAccess();

        var result = await fixture.Access.ReadFileAsync(fixture.ReadRequest("src/a.txt"));

        Assert.Equal(ResourceFailureKind.AuthorizationUnavailable, result.Failure);
        Assert.Equal(0, fixture.Provider.OpenReaderCalls);
        Assert.Equal(0, fixture.Provider.ReaderCalls);
    }

    [Fact]
    public async Task AsyncAuthoritySeamRejectsWrongRequestMissingEvidenceAndInconsistentStatus()
    {
        foreach (var corrupt in new[] { "wrong-request", "missing-evidence", "inconsistent-status" })
        {
            var fixture = new Fixture(AuthorityStatus.Permit);
            var request = fixture.ReadRequest("src/a.txt");
            var adapter = new HufuResourceAuthorizer(Context, Fixture.HostInvocationFor(request.Invocation.RequestIdentity),
                Workspace, new TestRequestAuthorizer(authorityRequest =>
                {
                    var answer = Authorized(authorityRequest);
                    return corrupt switch
                    {
                        "wrong-request" => answer with { Request = authorityRequest with { RequestIdentity = "substituted" } },
                        "missing-evidence" => answer with { EvidenceRecorded = false },
                        _ => answer with { Decision = answer.Decision! with { Status = AuthorityStatus.Deny } }
                    };
                }));
            using var access = new HufuWorkspaceAccess(fixture.Provider, adapter, fixture.Journal);

            var result = await access.ReadFileAsync(request);

            Assert.Equal(ResourceFailureKind.AuthorizationUnavailable, result.Failure);
            Assert.Equal(0, fixture.Provider.OpenReaderCalls);
        }
    }

    [Fact]
    public async Task CurrentAuthorityPermitThatExpiresDuringRecordingDoesNotOpenProvider()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var expiring = new AuthoritySnapshot(Context, "short-lived",
            [new AuthorityLayer("run", [new AuthorityGrant("grant", [AuthorityAction.ReadFile],
                new(Workspace.Value, "", AuthorityScopeKind.Subtree), [], clock.GetUtcNow().AddSeconds(-1),
                clock.GetUtcNow().AddSeconds(1))])], [], clock.GetUtcNow().AddSeconds(1));
        var recorder = new TestRecorder { OnRecord = () => clock.Advance(TimeSpan.FromSeconds(2)) };
        var current = new CurrentAuthorityRequestAuthorizer(new TestSource(expiring), fixture.Evaluator, recorder, clock);
        var adapter = new HufuResourceAuthorizer(Context, Fixture.HostInvocationFor(new("host-bound-request")),
            Workspace, current);
        using var access = new HufuWorkspaceAccess(fixture.Provider, adapter, fixture.Journal);

        var result = await access.ReadFileAsync(fixture.ReadRequest("src/a.txt"));

        Assert.Equal(ResourceFailureKind.AuthorizationUnavailable, result.Failure);
        Assert.Single(recorder.Requests);
        Assert.Equal(0, fixture.Provider.OpenReaderCalls);
    }

    [Fact]
    public async Task SameAuthorizerChecksAChildBeforeTestProviderReturnsIt()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        fixture.Provider.CheckChild = true;
        fixture.Evaluator.DeniedPath = "src/private.txt";

        var result = await fixture.Access.ListDirectoryAsync(fixture.ListRequest("src"));

        Assert.True(result.Succeeded);
        Assert.Equal("public.txt", Assert.Single(result.Value!.Entries).Name);
        Assert.Equal(3, fixture.Recorder.Requests.Count);
        Assert.Contains(fixture.Recorder.Requests, r => r.RelativePath == "src/private.txt");
    }

    [Fact]
    public async Task AuthorizedPagesReuseOneReaderSession()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        var first = await fixture.Access.ListDirectoryAsync(fixture.ListRequest("src"));
        var second = await fixture.Access.ListDirectoryAsync(fixture.ListRequest("src", new("next")));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal("first.txt", Assert.Single(first.Value!.Entries).Name);
        Assert.Equal("second.txt", Assert.Single(second.Value!.Entries).Name);
        Assert.Equal(1, fixture.Provider.OpenReaderCalls);
    }

    [Fact]
    public async Task ConditionalWriteMapsOnlyToWriteFileAuthorityAndDenialNeverOpensWriter()
    {
        var fixture = new Fixture(AuthorityStatus.Deny);
        var request = fixture.WriteRequest("src/a.txt");

        var result = await fixture.Access.WriteFileAsync(request);

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Equal(0, fixture.Provider.OpenWriterCalls);
        Assert.Equal(AuthorityAction.WriteFile, Assert.Single(fixture.Recorder.Requests).Action);
    }

    [Fact]
    public async Task AllowedConditionalWritePassesSameHooksToProvider()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        var request = fixture.WriteRequest("src/a.txt");

        var result = await fixture.Access.WriteFileAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(1, fixture.Provider.OpenWriterCalls);
        Assert.Same(fixture.Provider.OpenedAuthorizer, fixture.AccessAuthorizer);
        Assert.Same(fixture.Provider.OpenedJournal, fixture.Journal);
    }

    [Fact]
    public async Task WritePayloadIsFrozenBeforeAuthorizationAndDispatch()
    {
        var fixture = new Fixture(AuthorityStatus.Permit);
        var callerBytes = new byte[] { 1, 2, 3 };
        var request = fixture.WriteRequest("src/a.txt", callerBytes);
        fixture.Recorder.OnRecord = () => callerBytes[0] = 9;

        var result = await fixture.Access.WriteFileAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(new byte[] { 1, 2, 3 }, fixture.Provider.LastWrittenContent);
    }

    private sealed class Fixture
    {
        public readonly AuthoritySnapshot Snapshot;
        public readonly TestSource Source;
        public readonly TestEvaluator Evaluator;
        public TestRecorder Recorder;
        public readonly TestProvider Provider;
        public HufuWorkspaceAccess Access;
        public IResourceAuthorizer AccessAuthorizer { get; private set; } = null!;
        public TestJournal Journal { get; } = new();
        private readonly AuthenticatedAuthorityContext context = Context;

        public Fixture(AuthorityStatus status)
        {
            Snapshot = SnapshotFor();
            Source = new(Snapshot);
            Evaluator = new() { Status = status };
            Recorder = new();
            Provider = new(Workspace);
            Access = NewAccess();
        }

        public void ResetAccess() => Access = NewAccess();
        private HufuWorkspaceAccess NewAccess()
        {
            AccessAuthorizer = new HufuResourceAuthorizer(context, Invocation(new("host-bound-request")),
                Workspace, Source, Evaluator, Recorder);
            return new(Provider, AccessAuthorizer, Journal);
        }

        public FileReadRequest ReadRequest(string path, WorkspaceId? workspace = null)
        {
            var request = new FileReadRequest(Invocation(default), workspace ?? Workspace, new(path), new IoLimits(1024));
            var id = ResourceRequestIdentity.Compute(request);
            return request with { Invocation = Invocation(id) };
        }

        public DirectoryListRequest ListRequest(string path, DirectoryContinuation? continuation = null)
        {
            var request = new DirectoryListRequest(Invocation(default), Workspace, new(path), 20, 20, 4096, continuation);
            var id = ResourceRequestIdentity.Compute(request);
            return request with { Invocation = Invocation(id) };
        }

        public FileWriteRequest WriteRequest(string path, byte[]? content = null)
        {
            var request = new FileWriteRequest(Invocation(default), Workspace, new(path), content ?? new byte[] { 1, 2, 3 },
                new IoLimits(1024), new(WritePreconditionKind.MustNotExist));
            var id = ResourceRequestIdentity.Compute(request);
            return request with { Invocation = Invocation(id) };
        }

        public static HostInvocation HostInvocationFor(RequestIdentity id) => new("invocation", "subject", "effect", "attempt", "scope", null, id);
        private static HostInvocation Invocation(RequestIdentity id) => HostInvocationFor(id);
        private static AuthoritySnapshot SnapshotFor() => new(Context, "version-1",
            [new AuthorityLayer("run", [new AuthorityGrant("grant", [AuthorityAction.ReadFile, AuthorityAction.ListDirectory,
                AuthorityAction.ReadMetadata, AuthorityAction.PatchFile, AuthorityAction.WriteFile],
                new(Workspace.Value, "", AuthorityScopeKind.Subtree), [], DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1))])], [], DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class TestSource(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AuthoritySnapshot?>(snapshot);
    }

    private sealed class TestEvaluator : IAuthorityEvaluator
    {
        public AuthorityStatus Status { get; set; }
        public string? DeniedPath { get; set; }
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now) =>
            new(request.RelativePath == DeniedPath ? AuthorityStatus.Deny : Status,
                "test.decision", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
    }

    private sealed class TestRecorder : IAuthorityDecisionRecorder
    {
        public bool Acknowledge { get; set; } = true;
        public Action? OnRecord { get; set; }
        public List<AuthorityRequest> Requests { get; } = [];
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        { Requests.Add(request); OnRecord?.Invoke(); return ValueTask.FromResult(Acknowledge); }
    }

    private sealed class TestProvider(WorkspaceId workspace) : IWorkspaceProvider
    {
        public WorkspaceId Workspace { get; } = workspace;
        public WorkspaceProviderCapabilities Capabilities { get; } = new("test-read-v1", "test-write-v1", true, true);
        public int OpenReaderCalls { get; private set; }
        public int ReaderCalls { get; private set; }
        public int OpenWriterCalls { get; private set; }
        public IResourceAuthorizer? OpenedAuthorizer { get; private set; }
        public IResourceMutationJournal? OpenedJournal { get; private set; }
        public byte[]? LastWrittenContent { get; private set; }
        public bool CheckChild { get; set; }
        private IResourceAuthorizer? _authorizer;
        public IWorkspaceReaderSession OpenReader(IResourceAuthorizer authorizer, WorkspaceReaderOptions? options = null)
        { OpenReaderCalls++; _authorizer = authorizer; return new Reader(this); }
        public IWorkspaceConditionalWriter OpenWriter(IResourceAuthorizer authorizer, IResourceMutationJournal journal, WorkspaceWriterOptions? options = null)
        { OpenWriterCalls++; OpenedAuthorizer = authorizer; OpenedJournal = journal; return new Writer(this); }

        private sealed class Reader(TestProvider owner) : IWorkspaceReaderSession
        {
            private int _listCalls;
            public ValueTask<ResourceResult<FileReadResult>> ReadFileAsync(FileReadRequest request, CancellationToken cancellationToken = default)
            { owner.ReaderCalls++; return ValueTask.FromResult(ResourceResult<FileReadResult>.Success(new(new byte[] { 1 }, new("v1")))); }
            public ValueTask<ResourceResult<FileMetadata>> GetFileMetadataAsync(FileMetadataRequest request, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(ResourceResult<FileMetadata>.Success(new(true, 1, new("v1"))));
            public async ValueTask<ResourceResult<DirectoryPage>> ListDirectoryAsync(DirectoryListRequest request, CancellationToken cancellationToken = default)
            {
                _listCalls++;
                if (!owner.CheckChild)
                {
                    if (_listCalls == 1 && request.Continuation is null)
                        return ResourceResult<DirectoryPage>.Success(new([new("first.txt", false, 1, new("v1"))], false, new("next"), DirectoryTruncationReason.EntryLimit));
                    if (_listCalls == 2 && request.Continuation?.Value == "next")
                        return ResourceResult<DirectoryPage>.Success(new([new("second.txt", false, 1, new("v1"))], true, null, null));
                    return ResourceResult<DirectoryPage>.Failed(ResourceFailureKind.InvalidRequest);
                }
                var entries = new List<DirectoryEntry>();
                foreach (var name in new[] { "public.txt", "private.txt" })
                {
                    var path = request.Path.Value + "/" + name;
                    if (owner.CheckChild)
                    {
                        var identity = request.Invocation.RequestIdentity;
                        var decision = await owner._authorizer!.AuthorizeAsync(new(request.Invocation, identity,
                            ResourceAction.ReadMetadata, new ResourceBinding.WorkspaceEntry(owner.Workspace, new(path))), cancellationToken);
                        if (decision.Status != AuthorizationStatus.Permit) continue;
                    }
                    entries.Add(new(name, false, 1, new("v1")));
                }
                return ResourceResult<DirectoryPage>.Success(new(entries, true, null, null));
            }
            public void Dispose() { }
        }
        private sealed class Writer(TestProvider owner) : IWorkspaceConditionalWriter
        {
            public ValueTask<ResourceResult<ResourceVersion>> WriteFileAsync(FileWriteRequest request, CancellationToken cancellationToken = default)
            {
                owner.LastWrittenContent = request.Content.ToArray();
                return ValueTask.FromResult(ResourceResult<ResourceVersion>.Success(new("v2")));
            }
        }
    }

    private sealed class TestJournal : IResourceMutationJournal
    {
        public ValueTask<MutationStartDecision> StartAsync(MutationStartRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Deny));
        public ValueTask<bool> CompleteAsync(MutationCompletion completion, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }

    private sealed class CancelAfterPermitAuthorizer(CancellationTokenSource cancellation) : IResourceAuthorizer
    {
        public ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Permit));
        }
    }

    private sealed class DisposeDuringPermitAuthorizer(Action dispose) : IResourceAuthorizer
    {
        public ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            dispose();
            return ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Permit));
        }
    }

    private static AuthorityRequestAuthorization Authorized(AuthorityRequest request)
    {
        var decision = new AuthorityDecision(AuthorityStatus.Permit, "test.permit", "version-1", "test-seam", new string('a', 64));
        return new(request, AuthorityStatus.Permit, decision, true);
    }

    private sealed class TestRequestAuthorizer(Func<AuthorityRequest, AuthorityRequestAuthorization> authorize) : IAuthorityRequestAuthorizer
    {
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authorize(request));
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;
        public override DateTimeOffset GetUtcNow() => _current;
        public void Advance(TimeSpan amount) => _current += amount;
    }
}
