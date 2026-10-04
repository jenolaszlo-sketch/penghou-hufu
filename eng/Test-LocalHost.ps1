#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The local host profile requires Windows NTFS.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'samples/Hufu.LocalHost/Hufu.LocalHost.csproj'
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Local host build failed.' }
$profile = [IO.Path]::GetFullPath([Environment]::GetFolderPath('UserProfile'))
$evidence = [Collections.Generic.List[object]]::new()
foreach ($framework in @('net8.0-windows', 'net10.0-windows')) {
    $root = Join-Path $profile ('Hufu.LocalHost.Qualification-' + [Guid]::NewGuid().ToString('N'))
    $assembly = Join-Path $repo "samples/Hufu.LocalHost/bin/Release/$framework/Hufu.LocalHost.dll"
    function Invoke-HostCommand([string[]]$Arguments, [string]$InputText = '') {
        $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add($assembly)
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        try {
            if (-not $process.Start()) { throw 'Host process did not start.' }
            $output = $process.StandardOutput.ReadToEndAsync()
            $errors = $process.StandardError.ReadToEndAsync()
            $bytes = [Text.Encoding]::UTF8.GetBytes($InputText)
            $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Host command exceeded 60 seconds.' }
            $text = $output.GetAwaiter().GetResult()
            $errorText = $errors.GetAwaiter().GetResult()
            if ([string]::IsNullOrWhiteSpace($text) -or -not [string]::IsNullOrWhiteSpace($errorText)) {
                throw 'Host command failed without a valid typed result.'
            }
            [pscustomobject]@{ ExitCode = $process.ExitCode; Result = ($text | ConvertFrom-Json) }
        }
        finally { $process.Dispose() }
    }
    function Require-Status($Result, [string]$Expected, [int]$ExitCode = 0) {
        if ($Result.ExitCode -ne $ExitCode -or $Result.Result.Status -ne $Expected) {
            throw "Host command did not return the expected $Expected outcome."
        }
    }
    try {
        Require-Status (Invoke-HostCommand @('init', $root, 'docs/file.txt') 'old') 'Initialized'
        $preview = Invoke-HostCommand @('preview', $root, 'cli-operation', '0', '3') 'new'
        Require-Status $preview 'Prepared'
        $unapproved = Invoke-HostCommand @('apply', $root, 'cli-operation')
        if ($unapproved.ExitCode -ne 2 -or $unapproved.Result.Status -eq 'Succeeded') { throw 'Unapproved patch was not denied.' }
        $target = Join-Path $root 'workspace/docs/file.txt'
        if ([IO.File]::ReadAllText($target) -ne 'old') { throw 'Denied patch changed the file.' }
        $review = Invoke-HostCommand @('review', $root, 'cli-operation')
        if ($review.ExitCode -ne 0 -or $review.Result.Facts.Status -ne 'Reviewed' -or
            $review.Result.BeforeUtf8 -ne 'old' -or $review.Result.AfterUtf8 -ne 'new') {
            throw 'Complete fixture review failed.'
        }
        Require-Status (Invoke-HostCommand @('approve', $root, 'cli-operation', $preview.Result.AdmissionIdentity)) 'Recorded'
        Require-Status (Invoke-HostCommand @('apply', $root, 'cli-operation')) 'Succeeded'
        if ([IO.File]::ReadAllText($target) -ne 'new') { throw 'Approved patch did not reach the expected bytes.' }
        Require-Status (Invoke-HostCommand @('inspect', $root, 'cli-operation')) 'Completed'
        Require-Status (Invoke-HostCommand @('apply', $root, 'cli-operation')) 'AlreadyRecorded' 2
        Require-Status (Invoke-HostCommand @('revoke', $root)) 'Applied'
        $evidence.Add([ordered]@{Framework=$framework;Status='passed';SeparateProcesses=9;
            UnapprovedWriteDenied=$true;CompleteReviewVerified=$true;DurableCompleted=$true;ReplayNeverRedispatched=$true})
    }
    finally {
        $full = [IO.Path]::GetFullPath($root)
        $name = [IO.Path]::GetFileName($full)
        $scopeId = [Guid]::Empty
        if ([IO.Path]::GetDirectoryName($full) -ne $profile -or -not $name.StartsWith('Hufu.LocalHost.Qualification-', [StringComparison]::Ordinal) -or
            -not [Guid]::TryParseExact($name.Substring('Hufu.LocalHost.Qualification-'.Length), 'N', [ref]$scopeId)) {
            throw 'Refusing cleanup outside the generated qualification scope.'
        }
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
}
$directory = Join-Path $repo 'qualification/local-host'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
ConvertTo-Json -InputObject ([ordered]@{SchemaVersion=1;Status='passed';Profile='Windows local operator, NTFS';
    PublishedHufuVersion='0.1.0-preview.3';Runs=@($evidence.ToArray())}) -Depth 8 |
    Set-Content -LiteralPath (Join-Path $directory 'qualification.json')
Write-Output 'Local host separate-process approval, write, durable recovery and replay qualification passed on .NET 8/10.'
