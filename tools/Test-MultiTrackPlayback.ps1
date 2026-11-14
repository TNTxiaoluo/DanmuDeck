#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.MultiTrackPlaybackTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
# Production UGC/PGC/DASH/capability code, with existing in-memory transport.
$taskHarness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-AccountLibrary.ps1'))
$taskHarness = $taskHarness.Replace('$PSScriptRoot', ("'" + $PSScriptRoot.Replace("'", "''") + "'"))
$taskHarness = $taskHarness.Replace('tests\AccountLibrary.Stubs.cs', 'tests\MultiTrackPlayback.Stubs.cs')
$taskHarness = $taskHarness.Replace('BiliXbox.AccountLibraryTests.Tests', 'BiliXbox.MultiTrackPlaybackTests.Tests')
$taskHarness = $taskHarness.Substring(0, $taskHarness.IndexOf('if ($PublicSeasonResponsePath)', [StringComparison]::Ordinal))
Invoke-Expression $taskHarness
