using Penghou.Hufu.Broker;
using Penghou.Hufu.IO;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Xunit;

namespace Penghou.Hufu.Broker.Tests;

/// <summary>
/// AE-2.2 vertical: the authenticated broker enforces AE-1 authorization and
/// commits start evidence inside the ordered boundary, reads through the real
/// HufuWorkspaceAccess/IWorkspaceReader path, and independently authorizes
/// release before returning any bytes.
/// </summary>
public sealed class BrokerReadHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Workspace = "broker-ws";

    [Fact]
    public async Task AuthorizedRead_ReturnsBytesAndBothEvidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(workspace.File("file.txt"), bytes);
        using var fixture = new Fixture(workspace);

        var response = await fixture.HandleAsync("op-1");

        Assert.True(response.Succeeded, response.Failure?.ToString());
        Assert.Equal(bytes, response.Content.ToArray());
        Assert.NotNull(response.StartEvidenceId);
        Assert.NotNull(response.ReleaseEvidenceId);
        Assert.Single(fixture.Evidence.Starts);
        Assert.Single(fixture.Evidence.Releases);
        Assert.Equal(1, fixture.WorkspaceSourceCalls);
    }

    [Fact]
    public async Task WrongWorkerSession_IsUnauthorizedWithoutProviderRead()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1, 2, 3]);
        using var fixture = new Fixture(workspace);
        var otherWorker = fixture.Session with { SessionId = "session-2", WorkerIdentity = "worker-2" };

        var response = await fixture.HandleAsync("op-1", session: otherWorker);

        Assert.Equal(BrokerFailureKind.Unauthorized, response.Failure);
        Assert.Empty(response.Content.ToArray());
        Assert.Equal(0, fixture.WorkspaceSourceCalls);
    }

    [Fact]
    public async Task RevokedReference_IsUnauthorized()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1, 2, 3]);
        using var fixture = new Fixture(workspace);
        await fixture.References.RevokeAsync(fixture.Reference.Reference);

        var response = await fixture.HandleAsync("op-1");

        Assert.Equal(BrokerFailureKind.Unauthorized, response.Failure);
        Assert.Equal(0, fixture.WorkspaceSourceCalls);
    }

    [Fact]
    public async Task StartEvidenceFailure_BlocksProviderRead()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1, 2, 3]);
        using var fixture = new Fixture(workspace);
        fixture.Evidence.FailStart = true;

        var response = await fixture.HandleAsync("op-1");

        Assert.Equal(BrokerFailureKind.Unavailable, response.Failure);
        Assert.Equal(0, fixture.WorkspaceSourceCalls);
    }

    [Fact]
    public async Task ReleaseDeniedAfterRead_DisclosesNothing()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [9, 9, 9]);
        using var fixture = new Fixture(workspace);
        // Flip authority to deny after the read path's authorization (2nd ReadFile record).
        fixture.FlipAuthorityAfterReadFileRecord(2);

        var response = await fixture.HandleAsync("op-1");

        Assert.False(response.Succeeded);
        Assert.Equal(BrokerFailureKind.Unauthorized, response.Failure);
        Assert.Empty(response.Content.ToArray());
        Assert.Single(fixture.Evidence.Starts);
        Assert.Empty(fixture.Evidence.Releases);
        Assert.Equal(1, fixture.WorkspaceSourceCalls);
    }

    [Fact]
    public async Task MissingFile_IsOperationFailedWithoutContent()
    {
        using var workspace = new TempWorkspace();
        using var fixture = new Fixture(workspace, relativePath: "missing.txt");

        var response = await fixture.HandleAsync("op-1");

        Assert.Equal(BrokerFailureKind.OperationFailed, response.Failure);
        Assert.Empty(response.Content.ToArray());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AuthoritySnapshot _deny;
        public MutableSnapshotSource Source { get; }
        public InMemoryEvidence Evidence { get; } = new();
        public BrokerAuthorityReferenceStore References { get; }
        public BrokerStartArbiter Arbiter { get; } = new();
        public RecordingDecisionRecorder Decisions { get; } = new();
        public BrokerSessionPrincipal Session { get; }
        public BrokerAuthorityReference Reference { get; }
        public int WorkspaceSourceCalls { get; private set; }
        private readonly BrokerReadHandler _handler;

        public Fixture(TempWorkspace workspace, string relativePath = "file.txt")
        {
            var context = new AuthenticatedAuthorityContext("tenant", "subject", "run", "revision", "fence");
            Session = new BrokerSessionPrincipal("session-1", "tenant", "subject", "worker-1", "audience",
                BrokerProtocol.Version, Now, Now.AddHours(1));
            var permit = Snapshot(context, "v1", [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata]);
            _deny = Snapshot(context, "v2", [AuthorityAction.ReadMetadata]);
            Source = new MutableSnapshotSource(permit);
            var authority = new CurrentAuthorityRequestAuthorizer(Source, new SnapshotGrantEvaluator(), Decisions, new Clock(Now));
            References = new BrokerAuthorityReferenceStore(new Clock(Now));
            Reference = References.Register(Session, "delegation-1", "gen-1", "fence-1", context, Workspace,
                relativePath, AuthorityAction.ReadFile, 4096);
            var provider = new LocalWorkspaceProvider(new WorkspaceId(Workspace), workspace.Root, LocalPatchNamespace.HostControlled);
            _handler = new BrokerReadHandler(Arbiter, authority, References, Evidence, _ =>
            {
                WorkspaceSourceCalls++;
                return new BrokerWorkspaceSource(provider, new TestJournal());
            }, new Clock(Now));
        }

        public void FlipAuthorityAfterReadFileRecord(int recordNumber)
        {
            var count = 0;
            Decisions.AfterRecord = entry =>
            {
                if (entry.Request.Action == AuthorityAction.ReadFile && ++count == recordNumber)
                    Source.Current = _deny;
            };
        }

        public ValueTask<BrokerReadResponse> HandleAsync(string operationId, BrokerSessionPrincipal? session = null) =>
            _handler.HandleAsync(new BrokerReadFileRequest(BrokerProtocol.Version, operationId, Reference.Reference,
                Workspace, Reference.RelativePath, 4096, Now.AddSeconds(10)), session ?? Session);

        public void Dispose() => Arbiter.Dispose();

        private static AuthoritySnapshot Snapshot(AuthenticatedAuthorityContext context, string version,
            IReadOnlyList<AuthorityAction> actions) =>
            new(context, version, [new AuthorityLayer("broker",
                [new AuthorityGrant("workspace-grant", actions, new(Workspace, "", AuthorityScopeKind.Subtree), [],
                    Now.AddMinutes(-1), Now.AddHours(1))])], [], Now.AddHours(1));
    }

    private sealed class InMemoryEvidence : IBrokerEvidenceRecorder
    {
        public List<BrokerStartEvidence> Starts { get; } = [];
        public List<BrokerReleaseEvidence> Releases { get; } = [];
        public bool FailStart { get; set; }
        public ValueTask<BrokerEvidenceReceipt?> RecordStartAsync(BrokerStartEvidence evidence, CancellationToken ct = default)
        {
            if (FailStart) return ValueTask.FromResult<BrokerEvidenceReceipt?>(null);
            Starts.Add(evidence);
            return ValueTask.FromResult<BrokerEvidenceReceipt?>(new BrokerEvidenceReceipt("start-" + Starts.Count));
        }
        public ValueTask<BrokerEvidenceReceipt?> RecordReleaseAsync(BrokerReleaseEvidence evidence, CancellationToken ct = default)
        {
            Releases.Add(evidence);
            return ValueTask.FromResult<BrokerEvidenceReceipt?>(new BrokerEvidenceReceipt("release-" + Releases.Count));
        }
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
                snapshot.Version, "broker-tests-grant-evaluator-v1", snapshot.Identity);
        }
    }

    private sealed class RecordingDecisionRecorder : IAuthorityDecisionRecorder
    {
        public Action<DecisionEntry>? AfterRecord { get; set; }
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken ct = default)
        {
            AfterRecord?.Invoke(new DecisionEntry(request, decision));
            return ValueTask.FromResult(true);
        }
    }

    private sealed record DecisionEntry(AuthorityRequest Request, AuthorityDecision Decision);

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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hufu-broker-" + Guid.NewGuid().ToString("N"));
        public TempWorkspace() => Directory.CreateDirectory(Root);
        public string File(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
