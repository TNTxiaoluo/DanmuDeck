#requires -Version 7.0
<#
.SYNOPSIS
Exercises the production creator video load, focus and focus-restore methods.
.DESCRIPTION
Controlled network/layout providers expose asynchronous replacement races. No
account, API request, native XAML scrolling or physical gamepad is used.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.CreatorPagingTests.CreatorHubView' -as [type]) { throw 'Use a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskMethods = ''
foreach ($taskDefinition in @(
    @('src\BiliXbox\Controls\CreatorHubView.cs', 'private async Task LoadVideosAsync'),
    @('src\BiliXbox\Controls\CreatorHubView.cs', 'public void RestoreFocus'),
    @('src\BiliXbox\Controls\CreatorHubView.Navigation.cs', 'private async void FocusItem')
)) {
    $taskCode = [IO.File]::ReadAllText((Join-Path $taskRoot $taskDefinition[0]))
    $taskMatch = [regex]::Match($taskCode, '(?m)^        ' + [regex]::Escape($taskDefinition[1]) + '\([^\r\n]*\)\s*\{')
    if (-not $taskMatch.Success) { throw ('Missing production method: ' + $taskDefinition[1]) }
    $taskStart = $taskCode.IndexOf('{', $taskMatch.Index)
    $taskEnd = $taskStart + 1; $taskDepth = 1
    while ($taskEnd -lt $taskCode.Length -and $taskDepth -gt 0) {
        if ($taskCode[$taskEnd] -eq '{') { $taskDepth++ }
        elseif ($taskCode[$taskEnd] -eq '}') { $taskDepth-- }
        $taskEnd++
    }
    if ($taskDepth -ne 0) { throw 'Unbalanced production method.' }
    $taskMethods += $taskCode.Substring($taskMatch.Index, $taskEnd - $taskMatch.Index) + [Environment]::NewLine
}
$taskHarness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\CreatorPagingFocus.Tests.cs'))
Add-Type -TypeDefinition $taskHarness.Replace('/* PRODUCTION_METHODS */', $taskMethods) -CompilerOptions '/langversion:7.3'
[BiliXbox.CreatorPagingTests.CreatorHubView]::Run().GetAwaiter().GetResult()
