#requires -Version 7.0
<#
.SYNOPSIS
Tests the production Xbox HDMI output controller with deterministic WinRT doubles.
.DESCRIPTION
No display modes are changed and no Xbox hardware result is fabricated. Exercises
mode selection and asynchronous cancellation/reset ownership, including failed OS
requests and cleanup. Compile as C# 7.3 just like the application.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlaybackDisplayTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display.Core;
using Windows.System.Profile;
'@
foreach ($taskFile in @('src\BiliXbox\Models\ApiModels.cs', 'src\BiliXbox\Services\PlaybackDisplayController.cs',
    'tools\tests\PlaybackDisplay.Tests.cs')) {
    $taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.PlaybackDisplayTests.Tests]::Run().GetAwaiter().GetResult()
