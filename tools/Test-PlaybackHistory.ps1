#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlaybackHistoryTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Reuse the production API fixture compiler and transport, with no real network,
# browser cookies, local account storage or account writes.
$taskHarness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-AccountLibrary.ps1'))
$taskHarness = $taskHarness.Replace('$PSScriptRoot', ("'" + $PSScriptRoot.Replace("'", "''") + "'"))
$taskHarness = $taskHarness.Replace("'src\BiliXbox\Models\ApiModels.cs'", "'src\BiliXbox\Models\ApiModels.cs'`n    'src\BiliXbox\Models\PlaybackHistory.cs'`n    'src\BiliXbox\Services\PlaybackHistoryTracker.cs'`n    'src\BiliXbox\Services\PlaybackHistoryUploadQueue.cs'`n    'src\BiliXbox\Services\BilibiliClient.History.cs'")
$taskHarness = $taskHarness.Replace("'src\BiliXbox\Models\ApiModels.cs'", "'src\BiliXbox\MainPage.PlaybackHistory.cs'`n    'src\BiliXbox\Models\ApiModels.cs'")
$taskHarness = $taskHarness.Replace('using Windows.Web.Http.Filters;', "using Windows.Web.Http.Filters;`nusing System.Diagnostics;`nusing BiliXbox.Services;`nusing Windows.Media.Playback;`nusing Windows.UI.Core;`nusing Windows.UI.Xaml.Controls;")
$taskHarness = $taskHarness.Replace('tests\AccountLibrary.Stubs.cs', 'tests\PlaybackHistory.Stubs.cs')
$taskHarness = $taskHarness.Replace('BiliXbox.AccountLibraryTests.Tests', 'BiliXbox.PlaybackHistoryTests.Tests')
$taskHarness = $taskHarness.Substring(0, $taskHarness.IndexOf('if ($PublicSeasonResponsePath)', [StringComparison]::Ordinal))
# Compile the actual library dirty-state and return-focus methods alongside the
# actual MainPage history integration, so the old empty cache is exercised too.
function Get-HistoryMethod([string]$relative, [string]$signature) {
    $body = [IO.File]::ReadAllText((Join-Path $taskRoot $relative))
    $start = $body.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production method $signature" }
    $brace = $body.IndexOf('{', $start); $depth = 1; $end = $brace + 1
    while ($depth -gt 0 -and $end -lt $body.Length) {
        if ($body[$end] -eq '{') { $depth++ }; if ($body[$end] -eq '}') { $depth-- }; $end++
    }
    return $body.Substring($start, $end - $start)
}
$taskDirty = Get-HistoryMethod 'src/BiliXbox/Controls/AccountLibraryView.cs' 'public void InvalidateHistoryContent()'
$taskRestore = Get-HistoryMethod 'src/BiliXbox/Controls/AccountLibraryView.Navigation.cs' 'public void RestoreFocus()'
$taskHarness = $taskHarness.Replace('$taskReferences =', ('$taskSource = $taskSource.Replace("/* HISTORY_DIRTY_METHOD */", $taskDirty).Replace("/* HISTORY_RESTORE_METHOD */", $taskRestore)' + [Environment]::NewLine + '$taskReferences ='))
Invoke-Expression $taskHarness
