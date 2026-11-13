#requires -Version 7.0
<#
.SYNOPSIS
Tests the production buffering watchdog with a deterministic media clock.
.DESCRIPTION
Compiles PlaybackHealthState unchanged and feeds explicit wall/media times. No
network, sleep, UWP media player or Xbox hardware is used. Run in a fresh process.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlaybackHealthTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$productionPath = Join-Path $repoRoot 'src\BiliXbox\Controls\PlaybackHealthState.cs'
$testPath = Join-Path $PSScriptRoot 'tests\PlaybackHealth.Tests.cs'
Add-Type -TypeDefinition ([IO.File]::ReadAllText($productionPath) + [Environment]::NewLine +
    [IO.File]::ReadAllText($testPath)) -CompilerOptions '/langversion:7.3'
[BiliXbox.PlaybackHealthTests.Tests]::Run()
