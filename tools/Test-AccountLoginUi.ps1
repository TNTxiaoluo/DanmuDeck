#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.Controls.AccountLoginDialog' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
'@
foreach ($taskFile in @('src\BiliXbox\Models\PasswordLoginModels.cs', 'src\BiliXbox\Services\BilibiliApiException.cs', 'tools\tests\AccountLoginUi.Tests.cs')) {
    $taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
$taskWorkflow = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\AccountLoginDialog.Workflow.cs'))
foreach ($taskSignature in @('private bool BeginOperation()', 'private Task EndOperationAsync(int operation)', 'private void SetFormEnabled(bool enabled)', 'private async Task LoginPasswordAsync()', 'private async Task SendSmsAsync()', 'private async Task LoginSmsAsync()', 'private void ShowLoginResult(AccountLoginResult result)', 'private void CompleteLogin()', 'private void SwitchMode()', 'private void InvalidateSmsTicket()', 'private void UpdateCooldown()', 'private static string LoginError(Exception error, string fallback, string sensitive)')) {
    $taskStart = $taskWorkflow.IndexOf($taskSignature, [StringComparison]::Ordinal)
    if ($taskStart -lt 0) { throw "Missing production method: $taskSignature" }
    $taskBrace = $taskWorkflow.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
    for (; $taskEnd -lt $taskWorkflow.Length; $taskEnd++) {
        if ($taskWorkflow[$taskEnd] -eq '{') { $taskDepth++ }
        elseif ($taskWorkflow[$taskEnd] -eq '}') { $taskDepth--; if ($taskDepth -eq 0) { break } }
    }
    $taskSource += "`nnamespace BiliXbox.Controls { public sealed partial class AccountLoginDialog { " + $taskWorkflow.Substring($taskStart, $taskEnd - $taskStart + 1) + " } }`n"
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649'
[BiliXbox.Controls.AccountLoginDialog]::RunTests().GetAwaiter().GetResult() | Out-Null
