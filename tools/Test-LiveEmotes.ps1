#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$paths = @('src/BiliXbox/Models/ApiModels.cs','src/BiliXbox/Models/LiveModels.cs','src/BiliXbox/Models/DanmakuSettings.cs',
    'src/BiliXbox/Models/DanmakuBlockRule.cs','src/BiliXbox/Services/LiveDanmakuProtocol.cs','src/BiliXbox/Services/LiveEmoteMetadata.cs',
    'src/BiliXbox/Services/LiveEmoteContent.cs','src/BiliXbox/Controls/DanmakuFilter.cs','src/BiliXbox/Controls/DanmakuOverlay.cs',
    'tools/tests/DanmakuOverlay.Stubs.cs','tools/tests/LiveEmotes.Tests.cs') | ForEach-Object { Join-Path $root $_ }
$refs = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName + [Newtonsoft.Json.JsonConvert].Assembly.Location)
Add-Type -Path $paths -ReferencedAssemblies $refs -CompilerOptions '/langversion:7.3', '/nowarn:1701,0649'
[BiliXbox.LiveEmoteTests.Tests]::Run()
