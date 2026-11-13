#requires -Version 7.0
<#
.SYNOPSIS
Tests production physical controller sampling, repeat timing, and input lifecycle.
.DESCRIPTION
Compiles unchanged GamepadInputState and PlayerInputState with deterministic raw
controller snapshots. Actual Xbox controller enumeration and focus need device QA.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.GamepadInputTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile current source.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$paths = @(
    (Join-Path $repoRoot 'src\BiliXbox\Controls\PlayerInputState.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\GamepadInputState.cs'),
    (Join-Path $PSScriptRoot 'tests\GamepadInput.Tests.cs')
)
Add-Type -Path $paths -CompilerOptions '/langversion:7.3'
[BiliXbox.GamepadInputTests.Tests]::Run()
