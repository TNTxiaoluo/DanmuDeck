#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ('BiliXbox.PlaybackQualityTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile the current production source.'
}
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskReferencesFolder = Join-Path $PSHOME 'ref'
if (-not (Test-Path -LiteralPath $taskReferencesFolder)) {
    throw 'This test requires PowerShell 7 with its bundled .NET reference assemblies.'
}
$taskFiles = @(
    'src\BiliXbox\Models\ApiModels.cs'
    'src\BiliXbox\Models\RecommendationPage.cs'
    'src\BiliXbox\Models\DanmakuBlockRule.cs'
    'src\BiliXbox\Models\DanmakuMask.cs'
    'src\BiliXbox\Services\BilibiliApiException.cs'
    'src\BiliXbox\Services\WbiSigner.cs'
    'src\BiliXbox\Services\ProtectedCookieStore.cs'
    'src\BiliXbox\Services\BilibiliClient.cs'
    'src\BiliXbox\Services\BilibiliClient.CommentReplies.cs'
    'src\BiliXbox\Services\BilibiliClient.Recommendations.cs'
    'src\BiliXbox\Services\BilibiliClient.Danmaku.cs'
    'src\BiliXbox\Services\WebsiteSearchParser.cs'
    'src\BiliXbox\Services\DashManifestBuilder.cs'
    'src\BiliXbox\Services\PlaybackCodecSupport.cs'
)
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using BiliXbox.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
'@
$taskSource += [Environment]::NewLine
foreach ($taskFile in $taskFiles) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '') + [Environment]::NewLine
}

# Reuse the existing no-network WinRT/storage doubles and production headers
# assertions. The transport exposes no real account, cookies or persistent files.
$taskStorage = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\ApiLayer.Stubs.cs'))
$taskHttpStart = $taskStorage.IndexOf('namespace Windows.Web.Http', [StringComparison]::Ordinal)
if ($taskHttpStart -lt 0) { throw 'Cannot locate shared storage doubles.' }
$taskSource += $taskStorage.Substring(0, $taskHttpStart)
$taskTransport = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\VideoDetail.Stubs.cs'))
$taskTestsStart = $taskTransport.IndexOf('    public static class Tests', [StringComparison]::Ordinal)
if ($taskTestsStart -lt 0) { throw 'Cannot locate shared HTTP doubles.' }
$taskSource += $taskTransport.Substring(0, $taskTestsStart) + '}' + [Environment]::NewLine
$taskSource += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\PlaybackQuality.Stubs.cs'))
$taskReferences = @((Get-ChildItem -LiteralPath $taskReferencesFolder -Filter '*.dll').FullName +
    [Newtonsoft.Json.JsonConvert].Assembly.Location)
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskReferences `
    -CompilerOptions '/langversion:7.3', '/nowarn:1701'
[BiliXbox.PlaybackQualityTests.Tests]::Run().GetAwaiter().GetResult()
