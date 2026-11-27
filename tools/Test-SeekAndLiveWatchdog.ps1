#requires -Version 7.0
<#
.SYNOPSIS
Tests accelerated seeking steps and the live playback expiry/stall watchdog.
.DESCRIPTION
Compiles unchanged SeekAccelerationState and LivePlaybackWatchdog with deterministic
clocks. This does not simulate Xbox input timing or a real live CDN; verify on a device.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.SeekAndLiveWatchdogTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile the current production source.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$paths = @(
    (Join-Path $repoRoot 'src\BiliXbox\Controls\SeekAccelerationState.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\LivePlaybackWatchdog.cs'),
    (Join-Path $PSScriptRoot 'tests\SeekAndLiveWatchdog.Tests.cs')
)
$usingPattern = '(?m)^using [A-Za-z0-9_.]+;\s*$'
$texts = @($paths | ForEach-Object { [IO.File]::ReadAllText($_) })
$usings = @($texts | ForEach-Object { [regex]::Matches($_, $usingPattern) | ForEach-Object { $_.Value.Trim() } } | Sort-Object -Unique)
$bodies = @($texts | ForEach-Object { [regex]::Replace($_, $usingPattern, '') })
$source = ($usings + $bodies) -join [Environment]::NewLine
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.SeekAndLiveWatchdogTests.Tests]::Run()
