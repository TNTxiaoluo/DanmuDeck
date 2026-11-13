param([string]$ProjectRoot = (Join-Path $PSScriptRoot '..\src\BiliXbox'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Generate-BrandAssets.ps1') -ProjectRoot $ProjectRoot
