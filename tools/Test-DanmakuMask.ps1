#requires -Version 7.0
param([string]$SvgSamplePath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using BiliXbox.Models;
'@
foreach ($file in @('Models\DanmakuMask.cs','Services\WebMaskParser.cs')) {
    $part = [IO.File]::ReadAllText((Join-Path $repo ('src\BiliXbox\' + $file)))
    $source += [regex]::Replace($part, '(?m)^using [^;]+;\r?\n', '')
}
$controller = [IO.File]::ReadAllText((Join-Path $repo 'src\BiliXbox\Controls\DanmakuMaskController.cs'))
$source += 'namespace BiliXbox.Controls {' + $controller.Substring($controller.IndexOf('    internal sealed class SvgMaskPath'))
$source += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\DanmakuMask.Tests.cs'))
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.MaskTests.Tests]::Run()
if ($SvgSamplePath) { [BiliXbox.MaskTests.Tests]::CheckPublicSvg([IO.File]::ReadAllText([IO.Path]::GetFullPath($SvgSamplePath)),[IO.File]::ReadAllBytes([IO.Path]::ChangeExtension([IO.Path]::GetFullPath($SvgSamplePath),'block.bin'))) }
