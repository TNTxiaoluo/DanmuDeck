#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.LiveUiTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Controls;
'@
foreach ($taskFile in @('src\BiliXbox\Models\LiveModels.cs','src\BiliXbox\Controls\LiveWriteGate.cs','tools\tests\LiveUi.Tests.cs')) {
    $taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
# Compile the mutation orchestration from the real control, rather than a
# second implementation. The surrounding shell below only supplies UI/API fakes.
$taskActions = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\LiveHubView.Actions.cs'))
foreach ($taskSignature in @('private async Task SendChatAsync()', 'private async Task PrepareGiftAsync()', 'private async Task SendGiftAsync()')) {
    $taskStart = $taskActions.IndexOf($taskSignature, [StringComparison]::Ordinal)
    if ($taskStart -lt 0) { throw "Production method missing: $taskSignature" }
    $taskBrace = $taskActions.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
    for (; $taskEnd -lt $taskActions.Length; $taskEnd++) {
        if ($taskActions[$taskEnd] -eq '{') { $taskDepth++ }
        elseif ($taskActions[$taskEnd] -eq '}') { $taskDepth--; if ($taskDepth -eq 0) { break } }
    }
    $taskMethod = $taskActions.Substring($taskStart, $taskEnd - $taskStart + 1)
    $taskSource += "`nnamespace BiliXbox.Controls { public sealed partial class LiveHubView { $taskMethod } }`n"
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649'
[BiliXbox.LiveUiTests.Tests]::Run().GetAwaiter().GetResult() | Out-Null
