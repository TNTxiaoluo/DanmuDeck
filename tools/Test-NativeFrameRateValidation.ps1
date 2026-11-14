#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Xaml;
'@ + [Environment]::NewLine
foreach ($taskFile in @('src\BiliXbox\Models\ApiModels.cs', 'src\BiliXbox\Services\PlaybackFrameRatePolicy.cs',
    'src\BiliXbox\MainPage.FrameRateValidation.cs',
    'tools\tests\NativeFrameRateValidation.Tests.cs')) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '') + [Environment]::NewLine
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.FrameRateValidationTests.Tests]::Run().GetAwaiter().GetResult()
