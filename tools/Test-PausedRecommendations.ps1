#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$code = [IO.File]::ReadAllText((Join-Path $root 'src/BiliXbox/MainPage.PausedRecommendations.cs'))
$methods = ''
foreach ($signature in @('private int CurrentCollectionIndex', 'private int PreparePausedCollection',
    'private async Task OpenPausedRecommendationsAsync', 'private void ClosePausedRecommendations',
    'private async Task OpenPausedCollectionEpisodeAsync', 'private bool HandlePausedRecommendationKey', 'private void ShowPausedTab', 'private void SwitchPausedTab', 'private List<int> PausedTabs', 'private void FocusPausedTabButton', 'private bool PausedHeaderFocused', 'private void ScrollPausedIntro')) {
    $match = [regex]::Match($code, '(?ms)^        ' + [regex]::Escape($signature) + '\([^\r\n]*\)\s*\{.*?^        \}')
    if (-not $match.Success) { throw "Missing method: $signature" }
    $methods += $match.Value + [Environment]::NewLine
}
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/PausedRecommendations.Tests.cs')).Replace('/* METHODS */', $methods)
$gate = [regex]::Match($code, '(?ms)^        private bool CanOpenPausedRecommendations =>.*?;')
if (-not $gate.Success) { throw 'Production pause gate missing.' }
$source = $source.Replace('/* CAN_OPEN */', $gate.Value)
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.MainPage]::RunPausedTests().GetAwaiter().GetResult()
