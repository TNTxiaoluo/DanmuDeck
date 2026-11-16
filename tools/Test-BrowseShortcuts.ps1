#requires -Version 7.0
# Compile production face-button/chord routing with external device/UI substitutes.
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskPage=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/MainPage.ViewingShortcuts.cs'))
$taskMethods=''
foreach($taskName in @('HandleViewingChord','ReleaseViewingChord','PollViewingChord','RunViewingChord','RunTargetedCommentReply')){
    $taskMatch=[regex]::Match($taskPage,'(?m)^        private (?:async )?(?:void|bool) '+$taskName+'\([^\r\n]*\)\s*\{')
    if(-not $taskMatch.Success){throw "Production method missing: $taskName"}
    $taskBrace=$taskPage.IndexOf('{',$taskMatch.Index);$taskEnd=$taskBrace+1;$taskDepth=1
    while($taskDepth -gt 0){if($taskPage[$taskEnd] -eq '{'){$taskDepth++};if($taskPage[$taskEnd] -eq '}'){$taskDepth--};$taskEnd++}
    $taskMethods+=$taskPage.Substring($taskMatch.Index,$taskEnd-$taskMatch.Index)+[Environment]::NewLine
}
$taskSource=@'
using System;
using System.Threading.Tasks;
using BiliXbox.Controls;
using Windows.System;
using Windows.Gaming.Input;
'@+[Environment]::NewLine
$taskSource+=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/ShoulderChordState.cs'))
$taskSource+=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/BrowseShortcuts.Tests.cs')).Replace('/* PRODUCTION_METHODS */',$taskMethods)
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3','/nowarn:0649,0414'
[BiliXbox.MainPage]::RunBrowseTests()
