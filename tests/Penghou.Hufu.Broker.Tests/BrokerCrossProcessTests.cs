using System.Diagnostics;
using Penghou.Hufu.Broker;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Xunit;

namespace Penghou.Hufu.Broker.Tests;

/// <summary>
/// AE-2.3 cross-process boundary: a separate worker process performs the full
/// broker protocol against a host broker; wrong references, revocation, restart
/// and bootstrap reuse fail closed without provider access.
/// </summary>
public sealed class BrokerCrossProcessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Workspace = "broker-xproc-ws";

    [Fact]
    public async Task SeparateWorkerProcess_ReceivesAuthorizedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var bytes = new byte[] { 11, 22, 33 };
        File.WriteAllBytes(workspace.File("file.txt"), bytes);
        await using var environment = new Host(Now, workspace);

        var (code, output) = await RunWorkerAsync("read", environment.PipeName, environment.Bootstrap.BootstrapSecret,
            environment.Reference.Reference, Workspace, "file.txt", "1024", Deadline(30).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "op-1");

        Assert.Equal(0, code);
        Assert.StartsWith("OK ", output, StringComparison.Ordinal);
        Assert.Single(environment.Evidence.Starts);
        Assert.Single(environment.Evidence.Releases);
    }

    [Fact]
    public async Task SecondWorker_CannotUseFirstWorkersReference()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1, 2, 3]);
        await using var environment = new Host(Now, workspace);
        var other = environment.Sessions.Issue("tenant", "subject", "worker-2", "audience");

        var (code, output) = await RunWorkerAsync("read", environment.PipeName, other.BootstrapSecret,
            environment.Reference.Reference, Workspace, "file.txt", "1024", Deadline(30).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "op-1");

        Assert.NotEqual(0, code);
        Assert.Contains("Unauthorized", output, StringComparison.Ordinal);
        Assert.Equal(0, environment.ProviderOpens);
        Assert.Empty(environment.Evidence.Starts);
    }

    [Fact]
    public async Task RevokedReference_BeforeWorkerRead_IsUnauthorized()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [1, 2, 3]);
        await using var environment = new Host(Now, workspace);
        await environment.References.RevokeAsync(environment.Reference.Reference);

        var (code, output) = await RunWorkerAsync("read", environment.PipeName, environment.Bootstrap.BootstrapSecret,
            environment.Reference.Reference, Workspace, "file.txt", "1024", Deadline(30).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "op-1");

        Assert.NotEqual(0, code);
        Assert.Contains("Unauthorized", output, StringComparison.Ordinal);
        Assert.Equal(0, environment.ProviderOpens);
    }

    [Fact]
    public async Task UnknownBootstrap_IsRejectedAtHandshake()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        await using var environment = new Host(Now, workspace);

        var (code, output) = await RunWorkerAsync("read", environment.PipeName, "not-a-bootstrap",
            environment.Reference.Reference, Workspace, "file.txt", "1024", Deadline(30).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "op-1");

        Assert.NotEqual(0, code);
        Assert.Contains("HandshakeRejected", output, StringComparison.Ordinal);
        Assert.Equal(0, environment.ProviderOpens);
    }

    [Fact]
    public async Task BootstrapReuse_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [4, 5, 6]);
        await using var environment = new Host(Now, workspace);

        var first = await RunWorkerAsync("read", environment.PipeName, environment.Bootstrap.BootstrapSecret,
            environment.Reference.Reference, Workspace, "file.txt", "1024", Deadline(30).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "op-1");
        Assert.Equal(0, first.Code);

        var second = await RunWorkerAsync("read", environment.PipeName, environment.Bootstrap.BootstrapSecret,
            environment.Reference.Reference, Workspace, "file.txt", "1024", Deadline(30).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "op-2");
        Assert.NotEqual(0, second.Code);
        Assert.Contains("HandshakeRejected", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_InvalidatesOldSessionAndReference()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [7, 8]);
        await using var environment = new Host(Now, workspace);
        var oldBootstrap = environment.Bootstrap.BootstrapSecret;
        var oldReference = environment.Reference.Reference;

        // First generation works.
        var first = await RunWorkerAsync("read", environment.PipeName, oldBootstrap, oldReference, Workspace,
            "file.txt", "1024", Deadline(30).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), "op-1");
        Assert.Equal(0, first.Code);

        // Restart: fresh session/reference stores and a fresh server on a new pipe.
        var providerOpens = environment.ProviderOpens;
        await environment.RestartAsync();

        var second = await RunWorkerAsync("read", environment.PipeName, oldBootstrap, oldReference, Workspace,
            "file.txt", "1024", Deadline(30).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), "op-2");
        Assert.NotEqual(0, second.Code);
        Assert.DoesNotContain("OK ", second.Output, StringComparison.Ordinal);
        Assert.Equal(providerOpens, environment.ProviderOpens);
    }

    [Fact]
    public async Task IdenticalRetry_ReauthorizesWithFreshEvidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(workspace.File("file.txt"), [9]);
        await using var environment = new Host(Now, workspace);

        // Note: the same bootstrap is single-use, so the retry needs its own session
        // on this one-connection-per-worker fixture. Re-issue and re-register instead.
        var first = await RunWorkerAsync("read", environment.PipeName, environment.Bootstrap.BootstrapSecret,
            environment.Reference.Reference, Workspace, "file.txt", "1024", Deadline(30).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "op-1");
        Assert.Equal(0, first.Code);
        Assert.Single(environment.Evidence.Starts);
        Assert.Single(environment.Evidence.Releases);

        environment.Reissue();
        var second = await RunWorkerAsync("read", environment.PipeName, environment.Bootstrap.BootstrapSecret,
            environment.Reference.Reference, Workspace, "file.txt", "1024", Deadline(30).UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture), "op-1");
        Assert.Equal(0, second.Code);
        Assert.Equal(2, environment.Evidence.Starts.Count);
        Assert.Equal(2, environment.Evidence.Releases.Count);
    }

    private static DateTimeOffset Deadline(int seconds) => Now.AddSeconds(seconds);

    private static async Task<(int Code, string Output)> RunWorkerAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(HarnessPath(), string.Join(" ", arguments.Select(Quote)))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("worker did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return (process.ExitCode, output.Trim());
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string HarnessPath()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targetFramework = Path.GetFileName(baseDirectory);
        var configuration = Path.GetFileName(Path.GetDirectoryName(baseDirectory)!);
        var testsDirectory = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", ".."));
        var fileName = "Penghou.Hufu.BrokerWorkerHarness" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        return Path.Combine(testsDirectory, "Penghou.Hufu.BrokerWorkerHarness", "bin", configuration, targetFramework, fileName);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly DateTimeOffset _now;
        private readonly MutableSnapshotSource _source;
        private readonly IAuthorityRequestAuthorizer _authority;
        private readonly IWorkspaceProvider _provider;
        private BrokerPipeServer? _server;

        public string PipeName { get; }
        public BrokerSessionRegistry Sessions { get; private set; } = null!;
        public BrokerAuthorityReferenceStore References { get; private set; } = null!;
        public BrokerStartArbiter Arbiter { get; } = new();
        public InMemoryEvidence Evidence { get; } = new();
        public BrokerSessionBootstrap Bootstrap { get; private set; } = null!;
        public BrokerAuthorityReference Reference { get; private set; } = null!;
        public int ProviderOpens { get; private set; }

        public Host(DateTimeOffset now, TempWorkspace workspace)
        {
            _now = now;
            PipeName = "hufu-broker-xproc-" + Guid.NewGuid().ToString("N");
            var context = new AuthenticatedAuthorityContext("tenant", "subject", "run", "revision", "fence");
            var snapshot = new AuthoritySnapshot(context, "v1", [new AuthorityLayer("broker",
                [new AuthorityGrant("workspace-grant", [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata],
                    new(Workspace, "", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddHours(1))])], [], now.AddHours(1));
            _source = new MutableSnapshotSource(snapshot);
            _authority = new CurrentAuthorityRequestAuthorizer(_source, new SnapshotGrantEvaluator(), new AllowRecorder(), new Clock(now));
            _provider = new LocalWorkspaceProvider(new WorkspaceId(Workspace), workspace.Root, LocalPatchNamespace.HostControlled);
            RebuildBroker();
        }

        public void Reissue()
        {
            var bootstrap = Sessions.Issue("tenant", "subject", "worker-1", "audience");
            Bootstrap = bootstrap;
            var session = new BrokerSessionPrincipal(bootstrap.SessionId, "tenant", "subject", "worker-1", "audience",
                BrokerProtocol.Version, _now, _now.AddHours(1));
            Reference = References.Register(session, "delegation-1", "gen-1", "fence-1", Reference.AuthorityContext,
                Workspace, "file.txt", AuthorityAction.ReadFile, 4096);
        }

        public async ValueTask RestartAsync()
        {
            if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
            Sessions.Dispose();
            References.Dispose();
            RebuildBroker();
        }

        private void RebuildBroker()
        {
            Sessions = new BrokerSessionRegistry(new Clock(_now));
            References = new BrokerAuthorityReferenceStore(new Clock(_now));
            var context = new AuthenticatedAuthorityContext("tenant", "subject", "run", "revision", "fence");
            var bootstrap = Sessions.Issue("tenant", "subject", "worker-1", "audience");
            Bootstrap = bootstrap;
            var session = new BrokerSessionPrincipal(bootstrap.SessionId, "tenant", "subject", "worker-1", "audience",
                BrokerProtocol.Version, _now, _now.AddHours(1));
            Reference = References.Register(session, "delegation-1", "gen-1", "fence-1", context, Workspace,
                "file.txt", AuthorityAction.ReadFile, 4096);
            var handler = new BrokerReadHandler(Arbiter, _authority, References, Evidence, _ =>
            {
                ProviderOpens++;
                return new BrokerWorkspaceSource(_provider, new TestJournal());
            }, new Clock(_now));
            _server = new BrokerPipeServer(PipeName, Sessions, handler);
            _server.Start();
        }

        public async ValueTask DisposeAsync()
        {
            if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
            Sessions.Dispose();
            References.Dispose();
            Arbiter.Dispose();
        }
    }

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
                "broker-xproc-v1", snapshot.Identity);
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
            return ValueTask.FromResult<BrokerEvidenceReceipt?>(new BrokerEvidenceReceipt("start-" + Starts.Count));
        }
        public ValueTask<BrokerEvidenceReceipt?> RecordReleaseAsync(BrokerReleaseEvidence evidence, CancellationToken ct = default)
        {
            Releases.Add(evidence);
            return ValueTask.FromResult<BrokerEvidenceReceipt?>(new BrokerEvidenceReceipt("release-" + Releases.Count));
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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hufu-broker-xproc-" + Guid.NewGuid().ToString("N"));
        public TempWorkspace() => Directory.CreateDirectory(Root);
        public string File(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
