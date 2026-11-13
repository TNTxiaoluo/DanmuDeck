#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI.Xaml;
'@ + [Environment]::NewLine
foreach ($taskFile in @('src\BiliXbox\Models\ApiModels.cs', 'src\BiliXbox\Services\DownloadSpeedMeter.cs',
    'src\BiliXbox\Controls\PlaybackRecoveryState.cs', 'src\BiliXbox\Controls\PlaybackPipelineRecoveryState.cs',
    'src\BiliXbox\MainPage.PlaybackTransport.cs', 'tools\tests\PlaybackTransportIntegration.Tests.cs')) {
    $taskSource += [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '') + [Environment]::NewLine
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.PlaybackTransportTests.Tests]::Run()
