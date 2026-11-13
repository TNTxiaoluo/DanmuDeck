#requires -Version 7.0
<#
.SYNOPSIS
Tests search, recommendation navigation, pagination, and stale feed request rejection.
.DESCRIPTION
Compiles the unchanged production FeedNavigationState with deterministic navigation sequences.
No live Bilibili API, native keyboard, Xbox input, or UWP focus is involved.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.FeedNavigationTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile the current production source.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sources = @(
    (Join-Path $repoRoot 'src\BiliXbox\Controls\FeedNavigationState.cs'),
    (Join-Path $PSScriptRoot 'tests\FeedNavigation.Tests.cs')
)
Add-Type -Path $sources -CompilerOptions '/langversion:7.3'
[BiliXbox.FeedNavigationTests.Tests]::Run()
