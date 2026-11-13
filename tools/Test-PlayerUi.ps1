#requires -Version 7.0
<#
.SYNOPSIS
Tests the production player's overlay and drawer state without the Windows SDK.
.DESCRIPTION
Compiles the unchanged PlayerUiState source and a harness in the same assembly.
All times are deterministic; no real timers, media, keyboard, UWP focus, or Xbox
device is used. Run each invocation in a fresh PowerShell process.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlayerUiTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile the current production source.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$productionPath = Join-Path $repoRoot 'src\BiliXbox\Controls\PlayerUiState.cs'
$testPath = Join-Path $PSScriptRoot 'tests\PlayerUi.Tests.cs'
$source = [IO.File]::ReadAllText($productionPath) + [Environment]::NewLine +
    [IO.File]::ReadAllText($testPath)
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.PlayerUiTests.Tests]::Run()
