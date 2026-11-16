#requires -Version 7.0
# Production API tests with controlled transport; never uses an account or live write operation.
$ErrorActionPreference = 'Stop'
$harness = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-AccountLibrary.ps1'))
$harness = $harness.Replace("'src\BiliXbox\Models\ApiModels.cs'", "'src\BiliXbox\Models\ApiModels.cs', 'src\BiliXbox\Models\ViewingExperience.cs', 'src\BiliXbox\Services\BilibiliClient.Experience.cs'")
$harness = $harness.Replace('tests\AccountLibrary.Stubs.cs', 'tests\ViewingApi.Tests.cs')
$harness = $harness.Replace('BiliXbox.AccountLibraryTests.Tests', 'BiliXbox.ViewingApiTests.Tests')
$harness = $harness.Replace('$PSScriptRoot', ("'" + $PSScriptRoot.Replace("'", "''") + "'"))
Invoke-Expression $harness
