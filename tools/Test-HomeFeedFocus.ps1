#requires -Version 7.0
<#
.SYNOPSIS
Regresses stale pager focus after a feed page or result type is replaced.
.DESCRIPTION
Compiles unchanged production replacement, restoration and direction-dispatch
methods from MainPage.Gamepad.cs. UI focus/containers/layout are substitutes;
this checks the production transition, not native XAML scrolling or Xbox input.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.HomeFeedFocusTests.MainPage' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = [IO.File]::ReadAllText((Join-Path $repoRoot 'src\BiliXbox\MainPage.Gamepad.cs'))
$methods = @(
    'BeginHomeFeedReplacement', 'CompleteHomeFeedReplacement',
    'CancelGamepadHomeFocus', 'RestoreHomeGamepadFocus', 'NavigateGamepadHome'
)
$production = ''
foreach ($name in $methods) {
    $match = [regex]::Match($source, '(?m)^        private void ' + [regex]::Escape($name) + '\([^\r\n]*\)\s*\{')
    if (-not $match.Success) { throw "Production method missing: $name" }
    $open = $source.IndexOf('{', $match.Index)
    $depth = 1
    $end = $open + 1
    while ($depth -gt 0 -and $end -lt $source.Length) {
        if ($source[$end] -eq '{') { $depth++ }
        elseif ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    if ($depth -ne 0) { throw "Unbalanced method: $name" }
    $production += $source.Substring($match.Index, $end - $match.Index) + [Environment]::NewLine
}
$harness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\HomeFeedFocus.Tests.cs'))
$harness = $harness.Replace('/* PRODUCTION_METHODS */', $production)
Add-Type -TypeDefinition $harness -CompilerOptions '/langversion:7.3', '/nowarn:0649'
[BiliXbox.HomeFeedFocusTests.MainPage]::Run()
