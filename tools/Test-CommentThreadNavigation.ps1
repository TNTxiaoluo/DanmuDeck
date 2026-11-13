#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.MainPage' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskSource = @'
using System;
using System.Collections.Generic;
using BiliXbox.Controls;
using BiliXbox.Models;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
'@
foreach ($taskFile in @('src\BiliXbox\Controls\CommentReadingScroll.cs', 'tools\tests\CommentThreadNavigation.Tests.cs')) {
    $taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
$taskProduction = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\MainPage.CommentsNavigation.cs'))
foreach ($taskSignature in @('private bool TryOpenCommentComposer()', 'private void RememberCommentComposerPosition(Control control)',
    'private void RestoreCommentComposerPosition()', 'private bool NavigateCommentDrawerControls(Control current, PlayerDirection direction)',
    'private bool ReadExpandedComment(Control row, PlayerDirection direction)', 'private bool ScrollReplyThread(double delta)',
    'private void CommentList_ItemClick(object sender, ItemClickEventArgs args)', 'private bool TryActivateCommentControl(Control control)',
    'private void EnterCommentActions(CommentCard card, Control returnTo)', 'private bool ExitCommentActions()',
    'private void CommentImage_Requested(object sender, CommentImageRequestedEventArgs args)',
    'private void SelectReplyComposerTarget(CommentItem item)')) {
    $taskStart = $taskProduction.IndexOf($taskSignature, [StringComparison]::Ordinal)
    if ($taskStart -lt 0) { throw "Missing production method: $taskSignature" }
    $taskBrace = $taskProduction.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
    for (; $taskEnd -lt $taskProduction.Length; $taskEnd++) {
        if ($taskProduction[$taskEnd] -eq '{') { $taskDepth++ }
        elseif ($taskProduction[$taskEnd] -eq '}') { $taskDepth--; if ($taskDepth -eq 0) { break } }
    }
    $taskSource += "`nnamespace BiliXbox { public sealed partial class MainPage { " + $taskProduction.Substring($taskStart, $taskEnd - $taskStart + 1) + " } }`n"
}
$taskDispatcher = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\MainPage.Gamepad.cs'))
$taskStart = $taskDispatcher.IndexOf('private void ActivateGamepadDrawerControl(Control control)', [StringComparison]::Ordinal)
if ($taskStart -lt 0) { throw 'Missing production drawer activation dispatcher.' }
$taskBrace = $taskDispatcher.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
for (; $taskEnd -lt $taskDispatcher.Length; $taskEnd++) {
    if ($taskDispatcher[$taskEnd] -eq '{') { $taskDepth++ }
    elseif ($taskDispatcher[$taskEnd] -eq '}') { $taskDepth--; if ($taskDepth -eq 0) { break } }
}
$taskSource += "`nnamespace BiliXbox { public sealed partial class MainPage { " + $taskDispatcher.Substring($taskStart, $taskEnd - $taskStart + 1) + " } }`n"
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.MainPage]::RunThreadNavigationTests()
