#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Reuse the established no-network production API compilation fixture.
$taskHarness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-VideoInteractions.ps1'))
$taskHarness = $taskHarness.Replace("'src\BiliXbox\Services\BilibiliClient.Interactions.cs'", "'src\BiliXbox\Services\BilibiliClient.Sections.cs'")
$taskHarness = $taskHarness.Replace('BiliXbox.VideoInteractionTests.Tests', 'BiliXbox.SectionTests.Tests')
$taskHarness = $taskHarness.Replace('tests\VideoInteractions.Tests.cs', 'tests\Sections.Tests.cs')
$taskHarness = $taskHarness.Replace('$PSScriptRoot', ("'" + $PSScriptRoot.Replace("'", "''") + "'"))
Invoke-Expression $taskHarness
