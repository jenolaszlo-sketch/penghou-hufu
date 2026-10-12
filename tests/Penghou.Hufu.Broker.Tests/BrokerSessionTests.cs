using Penghou.Hufu.Broker;
using Xunit;

namespace Penghou.Hufu.Broker.Tests;

public sealed class BrokerSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Connection = "connection-1";

    [Fact]
    public async Task IssueThenAuthenticate_EstablishesBoundSession()
    {
        using var registry = new BrokerSessionRegistry(new Clock(Now));
        var bootstrap = registry.Issue("tenant", "subject", "worker-1", "audience", TimeSpan.FromSeconds(30));

        var session = await registry.AuthenticateAsync(bootstrap.BootstrapSecret, Connection, BrokerProtocol.Version);

        Assert.NotNull(session);
        Assert.Equal("tenant", session!.TenantId);
        Assert.Equal("subject", session.SubjectId);
        Assert.Equal("worker-1", session.WorkerIdentity);
        Assert.Equal("audience", session.Audience);
        Assert.Equal(BrokerProtocol.Version, session.ProtocolVersion);
        Assert.True(registry.IsActive(session));
    }

    [Fact]
    public async Task BootstrapIsSingleUse()
    {
        using var registry = new BrokerSessionRegistry(new Clock(Now));
        var bootstrap = registry.Issue("tenant", "subject", "worker-1", "audience");
        Assert.NotNull(await registry.AuthenticateAsync(bootstrap.BootstrapSecret, Connection, BrokerProtocol.Version));
        Assert.Null(await registry.AuthenticateAsync(bootstrap.BootstrapSecret, Connection, BrokerProtocol.Version));
    }

    [Fact]
    public async Task WrongProtocolVersion_IsRejectedWithoutConsumingBootstrap()
    {
        using var registry = new BrokerSessionRegistry(new Clock(Now));
        var bootstrap = registry.Issue("tenant", "subject", "worker-1", "audience");
        Assert.Null(await registry.AuthenticateAsync(bootstrap.BootstrapSecret, Connection, BrokerProtocol.Version + 1));
        Assert.NotNull(await registry.AuthenticateAsync(bootstrap.BootstrapSecret, Connection, BrokerProtocol.Version));
    }

    [Fact]
    public async Task ExpiredBootstrap_IsRejected()
    {
        var clock = new Clock(Now);
        using var registry = new BrokerSessionRegistry(clock);
        var bootstrap = registry.Issue("tenant", "subject", "worker-1", "audience", TimeSpan.FromSeconds(1));
        clock.Now = Now.AddSeconds(2);
        Assert.Null(await registry.AuthenticateAsync(bootstrap.BootstrapSecret, Connection, BrokerProtocol.Version));
    }

    [Fact]
    public async Task SessionCapacityPerWorker_IsBounded()
    {
        using var registry = new BrokerSessionRegistry(new Clock(Now), maximumSessionsPerWorker: 1);
        var first = registry.Issue("tenant", "subject", "worker-1", "audience");
        var second = registry.Issue("tenant", "subject", "worker-1", "audience");
        Assert.NotNull(await registry.AuthenticateAsync(first.BootstrapSecret, Connection, BrokerProtocol.Version));
        Assert.Null(await registry.AuthenticateAsync(second.BootstrapSecret, Connection, BrokerProtocol.Version));
    }

    [Fact]
    public async Task RevokedSession_IsNotActive()
    {
        using var registry = new BrokerSessionRegistry(new Clock(Now));
        var bootstrap = registry.Issue("tenant", "subject", "worker-1", "audience");
        var session = (await registry.AuthenticateAsync(bootstrap.BootstrapSecret, Connection, BrokerProtocol.Version))!;
        Assert.True(registry.Revoke(session.SessionId));
        Assert.False(registry.IsActive(session));
    }

    [Fact]
    public async Task UnknownConnectionIdentity_IsRejected()
    {
        using var registry = new BrokerSessionRegistry(new Clock(Now));
        var bootstrap = registry.Issue("tenant", "subject", "worker-1", "audience");
        Assert.Null(await registry.AuthenticateAsync(bootstrap.BootstrapSecret, " ", BrokerProtocol.Version));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
