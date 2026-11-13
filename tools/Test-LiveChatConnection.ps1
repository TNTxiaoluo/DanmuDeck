#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.LiveChatConnectionTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.UI.Xaml.Automation;
'@
foreach ($taskFile in @('src\BiliXbox\Models\LiveModels.cs','tools\tests\LiveChatConnection.Tests.cs')) {
    $taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
$taskProduction = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\LiveHubView.Playback.cs'))
foreach ($taskSignature in @('private async Task ConnectChatAsync(int generation)', 'private void StopChatConnection()')) {
    $taskStart = $taskProduction.IndexOf($taskSignature, [StringComparison]::Ordinal)
    if ($taskStart -lt 0) { throw "Production method missing: $taskSignature" }
    $taskBrace = $taskProduction.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
    for (; $taskEnd -lt $taskProduction.Length; $taskEnd++) {
        if ($taskProduction[$taskEnd] -eq '{') { $taskDepth++ }
        elseif ($taskProduction[$taskEnd] -eq '}') { $taskDepth--; if ($taskDepth -eq 0) { break } }
    }
    $taskMethod = $taskProduction.Substring($taskStart, $taskEnd - $taskStart + 1)
    $taskSource += "`nnamespace BiliXbox.Controls { public sealed partial class LiveHubView { $taskMethod } }`n"
}
foreach ($taskSignature in @('private bool ChatCurrent(', 'private Task ReconnectChatAsync()')) {
    $taskStart = $taskProduction.IndexOf($taskSignature, [StringComparison]::Ordinal)
    if ($taskStart -lt 0) { throw "Production method missing: $taskSignature" }
    $taskEnd = $taskProduction.IndexOf(';', $taskStart)
    $taskMethod = $taskProduction.Substring($taskStart, $taskEnd - $taskStart + 1)
    $taskSource += "`nnamespace BiliXbox.Controls { public sealed partial class LiveHubView { $taskMethod } }`n"
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649'
[BiliXbox.LiveChatConnectionTests.Tests]::Run().GetAwaiter().GetResult() | Out-Null
