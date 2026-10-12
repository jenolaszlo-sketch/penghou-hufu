using Penghou.Hufu.Broker;
using Xunit;

namespace Penghou.Hufu.Broker.Tests;

public sealed class BrokerReadRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidRequest_MapsToCanonicalOperation()
    {
        var fixture = new Fixture();
        var mapping = await fixture.Map(Deadline(10), maxBytes: 2048);
        Assert.True(mapping.Succeeded);
        var operation = mapping.Operation!;
        Assert.Equal("op-1", operation.OperationId);
        Assert.Equal(fixture.Context, operation.AuthorityRequest.Context);
        Assert.Equal(AuthorityAction.ReadFile, operation.AuthorityRequest.Action);
        Assert.Equal("workspace", operation.AuthorityRequest.WorkspaceId);
        Assert.Equal("src/a.cs", operation.AuthorityRequest.RelativePath);
        Assert.Equal(64, operation.RequestDigest.Length);
    }

    [Fact]
    public async Task UnknownProtocolVersion_FailsBeforeResolution()
    {
        var fixture = new Fixture();
        Assert.Equal(BrokerFailureKind.InvalidRequest,
            (await fixture.Map(Deadline(10), protocolVersion: BrokerProtocol.Version + 1)).Failure);
    }

    [Fact]
    public async Task PastDeadline_IsDeadlineExceeded()
    {
        var fixture = new Fixture();
        Assert.Equal(BrokerFailureKind.DeadlineExceeded,
            (await fixture.Map(Now.AddSeconds(-1))).Failure);
    }

    [Fact]
    public async Task DeadlineBeyondMaximum_IsInvalid()
    {
        var fixture = new Fixture();
        Assert.Equal(BrokerFailureKind.InvalidRequest,
            (await fixture.Map(Now.Add(BrokerProtocol.MaximumDeadline).AddSeconds(1))).Failure);
    }

    [Fact]
    public async Task UnknownReference_IsUnauthorized()
    {
        var fixture = new Fixture();
        var request = fixture.Request(Deadline(10), reference: "deadbeef", maxBytes: 2048);
        Assert.Equal(BrokerFailureKind.Unauthorized,
            (await BrokerReadRequestMapper.MapAsync(request, fixture.Session, fixture.Store, Now)).Failure);
    }

    [Fact]
    public async Task WrongWorkspace_IsInvalid()
    {
        var fixture = new Fixture();
        Assert.Equal(BrokerFailureKind.InvalidRequest,
            (await fixture.Map(Deadline(10), workspaceId: "other-workspace")).Failure);
    }

    [Fact]
    public async Task WrongRelativePath_IsInvalid()
    {
        // The reference bound `src/a.cs`; a request for a different canonical path
        // must not substitute or reinterpret the target.
        var fixture = new Fixture();
        var request = new BrokerReadFileRequest(BrokerProtocol.Version, "op-1", fixture.Reference.Reference,
            "workspace", "src/b.cs", 2048, Deadline(10));
        Assert.Equal(BrokerFailureKind.InvalidRequest,
            (await BrokerReadRequestMapper.MapAsync(request, fixture.Session, fixture.Store, Now)).Failure);
    }

    [Fact]
    public async Task CanonicalRequest_UsesBoundReferenceTarget()
    {
        var fixture = new Fixture();
        var operation = (await fixture.Map(Deadline(10), maxBytes: 2048)).Operation!;
        Assert.Equal(fixture.Reference.WorkspaceId, operation.AuthorityRequest.WorkspaceId);
        Assert.Equal(fixture.Reference.RelativePath, operation.AuthorityRequest.RelativePath);
        Assert.Equal(fixture.Reference.Action, operation.AuthorityRequest.Action);
        Assert.Equal(fixture.Reference.AuthorityContext, operation.AuthorityRequest.Context);
    }

    [Fact]
    public async Task MaxBytesBeyondReference_IsInvalid()
    {
        var fixture = new Fixture();
        Assert.Equal(BrokerFailureKind.InvalidRequest,
            (await fixture.Map(Deadline(10), maxBytes: 8192)).Failure);
    }

    [Fact]
    public async Task ExpiredSession_IsUnauthorized()
    {
        var fixture = new Fixture();
        var expired = fixture.Session with { ExpiresAt = Now.AddSeconds(-1) };
        var request = fixture.Request(Deadline(10), maxBytes: 2048);
        Assert.Equal(BrokerFailureKind.Unauthorized,
            (await BrokerReadRequestMapper.MapAsync(request, expired, fixture.Store, Now)).Failure);
    }

    [Fact]
    public async Task ReferenceForDifferentAction_IsInvalid()
    {
        var fixture = new Fixture();
        var reference = fixture.Store.Register(fixture.Session, "delegation-1", "gen-1", "fence-1", fixture.Context,
            "workspace", "src/a.cs", AuthorityAction.ListDirectory, 4096);
        var request = fixture.Request(Deadline(10), reference: reference.Reference, maxBytes: 2048);
        Assert.Equal(BrokerFailureKind.InvalidRequest,
            (await BrokerReadRequestMapper.MapAsync(request, fixture.Session, fixture.Store, Now)).Failure);
    }

    [Fact]
    public async Task RevokedReference_IsUnauthorized()
    {
        var fixture = new Fixture();
        await fixture.Store.RevokeAsync(fixture.Reference.Reference);
        Assert.Equal(BrokerFailureKind.Unauthorized, (await fixture.Map(Deadline(10), maxBytes: 2048)).Failure);
    }

    private static DateTimeOffset Deadline(int seconds) => Now.AddSeconds(seconds);

    private sealed class Fixture
    {
        public BrokerSessionPrincipal Session { get; } =
            new("session-1", "tenant", "subject", "worker-1", "audience", BrokerProtocol.Version, Now, Now.AddHours(1));
        public AuthenticatedAuthorityContext Context { get; } = new("tenant", "subject", "run", "rev", "fence");
        public BrokerAuthorityReferenceStore Store { get; } = new(new Clock(Now));
        public BrokerAuthorityReference Reference { get; }

        public Fixture() => Reference = Store.Register(Session, "delegation-1", "gen-1", "fence-1", Context,
            "workspace", "src/a.cs", AuthorityAction.ReadFile, 4096);

        public BrokerReadFileRequest Request(DateTimeOffset deadline, string reference = "", int maxBytes = 2048,
            string workspaceId = "workspace") =>
            new(BrokerProtocol.Version, "op-1", reference.Length == 0 ? Reference.Reference : reference,
                workspaceId, "src/a.cs", maxBytes, deadline);

        public ValueTask<BrokerReadMapping> Map(DateTimeOffset deadline, int maxBytes = 2048,
            string workspaceId = "workspace", int protocolVersion = BrokerProtocol.Version)
        {
            var request = new BrokerReadFileRequest(protocolVersion, "op-1", Reference.Reference, workspaceId,
                "src/a.cs", maxBytes, deadline);
            return BrokerReadRequestMapper.MapAsync(request, Session, Store, Now);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
