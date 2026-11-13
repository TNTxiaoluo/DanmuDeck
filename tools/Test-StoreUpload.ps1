# Read-only verification of an actual single-architecture UWP Store upload container.
# The Store upload can carry managed input for Store-side .NET Native compilation;
# this is different from the separate native sideload package produced by StoreUpload.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UploadPath,
    [Parameter(Mandatory = $true)][string]$IdentityName,
    [Parameter(Mandatory = $true)][string]$IdentityPublisher,
    [Parameter(Mandatory = $true)][string]$PublisherDisplayName,
    [string]$Version = '1.0.0.0',
    [string]$DisplayName = '弹幕舱',
    [switch]$DevelopmentIdentityDraft
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
function Read-EntryBytes {
    param([IO.Compression.ZipArchiveEntry]$Entry, [long]$Limit = 512MB)
    if (-not $Entry -or $Entry.Length -gt $Limit) { throw 'Archive entry is missing or exceeds the validation limit.' }
    $inputStream = $Entry.Open()
    $buffer = [IO.MemoryStream]::new()
    try { $inputStream.CopyTo($buffer); return ,$buffer.ToArray() }
    finally { $inputStream.Dispose(); $buffer.Dispose() }
}
function Read-EntryXml {
    param([IO.Compression.ZipArchiveEntry]$Entry)
    $bytes = Read-EntryBytes $Entry -Limit 4MB
    $stream = [IO.MemoryStream]::new($bytes, $false)
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 4MB
    $reader = [Xml.XmlReader]::Create($stream, $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    try { $document.Load($reader); return $document }
    finally { $reader.Dispose(); $stream.Dispose() }
}
function Assert-ArchiveNames {
    param([IO.Compression.ZipArchive]$Archive)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName
        if ($name.Contains('\') -or $name.StartsWith('/') -or $name.Contains(':') -or
            ($name -split '/') -contains '..' -or -not $seen.Add($name)) { throw "Unsafe/duplicate archive name: $name" }
        if ($name -match '(?i)\.(pfx|p12|key|snk)$') { throw "Private-key material is forbidden in upload: $name" }
    }
}

if ([IO.Path]::GetExtension($UploadPath) -notin @('.appxupload', '.msixupload')) { throw 'UploadPath must be the generated .appxupload or .msixupload, not the Xbox test ZIP.' }
if (-not $DevelopmentIdentityDraft -and ($IdentityName -eq 'BiliXbox.Community' -or $IdentityPublisher -eq 'CN=BiliXboxDev')) { throw 'Development identity is not a final Partner Center product identity.' }
if ($Version -notmatch '^[1-9]\d*\.\d+\.\d+\.0$') { throw 'Store version must reserve the fourth component as 0.' }
$upload = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $UploadPath).Path)
$package = $null
$packageStream = $null
try {
    Assert-ArchiveNames $upload
    $appxEntries = @($upload.Entries | Where-Object FullName -match '^[^/]+\.(appx|msix)$')
    $symbolEntries = @($upload.Entries | Where-Object FullName -match '^[^/]+\.appxsym$')
    if ($appxEntries.Count -ne 1 -or $symbolEntries.Count -ne 1 -or $upload.Entries.Count -ne 2) {
        throw 'Expected exactly one root-level Appx/MSIX and its Appxsym; no test ZIP files, dependencies, certificates or installer scripts.'
    }
    $packageStream = [IO.MemoryStream]::new((Read-EntryBytes $appxEntries[0]), $false)
    $package = [IO.Compression.ZipArchive]::new($packageStream, [IO.Compression.ZipArchiveMode]::Read)
    Assert-ArchiveNames $package
    $manifest = Read-EntryXml $package.GetEntry('AppxManifest.xml')
    $identity = $manifest.Package.Identity
    foreach ($pair in @(
        @('Name', $IdentityName), @('Publisher', $IdentityPublisher), @('Version', $Version), @('ProcessorArchitecture', 'x64')
    )) { if ([string]$identity.($pair[0]) -cne [string]$pair[1]) { throw "Packaged Identity/$($pair[0]) differs from the expected value." } }
    if ([string]$manifest.Package.Properties.DisplayName -cne $DisplayName -or
        [string]$manifest.Package.Properties.PublisherDisplayName -cne $PublisherDisplayName) { throw 'Packaged DisplayName/PublisherDisplayName mismatch.' }
    foreach ($visual in $manifest.SelectNodes('//*[local-name()="VisualElements"]')) {
        if ($visual.GetAttribute('DisplayName') -cne $DisplayName) { throw 'Application VisualElements DisplayName mismatch.' }
    }
    if (-not $manifest.SelectSingleNode('//*[local-name()="TargetDeviceFamily" and (@Name="Windows.Universal" or @Name="Windows.Xbox")]')) { throw 'Package is not applicable to Xbox.' }
    foreach ($attribute in $manifest.SelectNodes('//@*')) {
        if ($attribute.Value -match '^Assets[\\/].*\.png$') {
            $entry = $package.GetEntry($attribute.Value.Replace('\', '/'))
            if (-not $entry -or $entry.Length -eq 0) { throw "Missing packaged brand asset: $($attribute.Value)" }
        }
    }
    $blockMap = Read-EntryXml $package.GetEntry('AppxBlockMap.xml')
    if ($blockMap.DocumentElement.NamespaceURI -ne 'http://schemas.microsoft.com/appx/2010/blockmap' -or
        [string]$blockMap.BlockMap.HashMethod -ne 'http://www.w3.org/2001/04/xmlenc#sha256') { throw 'Unexpected block-map schema/hash algorithm.' }
    $mapped = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $blockCount = 0
    foreach ($file in $blockMap.BlockMap.File) {
        $name = ([string]$file.Name).Replace('\', '/')
        if (-not $mapped.Add($name)) { throw "Duplicate block-map file: $name" }
        $entry = $package.GetEntry($name)
        if (-not $entry -or $entry.Length -ne [long]$file.Size) { throw "Block-map file size mismatch: $name" }
        $blocks = @($file.Block | Where-Object { $null -ne $_ })
        $expectedCount = [int][Math]::Ceiling($entry.Length / 65536.0)
        if ($blocks.Count -ne $expectedCount -and -not ($entry.Length -eq 0 -and $blocks.Count -eq 1)) { throw "Block-map block count mismatch: $name" }
        $stream = $entry.Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try {
            $buffer = [byte[]]::new(65536)
            $remaining = $entry.Length
            foreach ($block in $blocks) {
                $length = [int][Math]::Min(65536, $remaining)
                $read = 0
                while ($read -lt $length) {
                    $count = $stream.Read($buffer, $read, $length - $read)
                    if ($count -eq 0) { throw "Truncated Appx file: $name" }
                    $read += $count
                }
                if ([Convert]::ToBase64String($hash.ComputeHash($buffer, 0, $length)) -cne [string]$block.Hash) { throw "Appx block hash mismatch: $name" }
                $remaining -= $length
                $blockCount++
            }
            if ($remaining -ne 0 -or $stream.ReadByte() -ne -1) { throw "Appx file length mismatch: $name" }
        } finally { $hash.Dispose(); $stream.Dispose() }
    }
    $footprint = @('AppxBlockMap.xml', 'AppxSignature.p7x', '[Content_Types].xml', 'AppxMetadata/CodeIntegrity.cat')
    foreach ($entry in $package.Entries) {
        if (-not $entry.FullName.EndsWith('/') -and -not $mapped.Contains($entry.FullName) -and $entry.FullName -notin $footprint) { throw "Unmapped Appx payload: $($entry.FullName)" }
    }
    $symbolStream = [IO.MemoryStream]::new((Read-EntryBytes $symbolEntries[0]), $false)
    $symbols = $null
    try {
        $symbols = [IO.Compression.ZipArchive]::new($symbolStream, [IO.Compression.ZipArchiveMode]::Read)
        Assert-ArchiveNames $symbols
        $pdbs = @($symbols.Entries | Where-Object FullName -match '(?i)\.pdb$')
        if ($pdbs.Count -eq 0) { throw 'Appxsym has no public PDB symbol payload.' }
    } finally { if ($symbols) { $symbols.Dispose() }; $symbolStream.Dispose() }
    [pscustomobject]@{
        Result = 'PASS'; DisplayName = $DisplayName; Version = $Version; Architecture = 'x64'
        IdentityName = $IdentityName; IdentityPublisher = $IdentityPublisher; PublisherDisplayName = $PublisherDisplayName
        PartnerCenterIdentityProvided = -not [bool]$DevelopmentIdentityDraft
        SubmissionStatus = $(if ($DevelopmentIdentityDraft) { 'DEVELOPMENT IDENTITY DRAFT - DO NOT SUBMIT' } else { 'Assigned identity supplied; Partner Center certification not performed' })
        ApplicationPackage = $appxEntries[0].FullName; SymbolPackage = $symbolEntries[0].FullName
        PackageFilesVerified = $mapped.Count; PackageBlocksVerified = $blockCount; PdbFiles = $pdbs.Count
        UploadSha256 = (Get-FileHash -LiteralPath $UploadPath -Algorithm SHA256).Hash
    }
} finally {
    if ($package) { $package.Dispose() }
    if ($packageStream) { $packageStream.Dispose() }
    $upload.Dispose()
}
