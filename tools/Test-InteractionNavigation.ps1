#requires -Version 7.0
<#
.SYNOPSIS
Tests the unchanged production interaction-page orchestration and key dispatcher.
.DESCRIPTION
External XAML, authentication and view substitutes expose request ownership and
async navigation races. No real account login, UI input or POST is performed.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.InteractionNavigationTests' -as [type]) { throw 'Use a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using Windows.System;
using Windows.UI.Xaml;
'@
foreach ($taskFile in @('src\BiliXbox\Controls\PlayerUiState.cs', 'src\BiliXbox\MainPage.Interactions.cs')) {
    $taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
$taskCode = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\MainPage.Gamepad.cs'))
$taskMatch = [regex]::Match($taskCode, '(?m)^        private bool DispatchPlayerGamepadKey\([^\r\n]*\)\s*\{')
if (-not $taskMatch.Success) { throw 'Production gamepad dispatch method missing.' }
$taskStart = $taskCode.IndexOf('{', $taskMatch.Index); $taskEnd = $taskStart + 1; $taskDepth = 1
while ($taskEnd -lt $taskCode.Length -and $taskDepth -gt 0) {
    if ($taskCode[$taskEnd] -eq '{') { $taskDepth++ }
    elseif ($taskCode[$taskEnd] -eq '}') { $taskDepth-- }
    $taskEnd++
}
if ($taskDepth -ne 0) { throw 'Unbalanced dispatch method.' }
$taskDispatch = $taskCode.Substring($taskMatch.Index, $taskEnd - $taskMatch.Index)
$taskHarness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\InteractionNavigation.Tests.cs'))
$taskSource += [Environment]::NewLine + $taskHarness.Replace('/* PRODUCTION_DISPATCH */', $taskDispatch)
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.InteractionNavigationTests]::Run().GetAwaiter().GetResult()
