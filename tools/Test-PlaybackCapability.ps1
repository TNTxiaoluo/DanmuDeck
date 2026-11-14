#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = "using System;`nusing System.Collections.Generic;`nusing System.Globalization;`nusing System.Linq;`nusing System.Threading;`nusing System.Threading.Tasks;`n"
foreach ($taskFile in @('src/BiliXbox/Services/PlaybackFrameRatePolicy.cs', 'src/BiliXbox/Services/PlaybackOutputSizePolicy.cs', 'src/BiliXbox/Services/PlaybackCodecSupport.cs', 'tools/tests/PlaybackCapability.Tests.cs')) {
    $taskSource += [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '') + "`n"
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649'
[BiliXbox.Services.PlaybackCapabilityTests]::Run()
