#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory, [Parameter(Mandatory)][string]$Version)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$package = Join-Path $directory "Penghou.Hufu.$Version.nupkg"
$symbol = Join-Path $directory "Penghou.Hufu.$Version.snupkg"
$verify = Join-Path $PSScriptRoot 'Verify-HufuPackageSymbols.ps1'
& $verify -PackagePath $package -SymbolPath $symbol | Out-Null
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('hufu-symbol-test-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testDirectory)
foreach ($fault in @('wrong-framework', 'extra-pdb', 'missing-pdb', 'altered-pdb')) {
    $bad = Join-Path $testDirectory "$fault.snupkg"
    $input = [IO.Compression.ZipFile]::OpenRead($symbol)
    $output = [IO.Compression.ZipFile]::Open($bad, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $input.Entries) {
            if ($fault -eq 'missing-pdb' -and $entry.FullName -eq 'lib/net8.0/Penghou.Hufu.pdb') { continue }
            $source = if ($fault -eq 'wrong-framework' -and $entry.FullName -eq 'lib/net8.0/Penghou.Hufu.pdb') { $input.GetEntry('lib/net10.0/Penghou.Hufu.pdb') } else { $entry }
            $from = $source.Open(); $to = $output.CreateEntry($entry.FullName).Open()
            try {
                if ($fault -eq 'altered-pdb' -and $entry.FullName -eq 'lib/net8.0/Penghou.Hufu.pdb') {
                    $buffer = [IO.MemoryStream]::new()
                    try {
                        $from.CopyTo($buffer)
                        $bytes = $buffer.ToArray()
                        $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
                        $to.Write($bytes, 0, $bytes.Length)
                    }
                    finally { $buffer.Dispose() }
                }
                else { $from.CopyTo($to) }
            }
            finally { $from.Dispose(); $to.Dispose() }
        }
        if ($fault -eq 'extra-pdb') {
            $from = $input.GetEntry('lib/net8.0/Penghou.Hufu.pdb').Open()
            $to = $output.CreateEntry('lib/net8.0/NotInPackage.pdb').Open()
            try { $from.CopyTo($to) } finally { $from.Dispose(); $to.Dispose() }
        }
    }
    finally { $input.Dispose(); $output.Dispose() }
    $rejected = $false
    try { & $verify -PackagePath $package -SymbolPath $bad | Out-Null }
    catch {
        $expected = switch ($fault) {
            'wrong-framework' { 'Portable PDB identity does not match DLL:' }
            'extra-pdb' { 'Symbols package has no corresponding DLL:' }
            'missing-pdb' { 'Missing corresponding portable PDB:' }
            'altered-pdb' { 'Portable PDB checksum does not match DLL:' }
        }
        if (-not $_.Exception.Message.StartsWith($expected, [StringComparison]::Ordinal)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Invalid symbols were accepted: $fault" }
}
Write-Output 'Symbol validation accepts matching bytes and rejects wrong-framework, extra, missing and altered PDBs.'
