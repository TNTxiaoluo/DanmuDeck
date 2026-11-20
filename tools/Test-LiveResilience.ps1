#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$production = [IO.File]::ReadAllText((Join-Path $root 'src/BiliXbox/Controls/LiveHubView.Playback.cs'))
$start = $production.IndexOf('private void DisposeMedia()', [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Live media cleanup missing.' }
$brace = $production.IndexOf('{', $start); $depth = 0; $end = $brace
for (; $end -lt $production.Length; $end++) {
    if ($production[$end] -eq '{') { $depth++ }
    elseif ($production[$end] -eq '}') { $depth--; if ($depth -eq 0) { break } }
}
$method = $production.Substring($start, $end - $start + 1)
$source = [IO.File]::ReadAllText((Join-Path $root 'src/BiliXbox/Controls/LiveMessageQueue.cs'))
$source += "`nnamespace BiliXbox.Controls { public sealed partial class LiveHubView { $method } }`n"
$source += [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/LiveResilience.Tests.cs'))
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3', '/nowarn:0067'
[BiliXbox.Controls.LiveHubView]::RunResilienceTests()
