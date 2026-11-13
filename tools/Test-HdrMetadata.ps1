#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.HdrMetadataTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
'@ + [Environment]::NewLine
foreach ($taskFile in @('src\BiliXbox\Services\BilibiliApiException.cs', 'src\BiliXbox\Services\DashManifestBuilder.cs',
    'tools\tests\HdrMetadata.Tests.cs')) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '') + [Environment]::NewLine
}
$taskReferences = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName +
    [Newtonsoft.Json.JsonConvert].Assembly.Location)
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskReferences -CompilerOptions '/langversion:7.3', '/nowarn:1701'
[BiliXbox.HdrMetadataTests.Tests]::Run()
