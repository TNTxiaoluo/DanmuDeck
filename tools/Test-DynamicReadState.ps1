#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSource=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/DynamicReadTracker.cs'))
$taskSource+=[Environment]::NewLine+[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/DynamicReadState.Tests.cs'))
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.DynamicReadTests.Tests]::Run()
