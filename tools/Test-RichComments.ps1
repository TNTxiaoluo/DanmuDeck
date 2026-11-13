#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.RichCommentTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskFiles = @(
    'src\BiliXbox\Models\ApiModels.cs'
    'src\BiliXbox\Models\VideoInteractionModels.cs'
    'src\BiliXbox\Models\RecommendationPage.cs'
    'src\BiliXbox\Models\DanmakuBlockRule.cs'
    'src\BiliXbox\Models\DanmakuMask.cs'
    'src\BiliXbox\Services\BilibiliApiException.cs'
    'src\BiliXbox\Services\WbiSigner.cs'
    'src\BiliXbox\Services\ProtectedCookieStore.cs'
    'src\BiliXbox\Services\BilibiliClient.cs'
    'src\BiliXbox\Services\BilibiliClient.Interactions.cs'
    'src\BiliXbox\Services\BilibiliClient.CommentInteractions.cs'
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
foreach ($taskFile in $taskFiles) {
    $taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
$taskStorage = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\ApiLayer.Stubs.cs'))
$taskSource += $taskStorage.Substring(0, $taskStorage.IndexOf('namespace Windows.Web.Http', [StringComparison]::Ordinal))
$taskTransport = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\VideoDetail.Stubs.cs'))
$taskTransport = $taskTransport.Replace('public HttpFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> form) { }', 'public Dictionary<string, string> Values { get; } public HttpFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> form) { Values = form.ToDictionary(pair => pair.Key, pair => pair.Value); }')
$taskSource += $taskTransport.Substring(0, $taskTransport.IndexOf('    public static class Tests', [StringComparison]::Ordinal)) + '}'
$taskSource += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\RichComments.Tests.cs'))
$taskReferences = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName + [Newtonsoft.Json.JsonConvert].Assembly.Location)
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskReferences -CompilerOptions '/langversion:7.3', '/nowarn:1701'
[BiliXbox.RichCommentTests.Tests]::Run().GetAwaiter().GetResult()
