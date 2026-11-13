#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.IO;
using System.Globalization;
using Windows.Media.Core;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using BiliXbox.Services;
'@ + [Environment]::NewLine
foreach ($taskFile in @('src\BiliXbox\Services\AdaptivePlaybackSource.cs',
    'src\BiliXbox\Services\DashRoutePolicy.cs', 'src\BiliXbox\Services\DownloadSpeedMeter.cs',
    'tools\tests\AdaptivePlaybackSource.Tests.cs')) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '') + [Environment]::NewLine
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.AdaptiveSourceTests.Tests]::Run().GetAwaiter().GetResult()
