using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Penghou.Hufu.Broker;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Xunit;

namespace Penghou.Hufu.Broker.Tests;

/// <summary>
/// AE-2.3 race and malformed/adversarial cases: the start arbiter orders a
/// participating mutation against admission, and hostile read-path traffic is
/// rejected before any provider access.
/// </summary>
public sealed class BrokerQualificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Workspace = "broker-qual-ws";

    [Fact]
    public async Task MutationHeld_BlocksHandlerStartUntilReleased()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1, 2, 3]);
        using var fixture = new Fixture(workspace);
        var binding = BrokerAuthorityBinding.From(fixture.Reference);
        var mutationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var mutation = fixture.Arbiter.RunMutationAsync(binding, async _ =>
        {
            mutationEntered.SetResult();
            await releaseMutation.Task;
        });
        await mutationEntered.Task;

        var handle = fixture.HandleAsync("op-1").AsTask();
        await Task.Delay(50);
        Assert.False(handle.IsCompleted);

        // A revocation commit inside the ordering domain wins first: the later
        // start attempt must observe it and deny with no provider access.
        fixture.RevokeAuthority();
        releaseMutation.SetResult();
        await mutation;

        var response = await handle;
        Assert.Equal(BrokerFailureKind.Unauthorized, response.Failure);
        Assert.Equal(0, fixture.WorkspaceSourceCalls);
    }

    [Fact]
    public async Task UnknownProtocolVersionFrame_IsInvalidRequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1]);
        using var fixture = new Fixture(workspace);
        await using var stream = await RawHandshakeAsync(fixture);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ProtocolVersion = BrokerProtocol.Version + 1,
            OperationId = "op",
            AuthorityReference = fixture.Reference.Reference,
            WorkspaceId = Workspace,
            RelativePath = "file.txt",
            MaxBytes = 128,
            Deadline = Now.AddSeconds(10)
        });
        await WriteFrameAsync(stream, payload);
        var response = await ReadFrameAsync(stream);

        Assert.NotNull(response);
        Assert.Equal((int)BrokerFailureKind.InvalidRequest, ReadFailure(response!));
        Assert.Equal(0, fixture.WorkspaceSourceCalls);
    }

    [Fact]
    public async Task UnknownFieldFrame_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1]);
        using var fixture = new Fixture(workspace);
        await using var stream = await RawHandshakeAsync(fixture);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ProtocolVersion = BrokerProtocol.Version,
            OperationId = "op",
            AuthorityReference = fixture.Reference.Reference,
            WorkspaceId = Workspace,
            RelativePath = "file.txt",
            MaxBytes = 128,
            Deadline = Now.AddSeconds(10),
            Unexpected = "adversarial"
        });
        await WriteFrameAsync(stream, payload);
        var response = await ReadFrameAsync(stream);

        Assert.NotNull(response);
        Assert.Equal((int)BrokerFailureKind.InvalidRequest, ReadFailure(response!));
        Assert.Equal(0, fixture.WorkspaceSourceCalls);
    }

    [Fact]
    public async Task OversizedFrame_ClosesConnectionWithoutProviderAccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1]);
        using var fixture = new Fixture(workspace);
        await using var stream = await RawHandshakeAsync(fixture);

        // A length prefix beyond the frozen bound must be rejected by the server
        // before any authority resolution or provider access.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, BrokerProtocol.MaximumRequestBodyBytes + 1);
        await stream.WriteAsync(header);
        await stream.FlushAsync();
        var closed = await stream.ReadAsync(new byte[4]);

        Assert.Equal(0, closed);
        Assert.Equal(0, fixture.WorkspaceSourceCalls);
    }

    private static async Task<NamedPipeClientStream> RawHandshakeAsync(Fixture fixture)
    {
        var stream = new NamedPipeClientStream(".", fixture.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync();
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new { Bootstrap = fixture.Bootstrap.BootstrapSecret }));
        var result = await ReadFrameAsync(stream);
        Assert.NotNull(result);
        return stream;
    }

    private static int ReadFailure(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("Failure").GetInt32();
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var header = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var count = await stream.ReadAsync(header.AsMemory(read, 4 - read), timeout.Token);
            if (count == 0) return null;
            read += count;
        }
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > BrokerProtocol.MaximumRequestBodyBytes) return null;
        var payload = new byte[length];
        read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(payload.AsMemory(read, length - read), timeout.Token);
            if (count == 0) return null;
            read += count;
        }
        return payload;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AuthoritySnapshot _deny;
        public MutableSnapshotSource Source { get; }
        public BrokerSessionRegistry Sessions { get; } = new(new Clock(Now));
        public BrokerAuthorityReferenceStore References { get; } = new(new Clock(Now));
        public BrokerStartArbiter Arbiter { get; } = new();
        public InMemoryEvidence Evidence { get; } = new();
        public BrokerSessionBootstrap Bootstrap { get; }
        public BrokerAuthorityReference Reference { get; }
        public string PipeName { get; } = "hufu-broker-qual-" + Guid.NewGuid().ToString("N");
        public int WorkspaceSourceCalls { get; private set; }
        private readonly BrokerPipeServer _server;
        private readonly BrokerReadHandler _handler;
        private readonly BrokerSessionPrincipal _session;

        public Fixture(TempWorkspace workspace)
        {
            var context = new AuthenticatedAuthorityContext("tenant", "subject", "run", "revision", "fence");
            Bootstrap = Sessions.Issue("tenant", "subject", "worker-1", "audience");
            _session = new BrokerSessionPrincipal(Bootstrap.SessionId, "tenant", "subject", "worker-1", "audience",
                BrokerProtocol.Version, Now, Now.AddHours(1));
            Reference = References.Register(_session, "delegation-1", "gen-1", "fence-1", context, Workspace,
                "file.txt", AuthorityAction.ReadFile, 4096);
            var permit = Snapshot(context, "v1", [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata]);
            _deny = Snapshot(context, "v2", [AuthorityAction.ReadMetadata]);
            Source = new MutableSnapshotSource(permit);
            var authority = new CurrentAuthorityRequestAuthorizer(Source, new GrantEvaluator(), new AllowRecorder(), new Clock(Now));
            var provider = new LocalWorkspaceProvider(new WorkspaceId(Workspace), workspace.Root, LocalPatchNamespace.HostControlled);
            _handler = new BrokerReadHandler(Arbiter, authority, References, Evidence, _ =>
            {
                WorkspaceSourceCalls++;
                return new BrokerWorkspaceSource(provider, new TestJournal());
            }, new Clock(Now));
            _server = new BrokerPipeServer(PipeName, Sessions, _handler);
            _server.Start();
        }

        public void RevokeAuthority() => Source.Current = _deny;

        public ValueTask<BrokerReadResponse> HandleAsync(string operationId) =>
            _handler.HandleAsync(new BrokerReadFileRequest(BrokerProtocol.Version, operationId, Reference.Reference,
                Workspace, "file.txt", 4096, Now.AddSeconds(10)), _session);

        public void Dispose()
        {
            _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Sessions.Dispose();
            References.Dispose();
            Arbiter.Dispose();
        }

        private static AuthoritySnapshot Snapshot(AuthenticatedAuthorityContext context, string version,
            IReadOnlyList<AuthorityAction> actions) =>
            new(context, version, [new AuthorityLayer("broker",
                [new AuthorityGrant("workspace-grant", actions, new(Workspace, "", AuthorityScopeKind.Subtree), [],
                    Now.AddMinutes(-1), Now.AddHours(1))])], [], Now.AddHours(1));
    }

    private sealed class MutableSnapshotSource(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        public AuthoritySnapshot Current { get; set; } = snapshot;
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken ct = default) =>
            ValueTask.FromResult<AuthoritySnapshot?>(Current);
    }

    private sealed class GrantEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
        {
            var target = new AuthorityScope(request.WorkspaceId, request.RelativePath, AuthorityScopeKind.Exact);
            var covered = snapshot.Layers.All(layer => layer.Grants.Any(grant =>
                grant.NotBefore <= now && now < grant.ExpiresAt && grant.Actions.Contains(request.Action) &&
                AuthorityValidation.Contains(grant.Scope, target)));
            var status = covered ? AuthorityStatus.Permit : AuthorityStatus.Deny;
            return new(status, covered ? "test.granted" : "test.denied", snapshot.Version, "broker-qual-v1", snapshot.Identity);
        }
    }

    private sealed class AllowRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken ct = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class InMemoryEvidence : IBrokerEvidenceRecorder
    {
        public ValueTask<BrokerEvidenceReceipt?> RecordStartAsync(BrokerStartEvidence evidence, CancellationToken ct = default) =>
            ValueTask.FromResult<BrokerEvidenceReceipt?>(new BrokerEvidenceReceipt("start"));
        public ValueTask<BrokerEvidenceReceipt?> RecordReleaseAsync(BrokerReleaseEvidence evidence, CancellationToken ct = default) =>
            ValueTask.FromResult<BrokerEvidenceReceipt?>(new BrokerEvidenceReceipt("release"));
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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hufu-broker-qual-" + Guid.NewGuid().ToString("N"));
        public TempWorkspace() => Directory.CreateDirectory(Root);
        public string File(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
