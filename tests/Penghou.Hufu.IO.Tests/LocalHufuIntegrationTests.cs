using System.Security.Cryptography;
using Penghou.Hufu;
using Penghou.Hufu.IO;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Xunit;

namespace Penghou.Hufu.IO.Tests;

public sealed class LocalHufuIntegrationTests
{
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");
    private static readonly WorkspaceId Workspace = new("hufu-local-integration");

    [Fact]
    public async Task LocalReadReturnsOnlyBoundedAuthorizedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var content = new byte[] { 4, 0, 5, 0, 6 };
        File.WriteAllBytes(workspace.File("read.bin"), content);
        var integration = Create(workspace, Snapshot("read-v1", [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata]));
        using var access = integration.Access;

        var result = await access.ReadFileAsync(ReadRequest("read.bin", maxBytes: content.Length));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(content, result.Value!.Content.ToArray());
        Assert.Contains(integration.Recorder.Entries,
            e => e.Request.Action == AuthorityAction.ReadFile && e.Decision.Status == AuthorityStatus.Permit);
    }

    [Fact]
    public async Task LocalListingChecksExcludedCandidatesAndContinuesAcrossPagesInOneSession()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        File.WriteAllText(workspace.File("public-a.txt"), "a");
        File.WriteAllText(workspace.File("private-secret.txt"), "secret");
        File.WriteAllText(workspace.File("public-b.txt"), "b");
        var snapshot = Snapshot("list-v1", [AuthorityAction.ListDirectory, AuthorityAction.ReadMetadata],
            [new AuthorityScope(Workspace.Value, "private-secret.txt", AuthorityScopeKind.Exact)]);
        var integration = Create(workspace, snapshot);
        using var access = integration.Access;

        var names = new List<string>();
        DirectoryContinuation? continuation = null;
        var pages = 0;
        while (true)
        {
            var result = await access.ListDirectoryAsync(ListRequest("", 1, continuation));
            Assert.True(result.Succeeded, result.Failure?.ToString());
            pages++;
            names.AddRange(result.Value!.Entries.Select(entry => entry.Name));
            if (result.Value.IsComplete) break;
            Assert.NotNull(result.Value.Continuation);
            continuation = result.Value.Continuation!.Value;
            Assert.True(pages < 5, "Local pagination did not complete within the bounded page loop.");
        }

        Assert.True(pages > 1);
        Assert.Contains("public-a.txt", names);
        Assert.Contains("public-b.txt", names);
        Assert.DoesNotContain("private-secret.txt", names);
        Assert.Contains(integration.Recorder.Entries, e => e.Request.RelativePath == "private-secret.txt" &&
            e.Request.Action == AuthorityAction.ReadMetadata && e.Decision.Status == AuthorityStatus.Deny);
    }

    [Fact]
    public async Task LocalConditionalWriteUsesExactVersionAndTestJournalAtLockedStart()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var original = new byte[] { 1, 0, 255 };
        var proposed = new byte[] { 2, 0, 254, 3 };
        File.WriteAllBytes(workspace.File("write.bin"), original);
        var integration = Create(workspace, Snapshot("write-v1",
            [AuthorityAction.WriteFile, AuthorityAction.ReadFile, AuthorityAction.ReadMetadata]));
        using var access = integration.Access;
        var request = WriteRequest("write.bin", proposed, Version(original));

        var result = await access.WriteFileAsync(request);

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(proposed, File.ReadAllBytes(workspace.File("write.bin")));
        var start = Assert.Single(integration.Journal.Starts);
        Assert.Equal(Version(original), start.OriginalVersion);
        Assert.Equal(Version(proposed), start.ProposedVersion);
        Assert.Equal(MutationOutcome.Completed, Assert.Single(integration.Journal.Completions).Outcome);
        Assert.DoesNotContain(integration.Recorder.Entries, e => e.Request.Action == AuthorityAction.PatchFile);
    }

    [Fact]
    public async Task CurrentRevocationBeforeFinalWriteCheckPreventsStartAndPreservesBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var original = new byte[] { 7, 0, 8 };
        var proposed = new byte[] { 9, 0, 10 };
        File.WriteAllBytes(workspace.File("revoke.bin"), original);
        var current = Snapshot("write-v1",
            [AuthorityAction.WriteFile, AuthorityAction.ReadFile, AuthorityAction.ReadMetadata]);
        var revoked = Snapshot("write-revoked-v2",
            [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata]);
        var integration = Create(workspace, current);
        integration.Recorder.AfterRecord = entry =>
        {
            if (entry.Request.Action == AuthorityAction.ReadMetadata && entry.Request.RelativePath == "revoke.bin")
                integration.Source.Current = revoked;
        };
        using var access = integration.Access;

        var result = await access.WriteFileAsync(WriteRequest("revoke.bin", proposed, Version(original)));

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Empty(integration.Journal.Starts);
        Assert.Equal(original, File.ReadAllBytes(workspace.File("revoke.bin")));
        Assert.Contains(integration.Recorder.Entries, e => e.Request.Action == AuthorityAction.WriteFile &&
            e.Request.RelativePath == "revoke.bin" && e.Decision.SnapshotVersion == "write-revoked-v2" &&
            e.Decision.Status == AuthorityStatus.Deny);
    }

    private static Integration Create(TemporaryWorkspace workspace, AuthoritySnapshot snapshot)
    {
        var source = new MutableSnapshotSource(snapshot);
        var evaluator = new SnapshotGrantEvaluator();
        var recorder = new RecordingDecisionRecorder();
        var journal = new TestMutationJournal();
        var invocation = Invocation(new("host-invocation-binding"));
        var authorizer = new HufuResourceAuthorizer(Context, invocation, Workspace, source, evaluator, recorder);
        var provider = new LocalWorkspaceProvider(Workspace, workspace.Root, LocalPatchNamespace.HostControlled);
        var access = new HufuWorkspaceAccess(provider, authorizer, journal,
            new WorkspaceReaderOptions(MaxEntries: 32, MaxCandidatesScanned: 128, MaxTotalCandidatesScanned: 512),
            new WorkspaceWriterOptions(MaxOriginalBytes: 1024, MaxReadBytes: 4096, TimeoutMilliseconds: 10_000));
        return new(source, recorder, journal, access);
    }

    private static AuthoritySnapshot Snapshot(string version, IReadOnlyList<AuthorityAction> actions,
        IReadOnlyList<AuthorityScope>? exclusions = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Context, version,
            [new AuthorityLayer("integration", [new AuthorityGrant("workspace-grant", actions,
                new(Workspace.Value, "", AuthorityScopeKind.Subtree), exclusions ?? [], now.AddMinutes(-1), now.AddHours(1))])],
            [], now.AddHours(1));
    }

    private static FileReadRequest ReadRequest(string path, int maxBytes)
    {
        var request = new FileReadRequest(Invocation(default), Workspace, new(path), new(maxBytes));
        return request with { Invocation = Invocation(ResourceRequestIdentity.Compute(request)) };
    }

    private static DirectoryListRequest ListRequest(string path, int maxEntries, DirectoryContinuation? continuation)
    {
        var request = new DirectoryListRequest(Invocation(default), Workspace, new(path), maxEntries,
            MaxCandidatesScanned: 32, MaxOutputBytes: 4096, Continuation: continuation);
        return request with { Invocation = Invocation(ResourceRequestIdentity.Compute(request)) };
    }

    private static FileWriteRequest WriteRequest(string path, byte[] proposed, ResourceVersion originalVersion)
    {
        var request = new FileWriteRequest(Invocation(default), Workspace, new(path), proposed, new(1024),
            new(WritePreconditionKind.MustMatchVersion, originalVersion));
        return request with { Invocation = Invocation(ResourceRequestIdentity.Compute(request)) };
    }

    private static HostInvocation Invocation(RequestIdentity identity) =>
        new("hufu-local-invocation", Context.SubjectId, "effect", "attempt", "scope", null, identity);

    private static ResourceVersion Version(byte[] bytes) => new("local-read-v1:sha256:" +
        Convert.ToHexString(SHA256.HashData(bytes)));

    private sealed record Integration(MutableSnapshotSource Source, RecordingDecisionRecorder Recorder,
        TestMutationJournal Journal, HufuWorkspaceAccess Access);

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hufu-io-local-" + Guid.NewGuid().ToString("N"));
        public TemporaryWorkspace() => Directory.CreateDirectory(Root);
        public string File(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class MutableSnapshotSource(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        public AuthoritySnapshot Current { get; set; } = snapshot;
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<AuthoritySnapshot?>(Current);
    }

    private sealed class SnapshotGrantEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
        {
            var target = new AuthorityScope(request.WorkspaceId, request.RelativePath, AuthorityScopeKind.Exact);
            var mandatoryDeny = snapshot.MandatoryDenials.Any(denial => AuthorityValidation.Contains(denial, target));
            var allLayersPermit = snapshot.Layers.All(layer => layer.Grants.Any(grant =>
                grant.NotBefore <= now && now < grant.ExpiresAt && grant.Actions.Contains(request.Action) &&
                AuthorityValidation.Contains(grant.Scope, target) &&
                !grant.Exclusions.Any(exclusion => AuthorityValidation.Contains(exclusion, target))));
            var status = snapshot.Context == request.Context && snapshot.ValidUntil > now && !mandatoryDeny && allLayersPermit
                ? AuthorityStatus.Permit : AuthorityStatus.Deny;
            return new(status, status == AuthorityStatus.Permit ? "test.granted" : "test.denied",
                snapshot.Version, "hufu-io-tests-grant-evaluator-v1", snapshot.Identity);
        }
    }

    private sealed class RecordingDecisionRecorder : IAuthorityDecisionRecorder
    {
        public List<DecisionEntry> Entries { get; } = [];
        public Action<DecisionEntry>? AfterRecord { get; set; }
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision,
            CancellationToken cancellationToken = default)
        {
            var entry = new DecisionEntry(request, decision);
            Entries.Add(entry);
            AfterRecord?.Invoke(entry);
            return ValueTask.FromResult(true);
        }
    }

    private sealed record DecisionEntry(AuthorityRequest Request, AuthorityDecision Decision);

    private sealed class TestMutationJournal : IResourceMutationJournal
    {
        public List<MutationStartRequest> Starts { get; } = [];
        public List<MutationCompletion> Completions { get; } = [];
        public ValueTask<MutationStartDecision> StartAsync(MutationStartRequest request,
            CancellationToken cancellationToken = default)
        {
            Starts.Add(request);
            return ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Started, "test-start-evidence"));
        }
        public ValueTask<bool> CompleteAsync(MutationCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            return ValueTask.FromResult(true);
        }
    }
}
