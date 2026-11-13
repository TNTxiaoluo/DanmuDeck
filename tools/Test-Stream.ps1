#requires -Version 7.0
<#
.SYNOPSIS
Runs HttpRandomAccessStream behavioral tests against test-only WinRT/HTTP stubs.
.DESCRIPTION
Compiles the actual production C# file in memory with deterministic HTTP doubles.
Does not build the UWP application, use native WinRT, or contact Bilibili.
Run in a fresh PowerShell 7 process so previous dynamic types cannot hide edits:
  pwsh -NoProfile -File .\tools\Test-Stream.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'Run this test with PowerShell 7 (pwsh), which includes the C# compiler.'
}
if ('BiliXbox.StreamTests.Tests' -as [type]) {
    throw 'Test types are already loaded. Run pwsh -NoProfile -File .\tools\Test-Stream.ps1 in a fresh process.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$servicePath = Join-Path $repoRoot 'src\BiliXbox\Services\HttpRandomAccessStream.cs'
$fixturePath = Join-Path $PSScriptRoot 'tests\HttpRandomAccessStream.Stubs.cs'
foreach ($path in @($servicePath, $fixturePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required test input is missing: $path"
    }
}

Write-Host 'Testing the actual HTTP stream source with WinRT/HTTP stubs (not a UWP build).'
$source = [IO.File]::ReadAllText($servicePath)
$source += [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $repoRoot 'src\BiliXbox\Services\DownloadSpeedMeter.cs'))
$fixture = [IO.File]::ReadAllText($fixturePath)
$fixture += [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\StreamConcurrency.Tests.cs'))
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $fixture) `
    -Language CSharp -CompilerOptions '/langversion:7.3' -ErrorAction Stop

[BiliXbox.StreamTests.Tests]::Run().GetAwaiter().GetResult()
[BiliXbox.StreamTests.ConcurrencyTests]::Run().GetAwaiter().GetResult()
