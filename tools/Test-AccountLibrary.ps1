#requires -Version 7.0
[CmdletBinding()]
param([string]$PublicSeasonResponsePath)
$ErrorActionPreference = 'Stop'
if ('BiliXbox.AccountLibraryTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskFiles = @(
    'src\BiliXbox\Models\ApiModels.cs'
    'src\BiliXbox\Models\CreatorModels.cs'
    'src\BiliXbox\Models\AccountLibraryModels.cs'
    'src\BiliXbox\Models\RecommendationPage.cs'
    'src\BiliXbox\Models\DanmakuBlockRule.cs'
    'src\BiliXbox\Models\DanmakuMask.cs'
    'src\BiliXbox\Services\BilibiliApiException.cs'
    'src\BiliXbox\Services\WbiSigner.cs'
    'src\BiliXbox\Services\ProtectedCookieStore.cs'
    'src\BiliXbox\Services\BilibiliClient.cs'
    'src\BiliXbox\Services\BilibiliClient.Creator.cs'
    'src\BiliXbox\Services\BilibiliClient.Library.cs'
    'src\BiliXbox\Services\BilibiliClient.CommentReplies.cs'
    'src\BiliXbox\Services\BilibiliClient.Recommendations.cs'
    'src\BiliXbox\Services\BilibiliClient.Danmaku.cs'
    'src\BiliXbox\Services\WebsiteSearchParser.cs'
    'src\BiliXbox\Services\DashManifestBuilder.cs'
    'src\BiliXbox\Services\PlaybackFrameRatePolicy.cs', 'src\BiliXbox\Services\PlaybackOutputSizePolicy.cs', 'src\BiliXbox\Services\PlaybackCodecSupport.cs'
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
$taskStorage = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\ApiLayer.Stubs.cs'))
$taskHttpStart = $taskStorage.IndexOf('namespace Windows.Web.Http', [StringComparison]::Ordinal)
$taskSource += $taskStorage.Substring(0, $taskHttpStart)
$taskTransport = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\VideoDetail.Stubs.cs'))
$taskTransport = $taskTransport.Replace('public HttpFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> form) { }', 'public Dictionary<string, string> Values { get; } public HttpFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> form) { Values = form.ToDictionary(pair => pair.Key, pair => pair.Value); }')
$taskTestsStart = $taskTransport.IndexOf('    public static class Tests', [StringComparison]::Ordinal)
$taskSource += $taskTransport.Substring(0, $taskTestsStart) + '}' + [Environment]::NewLine
$taskSource += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\AccountLibrary.Stubs.cs'))
$taskReferences = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName +
    [Newtonsoft.Json.JsonConvert].Assembly.Location)
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskReferences `
    -CompilerOptions '/langversion:7.3', '/nowarn:1701'
[BiliXbox.AccountLibraryTests.Tests]::Run().GetAwaiter().GetResult()
if ($PublicSeasonResponsePath) {
    [BiliXbox.AccountLibraryTests.Tests]::ValidatePublicSeason([IO.File]::ReadAllText(
        (Resolve-Path -LiteralPath $PublicSeasonResponsePath).Path)).GetAwaiter().GetResult()
}

