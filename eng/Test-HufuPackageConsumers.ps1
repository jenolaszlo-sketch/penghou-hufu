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
$ids = @('Penghou.Hufu','Penghou.Hufu.Cedar','Penghou.Hufu.IO','Penghou.Hufu.Luban','Penghou.Hufu.Sqlite','Penghou.Hufu.Workflow','Penghou.Hufu.Luban.Sqlite')
if ($Version -in @('0.1.0-preview.1','0.1.0-preview.2')) { $ids = @($ids | Where-Object { $_ -ne 'Penghou.Hufu.Luban.Sqlite' }) }
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
$program = @'
using Penghou.Hufu;
using Penghou.Hufu.Workflow;
using Penghou.Hufu.Luban;
using Penghou.IO.Abstractions;
using Penghou.Luban;
using Penghou.Luban.Language;
using Penghou.Workflow.Abstractions;
using System.Diagnostics.Metrics;

Type[] packages = [typeof(AuthoritySnapshot), typeof(Penghou.Hufu.Cedar.CedarAuthorityEvaluator),
    typeof(Penghou.Hufu.IO.HufuResourceAuthorizer), typeof(Penghou.Hufu.Luban.HufuLanguageAuthorizer),
    typeof(Penghou.Hufu.Sqlite.SqliteAuthorityStore), typeof(HufuExecutionAuthorizer), typeof(Penghou.Hufu.Luban.Sqlite.HufuSinglePatchHost)];
if (packages.Select(t => t.Assembly.GetName().Name).Distinct().Count() != 7)
    throw new Exception("Seven candidate package assemblies must load independently.");
var recorder = new Recorder();
var authorizer = new HufuExecutionAuthorizer("probe-policy", "probe-host", "probe-mapping-v1",
    new Bindings(), new Authority(), new Approval(), recorder);
var context = new ExecutionAuthorizationContext(new ExecutionIdentity("execution", "operation", 1, "revision"), "fresh-request", []);
var result = await authorizer.AuthorizeAsync(context);
if (result.Decision != ExecutionAuthorizationDecision.Denied || result.EvidenceId != "recorded-probe" ||
    result.AuthorizationRequestId != context.AuthorizationRequestId || recorder.Record?.Context != context ||
    recorder.Record.Result.Decision != ExecutionAuthorizationDecision.Denied)
    throw new Exception("Empty declarations must deny with the exact recorded outcome before any trusted service activation.");
Console.WriteLine("Seven-package standalone consumer and recorded denial passed.");

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

var measurement = new TaskCompletionSource<(long Value, Dictionary<string, object?> Tags)>(TaskCreationOptions.RunContinuationsAsynchronously);
using (var listener = new MeterListener())
{
    listener.InstrumentPublished = (instrument, owner) =>
    {
        if (instrument.Meter.Name == AuthorityTelemetry.SourceName && instrument.Name == AuthorityTelemetry.AuthorizationCountName)
            owner.EnableMeasurementEvents(instrument);
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
    {
        var copied = new Dictionary<string, object?>();
        foreach (var tag in tags) copied[tag.Key] = tag.Value;
        measurement.TrySetResult((value, copied));
    });
    listener.Start();
    using var telemetry = new AuthorityTelemetry(maxQueuedMeasurements: 1);
    var observed = new TelemetryAuthorityRequestAuthorizer(bounded, telemetry);
    if ((await observed.AuthorizeAsync(request)).Status != AuthorityStatus.Unavailable)
        throw new Exception("Telemetry must preserve required fail-closed preflight.");
    var exported = await measurement.Task.WaitAsync(TimeSpan.FromSeconds(5));
    if (exported.Value != 1 || exported.Tags.Count != 2 ||
        !Equals(exported.Tags["hufu.action"], "read_file") || !Equals(exported.Tags["hufu.outcome"], "unavailable"))
        throw new Exception("Telemetry must emit only closed bounded categories.");
    // Disposal and absent collectors must never alter mandatory-evidence failure.
    telemetry.Dispose();
    if ((await observed.AuthorizeAsync(request)).Status != AuthorityStatus.Unavailable || telemetry.DroppedMeasurements != 1)
        throw new Exception("Optional telemetry must preserve the exact fail-closed preflight result after shutdown.");
}
Console.WriteLine("Packaged optional telemetry emits closed categories and preserves fail-closed authorization after shutdown.");

var profile = HufuLanguageAuthorityProfile.ReadAndDiffV2;
var diff = LanguageCompiler.Compile("diff before.txt after.txt", new WorkspaceId("workspace"),
    new LanguageCompilerOptions(Versions: profile.Versions));
if (!diff.Succeeded) throw new Exception("Published Luban must compile the selected v2 profile.");
var document = diff.Document!;
var invocation = new EffectInvocation("subject", "consumer-diff", "attempt");
var diffAuthority = new DiffAuthority();
var diffAuthorizer = new HufuLanguageAuthorizer(authorityContext, invocation, document, profile, diffAuthority);
var node = document.Statements[0][0];
var admission = new LanguageAuthorizationRequest(invocation, document.Identity, node.Identity, node.Descriptor,
    LanguageProfile.DescriptorVersion, document.Versions, document.Workspace, node.Stage, LanguageAuthorizationPhase.Preflight);
if ((await diffAuthorizer.AuthorizeAsync(admission)).Status != LanguageAuthorityStatus.Permit)
    throw new Exception("The evidenced v2 semantic admission must permit.");
foreach (var input in new[] { "before.txt", "after.txt" })
    if (!diffAuthority.Requests.Any(r => r.Action == AuthorityAction.ReadFile && r.RelativePath == input) ||
        !diffAuthority.Requests.Any(r => r.Action == AuthorityAction.Release && r.RelativePath == input))
        throw new Exception("Diff admission must check both read and release scopes.");
if (diffAuthority.Requests.Any(r => r.Action is AuthorityAction.PatchFile or AuthorityAction.WriteFile))
    throw new Exception("Diff must never request mutation permission.");
var beforeForgery = diffAuthority.Requests.Count;
if ((await diffAuthorizer.AuthorizeAsync(admission with { Phase = LanguageAuthorizationPhase.ResourceAccess,
    ResourcePath = "elsewhere.txt", Action = ResourceAction.ReadFile, ResourceRequestIdentity = new RequestIdentity("forged") }))
    .Status != LanguageAuthorityStatus.Deny || diffAuthority.Requests.Count != beforeForgery)
    throw new Exception("Out-of-input resource access must deny before authority dispatch.");
try
{
    _ = new HufuLanguageAuthorizer(authorityContext, invocation, document, diffAuthority);
    throw new Exception("The existing constructor must continue rejecting v2.");
}
catch (ArgumentException) { }
var missingEvidence = new HufuLanguageAuthorizer(authorityContext, invocation, document, profile, new DiffAuthority(false));
if ((await missingEvidence.AuthorizeAsync(admission)).Status != LanguageAuthorityStatus.Unavailable)
    throw new Exception("An undocumented permit cannot admit a packaged diff.");
Console.WriteLine("Packaged explicit v2 profile, both input scopes, v1 compatibility and evidence failure passed.");

// Optional patch journal: real native schema initialization and independently denied history.
var patchDatabase = Path.Combine(Path.GetTempPath(), "hufu-package-patch-" + Guid.NewGuid().ToString("N") + ".db");
try
{
    var patchJournal = new Penghou.Hufu.Luban.Sqlite.SqlitePatchOutcomeJournal(patchDatabase, new DenyPatchJournal());
    using (var connection = await patchJournal.OpenAsync())
        if (connection.State != System.Data.ConnectionState.Open) throw new Exception("Patch journal owner must initialize the real SQLite database.");
    if ((await patchJournal.InspectAsync(actor, authorityContext, "probe-patch", new string('a', 64))).State != Penghou.Hufu.Luban.Sqlite.PatchRecoveryState.Unavailable)
        throw new Exception("Patch history must remain unavailable without independent authentication/policy.");
    if (patchJournal.ProfileIdentity != "hufu-sqlite-single-patch-start-v1") throw new Exception("Unexpected patch participant profile.");
}
finally
{
    foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(patchDatabase + suffix);
}
Console.WriteLine("Packaged co-located patch journal and denied disclosure passed.");

sealed class DenyPatchJournal : Penghou.Hufu.Luban.Sqlite.IPatchJournalAuthorizer
{
    public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(Penghou.Hufu.Luban.Sqlite.PatchJournalAccessRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new AuthorityStoreAuthorization(AuthorityStatus.Deny));
}

sealed class DiffAuthority(bool evidence = true) : IAuthorityRequestAuthorizer
{
    public List<AuthorityRequest> Requests { get; } = [];
    public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return ValueTask.FromResult(evidence
            ? new AuthorityRequestAuthorization(request, AuthorityStatus.Permit,
                new AuthorityDecision(AuthorityStatus.Permit, "probe.permit", "snapshot", "probe-evaluator", new string('a', 64)), true)
            : new AuthorityRequestAuthorization(request, AuthorityStatus.Permit));
    }
}

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
if ($Version -in @('0.1.0-preview.1','0.1.0-preview.2')) {
    $program = $program.Replace(', typeof(Penghou.Hufu.Luban.Sqlite.HufuSinglePatchHost)', '').Replace('Count() != 7','Count() != 6')
    $program = $program.Replace('Seven candidate','Six candidate').Replace('Seven-package','Six-package')
    $start = $program.IndexOf('// Optional patch journal:', [StringComparison]::Ordinal)
    $end = $program.IndexOf('sealed class DiffAuthority', [StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -le $start) { throw 'Patch probe boundaries must be present.' }
    $program = $program.Remove($start, $end - $start)
}
# Preserve qualification of immutable preview.1 recovery artifacts, which do not
# expose the new opt-in profile. Later candidates must run the v2 API probes.
if ($Version -eq '0.1.0-preview.1') {
    $start = $program.IndexOf('var profile = HufuLanguageAuthorityProfile.ReadAndDiffV2;', [StringComparison]::Ordinal)
    $end = $program.IndexOf('// Explicit trusted test fixture, not a production credential authenticator.', [StringComparison]::Ordinal)
    if ($start -lt 0 -or $end -le $start) { throw 'V2 probe boundaries must be present.' }
    $program = $program.Remove($start, $end - $start)
}
Set-Content -LiteralPath (Join-Path $consumer 'Program.cs') $program
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
    [ordered]@{ TargetFramework=$tfm; RecordedDenialPassed=$true; CoreAdmissionProbePassed=$true; CoreIssuanceProbePassed=$true; CedarExplanationCapturePassed=$true; RedactedExplanationSummaryPassed=$true; OptionalTelemetryIsolationPassed=$true; ExplicitLubanV2ProbePassed=($Version -ne '0.1.0-preview.1'); OptionalPatchJournalProbePassed=($Version -notin @('0.1.0-preview.1','0.1.0-preview.2')) }
}
ConvertTo-Json -InputObject ([ordered]@{SchemaVersion=1;PackageVersion=$Version;FreshCache=$cache;Packages=@($libraries.Name);Targets=@($results);NoProjectReferences=$true;NoZhinuDependencies=$true;Status='passed'}) -Depth 8 |
    Set-Content -LiteralPath (Join-Path $run 'qualification.json')
Write-Output (Join-Path $run 'qualification.json')
