#requires -Version 7.0
<#
.SYNOPSIS
Tests production player playback/controls switching, focus navigation, and confirmation delivery.
.DESCRIPTION
Compiles unchanged PlayerInputState with deterministic input sequences. This does
not simulate Xbox hardware input or UWP event order; verify those on a device.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlayerInputTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile the current production source.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$productionPath = Join-Path $repoRoot 'src\BiliXbox\Controls\PlayerInputState.cs'
$testPath = Join-Path $PSScriptRoot 'tests\PlayerInput.Tests.cs'
$source = [IO.File]::ReadAllText($productionPath) + [Environment]::NewLine +
    [IO.File]::ReadAllText($testPath)
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.PlayerInputTests.Tests]::Run()
