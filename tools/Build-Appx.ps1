param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$MSBuildPath,
    [string]$CertificatePath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\AppPackages')
)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\BiliXbox.sln'))
if (-not $MSBuildPath) {
    $localMSBuild = Join-Path $workspaceRoot '.tools\vs2022\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path -LiteralPath $localMSBuild) { $MSBuildPath = $localMSBuild }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio 2022 with UWP C# tools and Windows SDK 10.0.19041.0 first.' }
        $MSBuildPath = (& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1)
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) { throw 'Visual Studio MSBuild was not found. Set -MSBuildPath explicitly.' }
$packageDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
$logDirectory = Join-Path $workspaceRoot 'artifacts\logs'
[IO.Directory]::CreateDirectory($logDirectory) | Out-Null
$logFile = Join-Path $logDirectory ("build-$Configuration-x64.log")
$packagesDirectory = Join-Path $workspaceRoot '.tools\nuget'
$arguments = @($solutionPath,'/restore','/m','/nologo','/v:minimal',"/p:Configuration=$Configuration",'/p:Platform=x64','/p:GenerateAppxPackageOnBuild=true','/p:UapAppxPackageBuildMode=SideloadOnly','/p:AppxBundle=Never',"/p:AppxPackageDir=$packageDirectory",("/flp:logfile={0};verbosity=normal;encoding=UTF-8" -f $logFile),"/p:RestorePackagesPath=$packagesDirectory")
if ($CertificatePath) {
    # Use an unencrypted development PFX only, or sign in VS with a secure certificate workflow.
    # The PFX publisher must equal CN=BiliXboxDev (or update Package.appxmanifest first).
    $certificate = (Resolve-Path -LiteralPath $CertificatePath).Path
    $arguments += @('/p:AppxPackageSigningEnabled=true',"/p:PackageCertificateKeyFile=$certificate")
} else {
    $arguments += '/p:AppxPackageSigningEnabled=false'
    Write-Warning 'This builds an unsigned package. Sign in Visual Studio before installing on Xbox; see README.'
}
& $MSBuildPath @arguments
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }
Write-Output "Package output: $packageDirectory"
Write-Output "Build log: $logFile"
