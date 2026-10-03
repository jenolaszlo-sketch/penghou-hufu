#Requires -Version 7.0
[CmdletBinding()]
param([string] $ZhinuRoot = (Join-Path $PSScriptRoot '../../Penghou.Zhinu'), [string] $CandidateFeedPath)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This qualification requires the supported Windows Local/native profiles.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$zhinu = (Resolve-Path -LiteralPath $ZhinuRoot).Path
& (Join-Path $PSScriptRoot 'Restore-BiscuitCandidate.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('hufu-public-packages-' + [guid]::NewGuid().ToString('N'))
$consumer = Join-Path $scratch 'Penghou.Hufu'
$cache = Join-Path $scratch 'packages'
$previousCache = $env:NUGET_PACKAGES
$expectedSource = if ($CandidateFeedPath) { (Resolve-Path -LiteralPath $CandidateFeedPath).Path } else { 'https://api.nuget.org/v3/index.json' }

function Copy-SourceTree([string] $source, [string] $destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) {
        if ($entry.PSIsContainer) {
            if ($entry.Name -notin @('bin', 'obj', '.git', 'TestResults')) {
                Copy-SourceTree $entry.FullName (Join-Path $destination $entry.Name)
            }
        } else {
            Copy-Item -LiteralPath $entry.FullName -Destination $destination
        }
    }
}

try {
    New-Item -ItemType Directory -Path $consumer -Force | Out-Null
    foreach ($name in @('src', 'tests')) {
        Copy-SourceTree (Join-Path $repo $name) (Join-Path $consumer $name)
    }
    foreach ($name in @('Directory.Build.props', 'Penghou.Hufu.slnx', 'nuget.config')) {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $consumer
    }
    $isolatedZhinu = Join-Path $scratch 'Penghou.Zhinu'
    Copy-SourceTree (Join-Path $zhinu 'src') (Join-Path $isolatedZhinu 'src')
    Copy-Item -LiteralPath (Join-Path $zhinu 'Directory.Build.props') -Destination $isolatedZhinu
    $biscuitFeed = Join-Path $consumer 'artifacts/biscuitsharp-feed'
    New-Item -ItemType Directory -Path $biscuitFeed -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'artifacts/biscuitsharp-feed/BiscuitSharp.0.1.0-preview.2.nupkg') -Destination $biscuitFeed
    if ($CandidateFeedPath) {
        [xml] $config = Get-Content -LiteralPath (Join-Path $consumer 'nuget.config') -Raw
        $source = $config.CreateElement('add')
        $source.SetAttribute('key', 'resource-candidate')
        $source.SetAttribute('value', $expectedSource)
        $config.configuration.packageSources.AppendChild($source) | Out-Null
        $mapping = $config.CreateElement('packageSource')
        $mapping.SetAttribute('key', 'resource-candidate')
        foreach ($pattern in @('Penghou.IO.*', 'Penghou.Luban')) {
            $package = $config.CreateElement('package')
            $package.SetAttribute('pattern', $pattern)
            $mapping.AppendChild($package) | Out-Null
        }
        $config.configuration.packageSourceMapping.AppendChild($mapping) | Out-Null
        $config.Save((Join-Path $consumer 'nuget.config'))
    }
    $env:NUGET_PACKAGES = $cache
    $solution = Join-Path $consumer 'Penghou.Hufu.slnx'
    dotnet restore $solution --configfile (Join-Path $consumer 'nuget.config') --packages $cache --no-cache -p:UsePenghouSource=false -p:UseLubanSource=false
    if ($LASTEXITCODE -ne 0) { throw 'Isolated public-package restore failed.' }

    $packageEvidence = @()
    foreach ($id in @('penghou.io.abstractions', 'penghou.io.protocols', 'penghou.io.local', 'penghou.luban')) {
        $directory = Join-Path $cache "$id/0.1.0-preview.1"
        $metadata = Get-Content -LiteralPath (Join-Path $directory '.nupkg.metadata') -Raw | ConvertFrom-Json
        if ($metadata.source.TrimEnd('/') -ne $expectedSource.TrimEnd('/')) { throw "$id did not restore from the required source." }
        $archive = Join-Path $directory "$id.0.1.0-preview.1.nupkg"
        $packageEvidence += [ordered]@{ id = $id; version = '0.1.0-preview.1'; source = $metadata.source; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    foreach ($asset in Get-ChildItem -LiteralPath $consumer -Recurse -Filter project.assets.json) {
        $json = Get-Content -LiteralPath $asset.FullName -Raw | ConvertFrom-Json
        foreach ($library in $json.libraries.PSObject.Properties) {
            if ($library.Name -match '^Penghou\.(IO\.|Luban/)' -and $library.Value.type -ne 'package') {
                throw "Source dependency detected in $($asset.FullName): $($library.Name)"
            }
        }
    }

    $testEvidence = @()
    foreach ($tfm in @('net8.0', 'net10.0')) {
        $results = Join-Path $scratch "results/$tfm"
        foreach ($suite in @('Penghou.Hufu.Tests', 'Penghou.Hufu.Biscuit.Tests', 'Penghou.Hufu.IO.Tests')) {
            $project = Join-Path $consumer "tests/$suite/$suite.csproj"
            dotnet test $project -c Release -f $tfm --no-restore -p:UsePenghouSource=false -p:UseLubanSource=false --results-directory (Join-Path $results $suite) --logger trx
            if ($LASTEXITCODE -ne 0) { throw "Isolated Hufu integration failed ($suite, $tfm)." }
        }
        $trxFiles = @(Get-ChildItem -LiteralPath $results -Recurse -Filter '*.trx')
        if ($trxFiles.Count -ne 3) { throw "Expected the three Hufu test suites ($tfm)." }
        foreach ($trx in $trxFiles) {
            [xml] $run = Get-Content -LiteralPath $trx.FullName -Raw
            $counters = $run.TestRun.ResultSummary.Counters
            if ([int]$counters.failed -ne 0 -or [int]$counters.total -eq 0 -or [int]$counters.passed -ne [int]$counters.total) {
                throw "Failures, empty suite or skipped cases in $($trx.FullName)."
            }
            $testEvidence += [ordered]@{ framework = $tfm; suite = [IO.Path]::GetFileName($trx.DirectoryName); passed = [int]$counters.passed; failed = [int]$counters.failed; skipped = [int]$counters.total - [int]$counters.executed }
        }
    }
    $report = [ordered]@{
        schema = if ($CandidateFeedPath) { 'hufu-candidate-resource-packages-v1' } else { 'hufu-public-resource-packages-v1' }
        recordedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        operatingSystem = [Environment]::OSVersion.VersionString
        resourcePackages = $packageEvidence
        tests = $testEvidence
        isolatedFrom = @('Penghou source', 'Luban source', 'existing package caches', 'original build outputs')
        remainingSourceDependency = 'Zhinu (copied source; not a package migration claim)'
        biscuitDependency = 'Exact qualified unpublished BiscuitSharp preview.2 artifact; not a public-feed claim'
    }
    $reportName = if ($CandidateFeedPath) { 'candidate-resource-packages.json' } else { 'public-resource-packages.json' }
    $reportPath = Join-Path $repo "docs/qualification/$reportName"
    New-Item -ItemType Directory -Path (Split-Path $reportPath) -Force | Out-Null
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8) + [char]10, [Text.UTF8Encoding]::new($false))
    Write-Output "Resource-package qualification recorded at $reportPath"
} finally {
    $env:NUGET_PACKAGES = $previousCache
    if (Test-Path -LiteralPath $scratch) {
        $resolved = (Resolve-Path -LiteralPath $scratch).Path
        $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (!$resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'hufu-public-packages-*') { throw 'Refusing to delete unexpected scratch directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
