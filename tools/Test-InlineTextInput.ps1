#requires -Version 7.0
# Lifecycle tests compile the production session unchanged. Visual tree replacement
# and native keyboard rendering require the SDK/native UI probe and Xbox testing.
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSource = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\NativeTextInputDialog.cs'))
$taskInline = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\InlineNativeTextInputSession.cs'))
$taskInline = [regex]::Replace($taskInline, '(?m)^using [^;]+;\r?\n', '')
$taskSource += [Environment]::NewLine + $taskInline
$taskSource += [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\NativeTextInput.Stubs.cs'))
$taskSource += [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\InlineTextInput.Tests.cs'))
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.InlineInputTests.Tests]::Run()
