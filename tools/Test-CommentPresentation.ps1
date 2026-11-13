#requires -Version 7.0
<#
.SYNOPSIS
Checks comment empty, unavailable, partial, closed, and cursor presentation states.
.DESCRIPTION
Compiles the production models and formatter. No network or UWP UI is involved.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.CommentPresentationTests.Tests' -as [type]) {
    throw 'Run in a fresh PowerShell process to compile the current production source.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$sources = @(
    (Join-Path $repoRoot 'src\BiliXbox\Models\ApiModels.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\CommentPresentation.cs'),
    (Join-Path $PSScriptRoot 'tests\CommentPresentation.Tests.cs')
)
Add-Type -Path $sources -CompilerOptions '/langversion:7.3'
[BiliXbox.CommentPresentationTests.Tests]::Run()
