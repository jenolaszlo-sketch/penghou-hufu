using Penghou.Hufu.Luban;
using Penghou.Hufu.Biscuit.Sqlite;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban;
using Penghou.Luban.Language;
using static Penghou.Hufu.Biscuit.Tests.BiscuitIntegrationFixture;

namespace Penghou.Hufu.Biscuit.Tests;

public sealed class BiscuitLubanReadTests
{
    [Fact]
    public async Task RealReadRequiresCurrentBiscuitCedarMetadataAndReleaseEvidence()
    {
        using var host=await ReadHost.CreateAsync("read src/public.txt");
        var result=await host.RunAsync();
        Assert.Equal(LanguageRunStatus.Succeeded,result.Status);
        Assert.Equal("public needle",Assert.IsType<FileContentValue>(Assert.Single(Assert.Single(result.Statements!).Values)).Content);
        Assert.Contains(host.Authorizer.Calls,c=>c.Request.Phase==LanguageAuthorizationPhase.ResourceAccess &&
            c.Request.Action==ResourceAction.ReadFile && c.Decision.Status==LanguageAuthorityStatus.Permit);
        Assert.Contains(host.Authorizer.Calls,c=>c.Request.Phase==LanguageAuthorizationPhase.ResourceAccess &&
            c.Request.Action==ResourceAction.ReadMetadata && c.Request.ResourcePath=="");
        Assert.Contains(host.Authorizer.Calls,c=>c.Request.Phase==LanguageAuthorizationPhase.Release);
        Assert.True(host.Evidence.Count>5);
        foreach(var evidence in host.Evidence)
        {
            var recorded=await host.Fixture.Store.ReadDecisionAsync(Actor,AuthoritySubject.From(Context),evidence);
            Assert.Equal(AuthorityReadStatus.Active,recorded.Status);
            Assert.Contains("Penghou.IO.Local.Windows.Read.v1",recorded.Entry!.Record.EvidenceJson);
            Assert.DoesNotContain("public needle",recorded.Entry.Record.EvidenceJson);
        }
    }

    [Theory]
    [InlineData("find src **/*.txt")]
    [InlineData("search needle src --include **/*.txt")]
    public async Task RootPermitCannotDiscloseExcludedChildPathOrContent(string script)
    {
        using var host=await ReadHost.CreateAsync(script);
        var result=await host.RunAsync();
        Assert.Equal(LanguageRunStatus.Succeeded,result.Status);
        var values=result.Statements!.SelectMany(s=>s.Values).ToArray();
        Assert.NotEmpty(values);
        Assert.DoesNotContain(values,v=>v.ToString()!.Contains("private",StringComparison.OrdinalIgnoreCase) ||
            v.ToString()!.Contains("secret",StringComparison.OrdinalIgnoreCase));
        Assert.Contains(host.Authorizer.Calls,c=>c.Request.Action==ResourceAction.ReadMetadata &&
            c.Request.ResourcePath=="src/private" && c.Decision.Status==LanguageAuthorityStatus.Deny);
        Assert.DoesNotContain(host.Authorizer.Calls,c=>c.Request.ResourcePath is string path &&
            path.StartsWith("src/private/",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("read src/private/secret.txt")]
    [InlineData("read src/public.txt\nread src/private/secret.txt")]
    public async Task KnownDeniedTargetBlocksWholeDocumentBeforeResourceAccess(string script)
    {
        using var host=await ReadHost.CreateAsync(script);
        var result=await host.RunAsync();
        Assert.Equal(LanguageRunStatus.AuthorityDenied,result.Status);
        Assert.Null(result.Statements);
        Assert.DoesNotContain(host.Authorizer.Calls,c=>c.Request.Phase==LanguageAuthorizationPhase.ResourceAccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialRevokedAtActualReadOrFinalReleaseCannotDiscloseContent(bool release)
    {
        using var host=await ReadHost.CreateAsync("read src/public.txt");
        bool revoked=false;
        host.Authorizer.Before=async request=>{
            if(!revoked && (release ? request.Phase==LanguageAuthorizationPhase.Release :
                request.Phase==LanguageAuthorizationPhase.ResourceAccess && request.Action==ResourceAction.ReadFile))
            {
                revoked=true;
                Assert.Equal(BiscuitRegistryStatus.Recorded,await host.Fixture.Registry.RevokeAsync(
                    Actor,Realm,Context,Fingerprint(host.Envelope),"revoked"));
            }
        };
        var result=await host.RunAsync();
        Assert.True(revoked);
        Assert.Equal(LanguageRunStatus.AuthorityDenied,result.Status);
        Assert.Null(result.Statements);
        if(release) Assert.Contains(host.Authorizer.Calls,c=>c.Request.Action==ResourceAction.ReadFile &&
            c.Decision.Status==LanguageAuthorityStatus.Permit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredEvidenceFailureAtReadOrReleaseBlocksPublicResults(bool release)
    {
        using var host=await ReadHost.CreateAsync("read src/public.txt");
        host.Authorizer.Before=request=>{
            if(release ? request.Phase==LanguageAuthorizationPhase.Release :
                request.Phase==LanguageAuthorizationPhase.ResourceAccess && request.Action==ResourceAction.ReadFile)
                host.Fixture.FailCoreEvidence=true;
            return Task.CompletedTask;
        };
        var result=await host.RunAsync();
        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable,result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task CurrentMandatoryDenialAddedAfterPreflightBlocksConcreteRead()
    {
        using var host=await ReadHost.CreateAsync("read src/public.txt");
        bool published=false;
        host.Authorizer.Before=async request=>{
            if(!published && request.Phase==LanguageAuthorizationPhase.ResourceAccess && request.Action==ResourceAction.ReadFile)
            {
                published=true;
                await host.Fixture.PublishAsync(host.Fixture.CreateSnapshot("v2",host.Fixture.Snapshot.Layers,
                    [new("workspace","src/public.txt",AuthorityScopeKind.Exact)]));
            }
        };
        var result=await host.RunAsync();
        Assert.Equal(LanguageRunStatus.AuthorityDenied,result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task UnicodeCaseAliasCannotBypassExactExclusionThroughRealWindowsProvider()
    {
        using var host=await ReadHost.CreateAsync("read src/é.txt",
            [new("workspace","src/private",AuthorityScopeKind.Subtree),new("workspace","src/É.txt",AuthorityScopeKind.Exact)]);
        File.WriteAllText(Path.Combine(host.Root,"src","É.txt"),"excluded unicode secret");
        Assert.True(File.Exists(Path.Combine(host.Root,"src","é.txt")));
        var result=await host.RunAsync();
        Assert.Equal(LanguageRunStatus.AccessDenied,result.Status);
        Assert.Null(result.Statements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JunctionAndApprovalToQualificationSubstitutionCannotReadOutsideWorkspace(bool substituteAfterPermit)
    {
        using var host=await ReadHost.CreateAsync("read src/escape/secret.txt");
        var outside=Path.Combine(Path.GetDirectoryName(host.Root)!,"outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside,"secret.txt"),"outside secret");
        var link=Path.Combine(host.Root,"src","escape");
        if(substituteAfterPermit) {
            Directory.CreateDirectory(link); File.WriteAllText(Path.Combine(link,"secret.txt"),"inside sentinel");
            host.Authorizer.After=request=>{
                if(request.Phase==LanguageAuthorizationPhase.ResourceAccess && request.Action==ResourceAction.ReadFile) {
                    File.Delete(Path.Combine(link,"secret.txt")); Directory.Delete(link);
                    WindowsResourceTestHelpers.CreateJunction(link,outside);
                }
                return Task.CompletedTask;
            };
        }
        else WindowsResourceTestHelpers.CreateJunction(link,outside);
        try {
            var result=await host.RunAsync();
            Assert.Equal(LanguageRunStatus.AccessDenied,result.Status);
            Assert.Null(result.Statements);
        }
        finally {
            if(Directory.Exists(link) && (File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0)
                WindowsResourceTestHelpers.DeleteJunction(link,host.Root);
        }
    }

    [Fact]
    public async Task JunctionIsOmittedFromRealDirectoryEnumerationAndNeverTraversed()
    {
        using var host=await ReadHost.CreateAsync("find src **/*.txt");
        var outside=Path.Combine(Path.GetDirectoryName(host.Root)!,"outside");
        Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside,"secret.txt"),"outside secret");
        var link=Path.Combine(host.Root,"src","escape");
        WindowsResourceTestHelpers.CreateJunction(link,outside);
        try {
            var result=await host.RunAsync();
            Assert.Equal(LanguageRunStatus.Succeeded,result.Status);
            Assert.DoesNotContain(result.Statements!.SelectMany(s=>s.Values),v=>v.ToString()!.Contains("escape",StringComparison.Ordinal));
            Assert.DoesNotContain(host.Authorizer.Calls,c=>c.Request.ResourcePath is string path && path.StartsWith("src/escape/",StringComparison.Ordinal));
        }
        finally { WindowsResourceTestHelpers.DeleteJunction(link,host.Root); }
    }

    [Fact]
    public async Task NativeShortAliasCannotBypassExactFileExclusionWhenVolumeProvidesAliases()
    {
        using var host=await ReadHost.CreateAsync("read src/public.txt",
            [new("workspace","src/protected-longfilename.txt",AuthorityScopeKind.Exact)]);
        var physical=Path.Combine(host.Root,"src","protected-longfilename.txt");
        File.WriteAllText(physical,"short alias secret");
        var alias=Path.GetFileName(WindowsResourceTestHelpers.GetShortPath(physical));
        Assert.True(File.Exists(Path.Combine(host.Root,"src",alias)));
        host.Configure("read src/"+alias);
        var result=await host.RunAsync();
        Assert.Equal(string.Equals(alias,"protected-longfilename.txt",StringComparison.OrdinalIgnoreCase) ?
            LanguageRunStatus.AuthorityDenied : LanguageRunStatus.AccessDenied,result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task ForgedInvocationAndPermitWithoutEvidenceCannotReachProvider()
    {
        using var host=await ReadHost.CreateAsync("read src/public.txt");
        var forged=host.Invocation with { SubjectId="another-subject" };
        var result=await host.Runtime.ExecuteAsync(forged,host.Document);
        Assert.Equal(LanguageRunStatus.AuthorityDenied,result.Status);
        Assert.Null(result.Statements);
        var unsafeAuthorizer=new HufuLanguageAuthorizer(Context,host.Invocation,host.Document,new MissingEvidenceAuthorizer());
        var unsafeRuntime=new LanguageRuntime(new WorkspaceReference("workspace"),new LocalWorkspaceProvider(new WorkspaceId("workspace"),host.Root),unsafeAuthorizer);
        Assert.Equal(LanguageRunStatus.AuthorizationUnavailable,(await unsafeRuntime.ExecuteAsync(host.Invocation,host.Document)).Status);
    }

    private sealed class MissingEvidenceAuthorizer : IAuthorityRequestAuthorizer
    {
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,CancellationToken ct=default) =>
            ValueTask.FromResult(new AuthorityRequestAuthorization(request,AuthorityStatus.Permit,
                new(AuthorityStatus.Permit,"permit","v1","unsafe-evaluator",new string('0',64)),false));
    }

    internal sealed class ReadHost : IDisposable
    {
        internal BiscuitIntegrationFixture Fixture { get; }=new();
        internal string Root => Path.Combine(Path.GetDirectoryName(Fixture.DatabasePath)!,"workspace-root");
        internal BiscuitEnvelope Envelope { get; private set; }=null!;
        internal CompiledDocument Document { get; private set; }=null!;
        internal EffectInvocation Invocation { get; }=new("subject","effect-read","attempt-read");
        internal RecordingAuthorizer Authorizer { get; private set; }=null!;
        internal LanguageRuntime Runtime { get; private set; }=null!;
        internal List<string> Evidence { get; }=[];
        internal static async Task<ReadHost> CreateAsync(string script,IReadOnlyList<AuthorityScope>? exclusions=null)
        {
            if(!OperatingSystem.IsWindows()) throw Xunit.Sdk.SkipException.ForSkip("Qualified existing Windows Local read profile only.");
            var host=new ReadHost();
            try
            {
                Directory.CreateDirectory(Path.Combine(host.Root,"src","private"));
                File.WriteAllText(Path.Combine(host.Root,"src","public.txt"),"public needle");
                File.WriteAllText(Path.Combine(host.Root,"src","private","secret.txt"),"secret needle");
                var grant=new AuthorityGrant("grant",[AuthorityAction.ReadFile,AuthorityAction.ListDirectory,AuthorityAction.ReadMetadata,AuthorityAction.Release],
                    new("workspace","",AuthorityScopeKind.Subtree),
                    exclusions ?? [new("workspace","src/private",AuthorityScopeKind.Subtree)],Now.AddMinutes(-1),Now.AddHours(1));
                await host.Fixture.PublishAsync(host.Fixture.CreateSnapshot("v1",[new("workflow",[grant])]));
                host.Fixture.ResourceBindingFactory=request=>new(request,"Penghou.IO.Local.Windows.Read.v1",
                    BiscuitProfile.Hash(BiscuitProfile.Utf8.GetBytes(host.Root+"\n"+request.RelativePath)),request.RequestIdentity);
                host.Envelope=await host.Fixture.IssueAsync();
                host.Configure(script);

                return host;
            }
            catch { host.Dispose(); throw; }
        }
        internal void Configure(string script)
        {
            var compilation=LanguageCompiler.Compile(script,new WorkspaceId("workspace"));
            Assert.True(compilation.Succeeded,string.Join(",",compilation.Diagnostics.Select(d=>d.Code)));
            Document=compilation.Document!;
            var service=Fixture.CreateService(new CapturingRecorder(new BiscuitSqliteDecisionRecorder(Fixture.Store,Fixture.Registry),this));
            var semantic=new HufuLanguageAuthorizer(Context,Invocation,Document,new BiscuitRequestAuthorizer(service,Envelope));
            Authorizer=new(semantic);
            Runtime=new(new WorkspaceReference("workspace"),new LocalWorkspaceProvider(new WorkspaceId("workspace"),Root),Authorizer);
        }
        internal Task<LanguageRunResult> RunAsync()=>Runtime.ExecuteAsync(Invocation,Document).AsTask();
        public void Dispose()=>Fixture.Dispose();
        private sealed class CapturingRecorder(IBiscuitDecisionRecorder inner,ReadHost host) : IBiscuitDecisionRecorder
        {
            public async ValueTask<bool> RecordAsync(BiscuitDecisionEvidence evidence,CancellationToken ct=default)
            {
                var recorded=await inner.RecordAsync(evidence,ct);
                if(recorded) host.Evidence.Add(evidence.Id);
                return recorded;
            }
        }
    }
    internal sealed class RecordingAuthorizer(ILanguageAuthorizer inner) : ILanguageAuthorizer
    {
        internal List<(LanguageAuthorizationRequest Request,LanguageAuthorityDecision Decision)> Calls { get; }=[];
        internal Func<LanguageAuthorizationRequest,Task>? Before { get; set; }
        internal Func<LanguageAuthorizationRequest,Task>? After { get; set; }
        public async ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request,CancellationToken ct=default)
        {
            if(Before is not null) await Before(request);
            var result=await inner.AuthorizeAsync(request,ct);
            Calls.Add((request,result));
            if(result.Status==LanguageAuthorityStatus.Permit && After is not null) await After(request);
            return result;
        }
    }
}
