#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$parts = foreach ($file in @('src/BiliXbox/Models/DanmakuSettings.cs','src/BiliXbox/Services/DanmakuSettingsStore.cs',
    'src/BiliXbox/Controls/LiveHubView.DanmakuSettings.cs','tools/tests/DanmakuSettings.Stubs.cs','tools/tests/LiveDanmakuPanel.Tests.cs')) {
    [IO.File]::ReadAllText((Join-Path $root $file))
}
$code = [IO.File]::ReadAllText((Join-Path $root 'src/BiliXbox/Controls/LiveHubView.Playback.cs'))
$start = $code.IndexOf('private void ToggleDanmaku()', [StringComparison]::Ordinal)
$brace = $code.IndexOf('{', $start); $depth = 0; $end = $brace
for (; $end -lt $code.Length; $end++) {
    if ($code[$end] -eq '{') { $depth++ }
    elseif ($code[$end] -eq '}') { $depth--; if ($depth -eq 0) { break } }
}
$parts += 'namespace BiliXbox.Controls { public sealed partial class LiveHubView {' + $code.Substring($start, $end-$start+1) + '}}'
$usings = [Collections.Generic.HashSet[string]]::new()
$bodies = foreach ($part in $parts) {
    foreach ($u in [regex]::Matches($part, '(?m)^using [^;]+;')) { [void]$usings.Add($u.Value) }
    [regex]::Replace($part, '(?m)^using [^;]+;\r?\n', '')
}
Add-Type -TypeDefinition (($usings -join "`n") + "`n" + ($bodies -join "`n")) -CompilerOptions '/langversion:7.3'
[BiliXbox.Controls.LiveHubView]::RunPanelTests()
