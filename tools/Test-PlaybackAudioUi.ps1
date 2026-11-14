#requires -Version 7.0
# Compiles the unchanged audio UI partial and model; native XAML, network and
# source switching are controlled collaborators, never real account writes.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskParts = foreach($taskFile in @('src/BiliXbox/Models/ApiModels.cs', 'src/BiliXbox/MainPage.Audio.cs', 'tools/tests/PlaybackAudioUi.Tests.cs')) {
    [IO.File]::ReadAllText((Join-Path $taskRoot $taskFile))
}
$taskUsings = [Collections.Generic.HashSet[string]]::new()
$taskBodies = foreach($taskPart in $taskParts) {
    foreach($taskUsing in [regex]::Matches($taskPart, '(?m)^using [^;]+;')) { [void]$taskUsings.Add($taskUsing.Value) }
    [regex]::Replace($taskPart, '(?m)^using [^;]+;\r?\n', '')
}
Add-Type -TypeDefinition (($taskUsings -join "`n") + "`n" + ($taskBodies -join "`n")) -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414,0067'
[BiliXbox.MainPage]::RunAudioUiTests().GetAwaiter().GetResult()
