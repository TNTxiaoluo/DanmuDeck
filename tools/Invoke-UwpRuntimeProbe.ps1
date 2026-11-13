#requires -Version 7.0
<#
.SYNOPSIS
Temporarily registers and activates this workspace's real BiliXbox Appx.
.DESCRIPTION
Requires existing Developer Mode and installed framework dependencies. This tool
does not change policy or certificate trust. It refuses to replace an existing
BiliXbox registration, captures native activation/crash events, stops only the
returned BiliXbox PID, and removes the exact registration it created in finally.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [ValidateRange(3, 20)][int] $ObservationSeconds = 8,
    [string] $UiProbeScriptPath,
    [switch] $InteractionsOnly,
    [switch] $LiveOnly,
    [switch] $LoginOnly,
    [switch] $CommentsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageFile = (Resolve-Path -LiteralPath $PackagePath).Path
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$uiProbeFile = $null
if (($InteractionsOnly -or $LiveOnly -or $LoginOnly -or $CommentsOnly) -and -not $UiProbeScriptPath) {
    throw 'A targeted mode requires the explicitly selected workspace UI probe script.'
}
if (@(@($InteractionsOnly, $LiveOnly, $LoginOnly, $CommentsOnly) | Where-Object { $_ }).Count -gt 1) { throw 'Choose one UI probe mode.' }
if ($UiProbeScriptPath) {
    $uiProbeFile = (Resolve-Path -LiteralPath $UiProbeScriptPath -ErrorAction Stop).Path
    $allowedUiProbe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Probe-CreatorUi.ps1'))
    if (-not [string]::Equals($uiProbeFile, $allowedUiProbe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Only the workspace tools/Probe-CreatorUi.ps1 read-only UI probe is supported.'
    }
}
if (-not $packageFile.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The runtime probe only accepts a package under this workspace artifacts directory.'
}
$existing = @(Get-AppxPackage -Name 'BiliXbox.Community')
if ($existing.Count -ne 0) { throw 'BiliXbox is already registered; this probe will not replace an existing application.' }
$developerPolicy = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
if ($developerPolicy.AllowDevelopmentWithoutDevLicense -ne 1) { throw 'Developer Mode is not already enabled; no policy was changed.' }
$probeDirectory = Join-Path $artifactsRoot ('uwp-runtime-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $probeDirectory
[IO.Compression.ZipFile]::ExtractToDirectory($packageFile, $probeDirectory)
$manifestPath = Join-Path $probeDirectory 'AppxManifest.xml'
[xml] $manifest = [IO.File]::ReadAllText($manifestPath)
if ($manifest.Package.Identity.Name -ne 'BiliXbox.Community' -or $manifest.Package.Identity.Publisher -ne 'CN=BiliXboxDev') {
    throw 'Package identity does not match the authorized workspace test application.'
}
$expectedVersion = [string] $manifest.Package.Identity.Version
$report = [ordered]@{
    PackagePath = $packageFile
    PackageSha256 = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash
    Version = $expectedVersion
    ExtractedDirectory = $probeDirectory
    OriginalRegisteredCount = $existing.Count
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    Registration = $null
    ActivationHResult = $null
    ProcessId = $null
    ProcessAliveAfterObservation = $false
    StartupStatus = $null
    StartupError = $null
    StartupCompleted = $false
    UiProbe = $null
    Events = @()
    Failure = $null
    RemovedRegistration = $false
}
$registered = $null
$testProcessId = 0
$activationStart = Get-Date
try {
    Write-Host "Registering workspace test package $expectedVersion under existing Developer Mode."
    Add-AppxPackage -Register -Path $manifestPath -ErrorAction Stop
    $registered = Get-AppxPackage -Name 'BiliXbox.Community'
    if (-not $registered -or [string]$registered.Version -ne $expectedVersion -or
        [IO.Path]::GetFullPath($registered.InstallLocation).TrimEnd('\') -cne $probeDirectory.TrimEnd('\')) {
        throw 'The registration does not point to the expected isolated probe directory.'
    }
    $report.Registration = $registered.PackageFullName
    if (-not ('BiliXboxRuntimeProbe.Activation' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace BiliXboxRuntimeProbe {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string id,
            IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr items, out uint processId);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    internal class ActivationManager { }
    public sealed class ActivationResult { public int HResult; public uint ProcessId; }
    public static class Activation {
        public static ActivationResult Run(string appId) {
            var manager = (IApplicationActivationManager)new ActivationManager();
            try {
                uint processId;
                int result = manager.ActivateApplication(appId, "", 0, out processId);
                return new ActivationResult { HResult = result, ProcessId = processId };
            } finally { Marshal.FinalReleaseComObject(manager); }
        }
    }
}
'@
    }
    $activationStart = Get-Date
    $result = [BiliXboxRuntimeProbe.Activation]::Run($registered.PackageFamilyName + '!App')
    $report.ActivationHResult = '0x' + ([uint32]([long]$result.HResult -band 0xffffffffL)).ToString('X8')
    $testProcessId = [int]$result.ProcessId
    $report.ProcessId = $testProcessId
    Write-Host ('Activation HRESULT ' + $report.ActivationHResult + '; PID ' + $testProcessId)
    if ($result.HResult -lt 0) { throw ('Windows activation failed with ' + $report.ActivationHResult) }
    if ($uiProbeFile) {
        $uiReportPath = Join-Path $probeDirectory 'creator-ui-report.json'
        $uiOutputPath = Join-Path $probeDirectory 'creator-ui-output.txt'
        $uiErrorPath = Join-Path $probeDirectory 'creator-ui-error.txt'
        $uiShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $uiArguments = @(
            '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $uiProbeFile + '"'),
            '-AppProcessId', [string]$testProcessId, '-ReportPath', ('"' + $uiReportPath + '"')
        )
        if ($InteractionsOnly) { $uiArguments += '-InteractionsOnly' }
        if ($LiveOnly) { $uiArguments += '-LiveOnly' }
        if ($LoginOnly) { $uiArguments += '-LoginOnly' }
        if ($CommentsOnly) { $uiArguments += '-CommentsOnly' }
        $uiProcess = Start-Process -FilePath $uiShell -ArgumentList $uiArguments -WindowStyle Hidden -RedirectStandardOutput $uiOutputPath -RedirectStandardError $uiErrorPath -PassThru
        $uiClock = [Diagnostics.Stopwatch]::StartNew()
        while (-not $uiProcess.WaitForExit(500) -and $uiClock.Elapsed.TotalSeconds -lt 150) { }
        $uiTimedOut = -not $uiProcess.HasExited
        if ($uiTimedOut) { $uiProcess.Kill(); $uiProcess.WaitForExit(2000) | Out-Null }
        $report.UiProbe = [ordered]@{
            ScriptPath = $uiProbeFile
            Mode = if ($CommentsOnly) { 'CommentsOnly' } elseif ($LoginOnly) { 'LoginOnly' } elseif ($LiveOnly) { 'LiveOnly' } elseif ($InteractionsOnly) { 'InteractionsOnly' } else { 'Full' }
            ExitCode = if ($uiProcess.HasExited) { $uiProcess.ExitCode } else { $null }
            TimedOut = $uiTimedOut
            ReportPath = $uiReportPath
            Result = $null
        }
        if (Test-Path -LiteralPath $uiReportPath -PathType Leaf) {
            $report.UiProbe.Result = [IO.File]::ReadAllText($uiReportPath) | ConvertFrom-Json
        }
        Write-Host ('Creator UI probe exit code: ' + $report.UiProbe.ExitCode + '; report: ' + $uiReportPath)
        if (Test-Path -LiteralPath $uiOutputPath -PathType Leaf) { Write-Host ([IO.File]::ReadAllText($uiOutputPath)) }
        if (Test-Path -LiteralPath $uiErrorPath -PathType Leaf) { Write-Host ([IO.File]::ReadAllText($uiErrorPath)) }
    }
    $observationClock = [Diagnostics.Stopwatch]::StartNew()
    while ($observationClock.Elapsed.TotalSeconds -lt $ObservationSeconds) { Start-Sleep -Milliseconds 250 }
    $report.ProcessAliveAfterObservation = $testProcessId -gt 0 -and $null -ne (Get-Process -Id $testProcessId -ErrorAction SilentlyContinue)
    Write-Host ('BiliXbox alive after observation: ' + $report.ProcessAliveAfterObservation)
} catch {
    $report.Failure = $_.Exception.Message
    Write-Host ('Probe failure: ' + $_.Exception.Message)
} finally {
    # These two app-owned startup diagnostic files contain no account data.
    # Capture them before removing this fresh development registration.
    if ($registered -and $registered.Name -eq 'BiliXbox.Community' -and
        [string]$registered.Version -eq $expectedVersion -and
        [IO.Path]::GetFullPath($registered.InstallLocation).TrimEnd('\') -ceq $probeDirectory.TrimEnd('\')) {
        $localState = Join-Path $env:LOCALAPPDATA ('Packages\' + $registered.PackageFamilyName + '\LocalState')
        foreach ($diagnostic in @(
            @{ File='startup-status.txt'; Key='StartupStatus' },
            @{ File='startup-error.txt'; Key='StartupError' }
        )) {
            $diagnosticPath = Join-Path $localState $diagnostic.File
            if (Test-Path -LiteralPath $diagnosticPath -PathType Leaf) {
                $file = Get-Item -LiteralPath $diagnosticPath
                if ($file.Length -le 32768) {
                    $contents = [IO.File]::ReadAllText($diagnosticPath)
                    $fresh = $file.LastWriteTimeUtc -ge $activationStart.AddSeconds(-2).ToUniversalTime()
                    $report[$diagnostic.Key] = [ordered]@{
                        LastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('O')
                        WrittenDuringActivation = $fresh
                        Contents = $contents
                    }
                    [IO.File]::WriteAllText((Join-Path $probeDirectory $diagnostic.File), $contents, [Text.UTF8Encoding]::new($false))
                    Write-Host ($diagnostic.File + ': ' + $contents)
                }
            }
        }
        $statusFresh = $null -ne $report.StartupStatus -and $report.StartupStatus.WrittenDuringActivation
        $hasFreshError = $null -ne $report.StartupError -and $report.StartupError.WrittenDuringActivation
        $report.StartupCompleted = $report.ProcessAliveAfterObservation -and $statusFresh -and
            $report.StartupStatus.Contents.Contains('启动完成') -and -not $hasFreshError
        Write-Host ('Homepage startup completed without a fresh fallback error: ' + $report.StartupCompleted)
    }
    foreach ($log in @('Application', 'Microsoft-Windows-TWinUI/Operational', 'Microsoft-Windows-AppModel-Runtime/Admin')) {
        $found = @(Get-WinEvent -FilterHashtable @{LogName=$log; StartTime=$activationStart.AddSeconds(-1)} -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'BiliXbox' } |
            Select-Object TimeCreated, Id, ProviderName, Message)
        $report.Events += $found
    }
    if ($testProcessId -gt 0) {
        $testProcess = Get-Process -Id $testProcessId -ErrorAction SilentlyContinue
        if ($testProcess -and $testProcess.ProcessName -eq 'BiliXbox' -and $testProcess.StartTime -ge $activationStart.AddSeconds(-2)) {
            Stop-Process -Id $testProcessId -ErrorAction SilentlyContinue
        }
    }
    if ($registered -and $registered.Name -eq 'BiliXbox.Community' -and
        [string]$registered.Version -eq $expectedVersion -and
        [IO.Path]::GetFullPath($registered.InstallLocation).TrimEnd('\') -ceq $probeDirectory.TrimEnd('\')) {
        Remove-AppxPackage -Package $registered.PackageFullName -ErrorAction Stop
        $report.RemovedRegistration = @(Get-AppxPackage -Name 'BiliXbox.Community').Count -eq 0
    }
    $reportPath = Join-Path $probeDirectory 'runtime-report.json'
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Host ('Runtime report: ' + $reportPath)
    Write-Host ('Temporary registration removed: ' + $report.RemovedRegistration)
    foreach ($entry in $report.Events) { Write-Host ("{0} event {1}: {2}" -f $entry.TimeCreated, $entry.Id, $entry.Message) }
}
