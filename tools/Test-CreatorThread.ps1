#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskMethods = ''
foreach ($taskDefinition in @(
    @('CreatorHubView.cs', 'private string TargetKey'),
    @('CreatorHubView.cs', 'private void SelectDraft'),
    @('CreatorHubView.cs', 'private async Task PostCommentAsync'),
    @('CreatorHubView.Comments.cs', 'private void ResetReplyTarget'),
    @('CreatorHubView.Comments.cs', 'private bool SelectReplyTarget'),
    @('CreatorHubView.Comments.cs', 'private void ActivateCommentCard'),
    @('CreatorHubView.Comments.cs', 'private void EnterCommentActions'),
    @('CreatorHubView.Comments.cs', 'private void ClearCommentActions'),
    @('CreatorHubView.Comments.cs', 'private bool LeaveCommentActions'),
    @('CreatorHubView.Comments.cs', 'private int NextCommentRow')
)) {
    $taskCode = [IO.File]::ReadAllText((Join-Path $taskRoot ('src\BiliXbox\Controls\' + $taskDefinition[0])))
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
$taskState = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\CreatorCommentDraftState.cs'))
$taskState = $taskState.Replace('namespace BiliXbox.Controls', 'namespace BiliXbox.CreatorThreadTests')
$taskHarness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\CreatorThread.Tests.cs'))
Add-Type -TypeDefinition ($taskHarness.Replace('/* PRODUCTION_METHODS */', $taskMethods) + [Environment]::NewLine + $taskState.Replace('using System;', '')) -CompilerOptions '/langversion:7.3'
[BiliXbox.CreatorThreadTests.CreatorHubView]::Run().GetAwaiter().GetResult()
