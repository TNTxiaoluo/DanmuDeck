#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = [IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Services/PlaybackBufferingPolicy.cs'))
$taskSource += [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/PlaybackBuffering.Tests.cs'))
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.PlaybackBufferingTests.Tests]::Run()
