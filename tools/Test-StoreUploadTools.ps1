# Exercises Store identity isolation and rejection paths without building/submitting/installing.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$scratch = Join-Path $workspace ('artifacts\store-upload-tool-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$checks = 0
function Assert-Check {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-Rejected {
    param([scriptblock]$Action, [string]$Expected)
    try { & $Action | Out-Null } catch {
        Assert-Check ($_.Exception.Message -match $Expected) ('Unexpected rejection: ' + $_.Exception.Message)
        return
    }
    throw "Expected rejection matching: $Expected"
}
$manifestPath = Join-Path $workspace 'src\BiliXbox\Package.appxmanifest'
$originalHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
try {
    Assert-Rejected { & "$PSScriptRoot\Build-StoreUpload.ps1" -PrepareOnly } 'Provide Partner Center'
    Assert-Rejected { & "$PSScriptRoot\Build-StoreUpload.ps1" -DevelopmentIdentityDraft -Version '1.0.0.1' -PrepareOnly } 'Major.Minor.Build.0'
    Assert-Rejected { & "$PSScriptRoot\Build-StoreUpload.ps1" -IdentityName 'BiliXbox.Community' -IdentityPublisher 'CN=BiliXboxDev' -PublisherDisplayName 'BiliXbox Community' -PrepareOnly } 'Development identity'
    $result = & "$PSScriptRoot\Build-StoreUpload.ps1" -DevelopmentIdentityDraft -Version '1.0.0.0' -PrepareOnly -OutputDirectory $scratch | Where-Object { $_ -isnot [string] } | Select-Object -Last 1
    $plan = Get-Content -LiteralPath $result.BuildPlan -Raw | ConvertFrom-Json
    Assert-Check (-not $plan.PartnerCenterIdentityProvided) 'Draft must not claim a Partner Center identity.'
    Assert-Check ($plan.SubmissionStatus -match 'DO NOT SUBMIT') 'Draft must be explicitly marked non-submittable.'
    Assert-Check (-not $plan.AutomaticSubmission) 'Build must not submit to the Store.'
    foreach ($flag in @('/p:Configuration=Release', '/p:Platform=x64', '/p:UseDotNetNativeToolchain=true',
                        '/p:UapAppxPackageBuildMode=StoreUpload', '/p:AppxSymbolPackageEnabled=true',
                        '/p:AppxPackageSigningEnabled=false', '/p:EnableDirectStoreSubmission=false', '/p:AppxAutoIncrementPackageRevision=false')) {
        Assert-Check ($plan.MSBuildArguments -contains $flag) "Missing required build flag: $flag"
    }
    [xml]$draftManifest = Get-Content -LiteralPath (Join-Path $result.SourceDirectory 'Package.appxmanifest') -Raw
    Assert-Check ([string]$draftManifest.Package.Identity.Version -ceq '1.0.0.0') 'Draft version override failed.'
    Assert-Check ([string]$draftManifest.Package.Properties.DisplayName -ceq '弹幕舱') 'Draft display name override failed.'
    Assert-Check (@(Get-ChildItem -LiteralPath $result.SourceDirectory -Recurse -File | Where-Object Extension -in @('.pfx', '.p12', '.key')).Count -eq 0) 'Private keys must not enter source snapshot.'
    $associationPath = Join-Path $scratch 'Package.StoreAssociation.xml'
    @'
<StoreAssociation xmlns="http://schemas.microsoft.com/appx/2010/store-association">
  <PublisherDisplayName>Example test publisher</PublisherDisplayName>
  <Publisher>CN=00000000-0000-0000-0000-000000000001</Publisher>
  <ProductReservedInfo><MainPackageIdentityName>Example.Tests.DanmuCabin</MainPackageIdentityName></ProductReservedInfo>
</StoreAssociation>
'@ | Set-Content -LiteralPath $associationPath -Encoding utf8
    $associated = & "$PSScriptRoot\Build-StoreUpload.ps1" -StoreAssociationPath $associationPath -PrepareOnly -OutputDirectory $scratch | Where-Object { $_ -isnot [string] } | Select-Object -Last 1
    [xml]$associatedManifest = Get-Content -LiteralPath (Join-Path $associated.SourceDirectory 'Package.appxmanifest') -Raw
    Assert-Check ([string]$associatedManifest.Package.Identity.Name -ceq 'Example.Tests.DanmuCabin') 'Associated name was not copied exactly.'
    Assert-Check ([string]$associatedManifest.Package.Identity.Publisher -ceq 'CN=00000000-0000-0000-0000-000000000001') 'Associated publisher was not copied exactly.'
    Assert-Check ([string]$associatedManifest.Package.Properties.PublisherDisplayName -ceq 'Example test publisher') 'Associated publisher display name was not copied exactly.'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $associated.SourceDirectory 'Package.StoreAssociation.xml'))) 'Association account data must not be copied into source snapshot.'
    Assert-Check ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ceq $originalHash) 'Original development manifest was changed.'
    Assert-Rejected { & "$PSScriptRoot\Test-StoreUpload.ps1" -UploadPath (Join-Path $scratch 'Xbox-Test.zip') -IdentityName 'BiliXbox.Community' -IdentityPublisher 'CN=BiliXboxDev' -PublisherDisplayName 'BiliXbox Community' -DevelopmentIdentityDraft } 'not the Xbox test ZIP'
    Assert-Rejected { & "$PSScriptRoot\Test-StoreUpload.ps1" -UploadPath (Join-Path $scratch 'Store.appxupload') -IdentityName 'BiliXbox.Community' -IdentityPublisher 'CN=BiliXboxDev' -PublisherDisplayName 'BiliXbox Community' } 'Development identity'
    [pscustomobject]@{ Result = 'PASS'; Checks = $checks; BuildOrSubmissionPerformed = $false; SourceManifestUnchanged = $true }
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $prefix = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup path escaped artifacts.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
