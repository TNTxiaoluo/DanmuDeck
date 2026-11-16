#requires -Version 7.0
# Compile the production end-of-video decision method with controlled media/navigation boundaries.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Controls;
'@
foreach ($file in @('src/BiliXbox/Models/ApiModels.cs','src/BiliXbox/Models/CreatorModels.cs','src/BiliXbox/Models/AccountLibraryModels.cs','src/BiliXbox/Models/ViewingExperience.cs','src/BiliXbox/Controls/ShoulderChordState.cs')) {
    $source += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $root $file)), '(?m)^using [^;]+;\r?\n', '')
}
$page = [IO.File]::ReadAllText((Join-Path $root 'src/BiliXbox/MainPage.ViewingExperience.cs'))
$method = [regex]::Match($page, '(?s)        private async Task<bool> AdvanceAfterEndAsync\(object source\).*?\r?\n        \}')
if (-not $method.Success) { throw 'Production end-of-video method not found.' }
$source += [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/ViewingPlayback.Tests.cs')).Replace('/* END_METHOD */', $method.Value)
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.MainPage]::RunViewingPlaybackTests().GetAwaiter().GetResult()
