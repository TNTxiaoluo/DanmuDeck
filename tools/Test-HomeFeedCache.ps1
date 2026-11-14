#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.HomeFeedCacheTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSources = @(
    (Join-Path $taskRoot 'src\BiliXbox\Controls\FeedNavigationState.cs'),
    (Join-Path $taskRoot 'src\BiliXbox\Controls\HomeFeedPageCache.cs'),
    (Join-Path $taskRoot 'src\BiliXbox\Models\ApiModels.cs'),
    (Join-Path $PSScriptRoot 'tests\HomeFeedCache.Tests.cs')
)
Add-Type -Path $taskSources -CompilerOptions '/langversion:7.3'
[BiliXbox.HomeFeedCacheTests.Tests]::Run()
