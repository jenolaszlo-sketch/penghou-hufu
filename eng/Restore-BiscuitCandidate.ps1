#Requires -Version 5.1
<#
.SYNOPSIS
  Restores the exact qualified unpublished BiscuitSharp preview.2 package.
.DESCRIPTION
  Bootstraps the local BiscuitSharp package feed used by this experimental Hufu
  adapter. The candidate is pinned to one successful immutable CI run, commit,
  filename and SHA-256. An already present matching package is verified and
  reused without network access. This is not a stable package restore contract.
  No workflow is triggered and no package is published.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Fail([string]$message) { throw "BISCUIT-CANDIDATE: $message" }

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if ($args.Count -ne 0) { Fail "this bootstrap has no candidate overrides" }

$sourceRepo = "jenolaszlo-sketch/biscuit-sharp"
$runId = "36979786333"
$expectedHeadSha = "f89285702ebade4b7fe92e4bfb2f72080c8d72ab"
$packageVersion = "0.1.0-preview.2"
$packageName = "BiscuitSharp.$packageVersion.nupkg"
$expectedPackageSha256 = "c5f0c94aa14cf82b68239ed49314a3cf468780337432cdff89975beed6ead3b1"

$scriptRootPath = [IO.Path]::GetFullPath($PSScriptRoot)
$repoRootPath = [IO.Path]::GetFullPath((Join-Path $scriptRootPath ".."))
$feedPath = Join-Path (Join-Path $repoRootPath "artifacts") "biscuitsharp-feed"
$packagePath = Join-Path $feedPath $packageName
$downloadDirectoryPath = $null
$downloadDirectoryCreated = $false

try {
    if (Test-Path -LiteralPath $packagePath) {
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            Fail "the expected package path exists but is not a file: $packagePath"
        }

        $localPackageSha256 = Get-Sha256 $packagePath
        if ($localPackageSha256 -ne $expectedPackageSha256) {
            Fail "existing candidate package hash mismatch; refusing to reuse or overwrite it"
        }

        Write-Output "Verified existing unpublished BiscuitSharp candidate: $packageName ($localPackageSha256)."
        return
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail "GitHub CLI 'gh' is required to restore the missing pinned artifact"
    }

    $runOutput = & gh run view $runId --repo $sourceRepo --json status,conclusion,headSha 2>$null
    $ghExitCode = $LASTEXITCODE
    if ($ghExitCode -ne 0) {
        Fail "could not inspect pinned CI run $runId"
    }

    try {
        $runInfo = ($runOutput -join "`n") | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Fail "GitHub CLI returned invalid run metadata :: $($_.Exception.Message)"
    }

    if ($runInfo.status -ne "completed" -or $runInfo.conclusion -ne "success" -or
        -not [string]::Equals([string]$runInfo.headSha, $expectedHeadSha, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "pinned CI run is not completed successfully at the expected source commit"
    }

    $tempRootPath = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $downloadDirectoryName = "hufu-biscuit-candidate-" + [Guid]::NewGuid().ToString("N")
    $downloadDirectoryPath = Join-Path $tempRootPath $downloadDirectoryName
    New-Item -ItemType Directory -Path $downloadDirectoryPath | Out-Null
    $downloadDirectoryCreated = $true

    & gh run download $runId --repo $sourceRepo --name nupkg --dir $downloadDirectoryPath 2>$null
    $ghExitCode = $LASTEXITCODE
    if ($ghExitCode -ne 0) {
        Fail "could not download the package artifact from pinned run $runId"
    }

    $downloadedPackages = @(
        Get-ChildItem -LiteralPath $downloadDirectoryPath -Filter "BiscuitSharp.*.nupkg" -File -Recurse
    )
    if ($downloadedPackages.Count -ne 1) {
        Fail "pinned artifact must contain exactly $packageName"
    }
    if ($downloadedPackages[0].Name -cne $packageName) {
        Fail "pinned artifact must contain exactly $packageName"
    }

    $downloadedPackagePath = $downloadedPackages[0].FullName
    $downloadedPackageSha256 = Get-Sha256 $downloadedPackagePath
    if ($downloadedPackageSha256 -ne $expectedPackageSha256) {
        Fail "downloaded candidate package hash mismatch; feed was not modified"
    }

    if (-not (Test-Path -LiteralPath $feedPath -PathType Container)) {
        New-Item -ItemType Directory -Path $feedPath | Out-Null
    }
    if (Test-Path -LiteralPath $packagePath) {
        Fail "candidate package appeared in the feed during restore; refusing to overwrite it"
    }

    [IO.File]::Copy($downloadedPackagePath, $packagePath, $false)
    $copiedPackageSha256 = Get-Sha256 $packagePath
    if ($copiedPackageSha256 -ne $expectedPackageSha256) {
        Fail "copied candidate package hash mismatch"
    }

    Write-Output "Restored pinned unpublished BiscuitSharp candidate: $packageName ($copiedPackageSha256)."
}
finally {
    if ($downloadDirectoryCreated) {
        $resolvedDownloadPath = [IO.Path]::GetFullPath($downloadDirectoryPath)
        $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        $resolvedParentPath = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($resolvedDownloadPath)).TrimEnd('\', '/')
        $resolvedDownloadName = [IO.Path]::GetFileName($resolvedDownloadPath)
        $downloadDirectoryItem = Get-Item -LiteralPath $resolvedDownloadPath -ErrorAction SilentlyContinue
        $downloadNameIsGuid = [regex]::IsMatch($resolvedDownloadName, '^hufu-biscuit-candidate-[0-9a-f]{32}$')
        if (-not [string]::Equals($resolvedParentPath, $resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($resolvedDownloadName, $downloadDirectoryName, [StringComparison]::Ordinal) -or
            -not $downloadNameIsGuid -or $null -eq $downloadDirectoryItem -or
            -not $downloadDirectoryItem.PSIsContainer -or
            ($downloadDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Fail "refusing cleanup outside the verified unique candidate temp directory"
        }

        Remove-Item -LiteralPath $resolvedDownloadPath -Recurse -Force
    }
}

