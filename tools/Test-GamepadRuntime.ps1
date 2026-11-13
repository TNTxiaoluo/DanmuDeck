#requires -Version 7.0
<#
.SYNOPSIS
Enumerates and polls actual Windows Gaming Input devices without input injection.
.DESCRIPTION
Compiles a separate console probe against the installed Windows SDK. It does not
install the app, change device settings, or use simulated gamepads. No connected
device is reported as a hardware-test skip, not successful Xbox validation.
#>
[CmdletBinding()]
param([ValidateRange(1, 10)][int] $Seconds = 3)

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
$compiler = Join-Path $repoRoot '.tools\vs2022\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The repository Windows build tools are required.' }
$outputDirectory = Join-Path $repoRoot 'artifacts\gamepad-runtime-probe'
$null = New-Item -ItemType Directory -Path $outputDirectory -Force
$executable = Join-Path $outputDirectory 'GamepadRuntime.exe'
$arguments = @('/nologo', '/target:exe', '/langversion:7.3', '/platform:x64', '/optimize+', "/out:$executable", "/reference:$metadata")
foreach ($assembly in @('System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll', 'System.Runtime.dll')) {
    $arguments += "/reference:$(Join-Path $framework $assembly)"
}
$arguments += Join-Path $PSScriptRoot 'tests\GamepadRuntime.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Gamepad runtime probe compilation failed: $LASTEXITCODE" }
& $executable ([string] $Seconds)
if ($LASTEXITCODE -ne 0) { throw "Actual Windows Gamepad API probe failed: $LASTEXITCODE" }
Write-Host 'PASS: Windows Gamepad API enumeration probe completed; see the hardware status above.'
