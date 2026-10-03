using BiscuitSharp;
using Penghou.Hufu.Biscuit.Sqlite;
using Penghou.Hufu.IO;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using static Penghou.Hufu.Biscuit.Tests.BiscuitIntegrationFixture;

namespace Penghou.Hufu.Biscuit.Tests;

public sealed class BiscuitResourceReadTests
{
    private static readonly WorkspaceId Workspace = new("workspace");

    [Fact]
    public async Task RegisteredBiscuitAllowsRealWorkspaceReadAndRecordsEvidence()
    {
        RequireWindows();
        using var fixture = new BiscuitIntegrationFixture();
        await PublishResourceSnapshot(fixture);
        using var root = new TemporaryWorkspace();
        var content = "real authorized content"u8.ToArray();
        File.WriteAllBytes(root.File("src/public.txt"), content);
        var envelope = await fixture.IssueAsync();
        var invocation = Invocation();
        var authorizer = CreateAuthorizer(fixture, envelope, invocation, root.Path);
        var provider = new CountingProvider(new LocalWorkspaceProvider(Workspace, root.Path));
        using var access = Access(provider, authorizer);
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        fixture.OnRegistryAuthorization = request =>
        {
            if (request.Operation == BiscuitRegistryOperation.RecordVerification && request.Evidence is not null)
                recorded.Add(request.Evidence.Id);
        };

        var result = await access.ReadFileAsync(ReadRequest(invocation, "src/public.txt"));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(content, result.Value!.Content.ToArray());
        Assert.Equal(1, provider.OpenReaderCalls);
        Assert.Equal(1, provider.ReadFileCalls);
        Assert.NotEmpty(recorded);
        foreach (var id in recorded)
        {
            var decision = await fixture.Store.ReadDecisionAsync(Actor, AuthoritySubject.From(Context), id);
            Assert.Equal(AuthorityReadStatus.Active, decision.Status);
            Assert.NotNull(decision.Entry);
        }
    }

    [Fact]
    public async Task AuthorizedListingOmitsExcludedChildAndRecordsItsDenial()
    {
        RequireWindows();
        using var fixture = new BiscuitIntegrationFixture();
        await PublishResourceSnapshot(fixture);
        using var root = new TemporaryWorkspace();
        File.WriteAllText(root.File("src/a.txt"), "a");
        File.WriteAllText(root.File("src/b.txt"), "b");
        File.WriteAllText(root.File("src/private/secret.txt"), "secret");
        var envelope = await fixture.IssueAsync();
        var invocation = Invocation();
        var authorizer = CreateAuthorizer(fixture, envelope, invocation, root.Path);
        var evidence = new List<BiscuitDecisionEvidence>();
        fixture.OnRegistryAuthorization = access =>
        {
            if (access.Operation == BiscuitRegistryOperation.RecordVerification && access.Evidence is not null)
                evidence.Add(access.Evidence);
        };
        using var access = Access(new LocalWorkspaceProvider(Workspace, root.Path), authorizer,
            new WorkspaceReaderOptions(MaxEntries: 64, MaxCandidatesScanned: 128, MaxTotalCandidatesScanned: 256));

        var result = await access.ListDirectoryAsync(ListRequest(invocation, "src", 64, null));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.True(result.Value!.IsComplete);
        var names = result.Value.Entries.Select(entry => entry.Name).ToArray();
        Assert.Contains("a.txt", names);
        Assert.Contains("b.txt", names);
        Assert.DoesNotContain("private", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.txt", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(evidence, item => item.Request.RelativePath == "src/private" &&
            item.Request.Action == AuthorityAction.ReadMetadata && item.Decision.Status == AuthorityStatus.Deny);
    }

    [Fact]
    public async Task CredentialRevocationBetweenListPagesFailsClosedBeforeSecondProviderCall()
    {
        RequireWindows();
        using var fixture = new BiscuitIntegrationFixture();
        await PublishResourceSnapshot(fixture);
        using var root = new TemporaryWorkspace();
        File.WriteAllText(root.File("src/a.txt"), "a");
        File.WriteAllText(root.File("src/b.txt"), "b");
        var envelope = await fixture.IssueAsync();
        var invocation = Invocation();
        var authorizer = CreateAuthorizer(fixture, envelope, invocation, root.Path);
        var provider = new CountingProvider(new LocalWorkspaceProvider(Workspace, root.Path));
        using var access = Access(provider, authorizer,
            new WorkspaceReaderOptions(MaxEntries: 1, MaxCandidatesScanned: 64, MaxTotalCandidatesScanned: 256));

        var first = await access.ListDirectoryAsync(ListRequest(invocation, "src", 1, null));
        Assert.True(first.Succeeded, first.Failure?.ToString());
        Assert.False(first.Value!.IsComplete);
        Assert.NotNull(first.Value.Continuation);
        Assert.Equal(1, provider.ListDirectoryCalls);
        Assert.Equal(BiscuitRegistryStatus.Recorded, await fixture.Registry.RevokeAsync(
            Actor, Realm, Context, Fingerprint(envelope), "test revoke between pages"));

        var second = await access.ListDirectoryAsync(ListRequest(invocation, "src", 1, first.Value.Continuation));

        // Early revocation has no attributable decision/evidence to project, so the adapter fails closed as unavailable.
        Assert.Equal(ResourceFailureKind.AuthorizationUnavailable, second.Failure);
        Assert.Empty(second.Value?.Entries ?? []);
        Assert.Equal(1, provider.OpenReaderCalls);
        Assert.Equal(1, provider.ListDirectoryCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequiredCoreOrRegistryEvidenceFailureStopsBeforeOpeningProvider(bool failCoreEvidence)
    {
        RequireWindows();
        using var fixture = new BiscuitIntegrationFixture();
        await PublishResourceSnapshot(fixture);
        using var root = new TemporaryWorkspace();
        File.WriteAllText(root.File("src/file.txt"), "must not be read");
        var invocation = Invocation();
        var envelope = await fixture.IssueAsync();
        fixture.FailCoreEvidence = failCoreEvidence;
        fixture.FailRegistryEvidence = !failCoreEvidence;
        var provider = new CountingProvider(new LocalWorkspaceProvider(Workspace, root.Path));
        var authorizer = CreateAuthorizer(fixture, envelope, invocation, root.Path);
        using var access = Access(provider, authorizer);

        var result = await access.ReadFileAsync(ReadRequest(invocation, "src/file.txt"));

        Assert.Equal(ResourceFailureKind.AuthorizationUnavailable, result.Failure);
        Assert.Null(result.Value);
        Assert.Equal(0, provider.OpenReaderCalls);
    }

    [Fact]
    public async Task ForgedInvocationIsDeniedBeforeOpeningProvider()
    {
        RequireWindows();
        using var fixture = new BiscuitIntegrationFixture();
        await PublishResourceSnapshot(fixture);
        using var root = new TemporaryWorkspace();
        File.WriteAllText(root.File("src/file.txt"), "must not be read");
        var trusted = Invocation();
        var envelope = await fixture.IssueAsync();
        var authorizer = CreateAuthorizer(fixture, envelope, trusted, root.Path);
        var provider = new CountingProvider(new LocalWorkspaceProvider(Workspace, root.Path));
        using var access = Access(provider, authorizer);
        var forged = ReadRequest(trusted with { SubjectId = "forged-subject" }, "src/file.txt");

        var result = await access.ReadFileAsync(forged);

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Null(result.Value);
        Assert.Equal(0, provider.OpenReaderCalls);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("The real Local provider resource tests require Windows.");
    }

    private static Task PublishResourceSnapshot(BiscuitIntegrationFixture fixture)
    {
        var grant = new AuthorityGrant("grant",
            [AuthorityAction.ReadFile, AuthorityAction.ListDirectory, AuthorityAction.ReadMetadata],
            new("workspace", "", AuthorityScopeKind.Subtree),
            [new("workspace", "src/private", AuthorityScopeKind.Subtree)],
            Now.AddMinutes(-1), Now.AddHours(1));
        return fixture.PublishAsync(fixture.CreateSnapshot("resource-read-v1", [new("workflow", [grant])]));
    }

    private static HufuWorkspaceAccess Access(IWorkspaceProvider provider, HufuResourceAuthorizer authorizer,
        WorkspaceReaderOptions? readerOptions = null) => new(provider, authorizer, new FailClosedJournal(), readerOptions);

    private static HufuResourceAuthorizer CreateAuthorizer(BiscuitIntegrationFixture fixture,
        BiscuitEnvelope envelope, HostInvocation invocation, string physicalRoot)
    {
        // Trusted logical binding; Local independently checks actual native spelling/links.
        fixture.ResourceBindingFactory = request => new(request, "local-windows-read-v1",
            BiscuitProfile.Hash(BiscuitProfile.Utf8.GetBytes(physicalRoot + "\n" + request.RelativePath)),
            request.RequestIdentity);
        return new(Context, invocation, Workspace, new BiscuitRequestAuthorizer(fixture.Service, envelope));
    }

    private static FileReadRequest ReadRequest(HostInvocation invocation, string path)
    {
        var raw = new FileReadRequest(invocation with { RequestIdentity = default }, Workspace, new(path), new(1024));
        return raw with { Invocation = invocation with { RequestIdentity = ResourceRequestIdentity.Compute(raw) } };
    }

    private static DirectoryListRequest ListRequest(HostInvocation invocation, string path, int maxEntries,
        DirectoryContinuation? continuation)
    {
        var raw = new DirectoryListRequest(invocation with { RequestIdentity = default }, Workspace, new(path),
            maxEntries, MaxCandidatesScanned: 64, MaxOutputBytes: 4096, Continuation: continuation);
        return raw with { Invocation = invocation with { RequestIdentity = ResourceRequestIdentity.Compute(raw) } };
    }

    private static HostInvocation Invocation() =>
        new("resource-test-invocation", Context.SubjectId, "effect", "attempt", "scope", null,
            new RequestIdentity("initial-resource-binding"));

    private sealed class CountingProvider(IWorkspaceProvider inner) : IWorkspaceProvider
    {
        public int OpenReaderCalls { get; private set; }
        public int ReadFileCalls { get; private set; }
        public int ListDirectoryCalls { get; private set; }
        public WorkspaceId Workspace => inner.Workspace;
        public WorkspaceProviderCapabilities Capabilities => inner.Capabilities;

        public IWorkspaceReaderSession OpenReader(IResourceAuthorizer authorizer, WorkspaceReaderOptions? options = null)
        {
            OpenReaderCalls++;
            return new CountingReaderSession(inner.OpenReader(authorizer, options), this);
        }

        public IWorkspaceConditionalWriter OpenWriter(IResourceAuthorizer authorizer, IResourceMutationJournal journal,
            WorkspaceWriterOptions? options = null) => inner.OpenWriter(authorizer, journal, options);

        private sealed class CountingReaderSession(IWorkspaceReaderSession inner, CountingProvider owner) : IWorkspaceReaderSession
        {
            public ValueTask<ResourceResult<FileReadResult>> ReadFileAsync(FileReadRequest request,
                CancellationToken cancellationToken = default)
            {
                owner.ReadFileCalls++;
                return inner.ReadFileAsync(request, cancellationToken);
            }

            public ValueTask<ResourceResult<FileMetadata>> GetFileMetadataAsync(FileMetadataRequest request,
                CancellationToken cancellationToken = default) => inner.GetFileMetadataAsync(request, cancellationToken);

            public ValueTask<ResourceResult<DirectoryPage>> ListDirectoryAsync(DirectoryListRequest request,
                CancellationToken cancellationToken = default)
            {
                owner.ListDirectoryCalls++;
                return inner.ListDirectoryAsync(request, cancellationToken);
            }

            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class FailClosedJournal : IResourceMutationJournal
    {
        public ValueTask<MutationStartDecision> StartAsync(MutationStartRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Unavailable));

        public ValueTask<bool> CompleteAsync(MutationCompletion completion, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private const string Prefix = "hufu-biscuit-resource-read-";
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        public TemporaryWorkspace() => Directory.CreateDirectory(Path);

        public string File(string relative)
        {
            var path = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose()
        {
            var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            var target = System.IO.Path.GetFullPath(Path);
            if (target.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                System.IO.Path.GetFileName(target).StartsWith(Prefix, StringComparison.Ordinal))
                Directory.Delete(target, recursive: true);
        }
    }
}
