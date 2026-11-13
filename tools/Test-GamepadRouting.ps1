#requires -Version 7.0
<#
.SYNOPSIS
Tests mapped Xbox key classification with production gamepad and player states.
.DESCRIPTION
Compiles the real GamepadKeyRouting, GamepadInputState, and PlayerInputState.
Only the WinRT event/key types are stubs. Deterministic sequences simulate a raw
owner also observing CoreWindow and mapped XAML events; this is not a native UWP
event-routing, focus, dialog, or Xbox hardware integration test. Input gating is
tested through the sampler's real enabled argument, not duplicated UI conditions.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.GamepadRoutingTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile current source.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$paths = @(
    (Join-Path $repoRoot 'src\BiliXbox\Controls\PlayerInputState.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\GamepadInputState.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\GamepadKeyRouting.cs'),
    (Join-Path $PSScriptRoot 'tests\GamepadRouting.Stubs.cs'),
    (Join-Path $PSScriptRoot 'tests\GamepadRouting.Tests.cs')
)
Add-Type -Path $paths -CompilerOptions '/langversion:7.3'
[BiliXbox.GamepadRoutingTests.Tests]::Run()
