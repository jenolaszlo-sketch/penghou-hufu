[CmdletBinding()]
param(
    [ValidateSet('net8.0','net10.0')][string]$Framework = 'net10.0',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $repo "artifacts/biscuit-budget-$Framework.json" }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
& dotnet run --project (Join-Path $repo 'tools/BiscuitBudgetProbe/BiscuitBudgetProbe.csproj') -c Release -f $Framework -- $OutputPath
if ($LASTEXITCODE -ne 0) { throw "Biscuit budget probe failed with exit $LASTEXITCODE" }
