#requires -Version 7.0
<#
.SYNOPSIS
Checks rich comment metadata, trusted images, bounded emoji sizing and primary action routing.
.DESCRIPTION
Compiles the production models and presentation helper. No network, account, content write or UWP registration is used.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.CommentRichPresentationTests.Tests' -as [type]) {
    throw 'Run in a fresh PowerShell process to compile the current production source.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$sources = @(
    (Join-Path $repoRoot 'src\BiliXbox\Models\ApiModels.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\CommentRichPresentation.cs'),
    (Join-Path $PSScriptRoot 'tests\CommentRichPresentation.Tests.cs')
)
Add-Type -Path $sources -CompilerOptions '/langversion:7.3'
[BiliXbox.CommentRichPresentationTests.Tests]::Run()
