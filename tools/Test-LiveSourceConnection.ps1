#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.LiveSourceConnectionTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskProduction = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\LiveHubView.Playback.cs'))
$taskStart = $taskProduction.IndexOf('internal static class LiveSourceConnectionGate', [StringComparison]::Ordinal)
if ($taskStart -lt 0) { throw 'Production connection gate missing.' }
$taskBrace = $taskProduction.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
for (; $taskEnd -lt $taskProduction.Length; $taskEnd++) {
    if ($taskProduction[$taskEnd] -eq '{') { $taskDepth++ }
    elseif ($taskProduction[$taskEnd] -eq '}') { $taskDepth--; if ($taskDepth -eq 0) { break } }
}
$taskGate = $taskProduction.Substring($taskStart, $taskEnd - $taskStart + 1)
$taskSource = @'
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Controls;
'@
$taskSource += "`nnamespace BiliXbox.Controls { $taskGate }`n"
$taskSource += [IO.File]::ReadAllText((Join-Path $taskRoot 'tools\tests\LiveSourceConnection.Tests.cs'))
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.LiveSourceConnectionTests.Tests]::Run().GetAwaiter().GetResult() | Out-Null
