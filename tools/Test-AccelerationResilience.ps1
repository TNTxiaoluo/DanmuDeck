#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$source='using System;using System.IO;using System.Diagnostics;using System.Linq;using System.Threading;using System.Threading.Tasks;using System.Collections.Generic;' + [Environment]::NewLine
foreach($file in @('src/BiliXbox/Controls/DialogConfirmReleaseGate.cs','src/BiliXbox/Controls/PlaybackAccelerationHealthState.cs','src/BiliXbox/Services/OrderedRangeReadStream.cs','tools/tests/AccelerationResilience.Tests.cs')) {
    $source += [regex]::Replace([IO.File]::ReadAllText((Join-Path $root $file)),'(?m)^using [^;]+;\r?\n','')+[Environment]::NewLine
}
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.AccelerationResilience.Tests]::Run().GetAwaiter().GetResult()
