#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/packages')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
    $Version = [string]$props.Project.PropertyGroup[0].Version
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw 'A checked-in or explicit package version is required.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (@(Get-ChildItem -LiteralPath $output -File | Where-Object Extension -In '.nupkg','.snupkg').Count) { throw 'Package output must start empty; preserve prior artifacts separately.' }
$config = Join-Path $output 'NuGet.Config'
Set-Content -LiteralPath $config '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>'
foreach ($id in @('Penghou.Hufu','Penghou.Hufu.Cedar','Penghou.Hufu.IO','Penghou.Hufu.Luban','Penghou.Hufu.Sqlite','Penghou.Hufu.Workflow','Penghou.Hufu.Luban.Sqlite')) {
    $project = Join-Path $repo "src/$id/$id.csproj"
    dotnet restore $project --configfile $config -p:Version=$Version -p:UsePenghouSource=false -p:UseLubanSource=false
    if ($LASTEXITCODE -ne 0) { throw "Public-feed restore failed: $id" }
    dotnet pack $project -c Release --no-restore -o $output -p:Version=$Version -p:UsePenghouSource=false -p:UseLubanSource=false
    if ($LASTEXITCODE -ne 0) { throw "Pack failed: $id" }
}
& (Join-Path $PSScriptRoot 'Verify-HufuPackageSet.ps1') -PackageDirectory $output -Version $Version
