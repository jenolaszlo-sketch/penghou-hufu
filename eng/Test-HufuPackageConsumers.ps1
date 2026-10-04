#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory, [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../qualification'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packages = (Resolve-Path -LiteralPath $PackageDirectory).Path
$run = [IO.Path]::GetFullPath((Join-Path $OutputDirectory ('consumer-' + [Guid]::NewGuid().ToString('N'))))
$cache = Join-Path ([IO.Path]::GetTempPath()) ('hfc-' + [Guid]::NewGuid().ToString('N'))
$consumer = Join-Path $run 'consumer'
New-Item -ItemType Directory -Path $cache,$consumer -Force | Out-Null
$ids = @('Penghou.Hufu','Penghou.Hufu.Cedar','Penghou.Hufu.IO','Penghou.Hufu.Luban','Penghou.Hufu.Sqlite','Penghou.Hufu.Workflow')
Set-Content -LiteralPath (Join-Path $consumer 'Directory.Build.props') '<Project />'
Set-Content -LiteralPath (Join-Path $consumer 'Directory.Build.targets') '<Project />'
$references = ($ids | ForEach-Object { "<PackageReference Include=`"$_`" Version=`"[$Version]`"/>" }) -join "`n"
Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj') @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net8.0;net10.0</TargetFrameworks><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>$references</ItemGroup></Project>
"@
$escapedPackages = [System.Security.SecurityElement]::Escape($packages)
$escapedCache = [System.Security.SecurityElement]::Escape($cache)
$mappings = ($ids | ForEach-Object { "<package pattern=`"$_`"/>" }) -join ''
$config = Join-Path $run 'NuGet.Config'
Set-Content -LiteralPath $config @"
<configuration><packageSources><clear/><add key="hufu-local" value="$escapedPackages"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="hufu-local">$mappings</packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders><config><add key="globalPackagesFolder" value="$escapedCache"/></config></configuration>
"@
Set-Content -LiteralPath (Join-Path $consumer 'Program.cs') @'
using Penghou.Hufu;
using Penghou.Hufu.Workflow;
using Penghou.Workflow.Abstractions;

Type[] packages = [typeof(AuthoritySnapshot), typeof(Penghou.Hufu.Cedar.CedarAuthorityEvaluator),
    typeof(Penghou.Hufu.IO.HufuResourceAuthorizer), typeof(Penghou.Hufu.Luban.HufuLanguageAuthorizer),
    typeof(Penghou.Hufu.Sqlite.SqliteAuthorityStore), typeof(HufuExecutionAuthorizer)];
if (packages.Select(t => t.Assembly.GetName().Name).Distinct().Count() != 6)
    throw new Exception("Six candidate package assemblies must load independently.");
var recorder = new Recorder();
var authorizer = new HufuExecutionAuthorizer("probe-policy", "probe-host", "probe-mapping-v1",
    new Bindings(), new Authority(), new Approval(), recorder);
var context = new ExecutionAuthorizationContext(new ExecutionIdentity("execution", "operation", 1, "revision"), "fresh-request", []);
var result = await authorizer.AuthorizeAsync(context);
if (result.Decision != ExecutionAuthorizationDecision.Denied || result.EvidenceId != "recorded-probe" ||
    result.AuthorizationRequestId != context.AuthorizationRequestId || recorder.Record?.Context != context ||
    recorder.Record.Result.Decision != ExecutionAuthorizationDecision.Denied)
    throw new Exception("Empty declarations must deny with the exact recorded outcome before any trusted service activation.");
Console.WriteLine("Six-package standalone consumer and recorded denial passed.");

var authorityContext = new AuthenticatedAuthorityContext("tenant", "subject", "run", "revision", "fence");
var request = new AuthorityRequest(authorityContext, AuthorityAction.ReadFile, "workspace", "src/file.txt", "probe-request");
var bounded = new BoundedAuthorityRequestAuthorizer(new Authority(), 1, 0, TimeSpan.FromSeconds(1));
if ((await bounded.AuthorizeAsync(request)).Status != AuthorityStatus.Unavailable)
    throw new Exception("A failed inner authorizer must remain unavailable through the admission wrapper.");
var actor = new AuthorityStoreActor("tenant", "host", "session");
var now = DateTimeOffset.UtcNow;
var snapshot = new AuthoritySnapshot(authorityContext, "probe", [new("layer", [new("grant", [AuthorityAction.ReadFile],
    new("workspace", "src", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddMinutes(1))])], [], now.AddMinutes(1));
var issuance = new BoundedAuthorityIssuanceAuthorizer(new Unauthenticated(), new UnreachablePolicy());
var publication = new AuthorityStoreAccessRequest(actor, AuthorityStoreOperation.Publish,
    AuthoritySubject.From(authorityContext), authorityContext, "probe-command", 0, snapshot);
if ((await issuance.AuthorizeAsync(publication)).Status != AuthorityStatus.Deny)
    throw new Exception("Publication must deny without authenticated issuer facts.");
Console.WriteLine("Packaged core admission and issuance fail-closed probes passed.");

var captured = new Penghou.Hufu.Cedar.CedarAuthorityEvaluator().EvaluateExplained(snapshot, request, now);
if (captured.Decision.Status != AuthorityStatus.Permit || captured.Coverage != AuthorityExplanationCoverage.CapturedLayerOutcomes)
    throw new Exception("Packaged Cedar must capture the actual typed-path evaluation.");
var explanationReader = new AuthorityExplanationReader(new ProbeExplanationPolicy(actor, captured.Identity));
var explained = await explanationReader.ReadAsync(actor, captured);
if (explained.Status != AuthorityExplanationReadStatus.Disclosed || explained.Projection?.DecisionStatus != captured.Decision.Status ||
    explained.Projection.Details is not null)
    throw new Exception("The separately authorized summary must contain only the captured outcome.");
Console.WriteLine("Packaged Cedar capture and authorized redacted explanation summary passed.");

// Explicit trusted test fixture, not a production credential authenticator.
sealed class ProbeExplanationPolicy(AuthorityStoreActor expectedActor, string expectedExplanation) : IAuthorityExplanationAccessPolicy
{
    public ValueTask<AuthorityExplanationAccess?> AuthorizeAsync(AuthorityStoreActor actor,
        AuthorityDecisionExplanation explanation, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AuthorityExplanationAccess?>(actor == expectedActor && explanation.Identity == expectedExplanation
            ? new(AuthorityStatus.Permit, expectedActor, expectedExplanation, AuthorityExplanationDetailLevel.Summary, DateTimeOffset.UtcNow.AddMinutes(1))
            : new(AuthorityStatus.Deny));
}

sealed class Unauthenticated : IAuthorityIssuanceTrustSource
{
    public ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(AuthorityStoreActor actor,
        AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AuthorityIssuancePrincipal?>(null);
}
sealed class UnreachablePolicy : IAuthorityStoreAuthorizer
{
    public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
        CancellationToken cancellationToken = default) =>
        throw new Exception("Unauthenticated issuance must deny before operation policy.");
}

sealed class Bindings : IWorkflowAuthorityBindingSource
{
    public ValueTask<WorkflowAuthorityBinding?> ResolveAsync(ExecutionAuthorizationContext context, CancellationToken cancellationToken = default) =>
        throw new Exception("Unsupported declarations must fail before identity/resource resolution.");
}
sealed class Authority : IAuthorityRequestAuthorizer
{
    public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default) =>
        throw new Exception("Unsupported declarations must never evaluate Hufu authority.");
}
sealed class Approval : IWorkflowApprovalCoordinator
{
    public ValueTask<WorkflowApprovalResult?> EvaluateAsync(WorkflowAuthorityBinding binding, CancellationToken cancellationToken = default) =>
        throw new Exception("A denial must never request approval.");
}
sealed class Recorder : IWorkflowAuthorizationRecorder
{
    public WorkflowAuthorizationRecord? Record { get; private set; }
    public ValueTask<string?> RecordAsync(WorkflowAuthorizationRecord record, CancellationToken cancellationToken = default)
    { Record = record; return ValueTask.FromResult<string?>("recorded-probe"); }
}
'@
$project = Join-Path $consumer 'Consumer.csproj'
dotnet restore $project --configfile $config --packages $cache --no-cache
if ($LASTEXITCODE -ne 0) { throw 'Isolated consumer restore failed.' }
$assetsPath = Join-Path $consumer 'obj/project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$libraries = @($assets.libraries.PSObject.Properties)
if ($libraries | Where-Object { $_.Value.type -eq 'project' -or $_.Name -match 'Penghou\.Zhinu|BiscuitSharp' }) { throw 'Forbidden project/Zhinu/Biscuit dependency in release consumer.' }
foreach ($id in $ids) {
    if (-not ($libraries.Name -ccontains "$id/$Version")) { throw "Consumer did not resolve exact candidate: $id" }
}
if (-not ($libraries.Name -ccontains 'Penghou.Workflow.Abstractions/0.1.0-preview.2')) { throw 'Neutral contract pin drifted.' }
$folders = @($assets.packageFolders.PSObject.Properties.Name)
if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd([char[]]@('/','\')) -ne $cache.TrimEnd([char[]]@('/','\'))) { throw 'Unexpected package cache or fallback.' }
$results = foreach ($tfm in @('net8.0','net10.0')) {
    dotnet build $project -c Release -f $tfm --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Consumer build failed: $tfm" }
    dotnet (Join-Path $consumer "bin/Release/$tfm/Consumer.dll") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Consumer execution failed: $tfm" }
    [ordered]@{ TargetFramework=$tfm; RecordedDenialPassed=$true; CoreAdmissionProbePassed=$true; CoreIssuanceProbePassed=$true; CedarExplanationCapturePassed=$true; RedactedExplanationSummaryPassed=$true }
}
ConvertTo-Json -InputObject ([ordered]@{SchemaVersion=1;PackageVersion=$Version;FreshCache=$cache;Packages=@($libraries.Name);Targets=@($results);NoProjectReferences=$true;NoZhinuDependencies=$true;Status='passed'}) -Depth 8 |
    Set-Content -LiteralPath (Join-Path $run 'qualification.json')
Write-Output (Join-Path $run 'qualification.json')
