#requires -Version 7.0
<#
.SYNOPSIS
Runs production DanmakuOverlay logic against small UI stubs, without the Windows SDK.
.DESCRIPTION
Compiles the actual control and API models with Add-Type. The tests cover timeline
selection, paused coordinates, lane collisions, bounded input, and cleanup. The
stubs do not validate UWP APIs, XAML, rendering frames, or Xbox behavior.
Use a fresh PowerShell process for each run because Add-Type types cannot unload.
.EXAMPLE
pwsh -NoProfile -File .\tools\Test-Danmaku.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function New-ItemData {
    param([double] $Time, [string] $Text, [int] $Mode = 1)
    $item = [BiliXbox.Models.DanmakuItem]::new()
    $item.Time = $Time
    $item.Text = $Text
    $item.Mode = $Mode
    return $item
}

function New-Surface {
    $surface = [BiliXbox.Controls.DanmakuOverlay]::new()
    $surface.SetViewport(1280, 720)
    $surface.RaiseLoaded()
    return $surface
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$controlPath = Join-Path $repoRoot 'src\BiliXbox\Controls\DanmakuOverlay.cs'
$modelsPath = Join-Path $repoRoot 'src\BiliXbox\Models\ApiModels.cs'
$stubsPath = Join-Path $PSScriptRoot 'tests\DanmakuOverlay.Stubs.cs'
if ('BiliXbox.Controls.DanmakuOverlay' -as [type]) {
    throw 'DanmakuOverlay is already loaded. Run this script in a fresh pwsh process.'
}
$paths = @($controlPath, $modelsPath, $stubsPath,
    (Join-Path $repoRoot 'src\BiliXbox\Models\DanmakuSettings.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Models\DanmakuBlockRule.cs'),
    (Join-Path $repoRoot 'src\BiliXbox\Controls\DanmakuFilter.cs'))
Add-Type -Path $paths -CompilerOptions '/langversion:7.3'

$surface = New-Surface
try {
    $original = New-ItemData 0 ('initial ' + [char]::ConvertFromUtf32(0x1F3AE))
    $future = New-ItemData 100 'destination'
    $invalid = New-ItemData ([double]::NaN) 'invalid'
    $blank = New-ItemData 0 '   '
    $surface.Load([BiliXbox.Models.DanmakuItem[]]@($future, $invalid, $blank, $original))
    $surface.Update([TimeSpan]::Zero, $false)
    Assert-True ($surface.Children.Count -eq 1) 'Initial/unsorted timeline or invalid input filtering failed.'
    Assert-True ($surface.Children[0].Text -eq $original.Text) 'Unicode message changed.'
    Assert-True (-not $surface.IsHitTestVisible) 'Overlay can intercept player input.'

    $surface.Update([TimeSpan]::FromSeconds(0.5), $false)
    $pausedLeft = $surface.Children[0].Left
    $surface.Update([TimeSpan]::FromSeconds(0.5), $false)
    Assert-True ($surface.Children[0].Left -eq $pausedLeft) 'A paused message moved.'

    $surface.Update([TimeSpan]::FromSeconds(100), $false)
    Assert-True ($surface.Children.Count -eq 1 -and $surface.Children[0].Text -eq 'destination') 'Forward seek replayed old messages or missed the destination.'
    $surface.Update([TimeSpan]::Zero, $false)
    Assert-True ($surface.Children.Count -eq 1 -and $surface.Children[0].Text -eq $original.Text) 'Reverse seek did not reset the timeline cursor.'
    $surface.Update([TimeSpan]::FromSeconds(2), $false)
    $surface.Add((New-ItemData 2 'posted'))
    $surface.Update([TimeSpan]::FromSeconds(2), $false)
    Assert-True ($surface.Children.Count -eq 1 -and $surface.Children[0].Text -eq 'posted') 'Add failed or a posted message was displayed twice.'
    $surface.Reset()
    Assert-True ($surface.Children.Count -eq 0) 'Reset left visible messages behind.'
    Write-Host 'PASS: sorting, invalid input, Unicode, paused coordinates, seeks, Add, and Reset.'
}
finally { $surface.Dispose() }

$surface = New-Surface
try {
    $items = [System.Collections.Generic.List[BiliXbox.Models.DanmakuItem]]::new()
    for ($i = 0; $i -lt 500; $i++) { $items.Add((New-ItemData 0 ('first-' + $i))) }
    $surface.Load($items)
    $surface.Update([TimeSpan]::Zero, $false)
    $initialCount = $surface.Children.Count
    Assert-True ($initialCount -gt 0 -and $initialCount -le 12) 'Dense scrolling input exceeded the lane limit.'
    $occupiedRows = @($surface.Children | ForEach-Object { $_.Top } | Sort-Object -Unique)
    Assert-True ($occupiedRows.Count -eq $initialCount) 'Simultaneous messages collided in one scrolling lane.'

    $surface.Add((New-ItemData 0.1 'too-close'))
    $surface.Add((New-ItemData 2 'following'))
    for ($step = 1; $step -le 20; $step++) {
        $surface.Update([TimeSpan]::FromSeconds($step / 10.0), $false)
    }
    Assert-True (@($surface.Children | Where-Object { $_.Text -eq 'too-close' }).Count -eq 0) 'An unsafe entrance gap allowed a collision.'
    Assert-True (@($surface.Children | Where-Object { $_.Text -eq 'following' }).Count -eq 1) 'A later message could not reuse a safely cleared entrance.'
    foreach ($row in @($surface.Children | Group-Object Top)) {
        $messages = @($row.Group | Sort-Object Left)
        for ($i = 1; $i -lt $messages.Count; $i++) {
            $priorRight = $messages[$i - 1].Left + $messages[$i - 1].DesiredSize.Width
            Assert-True ($priorRight -le $messages[$i].Left) 'Scrolling messages overlap in one lane.'
        }
    }
    Write-Host 'PASS: 500-message flood, lane limits, safe entrance gaps, and later lane reuse.'
}
finally { $surface.Dispose() }

$surface = New-Surface
try {
    $surface.Load([BiliXbox.Models.DanmakuItem[]]@(
        (New-ItemData 0 'top' 5), (New-ItemData 0 'bottom' 4), (New-ItemData 0 'scroll' 1)))
    $surface.Update([TimeSpan]::Zero, $false)
    Assert-True ($surface.Children.Count -eq 3) 'A basic danmaku mode did not render.'
    $top = @($surface.Children | Where-Object { $_.Text -eq 'top' })[0]
    $bottom = @($surface.Children | Where-Object { $_.Text -eq 'bottom' })[0]
    $scroll = @($surface.Children | Where-Object { $_.Text -eq 'scroll' })[0]
    Assert-True ($top.Top -lt $scroll.Top -and $scroll.Top -lt $bottom.Top) 'Fixed and scrolling modes did not reserve separate vertical areas.'
    for ($step = 1; $step -le 10; $step++) {
        $surface.Update([TimeSpan]::FromSeconds($step / 2.0), $false)
    }
    Assert-True ($surface.Children.Count -eq 1 -and $surface.Children[0].Text -eq 'scroll') 'Expired fixed messages were retained or scrolling messages expired early.'

    $surface.Reset()
    $emoji = [char]::ConvertFromUtf32(0x1F3AE)
    $longText = '  ' + ($emoji * 300) + "`n"
    $surface.Load([BiliXbox.Models.DanmakuItem[]]@((New-ItemData 0 $longText)))
    $surface.Update([TimeSpan]::Zero, $false)
    $bounded = $surface.Children[0].Text
    Assert-True ($bounded.Length -le 256) 'Message text exceeded its UTF-16 bound.'
    Assert-True (-not [char]::IsHighSurrogate($bounded[$bounded.Length - 1])) 'Text truncation split a Unicode surrogate pair.'

    $surface.Visibility = [Windows.UI.Xaml.Visibility]::Collapsed
    $surface.Update([TimeSpan]::FromSeconds(0.5), $false)
    Assert-True ($surface.Children.Count -eq 0) 'Hidden overlay retained visible messages.'
    $surface.Visibility = [Windows.UI.Xaml.Visibility]::Visible
    $surface.Update([TimeSpan]::FromSeconds(0.5), $true)
    Assert-True ([Windows.UI.Xaml.Media.CompositionTarget]::SubscriberCount -eq 1) 'Playing overlay did not attach to rendering frames.'
    $surface.Update([TimeSpan]::FromSeconds(0.5), $false)
    Assert-True ([Windows.UI.Xaml.Media.CompositionTarget]::SubscriberCount -eq 0) 'Paused overlay retained its frame handler.'
    $surface.Update([TimeSpan]::FromSeconds(0.5), $true)
    $surface.RaiseUnloaded()
    Assert-True ([Windows.UI.Xaml.Media.CompositionTarget]::SubscriberCount -eq 0 -and $surface.Children.Count -eq 0) 'Unloaded overlay did not stop/clear its frame handler and visuals.'
    $surface.Dispose()
    $surface.RaiseLoaded()
    [Windows.UI.Xaml.Media.CompositionTarget]::Pulse()
    Assert-True ([Windows.UI.Xaml.Media.CompositionTarget]::SubscriberCount -eq 0 -and $surface.Children.Count -eq 0) 'Dispose left an active rendering or Loaded handler.'
    Write-Host 'PASS: three modes, expiry, bounded Unicode, visibility, frame lifecycle, unload, and disposal.'
}
finally { $surface.Dispose() }

$surface = New-Surface
try {
    $surface.Load([BiliXbox.Models.DanmakuItem[]]@((New-ItemData 0 'smooth')))
    $surface.Update([TimeSpan]::Zero, $true)
    $message = $surface.Children[0]
    Start-Sleep -Milliseconds 60
    [Windows.UI.Xaml.Media.CompositionTarget]::Pulse()
    $beforeSample = $message.Left
    Assert-True ($beforeSample -lt 1280) 'Rendering frames did not advance a playing message.'
    $surface.Update([TimeSpan]::Zero, $true)
    Assert-True ($message.Left -le $beforeSample) 'A repeated media-position sample pulled a message backwards.'
    Start-Sleep -Milliseconds 30
    [Windows.UI.Xaml.Media.CompositionTarget]::Pulse()
    Assert-True ($message.Left -lt $beforeSample) 'A repeated media sample stopped interpolation prematurely.'
    Assert-True ($message.LeftLayoutWrites -eq 1) 'Animation changed Canvas layout instead of the render transform.'
    Start-Sleep -Milliseconds 300
    [Windows.UI.Xaml.Media.CompositionTarget]::Pulse()
    $stalledLeft = $message.Left
    Start-Sleep -Milliseconds 30
    [Windows.UI.Xaml.Media.CompositionTarget]::Pulse()
    Assert-True ($message.Left -eq $stalledLeft) 'Comments ran ahead indefinitely after media samples stalled.'
    $surface.Update([TimeSpan]::FromSeconds(0.1), $false)
    $bufferedLeft = $message.Left
    Start-Sleep -Milliseconds 30
    [Windows.UI.Xaml.Media.CompositionTarget]::Pulse()
    $surface.Update([TimeSpan]::FromSeconds(0.1), $false)
    Assert-True ($message.Left -eq $bufferedLeft) 'A buffering/paused frame moved.'
    Assert-True ([Windows.UI.Xaml.Media.CompositionTarget]::SubscriberCount -eq 0) 'Buffering retained a rendering handler.'
    $surface.Update([TimeSpan]::FromSeconds(0.1), $true)
    Start-Sleep -Milliseconds 30
    [Windows.UI.Xaml.Media.CompositionTarget]::Pulse()
    Assert-True ($message.Left -lt $bufferedLeft) 'Playback did not resume after buffering.'
    Write-Host 'PASS: repeated positions never reverse motion, frames avoid layout, stalled samples freeze, and buffering resumes.'
}
finally { $surface.Dispose() }

Write-Host 'All danmaku logic tests passed using UI stubs. This is not a UWP build or Xbox rendering test.'
