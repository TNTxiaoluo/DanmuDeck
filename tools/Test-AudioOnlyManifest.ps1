#requires -Version 7.0
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Test-PlaybackQuality.ps1') | Out-Null
$taskType=('BiliXbox.PlaybackQualityTests.Tests' -as [type]).Assembly.GetType('BiliXbox.Services.DashManifestBuilder',$true)
$taskMethod=$taskType.GetMethod('AudioOnly',[Reflection.BindingFlags]'Static,NonPublic')
$taskChecks=0
function Assert-Audio([bool]$value,[string]$name){$script:taskChecks++;if(-not $value){throw $name}}
foreach($taskCodec in @('mp4a.40.2','ec-3','fLaC')){
    $taskXml='<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" mediaPresentationDuration="PT180S" minBufferTime="PT3S"><Period duration="PT180S"><AdaptationSet contentType="video"><Representation codecs="hvc1.2.4.L153.B0"><BaseURL>https://video.example.test/v.m4s</BaseURL></Representation></AdaptationSet><AdaptationSet contentType="audio"><Representation codecs="'+$taskCodec+'"><BaseURL>https://audio.example.test/a.m4s?token=fixture</BaseURL><SegmentBase indexRange="100-199"><Initialization range="0-99" /></SegmentBase></Representation></AdaptationSet></Period></MPD>'
    [xml]$taskAudio=$taskMethod.Invoke($null,@($taskXml))
    Assert-Audio ($taskAudio.MPD.Period.AdaptationSet.contentType -eq 'audio') 'Audio-only MPD must remove every video adaptation'
    Assert-Audio ($taskAudio.MPD.Period.AdaptationSet.Representation.codecs -eq $taskCodec) 'Original audio codec retained'
    Assert-Audio ($taskAudio.MPD.Period.AdaptationSet.Representation.BaseURL -ceq 'https://audio.example.test/a.m4s?token=fixture') 'Exact signed audio address retained'
    Assert-Audio ($taskAudio.MPD.Period.AdaptationSet.Representation.SegmentBase.indexRange -eq '100-199') 'Audio SegmentBase retained'
    Assert-Audio ($taskAudio.MPD.mediaPresentationDuration -eq 'PT180S' -and $taskAudio.MPD.minBufferTime -eq 'PT3S') 'Duration and refill timing retained'
    Assert-Audio ($taskXml.Contains('contentType="video"')) 'Original full playback manifest remains intact for foreground restore'
}
foreach($taskInvalid in @('<MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period /></MPD>','<!DOCTYPE MPD [<!ENTITY x SYSTEM "file:///private">]><MPD>&x;</MPD>')){
    $taskRejected=$false;try{$taskMethod.Invoke($null,@($taskInvalid))|Out-Null}catch{$taskRejected=$true};Assert-Audio $taskRejected 'Missing audio or DTD must be rejected'
}
"PASS: $taskChecks production audio-only manifest, original audio/URL/range retention and safe-parser assertions."
