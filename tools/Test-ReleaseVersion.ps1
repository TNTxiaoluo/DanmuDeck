#requires -Version 7.0
# Offline tests for display channels, startup-safe version lookup, and numeric upgrade planning.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$checks = 0
function Assert-Equal($Actual, $Expected) {
    if ([string]$Actual -cne [string]$Expected) { throw "Expected '$Expected', got '$Actual'." }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected invalid version request to be rejected.' }
    $script:checks++
}
Assert-Equal (Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0') '1.0.2.0'
Assert-Equal (Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0' -PreviousVersions '1.0.7.0') '1.0.8.0'
Assert-Equal (Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0' -PreviousVersions @('1.0.8.0', '1.0.6.0')) '1.0.9.0'
Assert-Equal (Get-DevelopmentPackageVersion -ReleasePackageVersion '2.0.0.0' -PreviousVersions '1.0.8.0') '2.0.0.0'
Assert-Equal (Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0' -PreviousVersions '1.0.7.0' -RequestedVersion '1.0.7.1') '1.0.7.1'
Assert-Equal (Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0' -PreviousVersions '1.0.65535.3') '1.1.0.0'
Assert-Equal (Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0' -PreviousVersions '1.65535.65535.0') '2.0.0.0'
foreach ($invalid in @('1.0.2b', '1.0.2', '0.0.1.0', '1.65536.0.0', '-1.0.2.0', '1.0.2.65536')) {
    Assert-Rejected { ConvertTo-PackageVersion $invalid }
}
Assert-Rejected { Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0' -PreviousVersions '1.0.7.0' -RequestedVersion '1.0.7.0' }
Assert-Rejected { Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0' -RequestedVersion '1.0.1.9' }
Assert-Rejected { Get-DevelopmentPackageVersion -ReleasePackageVersion '1.0.2.0' -PreviousVersions '65535.65535.65535.0' }
$appVersion = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/BiliXbox/Services/AppVersionInfo.cs'))
$stubs = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/AppVersionInfo.Stubs.cs'))
Add-Type -TypeDefinition ($appVersion + [Environment]::NewLine + $stubs) -CompilerOptions '/langversion:7.3'
$checks += [BiliXbox.VersionTests.Tests]::Run()
Write-Output "PASS: $checks release/test display, numeric upgrade, and startup fallback checks."
