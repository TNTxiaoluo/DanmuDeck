#requires -Version 7.0
<#
.SYNOPSIS
Tests the bounded automatic playback source recovery policy.
.DESCRIPTION
Compiles the production policy unchanged and supplies deterministic wall and
media times. No network, sleep, UWP media player or Xbox hardware is used.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlaybackRecoveryTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$productionPath = Join-Path $repoRoot 'src\BiliXbox\Controls\PlaybackRecoveryState.cs'
$testPath = Join-Path $PSScriptRoot 'tests\PlaybackRecovery.Tests.cs'
Add-Type -TypeDefinition ([IO.File]::ReadAllText($productionPath) + [Environment]::NewLine +
    [IO.File]::ReadAllText($testPath)) -CompilerOptions '/langversion:7.3'
[BiliXbox.PlaybackRecoveryTests.Tests]::Run()
