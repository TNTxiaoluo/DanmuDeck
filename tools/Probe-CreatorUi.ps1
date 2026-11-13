#requires -Version 5.1
<#
.SYNOPSIS
Exercises only the already launched, exact BiliXbox PID through UI Automation.
.DESCRIPTION
Does not launch, register, sign in, publish, send global input, or change Windows
settings. Invoke-UwpRuntimeProbe can call this before its guarded cleanup.
All UI actions use providers whose ProcessId matches the supplied BiliXbox PID.
The only text written is the public video BV1spz8B4EXv (BV1J38968ECo in CommentsOnly) and the public keyword
TNTxiaoluo in the native search box, or public room ID 6 in LiveOnly mode.
LoginOnly observes the anonymous sign-in UI without writing any account data
or invoking credential submission, SMS, QR refresh, or verification actions.
CommentsOnly inspects public comment metadata, reactions without invoking them,
and the inline reply thread/full-window picture popup. No vote or content is submitted.
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][ValidateRange(1, 2147483647)][int] $AppProcessId,
    [Parameter(ParameterSetName = 'Run')][string] $ReportPath,
    [Parameter(ParameterSetName = 'Run')][switch] $SnapshotOnly,
    [Parameter(ParameterSetName = 'Run')][switch] $InteractionsOnly,
    [Parameter(ParameterSetName = 'Run')][switch] $LiveOnly,
    [Parameter(ParameterSetName = 'Run')][switch] $LoginOnly,
    [Parameter(ParameterSetName = 'Run')][switch] $CommentsOnly,
    [Parameter(Mandatory, ParameterSetName = 'Support')][switch] $ProbeSupport
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if ($ProbeSupport) {
    [pscustomobject]@{
        Runtime = [Environment]::Version.ToString()
        Apartment = [Threading.Thread]::CurrentThread.ApartmentState.ToString()
        AutomationRoot = [System.Windows.Automation.AutomationElement]::RootElement.Current.ControlType.ProgrammaticName
        ActionsPerformed = 0
    }
    return
}
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this probe using Windows PowerShell -STA.' }
if (@(@($SnapshotOnly, $InteractionsOnly, $LiveOnly, $LoginOnly, $CommentsOnly) | Where-Object { $_ }).Count -gt 1) { throw 'Choose one UI probe mode.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if (-not $ReportPath) { $ReportPath = Join-Path $artifactsRoot ('creator-ui-' + $AppProcessId + '.json') }
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
if (-not $ReportPath.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'UI report output must stay under this workspace artifacts directory.'
}
$appProcess = Get-Process -Id $AppProcessId -ErrorAction Stop
if ($appProcess.ProcessName -ne 'BiliXbox') { throw 'The supplied PID is not the BiliXbox process.' }
$expectedProcessStart = $appProcess.StartTime
$report = [ordered]@{
    ProcessId = $AppProcessId
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    AppSessionId = $appProcess.SessionId
    ProbeSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    StartupUiaSnapshots = @()
    StartupWindowRestoreAttempted = $false
    StartupWindowRestored = $false
    StartupAnchorFocusAttempted = $false
    StartupUiaLimitation = $null
    StartupExactWindowFocusAttempts = 0
    StartupExactWindowFocusConfirmed = $false
    StartupExactWindowFocusLimitation = $null
    Mode = if ($CommentsOnly) { 'CommentsOnly' } elseif ($LoginOnly) { 'LoginOnly' } elseif ($LiveOnly) { 'LiveOnly' } elseif ($InteractionsOnly) { 'InteractionsOnly' } elseif ($SnapshotOnly) { 'SnapshotOnly' } else { 'Full' }
    LiveResults = @{}
    LoginResults = @{}
    CommentResults = @{}
    InteractionOpened = $false
    InteractionAnonymousGuardPassed = $false
    InteractionMutationButtons = @()
    InteractionMutationButtonsDisabled = $false
    InteractionFocusObservations = @()
    InteractionNativeFocusVerified = $false
    InteractionClosed = $false
    InteractionReopened = $false
    InteractionCreatorOpened = $false
    InteractionCreatorVideoTabPresent = $false
    InteractionCreatorDynamicTabPresent = $false
    InteractionReturnedToVideo = $false
    InteractionMutationInvocations = 0
    InteractionLoginInvocations = 0
    InteractionPlayerButtonBounds = @()
    InteractionPlayerViewport = $null
    InteractionPlayerBoundsComplete = $false
    InteractionPlayerButtonsInsideViewport = $false
    InteractionPlayerButtonsDoNotOverlap = $false
    PagingAttempted = $false
    PageTwoReached = $false
    PagingScrollTop = $false
    PagingFirstItemFocused = $false
    PagingVerified = $false
    PagingLimitation = $null
    NativeSearchOpened = $false
    PublicBvWritten = $false
    SearchSubmitted = $false
    InlineSearchReopened = $false
    InlineSearchDraftPreserved = $false
    InlineSearchEditorRecreated = $false
    InlineSearchDialogAbsent = $false
    InlineSearchReleasedBeforeNavigation = $false
    WatchSearchReopenedAfterCancel = $false
    WatchSearchSubmitWithButtonFocus = $false
    WatchSearchLimitation = $null
    WatchSearchStages = @()
    DrawerFocusObservations = @()
    SettingsFocusCaptured = $false
    DanmakuSettingsFocusCaptured = $false
    RoutingControlsAbsentFromUia = $false
    DrawerFocusLimitation = $null
    CreatorOpened = $false
    VideoTabPresent = $false
    DynamicTabPresent = $false
    DynamicTabOpened = $false
    DynamicDataLoaded = $false
    DynamicCount = 0
    ReturnedToVideo = $false
    ReturnedToFeed = $false
    UserSearchOpened = $false
    UserQueryWritten = $false
    UserSearchSubmitted = $false
    UserModeSelected = $false
    UserResultFound = $false
    UserGridDiagnostics = @()
    UserResultDiagnostics = @()
    UserTextDiagnostics = @()
    UserDiagnosticsFailure = $null
    UserCreatorOpened = $false
    UserCreatorVideoTabPresent = $false
    UserCreatorDynamicTabPresent = $false
    UserCreatorDynamicTabOpened = $false
    UserDynamicRowsExposed = 0
    UserDynamicLimitation = $null
    ReturnedToUserResults = $false
    FocusBoundsObservations = @()
    FocusBoundsLimitations = @()
    PixelVerificationPerformed = $false
    Screenshots = @()
    ScreenshotLimitations = @()
    PublishedAnything = $false
    UsedGlobalInput = $false
    Completed = $false
    Notices = @()
    Actions = @()
    Failure = $null
}
$allowedNames = @('打开搜索键盘', '搜索', '取消', '打开 UP 主主页，查看视频和动态', '视频', '动态', '返回',
    '下一页', '相关视频', '相关视频，已选择', '用户', '用户，已选择', '返回视频列表', 'TNTxiaoluo')
$activeInlineSearch = $false
function Assert-AppProcess {
    $current = Get-Process -Id $AppProcessId -ErrorAction Stop
    if ($current.ProcessName -ne 'BiliXbox' -or $current.StartTime -ne $expectedProcessStart) {
        throw 'The original BiliXbox process has ended or its PID was reused.'
    }
}
function Find-AppElements([string] $Name, [string] $AutomationId = '', [switch] $AnyName, [switch] $IncludeOffscreen) {
    Assert-AppProcess
    $conditions = New-Object 'System.Collections.Generic.List[System.Windows.Automation.Condition]'
    $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId))
    if (-not $AnyName) { $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)) }
    if ($AutomationId) { $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)) }
    $condition = if ($conditions.Count -eq 1) { $conditions[0] } else {
        [System.Windows.Automation.AndCondition]::new($conditions.ToArray())
    }
    $found = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)
    for ($index = 0; $index -lt $found.Count; $index++) {
        $element = $found.Item($index)
        try {
            if ($element.Current.ProcessId -eq $AppProcessId -and ($IncludeOffscreen -or -not $element.Current.IsOffscreen)) { $element }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
    }
}
function Wait-AppElement([string] $Name, [string] $AutomationId = '', [int] $Seconds = 12, [switch] $AnyName) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $elements = @(Find-AppElements -Name $Name -AutomationId $AutomationId -AnyName:$AnyName | Where-Object { $_.Current.IsEnabled })
        if ($elements.Count -gt 0) { return $elements[0] }
        Start-Sleep -Milliseconds 180
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    return $null
}
function Read-AppProviderSnapshot([string] $Stage) {
    # Technical provider identity only. Never log editor values, names, account
    # labels, desktop windows or another application's controls.
    $all = @(Find-AppElements -AnyName -IncludeOffscreen)
    $visible = @($all | Where-Object { -not $_.Current.IsOffscreen })
    $anchors = @()
    foreach ($element in @($all | Select-Object -First 3)) {
        $anchors += [pscustomobject]@{
            Id = $element.Current.AutomationId; Class = $element.Current.ClassName
            Type = $element.Current.ControlType.ProgrammaticName; ProcessId = $element.Current.ProcessId
            Offscreen = $element.Current.IsOffscreen; NativeHandle = $element.Current.NativeWindowHandle
            Bounds = Read-Rect $element.Current.BoundingRectangle
        }
    }
    $report.StartupUiaSnapshots += [pscustomobject]@{
        Stage = $Stage; IncludingOffscreenCount = $all.Count; VisibleCount = $visible.Count; Anchors = $anchors
    }
    return $all
}
function Ensure-AppProviderVisible {
    try {
        $all = @(Read-AppProviderSnapshot 'before-visible-check')
        if ($all.Count -eq 0) { return }
        $visible = @($all | Where-Object { -not $_.Current.IsOffscreen })
        if ($visible.Count -gt 0) { return }
        $anchor = $all[0]
        $ancestor = $anchor
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        for ($depth = 0; $null -ne $ancestor -and $depth -lt 24; $depth++) {
            $owner = $ancestor.Current.ProcessId
            if ($owner -ne $AppProcessId) {
                if ($owner -le 0 -or (Get-Process -Id $owner -ErrorAction Stop).ProcessName -ne 'ApplicationFrameHost' -or
                    -not (Test-ElementDescendant $anchor $ancestor)) { break }
            }
            $windowPattern = $null
            if ($ancestor.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$windowPattern)) {
                $window = [System.Windows.Automation.WindowPattern]$windowPattern
                if ($window.Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Minimized) {
                    Assert-AppProcess
                    $report.StartupWindowRestoreAttempted = $true
                    $window.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
                    Start-Sleep -Milliseconds 300
                    $report.StartupWindowRestored = $window.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Minimized
                    $report.StartupAnchorFocusAttempted = $true
                    $anchor.SetFocus()
                }
                break
            }
            $ancestor = $walker.GetParent($ancestor)
        }
        $null = @(Read-AppProviderSnapshot 'after-visible-check')
    } catch { $report.StartupUiaLimitation = $_.Exception.Message }
}
function Ensure-TargetedAppFocus {
    # A visible UWP CoreWindow can still belong to a background app. Invoking
    # its provider does not activate it, and native editors then cancel when
    # they cannot obtain focus. Target only this exact app-owned CoreWindow;
    # never send global input or activate an unrelated window/host process.
    Assert-AppProcess
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId) {
        $report.StartupExactWindowFocusConfirmed = $true
        return $true
    }
    $windows = @(Find-AppElements -AnyName | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and
        $_.Current.ClassName -eq 'Windows.UI.Core.CoreWindow' -and
        $_.Current.NativeWindowHandle -ne 0 -and $_.Current.IsEnabled })
    if ($windows.Count -ne 1) {
        $report.StartupExactWindowFocusLimitation = 'No single visible, enabled CoreWindow from the exact app PID was exposed. No activation fallback was used.'
        return $false
    }
    $window = $windows[0]
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        Assert-AppProcess
        if ($window.Current.ProcessId -ne $AppProcessId -or $window.Current.IsOffscreen) { break }
        ++$report.StartupExactWindowFocusAttempts
        try {
            $window.SetFocus()
            Start-Sleep -Milliseconds 300
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            if ($null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId -and
                (Test-ElementDescendant $focused $window)) {
                $report.StartupExactWindowFocusConfirmed = $true
                return $true
            }
        } catch [System.InvalidOperationException] {
            $report.StartupExactWindowFocusLimitation = 'The exact app CoreWindow provider rejected SetFocus. No global-input or cross-process activation fallback was used.'
        } catch {
            $report.StartupExactWindowFocusLimitation = 'The exact app CoreWindow provider could not complete the bounded focus request. No global-input or cross-process activation fallback was used.'
        }
        Start-Sleep -Milliseconds 180
    }
    if (-not $report.StartupExactWindowFocusLimitation) {
        $report.StartupExactWindowFocusLimitation = 'The exact app CoreWindow did not obtain observable app focus. Native input and comment navigation remain unverified.'
    }
    return $false
}
function Invoke-AppElement($Element) {
    Assert-AppProcess
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId) { throw 'No exact-process UI action target was found.' }
    $name = $Element.Current.Name
    if ($allowedNames -notcontains $name -or $name -match '发布|登录|扫码|发送') { throw 'This UI action is outside the read-only probe allowlist.' }
    if (-not $Element.Current.IsEnabled -or $Element.Current.IsOffscreen) { throw ('The allowed UI target is not currently usable: ' + $name) }
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref] $pattern)) {
        throw ('The allowed UI target has no InvokePattern: ' + $name)
    }
    $report.Actions += $name
    ([System.Windows.Automation.InvokePattern] $pattern).Invoke()
    Write-Host ('UI probe invoked: ' + $name)
}
function Invoke-TransportButton($Element) {
    Assert-AppProcess
    $ids = @('PlayPauseButton', 'QualityButton', 'SettingsCloseButton', 'DanmakuSettingsButton', 'DanmakuSettingsCloseButton', 'WatchSearchButton')
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId -or
        $Element.Current.AutomationId -notin $ids -or -not $Element.Current.IsEnabled -or
        $Element.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) {
        throw 'Transport action must target an exact-process playback button.'
    }
    $Element.SetFocus()
    Start-Sleep -Milliseconds 200
    if ($Element.Current.IsOffscreen) { throw 'Transport button remains outside the visible app viewport.' }
    $invoke = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern] $invoke).Invoke()
    $report.Actions += $Element.Current.AutomationId
}
function Invoke-LiveReadOnly($Element) {
    Assert-AppProcess
    $ids = @('LiveSectionButton', 'HomeButton', 'PopularSectionButton', 'AnimationSectionButton', 'FilmSectionButton',
        'LiveChatButton', 'LiveGiftsButton', 'LiveDrawerCloseButton', 'LiveLeaveButton', 'LiveOpenRoomButton')
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId -or
        $Element.Current.AutomationId -notin $ids -or -not $Element.Current.IsEnabled -or $Element.Current.IsOffscreen) {
        throw 'Only exact-process read-only section/live controls may be invoked.'
    }
    $Element.SetFocus()
    Start-Sleep -Milliseconds 180
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $report.Actions += $Element.Current.AutomationId
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}
function Invoke-CommentReadOnly($Element, $Scope = $null) {
    Assert-AppProcess
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId -or
        -not $Element.Current.IsEnabled -or $Element.Current.IsOffscreen -or
        $Element.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) {
        throw 'Comment inspection requires an enabled visible exact-process button.'
    }
    $id = $Element.Current.AutomationId; $name = $Element.Current.Name
    $safeId = $id -in @('CommentsButton', 'CommentsCloseButton', 'MoreCommentsButton',
        'RepliesBackButton', 'RepliesCloseButton', 'CommentImageClose')
    $safeScopedName = $null -ne $Scope -and (Test-ElementDescendant $Element $Scope) -and
        (($id -eq 'CommentThreadDetails' -and $name -match '^查看(评论详情|回复| [0-9,]+ 条回复)$') -or
        ($id -match '^CommentPicture[0-9]+$' -and
        ($name -match '^查看图片评论 [0-9]+，共 [0-9]+ 张$' -or $name -match '^全屏查看图片 [0-9]+，共 [0-9]+ 张$')))
    if (-not ($safeId -or $safeScopedName)) { throw 'This comment action is outside the read-only allowlist. No vote or submit action is allowed.' }
    $Element.SetFocus()
    Start-Sleep -Milliseconds 220
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $report.Actions += if ($id) { $id } else { $name }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}
function Focus-CommentInspection($Element, $Scope) {
    Assert-AppProcess
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId -or
        -not (Test-ElementDescendant $Element $Scope)) { throw 'Comment focus must stay in its exact-process list or reader.' }
    $Element.SetFocus()
    Start-Sleep -Milliseconds 250
    # Focusing a native XAML button normally brings its containing comment into
    # view. A ListItem provider is the only optional scrolling fallback here.
    if ($Element.Current.IsOffscreen) {
        $ancestor = $Element; $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        for ($depth = 0; $null -ne $ancestor -and $depth -lt 20; $depth++) {
            if ($ancestor.Current.ProcessId -ne $AppProcessId -or -not (Test-ElementDescendant $ancestor $Scope)) { break }
            $scrollItem = $null
            if ($ancestor.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
                $ancestor.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scrollItem)) {
                ([System.Windows.Automation.ScrollItemPattern]$scrollItem).ScrollIntoView(); break
            }
            $ancestor = $walker.GetParent($ancestor)
        }
        Start-Sleep -Milliseconds 250
    }
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    return -not $Element.Current.IsOffscreen -and $null -ne $focused -and
        $focused.Current.ProcessId -eq $AppProcessId -and (Test-ElementDescendant $focused $Element)
}
function Test-CommentsReadOnly {
    $report.CommentResults = [ordered]@{
        PublicBv = 'BV1J38968ECo'; AnonymousEntry = $false; PublicVideoOpened = $false
        CommentListOpened = $false; RealizedRows = 0; LikeControls = 0; HateControls = 0
        LevelBadgesObserved = $false; IpLocationObserved = $false; FanNumberObserved = $false; EmoteImagesObserved = 0
        PictureDetailFound = $false; InlineThreadOpened = $false; ReplyRootCardExposed = $false; ThreadReactionControls = 0
        LikeCountLabels = @(); ThreadLikeCountLabels = @(); ThreadRows = 0; LegacyReaderAbsent = $false
        ExpandedPictureFocused = $false; ImagePopupOpened = $false; LegacyImageToolbarAbsent = $false
        ImagePopupBounds = $null; AppViewport = $null; ImagePopupCoversViewport = $false
        ImageLoadState = ''; InlineThreadRestored = $false; PictureFocusRestored = $false
        InlineThreadClosed = $false; ReturnedToVideo = $false; AdditionalCommentPages = 0; Partial = $true
        VoteInvocations = 0; PublishInvocations = 0; GlobalInputEvents = 0
        PhysicalBackKeyVerified = $false; PhysicalDirectionalInputVerified = $false; Limitation = $null
    }
    $entry = Wait-AppElement -AutomationId 'AccountButton' -AnyName -Seconds 8
    if ($null -eq $entry -or $entry.Current.Name -notin @('登录', '扫码登录')) { throw 'The comment probe requires an anonymous app session.' }
    $report.CommentResults.AnonymousEntry = $true
    $viewport = Read-AppViewport $entry; $report.CommentResults.AppViewport = Read-Rect $viewport
    Submit-PublicSearch -Query 'BV1J38968ECo'
    # Player controls appear before metadata and its comment-area id arrive.
    # Opening an empty drawer immediately does not establish an API failure.
    $owner = Wait-AppElement -Name '打开 UP 主主页，查看视频和动态' -Seconds 25
    if ($null -eq $owner) { Read-AppNotices; throw 'The public video metadata did not become ready for comment inspection.' }
    $button = Wait-AppElement -AutomationId 'CommentsButton' -AnyName -Seconds 25
    if ($null -eq $button) { Read-AppNotices; throw 'The public video did not expose its comment control.' }
    $report.CommentResults.PublicVideoOpened = $true
    Invoke-CommentReadOnly $button
    $list = Wait-AppElement -AutomationId 'CommentList' -AnyName -Seconds 10
    if ($null -eq $list) { throw 'The public comment list was not exposed.' }
    $report.CommentResults.CommentListOpened = $true
    $pictures = @(); $details = @(); $elements = @(); $wait = [Diagnostics.Stopwatch]::StartNew()
    do {
        $elements = @(Find-AppElements -AnyName -IncludeOffscreen | Where-Object { Test-ElementDescendant $_ $list })
        $pictures = @($elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $_.Current.AutomationId -match '^CommentPicture[0-9]+$' -and $_.Current.Name -match '^查看图片评论 [0-9]+，共 [0-9]+ 张$' })
        $details = @($elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $_.Current.AutomationId -eq 'CommentThreadDetails' })
        if ($pictures.Count -gt 0 -or ($details.Count -gt 0 -and $wait.Elapsed.TotalSeconds -ge 3)) { break }
        Start-Sleep -Milliseconds 250
    } while ($wait.Elapsed.TotalSeconds -lt 25)
    # List headers are not ListItems. Count actual public comment containers,
    # never infer loaded rows from the drawer or its empty-state controls.
    $report.CommentResults.RealizedRows = @($elements | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and -not $_.Current.IsOffscreen }).Count
    $likeControls = @($elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.AutomationId -eq 'CommentLike' })
    $report.CommentResults.LikeControls = $likeControls.Count
    $report.CommentResults.LikeCountLabels = @($likeControls | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^(取消)?点赞，[0-9]' })
    $report.CommentResults.HateControls = @($elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.AutomationId -eq 'CommentDislike' }).Count
    $report.CommentResults.LevelBadgesObserved = @($elements | Where-Object { $_.Current.Name -match '^LV[0-6]$' }).Count -gt 0
    $report.CommentResults.IpLocationObserved = @($elements | Where-Object { $_.Current.Name -match 'IP属地' }).Count -gt 0
    $report.CommentResults.FanNumberObserved = @($elements | Where-Object { $_.Current.Name -match 'CD[.·:]?\s*[0-9]' }).Count -gt 0
    $report.CommentResults.EmoteImagesObserved = @($elements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Image -and $_.Current.Name -match '^\[.+\]$' }).Count
    $threadEntry = if ($pictures.Count -gt 0) { $pictures[0] } elseif ($details.Count -gt 0) { $details[0] } else { $null }
    if ($null -eq $threadEntry) {
        $report.CommentResults.Limitation = 'This anonymous public response did not expose a comment detail action. Comment/image navigation was not verified; no test-only app data was injected.'
        $hints = @(Find-AppElements -AutomationId 'CommentHint' -AnyName)
        if ($hints.Count -gt 0) { $report.CommentResults.Limitation += ' ' + $hints[0].Current.Name }
        Read-AppNotices
        Save-AppScreenshot $list 'public-comment-list-partial'
    } else {
        $report.CommentResults.PictureDetailFound = $pictures.Count -gt 0
        if (-not (Focus-CommentInspection $threadEntry $list)) { throw 'The public detail action could not receive visible app focus.' }
        Invoke-CommentReadOnly $threadEntry $list
        $thread = Wait-AppElement -AutomationId 'ReplyList' -AnyName -Seconds 8
        if ($null -eq $thread) { throw 'The public detail action did not open the inline reply thread.' }
        $report.CommentResults.InlineThreadOpened = $true
        $report.CommentResults.LegacyReaderAbsent = @(Find-AppElements -AutomationId 'CommentReaderDialog' -AnyName -IncludeOffscreen).Count -eq 0
        $threadElements = @(Find-AppElements -AnyName -IncludeOffscreen | Where-Object { Test-ElementDescendant $_ $thread })
        $rootCard = @($threadElements | Where-Object { $_.Current.AutomationId -eq 'ReplyRootCard' })
        $report.CommentResults.ReplyRootCardExposed = $rootCard.Count -gt 0
        $report.CommentResults.ThreadRows = @($threadElements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem }).Count
        $reactions = @($threadElements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $_.Current.AutomationId -in @('CommentLike', 'CommentDislike') })
        $report.CommentResults.ThreadReactionControls = $reactions.Count
        $report.CommentResults.ThreadLikeCountLabels = @($reactions | Where-Object { $_.Current.AutomationId -eq 'CommentLike' } |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^(取消)?点赞，[0-9]' })
        $expanded = @($threadElements | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $_.Current.AutomationId -match '^CommentPicture[0-9]+$' -and $_.Current.Name -match '^全屏查看图片 [0-9]+，共 [0-9]+ 张$' })
        Save-AppScreenshot $thread 'public-inline-comment-thread'
        if ($expanded.Count -eq 0) {
            $report.CommentResults.Limitation = 'The loaded public thread contains no realized picture action; inline metadata was inspected, but fullscreen picture navigation was not verified.'
        } else {
            $picture = $expanded[0]
            $report.CommentResults.ExpandedPictureFocused = Focus-CommentInspection $picture $thread
            if (-not $report.CommentResults.ExpandedPictureFocused) { throw 'The expanded public picture did not receive visible app focus.' }
            Invoke-CommentReadOnly $picture $thread
            $popup = Wait-AppElement -AutomationId 'CommentImageViewer' -AnyName -Seconds 8
            if ($null -eq $popup) { throw 'The borderless picture popup was not exposed.' }
            $report.CommentResults.ImagePopupOpened = $true
            $bounds = $popup.Current.BoundingRectangle; $report.CommentResults.ImagePopupBounds = Read-Rect $bounds
            $report.CommentResults.ImagePopupCoversViewport = $bounds.Left -le $viewport.Left + 2 -and $bounds.Top -le $viewport.Top + 2 -and
                $bounds.Right -ge $viewport.Right - 2 -and $bounds.Bottom -ge $viewport.Bottom - 2
            $legacyTools = @(Find-AppElements -AnyName -IncludeOffscreen | Where-Object {
                $_.Current.AutomationId -in @('CommentImageFit', 'CommentImageZoomIn', 'CommentImageZoomOut') })
            $report.CommentResults.LegacyImageToolbarAbsent = $legacyTools.Count -eq 0
            Start-Sleep -Milliseconds 800
            $loadStatus = @(Find-AppElements -AutomationId 'CommentImageStatus' -AnyName)
            $report.CommentResults.ImageLoadState = if ($loadStatus.Count -eq 0) { 'No visible loading/error status' } else { $loadStatus[0].Current.Name }
            Save-AppScreenshot $popup 'public-comment-image-fullscreen'
            Invoke-CommentReadOnly (Wait-AppElement -AutomationId 'CommentImageClose' -AnyName -Seconds 3)
            Start-Sleep -Milliseconds 400
            $report.CommentResults.InlineThreadRestored = $null -ne (Wait-AppElement -AutomationId 'ReplyList' -AnyName -Seconds 3) -and
                @(Find-AppElements -AutomationId 'CommentImageViewer' -AnyName).Count -eq 0
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            $report.CommentResults.PictureFocusRestored = $null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId -and
                (Test-ElementDescendant $focused $picture)
            if (-not ($report.CommentResults.ImagePopupCoversViewport -and $report.CommentResults.InlineThreadRestored -and
                $report.CommentResults.PictureFocusRestored -and $report.CommentResults.LegacyImageToolbarAbsent)) {
                throw 'The borderless picture bounds or return-to-thread focus inspection failed.'
            }
        }
        Invoke-CommentReadOnly (Wait-AppElement -AutomationId 'RepliesBackButton' -AnyName -Seconds 3)
        Start-Sleep -Milliseconds 300
        $report.CommentResults.InlineThreadClosed = @(Find-AppElements -AutomationId 'ReplyList' -AnyName).Count -eq 0 -and
            $null -ne (Wait-AppElement -AutomationId 'CommentList' -AnyName -Seconds 3)
    }
    Invoke-CommentReadOnly (Wait-AppElement -AutomationId 'CommentsCloseButton' -AnyName -Seconds 3)
    $report.CommentResults.ReturnedToVideo = $null -ne (Wait-AppElement -AutomationId 'CommentsButton' -AnyName -Seconds 5)
    $report.Completed = $report.CommentResults.ReturnedToVideo -and $report.CommentResults.RealizedRows -gt 0 -and
        $report.CommentResults.InlineThreadOpened -and $report.CommentResults.InlineThreadClosed -and
        $report.CommentResults.ImagePopupOpened -and $report.CommentResults.PictureFocusRestored -and
        $report.CommentResults.LegacyReaderAbsent -and $report.CommentResults.ThreadLikeCountLabels.Count -gt 0
    $report.CommentResults.Partial = -not $report.Completed
    if (-not $report.Completed -and -not $report.CommentResults.Limitation) {
        $report.CommentResults.Limitation = 'Public data was insufficient to complete the inline comment, reaction-label and picture return inspection.'
    }
}
function Get-LoginDialog {
    $dialog = Wait-AppElement -AutomationId 'AccountLoginDialog' -AnyName -Seconds 8
    if ($null -eq $dialog -or $dialog.Current.ProcessId -ne $AppProcessId) {
        throw 'The exact-process account sign-in dialog was not exposed.'
    }
    return $dialog
}
function Get-LoginControl([string] $Id, $Dialog, [int] $Seconds = 5) {
    $element = Wait-AppElement -AutomationId $Id -AnyName -Seconds $Seconds
    if ($null -eq $element -or -not (Test-ElementDescendant $element $Dialog)) {
        throw ('No visible exact-dialog sign-in control: ' + $Id)
    }
    return $element
}
function Invoke-LoginReadOnly($Element, $Dialog = $null) {
    Assert-AppProcess
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId -or
        -not $Element.Current.IsEnabled -or $Element.Current.IsOffscreen -or
        $Element.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) {
        throw 'A sign-in probe action requires an enabled, exact-process button.'
    }
    $id = $Element.Current.AutomationId
    $entry = $id -eq 'AccountButton' -and $Element.Current.Name -in @('登录', '扫码登录')
    $mode = $id -eq 'AccountLoginSwitchMode' -and $null -ne $Dialog -and (Test-ElementDescendant $Element $Dialog)
    $close = $Element.Current.Name -eq '取消' -and $null -ne $Dialog -and (Test-ElementDescendant $Element $Dialog)
    # Do not add auth submission, QR refresh, verification, country selection,
    # or SMS actions here. No editor is ever written by LoginOnly.
    if (-not ($entry -or $mode -or $close)) { throw 'The requested sign-in action is outside the read-only probe allowlist.' }
    $Element.SetFocus()
    # Moving away from a native PasswordBox may dismiss its keyboard. Let the
    # app's 300 ms duplicate-B guard finish before invoking an explicit cancel.
    Start-Sleep -Milliseconds $(if ($close) { 360 } else { 180 })
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $report.Actions += if ($close) { 'AccountLoginCancel' } else { $id }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}
function Get-LoginCancel($Dialog) {
    $buttons = @(Find-AppElements -Name '取消' | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $_.Current.IsEnabled -and (Test-ElementDescendant $_ $Dialog)
    })
    if ($buttons.Count -ne 1) { throw 'The account dialog did not expose one exact native cancel button.' }
    return $buttons[0]
}
function Read-AppViewport($Anchor) {
    $candidate = $Anchor
    $viewport = $null
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    for ($depth = 0; $null -ne $candidate -and $depth -lt 24; $depth++) {
        if ($candidate.Current.ProcessId -ne $AppProcessId) { break }
        $bounds = $candidate.Current.BoundingRectangle
        if (-not $candidate.Current.IsOffscreen -and -not $bounds.IsEmpty -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) { $viewport = $bounds }
        $candidate = $walker.GetParent($candidate)
    }
    if ($null -eq $viewport) { throw 'No visible app-owned viewport bounds were available.' }
    return $viewport
}
function Measure-LoginLayout($Dialog, $Viewport, [string] $Mode) {
    $ids = if ($Mode -eq 'Password') {
        @('AccountLoginQrTitle', 'AccountLoginQrImage', 'AccountLoginModeTitle', 'AccountLoginUsername',
            'AccountLoginPassword', 'AccountLoginPasswordSubmit', 'AccountLoginSwitchMode')
    } else {
        @('AccountLoginQrTitle', 'AccountLoginQrImage', 'AccountLoginModeTitle', 'AccountLoginPhone',
            'AccountLoginSmsCode', 'AccountLoginSendSms', 'AccountLoginSmsSubmit', 'AccountLoginSwitchMode')
    }
    $elements = @{}
    $boundsById = @{}
    foreach ($id in $ids) {
        $element = Get-LoginControl $id $Dialog
        $bounds = $element.Current.BoundingRectangle
        if ($bounds.IsEmpty -or $bounds.Width -le 0 -or $bounds.Height -le 0) { throw ('Sign-in control has no usable bounds: ' + $id) }
        $inside = $bounds.Left -ge $Viewport.Left - 2 -and $bounds.Top -ge $Viewport.Top - 2 -and
            $bounds.Right -le $Viewport.Right + 2 -and $bounds.Bottom -le $Viewport.Bottom + 2
        $report.LoginResults.Bounds += [pscustomobject]@{ Mode = $Mode; Id = $id; Bounds = Read-Rect $bounds; InsideViewport = $inside }
        if (-not $inside) { throw ('The sign-in control is clipped by the app viewport: ' + $id) }
        $elements[$id] = $element
        $boundsById[$id] = $bounds
    }
    for ($leftIndex = 0; $leftIndex -lt $ids.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $ids.Count; $rightIndex++) {
            $a = $boundsById[$ids[$leftIndex]]; $b = $boundsById[$ids[$rightIndex]]
            $overlapWidth = [Math]::Min($a.Right, $b.Right) - [Math]::Max($a.Left, $b.Left)
            $overlapHeight = [Math]::Min($a.Bottom, $b.Bottom) - [Math]::Max($a.Top, $b.Top)
            if ($overlapWidth -gt 2 -and $overlapHeight -gt 2) {
                throw ('Sign-in controls overlap: ' + $ids[$leftIndex] + ' / ' + $ids[$rightIndex])
            }
        }
    }
    $firstEditorId = if ($Mode -eq 'Password') { 'AccountLoginUsername' } else { 'AccountLoginPhone' }
    $qr = $boundsById['AccountLoginQrImage']; $editor = $boundsById[$firstEditorId]
    if ($qr.Right -gt $editor.Left - 4) { throw 'The sign-in QR column is not to the left of the credential column.' }
    $report.LoginResults[$Mode + 'InsideViewport'] = $true
    $report.LoginResults[$Mode + 'NoOverlap'] = $true
    $report.LoginResults[$Mode + 'TwoColumns'] = $true
    return $elements
}
function Observe-LoginFocus($Element, $Dialog, [string] $Stage) {
    Assert-AppProcess
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId -or
        -not (Test-ElementDescendant $Element $Dialog) -or $Element.Current.IsOffscreen) { throw 'Focus observation must stay within this app sign-in dialog.' }
    # UIA SetFocus only; no injected keyboard/gamepad input, Tap or editor value.
    $Element.SetFocus()
    Start-Sleep -Milliseconds 220
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $verified = $null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId -and (Test-ElementDescendant $focused $Element)
    $report.LoginResults.FocusObservations += [pscustomobject]@{ Stage = $Stage; Id = $Element.Current.AutomationId; NativeFocus = $verified }
    if (-not $verified) { throw ('Native sign-in focus was not observed: ' + $Stage) }
}
function Test-LoginReadOnly {
    $report.LoginResults = [ordered]@{
        AnonymousEntry = $false; DefaultRecommendation = $false; Opened = $false
        PasswordProtected = $false; AccountEditorNative = $false; PasswordEditorNative = $false
        PasswordInsideViewport = $false; PasswordNoOverlap = $false; PasswordTwoColumns = $false
        SmsOpened = $false; SmsPhoneNative = $false; SmsCodeNative = $false; SmsSendPresent = $false
        SmsInsideViewport = $false; SmsNoOverlap = $false; SmsTwoColumns = $false
        ReturnedToPassword = $false; HiddenSmsControlsAbsent = $false
        ClosedAfterPasswordFocus = $false; FocusReleasedOnClose = $false; Reopened = $false; ClosedAgain = $false
        ReturnedToRecommendations = $false; Bounds = @(); FocusObservations = @()
        AccountTextWrites = 0; PasswordValueReads = 0; AuthenticationSubmissions = 0
        SmsSendInvocations = 0; QrRefreshInvocations = 0; VerificationInvocations = 0
        GlobalInputEvents = 0; PhysicalDirectionalInputVerified = $false; NativeKeyboardCancelKeyVerified = $false
    }
    $loginHomeButton = Wait-AppElement -AutomationId 'HomeButton' -AnyName -Seconds 8
    if ($null -eq $loginHomeButton -or $loginHomeButton.Current.HelpText -ne '当前分区') { throw 'The sign-in probe did not start on the recommendation tab.' }
    $report.LoginResults.DefaultRecommendation = $true
    $viewport = Read-AppViewport $loginHomeButton
    $report.LoginResults.Viewport = Read-Rect $viewport
    $entry = Wait-AppElement -AutomationId 'AccountButton' -AnyName -Seconds 5
    if ($null -eq $entry -or $entry.Current.Name -notin @('登录', '扫码登录')) { throw 'An anonymous account entry is required; no signed-in account was opened.' }
    $report.LoginResults.AnonymousEntry = $true
    Invoke-LoginReadOnly $entry
    $dialog = Get-LoginDialog
    $report.LoginResults.Opened = $true
    Start-Sleep -Milliseconds 320
    $passwordControls = Measure-LoginLayout $dialog $viewport 'Password'
    $account = $passwordControls['AccountLoginUsername']; $password = $passwordControls['AccountLoginPassword']
    $report.LoginResults.AccountEditorNative = $account.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit
    $report.LoginResults.PasswordEditorNative = $password.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit
    # IsPassword is metadata; do not request ValuePattern or read any content.
    $report.LoginResults.PasswordProtected = $password.Current.IsPassword
    if (-not ($report.LoginResults.AccountEditorNative -and $report.LoginResults.PasswordEditorNative -and $report.LoginResults.PasswordProtected)) {
        throw 'The account/password form did not expose native editors with protected password metadata.'
    }
    Observe-LoginFocus $account $dialog 'account'
    Observe-LoginFocus $password $dialog 'password'
    Observe-LoginFocus $passwordControls['AccountLoginSwitchMode'] $dialog 'password-to-mode-switch'
    Save-AppScreenshot $dialog 'login-password-native'
    Invoke-LoginReadOnly $passwordControls['AccountLoginSwitchMode'] $dialog
    Start-Sleep -Milliseconds 320
    $smsControls = Measure-LoginLayout $dialog $viewport 'Sms'
    $report.LoginResults.SmsOpened = $true
    $report.LoginResults.SmsPhoneNative = $smsControls['AccountLoginPhone'].Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit
    $report.LoginResults.SmsCodeNative = $smsControls['AccountLoginSmsCode'].Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit
    $report.LoginResults.SmsSendPresent = $smsControls['AccountLoginSendSms'].Current.ControlType -eq [System.Windows.Automation.ControlType]::Button
    if (-not ($report.LoginResults.SmsPhoneNative -and $report.LoginResults.SmsCodeNative -and $report.LoginResults.SmsSendPresent)) { throw 'The SMS sign-in form is incomplete.' }
    Observe-LoginFocus $smsControls['AccountLoginPhone'] $dialog 'sms-phone'
    Observe-LoginFocus $smsControls['AccountLoginSmsCode'] $dialog 'sms-code'
    Observe-LoginFocus $smsControls['AccountLoginSwitchMode'] $dialog 'sms-to-mode-switch'
    Save-AppScreenshot $dialog 'login-sms-native'
    Invoke-LoginReadOnly $smsControls['AccountLoginSwitchMode'] $dialog
    Start-Sleep -Milliseconds 320
    $password = Get-LoginControl 'AccountLoginPassword' $dialog
    $report.LoginResults.ReturnedToPassword = $password.Current.IsPassword
    $report.LoginResults.HiddenSmsControlsAbsent = @(Find-AppElements -AutomationId 'AccountLoginSmsCode' -AnyName).Count -eq 0
    if (-not ($report.LoginResults.ReturnedToPassword -and $report.LoginResults.HiddenSmsControlsAbsent)) { throw 'Returning to password mode did not hide the SMS form.' }
    Observe-LoginFocus $password $dialog 'password-before-close'
    Invoke-LoginReadOnly (Get-LoginCancel $dialog) $dialog
    Start-Sleep -Milliseconds 350
    $report.LoginResults.ClosedAfterPasswordFocus = @(Find-AppElements -AutomationId 'AccountLoginDialog' -AnyName).Count -eq 0
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $report.LoginResults.FocusReleasedOnClose = $null -eq $focused -or $focused.Current.ProcessId -ne $AppProcessId -or
        $focused.Current.AutomationId -notin @('AccountLoginUsername', 'AccountLoginPassword', 'AccountLoginPhone', 'AccountLoginSmsCode')
    if (-not ($report.LoginResults.ClosedAfterPasswordFocus -and $report.LoginResults.FocusReleasedOnClose)) { throw 'Closing the sign-in dialog did not release its native editor focus.' }
    Invoke-LoginReadOnly (Wait-AppElement -AutomationId 'AccountButton' -AnyName -Seconds 5)
    $dialog = Get-LoginDialog
    $report.LoginResults.Reopened = $null -ne (Get-LoginControl 'AccountLoginPassword' $dialog)
    Invoke-LoginReadOnly (Get-LoginCancel $dialog) $dialog
    Start-Sleep -Milliseconds 350
    $report.LoginResults.ClosedAgain = @(Find-AppElements -AutomationId 'AccountLoginDialog' -AnyName).Count -eq 0
    $loginHomeButton = Wait-AppElement -AutomationId 'HomeButton' -AnyName -Seconds 5
    $report.LoginResults.ReturnedToRecommendations = $null -ne $loginHomeButton -and $loginHomeButton.Current.HelpText -eq '当前分区' -and
        $null -ne (Wait-AppElement -AutomationId 'VideoGrid' -AnyName -Seconds 5)
    $report.Completed = $report.LoginResults.ClosedAgain -and $report.LoginResults.ReturnedToRecommendations
    if (-not $report.Completed) { throw 'The read-only sign-in probe did not return to recommendation.' }
}
function Test-LiveReadOnly {
    $ids = @('LiveSectionButton', 'HomeButton', 'PopularSectionButton', 'AnimationSectionButton', 'FilmSectionButton')
    $names = @('直播', '推荐', '热门', '动画', '影视')
    $lastRight = 0
    for ($index = 0; $index -lt $ids.Count; $index++) {
        $button = Wait-AppElement -AutomationId $ids[$index] -AnyName -Seconds 5
        if ($null -eq $button -or $button.Current.Name -ne $names[$index]) { throw 'The five required section tabs were not exposed in order.' }
        $bounds = $button.Current.BoundingRectangle
        if ($index -gt 0 -and $bounds.Left -lt $lastRight - 1) { throw 'Top section tab bounds overlap or have incorrect order.' }
        $lastRight = $bounds.Right
        if ($ids[$index] -eq 'HomeButton') { $report.LiveResults.DefaultRecommendation = $button.Current.HelpText -eq '当前分区' }
    }
    if (-not $report.LiveResults.DefaultRecommendation) { throw 'Startup did not select recommendation.' }
    $report.LiveResults.TabOrder = $names
    foreach ($id in @('PopularSectionButton', 'AnimationSectionButton', 'FilmSectionButton')) {
        Invoke-LiveReadOnly (Wait-AppElement -AutomationId $id -AnyName -Seconds 3)
        $selected = Wait-AppElement -AutomationId $id -AnyName -Seconds 3
        if ($selected.Current.HelpText -ne '当前分区') { throw 'The selected video section was not marked current.' }
        $report.LiveResults[$id] = $true
    }
    Invoke-LiveReadOnly (Wait-AppElement -AutomationId 'LiveSectionButton' -AnyName -Seconds 3)
    $grid = Wait-AppElement -AutomationId 'LiveRoomGrid' -AnyName -Seconds 8
    if ($null -eq $grid) { throw 'The live browse grid was not visible.' }
    $listCondition = [System.Windows.Automation.AndCondition]::new(@(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, $listCondition)
        if ($rows.Count -gt 0) { break }
        Start-Sleep -Milliseconds 200
    } while ($clock.Elapsed.TotalSeconds -lt 18)
    $report.LiveResults.RoomCards = $rows.Count
    if ($rows.Count -eq 0) { Read-AppNotices; throw 'Live API returned no realized room cards.' }
    $first = $rows.Item(0)
    if ($first.Current.ProcessId -ne $AppProcessId -or $first.Current.IsOffscreen -or -not (Test-ElementDescendant $first $grid)) { throw 'First room is not an available exact-process live card.' }
    $first.SetFocus()
    Start-Sleep -Milliseconds 220
    $report.LiveResults.RoomFocus = Test-ElementDescendant ([System.Windows.Automation.AutomationElement]::FocusedElement) $first
    # A known public room has a verified reachable HLS CDN. Only write this
    # literal public room ID into this app-owned inline editor; no chat text,
    # account data, login, purchase or global keyboard input is used.
    $editClock = [Diagnostics.Stopwatch]::StartNew()
    $roomEditor = $null; $valuePattern = $null
    for ($editAttempt = 0; $editAttempt -lt 3 -and $null -eq $roomEditor; $editAttempt++) {
        Invoke-LiveReadOnly (Wait-AppElement -AutomationId 'LiveOpenRoomButton' -AnyName -Seconds 3)
        $editClock.Restart()
        do {
            $editors = @(Find-AppElements -AutomationId 'LiveRoomInput' -AnyName)
            if ($editors.Count -eq 1 -and $editors[0].TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
                if (-not ([System.Windows.Automation.ValuePattern]$valuePattern).Current.IsReadOnly) { $roomEditor = $editors[0]; break }
            }
            Start-Sleep -Milliseconds 180
        } while ($editClock.Elapsed.TotalSeconds -lt 4)
    }
    $report.LiveResults.RoomEditorAttempts = $editAttempt
    if ($null -eq $roomEditor -or $roomEditor.Current.ProcessId -ne $AppProcessId) {
        $focus = [System.Windows.Automation.AutomationElement]::FocusedElement
        $report.LiveResults.EditorDiagnostics = @{ Count = $editors.Count; PatternPresent = $null -ne $valuePattern
            FocusId = if ($null -ne $focus -and $focus.Current.ProcessId -eq $AppProcessId) { $focus.Current.AutomationId } else { 'not-app-owned' } }
        throw 'The app-owned native room editor did not become writable.'
    }
    ([System.Windows.Automation.ValuePattern]$valuePattern).SetValue('6')
    $report.LiveResults.PublicRoomWritten = '6'
    $open = Wait-AppElement -AutomationId 'LiveOpenRoomButton' -AnyName -Seconds 3
    $open.SetFocus()
    $editClock.Restart()
    do {
        $released = ([System.Windows.Automation.ValuePattern]$valuePattern).Current.IsReadOnly
        if ($released) { break }
        Start-Sleep -Milliseconds 180
    } while ($editClock.Elapsed.TotalSeconds -lt 5)
    if (-not $released) { throw 'The native room editor did not release before navigation.' }
    $report.LiveResults.NativeRoomEditorReleased = $true
    Invoke-LiveReadOnly $open
    $chat = Wait-AppElement -AutomationId 'LiveChatButton' -AnyName -Seconds 15
    if ($null -eq $chat) { throw 'Opening a public live room did not expose playback controls.' }
    Invoke-LiveReadOnly $chat
    $messages = Wait-AppElement -AutomationId 'LiveChatMessages' -AnyName -Seconds 5
    if ($null -eq $messages) { throw 'The live chat drawer did not open.' }
    $report.LiveResults.ChatDrawer = $true
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $chat = @(Find-AppElements -AutomationId 'LiveChatButton' -AnyName -IncludeOffscreen)[0]
        $status = $chat.Current.HelpText
        $players = @(Find-AppElements -AutomationId 'LivePlayButton' -AnyName -IncludeOffscreen)
        $state = if ($players.Count -gt 0) { $players[0].Current.HelpText } else { '' }
        if ($status -eq '直播弹幕已连接' -and $state -eq 'Playing') { break }
        Start-Sleep -Milliseconds 300
    } while ($clock.Elapsed.TotalSeconds -lt 65)
    $report.LiveResults.SocketStatus = $status
    $report.LiveResults.PlayerState = $state
    $report.LiveResults.SocketConnected = $status -eq '直播弹幕已连接'
    $report.LiveResults.PlayingObserved = $state -eq 'Playing'
    $watchStatus = @(Find-AppElements -AutomationId 'LiveWatchStatus' -AnyName -IncludeOffscreen)
    if ($watchStatus.Count -gt 0) { $report.LiveResults.WatchStatus = [regex]::Replace($watchStatus[0].Current.Name, 'https?://\S+', '[媒体地址]') }
    $messageRows = $messages.FindAll([System.Windows.Automation.TreeScope]::Children, $listCondition)
    $report.LiveResults.ChatRows = $messageRows.Count
    Save-AppScreenshot $messages 'live-chat-native'
    Invoke-LiveReadOnly (Wait-AppElement -AutomationId 'LiveDrawerCloseButton' -AnyName -Seconds 3)
    Invoke-LiveReadOnly (Wait-AppElement -AutomationId 'LiveGiftsButton' -AnyName -Seconds 3)
    $login = Wait-AppElement -AutomationId 'LiveGiftLoginButton' -AnyName -Seconds 4
    if ($null -eq $login) { throw 'Anonymous gift drawer did not expose its login gate; no gift or login action was invoked.' }
    $report.LiveResults.AnonymousGiftGate = $true
    Invoke-LiveReadOnly (Wait-AppElement -AutomationId 'LiveDrawerCloseButton' -AnyName -Seconds 3)
    Invoke-LiveReadOnly (Wait-AppElement -AutomationId 'LiveLeaveButton' -AnyName -Seconds 3)
    $restored = Wait-AppElement -AutomationId 'LiveRoomGrid' -AnyName -Seconds 5
    $report.LiveResults.ReturnedToRooms = $null -ne $restored
    Invoke-LiveReadOnly (Wait-AppElement -AutomationId 'HomeButton' -AnyName -Seconds 4)
    $report.LiveResults.ReturnedToRecommendations = $null -ne (Wait-AppElement -AutomationId 'VideoGrid' -AnyName -Seconds 5)
    $report.Completed = $report.LiveResults.ReturnedToRooms -and $report.LiveResults.ReturnedToRecommendations
    if (-not $report.Completed) { throw 'Live return navigation did not restore the expected page.' }
}
function Invoke-InteractionReadOnly($Element) {
    Assert-AppProcess
    # This separate allowlist never accepts login, refresh, engagement actions,
    # a favorite-folder row, or a mutation confirmation, even if called by mistake.
    $ids = @('InteractionButton', 'InteractionCloseButton', 'InteractionCreatorButton', 'PlayerSurface')
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId -or
        $Element.Current.AutomationId -notin $ids -or -not $Element.Current.IsEnabled -or
        $Element.Current.IsOffscreen -or $Element.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) {
        throw 'Interaction smoke actions require an exact-process read-only allowlisted button.'
    }
    $id = $Element.Current.AutomationId
    if ($id -eq 'InteractionButton' -and $Element.Current.Name -ne '视频互动：点赞、投币、收藏和三连') {
        throw 'The video interaction entry did not match its expected public semantic name.'
    }
    if ($id -eq 'PlayerSurface' -and $Element.Current.Name -ne '暂停或播放视频') {
        throw 'The player surface did not match its local transport action.'
    }
    if ($id -in @('InteractionCloseButton', 'InteractionCreatorButton')) { Assert-AnonymousInteractions }
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw ('No InvokePattern for read-only interaction action: ' + $id)
    }
    $report.Actions += $id
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}
function Assert-AnonymousInteractions {
    Assert-AppProcess
    $close = @(Find-AppElements -AutomationId 'InteractionCloseButton' -AnyName | Where-Object { $_.Current.IsEnabled })
    if ($close.Count -ne 1) { throw 'A visible active interaction pane was not found.' }
    # Focusing the last row can scroll the login row out of view. Presence is
    # still checked on the exact active pane, without focusing or invoking login.
    $login = @(Find-AppElements -AutomationId 'InteractionLoginButton' -AnyName -IncludeOffscreen | Where-Object {
        $_.Current.IsEnabled -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button
    })
    if ($login.Count -ne 1) {
        throw 'Anonymous interaction login action is absent or ambiguous. This read-only probe stops without account, login or mutation actions.'
    }
    $observations = @()
    foreach ($id in @('InteractionLikeButton', 'InteractionCoinButton', 'InteractionFavoriteButton', 'InteractionTripleButton')) {
        $rows = @(Find-AppElements -AutomationId $id -AnyName -IncludeOffscreen)
        if ($rows.Count -ne 1 -or $rows[0].Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) {
            throw ('An anonymous engagement row is absent or ambiguous: ' + $id)
        }
        $observations += [pscustomobject]@{ AutomationId = $id; Enabled = $rows[0].Current.IsEnabled; ExactProcessId = $AppProcessId }
        if ($rows[0].Current.IsEnabled) { throw ('A mutation row was enabled in the anonymous session: ' + $id) }
    }
    $report.InteractionAnonymousGuardPassed = $true
    $report.InteractionMutationButtons = $observations
    $report.InteractionMutationButtonsDisabled = $true
}
function Open-ReadOnlyInteractions {
    $entry = Wait-AppElement -AutomationId 'InteractionButton' -AnyName -Seconds 3
    if ($null -eq $entry) {
        # Auto-hide may have collapsed chrome during metadata loading. Only
        # this app-owned local playback surface may reveal the controls.
        $surface = Wait-AppElement -AutomationId 'PlayerSurface' -Name '暂停或播放视频' -Seconds 2
        Invoke-InteractionReadOnly $surface
        $entry = Wait-AppElement -AutomationId 'InteractionButton' -AnyName -Seconds 5
    }
    if (-not $report.InteractionOpened) { Measure-InteractionPlayerButtons }
    Invoke-InteractionReadOnly $entry
    $login = Wait-AppElement -AutomationId 'InteractionLoginButton' -AnyName -Seconds 5
    if ($null -eq $login) { throw 'The interaction pane did not expose an anonymous login action; no signed-in engagement test was attempted.' }
    Assert-AnonymousInteractions
}
function Measure-InteractionPlayerButtons {
    Assert-AppProcess
    $surface = Wait-AppElement -AutomationId 'PlayerSurface' -Name '暂停或播放视频' -Seconds 2
    if ($null -eq $surface) { throw 'The app-owned player viewport was not available for button bounds inspection.' }
    $viewport = $surface.Current.BoundingRectangle
    if ($viewport.IsEmpty -or $viewport.Width -le 0 -or $viewport.Height -le 0) { throw 'The player viewport bounds were empty.' }
    $report.InteractionPlayerViewport = Read-Rect $viewport
    $ids = @('PlayPauseButton', 'BackTenButton', 'ForwardTenButton', 'DanmakuToggle', 'DanmakuEntryButton',
        'CommentsButton', 'InteractionButton', 'DanmakuSettingsButton', 'QualityButton', 'RetryPlaybackButton', 'HidePlayerControlsButton')
    $rows = @()
    foreach ($id in $ids) {
        $buttons = @(Find-AppElements -AutomationId $id -AnyName)
        if ($buttons.Count -ne 1) { throw ('A visible playback button was absent or ambiguous during bounds inspection: ' + $id) }
        $bounds = $buttons[0].Current.BoundingRectangle
        $inside = -not $bounds.IsEmpty -and $bounds.Width -gt 0 -and $bounds.Height -gt 0 -and
            $bounds.Left -ge $viewport.Left - 1 -and $bounds.Top -ge $viewport.Top - 1 -and
            $bounds.Right -le $viewport.Right + 1 -and $bounds.Bottom -le $viewport.Bottom + 1
        $rows += [pscustomobject]@{ AutomationId = $id; Rectangle = $bounds; Inside = $inside }
        $report.InteractionPlayerButtonBounds += [pscustomobject]@{
            AutomationId = $id; ExactProcessId = $AppProcessId; Bounds = Read-Rect $bounds; InsidePlayerViewport = $inside
        }
    }
    $overlap = $false
    for ($i = 0; $i -lt $rows.Count - 1; $i++) {
        for ($j = $i + 1; $j -lt $rows.Count; $j++) {
            $a = $rows[$i].Rectangle; $b = $rows[$j].Rectangle
            if ([Math]::Min($a.Right, $b.Right) - [Math]::Max($a.Left, $b.Left) -gt 1 -and
                [Math]::Min($a.Bottom, $b.Bottom) - [Math]::Max($a.Top, $b.Top) -gt 1) { $overlap = $true }
        }
    }
    $report.InteractionPlayerBoundsComplete = $rows.Count -eq $ids.Count
    $report.InteractionPlayerButtonsInsideViewport = @($rows | Where-Object { -not $_.Inside }).Count -eq 0
    $report.InteractionPlayerButtonsDoNotOverlap = -not $overlap
    if (-not $report.InteractionPlayerButtonsInsideViewport -or $overlap) {
        Save-AppScreenshot $surface 'interaction-player-button-bounds-failed'
        throw 'Playback button bounds overlap or extend outside the exact app-owned player viewport.'
    }
}
function Focus-ReadOnlyInteractionRow([string] $AutomationId) {
    if ($AutomationId -notin @('InteractionCloseButton', 'InteractionLoginButton', 'InteractionCreatorButton')) {
        throw 'Interaction focus target is outside the nonmutating allowlist.'
    }
    Assert-AnonymousInteractions
    $hasFocus = $false
    $row = $null
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $drawerMatches = @(Find-AppElements -AutomationId $AutomationId -AnyName -IncludeOffscreen | Where-Object {
            $_.Current.IsEnabled -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button
        })
        if ($drawerMatches.Count -ne 1) { Start-Sleep -Milliseconds 150; continue }
        $row = $drawerMatches[0]
        $row.SetFocus()
        Start-Sleep -Milliseconds 220
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $hasFocus = $null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId -and
            (Test-ElementDescendant $focused $row) -and -not $row.Current.IsOffscreen
        if ($hasFocus) { break }
    }
    $report.InteractionFocusObservations += [pscustomobject]@{
        AutomationId = $AutomationId; ExactProcessId = $AppProcessId; KeyboardFocusObserved = $hasFocus
        Method = 'Exact-process UIA SetFocus; physical D-pad input was not simulated.'
    }
    if (-not $hasFocus) { throw ('Visible native interaction focus was not confirmed: ' + $AutomationId) }
}
function Test-ReadOnlyInteractions {
    Submit-PublicSearch -Query 'BV1spz8B4EXv'
    $owner = Wait-AppElement -Name '打开 UP 主主页，查看视频和动态' -Seconds 25
    if ($null -eq $owner) { throw 'The public video did not expose its owner; metadata or network may be unavailable.' }
    Open-ReadOnlyInteractions
    $report.InteractionOpened = $true
    foreach ($id in @('InteractionCloseButton', 'InteractionLoginButton', 'InteractionCreatorButton')) {
        Focus-ReadOnlyInteractionRow $id
    }
    $report.InteractionNativeFocusVerified = $true
    $screenshotAnchor = Wait-AppElement -AutomationId 'InteractionCloseButton' -AnyName -Seconds 2
    Save-AppScreenshot $screenshotAnchor 'interaction-panel-native-focus'
    $close = Wait-AppElement -AutomationId 'InteractionCloseButton' -AnyName -Seconds 2
    Invoke-InteractionReadOnly $close
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $visibleClose = @(Find-AppElements -AutomationId 'InteractionCloseButton' -AnyName)
        if ($visibleClose.Count -eq 0) { break }
        Start-Sleep -Milliseconds 150
    } while ($clock.Elapsed.TotalSeconds -lt 3)
    $report.InteractionClosed = $visibleClose.Count -eq 0
    if (-not $report.InteractionClosed) { throw 'The interaction pane did not close.' }
    Open-ReadOnlyInteractions
    $report.InteractionReopened = $true
    Focus-ReadOnlyInteractionRow 'InteractionCreatorButton'
    $creator = Wait-AppElement -AutomationId 'InteractionCreatorButton' -AnyName -Seconds 2
    Invoke-InteractionReadOnly $creator
    $videos = Wait-AppElement -Name '视频' -Seconds 8
    $dynamics = Wait-AppElement -Name '动态' -Seconds 4
    $report.InteractionCreatorVideoTabPresent = $null -ne $videos
    $report.InteractionCreatorDynamicTabPresent = $null -ne $dynamics
    $report.InteractionCreatorOpened = $report.InteractionCreatorVideoTabPresent -and $report.InteractionCreatorDynamicTabPresent
    if (-not $report.InteractionCreatorOpened) { throw 'The read-only creator entry did not expose video and dynamic tabs.' }
    # Inspect the existing public tabs, then return. No dynamic, comment,
    # author-follow, account, login, or engagement write action is invoked.
    $back = Wait-AppElement -Name '返回' -Seconds 3
    Invoke-AppElement $back
    $restoredOwner = Wait-AppElement -Name '打开 UP 主主页，查看视频和动态' -Seconds 8
    $report.InteractionReturnedToVideo = $null -ne $restoredOwner
    $report.ReturnedToVideo = $report.InteractionReturnedToVideo
    if (-not $report.InteractionReturnedToVideo) { throw 'Creator return did not restore the public video owner.' }
    $report.Completed = $report.InteractionOpened -and $report.InteractionAnonymousGuardPassed -and
        $report.InteractionMutationButtonsDisabled -and $report.InteractionNativeFocusVerified -and
        $report.InteractionClosed -and $report.InteractionReopened -and $report.InteractionCreatorOpened -and
        $report.InteractionReturnedToVideo
}
function Find-DrawerRow([string] $AutomationId, [string] $Name) {
    if ($AutomationId) { $drawerMatches = @(Find-AppElements -AutomationId $AutomationId -AnyName -IncludeOffscreen) }
    else { $drawerMatches = @(Find-AppElements -Name $Name -IncludeOffscreen) }
    $drawerMatches = @($drawerMatches | Where-Object {
        $_.Current.IsEnabled -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button
    })
    if ($drawerMatches.Count -gt 0) { return $drawerMatches[0] }
    return $null
}
function Focus-DrawerRow([string] $Drawer, [string] $Label, [string] $AutomationId, [string] $Name, [int] $ScrollPercent = -1) {
    Assert-AppProcess
    $Element = Find-DrawerRow $AutomationId $Name
    if ($null -eq $Element -or $Element.Current.ProcessId -ne $AppProcessId -or -not $Element.Current.IsEnabled -or
        $Element.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button -or
        $Drawer -notin @('settings', 'danmaku')) { throw 'Drawer focus requires an enabled exact-process button.' }
    if ($Drawer -eq 'settings' -and $Element.Current.Name -notmatch '^\s*(✓\s*)?(HDR|4K|8K|1080P|720P|480P|360P|杜比|画质 [0-9])') {
        throw 'Only a public quality row may be focused in playback settings.'
    }
    if ($Drawer -eq 'danmaku' -and $Element.Current.AutomationId -notin @('DanmakuMaskButton', 'DanmakuSpeedButton', 'DanmakuResetButton')) {
        throw 'Danmaku focus target is outside the inspection allowlist.'
    }
    $wasOffscreen = $Element.Current.IsOffscreen
    if ($ScrollPercent -ge 0) {
        # Scroll only a verified ancestor owned by this PID. This exposes rows
        # below the viewport without global input or invoking any preference.
        $ancestor = $Element
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        for ($depth = 0; $null -ne $ancestor -and $depth -lt 16; $depth++) {
            if ($ancestor.Current.ProcessId -ne $AppProcessId) { break }
            $scrollPattern = $null
            if ($ancestor.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scrollPattern)) {
                $scroll = [System.Windows.Automation.ScrollPattern]$scrollPattern
                if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, $ScrollPercent) }
                break
            }
            $ancestor = $walker.GetParent($ancestor)
        }
    }
    # Settings refreshes authorized qualities asynchronously. Its old button
    # providers can retain bounds after replacement; reacquire the semantic row
    # after scrolling and on each bounded focus attempt instead of reusing them.
    Start-Sleep -Milliseconds 200
    $hasFocus = $false
    $focused = $null
    $attempts = 0
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        ++$attempts
        $Element = Find-DrawerRow $AutomationId $Name
        if ($null -eq $Element) { Start-Sleep -Milliseconds 200; continue }
        try {
            $Element.SetFocus()
            Start-Sleep -Milliseconds 250
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            $hasFocus = $null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId -and
                (Test-ElementDescendant $focused $Element)
            if ($hasFocus -and -not $Element.Current.IsOffscreen) { break }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
    }
    if ($null -eq $Element) { throw ('The refreshed drawer row was not exposed: ' + $Label) }
    $focusId = ''; $focusName = ''
    if ($null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId) {
        $focusId = $focused.Current.AutomationId; $focusName = $focused.Current.Name
    }
    $report.DrawerFocusObservations += [pscustomobject]@{
        Drawer = $Drawer; Label = $Label; AutomationId = $Element.Current.AutomationId
        Name = $Element.Current.Name; ExactProcessId = $AppProcessId; KeyboardFocusObserved = $hasFocus
        OffscreenBefore = $wasOffscreen; OffscreenAfter = $Element.Current.IsOffscreen
        Bounds = Read-Rect $Element.Current.BoundingRectangle; PixelVerified = $false
        FocusAttempts = $attempts; ActualFocusAutomationId = $focusId; ActualFocusName = $focusName
    }
    if (-not $hasFocus -or $Element.Current.IsOffscreen) {
        Save-AppScreenshot $Element ($Drawer + '-focus-' + $Label + '-unconfirmed')
        throw ('Drawer row did not receive visible keyboard focus: ' + $Label)
    }
    Save-AppScreenshot $Element ($Drawer + '-focus-' + $Label)
}
function Test-DrawerFocus {
    $issues = New-Object 'System.Collections.Generic.List[string]'
    try {
        $play = Wait-AppElement -AutomationId 'PlayPauseButton' -AnyName -Seconds 6
        if ($null -eq $play) { throw 'Public playback controls were not exposed.' }
        if ($play.Current.Name -eq '暂停') { Invoke-TransportButton $play }
    } catch { $issues.Add('Playback setup: ' + $_.Exception.Message) }
    $settingsOpen = $false
    try {
        Invoke-TransportButton (Wait-AppElement -AutomationId 'QualityButton' -AnyName -Seconds 5)
        $settingsOpen = $true
        $routes = @(Find-AppElements -AutomationId 'SwitchPlaybackRouteButton' -AnyName -IncludeOffscreen)
        $routes += @(Find-AppElements -AutomationId 'BufferingRoute' -AnyName -IncludeOffscreen)
        $routeText = @(Find-AppElements -AnyName | Where-Object { $_.Current.Name -match '播放线路|视频线路|音频线路|正在切换.*线路' })
        $report.RoutingControlsAbsentFromUia = $routes.Count -eq 0 -and $routeText.Count -eq 0
        if (-not $report.RoutingControlsAbsentFromUia) { throw 'Manual source-switch or route status remains exposed in the app UI.' }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        do {
            $rows = @(Find-AppElements -AnyName -IncludeOffscreen | Where-Object {
                $_.Current.IsEnabled -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
                $_.Current.Name -match '^\s*(✓\s*)?(HDR|4K|8K|1080P|720P|480P|360P|杜比|画质 [0-9])'
            })
            if ($rows.Count -gt 0) { break }
            Start-Sleep -Milliseconds 200
        } while ($clock.Elapsed.TotalSeconds -lt 8)
        if ($rows.Count -eq 0) { throw 'No enabled public quality row was exposed for focus inspection.' }
        $targets = @([pscustomobject]@{ Name = $rows[0].Current.Name; Label = 'first'; Scroll = 0 })
        if ($rows.Count -gt 1) { $targets += [pscustomobject]@{ Name = $rows[-1].Current.Name; Label = 'last'; Scroll = 100 } }
        $passed = 0
        foreach ($target in $targets) {
            try { Focus-DrawerRow 'settings' $target.Label '' $target.Name $target.Scroll; ++$passed }
            catch { $issues.Add('Settings ' + $target.Label + ': ' + $_.Exception.Message) }
        }
        $report.SettingsFocusCaptured = $passed -eq $targets.Count
    } catch { $issues.Add('Settings setup: ' + $_.Exception.Message) }
    finally {
        if ($settingsOpen) {
            try { Invoke-TransportButton (Wait-AppElement -AutomationId 'SettingsCloseButton' -AnyName -Seconds 3) }
            catch { $issues.Add('Settings close: ' + $_.Exception.Message) }
        }
    }
    # A stale quality provider must not prevent inspecting the independent
    # danmaku drawer. Never invoke a preference while testing focus.
    $danmakuOpen = $false
    try {
        Invoke-TransportButton (Wait-AppElement -AutomationId 'DanmakuSettingsButton' -AnyName -Seconds 3)
        $danmakuOpen = $true
        $passed = 0
        foreach ($target in @(
            [pscustomobject]@{ Id = 'DanmakuMaskButton'; Label = 'first'; Scroll = 0 },
            [pscustomobject]@{ Id = 'DanmakuSpeedButton'; Label = 'middle'; Scroll = -1 },
            [pscustomobject]@{ Id = 'DanmakuResetButton'; Label = 'last'; Scroll = 100 }
        )) {
            try { Focus-DrawerRow 'danmaku' $target.Label $target.Id '' $target.Scroll; ++$passed }
            catch { $issues.Add('Danmaku ' + $target.Label + ': ' + $_.Exception.Message) }
        }
        $report.DanmakuSettingsFocusCaptured = $passed -eq 3
    } catch { $issues.Add('Danmaku setup: ' + $_.Exception.Message) }
    finally {
        if ($danmakuOpen) {
            try { Invoke-TransportButton (Wait-AppElement -AutomationId 'DanmakuSettingsCloseButton' -AnyName -Seconds 3) }
            catch { $issues.Add('Danmaku close: ' + $_.Exception.Message) }
        }
    }
    if ($issues.Count -gt 0) { $report.DrawerFocusLimitation = $issues -join ' | ' }
}
function Read-AppNotices {
    # Record only public request/UI diagnostics, never editor values or identity.
    foreach ($element in @(Find-AppElements -AnyName)) {
        $name = $element.Current.Name
        if ($name -match '加载失败|读取失败|搜索失败|HTTP 412|暂未|已加载 [0-9]+|已找到|没有找到|正在搜索|平台未|该页暂无') {
            $report.Notices += $name.Substring(0, [Math]::Min(500, $name.Length))
        }
    }
    $report.Notices = @($report.Notices | Select-Object -Unique)
}
function Record-WatchSearchStage([string] $Stage) {
    Assert-AppProcess
    $controls = @()
    foreach ($id in @('WatchSearchButton', 'HeaderSearchButton', 'SearchInput', 'SearchEntryButton', 'SearchSubmitButton')) {
        $elements = @(Find-AppElements -AutomationId $id -AnyName -IncludeOffscreen)
        foreach ($element in $elements) {
            $controls += [pscustomobject]@{
                Id = $id; Enabled = $element.Current.IsEnabled; Offscreen = $element.Current.IsOffscreen
                HasKeyboardFocus = $element.Current.HasKeyboardFocus
                Bounds = Read-Rect $element.Current.BoundingRectangle
            }
        }
    }
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $focus = [ordered]@{ InApp = $false; Id = ''; Name = '' }
    if ($null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId) {
        $focus.InApp = $true; $focus.Id = $focused.Current.AutomationId; $focus.Name = $focused.Current.Name
    }
    $report.WatchSearchStages += [pscustomobject]@{ Stage = $Stage; Controls = $controls; Focus = $focus }
}
function Wait-InlineEditorFocused([int] $Seconds = 3) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $stable = 0
    do {
        $editors = @(Find-AppElements -AutomationId 'SearchInput' -AnyName)
        if ($editors.Count -gt 0) {
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            $hasFocus = $editors[0].Current.HasKeyboardFocus -or ($null -ne $focused -and
                $focused.Current.ProcessId -eq $AppProcessId -and (Test-ElementDescendant $focused $editors[0]))
            if ($hasFocus) { ++$stable } else { $stable = 0 }
            if ($stable -ge 2) { return $editors[0] }
        } else { $stable = 0 }
        Start-Sleep -Milliseconds 180
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    return $null
}
function Wait-WatchRestored([int] $Seconds = 5) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $stable = 0
    do {
        $editors = @(Find-AppElements -AutomationId 'SearchInput' -AnyName)
        $headers = @(Find-AppElements -AutomationId 'HeaderSearchButton' -AnyName)
        $watch = @(Find-AppElements -AutomationId 'WatchSearchButton' -AnyName | Where-Object { $_.Current.IsEnabled })
        if ($editors.Count -eq 0 -and $headers.Count -eq 0 -and $watch.Count -gt 0) { ++$stable } else { $stable = 0 }
        if ($stable -ge 2) { return $watch[0] }
        Start-Sleep -Milliseconds 180
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    return $null
}
function Test-WatchInlineSearch {
    try {
        Invoke-TransportButton (Wait-AppElement -AutomationId 'WatchSearchButton' -AnyName -Seconds 4)
        $script:activeInlineSearch = $true
        Record-WatchSearchStage 'first-open-invoked'
        # Visibility alone can precede ShowKeyboardAsync's low-priority focus
        # pass. Do not cancel before that pass has actually focused the editor.
        $editor = Wait-InlineEditorFocused
        Record-WatchSearchStage 'first-editor-focus-checked'
        if ($null -eq $editor) { throw 'Watch search editor did not obtain stable focus before cancellation.' }
        $liveEditor = @(Find-AppElements -AutomationId 'SearchInput' -AnyName)
        if ($liveEditor.Count -eq 0 -or -not $liveEditor[0].Current.HasKeyboardFocus) {
            throw 'The watch editor disappeared or lost focus before any explicit cancellation.'
        }
        $headerSearch = Wait-AppElement -AutomationId 'HeaderSearchButton' -AnyName -Seconds 3
        if ($null -eq $headerSearch) { throw 'Watch search cancellation target was unavailable.' }
        $headerSearch.SetFocus()
        Record-WatchSearchStage 'cancel-focus-requested'
        $watchSearch = Wait-WatchRestored
        Record-WatchSearchStage 'watch-restoration-checked'
        if ($null -eq $watchSearch) { throw 'Canceling page search did not restore playback.' }
        Invoke-TransportButton $watchSearch
        Record-WatchSearchStage 'second-open-invoked'
        $editor = Wait-InlineEditorFocused
        Record-WatchSearchStage 'second-editor-focus-checked'
        if ($null -eq $editor) { throw 'Search could not be reopened from playback after cancellation.' }
        $report.WatchSearchReopenedAfterCancel = $true
        $pattern = [System.Windows.Automation.ValuePattern]$editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $pattern.SetValue('BV1spz8B4EXv')
        $submit = Wait-AppElement -Name '搜索' -AutomationId 'SearchSubmitButton' -Seconds 3
        if ($null -eq $submit) { throw 'No exact page submit action in watch search.' }
        $submit.SetFocus()
        Start-Sleep -Milliseconds 250
        Record-WatchSearchStage 'submit-button-focused'
        if (@(Find-AppElements -AutomationId 'SearchInput' -AnyName).Count -eq 0) {
            throw 'Moving focus to the submit button prematurely hid the inline editor.'
        }
        Invoke-AppElement $submit
        $script:activeInlineSearch = $false
        $report.WatchSearchSubmitWithButtonFocus = $null -ne (Wait-AppElement -AutomationId 'WatchSearchButton' -AnyName -Seconds 20)
        Record-WatchSearchStage 'same-bv-submit-finished'
        if (-not $report.WatchSearchSubmitWithButtonFocus) { throw 'Submitting the same public BV did not restore playback controls.' }
    } catch {
        $report.WatchSearchLimitation = $_.Exception.Message
        try { Record-WatchSearchStage 'failure-observed' } catch { }
    }
    finally {
        if ($activeInlineSearch) {
            $headerSearch = Wait-AppElement -AutomationId 'HeaderSearchButton' -AnyName -Seconds 1
            if ($null -ne $headerSearch) { $headerSearch.SetFocus() }
            $script:activeInlineSearch = $false
        }
    }
}
function Submit-PublicSearch([string] $Query, [string] $EditorName = '搜索视频或用户输入内容') {
    $publicBv = $Query -eq 'BV1spz8B4EXv' -or ($CommentsOnly -and $Query -eq 'BV1J38968ECo')
    if (-not $publicBv -and $Query -ne 'TNTxiaoluo') { throw 'Only fixed public probe search inputs may be written.' }
    $search = Wait-AppElement -Name '打开搜索键盘' -AutomationId 'SearchEntryButton' -Seconds 15
    if ($null -eq $search) { throw 'The native search entry was not present in the exact BiliXbox PID.' }
    if ($CommentsOnly) {
        # This targeted probe starts as soon as the empty home surface appears.
        # Give the exact app entry native focus before starting its editor;
        # never focus another process or inject a desktop activation key.
        $search.SetFocus()
        Start-Sleep -Milliseconds 220
    }
    Invoke-AppElement $search
    $script:activeInlineSearch = $true
    $editor = Wait-InlineEditorFocused -Seconds 5
    if ($CommentsOnly) { Record-WatchSearchStage 'public-comment-search-editor-checked' }
    if ($null -eq $editor) { throw 'The native search editor was not exposed through the app UI Automation provider.' }
    if ($publicBv) { $report.NativeSearchOpened = $true } else { $report.UserSearchOpened = $true }
    $valuePattern = $null
    if ($editor.Current.ProcessId -ne $AppProcessId -or -not $editor.TryGetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern, [ref] $valuePattern)) { throw 'The native search editor has no exact-process ValuePattern; no keyboard fallback was used.' }
    ([System.Windows.Automation.ValuePattern] $valuePattern).SetValue($Query)
    if ($publicBv) { $report.PublicBvWritten = $true } else { $report.UserQueryWritten = $true }
    $modalActions = @(Find-AppElements -AutomationId 'PrimaryButton' -AnyName -IncludeOffscreen)
    $modalActions += @(Find-AppElements -AutomationId 'CloseButton' -AnyName -IncludeOffscreen)
    $report.InlineSearchDialogAbsent = $modalActions.Count -eq 0
    if (-not $report.InlineSearchDialogAbsent) { throw 'An extra app input-dialog action remains exposed.' }
    if ($Query -eq 'BV1spz8B4EXv') {
        $originalEditorId = $editor.GetRuntimeId() -join ','
        $headerSearch = Wait-AppElement -AutomationId 'HeaderSearchButton' -AnyName -Seconds 3
        if ($null -eq $headerSearch) { throw 'No exact-process header target for leaving inline editing.' }
        $headerSearch.SetFocus()
        $searchAgain = Wait-AppElement -Name '打开搜索键盘' -AutomationId 'SearchEntryButton' -Seconds 5
        if ($null -eq $searchAgain) { throw 'Leaving inline editing did not restore the original search field.' }
        Invoke-AppElement $searchAgain
        $editor = Wait-AppElement -Name $EditorName -AutomationId 'SearchInput' -Seconds 5
        if ($null -eq $editor) { throw 'The inline editor could not be reopened after cancellation.' }
        $editor = Wait-InlineEditorFocused
        if ($null -eq $editor) { throw 'The reopened page editor did not obtain stable focus.' }
        $report.InlineSearchReopened = $true
        $report.InlineSearchEditorRecreated = ($editor.GetRuntimeId() -join ',') -ne $originalEditorId
        $valuePattern = $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $report.InlineSearchDraftPreserved = ([System.Windows.Automation.ValuePattern]$valuePattern).Current.Value -eq $Query
        if (-not $report.InlineSearchDraftPreserved) { throw 'The inline draft changed after leaving and reopening editing.' }
    }
    $submit = Wait-AppElement -Name '搜索' -AutomationId 'SearchSubmitButton' -Seconds 5
    if ($null -eq $submit) { throw 'The exact page search action was not found; no ambiguous button was invoked.' }
    Invoke-AppElement $submit
    $script:activeInlineSearch = $false
    # The editing surface must be released before potentially slow video,
    # comment and search requests complete. A new watch search can start while
    # those requests are in flight; an old finally must not hide its editor.
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $visibleEditors = @(Find-AppElements -AutomationId 'SearchInput' -AnyName)
        if ($visibleEditors.Count -eq 0) { break }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt 2)
    $report.InlineSearchReleasedBeforeNavigation = $visibleEditors.Count -eq 0
    if (-not $report.InlineSearchReleasedBeforeNavigation) { throw 'The submitted editor remained active while navigation was already loading.' }
    if ($publicBv) { $report.SearchSubmitted = $true } else { $report.UserSearchSubmitted = $true }
}
function Test-FirstFeedPageReplacement {
    # UIA cannot simulate a physical D-pad here. Verify the real post-load focus
    # and scroll position without moving focus, selecting a row, or global input.
    try {
        $next = Wait-AppElement -Name '下一页' -AutomationId 'NextPage' -Seconds 12
        if ($null -eq $next) { $report.PagingLimitation = 'No enabled exact-process next-page action was exposed; pagination was not exercised.'; return }
        $report.PagingAttempted = $true
        Invoke-AppElement $next
        $secondPage = Wait-AppElement -Name '第 2 页' -Seconds 15
        $report.PageTwoReached = $null -ne $secondPage
        if (-not $report.PageTwoReached) { $report.PagingLimitation = 'Page two did not finish loading within the observation period.'; Read-AppNotices; return }
        $grids = @(Find-AppElements -AnyName -AutomationId 'VideoGrid' | Where-Object { $_.Current.IsEnabled })
        $grid = if ($grids.Count -gt 0) { $grids[0] } else { $null }
        if ($null -eq $grid) { $report.PagingLimitation = 'The exact-process video GridView provider was not exposed; first-item and scroll assertions are unavailable.'; return }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        do {
            Assert-AppProcess
            $scrollPattern = $null
            if ($grid.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref] $scrollPattern)) {
                $scroll = ([System.Windows.Automation.ScrollPattern] $scrollPattern).Current
                $report.PagingScrollTop = -not $scroll.VerticallyScrollable -or [Math]::Abs($scroll.VerticalScrollPercent) -lt 0.1
            }
            $itemCondition = [System.Windows.Automation.AndCondition]::new(@(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
            ))
            $items = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, $itemCondition)
            if ($items.Count -gt 0) {
                $first = $items.Item(0)
                $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
                if ($null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId) {
                    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
                    for ($depth = 0; $null -ne $focused -and $depth -lt 12; $depth++) {
                        if ([System.Windows.Automation.Automation]::Compare($focused, $first)) { $report.PagingFirstItemFocused = $true; break }
                        if ([System.Windows.Automation.Automation]::Compare($focused, $grid)) { break }
                        $focused = $walker.GetParent($focused)
                    }
                }
            }
            $report.PagingVerified = $report.PagingScrollTop -and $report.PagingFirstItemFocused
            if ($report.PagingVerified) { break }
            Start-Sleep -Milliseconds 180
        } while ($clock.Elapsed.TotalSeconds -lt 4)
        if (-not $report.PagingVerified) {
            $report.PagingLimitation = 'Page two appeared, but UIA did not confirm both first-item keyboard focus and top scroll position. No input fallback was used.'
        }
    } catch {
        $report.PagingLimitation = 'Pagination observation failed: ' + $_.Exception.Message
    }
}
function Read-PublicUserDiagnostics {
    # Only this app's public user-result GridView and its public rows are read.
    # Hidden grid candidates are included to distinguish missing/hidden UIA
    # providers, never to broaden actions to another app or account controls.
    try {
        Assert-AppProcess
        $report.UserGridDiagnostics = @()
        $report.UserResultDiagnostics = @()
        $report.UserTextDiagnostics = @()
        $grids = @(Find-AppElements -AnyName -AutomationId 'UserGrid' -IncludeOffscreen)
        foreach ($grid in @($grids | Select-Object -First 4)) {
            if ($grid.Current.ProcessId -ne $AppProcessId) { continue }
            $gridName = $grid.Current.Name
            $report.UserGridDiagnostics += [pscustomobject]@{
                Name = $gridName.Substring(0, [Math]::Min(200, $gridName.Length))
                AutomationId = $grid.Current.AutomationId
                ControlType = $grid.Current.ControlType.ProgrammaticName
                Visible = -not $grid.Current.IsOffscreen
                Enabled = $grid.Current.IsEnabled
            }
            $rowCondition = [System.Windows.Automation.AndCondition]::new(@(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
            ))
            $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
            for ($index = 0; $index -lt $rows.Count -and $report.UserResultDiagnostics.Count -lt 20; $index++) {
                $row = $rows.Item($index)
                if ($row.Current.ProcessId -ne $AppProcessId) { continue }
                $rowName = $row.Current.Name
                $report.UserResultDiagnostics += [pscustomobject]@{
                    Name = $rowName.Substring(0, [Math]::Min(200, $rowName.Length))
                    AutomationId = $row.Current.AutomationId
                    ControlType = $row.Current.ControlType.ProgrammaticName
                    Visible = -not $row.Current.IsOffscreen
                    Enabled = $row.Current.IsEnabled
                }
            }
            $textCondition = [System.Windows.Automation.AndCondition]::new(@(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
            ))
            $texts = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
            for ($index = 0; $index -lt $texts.Count -and $report.UserTextDiagnostics.Count -lt 20; $index++) {
                $text = $texts.Item($index)
                if ($text.Current.ProcessId -ne $AppProcessId) { continue }
                $publicText = $text.Current.Name
                $report.UserTextDiagnostics += [pscustomobject]@{
                    Name = $publicText.Substring(0, [Math]::Min(200, $publicText.Length))
                    AutomationId = $text.Current.AutomationId
                    ControlType = $text.Current.ControlType.ProgrammaticName
                    Visible = -not $text.Current.IsOffscreen
                }
            }
        }
    } catch { $report.UserDiagnosticsFailure = $_.Exception.Message }
}
function Wait-PublicUserResult([int] $Seconds = 20) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $grids = @(Find-AppElements -AnyName -AutomationId 'UserGrid')
        if ($grids.Count -gt 0) {
            $condition = [System.Windows.Automation.AndCondition]::new(@(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'TNTxiaoluo'),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
            ))
            $items = $grids[0].FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
            for ($index = 0; $index -lt $items.Count; $index++) {
                $item = $items.Item($index)
                if ($item.Current.ProcessId -eq $AppProcessId -and -not $item.Current.IsOffscreen -and $item.Current.IsEnabled) { return $item }
            }
        }
        Start-Sleep -Milliseconds 200
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    return $null
}
function Get-ListRows($Grid) {
    Assert-AppProcess
    if ($null -eq $Grid -or $Grid.Current.ProcessId -ne $AppProcessId) {
        throw 'A focus observation requires an exact-process, known public list.'
    }
    $publicList = $Grid.Current.AutomationId -in @('VideoGrid', 'CreatorVideoGrid', 'CreatorDynamicList')
    $commentList = $CommentsOnly -and $report.CommentResults.PublicVideoOpened -and
        $report.CommentResults.CommentListOpened -and $report.CommentResults.PublicBv -eq 'BV1J38968ECo' -and
        $Grid.Current.AutomationId -in @('CommentList', 'ReplyList') -and
        $Grid.Current.ControlType -eq [System.Windows.Automation.ControlType]::List -and -not $Grid.Current.IsOffscreen
    if (-not ($publicList -or $commentList)) {
        throw 'A focus observation requires an exact-process, known public list.'
    }
    $condition = [System.Windows.Automation.AndCondition]::new(@(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    ))
    $rows = $Grid.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
    for ($index = 0; $index -lt $rows.Count; $index++) {
        $row = $rows.Item($index)
        if ($row.Current.ProcessId -eq $AppProcessId -and -not $row.Current.IsOffscreen -and $row.Current.IsEnabled) { $row }
    }
}
function Read-Rect($Rect) {
    [pscustomobject]@{ X = [Math]::Round($Rect.X, 2); Y = [Math]::Round($Rect.Y, 2)
        Width = [Math]::Round($Rect.Width, 2); Height = [Math]::Round($Rect.Height, 2) }
}
function Test-ElementDescendant($Element, $Ancestor) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    for ($depth = 0; $null -ne $Element -and $depth -lt 24; $depth++) {
        if ([System.Windows.Automation.Automation]::Compare($Element, $Ancestor)) { return $true }
        $Element = $walker.GetParent($Element)
    }
    return $false
}
function Save-AppScreenshot($KnownList, [string] $Label) {
    # Capture only an exact app-owned UI root, with a verified foreground app or
    # its ApplicationFrameHost. No desktop screenshot or background fallback.
    try {
        Assert-AppProcess
        if ($Label -notmatch '^[a-z0-9-]+$') { throw 'Invalid screenshot label.' }
        if (-not ('BiliXboxUiProbe.NativeWindow' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace BiliXboxUiProbe {
    public static class NativeWindow {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        public static bool PointBelongsToAppOrForeground(int x, int y, uint appProcess, IntPtr foreground) {
            var window = WindowFromPoint(new Point { X = x, Y = y });
            uint process;
            GetWindowThreadProcessId(window, out process);
            return window != IntPtr.Zero && (process == appProcess ||
                GetAncestor(window, 2) == GetAncestor(foreground, 2));
        }
    }
}
'@
        }
        $foreground = [BiliXboxUiProbe.NativeWindow]::GetForegroundWindow()
        if ($foreground -eq [IntPtr]::Zero) { throw 'No foreground window.' }
        [uint32] $foregroundProcess = 0
        $null = [BiliXboxUiProbe.NativeWindow]::GetWindowThreadProcessId($foreground, [ref] $foregroundProcess)
        $foregroundElement = [System.Windows.Automation.AutomationElement]::FromHandle($foreground)
        if ($null -eq $foregroundElement) { throw 'No foreground UI Automation window.' }
        if ($foregroundProcess -ne $AppProcessId) {
            $hostProcess = Get-Process -Id $foregroundProcess -ErrorAction Stop
            if ($hostProcess.ProcessName -ne 'ApplicationFrameHost') { throw 'The foreground window is not this app or its host.' }
            $appCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId)
            if ($null -eq $foregroundElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $appCondition)) {
                throw 'The foreground frame does not contain the exact app PID.'
            }
        }
        if ($null -eq $KnownList -or $KnownList.Current.ProcessId -ne $AppProcessId) { throw 'No exact-process screenshot anchor.' }
        $candidate = $KnownList
        $root = $null
        $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
        for ($depth = 0; $null -ne $candidate -and $depth -lt 24; $depth++) {
            if ($candidate.Current.ProcessId -ne $AppProcessId) { break }
            if (-not $candidate.Current.IsOffscreen -and $candidate.Current.BoundingRectangle.Width -gt 0) { $root = $candidate }
            $candidate = $walker.GetParent($candidate)
        }
        if ($null -eq $root) { throw 'No exact app-owned screenshot root.' }
        $bounds = $root.Current.BoundingRectangle
        $foregroundBounds = $foregroundElement.Current.BoundingRectangle
        if ($bounds.IsEmpty -or $bounds.Width -lt 300 -or $bounds.Height -lt 200 -or
            $bounds.Left -lt $foregroundBounds.Left - 2 -or $bounds.Top -lt $foregroundBounds.Top - 2 -or
            $bounds.Right -gt $foregroundBounds.Right + 2 -or $bounds.Bottom -gt $foregroundBounds.Bottom + 2) {
            throw 'The app root rectangle is not bounded by the verified foreground window.'
        }
        Add-Type -AssemblyName System.Drawing
        Add-Type -AssemblyName System.Windows.Forms
        $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $x = [int][Math]::Ceiling($bounds.X); $y = [int][Math]::Ceiling($bounds.Y)
        $width = [int][Math]::Floor($bounds.Right) - $x; $height = [int][Math]::Floor($bounds.Bottom) - $y
        if ($x -lt $screen.Left -or $y -lt $screen.Top -or $x + $width -gt $screen.Right -or $y + $height -gt $screen.Bottom) {
            throw 'The exact app root is not fully within the current screen area.'
        }
        $directory = [IO.Path]::GetDirectoryName($ReportPath)
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $path = Join-Path $directory ($Label + '.png')
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = $null
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            Assert-AppProcess
            # Recheck the foreground immediately before copying pixels.
            if ([BiliXboxUiProbe.NativeWindow]::GetForegroundWindow() -ne $foreground) { throw 'The foreground changed before screenshot capture.' }
            # A taskbar thumbnail can overlap an otherwise foreground app.
            # Check a bounded grid only inside this app rectangle; do not capture
            # another application's overlay or inspect its title/content.
            for ($sampleY = 0; $sampleY -lt 7; $sampleY++) {
                for ($sampleX = 0; $sampleX -lt 9; $sampleX++) {
                    $pointX = $x + [int](($sampleX + 0.5) * $width / 9)
                    $pointY = $y + [int](($sampleY + 0.5) * $height / 7)
                    if (-not [BiliXboxUiProbe.NativeWindow]::PointBelongsToAppOrForeground($pointX, $pointY, $AppProcessId, $foreground)) {
                        throw 'A foreign overlay covers the app screenshot area; capture was skipped.'
                    }
                }
            }
            $graphics.CopyFromScreen($x, $y, 0, 0, $bitmap.Size)
            $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        } finally {
            if ($null -ne $graphics) { $graphics.Dispose() }
            $bitmap.Dispose()
        }
        $report.Screenshots += [pscustomobject]@{ Label = $Label; Path = $path; Bounds = Read-Rect $bounds; ExactProcessId = $AppProcessId }
    } catch { $report.ScreenshotLimitations += ($Label + ': ' + $_.Exception.Message) }
}
function Measure-PublicListFocus([string] $AutomationId, [string] $Label, [int] $Seconds = 8) {
    try {
        if ($AutomationId -notin @('VideoGrid', 'CreatorVideoGrid', 'CreatorDynamicList')) { throw 'The list is outside the focus-observation allowlist.' }
        $grid = Wait-AppElement -AnyName -AutomationId $AutomationId -Seconds $Seconds
        if ($null -eq $grid) { throw 'No visible exact-process list provider.' }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        do {
            $rows = @(Get-ListRows $grid)
            if ($rows.Count -gt 0) { break }
            Start-Sleep -Milliseconds 180
        } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
        if ($rows.Count -eq 0) { throw 'No visible public rows were exposed; data may be empty or limited.' }
        if ($AutomationId -eq 'CreatorDynamicList') { $report.UserDynamicRowsExposed = $rows.Count }
        $first = $rows[0]
        $firstBounds = $first.Current.BoundingRectangle
        # Select only rows already realized in the same first visible row. No
        # scroll, blind keys, or invoking public video items is required.
        $sameRow = @($rows | Where-Object { [Math]::Abs($_.Current.BoundingRectangle.Y - $firstBounds.Y) -lt 10 } |
            Sort-Object { $_.Current.BoundingRectangle.X })
        $targets = @([pscustomobject]@{ Element = $first; Suffix = 'first' })
        if ($sameRow.Count -gt 1 -and -not [System.Windows.Automation.Automation]::Compare($first, $sameRow[-1])) {
            $targets += [pscustomobject]@{ Element = $sameRow[-1]; Suffix = 'last-column' }
        }
        foreach ($target in $targets) {
            $row = $target.Element
            Assert-AppProcess
            if ($row.Current.ProcessId -ne $AppProcessId -or $row.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem -or
                -not $row.Current.IsEnabled -or $row.Current.IsOffscreen -or -not (Test-ElementDescendant $row $grid)) {
                throw 'The exact-process public focus target is not available in its allowed list.'
            }
            $row.SetFocus()
            Start-Sleep -Milliseconds 400
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            $hasFocus = $null -ne $focused -and $focused.Current.ProcessId -eq $AppProcessId -and (Test-ElementDescendant $focused $row)
            $bounds = $row.Current.BoundingRectangle; $viewport = $grid.Current.BoundingRectangle
            $inside = -not $bounds.IsEmpty -and $bounds.Left -ge $viewport.Left - 1 -and $bounds.Top -ge $viewport.Top - 1 -and
                $bounds.Right -le $viewport.Right + 1 -and $bounds.Bottom -le $viewport.Bottom + 1
            $report.FocusBoundsObservations += [pscustomobject]@{
                List = $AutomationId; Position = $target.Suffix; ExactProcessId = $AppProcessId
                KeyboardFocusObserved = $hasFocus; Bounds = Read-Rect $bounds; ViewportBounds = Read-Rect $viewport
                ObservedBoundsInsideViewport = $inside; PixelVerified = $false
                Limitation = 'UIA bounds may omit compositor scale and shadow. These measurements do not establish pixel clipping or Xbox rendering.'
            }
            Save-AppScreenshot $grid ($Label + '-' + $target.Suffix)
        }
    } catch {
        $report.FocusBoundsLimitations += ($AutomationId + ': ' + $_.Exception.Message)
        if ($AutomationId -eq 'CreatorDynamicList') { $report.UserDynamicLimitation = $_.Exception.Message }
    }
}
try {
    Ensure-AppProviderVisible
    if (($LoginOnly -or $CommentsOnly) -and -not (Ensure-TargetedAppFocus)) {
        throw $report.StartupExactWindowFocusLimitation
    }
    if ($SnapshotOnly) {
        Read-AppNotices
        $report.Completed = $true
    } elseif ($CommentsOnly) {
        Test-CommentsReadOnly
    } elseif ($LoginOnly) {
        Test-LoginReadOnly
    } elseif ($LiveOnly) {
        Test-LiveReadOnly
    } elseif ($InteractionsOnly) {
        Test-ReadOnlyInteractions
    } else {
        Test-FirstFeedPageReplacement
        Measure-PublicListFocus -AutomationId 'VideoGrid' -Label 'home-edge'
        Submit-PublicSearch -Query 'BV1spz8B4EXv'
        $creator = Wait-AppElement -Name '打开 UP 主主页，查看视频和动态' -Seconds 25
        if ($null -eq $creator) { Read-AppNotices; throw 'The public video did not expose a usable creator entry; API/network failures are recorded separately.' }
        Test-WatchInlineSearch
        Test-DrawerFocus
        # The search regression can reload this same public video. Reacquire
        # its enabled owner after metadata finishes, rather than using the
        # provider captured before that navigation.
        $creator = Wait-AppElement -Name '打开 UP 主主页，查看视频和动态' -Seconds 15
        if ($null -eq $creator) { throw 'The reloaded public video did not expose an enabled creator entry.' }
        Invoke-AppElement $creator
        $videos = Wait-AppElement -Name '视频' -Seconds 8
        $dynamics = Wait-AppElement -Name '动态' -Seconds 5
        $report.VideoTabPresent = $null -ne $videos
        $report.DynamicTabPresent = $null -ne $dynamics
        $report.CreatorOpened = $report.VideoTabPresent -and $report.DynamicTabPresent
        if (-not $report.CreatorOpened) { throw 'Creator tabs did not appear after the exact owner button invocation.' }
        Invoke-AppElement $dynamics
        $report.DynamicTabOpened = $true
        $dynamicClock = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 250
            Read-AppNotices
            $loaded = @($report.Notices | Where-Object { $_ -match '已加载 ([0-9]+) 条动态' })
            if ($loaded.Count -gt 0) {
                $report.DynamicDataLoaded = $true
                $report.DynamicCount = [int] [regex]::Match($loaded[0], '已加载 ([0-9]+) 条动态').Groups[1].Value
                break
            }
            if (@($report.Notices | Where-Object { $_ -match '动态加载失败|暂未读取到公开动态' }).Count -gt 0) { break }
        } while ($dynamicClock.Elapsed.TotalSeconds -lt 10)
        $videos = Wait-AppElement -Name '视频' -Seconds 3
        Invoke-AppElement $videos
        $back = Wait-AppElement -Name '返回' -Seconds 3
        Invoke-AppElement $back
        $restoredCreator = Wait-AppElement -Name '打开 UP 主主页，查看视频和动态' -Seconds 8
        $report.ReturnedToVideo = $null -ne $restoredCreator
        if (-not $report.ReturnedToVideo) { throw 'Creator page return did not restore the video owner control.' }
        $feedBack = Wait-AppElement -Name '返回视频列表' -Seconds 3
        if ($null -eq $feedBack) { throw 'The player did not expose its exact-process return-to-feed action.' }
        Invoke-AppElement $feedBack
        $feedSearch = Wait-AppElement -Name '打开搜索键盘' -Seconds 5
        $report.ReturnedToFeed = $null -ne $feedSearch
        if (-not $report.ReturnedToFeed) { throw 'Returning from the public video did not restore the feed search action.' }
        Submit-PublicSearch -Query 'TNTxiaoluo'
        $usersTab = Wait-AppElement -Name '用户' -AutomationId 'SearchCreatorButton' -Seconds 15
        if ($null -eq $usersTab) { throw 'The explicit user-search tab did not appear after the public keyword search.' }
        Invoke-AppElement $usersTab
        $report.UserModeSelected = $true
        $user = Wait-PublicUserResult -Seconds 25
        if ($null -eq $user) { Read-AppNotices; throw 'No exact-process TNTxiaoluo user-result ListItem was exposed; no account-header or approximate target was invoked.' }
        $report.UserResultFound = $true
        Invoke-AppElement $user
        $userVideos = Wait-AppElement -Name '视频' -Seconds 8
        $userDynamics = Wait-AppElement -Name '动态' -Seconds 5
        $report.UserCreatorVideoTabPresent = $null -ne $userVideos
        $report.UserCreatorDynamicTabPresent = $null -ne $userDynamics
        $report.UserCreatorOpened = $report.UserCreatorVideoTabPresent -and $report.UserCreatorDynamicTabPresent
        if (-not $report.UserCreatorOpened) { throw 'Opening the public user search result did not expose creator video/dynamic tabs.' }
        Measure-PublicListFocus -AutomationId 'CreatorVideoGrid' -Label 'creator-video'
        $userDynamics = Wait-AppElement -Name '动态' -Seconds 3
        Invoke-AppElement $userDynamics
        $report.UserCreatorDynamicTabOpened = $true
        Measure-PublicListFocus -AutomationId 'CreatorDynamicList' -Label 'creator-dynamics'
        Read-AppNotices
        $userBack = Wait-AppElement -Name '返回' -Seconds 3
        Invoke-AppElement $userBack
        $results = Wait-AppElement -AnyName -AutomationId 'UserGrid' -Seconds 8
        $report.ReturnedToUserResults = $null -ne $results
        $report.Completed = $report.CreatorOpened -and $report.ReturnedToVideo -and $report.UserCreatorOpened -and $report.ReturnedToUserResults
        if (-not $report.Completed) { throw 'Closing the user homepage did not restore the user search-result grid.' }
    }
} catch {
    $report.Failure = $_.Exception.Message
    Write-Host ('Creator UI probe limitation/failure: ' + $report.Failure)
    try { Read-AppNotices } catch { }
    if ($report.UserModeSelected -and -not $report.UserResultFound) { Read-PublicUserDiagnostics }
} finally {
    # Leave only this app's inline search editor. No system-keyboard/global
    # input action or implicit search submission is sent by this helper.
    if ($activeInlineSearch) {
        try {
            $headerSearch = Wait-AppElement -AutomationId 'HeaderSearchButton' -AnyName -Seconds 1
            if ($null -ne $headerSearch) { $headerSearch.SetFocus() }
        } catch { }
    }
    if (-not $report.Completed) {
        try { $null = @(Read-AppProviderSnapshot 'final-failed-probe') }
        catch { $report.StartupUiaLimitation = $_.Exception.Message }
    }
    $parent = [IO.Path]::GetDirectoryName($ReportPath)
    if (-not [IO.Directory]::Exists($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
    [IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 7), (New-Object Text.UTF8Encoding($false)))
    Write-Host ('Creator UI report: ' + $ReportPath)
}
if (-not $report.Completed) { exit 1 }
