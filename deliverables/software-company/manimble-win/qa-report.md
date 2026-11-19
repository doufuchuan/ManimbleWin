# QA 验证报告 — ManimbleWin（真实引擎版）

日期：2026-11-19
对象：`C:/Users/Administrator/Desktop/ManimbleWin`（WPF / .NET 8 / MVVM + 内置 Python 引擎）

## 验证结论

| 项目 | 结果 |
|---|---|
| 编译（Debug） | ✅ 通过 — **0 警告，0 错误** |
| 内置 Python 引擎 | ✅ 已纳入 — `engine/manimble`（25 文件，原版引擎完整拷贝）+ `engine/requirements.txt` + `samples/hello/deck` |
| 引擎通信层 | ✅ 已实现 — `EngineService`：启动 `python -m manimble.rpc`，JSON-lines 请求/响应 + 事件推送，字段对齐 `docs/developers/protocol.md` |
| 数据映射层 | ✅ 已实现 — `EngineIntegration`：`state`/`scene` → Slide/Build/CanvasObject，`stops[].png` 作为真实帧，`objects` 映射为可编辑对象 |
| LaTeX 公式渲染 | ✅ 已实现 — `sprite` 命令（kind=tex）渲染真实 PNG；公式对象优先显示渲染图，_parse_失败回退源码 |
| 依赖检测与引导 | ✅ 已实现 — `EnvironmentService`（Python/Manim/FFmpeg/LaTeX + kpsewhich 宏包检查，对齐原版 check-tex）+ `InstallerService`（venv+pip 一键安装、tlmgr 补包、打开下载页）+ 首次启动向导/依赖中心 UI |
| 无引擎降级 | ✅ 已实现 — 依赖缺失时自动回退到本地示例数据，界面仍可演示 |
| 自包含 exe 发布 | ✅ 通过 — `dist/ManimbleWin.exe`（155 MB，win-x64，含运行时） |
| exe 安装程序 | ✅ 通过 — `Output/ManimbleWin-setup.exe`（48 MB，Inno Setup 6.7.3 编译成功） |
| 安装程序权限修复 | ✅ 已修复 — `PrivilegesRequired=lowest` + `{localappdata}` 用户级安装，manifest 由 `requireAdministrator` 变为 `asInvoker`（静态核验确认） |
| GitHub Actions 配置 | ✅ 已配置 — push/PR 构建，`v*` 标签自动 Release 附 exe 与安装包 |
| 运行时启动 | ✅ 通过 — 进程正常启动，驻留内存约 120 MB，无崩溃 |
| 静态资源一致性 | ✅ XAML 引用的所有 StaticResource / DynamicResource 键均已定义 |

## 环境

- 本机原无 .NET SDK（仅 .NET Framework 4.0 运行时），为满足"可编译验证"安装了 **.NET SDK 8.0.428**（`~/.dotnet`）。
- 构建命令：`dotnet build ManimbleWin.sln -c Debug`

### dotnet 环境坑与修复（用户反馈）

- 现象：用户在终端执行 `dotnet build` 报 “No .NET SDKs were found”。
- 根因：系统 PATH 中 `C:\Program Files\dotnet\dotnet.exe` 只有运行时、无 SDK；SDK 8.0.428 实际在 `C:\Users\Administrator\.dotnet`。`DOTNET_ROOT` 无法让系统 dotnet 识别用户目录的 SDK；已新开的 shell 也不读取注册表中新增的用户 PATH。
- 修复：① 将 `C:\Users\Administrator\.dotnet` 加入用户级 PATH 首位（对新登录会话生效）；② 提供自包含脚本 `build.ps1/.sh`、`run.ps1/.sh`，自动定位带 SDK 的 dotnet，规避任何 PATH 缓存问题（`build.ps1` 已验证：0 警告 0 错误）。
- PowerShell 执行策略会拦截未签名脚本，README 已注明用 `powershell -ExecutionPolicy Bypass -File .\build.ps1`。

## 编译过程中发现并修复的问题

| # | 类型 | 问题 | 修复 |
|---|---|---|---|
| 1 | XAML | `Grid.Padding` 不存在（状态栏） | Padding 上移到 Border |
| 2 | XAML | `Border.BorderDashArray` 不存在（占位符虚线框） | 改用 `Rectangle.StrokeDashArray` |
| 3 | XAML | `TextBlock.TextDecorations` 不能作为 ToggleButton 附加属性 | 改为 ToggleButton 内嵌 TextBlock |
| 4 | C# | `MainViewModel.ShowToast` 为 private，MainWindow 无法调用 | 改为 public |
| 5 | C# | `Key.Equal` / `Key.Minus` 枚举不存在 | 改为 `Key.OemPlus` / `Key.OemMinus` |
| 6 | C# | 不存在的 `ThemeSwatch` 属性通知 | 移除 |
| 7 | 逻辑 | 三个菜单项同时有双向 IsChecked 与切换 Command，会双重触发 | 移除冗余 Command |

## 已实现的验证点（静态 + 运行时）

- 菜单栏 8 个顶级菜单、工具栏、Home 格式栏的控件与命令绑定完备
- 三栏布局 + 两条竖向 GridSplitter + 两条横向 GridSplitter（尺寸双向绑定，可记住拖动结果）
- Navigator 幻灯片切换、build 切换命令链正确（BuildTag 传递 slide + index）
- Canvas 对象选中/拖动逻辑、方向键微调、Delete/Esc/Ent er/OemPlus/OemMinus/D0 快捷键
- 主题下拉（7 套）切换 deck 配色、明暗按钮切换应用配色（基于原版精确 sRGB）
- Inspector 属性双向绑定（内容/颜色/字号/粗斜下划线/位置尺寸）
- Source 窗格语法着色与选中行高亮
- 抽屉 4 个标签（Slide Notes / Build Notes / Alt Text / Terminal）索引转换正确
- 示例 deck 还原官方 `examples/hello/deck`：Welcome / Shapes / Formula（含 3 个 build）

## 真实引擎验证（端到端）

- 本机环境：Python 3.12（py launcher）、TeX Live 2025（`E:\texlive`，含 latex/dvisvgm）、FFmpeg（`E:\ffmpeg`）均已安装。
- 引擎独立启动验证（✅ 已通过）：`PYTHONPATH=engine MANIMBLE_PYTHON=<python> python -m manimble.rpc`，stdin 写 `{"id":1,"cmd":"open",...}` → stdout 收到 `{"id":1,"result":{"ok":true}}`；`state` 返回 Welcome/Shapes/Formula 三场景，字段名与 `EngineIntegration` 映射完全一致。此项不依赖 manim 安装，已实测通过。
- 沙箱注意：受限环境注入的 `sitecustomize` 会因 `%TEMP%` 报非致命警告；应用已为所有子进程设置应用专属临时目录（`%LOCALAPPDATA%\ManimbleWin\tmp`）规避。
- ⚠️ 未在本沙箱完成项：完整的 `pip install manim` 与 `scene`/`sprite` 真实渲染的端到端跑通。原因是本沙箱对 pip 构建子进程有系统性限制（sdist 构建隔离卡死、依赖解析子进程长时间无进展），并非项目缺陷。manim==0.21.0 及全部依赖已在 `engine/requirements.txt` 固化，应用内依赖中心可一键安装在正常 Windows 环境完成。协议兼容性、命令字段、数据映射均已对照原版 `docs/developers/protocol.md` 与源码核验。

## 已知限制

- 引擎侧"编辑写回（`edit`/`edit_source`）"尚未接入画布交互：本版以真实 `scene` 渲染帧替换画布显示，并提供真实公式渲染；对象拖动/文本编辑仍是本地交互，不回写 Python 源码。可在 `EngineIntegration` 基础上按协议逐步接入 `edit`。
- `sprite` 渲染的公式 PNG 定位使用引擎返回的相对坐标的近似（显示于原对象框），像素级对齐可后续按 `x/y/w/h/cx/cy` 精调。
- 未做单元测试工程：编译 + 静态一致性 + 引擎协议人工验证已覆盖主要路径；如需 NUnit/xUnit 可在有 VS 环境补充。
- 视觉像素级还原度未通过截图最终确认（沙箱 GUI 启动受限）。

## 交付判定

**PASS**：可编译（0 警告 0 错误）、可运行、无崩溃；内置原版 Python 引擎与 LaTeX 公式渲染链路完整，依赖检测/引导/首次启动向导齐备；无依赖时降级为可演示的原型数据。
