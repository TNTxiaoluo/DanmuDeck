#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.LiveNavigationTests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskParts = @(
    [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\FeedNavigationState.cs'))
    [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\MainPage.Sections.cs'))
)
# Keep the actual platform-editor guard; the surrounding window/input and XAML
# objects are external substitutes, as in Test-InteractionNavigation.ps1.
$taskCode = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\MainPage.Gamepad.cs'))
$taskProperty = [regex]::Match($taskCode, '(?m)^        private bool IsNativeGamepadEditorActive\s*\{')
if (-not $taskProperty.Success) { throw 'Production editor input guard is missing.' }
$taskStart = $taskCode.IndexOf('{', $taskProperty.Index); $taskEnd = $taskStart + 1; $taskDepth = 1
while ($taskEnd -lt $taskCode.Length -and $taskDepth -gt 0) {
    if ($taskCode[$taskEnd] -eq '{') { $taskDepth++ }
    elseif ($taskCode[$taskEnd] -eq '}') { $taskDepth-- }
    $taskEnd++
}
$taskEditorGuard = $taskCode.Substring($taskProperty.Index, $taskEnd - $taskProperty.Index)
$taskParts += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\LiveNavigation.Tests.cs')).Replace('/* PRODUCTION_EDITOR_GUARD */', $taskEditorGuard)
$taskUsings = [Collections.Generic.HashSet[string]]::new()
$taskBodies = foreach ($taskPart in $taskParts) {
    foreach ($taskUsing in [regex]::Matches($taskPart, '(?m)^using [^;]+;')) { [void]$taskUsings.Add($taskUsing.Value) }
    [regex]::Replace($taskPart, '(?m)^using [^;]+;\r?\n', '')
}
Add-Type -TypeDefinition (($taskUsings -join "`n") + "`n" + ($taskBodies -join "`n")) -CompilerOptions '/langversion:7.3'
[BiliXbox.LiveNavigationTests]::Run().GetAwaiter().GetResult()
