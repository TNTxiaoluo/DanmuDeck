param([string]$RoslynDirectory)
$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\BiliXbox'))
$checked = 0
foreach ($file in [IO.Directory]::EnumerateFiles($projectDirectory, '*', [IO.SearchOption]::AllDirectories)) {
    if ($file -match '\\(bin|obj)\\') { continue }
    if ([IO.Path]::GetExtension($file) -in @('.xaml','.csproj','.appxmanifest','.xml')) {
        try { [xml][IO.File]::ReadAllText($file) | Out-Null; $checked++ }
        catch { throw "Invalid XML: $file`n$_" }
    }
}
Write-Output "PASS: $checked project/XAML/manifest XML files parse."
$xaml = [IO.File]::ReadAllText((Join-Path $projectDirectory 'MainPage.xaml'))
$code = ([IO.Directory]::EnumerateFiles($projectDirectory, 'MainPage*.cs') | ForEach-Object { [IO.File]::ReadAllText($_) }) -join [Environment]::NewLine
foreach ($match in [regex]::Matches($xaml, '(?:Click|KeyDown|ItemClick|ValueChanged|SelectionChanged)="([A-Za-z_][A-Za-z0-9_]*)"')) {
    if ($code -notmatch ('\b' + [regex]::Escape($match.Groups[1].Value) + '\s*\(')) { throw "Missing event handler: $($match.Groups[1].Value)" }
}
Write-Output 'PASS: XAML event handlers exist.'
[xml]$manifest = [IO.File]::ReadAllText((Join-Path $projectDirectory 'Package.appxmanifest'))
foreach ($attribute in $manifest.SelectNodes('//@*')) {
    if ($attribute.Value -match '^Assets\\.*\.png$') {
        $asset = Join-Path $projectDirectory $attribute.Value
        if (-not [IO.File]::Exists($asset)) { throw "Missing manifest image: $asset" }
        $bytes = [IO.File]::ReadAllBytes($asset)
        if ($bytes.Length -lt 24 -or [BitConverter]::ToString($bytes,0,8) -ne '89-50-4E-47-0D-0A-1A-0A') { throw "Invalid PNG: $asset" }
    }
}
Write-Output 'PASS: Manifest images exist and have PNG signatures.'
if (-not $RoslynDirectory) {
    $bundledCompiler = Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll'
    if (Test-Path -LiteralPath $bundledCompiler) { $RoslynDirectory = $PSHOME }
}
if ($RoslynDirectory) {
    Add-Type -Path (Join-Path $RoslynDirectory 'Microsoft.CodeAnalysis.dll')
    Add-Type -Path (Join-Path $RoslynDirectory 'Microsoft.CodeAnalysis.CSharp.dll')
    $parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp7_3)
    $syntaxCount = 0
    foreach ($file in [IO.Directory]::EnumerateFiles($projectDirectory, '*.cs', [IO.SearchOption]::AllDirectories)) {
        if ($file -match '\\(bin|obj)\\') { continue }
        $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file), $parseOptions, $file)
        $errors = @($tree.GetDiagnostics() | Where-Object Severity -eq 'Error')
        if ($errors.Count) { throw ($errors -join "`n") }
        $syntaxCount++
    }
    Write-Output "PASS: $syntaxCount C# files parse as C# 7.3. This is syntax validation, not a UWP build."
} else { Write-Warning 'Roslyn unavailable: C# syntax check skipped. Run the full Visual Studio build.' }
