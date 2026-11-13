#requires -Version 7.0
<#
.SYNOPSIS
Exercises production creator profile ownership and refresh orchestration.
.DESCRIPTION
Extracts the current production methods and runs controlled task providers.
No XAML runtime, network request, saved account or production method copy is used.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.CreatorProfileRefreshTests.CreatorHubView' -as [type]) { throw 'Use a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskCode = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\CreatorHubView.cs'))
$taskMethods = ''
foreach ($taskSignature in @(
    'private void EnsureSession',
    'private async Task LoadProfileAsync',
    'private Task RefreshAsync',
    'private void UpdateActions'
)) {
    $taskMatch = [regex]::Match($taskCode, '(?m)^        ' + [regex]::Escape($taskSignature) + '\([^\r\n]*\)\s*\{')
    if (-not $taskMatch.Success) { throw ('Missing production method: ' + $taskSignature) }
    $taskStart = $taskCode.IndexOf('{', $taskMatch.Index)
    $taskEnd = $taskStart + 1
    $taskDepth = 1
    while ($taskEnd -lt $taskCode.Length -and $taskDepth -gt 0) {
        if ($taskCode[$taskEnd] -eq '{') { $taskDepth++ }
        elseif ($taskCode[$taskEnd] -eq '}') { $taskDepth-- }
        $taskEnd++
    }
    if ($taskDepth -ne 0) { throw ('Unbalanced production method: ' + $taskSignature) }
    $taskMethods += $taskCode.Substring($taskMatch.Index, $taskEnd - $taskMatch.Index) + [Environment]::NewLine
}
foreach ($taskSignature in @('private bool IsDetail', 'private bool Current(', 'private static string Friendly(')) {
    $taskMatch = [regex]::Match($taskCode, '(?m)^        ' + [regex]::Escape($taskSignature) + '[^\r\n]+$')
    if (-not $taskMatch.Success) { throw ('Missing production expression: ' + $taskSignature) }
    $taskMethods += $taskMatch.Value + [Environment]::NewLine
}
$taskHarness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\CreatorProfileRefresh.Tests.cs'))
Add-Type -TypeDefinition $taskHarness.Replace('/* PRODUCTION_METHODS */', $taskMethods) -CompilerOptions '/langversion:7.3'
[BiliXbox.CreatorProfileRefreshTests.CreatorHubView]::Run().GetAwaiter().GetResult()
