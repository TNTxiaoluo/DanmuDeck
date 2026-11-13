#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ManifestPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
$taskMetadata = Get-ChildItem -LiteralPath $taskSdk -Directory |
    Where-Object Name -Match '^10\.' | Sort-Object { [version] $_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } | Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $taskMetadata) { throw 'Installed Windows SDK metadata is required.' }
$taskManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$taskOutput = Join-Path $taskRoot 'artifacts\runtime-probe'
$null = New-Item -ItemType Directory -Path $taskOutput -Force
$taskExecutable = Join-Path $taskOutput 'RouteSelectorRuntime.exe'
$taskSource = Join-Path $PSScriptRoot 'tests\PlaybackRouteSelectorRuntime.cs'
$taskApi = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Services\BilibiliClient.cs'))
$taskFixture = [IO.File]::ReadAllText($taskSource)
foreach ($taskName in @('Referer', 'UserAgent')) {
    $taskPattern = 'const\s+string\s+' + $taskName + '\s*=\s*"([^"]+)"'
    $taskExpected = [regex]::Match($taskApi, $taskPattern).Groups[1].Value
    $taskActual = [regex]::Match($taskFixture, $taskPattern).Groups[1].Value
    if (-not $taskExpected -or $taskExpected -cne $taskActual) { throw "Runtime probe $taskName differs from production." }
}
$taskCompiler = Join-Path $taskRoot '.tools\vs2022\MSBuild\Current\Bin\Roslyn\csc.exe'
$taskArguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:7.3', '/optimize+', "/out:$taskExecutable", "/reference:$taskMetadata")
foreach ($taskAssembly in @('System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll', 'System.Runtime.dll', 'System.Threading.Tasks.dll', 'System.Xml.Linq.dll')) {
    $taskArguments += "/reference:$(Join-Path $taskFramework $taskAssembly)"
}
$taskArguments += $taskSource
$taskArguments += Join-Path $taskRoot 'src\BiliXbox\Services\PlaybackRouteSelector.cs'
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Real WinRT route-selector compilation failed.' }
& $taskExecutable $taskManifest
if ($LASTEXITCODE -ne 0) { throw 'Real WinRT route-selector check failed; live signed URLs may need refreshing.' }
