#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.LiveDanmakuTests.Tests' -as [type]) { throw 'Run this script in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskPaths = @(
    'src\BiliXbox\Models\LiveModels.cs'
    'src\BiliXbox\Services\LiveDanmakuProtocol.cs'
    'src\BiliXbox\Services\LiveDanmakuClient.cs'
    'tools\tests\LiveDanmaku.Stubs.cs'
    'tools\tests\LiveDanmaku.Tests.cs'
)
$taskUsings = [Collections.Generic.HashSet[string]]::new()
$taskBodies = foreach ($taskPath in $taskPaths) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskPath))
    foreach ($taskUsing in [regex]::Matches($taskText, '(?m)^using [^;]+;')) { [void]$taskUsings.Add($taskUsing.Value) }
    [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '')
}
$taskSource = ($taskUsings -join "`n") + "`n" + ($taskBodies -join "`n")
$taskReferences = @((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName + [Newtonsoft.Json.JsonConvert].Assembly.Location)
Add-Type -TypeDefinition $taskSource -ReferencedAssemblies $taskReferences -CompilerOptions '/langversion:7.3', '/nowarn:1701,0649'
[BiliXbox.LiveDanmakuTests.Tests]::Run().GetAwaiter().GetResult()
