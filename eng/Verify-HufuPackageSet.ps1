#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory, [Parameter(Mandatory)][string]$Version)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$dependencies = @{
    'Penghou.Hufu' = @{}
    'Penghou.Hufu.Cedar' = @{ 'Penghou.Hufu'=$Version; 'CedarSharp'='[1.0.0]' }
    'Penghou.Hufu.IO' = @{ 'Penghou.Hufu'=$Version; 'Penghou.IO.Abstractions'='[0.1.0-preview.1]'; 'Penghou.IO.Protocols'='[0.1.0-preview.1]' }
    'Penghou.Hufu.Luban' = @{ 'Penghou.Hufu'=$Version; 'Penghou.Luban'='[0.1.0-preview.1]' }
    'Penghou.Hufu.Sqlite' = @{ 'Penghou.Hufu'=$Version; 'Microsoft.Data.Sqlite'='[10.0.10]'; 'SQLitePCLRaw.bundle_e_sqlite3'='[2.1.12]' }
    'Penghou.Hufu.Luban.Sqlite' = @{ 'Penghou.Hufu.Luban'=$Version; 'Penghou.Hufu.Sqlite'=$Version }
    'Penghou.Hufu.Workflow' = @{ 'Penghou.Hufu'=$Version; 'Penghou.Workflow.Abstractions'='[0.1.0-preview.2]' }
}
$packages = @(Get-ChildItem -LiteralPath $directory -Filter '*.nupkg' -File)
$symbols = @(Get-ChildItem -LiteralPath $directory -Filter '*.snupkg' -File)
if ($Version -in @('0.1.0-preview.1','0.1.0-preview.2')) { $dependencies.Remove('Penghou.Hufu.Luban.Sqlite') }
if ($packages.Count -ne $dependencies.Count -or $symbols.Count -ne $dependencies.Count) { throw 'Unexpected package/symbol pair count.' }
$report = foreach ($id in $dependencies.Keys | Sort-Object) {
    $package = Join-Path $directory "$id.$Version.nupkg"
    $symbol = Join-Path $directory "$id.$Version.snupkg"
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $nuspecs = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($nuspecs.Count -ne 1) { throw "Expected one nuspec: $id" }
        $reader = [IO.StreamReader]::new($nuspecs[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        if ([string]$metadata.id -cne $id -or [string]$metadata.version -cne $Version) { throw "Package identity mismatch: $id" }
        if ([string]::IsNullOrWhiteSpace([string]$metadata.description)) { throw "Missing package description: $id" }
        if ($nuspec.OuterXml -match 'Penghou\.Zhinu|BiscuitSharp') { throw "Forbidden dependency in released package: $id" }
        foreach ($entry in @('README.md','package-release-profile.md',"lib/net8.0/$id.dll","lib/net10.0/$id.dll","lib/net8.0/$id.xml","lib/net10.0/$id.xml")) {
            if ($null -eq $archive.GetEntry($entry)) { throw "Missing $entry in $id" }
        }
        foreach ($tfm in @('net8.0','net10.0')) {
            $actual = @{}
            $groups = @($metadata.SelectNodes("*[local-name()='dependencies']/*[local-name()='group'][@targetFramework='$tfm']"))
            if ($groups.Count -ne 1) { throw "Expected exactly one dependency group: $id/$tfm" }
            foreach ($dependency in @($groups[0].SelectNodes("*[local-name()='dependency']"))) {
                $actual[[string]$dependency.id] = [string]$dependency.version
            }
            $expected = $dependencies[$id]
            if ($actual.Count -ne $expected.Count) { throw "Unexpected dependency count: $id/$tfm" }
            foreach ($name in $expected.Keys) {
                if (-not $actual.ContainsKey($name) -or $actual[$name] -cne $expected[$name]) { throw "Unexpected dependency: $id/$tfm/$name ($($actual[$name]))" }
            }
        }
    } finally { $archive.Dispose() }
    $symbolArchive = [IO.Compression.ZipFile]::OpenRead($symbol)
    try {
        foreach ($tfm in @('net8.0','net10.0')) {
            if ($null -eq $symbolArchive.GetEntry("lib/$tfm/$id.pdb")) { throw "Missing symbol PDB: $id/$tfm" }
        }
    } finally { $symbolArchive.Dispose() }
    & (Join-Path $PSScriptRoot 'Verify-HufuPackageSymbols.ps1') -PackagePath $package -SymbolPath $symbol | Out-Null
    [ordered]@{ Id=$id; Version=$Version; Sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash; SymbolSha256=(Get-FileHash -LiteralPath $symbol -Algorithm SHA256).Hash }
}
ConvertTo-Json -InputObject ([ordered]@{SchemaVersion=1;Packages=@($report);NoZhinuDependencies=$true;PortableSymbolsMatchAssemblies=$true;Status='passed'}) -Depth 8 |
    Set-Content -LiteralPath (Join-Path $directory 'package-inspection.json')
Write-Output 'Seven package/symbol pairs, target frameworks and exact dependency inventories verified.'
