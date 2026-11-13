#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.LiveFocusTests.LiveHubView' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskMethods = ''
foreach ($taskDefinition in @(
    @('src\BiliXbox\Controls\LiveHubView.Navigation.cs', 'private async void FocusRoom'),
    @('src\BiliXbox\Controls\LiveHubView.Actions.cs', 'private async Task EditAsync')
)) {
    $taskSource = [IO.File]::ReadAllText((Join-Path $taskRoot $taskDefinition[0]))
    $taskMatch = [regex]::Match($taskSource, '(?m)^        ' + [regex]::Escape($taskDefinition[1]) + '\([^\r\n]*\)\s*\{')
    if (-not $taskMatch.Success) { throw "Missing production method: $($taskDefinition[1])" }
    $taskStart = $taskSource.IndexOf('{', $taskMatch.Index); $taskEnd = $taskStart + 1; $taskDepth = 1
    while ($taskEnd -lt $taskSource.Length -and $taskDepth -gt 0) {
        if ($taskSource[$taskEnd] -eq '{') { $taskDepth++ }
        elseif ($taskSource[$taskEnd] -eq '}') { $taskDepth-- }
        $taskEnd++
    }
    $taskMethods += $taskSource.Substring($taskMatch.Index, $taskEnd - $taskMatch.Index) + [Environment]::NewLine
}
$taskHarness = [IO.File]::ReadAllText((Join-Path $taskRoot 'tools\tests\LiveFocus.Tests.cs'))
Add-Type -TypeDefinition $taskHarness.Replace('/* PRODUCTION_METHODS */', $taskMethods) -CompilerOptions '/langversion:7.3', '/nowarn:0649'
[BiliXbox.LiveFocusTests.LiveHubView]::Run().GetAwaiter().GetResult() | Out-Null
