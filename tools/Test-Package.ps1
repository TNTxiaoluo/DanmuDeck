# Read-only PowerShell 7 verification of the native x64 Appx and its Xbox distribution ZIP.
# Payload verification uses signed AXBM/AXCT/AXCI digests and every 64-KiB block-map hash.
# ZIP layout/trust-chain checks remain the responsibility of MakeAppx/Windows deployment.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$PublicCertificatePath,
    [Parameter(Mandatory = $true)][string]$DistributionZipPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
Add-Type -AssemblyName System.Formats.Asn1

function Read-EntryBytes {
    param([IO.Compression.ZipArchiveEntry]$Entry, [long]$Limit = 64MB)
    if (-not $Entry -or $Entry.Length -gt $Limit) { throw 'An expected ZIP entry is missing or exceeds its size limit.' }
    $stream = $Entry.Open()
    $buffer = [IO.MemoryStream]::new()
    try { $stream.CopyTo($buffer); return ,$buffer.ToArray() }
    finally { $buffer.Dispose(); $stream.Dispose() }
}
function Get-EntryHash {
    param([IO.Compression.ZipArchiveEntry]$Entry)
    if (-not $Entry) { throw 'An expected ZIP entry is missing.' }
    $stream = $Entry.Open()
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($hash.ComputeHash($stream)).ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}
function Read-EntryXml {
    param([IO.Compression.ZipArchiveEntry]$Entry)
    $bytes = Read-EntryBytes $Entry -Limit 4MB
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $stream = [IO.MemoryStream]::new($bytes, $false)
    $reader = [Xml.XmlReader]::Create($stream, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    } finally { $reader.Dispose(); $stream.Dispose() }
}
function Assert-ArchiveNames {
    param([IO.Compression.ZipArchive]$Archive)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Archive.Entries) {
        if (-not $names.Add($entry.FullName)) { throw "Duplicate ZIP entry: $($entry.FullName)" }
        if ($entry.FullName -match '(^/|^[a-zA-Z]:|\\|(^|/)\.\.(/|$))') { throw "Unsafe ZIP entry: $($entry.FullName)" }
        if ($entry.FullName -match '(?i)\.(pfx|p12)$') { throw 'A private-key file was included in an archive.' }
    }
}
function Assert-NativePe {
    param([IO.Compression.ZipArchive]$Archive, [string]$Name)
    $bytes = Read-EntryBytes $Archive.GetEntry($Name)
    if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) { throw "Invalid native PE: $Name" }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 264 -gt $bytes.Length -or
        [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x4550 -or
        [BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664 -or
        [BitConverter]::ToUInt16($bytes, $peOffset + 24) -ne 0x20b) { throw "PE is not AMD64/PE32+: $Name" }
    if ([BitConverter]::ToUInt32($bytes, $peOffset + 24 + 224) -ne 0) { throw "PE contains a managed CLR header instead of a native Release payload: $Name" }
}
function Test-AppxArchive {
    param(
        [IO.Compression.ZipArchive]$Archive,
        [Security.Cryptography.X509Certificates.X509Certificate2]$ExpectedCertificate,
        [switch]$CheckNative
    )
    Assert-ArchiveNames $Archive
    $manifest = Read-EntryXml $Archive.GetEntry('AppxManifest.xml')
    $identity = $manifest.Package.Identity
    if ([string]$identity.ProcessorArchitecture -notin @('x64', 'neutral')) { throw 'An Appx architecture is incompatible with Xbox x64.' }
    $signature = Read-EntryBytes $Archive.GetEntry('AppxSignature.p7x') -Limit 10MB
    if ($signature.Length -lt 5 -or [Text.Encoding]::ASCII.GetString($signature, 0, 4) -ne 'PKCX') { throw 'Invalid Appx signature container.' }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode([byte[]]$signature[4..($signature.Length - 1)])
    $cms.CheckSignature($true)
    if ($cms.SignerInfos.Count -ne 1) { throw 'Expected exactly one Appx signer.' }
    $signer = $cms.SignerInfos[0].Certificate
    if ($ExpectedCertificate -and $signer.Thumbprint -ne $ExpectedCertificate.Thumbprint) { throw 'The Appx signer does not match the supplied public CER.' }
    $subject = [Security.Cryptography.X509Certificates.X500DistinguishedName]::new([string]$identity.Publisher)
    if ([Convert]::ToBase64String($subject.RawData) -ne [Convert]::ToBase64String($signer.SubjectName.RawData)) { throw 'The Appx Publisher does not match its signing certificate.' }
    if ($cms.ContentInfo.ContentType.Value -ne '1.3.6.1.4.1.311.2.1.4') { throw 'Unexpected Appx signed-content OID.' }
    # SPC_INDIRECT_DATA_CONTENT: data followed by SHA-256 DigestInfo containing APPX tagged hashes.
    $asn = [Formats.Asn1.AsnReader]::new([ReadOnlyMemory[byte]]::new($cms.ContentInfo.Content), [Formats.Asn1.AsnEncodingRules]::DER)
    $sequence = $asn.ReadSequence()
    [void]$sequence.ReadEncodedValue()
    $digestInfo = $sequence.ReadSequence()
    $algorithm = $digestInfo.ReadSequence()
    if ($algorithm.ReadObjectIdentifier() -ne '2.16.840.1.101.3.4.2.1') { throw 'The signed Appx digest algorithm is not SHA-256.' }
    if ($algorithm.HasData) { $algorithm.ReadNull() }
    $algorithm.ThrowIfNotEmpty()
    $digestBytes = $digestInfo.ReadOctetString()
    $digestInfo.ThrowIfNotEmpty(); $sequence.ThrowIfNotEmpty(); $asn.ThrowIfNotEmpty()
    if ($digestBytes.Length -lt 4 -or (($digestBytes.Length - 4) % 36) -ne 0 -or
        [Text.Encoding]::ASCII.GetString($digestBytes, 0, 4) -ne 'APPX') { throw 'Invalid signed Appx digest structure.' }
    $digests = @{}
    for ($offset = 4; $offset -lt $digestBytes.Length; $offset += 36) {
        $tag = [Text.Encoding]::ASCII.GetString($digestBytes, $offset, 4)
        if ($digests.ContainsKey($tag)) { throw "Duplicate signed Appx digest: $tag" }
        $digests[$tag] = [Convert]::ToHexString($digestBytes, $offset + 4, 32).ToLowerInvariant()
    }
    foreach ($footprint in @(
        @{ Tag = 'AXBM'; Name = 'AppxBlockMap.xml' },
        @{ Tag = 'AXCT'; Name = '[Content_Types].xml' },
        @{ Tag = 'AXCI'; Name = 'AppxMetadata/CodeIntegrity.cat' }
    )) {
        $entry = $Archive.GetEntry($footprint.Name)
        if ($footprint.Tag -eq 'AXCI' -and -not $entry -and -not $digests.ContainsKey('AXCI')) { continue }
        if (-not $entry -or (Get-EntryHash $entry) -ne $digests[$footprint.Tag]) { throw "The signed Appx digest does not match $($footprint.Name)." }
    }
    $blockMap = Read-EntryXml $Archive.GetEntry('AppxBlockMap.xml')
    if ($blockMap.DocumentElement.NamespaceURI -ne 'http://schemas.microsoft.com/appx/2010/blockmap' -or
        [string]$blockMap.BlockMap.HashMethod -ne 'http://www.w3.org/2001/04/xmlenc#sha256') { throw 'Unexpected block-map schema/hash algorithm.' }
    $mapped = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $blockCount = 0
    foreach ($file in $blockMap.BlockMap.File) {
        $name = ([string]$file.Name).Replace('\', '/')
        if (-not $mapped.Add($name)) { throw "Duplicate block-map file: $name" }
        $entry = $Archive.GetEntry($name)
        if (-not $entry -or $entry.Length -ne [long]$file.Size -or [int]$file.LfhSize -lt 30) { throw "Block-map size/header mismatch: $name" }
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
                $expectedHash = [Convert]::FromBase64String([string]$block.Hash)
                if ($expectedHash.Length -ne 32 -or
                    [Convert]::ToBase64String($hash.ComputeHash($buffer, 0, $length)) -ne [string]$block.Hash) { throw "Appx block hash mismatch: $name block $blockCount" }
                if ($block.HasAttribute('Size') -and [long]$block.Size -le 0) { throw "Invalid compressed block size: $name" }
                $remaining -= $length
                $blockCount++
            }
            if ($remaining -ne 0 -or $stream.ReadByte() -ne -1) { throw "Appx file length mismatch: $name" }
        } finally { $hash.Dispose(); $stream.Dispose() }
    }
    $footprintNames = @('AppxBlockMap.xml', 'AppxSignature.p7x', '[Content_Types].xml', 'AppxMetadata/CodeIntegrity.cat')
    foreach ($entry in $Archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        if (-not $mapped.Contains($entry.FullName) -and $entry.FullName -notin $footprintNames) { throw "Appx entry is not covered by its signed block map: $($entry.FullName)" }
    }
    if ($CheckNative) {
        if ([string]$identity.Name -ne 'BiliXbox.Community' -or [string]$identity.ProcessorArchitecture -ne 'x64') { throw 'Expected the BiliXbox x64 main package.' }
        Assert-NativePe $Archive 'BiliXbox.exe'
        Assert-NativePe $Archive 'BiliXbox.dll'
        foreach ($asset in @('Wide310x150Logo.png', 'SplashScreen.png', 'Square44x44Logo.png', 'Square150x150Logo.png', 'StoreLogo.png')) {
            $entry = $Archive.GetEntry("Assets/$asset")
            if (-not $entry -or $entry.Length -eq 0) { throw "Missing packaged asset: $asset" }
        }
        $pri = Read-EntryBytes $Archive.GetEntry('resources.pri') -Limit 16MB
        $priText = [Text.Encoding]::UTF8.GetString($pri)
        foreach ($xbf in @('MainPage.xbf', 'App.xbf')) {
            if (-not $Archive.GetEntry($xbf) -and -not $priText.Contains($xbf)) { throw "Compiled XAML resource is absent: $xbf" }
        }
    }
    return [pscustomobject]@{ Manifest = $manifest; Files = $mapped.Count; Blocks = $blockCount; SignerThumbprint = $signer.Thumbprint }
}

foreach ($path in @($PackagePath, $PublicCertificatePath, $DistributionZipPath)) {
    if (-not [IO.Path]::IsPathFullyQualified($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Supply an existing absolute file path: $path" }
}
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($PublicCertificatePath)
$main = $null
$distribution = $null
try {
    if ($certificate.HasPrivateKey) { throw 'The public CER unexpectedly contains a private key.' }
    $main = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    $mainResult = Test-AppxArchive $main $certificate -CheckNative
    $distribution = [IO.Compression.ZipFile]::OpenRead($DistributionZipPath)
    Assert-ArchiveNames $distribution
    $sumBytes = Read-EntryBytes $distribution.GetEntry('SHA256SUMS.txt') -Limit 1MB
    $sumText = [Text.Encoding]::UTF8.GetString($sumBytes).TrimStart([char]0xfeff)
    $hashes = @{}
    foreach ($line in ($sumText -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw 'Malformed SHA256SUMS.txt line.' }
        $name = $Matches[2]
        if ($hashes.ContainsKey($name)) { throw "Duplicate SHA256SUMS entry: $name" }
        $hashes[$name] = $Matches[1].ToLowerInvariant()
        if ((Get-EntryHash $distribution.GetEntry($name)) -ne $hashes[$name]) { throw "Distribution checksum mismatch: $name" }
    }
    foreach ($entry in $distribution.Entries) {
        if ($entry.FullName.EndsWith('/') -or $entry.FullName -eq 'SHA256SUMS.txt') { continue }
        if (-not $hashes.ContainsKey($entry.FullName)) { throw "A distribution entry has no checksum: $($entry.FullName)" }
    }
    $mainName = [IO.Path]::GetFileName($PackagePath)
    if ((Get-EntryHash $distribution.GetEntry($mainName)) -ne (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'The distributed Appx differs from the validated main Appx.' }
    if ((Get-EntryHash $distribution.GetEntry('BiliXboxDev.cer')) -ne (Get-FileHash -LiteralPath $PublicCertificatePath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'The distributed public CER differs from the validated certificate.' }
    if (-not $distribution.GetEntry('INSTALL.txt')) { throw 'The distribution has no installation guide.' }
    $dependencyResults = @()
    foreach ($entry in $distribution.Entries) {
        if ($entry.FullName -notmatch '^Dependencies/x64/[^/]+\.appx$') { continue }
        $bytes = Read-EntryBytes $entry
        $memory = [IO.MemoryStream]::new($bytes, $false)
        $dependencyArchive = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
        try { $dependencyResults += Test-AppxArchive $dependencyArchive }
        finally { $dependencyArchive.Dispose(); $memory.Dispose() }
    }
    foreach ($required in @($mainResult.Manifest.Package.Dependencies.PackageDependency)) {
        if (-not $required) { continue }
        $match = $dependencyResults | Where-Object {
            [string]$_.Manifest.Package.Identity.Name -eq [string]$required.Name -and
            [string]$_.Manifest.Package.Identity.Publisher -eq [string]$required.Publisher -and
            [Version]$_.Manifest.Package.Identity.Version -ge [Version]$required.MinVersion
        }
        if (-not $match) { throw "A required dependency is missing/old: $($required.Name) >= $($required.MinVersion)" }
    }
    [pscustomobject]@{
        Result = 'PASS'
        Identity = [string]$mainResult.Manifest.Package.Identity.Name
        Version = [string]$mainResult.Manifest.Package.Identity.Version
        NativeArchitecture = 'AMD64 (8664), PE32+, no CLR header'
        MainFilesVerified = $mainResult.Files
        MainBlocksVerified = $mainResult.Blocks
        DependencyPackagesVerified = $dependencyResults.Count
        DistributionFilesChecksummed = $hashes.Count
        SignerThumbprint = $mainResult.SignerThumbprint
        PackageSha256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
        DistributionSha256 = (Get-FileHash -LiteralPath $DistributionZipPath -Algorithm SHA256).Hash
    }
} finally {
    if ($distribution) { $distribution.Dispose() }
    if ($main) { $main.Dispose() }
    $certificate.Dispose()
}
