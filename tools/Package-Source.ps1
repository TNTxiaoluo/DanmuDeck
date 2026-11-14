#requires -Version 7.0
# Exports a public source ZIP from an explicit allowlist; never publishes to GitHub.
[CmdletBinding()]
param([string]$OutputPath, [switch]$Force)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$workspacePrefix = $workspace + [IO.Path]::DirectorySeparatorChar

function Assert-WorkspacePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source export paths must remain inside this workspace.'
    }
    $current = $resolved
    while ($current -and $current.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Source exports must not traverse symbolic links or junctions.'
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $resolved
}

function Test-Excluded([string]$Relative) {
    return $Relative -match '(?i)(^|/)(\.git|\.tools|artifacts|bin|obj|AppPackages|BundleArtifacts|\.vs|node_modules|__pycache__)(/|$)' -or
        $Relative -eq 'tools/Package.StoreAssociation.xml' -or $Relative.StartsWith('docs/store/', [StringComparison]::OrdinalIgnoreCase) -or
        $Relative -match '(?i)\.(pfx|p12|pem|key|snk|cer|appx|msix|appxbundle|msixbundle|appxupload|msixupload|appxsym|pdb|log|binlog|zip|bin|user|suo)$' -or
        $Relative -match '(?i)(^|/)(\.env(?:\..*)?|cookies?\.(txt|json))$'
}

$rootFiles = @('.gitignore', 'AGENTS.md', 'BiliXbox.sln', 'README.md', 'LICENSE', 'CHANGELOG.md', 'CONTRIBUTING.md', 'THIRD-PARTY-NOTICES.txt')
$publicDocuments = @('docs/BUILDING.md', 'docs/TESTING.md', 'docs/CONTROLS.md', 'docs/STORE.md', 'docs/PLAYBACK-DIAGNOSTICS.md', 'docs/RELEASE-NOTES-1.0.2.md')
$paths = [Collections.Generic.List[string]]::new()
foreach ($relative in $rootFiles + $publicDocuments) {
    $path = Assert-WorkspacePath (Join-Path $workspace $relative)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing public source file: $relative" }
    $paths.Add($relative)
}

$extensions = @{
    'src/BiliXbox' = @('.cs', '.xaml', '.csproj', '.appxmanifest', '.xml', '.resw', '.png')
    'tools' = @('.ps1', '.cs', '.js', '.xml', '.json', '.md')
    '.github' = @('.yml', '.yaml', '.md')
}
foreach ($base in $extensions.Keys) {
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue((Join-Path $workspace $base))
    while ($queue.Count -gt 0) {
        $directory = Assert-WorkspacePath $queue.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            $relative = [IO.Path]::GetRelativePath($workspace, $item.FullName).Replace('\', '/')
            if (Test-Excluded $relative) { continue }
            [void](Assert-WorkspacePath $item.FullName)
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName) }
            elseif ($item.Extension.ToLowerInvariant() -in $extensions[$base]) { $paths.Add($relative) }
        }
    }
}
$paths = @($paths | Sort-Object -Unique)
$pathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$fileManifest = @()
$secretPatterns = @(
    '-{5}BEGIN (?:[A-Z]+ )*PRIVATE KEY-{5}',
    '(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16})(?![A-Za-z0-9])',
    '(?<![A-Za-z0-9])sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{30,}'
)
$personalPathPattern = '(?i)(?:C|D):[\\/]Users?[\\/]Utente|[CD]:[\\/]Utente Pc[\\/]|' +
    ('codex-' + 'remote-attachments') + '|' + ('UTENTE' + '~1')
foreach ($relative in $paths) {
    if (Test-Excluded $relative) { throw "A private/local file entered the export: $relative" }
    [void]$pathSet.Add($relative)
    $path = Assert-WorkspacePath (Join-Path $workspace $relative)
    if ([IO.Path]::GetExtension($path) -ne '.png') {
        $text = [IO.File]::ReadAllText($path)
        foreach ($pattern in $secretPatterns) {
            if ([regex]::IsMatch($text, $pattern)) { throw "Potential private credential in $relative; inspect locally before export." }
        }
        if ($text -match $personalPathPattern) {
            throw "Machine-specific personal path found in $relative; review before publishing."
        }
    }
    $fileManifest += [pscustomobject]@{
        Path = $relative; Bytes = (Get-Item -LiteralPath $path).Length
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

# Check relative links against the actual public export, not private workspace files.
$linksVerified = 0
foreach ($relative in $paths | Where-Object { [IO.Path]::GetExtension($_) -eq '.md' }) {
    $text = [IO.File]::ReadAllText((Join-Path $workspace $relative))
    $targets = @([regex]::Matches($text, '!?\[[^\]\r\n]*\]\((?<target>[^)\r\n]+)\)') |
        ForEach-Object { $_.Groups['target'].Value.Trim().Trim('<', '>') })
    $targets += @([regex]::Matches($text, '<img\b[^>]*\bsrc="(?<target>[^"]+)"') |
        ForEach-Object { $_.Groups['target'].Value })
    foreach ($target in $targets) {
        if ($target -match '^(?:[A-Za-z][A-Za-z0-9+.-]*:|#)') { continue }
        $linkPath = [Uri]::UnescapeDataString(($target -split '[#?]', 2)[0])
        if ($linkPath.Length -eq 0) { continue }
        $resolved = Assert-WorkspacePath (Join-Path (Split-Path (Join-Path $workspace $relative)) $linkPath)
        $linkedRelative = [IO.Path]::GetRelativePath($workspace, $resolved).Replace('\', '/')
        if (-not $pathSet.Contains($linkedRelative)) { throw "Public documentation links to an excluded/missing file: $relative -> $target" }
        $linksVerified++
    }
}

[xml]$manifest = [IO.File]::ReadAllText((Join-Path $workspace 'src/BiliXbox/Package.appxmanifest'))
$version = [Version]::Parse([string]$manifest.Package.Identity.Version)
$releaseVersion = '{0}.{1}.{2}' -f $version.Major, $version.Minor, $version.Build
$folderName = "DanmuCabin-$releaseVersion-source"
if (-not $OutputPath) { $OutputPath = Join-Path $workspace "artifacts/OpenSource/$folderName.zip" }
elseif (-not [IO.Path]::IsPathRooted($OutputPath)) { $OutputPath = Join-Path $workspace $OutputPath }
$output = Assert-WorkspacePath $OutputPath
if ([IO.Path]::GetExtension($output) -ne '.zip') { throw 'OutputPath must end with .zip.' }
if ((Test-Path -LiteralPath $output) -and -not $Force) { throw 'Source ZIP already exists; choose a new OutputPath or use -Force to replace it after validation.' }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$temporary = Assert-WorkspacePath ($output + '.partial-' + [Guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $fileManifest) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $workspace $file.Path),
            ($folderName + '/' + $file.Path), [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }

# Read every ZIP entry back and verify its exact source hash before delivery.
$archive = [IO.Compression.ZipFile]::OpenRead($temporary)
try {
    if ($archive.Entries.Count -ne $fileManifest.Count) { throw 'Export file count mismatch.' }
    foreach ($file in $fileManifest) {
        $entry = $archive.GetEntry($folderName + '/' + $file.Path)
        if (-not $entry -or $entry.Length -ne $file.Bytes) { throw "Export size mismatch: $($file.Path)" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [Convert]::ToHexString($sha.ComputeHash($stream)) }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($actual -cne $file.Sha256) { throw "Export hash mismatch: $($file.Path)" }
    }
} finally { $archive.Dispose() }
Copy-Item -LiteralPath $temporary -Destination $output -Force:$Force
Remove-Item -LiteralPath $temporary -Force
$zipHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
$report = [ordered]@{
    Result = 'PASS'; Project = '弹幕舱'; Version = $releaseVersion; License = 'MIT'; RootDirectory = $folderName
    ZipName = [IO.Path]::GetFileName($output); ZipBytes = (Get-Item -LiteralPath $output).Length; ZipSha256 = $zipHash
    FilesVerified = $fileManifest.Count; DocumentationLinksVerified = $linksVerified
    PrivateKeyFilesIncluded = $false; MachineToolchainIncluded = $false; AccountSessionFilesIncluded = $false
    BuildOutputsIncluded = $false; MaintainerStoreAssociationIncluded = $false
    CredentialPatternScanPassed = $true; PersonalPathScanPassed = $true; UploadedToGitHub = $false; Files = $fileManifest
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($output + '.manifest.json') -Encoding utf8
('{0}  {1}' -f $zipHash, [IO.Path]::GetFileName($output)) | Set-Content -LiteralPath ($output + '.sha256') -Encoding ascii
[pscustomobject]@{ Result = 'PASS'; SourceZip = $output; Files = $fileManifest.Count; Bytes = (Get-Item -LiteralPath $output).Length; Sha256 = $zipHash }
