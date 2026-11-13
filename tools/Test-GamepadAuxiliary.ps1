#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/GamepadAuxiliaryState.cs'))
$taskTests=[IO.File]::ReadAllText((Join-Path $taskRoot 'tools/tests/GamepadAuxiliary.Tests.cs'))
Add-Type -TypeDefinition ($taskSource+[Environment]::NewLine+$taskTests) -CompilerOptions '/langversion:7.3'
[BiliXbox.Controls.AuxiliaryTests]::Run()
