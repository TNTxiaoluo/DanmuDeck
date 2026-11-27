#requires -Version 7.0
<#
.SYNOPSIS
Simulated TV/media remote for 弹幕舱 test builds on an Xbox.

.DESCRIPTION
Opens a Windows remote with D-pad, OK, Back, Menu, View, volume, channel,
transport (play/pause, seek, previous/next, stop) and number buttons. Presses
are sent over the LAN to the app's test-only remote bridge (TCP port 18765):
keys enter the same CoreWindow key path as a real remote, and transport
buttons enter the same MediaPlayer command path the system uses for a real
remote's media buttons. Store builds never open this port.

The Xbox (guide) and power buttons are handled by the console itself and never
reach an app, so they are not offered.

Direction/OK/Back/Menu/View can be reported three ways, because real remotes differ:
  手柄方向键  GamepadDPad* / GamepadA / GamepadB  (Xbox media remote, most HDMI-CEC TV remotes)
  导航键      Navigation*                         (some Bluetooth / universal remotes)
  键盘方向键  Up/Down/Left/Right/Enter/Escape     (keyboard-style remotes and air mice)

Holding a button repeats it like a held remote key. With the window focused:
arrows, Enter, Esc/Backspace, Space (play/pause), M (menu), V (view),
+/- (volume), PageUp/PageDown (channel), [ ] (rewind/fast forward), 0-9.

.EXAMPLE
pwsh -NoProfile -File tools/RemoteControl.ps1 -XboxAddress <Xbox 的局域网 IP，可在 Xbox 设置 → 网络 中查看>
#>
[CmdletBinding()]
param(
    [string]$XboxAddress = '',
    [int]$Port = 18765
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This tool needs Windows (WinForms).' }
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$script:client = $null
$script:writer = $null
$script:stream = $null
$script:held = $null
$script:heldSince = [DateTime]::MinValue

$modes = [ordered]@{
    '手柄方向键 (GamepadDPad)' = @{ Up = 'GamepadDPadUp'; Down = 'GamepadDPadDown'; Left = 'GamepadDPadLeft'; Right = 'GamepadDPadRight'
        Ok = 'GamepadA'; Back = 'GamepadB'; Menu = 'GamepadMenu'; View = 'GamepadView'
        Note = 'Xbox 媒体遥控器和多数电视 CEC 遥控器用这种。拿不准就选它。' }
    '导航键 (Navigation)' = @{ Up = 'NavigationUp'; Down = 'NavigationDown'; Left = 'NavigationLeft'; Right = 'NavigationRight'
        Ok = 'NavigationAccept'; Back = 'NavigationCancel'; Menu = 'NavigationMenu'; View = 'NavigationView'
        Note = '部分蓝牙遥控器、万能遥控器用这种。' }
    '键盘方向键 (Arrows)' = @{ Up = 'Up'; Down = 'Down'; Left = 'Left'; Right = 'Right'
        Ok = 'Enter'; Back = 'Escape'; Menu = 'GamepadMenu'; View = 'GamepadView'
        Note = '飞鼠、键盘式遥控器用这种，相当于按键盘方向键和回车。' }
}

$dark = [System.Drawing.Color]::FromArgb(24, 24, 28)
$buttonColor = [System.Drawing.Color]::FromArgb(52, 52, 58)
$accent = [System.Drawing.Color]::FromArgb(70, 90, 140)

$form = New-Object System.Windows.Forms.Form
$form.Text = '弹幕舱 模拟遥控器'
$form.ClientSize = New-Object System.Drawing.Size(400, 860)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false
$form.BackColor = $dark
$form.ForeColor = [System.Drawing.Color]::WhiteSmoke
$form.KeyPreview = $true
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)

$address = New-Object System.Windows.Forms.TextBox
$address.Text = $XboxAddress; $address.Location = '16,14'; $address.Size = '220,26'
$connect = New-Object System.Windows.Forms.Button
$connect.Text = '连接'; $connect.Location = '246,12'; $connect.Size = '138,30'; $connect.FlatStyle = 'Flat'
$mode = New-Object System.Windows.Forms.ComboBox
$mode.DropDownStyle = 'DropDownList'; $mode.Location = '16,50'; $mode.Size = '368,26'
foreach ($name in $modes.Keys) { [void]$mode.Items.Add($name) }
$modeNote = New-Object System.Windows.Forms.Label
$modeNote.Location = '16,80'; $modeNote.Size = '368,20'; $modeNote.ForeColor = [System.Drawing.Color]::Gray
$modeNote.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 8.5)
$mode.Add_SelectedIndexChanged({ $modeNote.Text = $modes[$mode.SelectedItem].Note })
$mode.SelectedIndex = 0
$status = New-Object System.Windows.Forms.Label
$status.Text = '未连接'; $status.Location = '16,102'; $status.Size = '368,22'; $status.ForeColor = [System.Drawing.Color]::Silver
$form.Controls.AddRange(@($address, $connect, $mode, $modeNote, $status))

function Set-Status([string]$text, [bool]$ok = $true) {
    $status.Text = $text
    $status.ForeColor = if ($ok) { [System.Drawing.Color]::LightGreen } else { [System.Drawing.Color]::Salmon }
}

function Close-Connection {
    foreach ($item in @($script:writer, $script:client)) { if ($item) { try { $item.Dispose() } catch { } } }
    $script:writer = $null; $script:client = $null; $script:stream = $null
}

function Open-Connection {
    Close-Connection
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $task = $client.ConnectAsync($address.Text.Trim(), $Port)
        if (-not $task.Wait(3000)) { throw '连接超时' }
        $script:client = $client
        $script:stream = $client.GetStream()
        $script:writer = New-Object System.IO.StreamWriter($script:stream, (New-Object System.Text.UTF8Encoding($false)))
        $script:writer.AutoFlush = $true
        $script:writer.NewLine = "`n"
        Set-Status "已连接 $($address.Text.Trim()):$Port"
    } catch {
        Close-Connection
        $message = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        Set-Status "连接失败：$message。请先在 Xbox 上打开弹幕舱测试版。" $false
    }
}

function Send-Line([string]$line) {
    if (-not $script:writer) { Set-Status '未连接，请先点“连接”' $false; return }
    try { $script:writer.WriteLine($line) }
    catch { Close-Connection; Set-Status "连接已断开：$($_.Exception.Message)" $false }
}

# A button spec is 'role:Up' (follows the selected mode), 'key:175' (a fixed
# key code) or 'media:PlayPause' (a system media button).
function Resolve-Key([string]$spec) {
    $kind, $value = $spec.Split(':', 2)
    if ($kind -eq 'role') { return $modes[$mode.SelectedItem][$value] }
    return $value
}

function Start-Hold([string]$spec) {
    if ($script:held) { return }
    $script:held = $spec; $script:heldSince = [DateTime]::UtcNow
    if ($spec.StartsWith('media:')) { Send-Line "media $($spec.Substring(6))" }
    else { Send-Line "down $(Resolve-Key $spec)" }
}
function Stop-Hold {
    if (-not $script:held) { return }
    $spec = $script:held; $script:held = $null
    if (-not $spec.StartsWith('media:')) { Send-Line "up $(Resolve-Key $spec)" }
}

# Drain replies so the socket buffer never fills; repeat held buttons.
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 60
$timer.Add_Tick({
    if ($script:stream -and $script:client -and $script:client.Available -gt 0) {
        $buffer = New-Object byte[] $script:client.Available
        $count = $script:stream.Read($buffer, 0, $buffer.Length)
        $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $count).Trim().Split("`n")[-1]
        if ($text) { Set-Status "已连接 · Xbox 回复：$text" }
    }
    if ($script:held -and ([DateTime]::UtcNow - $script:heldSince).TotalMilliseconds -ge 500) {
        $spec = $script:held
        if ($spec -in @('media:FastForward', 'media:Rewind')) { Send-Line "media $($spec.Substring(6))" }
        elseif (-not $spec.StartsWith('media:')) { Send-Line "repeat $(Resolve-Key $spec)" }
        $script:heldSince = [DateTime]::UtcNow.AddMilliseconds(-380)   # then every ~120 ms
    }
})
$timer.Start()

function Add-RemoteButton([string]$text, [string]$spec, [int]$x, [int]$y, [int]$w = 70, [int]$h = 46, [float]$size = 12, [bool]$highlight = $false) {
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $text; $button.Location = "$x,$y"; $button.Size = "$w,$h"
    $button.FlatStyle = 'Flat'; $button.BackColor = if ($highlight) { $accent } else { $buttonColor }
    $button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(80, 80, 88)
    $button.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', $size, [System.Drawing.FontStyle]::Bold)
    $button.TabStop = $false
    $button.Tag = $spec
    $button.Add_MouseDown({ param($s, $e) Start-Hold $s.Tag })
    $button.Add_MouseUp({ Stop-Hold })
    $button.Add_MouseLeave({ Stop-Hold })
    $button.Add_PreviewKeyDown({ param($s, $e) $e.IsInputKey = $true })
    $form.Controls.Add($button)
}
function Add-Caption([string]$text, [int]$y) {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $text; $label.Location = "16,$y"; $label.Size = '368,18'; $label.ForeColor = [System.Drawing.Color]::Gray
    $label.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 8.5)
    $form.Controls.Add($label)
}

# Top row like a media remote: View, Menu.
Add-RemoteButton '视图 ⧉' 'role:View' 40 134 150 40 11
Add-RemoteButton '菜单 ≡' 'role:Menu' 210 134 150 40 11

# D-pad and OK.
Add-RemoteButton '▲' 'role:Up' 160 186 80 56 16
Add-RemoteButton '◀' 'role:Left' 72 248 80 56 16
Add-RemoteButton 'OK' 'role:Ok' 160 248 80 56 16 $true
Add-RemoteButton '▶' 'role:Right' 248 248 80 56 16
Add-RemoteButton '▼' 'role:Down' 160 310 80 56 16
Add-RemoteButton '返回 ↩' 'role:Back' 40 378 150 42 12
Add-RemoteButton '主页 / Xbox' 'none' 210 378 150 42 10
$form.Controls[$form.Controls.Count - 1].Enabled = $false

# Volume and channel.
Add-Caption '音量                                          频道' 430
Add-RemoteButton '音量 +' 'key:175' 40 450 100 42 11
Add-RemoteButton '音量 −' 'key:174' 40 498 100 42 11
Add-RemoteButton '静音' 'key:173' 150 474 100 42 11
Add-RemoteButton '频道 +' 'key:PageUp' 260 450 100 42 11
Add-RemoteButton '频道 −' 'key:PageDown' 260 498 100 42 11

# Transport.
Add-Caption '播放控制' 552
Add-RemoteButton '⏮' 'media:Previous' 16 572 58 46 14
Add-RemoteButton '⏪' 'media:Rewind' 80 572 58 46 14
Add-RemoteButton '⏯' 'media:PlayPause' 144 572 112 46 14 $true
Add-RemoteButton '⏩' 'media:FastForward' 262 572 58 46 14
Add-RemoteButton '⏭' 'media:Next' 326 572 58 46 14
Add-RemoteButton '▶ 播放' 'media:Play' 16 624 120 38 10
Add-RemoteButton '⏸ 暂停' 'media:Pause' 140 624 120 38 10
Add-RemoteButton '⏹ 停止' 'media:Stop' 264 624 120 38 10

# Number pad.
Add-Caption '数字键' 672
$digits = @('1','2','3','4','5','6','7','8','9','','0','')
for ($i = 0; $i -lt $digits.Count; $i++) {
    if (-not $digits[$i]) { continue }
    $x = 70 + ($i % 3) * 90; $y = 692 + [math]::Floor($i / 3) * 40
    Add-RemoteButton $digits[$i] "key:Number$($digits[$i])" $x $y 80 34 11
}

$help = New-Object System.Windows.Forms.Label
$help.Text = 'Xbox 键和电源键由主机系统处理，App 收不到，所以是灰的。键盘：方向键 · Enter · Esc · 空格=播放暂停 · M 菜单 · V 视图 · +/- 音量 · PgUp/PgDn 频道 · [ ] 快退快进 · 0-9'
$help.Location = '16,856'; $help.Size = '368,0'; $help.AutoSize = $false
$form.ClientSize = New-Object System.Drawing.Size(400, 900)
$help.Location = '16,856'; $help.Size = '368,40'; $help.ForeColor = [System.Drawing.Color]::Gray
$help.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 8)
$form.Controls.Add($help)

$keyMap = @{
    Up = 'role:Up'; Down = 'role:Down'; Left = 'role:Left'; Right = 'role:Right'; Enter = 'role:Ok'
    Escape = 'role:Back'; Back = 'role:Back'; M = 'role:Menu'; V = 'role:View'
    Space = 'media:PlayPause'; OemOpenBrackets = 'media:Rewind'; OemCloseBrackets = 'media:FastForward'
    Oemplus = 'key:175'; Add = 'key:175'; OemMinus = 'key:174'; Subtract = 'key:174'
    PageUp = 'key:PageUp'; Prior = 'key:PageUp'; PageDown = 'key:PageDown'; Next = 'key:PageDown'
}
foreach ($n in 0..9) { $keyMap["D$n"] = "key:Number$n"; $keyMap["NumPad$n"] = "key:Number$n" }

$form.Add_KeyDown({
    param($s, $e)
    if ($address.Focused) { return }
    $spec = $keyMap[[string]$e.KeyCode]
    if (-not $spec) { return }
    $e.Handled = $true; $e.SuppressKeyPress = $true
    if ($script:held -eq $spec) { return }
    Stop-Hold; Start-Hold $spec
})
$form.Add_KeyUp({
    param($s, $e)
    if ($keyMap[[string]$e.KeyCode]) { $e.Handled = $true; Stop-Hold }
})
$form.Add_PreviewKeyDown({ param($s, $e) if (-not $address.Focused) { $e.IsInputKey = $true } })
$connect.Add_Click({ Open-Connection; $form.ActiveControl = $null })
$form.Add_Shown({ if ($address.Text.Trim()) { Open-Connection } else { Set-Status '请先填入 Xbox 的局域网 IP（Xbox 设置 → 网络），再点“连接”' $false }; $form.ActiveControl = $null })
$form.Add_FormClosed({ Stop-Hold; $timer.Stop(); Close-Connection })
[void]$form.ShowDialog()
