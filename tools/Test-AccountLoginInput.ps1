#requires -Version 7.0
<#
.SYNOPSIS
Exercises production login key activation and native-editor/back ownership.
.DESCRIPTION
Uses the real InlineNativeTextInputSession and extracted login input methods.
UWP event/dispatcher/focus substitutes verify mapped Xbox A/B, handled routing,
single-press country opening, cancellation and queued PasswordBox focus. Native
keyboard rendering, the actual XAML replacement tree and Xbox IME require a
device; no login, SMS or network request is made by this harness.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.Controls.AccountLoginDialog' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskSource = @'
using System;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
'@
$taskStubs = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\NativeTextInput.Stubs.cs'))
$taskStubs = $taskStubs.Substring(0, $taskStubs.IndexOf('namespace BiliXbox.NativeTextInputTests'))
$taskStubs = $taskStubs.Replace('GamepadA, Enter }', 'GamepadA, Enter, NavigationAccept, NavigationCancel, Space, Back, None }')
$taskStubs = $taskStubs.Replace('public enum FocusState { Programmatic }', 'public enum FocusState { Programmatic, Keyboard }')
$taskStubs = $taskStubs.Replace('public sealed class TextBox', 'public sealed partial class TextBox')
$taskStubs = $taskStubs.Replace('public Windows.System.VirtualKey Key;', 'public Windows.System.VirtualKey Key; public Windows.System.VirtualKey OriginalKey; public Windows.UI.Core.CorePhysicalKeyStatus KeyStatus;')
$taskStubs = $taskStubs.Replace('public static void SetName(object control, string name) { }', 'public static void SetName(object control, string name) { } public static void SetAutomationId(object control, string id) { }')
$taskSource += "`n" + $taskStubs
$taskInline = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\InlineNativeTextInputSession.cs'))
$taskSource += "`n" + [regex]::Replace($taskInline, '(?m)^using [^;]+;\r?\n', '')
foreach ($taskGroup in @(
    @{ Path = 'src\BiliXbox\Controls\AccountLoginDialog.cs'; Signatures = @('private void AttachEditor(TextBox editor)', 'private PasswordBox PasswordEditor()') },
    @{ Path = 'src\BiliXbox\Controls\AccountLoginDialog.Input.cs'; Signatures = @('private static bool IsAcceptKey(KeyRoutedEventArgs args)', 'private static bool IsBackKey(KeyRoutedEventArgs args)', 'private void CountryKeyDown(object sender, KeyRoutedEventArgs args)', 'private async Task EditTextAsync(TextBox previous)', 'private void EditPassword()', 'private async Task ShowPasswordKeyboardAsync(PasswordBox editor, int generation)', 'private void PaneShowing(InputPane sender, InputPaneVisibilityEventArgs args)', 'private void PaneHiding(InputPane sender, InputPaneVisibilityEventArgs args)', 'private void EndPassword(bool focusButton)', 'private void EndInput(bool focusButton)', 'public bool RequestBack()', 'private void DialogKeyDown(object sender, KeyRoutedEventArgs args)', 'private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)') }
)) {
    $taskFileSource = [IO.File]::ReadAllText((Join-Path $taskRoot $taskGroup.Path))
    foreach ($taskSignature in $taskGroup.Signatures) {
        $taskStart = $taskFileSource.IndexOf($taskSignature, [StringComparison]::Ordinal)
        if ($taskStart -lt 0) { throw "Missing production method: $taskSignature" }
        $taskBrace = $taskFileSource.IndexOf('{', $taskStart); $taskDepth = 0; $taskEnd = $taskBrace
        for (; $taskEnd -lt $taskFileSource.Length; ++$taskEnd) {
            if ($taskFileSource[$taskEnd] -eq '{') { ++$taskDepth }
            elseif ($taskFileSource[$taskEnd] -eq '}') { --$taskDepth; if ($taskDepth -eq 0) { break } }
        }
        $taskSource += "`nnamespace BiliXbox.Controls { public sealed partial class AccountLoginDialog { " + $taskFileSource.Substring($taskStart, $taskEnd - $taskStart + 1) + " } }`n"
    }
}
$taskSource += "`n" + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\AccountLoginInput.Tests.cs'))
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0649'
[BiliXbox.Controls.AccountLoginDialog]::RunInputTests()
