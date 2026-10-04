using System.Runtime.InteropServices;
using CedarSharp;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class CedarAuthorityEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "revision", "fence");

    [Fact]
    public void QualifiedCedarMappingAllowsExactWorkspaceRequestAndRetainsTrustedDetails()
    {
        var snapshot = Snapshot([Layer("run", [Grant("source-read", [AuthorityAction.ReadFile], Scope("workspace", "src"))])]);

        var details = new CedarAuthorityEvaluator().EvaluateDetailed(snapshot, Request(AuthorityAction.ReadFile, "workspace", "src/main.cs"), Now);

        Assert.Equal(AuthorityStatus.Permit, details.Decision.Status);
        Assert.Equal("authority.permitted", details.Decision.ReasonCode);
        Assert.Equal(snapshot.Version, details.Decision.SnapshotVersion);
        Assert.Equal(snapshot.Identity, details.Decision.SnapshotIdentity);
        Assert.StartsWith("hufu-cedar-v1:", details.Decision.EvaluatorIdentity);
        Assert.Equal(64, details.SchemaDigest.Length);
        Assert.Equal(64, details.EntityDigest.Length);
        var layer = Assert.Single(details.Layers);
        Assert.True(layer.PolicyValidation!.IsValid);
        Assert.True(layer.RequestValidation!.Scope.IsSuccess);
        Assert.True(layer.RequestValidation.Context.IsSuccess);
        Assert.True(layer.RequestValidation.Entities.IsSuccess);
        Assert.True(layer.Authorization!.IsCleanAllow);

        var native = Assert.IsType<CedarVersion>(details.NativeIdentity);
        Assert.Equal(1u, native.AbiVersion);
        Assert.Equal("4.13.0", native.SdkVersion);
        Assert.Equal("4.5", native.LanguageVersion);
        Assert.Equal("0.1.0", native.BridgeVersion);
        var expectedTarget = (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(),
            OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, false, false, Architecture.X64) => "x86_64-pc-windows-msvc",
            (false, true, false, Architecture.X64) => "x86_64-unknown-linux-gnu",
            (false, false, true, Architecture.Arm64) => "aarch64-apple-darwin",
            _ => throw new PlatformNotSupportedException("The native Cedar test requires a qualified OS/architecture.")
        };
        Assert.Equal(expectedTarget, native.Target);
        Assert.Equal(new[] { "datetime", "decimal", "ipaddr" }, native.Features.Order(StringComparer.Ordinal));
        Assert.Matches("^[0-9A-Fa-f]{64}$", native.Sha256);
    }

    [Fact]
    public void EachAuthorityLayerMustPermitAndAnEmptyLayerDefaultsToDeny()
    {
        var parent = Layer("parent", [Grant("read", [AuthorityAction.ReadFile], Scope("workspace", "src"))]);
        var emptyChild = Layer("activity", []);
        var evaluator = new CedarAuthorityEvaluator();

        var parentOnly = evaluator.EvaluateDetailed(Snapshot([parent]), Request(AuthorityAction.ReadFile, "workspace", "src/a.cs"), Now);
        var layered = evaluator.EvaluateDetailed(Snapshot([parent, emptyChild]), Request(AuthorityAction.ReadFile, "workspace", "src/a.cs"), Now);
        var emptyOnly = evaluator.EvaluateDetailed(Snapshot([emptyChild]), Request(AuthorityAction.ReadFile, "workspace", "src/a.cs"), Now);

        Assert.Equal(AuthorityStatus.Permit, parentOnly.Decision.Status);
        Assert.Equal(AuthorityStatus.Deny, layered.Decision.Status);
        Assert.Equal(AuthorityStatus.Deny, emptyOnly.Decision.Status);
        Assert.Equal(2, layered.Layers.Count);
        Assert.Contains(layered.Layers, item => item.Authorization is { IsCleanAllow: false });
    }

    [Fact]
    public void GrantLocalExclusionDoesNotCancelAnotherGrantButMandatoryForbidDoes()
    {
        var path = "src/private/key.txt";
        var broad = Grant("broad", [AuthorityAction.ReadFile], Scope("workspace", "src"),
            [Scope("workspace", "src/private", AuthorityScopeKind.Subtree)]);
        var specific = Grant("specific", [AuthorityAction.ReadFile], Scope("workspace", path, AuthorityScopeKind.Exact));
        var evaluator = new CedarAuthorityEvaluator();
        var withoutGlobalForbid = Snapshot([Layer("layer", [broad, specific])]);
        var withGlobalForbid = Snapshot([Layer("layer", [broad, specific])], [Scope("workspace", "src/private", AuthorityScopeKind.Subtree)]);

        var permittedBySecondGrant = evaluator.Evaluate(withoutGlobalForbid, Request(AuthorityAction.ReadFile, "workspace", path), Now);
        var mandatoryDenied = evaluator.Evaluate(withGlobalForbid, Request(AuthorityAction.ReadFile, "workspace", path), Now);

        Assert.Equal(AuthorityStatus.Permit, permittedBySecondGrant.Status);
        Assert.Equal(AuthorityStatus.Deny, mandatoryDenied.Status);
    }

    [Fact]
    public void ResourceScopeUsesCanonicalLiteralSegmentsIncludingUnicode()
    {
        var snapshot = Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope("workspace", "src"))])]);
        var evaluator = new CedarAuthorityEvaluator();

        var unicodeChild = evaluator.Evaluate(snapshot, Request(AuthorityAction.ReadFile, "workspace", "src/Éditeur.cs"), Now);
        var prefixSibling = evaluator.Evaluate(snapshot, Request(AuthorityAction.ReadFile, "workspace", "src-other/file.cs"), Now);
        var nonCanonicalCase = evaluator.Evaluate(snapshot, Request(AuthorityAction.ReadFile, "workspace", "src/file.cs") with { RelativePath = "SRC/file.cs" }, Now);

        Assert.Equal(AuthorityStatus.Permit, unicodeChild.Status);
        Assert.Equal(AuthorityStatus.Deny, prefixSibling.Status);
        Assert.Equal("authority.invalid-request", nonCanonicalCase.ReasonCode);
        Assert.Equal("src/Éditeur.cs", AuthorityValidation.NormalizePath("SRC/Éditeur.cs"));
    }

    [Fact]
    public void ExactScopeAndWorkspaceIdentityDoNotBroadenToDescendantsOrOtherWorkspace()
    {
        var exact = Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile],
            Scope("workspace", "src/file.cs", AuthorityScopeKind.Exact))])]);
        var evaluator = new CedarAuthorityEvaluator();

        var exactMatch = evaluator.Evaluate(exact, Request(AuthorityAction.ReadFile, "workspace", "src/file.cs"), Now);
        var descendant = evaluator.Evaluate(exact, Request(AuthorityAction.ReadFile, "workspace", "src/file.cs/child"), Now);
        var otherWorkspace = evaluator.Evaluate(exact, Request(AuthorityAction.ReadFile, "other-workspace", "src/file.cs"), Now);

        Assert.Equal(AuthorityStatus.Permit, exactMatch.Status);
        Assert.Equal(AuthorityStatus.Deny, descendant.Status);
        Assert.Equal(AuthorityStatus.Deny, otherWorkspace.Status);
    }

    [Fact]
    public void AllowWithPolicyEvaluationErrorsIsClassifiedAsDeny()
    {
        var engine = new CedarEngine();
        var policies = CedarPolicySet.FromPolicies(new Dictionary<string, string>
        {
            ["permit"] = "permit(principal, action, resource);",
            ["broken"] = "permit(principal, action, resource) when { principal.missing };"
        });
        var result = engine.Authorize(new CedarAuthorizationRequest(
            new CedarEntityUid("User", "alice"), new CedarEntityUid("Action", "read"),
            new CedarEntityUid("Document", "report"), policies));

        Assert.True(result.IsSuccess);
        Assert.Equal(CedarDecision.Allow, result.Decision);
        Assert.Empty(result.Errors);
        Assert.Single(result.PolicyErrors);
        var classification = CedarAuthorityEvaluator.ClassifyAuthorization(result);
        Assert.Equal(AuthorityStatus.Deny, classification.Status);
        Assert.Equal("cedar.policy-evaluation-error", classification.ReasonCode);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("subject")]
    [InlineData("run")]
    [InlineData("revision")]
    [InlineData("fence")]
    public void ContextBindingRejectsCrossTenantSubjectRunRevisionAndFence(string changedField)
    {
        var snapshot = Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope("workspace", "src"))])]);
        var other = changedField switch
        {
            "tenant" => Context with { TenantId = "tenant-other" },
            "subject" => Context with { SubjectId = "subject-other" },
            "run" => Context with { RunId = "run-other" },
            "revision" => Context with { RevisionId = "revision-other" },
            _ => Context with { FenceId = "fence-other" }
        };

        var decision = new CedarAuthorityEvaluator().Evaluate(snapshot,
            Request(AuthorityAction.ReadFile, "workspace", "src/a.cs", other), Now);

        Assert.Equal(AuthorityStatus.Deny, decision.Status);
        Assert.Equal("authority.invalid-request", decision.ReasonCode);
    }

    [Fact]
    public void SnapshotExpiryAndGrantValidityUseExactUtcBoundaries()
    {
        var evaluator = new CedarAuthorityEvaluator();
        var request = Request(AuthorityAction.ReadFile, "workspace", "src/a.cs");
        var startsAtNow = Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope("workspace", "src"),
            notBefore: Now, expiresAt: Now.AddMinutes(1))])]);
        var endsAtNow = Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope("workspace", "src"),
            notBefore: Now.AddMinutes(-1), expiresAt: Now)])]);
        var expiredSnapshot = Snapshot([Layer("layer", [Grant("read", [AuthorityAction.ReadFile], Scope("workspace", "src"))])],
            validUntil: Now);

        Assert.Equal(AuthorityStatus.Permit, evaluator.Evaluate(startsAtNow, request, Now).Status);
        Assert.Equal(AuthorityStatus.Deny, evaluator.Evaluate(endsAtNow, request, Now).Status);
        Assert.Equal("authority.snapshot-expired", evaluator.Evaluate(expiredSnapshot, request, Now).ReasonCode);
    }

    [Fact]
    public void EveryTypedActionHasAStableCedarMapping()
    {
        var evaluator = new CedarAuthorityEvaluator();
        foreach (var action in Enum.GetValues<AuthorityAction>())
        {
            var snapshot = Snapshot([Layer("layer", [Grant("one-action", [action], Scope("workspace", "src"))])]);
            var decision = evaluator.EvaluateDetailed(snapshot, Request(action, "workspace", "src/file.cs"), Now);
            Assert.Equal(AuthorityStatus.Permit, decision.Decision.Status);
            Assert.True(Assert.Single(decision.Layers).PolicyValidation!.IsValid);
            Assert.True(Assert.Single(decision.Layers).Authorization!.IsCleanAllow);
        }
    }

    private static AuthoritySnapshot Snapshot(IReadOnlyList<AuthorityLayer> layers,
        IReadOnlyList<AuthorityScope>? denials = null, DateTimeOffset? validUntil = null) =>
        new(Context, "snapshot-v1", layers, denials ?? [], validUntil ?? Now.AddHours(1));

    private static AuthorityRequest Request(AuthorityAction action, string workspace, string path,
        AuthenticatedAuthorityContext? context = null) => new(context ?? Context, action, workspace,
            AuthorityValidation.NormalizePath(path), "request-1");

    private static AuthorityLayer Layer(string id, IReadOnlyList<AuthorityGrant> grants) => new(id, grants);

    private static AuthorityScope Scope(string workspace, string path, AuthorityScopeKind kind = AuthorityScopeKind.Subtree) =>
        new(workspace, AuthorityValidation.NormalizePath(path), kind);

    private static AuthorityGrant Grant(string id, IReadOnlyList<AuthorityAction> actions, AuthorityScope scope,
        IReadOnlyList<AuthorityScope>? exclusions = null, DateTimeOffset? notBefore = null, DateTimeOffset? expiresAt = null) =>
        new(id, actions, scope, exclusions ?? [], notBefore ?? Now.AddMinutes(-1), expiresAt ?? Now.AddHours(1));
}
