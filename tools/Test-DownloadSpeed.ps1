#requires -Version 7.0
<#
.SYNOPSIS
Tests actual-byte throughput accounting with a deterministic monotonic clock.
.DESCRIPTION
Tests startup, stalled-network expiry, restart, concurrent audio/video reads and
formatting. No encoded bitrate, device/network test, or wall-clock sleep is used.
MP4 cache/clone behavior is also checked by Test-Stream.ps1; Test-DashRuntime.ps1
-Decode verifies that real Media Foundation downloads pass through the counter.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.DownloadSpeedTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$productionPath = Join-Path $repoRoot 'src\BiliXbox\Services\DownloadSpeedMeter.cs'
$testPath = Join-Path $PSScriptRoot 'tests\DownloadSpeed.Tests.cs'
Add-Type -TypeDefinition ([IO.File]::ReadAllText($productionPath) + [Environment]::NewLine +
    [IO.File]::ReadAllText($testPath)) -CompilerOptions '/langversion:7.3'
[BiliXbox.DownloadSpeedTests.Tests]::Run()
