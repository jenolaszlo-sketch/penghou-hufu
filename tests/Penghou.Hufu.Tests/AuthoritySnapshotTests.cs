using Penghou.Hufu;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class AuthoritySnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");
    private static readonly AuthorityScope Scope = new("workspace", "src", AuthorityScopeKind.Subtree);

    [Fact]
    public void DefaultAuthorityStatusCannotGrantPermission() =>
        Assert.Equal(AuthorityStatus.Unavailable, default(AuthorityStatus));

    [Fact]
    public void SnapshotFreezesEveryCallerOwnedCollection()
    {
        var actions = new List<AuthorityAction> { AuthorityAction.ReadFile };
        var exclusions = new List<AuthorityScope>
        {
            new("workspace", "src/private", AuthorityScopeKind.Subtree)
        };
        var grants = new List<AuthorityGrant>
        {
            Grant("read", actions, Scope, exclusions)
        };
        var layers = new List<AuthorityLayer> { new("run-layer", grants) };
        var denials = new List<AuthorityScope>
        {
            new("workspace", "secrets", AuthorityScopeKind.Subtree)
        };
        var snapshot = new AuthoritySnapshot(Context, "version-1", layers, denials, Now.AddHours(1));
        var identity = snapshot.Identity;

        actions[0] = AuthorityAction.PatchFile;
        exclusions[0] = new AuthorityScope("workspace", "src/public", AuthorityScopeKind.Subtree);
        grants[0] = Grant("replacement", [AuthorityAction.Release], new("other-workspace", "", AuthorityScopeKind.Subtree));
        grants.Clear();
        layers[0] = new AuthorityLayer("replacement-layer", []);
        layers.Clear();
        denials.Clear();

        Assert.Equal(identity, snapshot.Identity);
        var grant = Assert.Single(Assert.Single(snapshot.Layers).Grants);
        Assert.Equal(AuthorityAction.ReadFile, Assert.Single(grant.Actions));
        Assert.Equal("src/private", Assert.Single(grant.Exclusions).RelativePath);
        Assert.Equal("secrets", Assert.Single(snapshot.MandatoryDenials).RelativePath);
        Assert.True(Assert.IsAssignableFrom<IList<AuthorityLayer>>(snapshot.Layers).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<IList<AuthorityAction>>(grant.Actions).IsReadOnly);
    }

    [Fact]
    public void SnapshotIdentityChangesWhenAnyAuthorityFactChanges()
    {
        var baseline = Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope)])]);
        var differentScope = Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile],
            new("workspace", "src/docs", AuthorityScopeKind.Subtree))])]);
        var differentExpiry = new AuthoritySnapshot(Context, "version-1",
            [Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope)])], [], Now.AddMinutes(59));
        var differentDenial = new AuthoritySnapshot(Context, "version-1",
            [Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope)])],
            [new("workspace", "src/private", AuthorityScopeKind.Subtree)], Now.AddHours(1));

        Assert.NotEqual(baseline.Identity, differentScope.Identity);
        Assert.NotEqual(baseline.Identity, differentExpiry.Identity);
        Assert.NotEqual(baseline.Identity, differentDenial.Identity);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("src/../secret")]
    [InlineData("src//file.txt")]
    [InlineData("C:/secret")]
    [InlineData("/absolute")]
    [InlineData("src/file.txt ")]
    public void SnapshotRejectsTraversalAndNonCanonicalScopePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => new AuthoritySnapshot(Context, "version-1",
            [Layer("layer", [Grant("read", [AuthorityAction.ReadFile],
                new("workspace", path, AuthorityScopeKind.Subtree))])], [], Now.AddHours(1)));
    }

    [Fact]
    public void SnapshotRejectsExclusionOutsideItsGrantScope()
    {
        Assert.Throws<ArgumentException>(() => Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope,
            [new AuthorityScope("workspace", "other", AuthorityScopeKind.Subtree)])])]));
    }

    [Fact]
    public void SnapshotRejectsInvalidContextAndInvalidUtf16Tokens()
    {
        Assert.Throws<ArgumentException>(() => new AuthoritySnapshot(Context with { TenantId = "tenant-\ud800" },
            "version-1", [Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope)])], [], Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new AuthoritySnapshot(Context, "version-\ud800",
            [Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope)])], [], Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new AuthoritySnapshot(Context with { SubjectId = new string('x', 257) },
            "version-1", [Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope)])], [], Now.AddHours(1)));
    }

    [Fact]
    public void SnapshotRejectsInvalidLayerGrantActionAndTimeBounds()
    {
        Assert.Throws<ArgumentException>(() => new AuthoritySnapshot(Context, "version-1", [], [], Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new AuthoritySnapshot(Context, "version-1",
            Enumerable.Range(0, 9).Select(i => Layer("layer-" + i, [])), [], Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => Snapshot([Layer("layer", [Grant("bad", [], Scope)])]));
        Assert.Throws<ArgumentException>(() => Snapshot([Layer("layer", [Grant("bad", [(AuthorityAction)999], Scope)])]));
        Assert.Throws<ArgumentException>(() => Snapshot([Layer("layer", [Grant("bad", [AuthorityAction.ReadFile,
            AuthorityAction.ReadFile], Scope)])]));
        Assert.Throws<ArgumentException>(() => Snapshot([Layer("layer", [Grant("bad", [AuthorityAction.ReadFile], Scope,
            notBefore: Now.AddMinutes(1), expiresAt: Now)])]));
    }

    [Fact]
    public void RequestValidationRequiresCanonicalBoundedIdentityAndClosedAction()
    {
        var valid = new AuthorityRequest(Context, AuthorityAction.ReadFile, "workspace", "src/file.txt", "request-1");
        Assert.True(AuthorityValidation.IsValidRequest(valid));
        Assert.False(AuthorityValidation.IsValidRequest(valid with { RelativePath = "Src/file.txt" }));
        Assert.False(AuthorityValidation.IsValidRequest(valid with { RelativePath = "src/../secret" }));
        Assert.False(AuthorityValidation.IsValidRequest(valid with { Action = (AuthorityAction)999 }));
        Assert.False(AuthorityValidation.IsValidRequest(valid with { RequestIdentity = "bad-\ud800" }));
        Assert.False(AuthorityValidation.IsValidRequest(valid with { Context = Context with { RunId = "" } }));
    }

    [Fact]
    public void PathContainmentUsesSegmentBoundariesAndAsciiCaseNormalization()
    {
        var parent = new AuthorityScope("workspace", AuthorityValidation.NormalizePath("Src/Tools"), AuthorityScopeKind.Subtree);
        var child = new AuthorityScope("workspace", AuthorityValidation.NormalizePath("src/tools/Éditeur.cs"), AuthorityScopeKind.Exact);
        var sibling = new AuthorityScope("workspace", AuthorityValidation.NormalizePath("src/toolsmith/file.cs"), AuthorityScopeKind.Exact);
        var otherWorkspace = new AuthorityScope("other", child.RelativePath, AuthorityScopeKind.Exact);

        Assert.True(AuthorityValidation.Contains(parent, child));
        Assert.False(AuthorityValidation.Contains(parent, sibling));
        Assert.False(AuthorityValidation.Contains(parent, otherWorkspace));
        Assert.Equal("src/tools/Éditeur.cs", child.RelativePath); // Only ASCII A-Z is folded.
    }

    private static AuthoritySnapshot Snapshot(IReadOnlyList<AuthorityLayer> layers) =>
        new(Context, "version-1", layers, [], Now.AddHours(1));

    private static AuthorityLayer Layer(string id, IReadOnlyList<AuthorityGrant> grants) => new(id, grants);

    private static AuthorityGrant Grant(string id, IReadOnlyList<AuthorityAction> actions, AuthorityScope scope,
        IReadOnlyList<AuthorityScope>? exclusions = null, DateTimeOffset? notBefore = null, DateTimeOffset? expiresAt = null) =>
        new(id, actions, scope, exclusions ?? [], notBefore ?? Now.AddMinutes(-1), expiresAt ?? Now.AddHours(1));
}
