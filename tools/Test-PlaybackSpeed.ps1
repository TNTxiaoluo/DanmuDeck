#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskProduction=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/MainPage.PlaybackSpeed.cs'))
$taskSource=@'
using System;
using System.Collections.Generic;
using System.Globalization;
using BiliXbox.Controls;
using BiliXbox.Services;
using Windows.Gaming.Input;
using Windows.Storage;
using Windows.System;
'@
$taskSource+=[Environment]::NewLine+[regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/PlaybackSpeedState.cs')),'(?m)^using [^;]+;\r?\n','')
$taskSource+=[Environment]::NewLine+[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/PlaybackSpeed.Tests.cs'))
$taskStart=$taskProduction.IndexOf('private bool CanHoldPlaybackSpeed =>');$taskEnd=$taskProduction.IndexOf('private void InitializePlaybackSpeed()')
$taskSource+="`nnamespace BiliXbox { public sealed partial class MainPage { "+$taskProduction.Substring($taskStart,$taskEnd-$taskStart)+" } }"
foreach($taskSignature in @('private void InitializePlaybackSpeed()','private void UpdatePlaybackSpeedLabel()','private void ApplyPlaybackSpeed()','private void ResetTemporaryPlaybackSpeed()','private void PollPlaybackSpeed(GamepadReading reading, bool enabled)','private bool HandlePlaybackSpeedKey(VirtualKey key, bool down)')){
    $taskStart=$taskProduction.IndexOf($taskSignature,[StringComparison]::Ordinal);if($taskStart -lt 0){throw "Missing $taskSignature"}
    $taskBrace=$taskProduction.IndexOf('{',$taskStart);$taskEnd=$taskBrace+1;$taskDepth=1
    while($taskDepth -gt 0){if($taskProduction[$taskEnd] -eq '{'){$taskDepth++};if($taskProduction[$taskEnd] -eq '}'){$taskDepth--};$taskEnd++}
    $taskSource+="`nnamespace BiliXbox { public sealed partial class MainPage { "+$taskProduction.Substring($taskStart,$taskEnd-$taskStart)+" } }"
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3','/nowarn:0649,0414'
[BiliXbox.MainPage]::RunSpeedTests()
