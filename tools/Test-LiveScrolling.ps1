#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.Controls.LiveHubView' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskParts = @(
    [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\LiveHubView.Scrolling.cs'))
    [IO.File]::ReadAllText((Join-Path $taskRoot 'tools\tests\LiveScrolling.Tests.cs'))
)
$taskNavigation = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\LiveHubView.Navigation.cs'))
$taskStart = $taskNavigation.IndexOf('private void NavigateDrawer(', [StringComparison]::Ordinal)
if ($taskStart -lt 0) { throw 'Production drawer navigation is missing.' }
$taskBrace = $taskNavigation.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
for (; $taskEnd -lt $taskNavigation.Length; $taskEnd++) {
    if ($taskNavigation[$taskEnd] -eq '{') { $taskDepth++ }
    elseif ($taskNavigation[$taskEnd] -eq '}') { $taskDepth--; if ($taskDepth -eq 0) { break } }
}
$taskParts += 'namespace BiliXbox.Controls { public sealed partial class LiveHubView { ' + $taskNavigation.Substring($taskStart, $taskEnd - $taskStart + 1) + ' } }'
$taskUsings = [Collections.Generic.HashSet[string]]::new()
$taskBodies = foreach ($taskPart in $taskParts) {
    foreach ($taskUsing in [regex]::Matches($taskPart, '(?m)^using [^;]+;')) { [void]$taskUsings.Add($taskUsing.Value) }
    [regex]::Replace($taskPart, '(?m)^using [^;]+;\r?\n', '')
}
Add-Type -TypeDefinition (($taskUsings -join "`n") + "`n" + ($taskBodies -join "`n")) -CompilerOptions '/langversion:7.3', '/nowarn:0649'
[BiliXbox.Controls.LiveHubView]::RunScrollingTests()
