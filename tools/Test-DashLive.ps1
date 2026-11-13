#requires -Version 7.0
# Public, read-only probe: no login credentials, session changes, or posting.
[CmdletBinding()]
param(
    [ValidatePattern('^BV[0-9A-Za-z]{10}$')][string]$Bvid = 'BV1spz8B4EXv',
    [ValidateRange(1, 1000)][int]$Quality = 120,
    [ValidateSet('H264', 'HEVC')][string]$Codec = 'H264',
    [string]$ManifestPath
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-PlaybackQuality.ps1')
$taskAssembly = ('BiliXbox.PlaybackQualityTests.Tests' -as [type]).Assembly
$taskSign = $taskAssembly.GetType('BiliXbox.Services.WbiSigner', $true).GetMethod('Sign', [Reflection.BindingFlags]'Static,NonPublic')
$taskHeaders = @{ 'User-Agent'='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'; 'Referer'='https://www.bilibili.com/' }
$taskNav = Invoke-RestMethod 'https://api.bilibili.com/x/web-interface/nav' -Headers $taskHeaders -TimeoutSec 25
if ($taskNav.code -notin @(0, -101)) { throw "Public nav returned code $($taskNav.code)." }
$taskImage = [IO.Path]::GetFileNameWithoutExtension(([Uri]$taskNav.data.wbi_img.img_url).AbsolutePath)
$taskSub = [IO.Path]::GetFileNameWithoutExtension(([Uri]$taskNav.data.wbi_img.sub_url).AbsolutePath)
function Read-SignedData([string]$ApiPath, [hashtable]$Values) {
    $taskParameters = [Collections.Generic.Dictionary[string,string]]::new()
    foreach ($taskKey in $Values.Keys) { $taskParameters[$taskKey] = [string]$Values[$taskKey] }
    $taskArguments = [object[]]::new(4)
    $taskArguments[0]=$taskParameters; $taskArguments[1]=$taskImage; $taskArguments[2]=$taskSub
    $taskArguments[3]=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $taskQuery = [string]$taskSign.Invoke($null, $taskArguments)
    $taskResult = Invoke-RestMethod ('https://api.bilibili.com'+$ApiPath+'?'+$taskQuery) -Headers $taskHeaders -TimeoutSec 25
    if ($taskResult.code -ne 0) { throw "Public $ApiPath returned code $($taskResult.code)." }
    return $taskResult
}
$taskDetail = Read-SignedData '/x/web-interface/wbi/view' @{ bvid=$Bvid }
$taskPlayback = Read-SignedData '/x/player/wbi/playurl' @{ bvid=$Bvid; cid=$taskDetail.data.cid; qn=$Quality; fnval=2000; fnver=0; fourk=1 }
$taskManifest = [BiliXbox.PlaybackQualityTests.Tests]::BuildPublicManifest(($taskPlayback | ConvertTo-Json -Depth 30), $Codec -eq 'HEVC')
$taskDocument = [Xml.XmlDocument]::new()
$taskDocument.XmlResolver = $null
$taskDocument.LoadXml($taskManifest)
$taskNs = [Xml.XmlNamespaceManager]::new($taskDocument.NameTable)
$taskNs.AddNamespace('d', 'urn:mpeg:dash:schema:mpd:2011')
$taskRepresentations = $taskDocument.SelectNodes('//d:Representation', $taskNs)
if ($taskRepresentations.Count -ne 2) { throw 'Production manifest does not contain both video and audio.' }
Write-Output "PASS: $Bvid qn=$Quality returned a signed DASH catalogue and production $Codec/AAC MPD. Advertised quality IDs: $($taskPlayback.data.accept_quality -join ',')."
$taskVideo = $taskDocument.SelectSingleNode('//d:AdaptationSet[@contentType="video"]/d:Representation', $taskNs)
Write-Output "PASS: Actual public DASH video representation is $($taskVideo.GetAttribute('id')); reported quality metadata is $($taskPlayback.data.quality). No anonymous HD permissions are inferred from metadata."
foreach ($taskRepresentation in $taskRepresentations) {
    $taskUri = [Uri]$taskRepresentation.SelectSingleNode('d:BaseURL', $taskNs).InnerText
    if ($taskUri.Scheme -ne 'https') { throw 'Unsafe production media URL.' }
    $taskHttp = [Net.Http.HttpClient]::new()
    $taskRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $taskUri)
    $taskCancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(25))
    $taskResponse = $null
    try {
        $taskRequest.Headers.UserAgent.ParseAdd($taskHeaders['User-Agent'])
        $taskRequest.Headers.Referrer = [Uri]$taskHeaders['Referer']
        $taskRequest.Headers.Range = [Net.Http.Headers.RangeHeaderValue]::new(0,63)
        $taskResponse = $taskHttp.SendAsync($taskRequest, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $taskCancel.Token).GetAwaiter().GetResult()
        if ([int]$taskResponse.StatusCode -ne 206 -or $taskResponse.Content.Headers.ContentRange.From -ne 0 -or $taskResponse.Content.Headers.ContentRange.To -ne 63) { throw 'CDN did not honor bounded HTTP206 range.' }
        $taskStream = $taskResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            $taskBytes = [byte[]]::new(64); $taskCount = 0
            while ($taskCount -lt 64) {
                $taskRead = $taskStream.ReadAsync($taskBytes, [int]$taskCount, [int](64-$taskCount), $taskCancel.Token).GetAwaiter().GetResult()
                if ($taskRead -eq 0) { throw 'CDN ended the byte range early.' }
                $taskCount += $taskRead
            }
            if ([Text.Encoding]::ASCII.GetString($taskBytes,4,4) -ne 'ftyp') { throw 'DASH track does not begin with an ISO-BMFF ftyp header.' }
        } finally { $taskStream.Dispose() }
        Write-Output "PASS: $($taskRepresentation.GetAttribute('id')) track served 64 valid ISO-BMFF bytes with Referer/User-Agent and HTTP206."
    } finally {
        if ($taskResponse) { $taskResponse.Dispose() }
        $taskCancel.Dispose(); $taskRequest.Dispose(); $taskHttp.Dispose()
    }
}
if ($ManifestPath) {
    $taskOutput = [IO.Path]::GetFullPath($ManifestPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
    [IO.File]::WriteAllText($taskOutput, $taskManifest, [Text.UTF8Encoding]::new($false))
    Write-Output 'Exported production MPD locally for the actual Windows adaptive-source runtime probe; signed media addresses omitted from output.'
}
Write-Output 'Read-only DASH probe passed. Anonymous catalogue/CDN access does not establish account HD rights or Xbox decoding.'
