using Penghou.Hufu;
using Penghou.Hufu.Biscuit;
using Xunit;

namespace Penghou.Hufu.Biscuit.Tests;

/// <summary>
/// AE-3 reconciliation: one containment definition (different child grant id
/// allowed when core containment permits) and one current-authority path
/// (credential verification gates entry but never authorizes on its own).
/// </summary>
public sealed class BiscuitReconciliationTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Containment_AcceptsDifferentChildGrantId_WhenCorePermits()
    {
        var parent = new AuthorityGrant("parent", [AuthorityAction.ReadFile],
            new("workspace", "src", AuthorityScopeKind.Subtree), [], Start, End);
        // Core derivation mints a distinct child grant id; containment must not
        // require the same id (the old adapter routine did).
        var child = new AuthorityGrant("derived-child", [AuthorityAction.ReadFile],
            new("workspace", "src/service", AuthorityScopeKind.Subtree), [], Start.AddMinutes(1), End.AddMinutes(-1));

        Assert.True(BiscuitProfile.IsSubset(parent, child));
    }

    [Fact]
    public void Containment_StillRejectsWidening()
    {
        var parent = new AuthorityGrant("parent", [AuthorityAction.ReadFile],
            new("workspace", "src", AuthorityScopeKind.Subtree), [], Start, End);
        var widened = new AuthorityGrant("parent", [AuthorityAction.ReadFile, AuthorityAction.WriteFile],
            new("workspace", "src", AuthorityScopeKind.Subtree), [], Start, End);
        Assert.False(BiscuitProfile.IsSubset(parent, widened));
    }

    [Fact]
    public async Task ReconciledComposition_RequiresCurrentAuthorityNotJustSignature()
    {
        using var fixture = new BiscuitIntegrationFixture();
        await fixture.PublishAsync();
        var envelope = await fixture.IssueAsync();
        var request = fixture.Request();
        var authority = new SwitchAuthorizer(AuthorityStatus.Permit);
        var reconciled = new BiscuitQualifiedRequestAuthorizer(fixture.Service, envelope, authority);

        var permitted = await reconciled.AuthorizeAsync(request);
        Assert.Equal(AuthorityStatus.Permit, permitted.Status);
        Assert.True(authority.Called);

        // The signature does not substitute for current authority: the AE-1
        // decision denies and the composed result denies.
        authority.Status = AuthorityStatus.Deny;
        var denied = await reconciled.AuthorizeAsync(request);
        Assert.Equal(AuthorityStatus.Deny, denied.Status);
    }

    [Fact]
    public async Task ReconciledComposition_RejectsTamperedCredentialBeforeAuthority()
    {
        using var fixture = new BiscuitIntegrationFixture();
        await fixture.PublishAsync();
        var envelope = await fixture.IssueAsync();
        var tampered = new BiscuitEnvelope(envelope.RootKeyId,
            envelope.GetTokenBytes().Concat(new byte[] { 0x00 }).ToArray());
        var authority = new SwitchAuthorizer(AuthorityStatus.Permit);
        var reconciled = new BiscuitQualifiedRequestAuthorizer(fixture.Service, tampered, authority);

        var result = await reconciled.AuthorizeAsync(fixture.Request());

        Assert.NotEqual(AuthorityStatus.Permit, result.Status);
        Assert.False(authority.Called);
    }

    private sealed class SwitchAuthorizer(AuthorityStatus status) : IAuthorityRequestAuthorizer
    {
        public AuthorityStatus Status { get; set; } = status;
        public bool Called { get; private set; }
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken ct = default)
        {
            Called = true;
            return ValueTask.FromResult(new AuthorityRequestAuthorization(request, Status));
        }
    }
}
