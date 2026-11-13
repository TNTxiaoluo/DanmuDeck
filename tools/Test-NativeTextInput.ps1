#requires -Version 7.0
<#
.SYNOPSIS
Checks the production native text dialog's lifecycle and competing back events.
.DESCRIPTION
Compiles NativeTextInputDialog unchanged with small UWP event and focus stubs.
Covers cancellation before the queued show, fresh editors after B, duplicate back
delivery, explicit close, native-only content and API fallback. This does not run
the Xbox keyboard, validate native IME submission, or prove actual device focus.
Run in a fresh PowerShell process.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.Controls.NativeTextInputDialog' -as [type]) {
    throw 'Run this script in a fresh PowerShell process.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = [IO.File]::ReadAllText((Join-Path $repoRoot 'src\BiliXbox\Controls\NativeTextInputDialog.cs'))
$stubs = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\NativeTextInput.Stubs.cs'))
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $stubs) -CompilerOptions '/langversion:7.3'
[BiliXbox.NativeTextInputTests.Tests]::Run()
