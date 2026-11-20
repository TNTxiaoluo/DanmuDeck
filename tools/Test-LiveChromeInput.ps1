#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$code = [IO.File]::ReadAllText((Join-Path $root 'src/BiliXbox/Controls/LiveHubView.Navigation.cs'))
$methods = ''
foreach ($signature in @('public bool Back()', 'public void Confirm(bool repeat)', 'private bool TryRestoreLiveControls()')) {
    $start = $code.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing $signature" }
    $brace = $code.IndexOf('{', $start); $depth = 0; $end = $brace
    for (; $end -lt $code.Length; $end++) {
        if ($code[$end] -eq '{') { $depth++ }
        elseif ($code[$end] -eq '}') { $depth--; if ($depth -eq 0) { break } }
    }
    $methods += $code.Substring($start, $end-$start+1) + "`n"
}
$test = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/LiveChromeInput.Tests.cs'))
Add-Type -TypeDefinition $test.Replace('/* METHODS */', $methods) -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.Controls.LiveHubView]::RunTests()
