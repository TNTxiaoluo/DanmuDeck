#requires -Version 7.0
<#
.SYNOPSIS
Regresses long-comment focus paint crossing into the fixed comment composer.
.DESCRIPTION
Compiles the actual production viewport-intersection function unchanged. Only the
WinRT Rect value type is substituted; these are geometry tests, not a native Xbox
controller, XAML focus-template, keyboard, or scrolling integration test.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.CommentNavigationTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$sources = @(
    (Join-Path $repoRoot 'src\BiliXbox\Controls\FocusViewportClip.cs'),
    (Join-Path $PSScriptRoot 'tests\FocusViewportClip.Stubs.cs'),
    (Join-Path $PSScriptRoot 'tests\FocusViewportClip.Tests.cs')
)
Add-Type -Path $sources -CompilerOptions '/langversion:7.3'
[BiliXbox.CommentNavigationTests.Tests]::Run()
