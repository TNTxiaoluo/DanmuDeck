# Requires PowerShell 7 and Visual Studio UWP build tools. Creates a signed Xbox test ZIP.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$MSBuildPath,
    [string]$OutputDirectory,
    [string]$DistributionPath = (Join-Path $PSScriptRoot '..\artifacts\BiliXbox-Xbox-Test.zip')
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$workspacePrefix = $workspace + [IO.Path]::DirectorySeparatorChar
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
$devCertificate = & (Join-Path $PSScriptRoot 'New-DevCertificate.ps1')
$buildArguments = @{
    Configuration = $Configuration
    CertificatePath = $devCertificate.CertificatePath
    OutputDirectory = $packageOutput
}
if ($MSBuildPath) { $buildArguments.MSBuildPath = $MSBuildPath }
& (Join-Path $PSScriptRoot 'Build-Appx.ps1') @buildArguments

[xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $workspace 'src\BiliXbox\Package.appxmanifest') -Raw
$sourceIdentity = $sourceManifest.Package.Identity
Add-Type -AssemblyName System.IO.Compression
$candidates = @()
# Only inspect the explicit build output, excluding dependency and intermediate builddata trees.
$directories = [Collections.Generic.Queue[string]]::new()
$directories.Enqueue($packageOutput)
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
}
$dependencyDirectory = Join-Path ([IO.Path]::GetDirectoryName($selectedPackage)) 'Dependencies'
if (Test-Path -LiteralPath $dependencyDirectory -PathType Container) {
    $packagingArguments.DependenciesDirectory = $dependencyDirectory
}
& (Join-Path $PSScriptRoot 'Package-XboxTest.ps1') @packagingArguments
