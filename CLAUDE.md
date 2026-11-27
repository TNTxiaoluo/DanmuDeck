@AGENTS.md

# 弹幕舱开发入口

- 在 Windows 仓库根目录工作。应用是 C# / XAML UWP，入口为 `BiliXbox.sln` 和 `src/BiliXbox/`。Xbox 测试包使用 VS 2022 UWP 工具链、Windows SDK 10.0.19041.0、PowerShell 7 和 Release x64。
- 接手任务时先查看 `git status --short --branch`。如有 `artifacts/Claude-Handoff.md`，阅读其中记录的最近构建、验证结果和待复测事项，并以当前工作区为准。
- 播放、画质恢复和缓冲主要在 `src/BiliXbox/MainPage.PlaybackTransport.cs`、`src/BiliXbox/Services/`，实机调查见 `docs/PLAYBACK-DIAGNOSTICS.md`。构建操作见 `docs/BUILDING.md`，测试入口见 `docs/TESTING.md`。
- 日常检查运行 `pwsh -NoProfile -File tools/Check-Source.ps1`，再按改动范围选择 `tools/Test-*.ps1`。Xbox 测试包使用 `tools/Build-XboxTest.ps1`，Store 构建流程见 `docs/STORE.md`。报告验证时区分本地检查、桌面探针与 Xbox 实机结果。
- 发布时同时准备商店简版和 GitHub Release Markdown。先核对包内版本与身份，再区分本地已构建、Partner Center 已提交、商店已发布三个状态。
- 签名和升级沿用项目现有脚本。签名私钥、商店关联文件、账号会话、原始诊断和临时构建放在本机，按既有公开源码导出流程生成源码 ZIP。
- 署名规范：禁止在代码、注释、Git Commit、PR 和更新日志中主动添加 Claude 或 Anthropic 的作者及共同作者署名，包括 Co-Authored-By。保留真实外部贡献者的署名、原始提交记录及第三方开源许可证声明。
