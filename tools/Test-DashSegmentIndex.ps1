#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = [IO.File]::ReadAllText((Join-Path $root 'src/BiliXbox/Services/DashSegmentIndex.cs'))
$source += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/DashSegmentIndex.Tests.cs'))
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.Services.DashSegmentIndexTests]::Run()
