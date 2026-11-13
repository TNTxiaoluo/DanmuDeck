#requires -Version 7.0
<#
.SYNOPSIS
Checks the production full-screen comment-image navigation and B handoff state.
.DESCRIPTION
Compiles the actual state source unchanged. No pictures, account requests, XAML,
native controller input, or UI screenshots are simulated by these state checks.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.CommentImageNavigationTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
Add-Type -Path @(
    (Join-Path $taskRoot 'src\BiliXbox\Controls\CommentImageNavigationState.cs'),
    (Join-Path $taskRoot 'tools\tests\CommentImageNavigation.Tests.cs')
) -CompilerOptions '/langversion:7.3'
[BiliXbox.CommentImageNavigationTests.Tests]::Run()
