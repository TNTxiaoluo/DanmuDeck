#requires -Version 7.0
<#
.SYNOPSIS
Tests the complete production MainPage.AccountLibrary.cs orchestration unchanged.
.DESCRIPTION
Uses real account/video models and small external UWP, control, media and API
substitutes. Tests invoke the production private methods, not a second navigation
implementation. The external OpenVideoAsync substitute records navigation only;
fixtures explicitly establish playback state before testing return behavior.
No actual XAML, raw B-button dispatch, HTTP account data or logout is exercised.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.AccountNavigationTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = 'using System.Collections.Generic;' + [Environment]::NewLine +
    [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\MainPage.AccountLibrary.cs'))
foreach ($taskFile in @('src\BiliXbox\Models\ApiModels.cs', 'src\BiliXbox\Models\AccountLibraryModels.cs',
    'tools\tests\AccountNavigation.Stubs.cs', 'tools\tests\AccountNavigation.Tests.cs')) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [Environment]::NewLine + [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '')
}
# Fixture fields are populated/read via reflection because production methods
# and bookmark types remain private. Suppress only those test-field warnings.
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.AccountNavigationTests.Tests]::Run().GetAwaiter().GetResult()
