#requires -Version 7.0
[CmdletBinding()]
param([switch]$PublicReadOnly)
$ErrorActionPreference = 'Stop'
if ('BiliXbox.LoginApiTests.Tests' -as [type]) { throw 'Run this test in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskFiles = @(
    'src\BiliXbox\Models\ApiModels.cs', 'src\BiliXbox\Models\PasswordLoginModels.cs',
    'src\BiliXbox\Models\RecommendationPage.cs', 'src\BiliXbox\Models\DanmakuBlockRule.cs',
    'src\BiliXbox\Models\DanmakuMask.cs', 'src\BiliXbox\Services\BilibiliApiException.cs',
    'src\BiliXbox\Services\WbiSigner.cs', 'src\BiliXbox\Services\ProtectedCookieStore.cs',
    'src\BiliXbox\Services\BilibiliClient.cs', 'src\BiliXbox\Services\BilibiliClient.Login.cs',
    'src\BiliXbox\Services\BilibiliClient.CommentReplies.cs', 'src\BiliXbox\Services\BilibiliClient.Recommendations.cs',
    'src\BiliXbox\Services\BilibiliClient.Danmaku.cs', 'src\BiliXbox\Services\WebsiteSearchParser.cs',
    'src\BiliXbox\Services\DashManifestBuilder.cs', 'src\BiliXbox\Services\PlaybackCodecSupport.cs'
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
$taskSource += [regex]::Replace($taskStorage.Substring(0, $taskStorage.IndexOf('namespace Windows.Security.Cryptography.DataProtection', [StringComparison]::Ordinal)), '(?<!global::)\bSystem\.', 'global::System.')
$taskSource = [regex]::Replace($taskSource, 'public static class CryptographicBuffer\s*\{', 'public static class CryptographicBuffer {' + "`r`n" + '        public static byte[] DecodeFromBase64String(string text) => Convert.FromBase64String(text); public static string EncodeToBase64String(byte[] value) => Convert.ToBase64String(value);')
if (!$taskSource.Contains('DecodeFromBase64String(string text)')) { throw 'Cannot locate the crypto test-double insertion point.' }
$taskSource += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\LoginApi.Stubs.cs'))
$taskTransport = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\VideoDetail.Stubs.cs'))
$taskTransport = $taskTransport.Replace('public HttpFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> form) { }', 'public Dictionary<string, string> Values { get; } public HttpFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> form) { Values = form.ToDictionary(pair => pair.Key, pair => pair.Value); }')
$taskSource += $taskTransport.Substring(0, $taskTransport.IndexOf('    public static class Tests', [StringComparison]::Ordinal)) + '}'
$taskSource += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\LoginApi.Tests.cs'))
$taskReferences = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName + [Newtonsoft.Json.JsonConvert].Assembly.Location)
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskReferences -CompilerOptions '/langversion:7.3', '/nowarn:1701'
[BiliXbox.LoginApiTests.Tests]::Run().GetAwaiter().GetResult()
if ($PublicReadOnly) {
    # Only official unauthenticated GETs; no browser cookie loading, credentials,
    # SMS sends, QR polling, or login POSTs. Do not print challenge/key values.
    $taskHandler = [Net.Http.HttpClientHandler]::new()
    $taskHandler.UseCookies = $false
    $taskHandler.AllowAutoRedirect = $false
    $taskClient = [Net.Http.HttpClient]::new($taskHandler)
    $taskClient.Timeout = [TimeSpan]::FromSeconds(20)
    $taskClient.DefaultRequestHeaders.UserAgent.ParseAdd('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36')
    $taskClient.DefaultRequestHeaders.Referrer = [Uri]'https://passport.bilibili.com/login'
    try {
        foreach ($taskPath in @('/x/passport-login/captcha?source=main_web', '/x/passport-login/web/key', '/web/generic/country/list')) {
            $taskResponse = $taskClient.GetAsync('https://passport.bilibili.com' + $taskPath).GetAwaiter().GetResult()
            try {
                $taskJson = $taskResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                $taskSummary = [ordered]@{ Endpoint = $taskPath.Split('?')[0]; HttpStatus = [int]$taskResponse.StatusCode; ApiCode = $taskJson.code }
                switch -Wildcard ($taskPath) {
                    '*captcha*' { $taskSummary.Type = $taskJson.data.type; $taskSummary.ChallengePresent = [bool]$taskJson.data.geetest.challenge; $taskSummary.TokenPresent = [bool]$taskJson.data.token }
                    '*web/key' { $taskSummary.PublicKeyPresent = [bool]$taskJson.data.key; $taskSummary.SaltPresent = [bool]$taskJson.data.hash }
                    '*country/list' { $taskSummary.Countries = @($taskJson.data.common).Count + @($taskJson.data.others).Count }
                }
                [pscustomobject]$taskSummary | ConvertTo-Json -Compress
            } finally { $taskResponse.Dispose() }
        }
    } finally { $taskClient.Dispose() }
}
