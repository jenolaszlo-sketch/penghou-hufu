#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projects = @('src/Penghou.Hufu','src/Penghou.Hufu.Cedar','src/Penghou.Hufu.Biscuit',
    'src/Penghou.Hufu.Workflow','tests/Penghou.Hufu.Tests','tests/Penghou.Hufu.IO.Tests',
    'tests/Penghou.Hufu.Workflow.Tests')
$graph = foreach ($project in $projects) {
    $assetsPath = Join-Path $repo "$project/obj/project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) { throw "Restore the full solution before checking independence: $project" }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $libraries = @($assets.libraries.PSObject.Properties.Name)
    if ($libraries | Where-Object { $_ -match 'Penghou\.Zhinu|Penghou\.Hufu\.Zhinu' }) { throw "Zhinu leaked into independent graph: $project" }
    [ordered]@{Project=$project;Libraries=$libraries;NoZhinuDependencies=$true}
}
$directory = Join-Path $repo 'qualification/independent-graph'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
ConvertTo-Json -InputObject ([ordered]@{SchemaVersion=1;Projects=@($graph);Status='passed'}) -Depth 8 |
    Set-Content -LiteralPath (Join-Path $directory 'qualification.json')
Write-Output 'Core, Cedar, Biscuit, Workflow and independent test graphs contain no direct/transitive Zhinu dependency.'
