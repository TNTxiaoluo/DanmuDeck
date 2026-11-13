#requires -Version 7.0
<#
.SYNOPSIS
Uses actual Windows AdaptiveMediaSource APIs to parse a generated local DASH MPD.
.DESCRIPTION
Compiles a .NET Framework console probe against the installed Windows SDK WinMD.
Unlike the logic tests, it calls the real Windows media stack. -Decode additionally
downloads public audio/video segments and requires MediaOpened plus an advancing
playback clock. It does not install the app or change any machine configuration.
This is a desktop Windows check; Xbox focus, rendering, and decoding still require
device testing. The local manifest can contain signed URLs and is not printed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ManifestPath,
    [switch] $Decode,
    [ValidateRange(5, 60)][int] $TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
$metadata = Get-ChildItem -LiteralPath $sdkRoot -Directory |
    Where-Object Name -Match '^10\.' | Sort-Object { [version] $_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } | Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $metadata) { throw 'An installed Windows SDK Windows.winmd is required.' }
$manifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$outputDirectory = Join-Path $repoRoot 'artifacts\runtime-probe'
$null = New-Item -ItemType Directory -Path $outputDirectory -Force
$executable = Join-Path $outputDirectory 'DashRuntime.exe'
$source = Join-Path $PSScriptRoot 'tests\DashRuntime.cs'
# The constants-only dependency must use the exact production request headers.
$apiSource = [IO.File]::ReadAllText((Join-Path $repoRoot 'src\BiliXbox\Services\BilibiliClient.cs'))
$probeSource = [IO.File]::ReadAllText($source)
foreach ($name in @('Referer', 'UserAgent')) {
    $productionValue = [regex]::Match($apiSource, "const string $name = ([^;]+);").Groups[1].Value
    $probeValue = [regex]::Match($probeSource, "const string $name = ([^;]+);").Groups[1].Value
    if (-not $productionValue -or $productionValue -cne $probeValue) { throw "Runtime probe $name differs from the production header." }
}
$compiler = Join-Path $repoRoot '.tools\vs2022\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The repository Windows build tools are required to compile the production helper.' }
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', "/out:$executable", "/reference:$metadata")
foreach ($assembly in @('System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll', 'System.Runtime.dll', 'System.Threading.Tasks.dll', 'System.Xml.Linq.dll')) {
    $arguments += "/reference:$(Join-Path $framework $assembly)"
}
$arguments += $source
$arguments += Join-Path $repoRoot 'tools\tests\DashTransportRuntime.cs'
$arguments += Join-Path $repoRoot 'src\BiliXbox\Services\AdaptivePlaybackSource.cs'
$arguments += Join-Path $repoRoot 'src\BiliXbox\Services\DashRoutePolicy.cs'
$arguments += Join-Path $repoRoot 'src\BiliXbox\Services\DownloadSpeedMeter.cs'
$arguments += Join-Path $repoRoot 'src\BiliXbox\Services\PlaybackCodecSupport.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Runtime probe compilation failed: $LASTEXITCODE" }
$runtimeArguments = @($manifest, [string] $TimeoutSeconds)
if ($Decode) { $runtimeArguments += 'decode' }
& $executable @runtimeArguments
if ($LASTEXITCODE -ne 0) { throw "Actual Windows DASH runtime probe failed: $LASTEXITCODE" }
Write-Host 'PASS: real Windows AdaptiveMediaSource runtime probe.'
