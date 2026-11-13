#requires -Version 7.0
<#
.SYNOPSIS
Tests bounded native pipeline recovery using deterministic media/buffer clocks.
.DESCRIPTION
Compiles the real policy unchanged. No network, sleep, UWP device, account writes,
route switches or output-mode changes are used. Run in a fresh PowerShell process.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlaybackPipelineRecoveryTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$productionPath = Join-Path $repoRoot 'src\BiliXbox\Controls\PlaybackPipelineRecoveryState.cs'
$testPath = Join-Path $PSScriptRoot 'tests\PlaybackPipelineRecovery.Tests.cs'
Add-Type -TypeDefinition ([IO.File]::ReadAllText($productionPath) + [Environment]::NewLine +
    [IO.File]::ReadAllText($testPath)) -CompilerOptions '/langversion:7.3'
[BiliXbox.PlaybackPipelineRecoveryTests.Tests]::Run()
