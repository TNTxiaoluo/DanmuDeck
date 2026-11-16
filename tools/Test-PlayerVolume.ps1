#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = [IO.File]::ReadAllText((Join-Path $root 'src/BiliXbox/MainPage.Volume.cs')) + [Environment]::NewLine +
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/PlayerVolume.Tests.cs'))
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.MainPage]::RunVolumeTests()
