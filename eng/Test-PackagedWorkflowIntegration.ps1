#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory, [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../qualification'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'tests/Penghou.Hufu.Workflow.Integration.Tests/Penghou.Hufu.Workflow.Integration.Tests.csproj'
$packages = (Resolve-Path -LiteralPath $PackageDirectory).Path
$run = [IO.Path]::GetFullPath((Join-Path $OutputDirectory ('integration-' + [Guid]::NewGuid().ToString('N'))))
$cache = Join-Path ([IO.Path]::GetTempPath()) ('hfi-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run,$cache -Force | Out-Null
$config = Join-Path $run 'NuGet.Config'
$escapedPackages = [System.Security.SecurityElement]::Escape($packages)
$escapedCache = [System.Security.SecurityElement]::Escape($cache)
Set-Content -LiteralPath $config @"
<configuration><packageSources><clear/><add key="hufu-local" value="$escapedPackages"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="hufu-local"><package pattern="Penghou.Hufu"/><package pattern="Penghou.Hufu.Workflow"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders><config><add key="globalPackagesFolder" value="$escapedCache"/></config></configuration>
"@
$properties = @('-p:UseHufuWorkflowPackage=true', "-p:HufuWorkflowPackageVersion=$Version", "-p:RestorePackagesPath=$cache")
dotnet restore $project --configfile $config --no-cache @properties
if ($LASTEXITCODE -ne 0) { throw 'Package-only workflow integration restore failed.' }
$assetsPath = Join-Path $repo 'tests/Penghou.Hufu.Workflow.Integration.Tests/obj/project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$libraries = @($assets.libraries.PSObject.Properties)
if ($libraries | Where-Object { $_.Value.type -eq 'project' }) { throw 'Integration qualification still has a source project dependency.' }
$folders = @($assets.packageFolders.PSObject.Properties.Name)
if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd([char[]]@('/','\')) -ne $cache.TrimEnd([char[]]@('/','\'))) { throw 'Unexpected integration cache or fallback.' }
foreach ($exact in @("Penghou.Hufu.Workflow/$Version","Penghou.Hufu/$Version",'Penghou.Workflow.Abstractions/0.1.0-preview.2','Penghou.Zhinu/0.2.0-preview.1','Penghou.Zhinu.Sqlite/0.2.0-preview.1')) {
    if (-not ($libraries.Name -ccontains $exact)) { throw "Missing exact integration package: $exact" }
}
dotnet test $project -c Release --no-restore @properties --logger trx --results-directory (Join-Path $run 'tests')
if ($LASTEXITCODE -ne 0) { throw 'Package-only workflow integration tests failed.' }
$trxFiles = @(Get-ChildItem -LiteralPath (Join-Path $run 'tests') -Filter '*.trx' -File)
if ($trxFiles.Count -ne 2) { throw 'Expected integration reports from both frameworks.' }
$counts = foreach ($file in $trxFiles) {
    [xml]$trx = Get-Content -LiteralPath $file.FullName
    $counter = $trx.TestRun.ResultSummary.Counters
    if ([int]$counter.failed -ne 0 -or [int]$counter.notExecuted -ne 0 -or [int]$counter.total -le 0 -or [int]$counter.passed -ne [int]$counter.total) { throw 'Failed, skipped or empty integration suite.' }
    [ordered]@{Report=$file.Name;Total=[int]$counter.total;Passed=[int]$counter.passed}
}
ConvertTo-Json -InputObject ([ordered]@{SchemaVersion=1;CandidateVersion=$Version;FreshCache=$cache;PublishedZhinuVersion='0.2.0-preview.1';PublishedNeutralVersion='0.1.0-preview.2';NoProjectReferences=$true;Packages=@($libraries.Name);Reports=@($counts);Status='passed'}) -Depth 8 |
    Set-Content -LiteralPath (Join-Path $run 'qualification.json')
Write-Output (Join-Path $run 'qualification.json')
