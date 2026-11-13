#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ('BiliXbox.Services.ApiLogicTests' -as [type]) {
    throw 'Run this script in a fresh PowerShell process so it compiles the current production source.'
}

$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskServiceRoot = Join-Path $taskRoot 'src\BiliXbox\Services'
$taskReferencesFolder = Join-Path $PSHOME 'ref'
if (-not (Test-Path -LiteralPath $taskReferencesFolder)) {
    throw 'This test requires PowerShell 7 with its bundled .NET reference assemblies.'
}

# Compile real production files. The stubs cover only the unavailable Windows Runtime surface.
$taskImports = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Web.Http;
'@
$taskSource = $taskImports + [Environment]::NewLine
foreach ($taskName in @('BilibiliApiException.cs', 'WbiSigner.cs', 'ProtectedCookieStore.cs')) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskServiceRoot $taskName))
    $taskSource += [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '') + [Environment]::NewLine
}
$taskSource += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\ApiLayer.Stubs.cs'))
$taskReferences = @((Get-ChildItem -LiteralPath $taskReferencesFolder -Filter '*.dll').FullName +
    [Newtonsoft.Json.JsonConvert].Assembly.Location)

# PowerShell may bundle Newtonsoft built against an earlier .NET runtime; ignore only that
# reference-unification warning. This does not change the app's NuGet reference or build settings.
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskReferences `
    -CompilerOptions '/langversion:7.3', '/nowarn:1701'
[BiliXbox.Services.ApiLogicTests]::Run()
