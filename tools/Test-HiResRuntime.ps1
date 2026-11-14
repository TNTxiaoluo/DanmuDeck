#requires -Version 7.0
# Calls real Windows APIs with a muted, local authorized FLAC-in-MP4 sample.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SamplePath)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$taskSdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/UnionMetadata'
$taskMetadata = Get-ChildItem -LiteralPath $taskSdkRoot -Directory |
    Where-Object Name -Match '^10\.' | Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $taskMetadata) { throw 'An installed Windows SDK Windows.winmd is required.' }
$taskCompiler = Join-Path $taskRoot '.tools/vs2022/MSBuild/Current/Bin/Roslyn/csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Repository Windows build tools are required.' }
$taskOutput = Join-Path $taskRoot 'artifacts/runtime-probe'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskExe = Join-Path $taskOutput 'HiResRuntime.exe'
$taskArguments = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', "/out:$taskExe", "/reference:$taskMetadata")
foreach ($taskAssembly in @('System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll', 'System.Runtime.dll', 'System.Threading.Tasks.dll')) {
    $taskArguments += '/reference:' + (Join-Path $taskFramework $taskAssembly)
}
$taskArguments += Join-Path $PSScriptRoot 'tests/HiResRuntime.cs'
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw ('Native audio probe compilation failed: ' + $LASTEXITCODE) }
& $taskExe (Resolve-Path -LiteralPath $SamplePath).Path
if ($LASTEXITCODE -ne 0) { throw ('Native audio probe failed: ' + $LASTEXITCODE) }
