using Penghou.Hufu.Broker;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Xunit;

namespace Penghou.Hufu.Broker.Tests;

/// <summary>
/// AE-2.2 transport: a real named-pipe round trip uses the existing bootstrap/
/// session model; the transport creates no second identity model.
/// </summary>
public sealed class BrokerPipeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Workspace = "broker-pipe-ws";

    [Fact]
    public async Task NamedPipeRoundTrip_ReturnsAuthorizedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var bytes = new byte[] { 7, 14, 21 };
        File.WriteAllBytes(workspace.File("file.txt"), bytes);

        var pipeName = "hufu-broker-" + Guid.NewGuid().ToString("N");
        using var registry = new BrokerSessionRegistry(new Clock(Now));
        using var references = new BrokerAuthorityReferenceStore(new Clock(Now));
        using var arbiter = new BrokerStartArbiter();
        var evidence = new InMemoryEvidence();

        var context = new AuthenticatedAuthorityContext("tenant", "subject", "run", "revision", "fence");
        var bootstrap = registry.Issue("tenant", "subject", "worker-1", "audience");
        var session = new BrokerSessionPrincipal(bootstrap.SessionId, "tenant", "subject", "worker-1", "audience",
            BrokerProtocol.Version, Now, Now.AddHours(1));
        var reference = references.Register(session, "delegation-1", "gen-1", "fence-1", context, Workspace,
            "file.txt", AuthorityAction.ReadFile, 4096);

        var source = new MutableSnapshotSource(Snapshot(context, "v1"));
        var authority = new CurrentAuthorityRequestAuthorizer(source, new SnapshotGrantEvaluator(), new AllowRecorder(), new Clock(Now));
        var provider = new LocalWorkspaceProvider(new WorkspaceId(Workspace), workspace.Root, LocalPatchNamespace.HostControlled);
        var handler = new BrokerReadHandler(arbiter, authority, references, evidence,
            _ => new BrokerWorkspaceSource(provider, new TestJournal()), new Clock(Now));

        await using var server = new BrokerPipeServer(pipeName, registry, handler);
        server.Start();
        await using var client = await BrokerPipeClient.ConnectAsync(pipeName, bootstrap.BootstrapSecret);
        Assert.NotNull(client);
        var request = new BrokerReadFileRequest(BrokerProtocol.Version, "op-1", reference.Reference, Workspace,
            "file.txt", 4096, Now.AddSeconds(10));

        var response = await client.ReadAsync(request);

        Assert.True(response.Succeeded, response.Failure?.ToString());
        Assert.Equal(bytes, response.Content.ToArray());
        Assert.Single(evidence.Starts);
        Assert.Single(evidence.Releases);
    }

    [Fact]
    public async Task UnknownBootstrap_IsRejectedAtHandshake()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipeName = "hufu-broker-" + Guid.NewGuid().ToString("N");
        using var registry = new BrokerSessionRegistry(new Clock(Now));
        using var references = new BrokerAuthorityReferenceStore(new Clock(Now));
        using var arbiter = new BrokerStartArbiter();
        var authority = new CurrentAuthorityRequestAuthorizer(new MutableSnapshotSource(Snapshot(
            new AuthenticatedAuthorityContext("tenant", "subject", "run", "revision", "fence"), "v1")),
            new SnapshotGrantEvaluator(), new AllowRecorder(), new Clock(Now));
        var handler = new BrokerReadHandler(arbiter, authority, references, new InMemoryEvidence(),
            _ => throw new InvalidOperationException("no provider read expected"), new Clock(Now));

        await using var server = new BrokerPipeServer(pipeName, registry, handler);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = await BrokerPipeClient.ConnectAsync(pipeName, "not-a-valid-bootstrap", timeout.Token);

        Assert.Null(client);
    }

    private static AuthoritySnapshot Snapshot(AuthenticatedAuthorityContext context, string version) =>
        new(context, version, [new AuthorityLayer("broker",
            [new AuthorityGrant("workspace-grant", [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata],
                new(Workspace, "", AuthorityScopeKind.Subtree), [], Now.AddMinutes(-1), Now.AddHours(1))])], [], Now.AddHours(1));

    private sealed class MutableSnapshotSource(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<AuthoritySnapshot?>(snapshot);
    }

    private sealed class SnapshotGrantEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
        {
            var target = new AuthorityScope(request.WorkspaceId, request.RelativePath, AuthorityScopeKind.Exact);
            var covered = snapshot.Layers.All(layer => layer.Grants.Any(grant =>
                grant.NotBefore <= now && now < grant.ExpiresAt && grant.Actions.Contains(request.Action) &&
                AuthorityValidation.Contains(grant.Scope, target)));
            var status = covered ? AuthorityStatus.Permit : AuthorityStatus.Deny;
            return new(status, covered ? "test.granted" : "test.denied", snapshot.Version,
                "broker-pipe-tests-v1", snapshot.Identity);
        }
    }

    private sealed class AllowRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken ct = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class InMemoryEvidence : IBrokerEvidenceRecorder
    {
        public List<BrokerStartEvidence> Starts { get; } = [];
        public List<BrokerReleaseEvidence> Releases { get; } = [];
        public ValueTask<BrokerEvidenceReceipt?> RecordStartAsync(BrokerStartEvidence evidence, CancellationToken ct = default)
        {
            Starts.Add(evidence);
            return ValueTask.FromResult<BrokerEvidenceReceipt?>(new BrokerEvidenceReceipt("start"));
        }
        public ValueTask<BrokerEvidenceReceipt?> RecordReleaseAsync(BrokerReleaseEvidence evidence, CancellationToken ct = default)
        {
            Releases.Add(evidence);
            return ValueTask.FromResult<BrokerEvidenceReceipt?>(new BrokerEvidenceReceipt("release"));
        }
    }

    private sealed class TestJournal : IResourceMutationJournal
    {
        public ValueTask<MutationStartDecision> StartAsync(MutationStartRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Started, "test"));
        public ValueTask<bool> CompleteAsync(MutationCompletion completion, CancellationToken ct = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hufu-broker-pipe-" + Guid.NewGuid().ToString("N"));
        public TempWorkspace() => Directory.CreateDirectory(Root);
        public string File(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
