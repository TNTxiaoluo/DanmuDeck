# PowerShell 7. Produces the official MSBuild UWP Store upload container; never submits it.
# Identity values are public Partner Center Product identity fields, not account credentials.
[CmdletBinding()]
param(
    [string]$IdentityName,
    [string]$IdentityPublisher,
    [string]$PublisherDisplayName,
    [string]$StoreAssociationPath,
    [switch]$DevelopmentIdentityDraft,
    [string]$Version = '1.0.2.0',
    [string]$DisplayName = '弹幕舱',
    [string]$MSBuildPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\StoreUpload'),
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$workspacePrefix = $workspace + [IO.Path]::DirectorySeparatorChar

function Assert-WorkspaceOutput {
    param([string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Store build outputs must be in a subdirectory of this workspace.'
    }
    $current = $fullPath
    while ($current -and $current.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Store build output cannot traverse a link/junction: $current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $fullPath
}

function Read-SafeXml {
    param([string]$Path)
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 2MB
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    try { $document.Load($reader) } finally { $reader.Dispose() }
    return $document
}

$quad = $null
if (-not [Version]::TryParse($Version, [ref]$quad) -or $Version -notmatch '^\d+\.\d+\.\d+\.0$' -or
    $quad.Major -lt 1 -or $quad.Major -gt 65535 -or $quad.Minor -gt 65535 -or $quad.Build -gt 65535) {
    throw 'Store UWP version must be Major.Minor.Build.0; Major is 1..65535 and other components are 0..65535.'
}
if ([string]::IsNullOrWhiteSpace($DisplayName)) { throw 'DisplayName cannot be empty.' }
if ($StoreAssociationPath) {
    if ($IdentityName -or $IdentityPublisher -or $PublisherDisplayName -or $DevelopmentIdentityDraft) {
        throw 'Supply StoreAssociationPath or the three identity values or DevelopmentIdentityDraft, not a mixture.'
    }
    $association = Read-SafeXml (Resolve-Path -LiteralPath $StoreAssociationPath).Path
    $identityNode = $association.SelectSingleNode('//*[local-name()="MainPackageIdentityName"]')
    $publisherNode = $association.SelectSingleNode('/*/*[local-name()="Publisher"]')
    $displayNode = $association.SelectSingleNode('/*/*[local-name()="PublisherDisplayName"]')
    if (-not $identityNode -or -not $publisherNode -or -not $displayNode) {
        throw 'Association XML must contain MainPackageIdentityName, Publisher and PublisherDisplayName.'
    }
    $IdentityName = $identityNode.InnerText
    $IdentityPublisher = $publisherNode.InnerText
    $PublisherDisplayName = $displayNode.InnerText
    # Read only the public identity values; do not copy association/account data into artifacts.
}
$sourceProject = Join-Path $workspace 'src\BiliXbox'
$manifest = Read-SafeXml (Join-Path $sourceProject 'Package.appxmanifest')
if ($DevelopmentIdentityDraft) {
    if ($IdentityName -or $IdentityPublisher -or $PublisherDisplayName) { throw 'Draft mode must use only the existing development identity.' }
    $IdentityName = [string]$manifest.Package.Identity.Name
    $IdentityPublisher = [string]$manifest.Package.Identity.Publisher
    $PublisherDisplayName = [string]$manifest.Package.Properties.PublisherDisplayName
    Write-Warning 'DEVELOPMENT IDENTITY DRAFT: valid upload container format, but NOT READY for Partner Center. Rebuild with the three assigned Product identity values.'
} elseif ([string]::IsNullOrWhiteSpace($IdentityName) -or [string]::IsNullOrWhiteSpace($IdentityPublisher) -or
          [string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    throw 'Provide Partner Center Package/Identity/Name, Package/Identity/Publisher and Package/Properties/PublisherDisplayName, or StoreAssociationPath. Use DevelopmentIdentityDraft only for a clearly marked local preview.'
}
if ($IdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') { throw 'Invalid Package/Identity/Name format.' }
if (-not $DevelopmentIdentityDraft -and ($IdentityName -eq 'BiliXbox.Community' -or $IdentityPublisher -eq 'CN=BiliXboxDev')) {
    throw 'Development identity is not Store-associated. Supply assigned Partner Center fields, or explicitly use DevelopmentIdentityDraft.'
}
$dn = [Security.Cryptography.X509Certificates.X500DistinguishedName]::new($IdentityPublisher)
if ($dn.RawData.Length -eq 0) { throw 'Invalid Package/Identity/Publisher distinguished name.' }
if ($IdentityName.Contains(';') -or $IdentityPublisher.Contains(';') -or $PublisherDisplayName.Contains(';')) {
    throw 'Identity values must not contain MSBuild list separators.'
}

$output = Assert-WorkspaceOutput $OutputDirectory
$runDirectory = Assert-WorkspaceOutput (Join-Path $output ('build-' + [Guid]::NewGuid().ToString('N')))
$snapshotRoot = Join-Path $runDirectory 'source'
$snapshotProject = Join-Path $snapshotRoot 'src\BiliXbox'
[void][IO.Directory]::CreateDirectory($snapshotProject)
# Isolate manifest identity and native/Store intermediates from the signed development build.
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
$manifest.Package.Identity.SetAttribute('Name', $IdentityName)
$manifest.Package.Identity.SetAttribute('Publisher', $IdentityPublisher)
$manifest.Package.Identity.SetAttribute('Version', $Version)
$manifest.Package.Properties.DisplayName = $DisplayName
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
foreach ($node in $manifest.SelectNodes('//*[local-name()="VisualElements"]')) { $node.SetAttribute('DisplayName', $DisplayName) }
$manifest.Save((Join-Path $snapshotProject 'Package.appxmanifest'))
$versionInfoPath = Join-Path $snapshotProject 'Services\AppVersionInfo.cs'
$versionInfo = [IO.File]::ReadAllText($versionInfoPath)
if ([regex]::Matches($versionInfo, 'internal const bool IsTestBuild = (?:true|false);').Count -ne 1 -or
    [regex]::Matches($versionInfo, 'internal const string ReleaseVersionOverride = "[^"]*";').Count -ne 1) {
    throw 'The centralized version-info constants are missing or ambiguous.'
}
$versionInfo = [regex]::Replace($versionInfo, 'internal const bool IsTestBuild = (?:true|false);', 'internal const bool IsTestBuild = false;')
$versionInfo = [regex]::Replace($versionInfo, 'internal const string ReleaseVersionOverride = "[^"]*";', 'internal const string ReleaseVersionOverride = "";')
[IO.File]::WriteAllText($versionInfoPath, $versionInfo, [Text.UTF8Encoding]::new($false))

if (-not $MSBuildPath) {
    $local = Join-Path $workspace '.tools\vs2022\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $local) { $MSBuildPath = $local }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        }
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) { throw 'UWP Visual Studio MSBuild is required; supply MSBuildPath.' }
$packages = Join-Path $runDirectory 'AppPackages'
$logFile = Join-Path $runDirectory 'build-store-upload.log'
$arguments = @(
    (Join-Path $snapshotProject 'BiliXbox.csproj'), '/restore', '/m', '/nologo', '/v:minimal',
    '/p:Configuration=Release', '/p:Platform=x64', '/p:UseDotNetNativeToolchain=true',
    '/p:GenerateAppxPackageOnBuild=true', '/p:UapAppxPackageBuildMode=StoreUpload',
    '/p:AppxBundle=Never', '/p:AppxPackageSigningEnabled=false', '/p:AppxSymbolPackageEnabled=true',
    '/p:AppxAutoIncrementPackageRevision=false', '/p:EnableDirectStoreSubmission=false',
    ('/p:AppxPackageDir=' + $packages + '\'),
    ('/p:RestorePackagesPath=' + (Join-Path $workspace '.tools\nuget')),
    ('/flp:logfile={0};verbosity=normal;encoding=UTF-8' -f $logFile)
)
$plan = [ordered]@{
    DisplayName = $DisplayName; Version = $Version; Architecture = 'x64'; Configuration = 'Release'
    IdentityName = $IdentityName; IdentityPublisher = $IdentityPublisher; PublisherDisplayName = $PublisherDisplayName
    PartnerCenterIdentityProvided = -not [bool]$DevelopmentIdentityDraft
    SubmissionStatus = $(if ($DevelopmentIdentityDraft) { 'DEVELOPMENT IDENTITY DRAFT - DO NOT SUBMIT' } else { 'Assigned identity supplied; local validation and Partner Center certification still required' })
    Signing = 'Unsigned Store upload; Microsoft Store re-signs after certification. Development certificate untouched.'
    AutomaticSubmission = $false; SourceDirectory = $snapshotProject; PackageOutput = $packages
    DisplayVersion = ('{0}.{1}.{2}' -f $quad.Major, $quad.Minor, $quad.Build); IsTestBuild = $false
    VersionInfoOverlay = [pscustomobject]@{ Path = 'Services/AppVersionInfo.cs'; Sha256 = (Get-FileHash -LiteralPath $versionInfoPath -Algorithm SHA256).Hash }
    MSBuildPath = $MSBuildPath; MSBuildArguments = $arguments; SourceSnapshot = $copiedSources
}
$planPath = Join-Path $runDirectory 'store-build-plan.json'
$plan | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $planPath -Encoding utf8
if ($PrepareOnly) {
    Write-Output "Store build plan prepared: $planPath"
    return [pscustomobject]@{ Prepared = $true; BuildPlan = $planPath; SourceDirectory = $snapshotProject }
}
& $MSBuildPath @arguments
if ($LASTEXITCODE -ne 0) { throw "MSBuild StoreUpload failed ($LASTEXITCODE). See $logFile" }
$uploads = @(Get-ChildItem -LiteralPath $packages -File | Where-Object Extension -in @('.appxupload', '.msixupload'))
if ($uploads.Count -ne 1) { throw "Expected exactly one Store upload generated by this build; found $($uploads.Count) under $packages." }
$validation = & (Join-Path $PSScriptRoot 'Test-StoreUpload.ps1') -UploadPath $uploads[0].FullName -Version $Version -DisplayName $DisplayName -IdentityName $IdentityName -IdentityPublisher $IdentityPublisher -PublisherDisplayName $PublisherDisplayName -DevelopmentIdentityDraft:$DevelopmentIdentityDraft
$validation | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $runDirectory 'store-upload-validation.json') -Encoding utf8
$suffix = if ($DevelopmentIdentityDraft) { '_DEVELOPMENT-IDENTITY-DRAFT' } else { '' }
$finalName = 'DanmuCabin_{0}_x64{1}{2}' -f $Version, $suffix, $uploads[0].Extension
$finalPath = Assert-WorkspaceOutput (Join-Path $output $finalName)
Copy-Item -LiteralPath $uploads[0].FullName -Destination $finalPath
$validation | Add-Member -NotePropertyName FinalUploadPath -NotePropertyValue $finalPath
$validation | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath ($finalPath + '.validation.json') -Encoding utf8
Write-Output "Store upload container: $finalPath"
Write-Output "Build log: $logFile"
if ($DevelopmentIdentityDraft) { Write-Warning 'This DEVELOPMENT IDENTITY DRAFT cannot be submitted as the final Store app. Rebuild with the actual Partner Center Product identity fields.' }
return $validation
