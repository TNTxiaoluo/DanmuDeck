#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.RouteSelectorTests.Tests' -as [type]) { throw 'Run this test in a fresh PowerShell process.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSource = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Services\PlaybackRouteSelector.cs'))
$taskSource += [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\PlaybackRouteSelector.Stubs.cs'))
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.RouteSelectorTests.Tests]::Run().GetAwaiter().GetResult()
