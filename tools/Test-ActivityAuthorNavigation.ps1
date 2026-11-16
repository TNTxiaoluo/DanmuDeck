#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSource=@'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using BiliXbox.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
'@
$taskSource+=[Environment]::NewLine+[regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/DynamicReadTracker.cs')),'(?m)^using [^;]+;\r?\n','')
$taskSource+=[Environment]::NewLine+[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/ActivityAuthorNavigation.Tests.cs'))
foreach($taskFile in @('AccountLibraryView.Activity.cs','AccountLibraryView.Unread.cs')){
    $taskProduction=[IO.File]::ReadAllText((Join-Path $taskRoot "src/BiliXbox/Controls/$taskFile"))
    $taskSignatures=if($taskFile -like '*Unread*'){@('private void SortActivityAuthors()','private void UpdateAuthorBadge(Button button)','private void OnActivityLoaded(IReadOnlyList<CreatorDynamicItem> posts)','private void OnActivityViewed(CreatorDynamicItem post)')}else{@('private void BeginActivityVisit()','private void EndActivityVisit()','private void NavigateActivity(PlayerDirection direction)','private void RestoreActivityFocus()','private async void FocusActivityAuthor(Button button)')}
    foreach($taskSignature in $taskSignatures){
        $taskStart=$taskProduction.IndexOf($taskSignature,[StringComparison]::Ordinal);if($taskStart -lt 0){throw "Missing $taskSignature"}
        $taskBrace=$taskProduction.IndexOf('{',$taskStart);$taskEnd=$taskBrace+1;$taskDepth=1
        while($taskDepth -gt 0){if($taskProduction[$taskEnd] -eq '{'){$taskDepth++};if($taskProduction[$taskEnd] -eq '}'){$taskDepth--};$taskEnd++}
        $taskSource+="`nnamespace BiliXbox.Controls { public sealed partial class AccountLibraryView { "+$taskProduction.Substring($taskStart,$taskEnd-$taskStart)+" } }"
    }
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3','/nowarn:0649,0414'
[BiliXbox.Controls.AccountLibraryView]::RunAuthorTests()
