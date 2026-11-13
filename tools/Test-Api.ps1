#requires -Version 7.0
# Read-only live probes; never print QR keys, cookies or signed media addresses.
# Critical chain: popular video -> signed detail -> MP4 URL -> bounded CDN range.
[CmdletBinding()]
param([ValidatePattern('^BV[0-9A-Za-z]{10}$')][string]$Bvid)
$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent'='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'; 'Referer'='https://www.bilibili.com/' }

# Compile the production signer with the existing test-only crypto projection.
# This runs its deterministic checks; storage and DPAPI are not tested here.
if (-not ('BiliXbox.Services.ApiLogicTests' -as [type])) { & (Join-Path $PSScriptRoot 'Test-ApiLogic.ps1') }
$signerType = ('BiliXbox.Services.ApiLogicTests' -as [type]).Assembly.GetType('BiliXbox.Services.WbiSigner', $true)
$signMethod = $signerType.GetMethod('Sign', [Reflection.BindingFlags]'Static,NonPublic')
if (-not $signMethod) { throw 'The production WBI signer was not loaded.' }

function Get-ApiData {
    param([string]$Uri, [string]$Name)
    try { $result = Invoke-RestMethod -Uri $Uri -Headers $headers -TimeoutSec 25 }
    catch { throw "$Name failed ($($_.Exception.GetType().Name)); check network or service access restrictions." }
    if ($result.code -ne 0) { throw "$Name returned API code $($result.code)." }
    return $result.data
}

$popular = Get-ApiData 'https://api.bilibili.com/x/web-interface/popular?ps=1&pn=1' 'Popular'
if (@($popular.list).Count -lt 1 -or -not $popular.list[0].bvid) { throw 'Popular returned no testable video.' }
$videoBvid = if ($Bvid) { $Bvid } else { [string]$popular.list[0].bvid }
Write-Output 'PASS: Popular returned a real video.'
Write-Output "Testing video: $videoBvid"

$nav = Invoke-RestMethod -Uri 'https://api.bilibili.com/x/web-interface/nav' -Headers $headers -TimeoutSec 25
if ($nav.code -notin @(0,-101) -or -not $nav.data.wbi_img.img_url -or -not $nav.data.wbi_img.sub_url) { throw 'Public WBI keys unavailable.' }
$image = [IO.Path]::GetFileNameWithoutExtension(([Uri]$nav.data.wbi_img.img_url).AbsolutePath)
$sub = [IO.Path]::GetFileNameWithoutExtension(([Uri]$nav.data.wbi_img.sub_url).AbsolutePath)
$parameters = [Collections.Generic.Dictionary[string,string]]::new()
$parameters['bvid'] = $videoBvid
$arguments = [object[]]::new(4)
$arguments[0] = $parameters; $arguments[1] = $image; $arguments[2] = $sub
$arguments[3] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$query = [string]$signMethod.Invoke($null, $arguments)
$detail = Get-ApiData ('https://api.bilibili.com/x/web-interface/wbi/view?' + $query) 'Signed video detail'
if ($detail.bvid -ne $videoBvid -or [long]$detail.aid -le 0 -or [long]$detail.cid -le 0 -or @($detail.pages).Count -lt 1) { throw 'Video detail returned incomplete or mismatched metadata.' }
Write-Output "PASS: Signed detail for $videoBvid returned aid/cid and $(@($detail.pages).Count) part(s)."

$playback = Get-ApiData ("https://api.bilibili.com/x/player/playurl?bvid=$videoBvid&cid=$($detail.cid)&qn=64&fnval=1&fnver=0&fourk=0&platform=html5") 'MP4 playback'
if ([string]$playback.format -notmatch 'mp4' -or @($playback.durl).Count -ne 1) { throw 'The selected video did not return a supported single MP4 source.' }
$mediaUri = [Uri]$playback.durl[0].url
if ($mediaUri.Scheme -ne 'https') { throw 'Playback address is not HTTPS.' }
Write-Output 'PASS: The same video returned a single MP4 playback source.'

$http = [Net.Http.HttpClient]::new()
$request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $mediaUri)
$cancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(25))
$response = $null
try {
    $request.Headers.UserAgent.ParseAdd($headers['User-Agent'])
    $request.Headers.Referrer = [Uri]$headers['Referer']
    $request.Headers.Range = [Net.Http.Headers.RangeHeaderValue]::new(0,63)
    $response = $http.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cancel.Token).GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne 206 -or $response.Content.Headers.ContentRange.From -ne 0 -or $response.Content.Headers.ContentRange.To -ne 63) { throw 'CDN did not return the requested HTTP 206 byte range.' }
    $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    try {
        $buffer = [byte[]]::new(64)
        $readCount = 0
        while ($readCount -lt 64) {
            $read = $inputStream.ReadAsync($buffer, [int]$readCount, [int](64-$readCount), $cancel.Token).GetAwaiter().GetResult()
            if ($read -eq 0) { throw 'CDN range response ended early.' }
            $readCount += $read
        }
        if ([Text.Encoding]::ASCII.GetString($buffer,4,4) -ne 'ftyp') { throw 'The returned bytes do not start with an MP4 file header.' }
    } finally { $inputStream.Dispose() }
    Write-Output 'PASS: The same MP4 source returned 64 valid bytes with HTTP 206.'
} finally {
    if ($response) { $response.Dispose() }
    $cancel.Dispose(); $request.Dispose(); $http.Dispose()
}

$qr = Get-ApiData 'https://passport.bilibili.com/x/passport-login/web/qrcode/generate' 'QR generation'
if (-not $qr.qrcode_key -or -not $qr.url) { throw 'Login QR response is incomplete.' }
Write-Output 'PASS: QR generation returned a usable response (not logged in or confirmed).'
# A legacy reply response can be code 0 with an empty list and false count 0.
# Exercise the actual repaired service and validate visible reply data instead.
# A fresh child process avoids mixing its WinRT test projection with the signer
# assembly already loaded above. It never reads real account or cookie storage.
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $PSScriptRoot 'Test-Comments.ps1') -Live -Bvid $videoBvid
if ($LASTEXITCODE -ne 0) { throw 'Production comment probe failed; a code-0 response alone is not sufficient.' }
Write-Output 'PASS: The current production comment client validated reply data for the same video.'
$xmlResponse = Invoke-WebRequest -Uri ("https://api.bilibili.com/x/v1/dm/list.so?oid=$($detail.cid)") -Headers $headers -TimeoutSec 25
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = 10MB
$text = [IO.StringReader]::new([string]$xmlResponse.Content)
$reader = [Xml.XmlReader]::Create($text, $settings)
try {
    $document = [Xml.XmlDocument]::new()
    $document.Load($reader)
    if ($document.DocumentElement.Name -ne 'i') { throw 'Unexpected danmaku XML root.' }
} finally { $reader.Dispose(); $text.Dispose() }
Write-Output 'PASS: Danmaku for the same video returned valid XML.'
Write-Output 'All read-only probes passed, including the full metadata/playback request chain. Xbox decoding, login confirmation and publishing still require device testing.'
