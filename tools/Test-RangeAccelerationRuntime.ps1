#requires -Version 7.0
# Real Windows HTTP/IBuffer/IHttpFilter interop with an in-memory media server.
# No external requests, package registration, credentials or display mode changes.
[CmdletBinding()]
param([string]$MediaFixtureDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$metadata = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/UnionMetadata/10.0.19041.0/Windows.winmd'
$output = Join-Path $root 'artifacts/runtime-probe/RangeAccelerationRuntime.exe'
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:7.3', '/optimize+', "/out:$output", "/reference:$metadata")
foreach ($assembly in @('System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll', 'System.Runtime.dll', 'System.Threading.Tasks.dll', 'System.Xml.Linq.dll')) {
    $arguments += '/reference:' + (Join-Path $framework $assembly)
}
foreach ($source in @('ParallelRangeDownloader.cs', 'OrderedRangeReadStream.cs', 'RangeAccelerationFilter.cs', 'DashRoutePolicy.cs', 'DownloadSpeedMeter.cs', 'DashSegmentIndex.cs')) {
    $arguments += Join-Path $root ('src/BiliXbox/Services/' + $source)
}
$arguments += Join-Path $PSScriptRoot 'tests/RangeAccelerationRuntime.cs'
& (Join-Path $root '.tools/vs2022/MSBuild/Current/Bin/Roslyn/csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native Range adapter probe compilation failed.' }
if ($MediaFixtureDirectory) { & $output (Resolve-Path -LiteralPath $MediaFixtureDirectory).Path }
else { & $output }
if ($LASTEXITCODE -ne 0) { throw 'Native Range adapter probe failed.' }
