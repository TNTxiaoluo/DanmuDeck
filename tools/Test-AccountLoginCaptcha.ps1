#requires -Version 7.0
<#
.SYNOPSIS
Exercises production login verification cancellation without network or SMS.
.DESCRIPTION
Compiles the actual verification workflow, SMS gate, routed Back/Closing and
CoreWindow handler against platform substitutes. Covers pending challenge
fetches, WebView input ownership, native cancel, late completion and retries.
Provider rendering and physical Xbox input require device verification.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
'@
foreach ($taskFile in @('src\BiliXbox\Models\PasswordLoginModels.cs', 'src\BiliXbox\Services\BilibiliApiException.cs', 'src\BiliXbox\Controls\LoginCaptchaBackState.cs', 'src\BiliXbox\Controls\AccountLoginDialog.CaptchaBack.cs', 'tools\tests\AccountLoginCaptcha.Tests.cs')) {
    $taskSource += "`n" + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
foreach ($taskGroup in @(
    @{ Path = 'src\BiliXbox\Controls\AccountLoginDialog.Workflow.cs'; Signatures = @('private async Task<LoginCaptchaProof> VerifyCaptchaAsync(CancellationToken token)', 'private static async Task<LoginCaptchaChallenge> AwaitChallengeAsync(Task<LoginCaptchaChallenge> request, CancellationToken token)', 'private static async Task ObserveCancelledChallengeAsync(Task<LoginCaptchaChallenge> request)', 'private void CancelChallenge()', 'private void CaptchaWindowKeyDown(CoreWindow sender, KeyEventArgs args)', 'private void DetachCaptchaBackInput()', 'private bool BeginOperation()', 'private Task EndOperationAsync(int operation)', 'private void SetFormEnabled(bool enabled)', 'private async Task SendSmsAsync()', 'private void UpdateCooldown()', 'private static string LoginError(Exception error, string fallback, string sensitive)') },
    @{ Path = 'src\BiliXbox\Controls\AccountLoginDialog.Input.cs'; Signatures = @('public bool RequestBack()', 'private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)') }
)) {
    $taskText = [IO.File]::ReadAllText((Join-Path $taskRoot $taskGroup.Path))
    foreach ($taskSignature in $taskGroup.Signatures) {
        $taskStart = $taskText.IndexOf($taskSignature, [StringComparison]::Ordinal)
        if ($taskStart -lt 0) { throw "Missing production method: $taskSignature" }
        $taskBrace = $taskText.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
        for (; $taskEnd -lt $taskText.Length; ++$taskEnd) {
            if ($taskText[$taskEnd] -eq '{') { ++$taskDepth }
            elseif ($taskText[$taskEnd] -eq '}') { --$taskDepth; if ($taskDepth -eq 0) { break } }
        }
        $taskSource += "`nnamespace BiliXbox.Controls { public sealed partial class AccountLoginDialog { " + $taskText.Substring($taskStart, $taskEnd - $taskStart + 1) + " } }`n"
    }
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649,0414'
[BiliXbox.Controls.AccountLoginDialog]::RunCaptchaTests().GetAwaiter().GetResult() | Out-Null
