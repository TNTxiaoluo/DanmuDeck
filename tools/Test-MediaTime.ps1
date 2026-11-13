#requires -Version 7.0
<#
.SYNOPSIS
Tests production media timestamp formatting, including native slider converter behavior.
.DESCRIPTION
Compiles the unchanged formatter and converter with the IValueConverter interface shape.
These tests verify display values without creating an actual UWP slider or an Xbox session.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.MediaTimeTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile the current production source.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$sources = @(
    (Join-Path $repoRoot 'src\BiliXbox\Controls\MediaTimeFormatter.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\PlaybackTimeConverter.cs'),
    (Join-Path $PSScriptRoot 'tests\MediaTime.Tests.cs')
)
Add-Type -Path $sources -CompilerOptions '/langversion:7.3'
[BiliXbox.MediaTimeTests.Tests]::Run()
