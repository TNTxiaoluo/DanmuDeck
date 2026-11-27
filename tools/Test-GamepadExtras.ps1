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
using System.Globalization;
using Windows.Gaming.Input;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
'@
foreach ($taskFile in @('src\BiliXbox\Controls\ShoulderChordState.cs', 'src\BiliXbox\Controls\GamepadAuxiliaryState.cs', 'src\BiliXbox\Controls\FeedNavigationState.cs',
    'src\BiliXbox\Controls\PlayerUiState.cs', 'src\BiliXbox\Controls\SeekAccelerationState.cs', 'src\BiliXbox\Controls\MediaTimeFormatter.cs',
    'src\BiliXbox\MainPage.SeekAcceleration.cs', 'src\BiliXbox\MainPage.GamepadExtras.cs', 'tools\tests\GamepadExtras.Tests.cs')) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [Environment]::NewLine + [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '')
}
$chords = [IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/MainPage.ViewingShortcuts.cs'))
$poll = [regex]::Match($chords, '(?s)        private void PollViewingChord\(.*?\r?\n        \}')
if (-not $poll.Success) { throw 'Production chord polling method missing.' }
$taskSource = $taskSource.Replace('/* PRODUCTION_CHORD */', $poll.Value)
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414,0067'
[BiliXbox.MainPage]::RunExtrasTests()
