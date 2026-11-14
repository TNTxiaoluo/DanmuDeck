# Requires PowerShell 7 and Visual Studio UWP build tools. Creates a signed Xbox test ZIP.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$MSBuildPath,
    [string]$Version,
    [string]$OutputDirectory,
    [string]$DistributionPath = (Join-Path $PSScriptRoot '..\artifacts\BiliXbox-Xbox-Test.zip'),
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$workspacePrefix = $workspace + [IO.Path]::DirectorySeparatorChar
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace "artifacts\AppPackages\$Configuration" }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $workspace $OutputDirectory }
$packageOutput = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
if (-not $packageOutput.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be a subdirectory of this workspace.'
}

function Assert-NoReparsePath {
    param([string]$Path)
    $current = $Path
    while ($current -and $current.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Symbolic link/junction paths are not supported for the package output: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

Assert-NoReparsePath $packageOutput
if (-not [IO.Path]::IsPathRooted($DistributionPath)) { $DistributionPath = Join-Path $workspace $DistributionPath }
$DistributionPath = [IO.Path]::GetFullPath($DistributionPath)
if (-not $DistributionPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DistributionPath must be inside this workspace.'
}
Assert-NoReparsePath $DistributionPath
Add-Type -AssemblyName System.IO.Compression
[xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $workspace 'src\BiliXbox\Package.appxmanifest') -Raw
if ($sourceManifest.Package.Identity.Name -ne 'BiliXbox.Community' -or
    $sourceManifest.Package.Identity.Publisher -ne 'CN=BiliXboxDev') {
    throw 'Test builds require the development identity; do not use the Store identity for sideload test packages.'
}
$releaseQuad = ConvertTo-PackageVersion ([string]$sourceManifest.Package.Identity.Version)
$displayVersion = '{0}.{1}.{2}b' -f $releaseQuad.Major, $releaseQuad.Minor, $releaseQuad.Build
$statePath = Join-Path $workspace 'artifacts\development-version.json'
Assert-NoReparsePath $statePath
$previousVersions = @()
if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.IdentityName -ne [string]$sourceManifest.Package.Identity.Name -or
        $state.IdentityPublisher -ne [string]$sourceManifest.Package.Identity.Publisher) {
        throw 'Development-version state belongs to a different package identity.'
    }
    $previousVersions += [string]$state.PackageVersion
}
# Import versions from the previous canonical signed test packages on this machine.
# Future successful builds are tracked in the ignored state file regardless of output location.
$legacyDirectory = Join-Path $workspace 'artifacts\DeveloperTest'
Assert-NoReparsePath $legacyDirectory
if (Test-Path -LiteralPath $legacyDirectory -PathType Container) {
    foreach ($legacy in Get-ChildItem -LiteralPath $legacyDirectory -File -Filter '*.appx') {
        Assert-NoReparsePath $legacy.FullName
        $archive = [IO.Compression.ZipFile]::OpenRead($legacy.FullName)
        try {
            $entry = $archive.GetEntry('AppxManifest.xml')
            if (-not $entry -or $entry.Length -gt 2MB -or -not $archive.GetEntry('AppxSignature.p7x')) { continue }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [xml]$legacyManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($legacyManifest.Package.Identity.Name -eq [string]$sourceManifest.Package.Identity.Name -and
                $legacyManifest.Package.Identity.Publisher -eq [string]$sourceManifest.Package.Identity.Publisher) {
                $previousVersions += [string]$legacyManifest.Package.Identity.Version
            }
        } finally { $archive.Dispose() }
    }
}
$testQuad = Get-DevelopmentPackageVersion -ReleasePackageVersion $releaseQuad.ToString() -PreviousVersions $previousVersions -RequestedVersion $Version
$runDirectory = Join-Path $packageOutput ('build-' + [Guid]::NewGuid().ToString('N'))
Assert-NoReparsePath $runDirectory
$sourceProject = Join-Path $workspace 'src\BiliXbox'
$snapshotRoot = Join-Path $runDirectory 'source'
$snapshotProject = Join-Path $snapshotRoot 'src\BiliXbox'
[void][IO.Directory]::CreateDirectory($snapshotProject)
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue($sourceProject)
$copiedSources = @()
while ($pending.Count -gt 0) {
    $directory = $pending.Dequeue()
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source link/junction is unsupported: $($item.FullName)" }
        $relative = [IO.Path]::GetRelativePath($sourceProject, $item.FullName)
        if ($item.PSIsContainer) {
            if ($item.Name -notin @('bin', 'obj', '.vs', 'AppPackages')) {
                [void][IO.Directory]::CreateDirectory((Join-Path $snapshotProject $relative))
                $pending.Enqueue($item.FullName)
            }
        } elseif ($item.Extension -in @('.cs', '.xaml', '.csproj', '.appxmanifest', '.png', '.xml', '.resw') -and
                  $item.Name -ne 'Package.StoreAssociation.xml') {
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $snapshotProject $relative)
            $copiedSources += [pscustomobject]@{ Path = $relative; Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
        }
    }
}
Copy-Item -LiteralPath (Join-Path $workspace 'THIRD-PARTY-NOTICES.txt') -Destination $snapshotRoot
$sourceManifest.Package.Identity.SetAttribute('Version', $testQuad.ToString())
$sourceManifest.Save((Join-Path $snapshotProject 'Package.appxmanifest'))
$sourceIdentity = $sourceManifest.Package.Identity
$versionInfoPath = Join-Path $snapshotProject 'Services\AppVersionInfo.cs'
$versionInfo = [IO.File]::ReadAllText($versionInfoPath)
if ([regex]::Matches($versionInfo, 'internal const bool IsTestBuild = (?:true|false);').Count -ne 1 -or
    [regex]::Matches($versionInfo, 'internal const string ReleaseVersionOverride = "[^"]*";').Count -ne 1) {
    throw 'The centralized version-info constants are missing or ambiguous.'
}
$releaseDisplay = '{0}.{1}.{2}' -f $releaseQuad.Major, $releaseQuad.Minor, $releaseQuad.Build
$versionInfo = [regex]::Replace($versionInfo, 'internal const bool IsTestBuild = (?:true|false);', 'internal const bool IsTestBuild = true;')
$versionInfo = [regex]::Replace($versionInfo, 'internal const string ReleaseVersionOverride = "[^"]*";', ('internal const string ReleaseVersionOverride = "' + $releaseDisplay + '";'))
[IO.File]::WriteAllText($versionInfoPath, $versionInfo, [Text.UTF8Encoding]::new($false))
$snapshotPackages = Join-Path $runDirectory 'AppPackages'
$plan = [ordered]@{
    DisplayVersion = $displayVersion; PackageVersion = $testQuad.ToString(); IsTestBuild = $true
    IdentityName = [string]$sourceIdentity.Name; IdentityPublisher = [string]$sourceIdentity.Publisher
    SourceDirectory = $snapshotProject; PackageOutput = $snapshotPackages
    DistributionPath = $DistributionPath; SourceSnapshot = $copiedSources
}
$planPath = Join-Path $runDirectory 'test-build-plan.json'
$plan | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $planPath -Encoding utf8
if ($PrepareOnly) {
    return [pscustomobject]@{ Prepared = $true; BuildPlan = $planPath; SourceDirectory = $snapshotProject }
}
$devCertificate = & (Join-Path $PSScriptRoot 'New-DevCertificate.ps1')
$buildArguments = @{
    Configuration = $Configuration
    CertificatePath = $devCertificate.CertificatePath
    ProjectPath = (Join-Path $snapshotProject 'BiliXbox.csproj')
    OutputDirectory = $snapshotPackages
}
if ($MSBuildPath) { $buildArguments.MSBuildPath = $MSBuildPath }
& (Join-Path $PSScriptRoot 'Build-Appx.ps1') @buildArguments

$candidates = @()
# Only inspect the explicit build output, excluding dependency and intermediate builddata trees.
$directories = [Collections.Generic.Queue[string]]::new()
$directories.Enqueue($snapshotPackages)
while ($directories.Count -gt 0) {
    $directory = $directories.Dequeue()
    Assert-NoReparsePath $directory
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to scan a symbolic link/junction in the package output: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            if ($item.Name -notin @('Dependencies', 'builddata')) { $directories.Enqueue($item.FullName) }
            continue
        }
        if ($item.Extension -ne '.appx') { continue }
        $archive = [IO.Compression.ZipFile]::OpenRead($item.FullName)
        try {
            $entry = $archive.GetEntry('AppxManifest.xml')
            if (-not $entry -or $entry.Length -gt 2MB) { continue }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $identity = $manifest.Package.Identity
            if ([string]$identity.Name -eq [string]$sourceIdentity.Name -and
                [string]$identity.Publisher -eq [string]$sourceIdentity.Publisher -and
                [string]$identity.Version -eq [string]$sourceIdentity.Version -and
                [string]$identity.ProcessorArchitecture -eq 'x64' -and
                [string]::IsNullOrEmpty([string]$identity.ResourceId) -and
                $archive.GetEntry('AppxSignature.p7x')) {
                $candidates += $item
            }
        } finally { $archive.Dispose() }
    }
}
$candidates = @($candidates | Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, FullName)
if ($candidates.Count -eq 0) {
    throw "Build finished, but no signed x64 Appx matches manifest identity $($sourceIdentity.Name) $($sourceIdentity.Version) under $packageOutput."
}
if ($candidates.Count -ne 1) {
    $paths = ($candidates | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
    throw "Several signed Appx files match this version. Choose a separate OutputDirectory to make the result unambiguous. Matches (newest first):$([Environment]::NewLine)$paths"
}
$selectedPackage = $candidates[0].FullName
$packagingArguments = @{
    PackagePath = $selectedPackage
    PublicCertificatePath = $devCertificate.PublicCertificatePath
    OutputPath = $DistributionPath
    DisplayVersion = $displayVersion
}
$dependencyDirectory = Join-Path ([IO.Path]::GetDirectoryName($selectedPackage)) 'Dependencies'
if (Test-Path -LiteralPath $dependencyDirectory -PathType Container) {
    $packagingArguments.DependenciesDirectory = $dependencyDirectory
}
& (Join-Path $PSScriptRoot 'Package-XboxTest.ps1') @packagingArguments
$verification = & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackagePath $selectedPackage -PublicCertificatePath $devCertificate.PublicCertificatePath -DistributionZipPath $DistributionPath
if ($verification.Result -ne 'PASS') { throw 'Signed development package validation did not pass; install-version state was not updated.' }
# Commit install-version state only after the signed package and distribution pass validation.
$newState = [ordered]@{
    IdentityName = [string]$sourceIdentity.Name; IdentityPublisher = [string]$sourceIdentity.Publisher
    PackageVersion = $testQuad.ToString(); DisplayVersion = $displayVersion
    PackagePath = $selectedPackage; Sha256 = (Get-FileHash -LiteralPath $selectedPackage -Algorithm SHA256).Hash
}
$temporaryState = $statePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$newState | ConvertTo-Json | Set-Content -LiteralPath $temporaryState -Encoding utf8
Move-Item -LiteralPath $temporaryState -Destination $statePath -Force
return [pscustomobject]@{ PackagePath = $selectedPackage; DisplayVersion = $displayVersion; PackageVersion = $testQuad.ToString(); BuildPlan = $planPath; Validation = $verification }
