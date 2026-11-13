#requires -Version 7.0
<#
.SYNOPSIS
Tests production left-stick thresholds, axis hysteresis, and D-pad priority.
.DESCRIPTION
Compiles the unchanged mapper, sampler, and player state using deterministic input
sequences. Xbox physical input, native text entry, and focus require device QA.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.GamepadDirectionTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile current source.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$paths = @(
    (Join-Path $repoRoot 'src\BiliXbox\Controls\PlayerInputState.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\GamepadInputState.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\GamepadDirectionMapper.cs'),
    (Join-Path $PSScriptRoot 'tests\GamepadDirection.Tests.cs')
)
Add-Type -Path $paths -CompilerOptions '/langversion:7.3'
[BiliXbox.GamepadDirectionTests.Tests]::Run()
