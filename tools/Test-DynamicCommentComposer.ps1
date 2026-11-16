#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskProduction=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/CreatorHubView.cs'))
$taskStart=$taskProduction.IndexOf('public async Task WriteCommentAsync(bool replyToFocused = false)',[StringComparison]::Ordinal)
if($taskStart -lt 0){throw 'Production dynamic composer not found.'}
$taskBrace=$taskProduction.IndexOf('{',$taskStart);$taskEnd=$taskBrace+1;$taskDepth=1
while($taskDepth -gt 0){if($taskProduction[$taskEnd] -eq '{'){$taskDepth++};if($taskProduction[$taskEnd] -eq '}'){$taskDepth--};$taskEnd++}
$taskSource=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/CreatorCommentDraftState.cs'))
$taskSource+=[Environment]::NewLine+[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests/DynamicCommentComposer.Tests.cs'))
$taskSource+="`nnamespace BiliXbox.Controls { using System; using System.Threading.Tasks; public sealed partial class CreatorHubView { "+$taskProduction.Substring($taskStart,$taskEnd-$taskStart)+" } }"
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3','/nowarn:0649,0414'
[BiliXbox.Controls.CreatorHubView]::RunComposerTests().GetAwaiter().GetResult()
