#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $directory 'package-inspection.json') -Raw | ConvertFrom-Json
if ($manifest.Status -ne 'passed') { throw 'Package qualification must pass before checking public packages.' }
$downloads = Join-Path ([IO.Path]::GetTempPath()) ('hufu-public-identity-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $downloads)
$existing = @()
function Entry-Hash($entry) {
    $stream = $entry.Open()
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}
foreach ($item in $manifest.Packages) {
    $local = Join-Path $directory "$($item.Id).$($item.Version).nupkg"
    $symbol = Join-Path $directory "$($item.Id).$($item.Version).snupkg"
    if ((Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash -cne $item.Sha256 -or
        (Get-FileHash -LiteralPath $symbol -Algorithm SHA256).Hash -cne $item.SymbolSha256) { throw "Validated artifact hash changed: $($item.Id)" }
    $id = $item.Id.ToLowerInvariant(); $version = $item.Version.ToLowerInvariant()
    $public = Join-Path $downloads "$id.$version.nupkg"
    try { Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile $public }
    catch {
        if ($null -ne $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { continue }
        throw
    }
    $candidate = [IO.Compression.ZipFile]::OpenRead($local)
    $published = [IO.Compression.ZipFile]::OpenRead($public)
    try {
        $candidateEntries = @($candidate.Entries | Where-Object FullName -CNE '.signature.p7s')
        $publishedEntries = @($published.Entries | Where-Object FullName -CNE '.signature.p7s')
        if ($candidateEntries.Count -ne $publishedEntries.Count) { throw "Published version has different package contents: $($item.Id)/$($item.Version). Recover original artifacts or bump the version." }
        foreach ($entry in $candidateEntries) {
            $match = $published.GetEntry($entry.FullName)
            if ($null -eq $match -or (Entry-Hash $entry) -cne (Entry-Hash $match)) {
                throw "Published version has different package contents: $($item.Id)/$($item.Version)/$($entry.FullName). Recover original artifacts or bump the version."
            }
        }
    }
    finally { $candidate.Dispose(); $published.Dispose() }
    & (Join-Path $PSScriptRoot 'Verify-HufuPackageSymbols.ps1') -PackagePath $public -SymbolPath $symbol | Out-Null
    $existing += $item.Id
}
[pscustomobject]@{ Status = 'passed'; ExistingPackages = @($existing); PublicContentsAndSymbolsMatch = $true }
