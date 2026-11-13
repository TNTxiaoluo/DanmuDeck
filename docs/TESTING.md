# 测试

测试脚本位于 `tools/`，替身和用例位于 `tools/tests/`。从项目根目录使用 PowerShell 7，每个脚本启动独立的 `pwsh` 进程，避免重复加载同名测试类型。

## 日常检查

```powershell
pwsh -NoProfile -File tools/Check-Source.ps1
pwsh -NoProfile -File tools/Test-ApiLogic.ps1
pwsh -NoProfile -File tools/Test-Creator.ps1
pwsh -NoProfile -File tools/Test-CreatorAccess.ps1
pwsh -NoProfile -File tools/Test-CreatorProfileRefresh.ps1
pwsh -NoProfile -File tools/Test-Search.ps1
pwsh -NoProfile -File tools/Test-GamepadInput.ps1
pwsh -NoProfile -File tools/Test-PlaybackQuality.ps1
pwsh -NoProfile -File tools/Test-Comments.ps1
```

按改动选择相关脚本；[GitHub Actions 工作流](../.github/workflows/build.yml)列出了持续集成使用的其他检查。部分脚本需要 Windows、Visual Studio 或必选的文件路径，具体以脚本顶部参数为准。

大部分默认测试编译实际生产代码，并以受控 HTTP、存储、控件或任务替身检查解析、分页、取消、账号边界和焦点状态。它们不会证明 Xbox 的实际手柄映射、系统键盘、HDMI 输出、硬件解码帧率或视觉效果。

## 可选公开接口复测

以下命令会联网读取普通公开数据。B 站接口可能变更或限制请求，失败不应被当成空列表或虚构成功结果。

```powershell
pwsh -NoProfile -File tools/Test-Search.ps1 -Live -Keyword Xbox -Mode Video -Pages 1 -Summary
pwsh -NoProfile -File tools/Test-Creator.ps1 -PublicMid 2 -PublicPages 1
```

`Test-Creator.ps1` 的 `-PublicMid` 可替换为要复测的公开用户 UID；`-PublicPages` 限制读取页数。这类探测从空的内存会话开始，不加载本机登录文件。请勿将真实 Cookie、密码、短信验证码或带凭据的请求内容加入测试 fixture。

## 安装包检查

- `Test-Package.ps1`：验证已签名原生 x64 APPX、公开证书、依赖和分发 ZIP。必须提供现有绝对路径参数 `-PackagePath`、`-PublicCertificatePath`、`-DistributionZipPath`。
- `Test-StoreUpload.ps1`：按指定身份和版本检查 Store 上传包、符号和数据块。
- `Test-StoreUploadTools.ps1`：检查商店构建工具的参数、隔离副本和身份覆盖行为，不提交商店。

安装包签名和内容校验通过后，仍需在目标设备验证启动、播放、导航、暂停/恢复和退出。

## 本机运行时探针

`Invoke-UwpRuntimeProbe.ps1`、`Probe-CreatorUi.ps1` 及名称含 `Runtime` 的脚本用于更具体的 Windows/UWP 环境检查。部分探针需要本机 Windows SDK、编译器、预先生成的包或本机工具缓存，也可能临时注册应用或启动本地测试服务。请先阅读相应脚本的参数及清理行为。

这些探针属于可选验证工具，不是所有贡献都必须执行的 CI 前置条件。开源 ZIP 不附带它们曾使用的本机 SDK、工具缓存或运行记录。
