#requires -Version 7.0
<#
.SYNOPSIS
Checks that comment vote readbacks stay within their comment area and current account.
.DESCRIPTION
Compiles the production model and vote presentation helper. Exercises duplicate root/preview rows,
unknown write invalidation, account switching and logout. No API calls or real votes are performed.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.CommentVotePresentationTests.Tests' -as [type]) {
    throw 'Run in a fresh PowerShell process to compile the current production source.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
Add-Type -Path @(
    (Join-Path $repoRoot 'src\BiliXbox\Models\ApiModels.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\CommentVotePresentation.cs'),
    (Join-Path $PSScriptRoot 'tests\CommentVotePresentation.Tests.cs')
) -CompilerOptions '/langversion:7.3'
[BiliXbox.CommentVotePresentationTests.Tests]::Run()
