#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = 'using System;using System.Collections.Generic;using System.Diagnostics;using System.Linq;using System.Threading;using System.Threading.Tasks;using System.IO;using System.Globalization;' + [Environment]::NewLine
foreach ($file in @('src/BiliXbox/Services/ParallelRangeDownloader.cs', 'src/BiliXbox/Services/AdaptiveDownloadFailureState.cs', 'tools/tests/ParallelRangeDownloader.Tests.cs')) {
    $source += [regex]::Replace([IO.File]::ReadAllText((Join-Path $root $file)), '(?m)^using [^;]+;\r?\n', '') + [Environment]::NewLine
}
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.ParallelRangeTests.Tests]::Run().GetAwaiter().GetResult()
