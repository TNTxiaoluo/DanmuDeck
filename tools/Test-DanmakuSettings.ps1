#requires -Version 7.0
<#
.SYNOPSIS
Tests settings persistence, cloud rule filtering, layout bounds, and renderer budgets.
.DESCRIPTION
Compiles production settings/store/filter/overlay against small storage and UI
stubs. These tests verify logic and operation counts, not Xbox frame rate or masks.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ('BiliXbox.DanmakuSettingsTests.Tests' -as [type]) { throw 'Run in a fresh pwsh process.' }
$paths = @(
    'src\BiliXbox\Models\ApiModels.cs',
    'src\BiliXbox\Models\DanmakuSettings.cs',
    'src\BiliXbox\Models\DanmakuBlockRule.cs',
    'src\BiliXbox\Services\DanmakuSettingsStore.cs',
    'src\BiliXbox\Controls\DanmakuFilter.cs',
    'src\BiliXbox\Controls\DanmakuOverlay.cs',
    'tools\tests\DanmakuOverlay.Stubs.cs',
    'tools\tests\DanmakuSettings.Stubs.cs',
    'tools\tests\DanmakuSettings.Tests.cs'
) | ForEach-Object { Join-Path $repoRoot $_ }
Add-Type -Path $paths -CompilerOptions '/langversion:7.3'
[BiliXbox.DanmakuSettingsTests.Tests]::Run()
