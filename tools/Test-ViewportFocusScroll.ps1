#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.ViewportScrollTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskSource = 'using System.Threading.Tasks;' + [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\ViewportFocusScroll.cs'))
foreach ($taskFile in @('tools\tests\ViewportFocusScroll.Tests.cs')) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
    $taskSource += [Environment]::NewLine + [regex]::Replace($taskText, '(?m)^using [^;]+;\r?\n', '')
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0067'
[BiliXbox.ViewportScrollTests.Tests]::Run()
