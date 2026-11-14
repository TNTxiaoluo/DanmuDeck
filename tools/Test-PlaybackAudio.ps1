#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlaybackAudioTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
# Compile the real playback service, PGC path and DASH builder with the existing
# in-memory HTTP/account fixtures. No live credentials, network or writes.
$taskHarness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-AccountLibrary.ps1'))
$taskHarness = $taskHarness.Replace('$PSScriptRoot', ("'" + $PSScriptRoot.Replace("'", "''") + "'"))
$taskHarness = $taskHarness.Replace('tests\AccountLibrary.Stubs.cs', 'tests\PlaybackAudio.Stubs.cs')
$taskHarness = $taskHarness.Replace('BiliXbox.AccountLibraryTests.Tests', 'BiliXbox.PlaybackAudioTests.Tests')
$taskHarness = $taskHarness.Substring(0, $taskHarness.IndexOf('if ($PublicSeasonResponsePath)', [StringComparison]::Ordinal))
Invoke-Expression $taskHarness
