using Penghou.Hufu.Broker;
using Xunit;

namespace Penghou.Hufu.Broker.Tests;

public sealed class BrokerAuthorityReferenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reference_ResolvesOnlyUnderItsBoundSession()
    {
        var clock = new Clock(Now);
        using var store = new BrokerAuthorityReferenceStore(clock);
        var session = Session("session-1");
        var reference = store.Register(session, "delegation-1", "gen-1", "fence-1", Context(), "workspace", "src/a.cs",
            AuthorityAction.ReadFile, 4096);

        var resolved = await store.ResolveAsync(reference.Reference, session);
        Assert.NotNull(resolved);
        Assert.Equal(reference.Reference, resolved!.Reference);

        var other = Session("session-2");
        Assert.Null(await store.ResolveAsync(reference.Reference, other));
    }

    [Fact]
    public async Task ExpiredReference_DoesNotResolve()
    {
        var clock = new Clock(Now);
        using var store = new BrokerAuthorityReferenceStore(clock);
        var session = Session("session-1");
        var reference = store.Register(session, "delegation-1", "gen-1", "fence-1", Context(), "workspace", "src/a.cs",
            AuthorityAction.ReadFile, 4096, TimeSpan.FromSeconds(1));
        clock.Now = Now.AddSeconds(2);
        Assert.Null(await store.ResolveAsync(reference.Reference, session));
    }

    [Fact]
    public async Task RevokedReference_DoesNotResolve()
    {
        using var store = new BrokerAuthorityReferenceStore(new Clock(Now));
        var session = Session("session-1");
        var reference = store.Register(session, "delegation-1", "gen-1", "fence-1", Context(), "workspace", "src/a.cs",
            AuthorityAction.ReadFile, 4096);
        Assert.True(await store.RevokeAsync(reference.Reference));
        Assert.Null(await store.ResolveAsync(reference.Reference, session));
    }

    [Fact]
    public void ContextTenantMustMatchSession()
    {
        using var store = new BrokerAuthorityReferenceStore(new Clock(Now));
        var session = Session("session-1");
        var mismatched = new AuthenticatedAuthorityContext("other", "subject", "run", "rev", "fence");
        Assert.Throws<ArgumentException>(() => store.Register(session, "delegation-1", "gen-1", "fence-1", mismatched,
            "workspace", "src/a.cs", AuthorityAction.ReadFile, 4096));
    }

    [Fact]
    public void NonCanonicalPath_IsRejected()
    {
        using var store = new BrokerAuthorityReferenceStore(new Clock(Now));
        var session = Session("session-1");
        Assert.Throws<ArgumentException>(() => store.Register(session, "delegation-1", "gen-1", "fence-1", Context(),
            "workspace", "src/A.cs", AuthorityAction.ReadFile, 4096));
    }

    private static BrokerSessionPrincipal Session(string sessionId) =>
        new(sessionId, "tenant", "subject", "worker-1", "audience", BrokerProtocol.Version, Now, Now.AddHours(1));

    private static AuthenticatedAuthorityContext Context() =>
        new("tenant", "subject", "run", "rev", "fence");

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
