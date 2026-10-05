#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$SymbolPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Read-EntryBytes($entry) {
    $input = $entry.Open()
    $output = [IO.MemoryStream]::new()
    try { $input.CopyTo($output); return ,$output.ToArray() }
    finally { $input.Dispose(); $output.Dispose() }
}
$package = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
$symbols = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $SymbolPath).Path)
try {
    $pdbs = @($symbols.Entries | Where-Object FullName -Like '*.pdb')
    if ($pdbs.Count -eq 0) { throw 'Symbols package contains no portable PDBs.' }
    foreach ($pdb in $pdbs) {
        $dllPath = $pdb.FullName.Substring(0, $pdb.FullName.Length - 4) + '.dll'
        $dll = $package.GetEntry($dllPath)
        if ($null -eq $dll) { throw "Symbols package has no corresponding DLL: $($pdb.FullName)" }
        [byte[]]$dllBytes = Read-EntryBytes $dll
        [byte[]]$pdbBytes = Read-EntryBytes $pdb
        $peStream = [IO.MemoryStream]::new($dllBytes, $false)
        $pdbStream = [IO.MemoryStream]::new($pdbBytes, $false)
        $pe = $null
        $provider = $null
        try {
            $pe = [Reflection.PortableExecutable.PEReader]::new($peStream)
            $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($pdbStream)
            $reader = $provider.GetMetadataReader()
            [byte[]]$identity = $reader.DebugMetadataHeader.Id
            if ($identity.Length -ne 20) { throw "Invalid portable PDB identity: $($pdb.FullName)" }
            $guid = [Guid]::new([byte[]]$identity[0..15])
            $stamp = [BitConverter]::ToUInt32($identity, 16)
            $entries = @($pe.ReadDebugDirectory())
            $codeViews = @($entries | Where-Object Type -EQ CodeView)
            if ($codeViews.Count -ne 1) { throw "Expected one DLL CodeView identity: $dllPath" }
            $codeView = $pe.ReadCodeViewDebugDirectoryData($codeViews[0])
            if ($codeView.Guid -ne $guid -or $codeViews[0].Stamp -ne $stamp -or $codeView.Age -ne 1) {
                throw "Portable PDB identity does not match DLL: $($pdb.FullName)"
            }
            $checksums = @($entries | Where-Object Type -EQ PdbChecksum)
            if ($checksums.Count -ne 1) { throw "Expected one DLL PDB checksum: $dllPath" }
            $checksum = $pe.ReadPdbChecksumDebugDirectoryData($checksums[0])
            if ($checksum.AlgorithmName -cne 'SHA256') { throw "Unsupported PDB checksum algorithm: $dllPath" }
            # Portable PDB checksum hashes the image with the 20-byte #Pdb identity zeroed.
            # https://github.com/dotnet/runtime/blob/main/docs/design/specs/PE-COFF.md
            [byte[]]$hashImage = $pdbBytes.Clone()
            [Array]::Clear($hashImage, $reader.DebugMetadataHeader.IdStartOffset, 20)
            $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($hashImage))
            $expected = [Convert]::ToHexString([byte[]]$checksum.Checksum)
            if ($actual -cne $expected) { throw "Portable PDB checksum does not match DLL: $($pdb.FullName)" }
        }
        finally {
            if ($null -ne $provider) { $provider.Dispose() }
            if ($null -ne $pe) { $pe.Dispose() }
            $peStream.Dispose(); $pdbStream.Dispose()
        }
    }
    foreach ($dll in @($package.Entries | Where-Object FullName -Like 'lib/*.dll')) {
        $path = $dll.FullName.Substring(0, $dll.FullName.Length - 4) + '.pdb'
        if ($null -eq $symbols.GetEntry($path)) { throw "Missing corresponding portable PDB: $($dll.FullName)" }
    }
    [pscustomobject]@{ Status = 'passed'; PortablePdbCount = $pdbs.Count; IdentitiesAndChecksumsMatch = $true }
}
finally { $package.Dispose(); $symbols.Dispose() }
