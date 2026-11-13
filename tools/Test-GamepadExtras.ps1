#requires -Version 7.0
<#
Compiles the complete production auxiliary gamepad routing partial and its real
feed/player/input policies. External UWP views, viewport operations and section
loading are controlled substitutes; no native UI, network or account is used.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.MainPage' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BiliXbox.Controls;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
'@
foreach ($taskFile in @('src\BiliXbox\Controls\GamepadAuxiliaryState.cs', 'src\BiliXbox\Controls\FeedNavigationState.cs',
    'src\BiliXbox\Controls\PlayerUiState.cs', 'src\BiliXbox\MainPage.GamepadExtras.cs', 'tools\tests\GamepadExtras.Tests.cs')) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [Environment]::NewLine + [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '')
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.MainPage]::RunExtrasTests()
