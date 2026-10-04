#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory, [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../qualification'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packages = (Resolve-Path -LiteralPath $PackageDirectory).Path
$run = [IO.Path]::GetFullPath((Join-Path $OutputDirectory ('patch-consumer-' + [Guid]::NewGuid().ToString('N'))))
$cache = Join-Path ([IO.Path]::GetTempPath()) ('hpfc-' + [Guid]::NewGuid().ToString('N'))
$consumer = Join-Path $run 'consumer'
New-Item -ItemType Directory -Path $cache,$consumer -Force | Out-Null
Set-Content -LiteralPath (Join-Path $consumer 'Directory.Build.props') '<Project />'
Set-Content -LiteralPath (Join-Path $consumer 'Directory.Build.targets') '<Project />'
$ids = @('Penghou.Hufu','Penghou.Hufu.Cedar','Penghou.Hufu.IO','Penghou.Hufu.Luban','Penghou.Hufu.Sqlite','Penghou.Hufu.Workflow','Penghou.Hufu.Luban.Sqlite')
$references = ($ids | ForEach-Object { "<PackageReference Include=`"$_`" Version=`"[$Version]`"/>" }) -join "`n"
$project = Join-Path $consumer 'PatchConsumer.csproj'
Set-Content -LiteralPath $project @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks><AssemblyName>Penghou.Hufu.Luban.Sqlite.Tests</AssemblyName><IsTestProject>true</IsTestProject><IsPackable>false</IsPackable><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>$references<PackageReference Include="Penghou.IO.Local" Version="[0.1.0-preview.1]"/><PackageReference Include="Microsoft.NET.Test.Sdk" Version="[17.14.1]"/><PackageReference Include="xunit" Version="[2.9.3]"/><PackageReference Include="xunit.runner.visualstudio" Version="[3.1.4]"/></ItemGroup></Project>
"@
foreach ($name in @('HufuSinglePatchHostTests.cs','PatchOutcomeJournalTests.cs')) {
    Copy-Item -LiteralPath (Join-Path $repo "tests/Penghou.Hufu.Luban.Sqlite.Tests/$name") -Destination (Join-Path $consumer $name)
}
$escapedPackages = [System.Security.SecurityElement]::Escape($packages)
$escapedCache = [System.Security.SecurityElement]::Escape($cache)
$mappings = ($ids | ForEach-Object { "<package pattern=`"$_`"/>" }) -join ''
$config = Join-Path $run 'NuGet.Config'
Set-Content -LiteralPath $config @"
<configuration><packageSources><clear/><add key="hufu-local" value="$escapedPackages"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="hufu-local">$mappings</packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders><config><add key="globalPackagesFolder" value="$escapedCache"/></config></configuration>
"@
dotnet restore $project --configfile $config --packages $cache --no-cache
if ($LASTEXITCODE -ne 0) { throw 'Isolated patch consumer restore failed.' }
$assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
$libraries = @($assets.libraries.PSObject.Properties)
if ($libraries | Where-Object { $_.Value.type -eq 'project' -or $_.Name -match 'Penghou\.Zhinu|BiscuitSharp' }) { throw 'Forbidden dependency in patch consumer.' }
foreach ($id in $ids) { if (-not ($libraries.Name -ccontains "$id/$Version")) { throw "Candidate resolution drift: $id" } }
$folders = @($assets.packageFolders.PSObject.Properties.Name)
if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd([char[]]@('/','\')) -ne $cache.TrimEnd([char[]]@('/','\'))) { throw 'Unexpected package cache or fallback.' }
$native = [OperatingSystem]::IsWindows()
$results = foreach ($tfm in @('net8.0','net10.0')) {
    $arguments = @('test',$project,'-c','Release','-f',$tfm,'--no-restore','--logger','trx','--results-directory',(Join-Path $run 'tests'))
    if (-not $native) { $arguments += @('--filter','FullyQualifiedName!~HufuSinglePatchHostTests') }
    dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Packaged patch integration failed: $tfm" }
    [ordered]@{TargetFramework=$tfm;PortableJournal=$true;ControlledWindowsWriter=$native;Passed=$true}
}
ConvertTo-Json -InputObject ([ordered]@{SchemaVersion=1;PackageVersion=$Version;FreshCache=$cache;Packages=@($libraries.Name);Targets=@($results);NoProjectReferences=$true;NoZhinuDependencies=$true;Status='passed'}) -Depth 8 |
    Set-Content -LiteralPath (Join-Path $run 'qualification.json')
Write-Output (Join-Path $run 'qualification.json')
