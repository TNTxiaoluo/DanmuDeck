#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.Controls.VideoInteractionSessionTests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\VideoInteractionSession.cs')) +
    [Environment]::NewLine + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\VideoInteractionSession.Tests.cs'))
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.Controls.VideoInteractionSessionTests]::Run()
