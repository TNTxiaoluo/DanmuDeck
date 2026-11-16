#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSource=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/MainPage.BackgroundPlayback.cs'))
$taskMethods=''
foreach($taskName in @('Viewing_EnteredBackground','Viewing_LeavingBackground','TransitionBackgroundAsync')){
    $taskMatch=[regex]::Match($taskSource,'(?m)^        private async (?:void|Task) '+$taskName+'\([^\r\n]*\)\s*\{')
    if(-not $taskMatch.Success){throw "Missing background method: $taskName"}
    $taskBrace=$taskSource.IndexOf('{',$taskMatch.Index);$taskEnd=$taskBrace+1;$taskDepth=1
    while($taskDepth -gt 0){if($taskSource[$taskEnd] -eq '{'){$taskDepth++};if($taskSource[$taskEnd] -eq '}'){$taskDepth--};$taskEnd++}
    $taskMethods+=$taskSource.Substring($taskMatch.Index,$taskEnd-$taskMatch.Index)+[Environment]::NewLine
}
$taskHarness=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/BackgroundPlayback.Tests.cs')).Replace('/* METHODS */',$taskMethods)
Add-Type -TypeDefinition $taskHarness -CompilerOptions '/langversion:7.3','/nowarn:0649,0414'
[BiliXbox.MainPage]::RunBackgroundTests().GetAwaiter().GetResult()
