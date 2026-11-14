# Requires PowerShell 7. Packages an existing signed x64 Appx; it does not build or install it.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$PublicCertificatePath,
    [string]$DependenciesDirectory,
    [string]$SignToolPath,
    [string]$DisplayVersion,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\BiliXbox-Xbox-Test.zip')
)

$ErrorActionPreference = 'Stop'
if ($DisplayVersion -and $DisplayVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+b?$') {
    throw 'DisplayVersion must be a release display version such as 1.0.2b; the Appx identity remains numeric.'
}
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$workspacePrefix = $workspace + [IO.Path]::DirectorySeparatorChar

function Resolve-WorkspacePath {
    param([string]$Path, [switch]$MustExist)
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Keep package inputs and outputs within this workspace: $fullPath"
    }
    if ($MustExist -and -not (Test-Path -LiteralPath $fullPath)) { throw "Path does not exist: $fullPath" }
    # Reject junctions/symbolic links so dependency copying stays inside the workspace.
    $current = $fullPath
    while ($current -and $current.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Symbolic link/junction paths are not supported for packaging: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $fullPath
}

function Read-PackageManifest {
    param([string]$Path, [switch]$RequireSignature)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if (-not $entry -or $entry.Length -gt 2MB) { throw "Missing/invalid AppxManifest.xml: $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($RequireSignature) {
            $signature = $archive.GetEntry('AppxSignature.p7x')
            if (-not $signature -or $signature.Length -lt 5 -or $signature.Length -gt 10MB) {
                throw 'The main Appx must be signed before packaging.'
            }
            $stream = $signature.Open()
            $buffer = [IO.MemoryStream]::new()
            try { $stream.CopyTo($buffer); $signatureBytes = $buffer.ToArray() }
            finally { $stream.Dispose(); $buffer.Dispose() }
            if ([Text.Encoding]::ASCII.GetString($signatureBytes, 0, 4) -ne 'PKCX') { throw 'Invalid Appx signature header.' }
            $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
            $cms.Decode($signatureBytes[4..($signatureBytes.Length - 1)])
            # Validate the CMS signature without adding a self-signed test certificate to any store.
            # Full Appx content/trust validation is additionally available with -SignToolPath.
            $cms.CheckSignature($true)
            if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $certificate.Thumbprint) {
                throw 'The Appx signer does not match the supplied public CER.'
            }
        }
        return $xml
    } finally { $archive.Dispose() }
}

$package = Resolve-WorkspacePath $PackagePath -MustExist
$cer = Resolve-WorkspacePath $PublicCertificatePath -MustExist
$output = Resolve-WorkspacePath $OutputPath
if ([IO.Path]::GetExtension($package) -ne '.appx') { throw 'Choose the signed x64 .appx file, not a ZIP, PFX or bundle.' }
if ([IO.Path]::GetExtension($cer) -ne '.cer') { throw 'PublicCertificatePath must be a public .cer file. Do not distribute the PFX.' }
if ([IO.Path]::GetExtension($output) -ne '.zip') { throw 'OutputPath must end in .zip.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
$staging = $null
try {
    if ($certificate.HasPrivateKey) { throw 'The distribution certificate must not contain a private key.' }
    if ($certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or
        $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw 'The public certificate is not currently valid.' }
    $manifest = Read-PackageManifest $package -RequireSignature
    $identity = $manifest.Package.Identity
    if ([string]$identity.ProcessorArchitecture -ne 'x64') { throw 'Xbox UWP requires an x64 package.' }
    $publisher = [Security.Cryptography.X509Certificates.X500DistinguishedName]::new([string]$identity.Publisher)
    if ([Convert]::ToBase64String($publisher.RawData) -ne [Convert]::ToBase64String($certificate.SubjectName.RawData)) {
        throw 'The Appx Publisher does not match the public certificate.'
    }
    if ($SignToolPath) {
        if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw 'SignToolPath does not exist.' }
        & $SignToolPath verify /pa /v /all $package
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool verification failed ($LASTEXITCODE). Self-signed certificates also need trust for /pa verification; this script never installs a certificate."
        }
    }
    $dependencyFiles = @()
    $dependencyManifests = @()
    if ($DependenciesDirectory) {
        $dependencyRoot = Resolve-WorkspacePath $DependenciesDirectory -MustExist
        if (-not (Test-Path -LiteralPath $dependencyRoot -PathType Container)) { throw 'DependenciesDirectory must be a directory.' }
        # A package's Dependencies/ root may contain several architectures; choose x64 explicitly.
        $x64Root = Join-Path $dependencyRoot 'x64'
        if (Test-Path -LiteralPath $x64Root -PathType Container) { $dependencyRoot = Resolve-WorkspacePath $x64Root -MustExist }
        $dependencyFiles = @(Get-ChildItem -LiteralPath $dependencyRoot -File | Where-Object { $_.Extension -eq '.appx' })
        if ($dependencyFiles.Count -eq 0) { throw 'No dependency .appx files were found in the selected x64 directory.' }
        foreach ($dependencyFile in $dependencyFiles) {
            $dependencyPath = Resolve-WorkspacePath $dependencyFile.FullName -MustExist
            $dependencyManifest = Read-PackageManifest $dependencyPath
            if ([string]$dependencyManifest.Package.Identity.ProcessorArchitecture -notin @('x64', 'neutral')) {
                throw "A dependency has an incompatible architecture: $($dependencyFile.Name)"
            }
            $dependencyManifests += $dependencyManifest
        }
    } elseif (@($manifest.Package.Dependencies.PackageDependency).Count -gt 0 -and $manifest.Package.Dependencies.PackageDependency) {
        throw 'This Appx declares framework dependencies. Supply its generated Dependencies/x64 directory.'
    }
    foreach ($dependency in @($manifest.Package.Dependencies.PackageDependency)) {
        if (-not $dependency) { continue }
        $match = $dependencyManifests | Where-Object {
            [string]$_.Package.Identity.Name -eq [string]$dependency.Name -and
            [string]$_.Package.Identity.Publisher -eq [string]$dependency.Publisher -and
            [Version]$_.Package.Identity.Version -ge [Version]$dependency.MinVersion
        }
        if (-not $match) { throw "A required dependency is missing or too old: $($dependency.Name) >= $($dependency.MinVersion)" }
    }
    $staging = Resolve-WorkspacePath (Join-Path $workspace ('artifacts\xbox-package-' + [Guid]::NewGuid().ToString('N')))
    [void][IO.Directory]::CreateDirectory($staging)
    Copy-Item -LiteralPath $package -Destination (Join-Path $staging ([IO.Path]::GetFileName($package)))
    Copy-Item -LiteralPath $cer -Destination (Join-Path $staging 'BiliXboxDev.cer')
    $thirdPartyNotices = Join-Path $workspace 'THIRD-PARTY-NOTICES.txt'
    if (Test-Path -LiteralPath $thirdPartyNotices) { Copy-Item -LiteralPath $thirdPartyNotices -Destination (Join-Path $staging 'THIRD-PARTY-NOTICES.txt') }
    if ($dependencyFiles.Count -gt 0) {
        $dependencyOutput = Join-Path $staging 'Dependencies\x64'
        [void][IO.Directory]::CreateDirectory($dependencyOutput)
        foreach ($dependencyFile in $dependencyFiles) { Copy-Item -LiteralPath $dependencyFile.FullName -Destination $dependencyOutput }
    }
    $dependencyNote = if ($dependencyFiles.Count -gt 0) { '并把 Dependencies/x64 中的全部 .appx 添加为依赖包' } else { '此包没有额外框架依赖' }
    $versionNote = if ($DisplayVersion) { "显示版本：$DisplayVersion`n安装包版本：$($identity.Version) / x64" }
        else { "安装包版本：$($identity.Version) / x64" }
    $install = @"
$($manifest.Package.Properties.DisplayName) — Xbox 开发者模式测试包
$versionNote
打包时间（UTC）：$([DateTime]::UtcNow.ToString('u'))
证书指纹：$($certificate.Thumbprint)

安装：
1. 本包用于 Xbox One / Xbox Series 的开发者模式。先在 Xbox 上完成开发者模式激活，进入 Dev Home。
2. 让电脑与 Xbox 连接同一局域网。在 Xbox Dev Home 的 Home → Remote Access Settings 中启用 Xbox Device Portal，并按屏幕提示设置用户名和密码。
3. 在电脑浏览器中打开 Xbox 屏幕显示的 Device Portal 地址；只在确认该地址是自己的 Xbox 时处理其本地 HTTPS 证书提示。
4. 在 Device Portal 的 Home 页找到应用部署区域，添加本目录的 $([IO.Path]::GetFileName($package))，$dependencyNote，然后开始部署。页面名称以设备实际显示为准。
5. BiliXboxDev.cer 是公开开发测试证书；若设备要求验证发布者，请按设备提示使用此证书。它不包含私钥。
6. 安装完成后在 Dev Home 启动“$($manifest.Package.Properties.DisplayName)”。若有应用类型选项，将其设为 App。

分区：顶部从左到右为「直播、推荐、热门、动画、影视」，每次启动默认「推荐」。热门使用独立热门列表，动画和影视读取对应投稿分区，统一卡片、翻页和焦点；B可从其他分区回推荐，视频返回原列表。
直播：进入「直播」可浏览房间、翻页或原位输入直播间号/链接。A进入房间；底栏有暂停、弹幕开关、直播聊天、礼物和画质。实时弹幕使用房间认证连接，心跳和重连有上限；离房、切分区和挂起会停止媒体与弹幕连接，恢复仅回列表，不自动播放或送礼。画质使用平台实际返回的HLS/TS AVC源，权限受限不会虚标原画。
直播聊天：显示近期及实时弹幕和礼物消息；在原输入框使用系统键盘编辑，只有选择「发送弹幕」才提交。匿名可观看和读取消息，发送需扫码登录。发送失败或结果未知保留草稿，不自动重发。
直播礼物：背包礼物和全部礼物分开浏览，选择数量后核对平台实际单价、库存、上限及合计，最后选择「确认赠送」才发送。付费礼物明确显示电池或银瓜子费用；背包礼物显示消耗数量。价格、库存或账号变化会拒绝旧确认；提交超时不自动重试，需先核对官方背包/交易记录。测试不执行真实送礼、扣费或直播弹幕发布。
推荐：首页使用推荐接口，登录且服务端识别账号后显示「为你推荐」；刷新从第1页请求新批次，上一页使用当前批次缓存。受限时会明确提示降级热门，不冒充个性化。
个人中心：选择顶部已登录用户名，显示「播放历史」「收藏」「追番追剧」三个区块。历史按服务端游标翻页，收藏先选收藏夹，追番追剧分番剧与影视，可查看分集。复用首页卡片与焦点，左摇杆/方向键选择、A确认、B逐层返回；从片库播放后返回保留原位置。登录观看后自动同步真实播放进度到当前账户的云端历史；暂停、结束和离开时补报，不导入浏览器凭据，切换账户不会串号。
剧集播放：分集使用B站PGC播放接口，保留真实权限和画质；不可观看、试看或暂不支持的版权保护格式会提示原因，不借普通BV接口替代。历史记录的可用进度可继续播放。
手柄：全程用左摇杆（L）或方向键移动焦点，A 确认；首页可选择顶部按钮、搜索、视频/用户、翻页与刷新，返回首页恢复原列表位置。B 关闭面板/隐藏控件/返回，X 发弹幕，Y 搜索。
方向节奏：v0.1.14按原始按键来源拦截手柄映射成键盘箭头/空格的重复事件，避免物理轮询与原生焦点导航叠加。点按立即一步；持续按住550ms才连移，随后每220ms一步，松开停止。左摇杆使用相同节奏，原生输入框与真正的键盘仍按各自方式操作。
搜索：在原来的搜索栏直接输入，点击搜索栏或按Y打开系统原生键盘，不再有中间额外输入弹框；明确按搜索或键盘Enter执行搜索。B收起键盘保留文字，可再次打开；中文语言与输入法由设备系统提供。
搜索模式：首次搜索默认展示关键词相关视频；选择「用户」后按关键词显示相关用户、头像、签名、粉丝/视频数，A打开该用户主页。在用户标签修改关键词仍搜索用户，选择「相关视频」可切回视频。
翻页：上一页/下一页、刷新及切换搜索类型后，列表与焦点统一回到首条结果；从首条按上进入搜索区，不会直接跳到新页底部。普通从翻页按钮按上仍进入当前列表末行。
卡片：推荐、搜索和UP视频共用模板及焦点效果。边缘预留放大安全空间，宽动态卡每侧最多扩张12像素，阴影位于背景层；列表继续滚动。UP动态为统一宽度的中性圆角卡，详情保留完整图文、转发和评论。
搜索栏：宽搜索框聚焦不放大，文字跟随焦点背景切换颜色，长关键词显示省略号。仍使用原生系统键盘。
返回：顶部「推荐」与分区切换保留该分区的页码、卡片、滚动位置和焦点，不重新刷新推荐批次；只有主动选择「刷新」才请求新内容。搜索页按 B 回推荐；视频返回先回到打开视频前的列表。
播放：视频全屏，左右选择底部按钮，上移到进度条、再上移到顶栏返回/搜索/账号，下移返回。进度条左右前后10秒。播放时闲置5秒隐藏控件，方向键/左摇杆重新显示并选择；隐藏时A播放/暂停并显示控件。评论、画质及分P从底部按钮打开。
画质：按所选档位请求 AVC/HEVC，4K 为 qn=120，携带 fnval=2000、fourk=1；仅在超高清缺档且设备确认 AV1 硬解时追加一次 fnval=4048 协商。按微软 API 检查实际编码、尺寸、帧率、位深及显示能力；不支持的4K/HDR10选项隐藏，能力未确认则保留待确认状态。菜单补入实际返回的可用轨道，支持4∶3等比例的真实2160P，不把低清轨道冒充4K。请求期间保持原播放；切换成功保留进度和暂停状态。服务端缺档、设备拒绝、源信息无效、HDMI设置失败和实际播放失败分别诊断，兼容回退继续保留。
手柄翻阅：主页LB/RB向左/向右切换直播、推荐、热门、动画、影视，到边界停止。视频分区之间有260毫秒横向滑动，向右切换时旧页向左移出；尊重系统动画关闭设置。播放器LB/RB快退/快进5秒，暂停时保持暂停；底栏±10秒按钮与进度条步长保持原行为。
右摇杆：上下连续滚动主页、用户主页、个人内容、视频评论、播放设置、弹幕设置及直播聊天/列表；有摇杆死区及速度上限。焦点跟随可见内容，松开后用方向键/左摇杆或A接管，不恢复旧的离屏选择。直播聊天也可用上下方向翻阅消息，左键直达输入框；阅读旧消息时暂停自动跟随，回到底部恢复。
登录：左侧二维码与刷新，右侧手机号/邮箱密码登录，下方切换短信验证码登录及手机区号。已处理Xbox映射后的A/Space按键，选择输入框后单次A打开原位系统键盘，B先关闭键盘再退出登录页；区号框单次A展开，不再先进入绿色接管。沿用暗色圆角面板、白色焦点，密码隐藏，密码和验证码不落盘。密码及短信发送需先完成B站安全验证，额外验证通过官方页面处理，不把待验证状态当成成功。短信只在选择获取后发送，有重发等待，不自动重复发送。Xbox部分图形验证可能需要外接鼠标，仍可使用左侧扫码。
安全验证：验证期间暂时使用登录窗口的完整宽度，原生「取消验证，返回登录」按钮固定显示。提供商自己的说明、图片和确认按钮保留可滚动空间，确认按钮可用方向键选择后按A操作；文字/坐标点选仍由用户手动完成。B优先退出验证（网页获取焦点时也有独立手柄B检测），返回后恢复获取验证码入口；长按B不连续退出下一层。请求尚未完成也能立即取消，迟到响应不重新弹出，取消不发送短信或自动重试。扫码入口在返回普通登录页后仍可使用。
HDR10输出：仅对真实HDR10轨道使用HDR10输出，保留原码流和正确颜色元数据。Xbox播放前通过HdmiDisplayInformation选择不超过60Hz的兼容SDR/HDR10模式，切换失败不标记HDR10输出成功；停止、返回或挂起时恢复默认显示模式。HDR10能力与视频会员权限分别检查，Dolby Vision和HLG不冒充HDR10。
HDR10转SDR：真实4K/10bit HDR10源通过原尺寸解码与独立SDR输出检查后，可使用系统色调映射以4K SDR播放；有正常SDR轨道时优先。原始PQ/BT2020元数据、码流和源档位保留，界面明确显示HDR10→SDR，HDR10输出状态保持关闭，不能冒充实际HDR10。复测BV12Bn86nEXE时核对sourceQn、sdrToneMapping与outputRange、原生实际尺寸及电视SDR输出。
加载：首次连接、缓冲和播放时钟停滞时显示旋转动画，弹幕同步冻结，恢复后继续。
缓冲：读取当前位置起真实连续可播区间，首次及再次缓冲达到3秒时只尝试恢复一次。耗尽后继续等待至少3秒；不足3秒的片尾可正常结束。未知缓存交还原生处理，不用下载速度估算可播秒；用户暂停、定位、换源及挂起不被迟到恢复覆盖。原生解码/关键帧与音视频同步仍会影响恢复时机。
缓冲恢复：应用主动暂停等待3秒缓存时，不再被原生解码停滞检测误判而重新打开播放源，保留已积累缓存；恢复前不再额外暂停首次原生缓冲或已暂停的媒体源。真正交还原生恢复后仍持续卡住时保留有限重建，无数据的备用来源恢复也保留。4.18MB/s等单次接收速度不能证明后续网络稳定；复测BV12kWU63EVt时一起观察缓存、进度与source-open-attempt日志。
音频：播放设置中选择标准、杜比或Hi-Res无损音轨。杜比仅使用B站真实返回的ec-3/ac-3，Hi-Res读取实际dash.flac.audio中的FLAC；分别检查当前账号登录、大会员权限及系统解码器。切换保留画质、进度和暂停状态；高级音轨失败先回退同画质标准AAC音轨，随后保留原编码回退。详情只显示已知采样率、位深和声道，不默认补成96kHz/24bit。所选FLAC不等于已证明系统逐位不变输出，杜比音轨也不等于已启用AVR直通/Atmos；实际输出由Xbox系统设置和连接设备决定。画质右侧显示「需要登录」或「需要大会员」。
速度：缓冲时显示实际媒体接收速度 KB/s / MB/s 与可播缓存秒数；下载调度空闲时可归零，不等于全设备网络带宽为零。
自动优化：后台在当前设备对服务端真实播放源作有限小范围采样，自动优选；音视频并行评估，总等待最多约1.8秒。持续缺缓存时有限恢复，保留画质、进度和暂停状态；没有手动切换按钮，也不显示当前源或切换提示。采样只用于当前网络条件的初步排序，不保证整个视频的持续带宽。
播放状态：保留实际接收量、可播缓存及当前画质平均所需速度。MP4解码器克隆共享未完成请求，避免重复下载同一段；不能保证所有Wi-Fi/CDN或硬件解码问题都解决。
时间：进度条提示使用分秒及小时格式，例如 97 秒显示 1:37，3723 秒显示 1:02:03。
评论：显示头像、点赞数、普通/收藏集/装扮表情图片及楼中楼预览；按平台实际返回展示等级、UP/置顶、IP属地及CD装扮编号，缺失则隐藏。列表图片为有界缩略图，A或「查看评论详情」进入详情，再选图片按A全屏查看。全屏支持放大/缩小、适合屏幕、LB/RB换图，B先返回原评论与阅读位置。详情可点赞/点踩或取消，复用当前账户；写入前后核对真实状态与点赞数，结果不明只读取确认，不会自动重发。未公开点踩数量时不虚构数量。「查看回复」展开线程并支持加载更多，B回主评论。分页游标相同但有新增评论时继续加载，不再因此停在40条；整页评论ID无新增时保留原列表并可重试。表情加载失败保留原文字，图片失败保留重试说明；受限响应说明部分内容并保留已加载评论。
长评论：列表显示摘要，A或「阅读全文」查看完整文字/表情；焦点框按列表区域裁剪，避免盖住发布区。顶部重复的写评论按钮已移除，任意评论处按X在底部原输入框直接写，无中间弹框；方向右可直达输入/发布，方向左回原列表。B、键盘完成或失焦保留草稿，只有选择发布才发送。动态评论也使用原位输入框。
UP主页：播放器上移到顶栏，选择UP头像/名字并按A，查看该UP的视频和动态，支持投稿分页与动态更多。动态A打开全文、图片/视频及独立评论，查看回复；X写动态评论，选择发布才提交。评论入口是否开放以B站返回为准，读取失败保留错误及重试入口。
UP返回：主页视频打开后返回可回该UP主页；从用户搜索进入的主页关闭后恢复用户结果，从播放器进入则回先前视频并恢复进度/播放状态。动态发布中禁止重复触发，离开后未确认结果会保留草稿，先刷新确认再决定是否重发。
视频互动：播放器底栏选择「互动」，可点赞/取消点赞、投1或2枚硬币、选择多个收藏夹并保存，以及确认一键三连。三连最多使用2枚硬币并收藏到默认收藏夹；已投过硬币可分别完成其他操作。复用当前登录账户，读取状态不提交操作；投币和三连显示成本，收藏未保存时按B会放弃选择。提交超时或部分成功只刷新确认，不自动重复提交；收藏变化在返回个人中心时刷新。
用户主页：选择UP头像/名字或互动侧栏的「查看UP主主页」，浏览该用户投稿和动态；投稿翻页后从首项开始，返回仍保留原列表位置。用户搜索结果可直接打开对应用户主页。
头像：播放标题下显示 UP 头像，评论和楼中楼显示作者头像；图片不可用时显示默认用户图标。
光标：v0.1.13仅在应用构造时设置Xbox鼠标模式，移除0.1.12在首页加载和轮询中重复修改全局模式的路径。先完全退出旧版，更新后重新启动。帮助「?」中的Xbox指针模式应为WhenRequested、页面为Never。左摇杆/方向键移动焦点、A确认，搜索继续使用原生键盘。
启动：首页创建异常时会显示阶段和错误编号，可按A重试。LocalState/startup-status.txt与startup-error.txt保存启动阶段、异常类型、错误码和调用栈，不含账户或登录凭据；原生系统崩溃仍需结合Device Portal记录定位。
弹幕设置：播放器底栏选择「弹幕设置」，上下选项、A切换。包括全屏/3/4/1/2/1/4显示区域、不透明度、同屏数量、字号、速度、防重叠、短时间重复过滤及各类型开关，自动保存本机设置。
云屏蔽：恢复登录及账户切换时自动读取当前账户的关键词、正则和用户屏蔽规则；可手动同步。失败保留本账户旧列表，退出或换号清除原规则。只读取云端，不会修改B站云列表；没有使用电脑浏览器登录凭据。
人物防挡：开启后读取当前视频真实SVG人物遮罩，只裁剪弹幕层，并与进度、分P及横竖屏对齐。B站未提供遮罩或读取失败会说明不可用，不用固定留白代替人物轮廓。本机已只读验证公开分段300帧SVG，Xbox实际GPU裁剪仍需测试。
高清：画质侧栏显示会员识别、实际分辨率与编码。DASH 支持实际返回且可解码的 4K/1080P 等轨道；授权仍由服务端决定。本机未用 Xbox 大会员会话验证 4K。
4K优化：逐轨确认实际尺寸、帧率、码率、位深及SDR/HDR10要求，优先已验证的HEVC。某一4K轨道失败不会否定其他4K轨道；同档其他兼容来源按有限、无环顺序尝试后，再使用真实较低画质兼容源。杜比音频先尝试同视频的标准音轨。Probably按查询当时支持处理。保留hevcPlayback资源声明；Series X/S实际4K流畅度仍需真机验证。
DCI尺寸：3996×2160或4096×2160等片源仍按原始尺寸查询硬件解码，HDMI独立检查并选择3840×2160 UHD输出；显示时按比例适配，不改写码流或用低清冒充4K。例外仅限长边3841至4096、短边不超过2160，不能用它放行8K或绕过真实解码拒绝。复测BV1kX4y197P4时核对source=3996x2160与output=3840x2160，以及实际原生尺寸和电视输出。
帧率校验：保留接口声明、能力查询和原生解析三种帧率。接口声明超过60且约不超过120fps时，以60fps参数检查基础能力，通过后读取原生轨道并按不超过60Hz输出兼容试播，明确标注兼容试播，不声称已确认62.5或120fps硬件支持。原生4K尺寸和60fps内帧率确认后才记为校验通过并取消当前行试播标记；原生尺寸不符或超过试播范围则换其他真实兼容视频轨道。不会修改码流、清单帧率或把低清放大冒充4K。复测BV1ct4y1n7t9或BV1F4Ex6xEqX时核对reportedFps、probeFps、nativeFps及native-frame-rate事件。
帧率调查：公开480P短片段已发现接口声明与实际时间戳不一致，不能据此推断所有4K轨道都同样错误。119.99/120fps超出微软文档列举的能力查询档位，不能钳制为60并宣称支持。Xbox UWP媒体的120Hz输出限制也不能当作针对某条轨道已发生的硬件解码拒绝。会员4K的真实帧率和持续播放效果仍需实机验证。
播放日志：Xbox Device Portal应用文件浏览器中下载LocalState下的playback-diagnostics.log及playback-diagnostics.previous.log。记录脱敏的参数、画质与逐轨道排除原因，以及media-opened的实际解码尺寸；不保存Cookie或媒体地址。日志限制大小并自动轮转。
弹幕分层：滚动弹幕使用用户设置的完整显示区域；顶部和底部固定弹幕可与滚动弹幕重叠，并位于上层，同层防重叠仍遵守设置。
手柄排查：本版直接读取物理手柄方向键、左摇杆和A。电脑没有连接控制器，Xbox实际导航仍需测试。若无响应，返回首页选择「?」，查看物理手柄数、最近方向、动作及读取异常计数。
建议依次检查：左摇杆/方向键遍历首页及翻页 → A打开视频后B返回原位置 → 搜索/Y → B取消后重开（三次）→ 原生中文输入 → 播放器底栏/进度条/顶栏 → 画质/评论/回复逐条滚动 → 控件隐藏时A播放/暂停 → 弱网缓冲与弹幕冻结 → 登录后4K请求 → 评论/弹幕发布与退出登录。

排查：
- 依赖或版本错误：确认选择的是 Dependencies/x64 中全部依赖，检查控制台系统更新。
- 发布者或签名错误：确认 Appx 与本包 CER 来自同一次构建；不要把未签名包用于部署。
- 已有版本无法升级且提示证书不符：先备份需要保留的信息，再卸载旧测试版本；卸载会清除本应用的本地登录状态。
- 如果无法打开 Device Portal：检查 Xbox 仍在 Dev Home、远程访问已开启，电脑与 Xbox 在同一网络。

本压缩包不包含 PFX 私钥。请勿向其他人发送 artifacts/dev-certificate 目录。
已完成本机编译及签名并不代表已完成 Xbox 真机测试。真实扫码确认、解码、评论与弹幕发布仍需在设备上验证。

微软说明：
https://learn.microsoft.com/en-us/previous-versions/windows/uwp/xbox-apps/devkit-activation
https://learn.microsoft.com/en-us/previous-versions/windows/uwp/xbox-apps/device-portal-xbox
"@
    [IO.File]::WriteAllText((Join-Path $staging 'INSTALL.txt'), $install, [Text.UTF8Encoding]::new($true))
    $hashLines = @(Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    })
    [IO.File]::WriteAllLines((Join-Path $staging 'SHA256SUMS.txt'), $hashLines, [Text.UTF8Encoding]::new($false))
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    $temporaryZip = $output + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($staging, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
        Move-Item -LiteralPath $temporaryZip -Destination $output -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip -Force }
    }
    [pscustomobject]@{
        Package = $package
        DistributionZip = $output
        Sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        Dependencies = $dependencyFiles.Count
    }
} finally {
    if ($certificate) { $certificate.Dispose() }
    # This is the random staging directory created above, resolved within the workspace.
    if ($staging -and $staging.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $staging) -match '^xbox-package-[0-9a-f]{32}$' -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
