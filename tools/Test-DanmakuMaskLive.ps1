#requires -Version 7.0
# Public read-only range probe. Uses no browser cookies or logged-in credentials.
[CmdletBinding()]
param([string]$SamplePath = (Join-Path $PSScriptRoot '..\artifacts\mask-probe.svg'), [string]$MaskUrl = '')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-PlaybackQuality.ps1')
$assembly = ('BiliXbox.PlaybackQualityTests.Tests' -as [type]).Assembly
$sign = $assembly.GetType('BiliXbox.Services.WbiSigner').GetMethod('Sign', [Reflection.BindingFlags]'Static,NonPublic')
$headers = @{ 'User-Agent'='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'; 'Referer'='https://www.bilibili.com/' }
$nav = Invoke-RestMethod 'https://api.bilibili.com/x/web-interface/nav' -Headers $headers -TimeoutSec 20
$image = [IO.Path]::GetFileNameWithoutExtension(([Uri]$nav.data.wbi_img.img_url).AbsolutePath)
$sub = [IO.Path]::GetFileNameWithoutExtension(([Uri]$nav.data.wbi_img.sub_url).AbsolutePath)
function Read-Data([string]$ApiPath, [hashtable]$Values) {
    $parameters = [Collections.Generic.Dictionary[string,string]]::new()
    foreach ($key in $Values.Keys) { $parameters[$key] = [string]$Values[$key] }
    $query = [string]$sign.Invoke($null, @($parameters, $image, $sub, [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()))
    $response = Invoke-RestMethod ('https://api.bilibili.com' + $ApiPath + '?' + $query) -Headers $headers -TimeoutSec 20
    if ($response.code -ne 0) { throw "Public $ApiPath returned code $($response.code)" }
    return $response.data
}
function Read-Range([Uri]$Uri, [long]$First, [long]$Last) {
    $client = [Net.Http.HttpClient]::new()
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
    $response = $null
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(20)
        $request.Headers.UserAgent.ParseAdd($headers['User-Agent'])
        $request.Headers.Referrer = [Uri]$headers['Referer']
        $request.Headers.Range = [Net.Http.Headers.RangeHeaderValue]::new($First,$Last)
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 206 -or $response.Content.Headers.ContentRange.From -ne $First -or $response.Content.Headers.ContentRange.To -ne $Last) { throw 'Mask CDN did not honor the bounded range.' }
        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        if ($bytes.Length -ne $Last - $First + 1) { throw 'Mask range length mismatch.' }
        return @{ Bytes=$bytes; Length=$response.Content.Headers.ContentRange.Length }
    } finally { if ($response) { $response.Dispose() }; $request.Dispose(); $client.Dispose() }
}
function Read-U32([byte[]]$Bytes, [int]$Offset) {
    return [long]$Bytes[$Offset] * 16777216 + [long]$Bytes[$Offset+1] * 65536 + [long]$Bytes[$Offset+2] * 256 + $Bytes[$Offset+3]
}
$candidates = @()
if ($MaskUrl) { $candidates = @([pscustomobject]@{ bvid='public-mask-reference'; directMask=$MaskUrl }) }
else {
    $popular = Invoke-RestMethod 'https://api.bilibili.com/x/web-interface/popular?ps=8&pn=1' -Headers $headers -TimeoutSec 20
    if ($popular.code -ne 0) { throw "Public popular request returned code $($popular.code). No video-mask availability can be inferred." }
    $candidates = @($popular.data.list)
}
$found = $false
foreach ($video in $candidates) {
    if ($video.directMask) { $url = [string]$video.directMask }
    else {
        $meta = Read-Data '/x/player/wbi/v2' @{ aid=$video.aid; cid=$video.cid }
        if (!$meta.dm_mask.mask_url) { continue }
        $url = [string]$meta.dm_mask.mask_url
    }
    if ($url.StartsWith('//')) { $url = 'https:'+$url }
    $head = Read-Range ([Uri]$url) 0 15
    if ([Text.Encoding]::ASCII.GetString($head.Bytes,0,4) -ne 'MASK' -or $head.Bytes[8] -ne 2) { throw 'Unexpected official MASK header.' }
    $count = Read-U32 $head.Bytes 12
    if ($count -lt 1 -or $count -gt 10000) { throw 'Unexpected mask index count.' }
    $index = Read-Range ([Uri]$url) 16 (15 + $count*16)
    $offset = Read-U32 $index.Bytes 12
    $next = if ($count -gt 1) { Read-U32 $index.Bytes 28 } else { $head.Length }
    if ($next-$offset -gt 2MB) { throw 'Official mask block exceeds production limit.' }
    $block = Read-Range ([Uri]$url) $offset ($next-1)
    $input = [IO.MemoryStream]::new([byte[]]$block.Bytes)
    $decoder = if ($block.Bytes[0] -eq 0x1f -and $block.Bytes[1] -eq 0x8b) { [IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress) } else { [IO.Compression.ZLibStream]::new($input,[IO.Compression.CompressionMode]::Decompress) }
    $output = [IO.MemoryStream]::new()
    try { $decoder.CopyTo($output); $decoded = $output.ToArray() } finally { $decoder.Dispose(); $input.Dispose(); $output.Dispose() }
    $length = Read-U32 $decoded 0
    if ($length -gt 1MB -or $length+12 -gt $decoded.Length) { throw 'Official mask frame length invalid.' }
    $payload = [Text.Encoding]::UTF8.GetString($decoded,12,$length)
    $prefix = 'data:image/svg+xml;base64,'
    if (!$payload.StartsWith($prefix)) { throw 'Official mask frame is not an SVG data URL.' }
    $svg = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload.Substring($prefix.Length)))
    [xml]$document = $svg
    if ($document.DocumentElement.LocalName -ne 'svg') { throw 'Official mask has no SVG root.' }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($SamplePath),$svg,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes([IO.Path]::ChangeExtension([IO.Path]::GetFullPath($SamplePath),'header.bin'),$head.Bytes)
    [IO.File]::WriteAllBytes([IO.Path]::ChangeExtension([IO.Path]::GetFullPath($SamplePath),'index.bin'),$index.Bytes)
    [IO.File]::WriteAllBytes([IO.Path]::ChangeExtension([IO.Path]::GetFullPath($SamplePath),'block.bin'),$block.Bytes)
    $found = $true
    Write-Output "PASS: $($video.bvid) real HTTP206 MASK header, $count index entries, $($block.Bytes.Length) compressed bytes, $($decoded.Length) decoded bytes, SVG viewport $($document.DocumentElement.GetAttribute('viewBox'))."
    break
}
if (!$found) { throw 'No public mask in this sample. This does not establish person-mask availability.' }
