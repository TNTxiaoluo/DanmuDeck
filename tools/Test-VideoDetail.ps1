#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ('BiliXbox.VideoDetailTests.Tests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process to compile the current production source.'
}

$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskReferencesFolder = Join-Path $PSHOME 'ref'
if (-not (Test-Path -LiteralPath $taskReferencesFolder)) {
    throw 'This test requires PowerShell 7 with its bundled .NET reference assemblies.'
}

# Compile the actual service, models, signer, error type, and cookie store. The only
# substitutes are Windows Runtime transport, crypto, and storage APIs unavailable
# to standalone .NET. The transport cannot reach the network or read credentials.
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
$taskImports = @'
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
$taskSource = $taskImports + [Environment]::NewLine
foreach ($taskFile in $taskFiles) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '') + [Environment]::NewLine
}

# Reuse only the existing crypto/storage doubles, before its HTTP and test types.
$taskSharedStubs = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\ApiLayer.Stubs.cs'))
$taskHttpStart = $taskSharedStubs.IndexOf('namespace Windows.Web.Http', [StringComparison]::Ordinal)
if ($taskHttpStart -lt 0) { throw 'Cannot locate the shared crypto/storage test doubles.' }
$taskSource += $taskSharedStubs.Substring(0, $taskHttpStart)
$taskSource += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\VideoDetail.Stubs.cs'))
$taskReferences = @((Get-ChildItem -LiteralPath $taskReferencesFolder -Filter '*.dll').FullName +
    [Newtonsoft.Json.JsonConvert].Assembly.Location)
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskReferences `
    -CompilerOptions '/langversion:7.3', '/nowarn:1701'
[BiliXbox.VideoDetailTests.Tests]::Run().GetAwaiter().GetResult()
