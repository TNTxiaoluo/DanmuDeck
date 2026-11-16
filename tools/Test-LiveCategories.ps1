#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSource=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/LiveHubView.Categories.cs'))
$taskSource+=[Environment]::NewLine+[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/LiveCategories.Tests.cs'))
$taskProduction=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/LiveHubView.cs'))
foreach($taskSignature in @('private async Task LoadRoomsAsync(int page)','private void UpdateBrowseActions()')){
    $taskStart=$taskProduction.IndexOf($taskSignature,[StringComparison]::Ordinal)
    if($taskStart -lt 0){throw "Missing production method: $taskSignature"}
    $taskBrace=$taskProduction.IndexOf('{',$taskStart);$taskEnd=$taskBrace+1;$taskDepth=1
    while($taskDepth -gt 0){if($taskProduction[$taskEnd] -eq '{'){$taskDepth++};if($taskProduction[$taskEnd] -eq '}'){$taskDepth--};$taskEnd++}
    $taskSource+="`nnamespace BiliXbox.Controls { public sealed partial class LiveHubView { "+$taskProduction.Substring($taskStart,$taskEnd-$taskStart)+" } }"
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3','/nowarn:0649,0414'
[BiliXbox.Controls.LiveHubView]::RunCategoryTests().GetAwaiter().GetResult() | Out-Null
