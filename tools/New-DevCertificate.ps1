# Requires PowerShell 7. Creates a local test certificate without changing any certificate store.
[CmdletBinding()]
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\src\BiliXbox\Package.appxmanifest'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\dev-certificate'),
    [ValidateRange(30, 730)][int]$ValidityDays = 365
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$privateRoots = @((Join-Path $workspace 'artifacts'), (Join-Path $workspace '.tools'))
if (-not ($privateRoots | Where-Object {
    $output.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
})) {
    throw 'Keep the private development certificate inside this workspace''s ignored artifacts/ or .tools/ directory.'
}

[xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
$publisher = [string]$manifest.Package.Identity.Publisher
if ([string]::IsNullOrWhiteSpace($publisher)) { throw 'The app manifest has no Publisher identity.' }
$expectedSubject = [Security.Cryptography.X509Certificates.X500DistinguishedName]::new($publisher)
$expectedSubjectBytes = [Convert]::ToBase64String($expectedSubject.RawData)
$pfxPath = Join-Path $output 'BiliXboxDev.pfx'
$cerPath = Join-Path $output 'BiliXboxDev.cer'
$now = [DateTimeOffset]::UtcNow

function Assert-DevelopmentCertificate {
    param([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    if (-not $Certificate.HasPrivateKey) { throw 'The existing PFX has no private key.' }
    if ([Convert]::ToBase64String($Certificate.SubjectName.RawData) -ne $expectedSubjectBytes) {
        throw 'The development certificate subject does not match the manifest Publisher. Use a separate certificate directory for a different identity.'
    }
    if ($Certificate.NotBefore.ToUniversalTime() -gt $now.UtcDateTime -or
        $Certificate.NotAfter.ToUniversalTime() -le $now.UtcDateTime) {
        throw 'The development certificate is not currently valid. Keep a backup, then choose a new certificate directory to replace it.'
    }
    $ekuExtension = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if (-not $ekuExtension -or -not ($ekuExtension.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) {
        throw 'The development certificate does not permit code signing.'
    }
}

$certificate = $null
$rsa = $null
try {
    if (Test-Path -LiteralPath $pfxPath) {
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $pfxPath, '', [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        Assert-DevelopmentCertificate $certificate
        if (-not (Test-Path -LiteralPath $cerPath)) { throw 'The matching public CER is missing. Restore it or use a new certificate directory.' }
        $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)
        try {
            if ($publicCertificate.Thumbprint -ne $certificate.Thumbprint) { throw 'The existing public CER does not match the PFX.' }
        } finally { $publicCertificate.Dispose() }
    } else {
        if (Test-Path -LiteralPath $cerPath) { throw 'The public CER already exists without its PFX. Use a new certificate directory.' }
        [void][IO.Directory]::CreateDirectory($output)
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $expectedSubject, $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $usages = [Security.Cryptography.OidCollection]::new()
        [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))
        $certificate = $request.CreateSelfSigned($now.AddMinutes(-5), $now.AddDays($ValidityDays))
        Assert-DevelopmentCertificate $certificate
        # This unencrypted PFX is for this local sideload test build only. Never distribute it.
        [IO.File]::WriteAllBytes($pfxPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, ''))
        [IO.File]::WriteAllBytes($cerPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }
    [pscustomobject]@{
        Publisher = $publisher
        CertificatePath = $pfxPath
        PublicCertificatePath = $cerPath
        Thumbprint = $certificate.Thumbprint
        ExpiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('u')
    }
} finally {
    if ($certificate) { $certificate.Dispose() }
    if ($rsa) { $rsa.Dispose() }
}
