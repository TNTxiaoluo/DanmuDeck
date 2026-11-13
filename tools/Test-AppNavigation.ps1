#requires -Version 7.0
<#
.SYNOPSIS
Tests the production App startup and resume navigation policy without loading XAML.
.DESCRIPTION
Compiles unchanged App.xaml.cs against small UWP substitutes whose Application
starts in Auto pointer mode. InitializeComponent is deliberately a no-op, so the
tests establish that startup code actually applies the policy before navigation.
This checks application lifecycle behavior, not the Xbox shell or device input.
Navigation failures exercise the production App catch/retry path and unhandled
event reporting against diagnostic/page substitutes; native API timing and actual
diagnostic file persistence require separate runtime verification.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.AppNavigationTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile the current production source.'
}
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskProduction = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\App.xaml.cs'))
$taskSource = $taskProduction + [Environment]::NewLine +
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\AppNavigation.Stubs.cs')) + [Environment]::NewLine +
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\AppNavigation.Tests.cs'))
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.AppNavigationTests.Tests]::Run()
