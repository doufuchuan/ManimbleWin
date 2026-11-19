# ManimbleWin

Manimble 的 **Windows（WPF）桌面版**：高度还原 macOS 版 Manimble 的界面与交互，并**内置原版 Python 引擎**——通过 JSON-lines 协议驱动真实的 Manim 场景渲染与 LaTeX 公式渲染。

基于原项目 [pachterlab/Manimble](https://github.com/pachterlab/Manimble)（BSD 2-Clause）。原版是 Swift/AppKit/Metal 前端 + 平台无关的 Python 引擎（`engine/manimble`）；本项目用 C#/WPF 重建前端，并直接复用原版引擎，实现真正的幻灯片渲染。

## 能力

- **真实 Manim 渲染**：内置 `engine/manimble`，WPF 启动 `python -m manimble.rpc` 子进程，按 `engine/manimble/rpc.py` 中定义的 JSON-lines 协议通信；画布显示引擎渲染出的真实帧（`state` / `scene`）。
- **LaTeX 公式渲染**：公式对象通过引擎 `sprite` 命令（kind=tex）生成由 LaTeX 排版的真实 PNG。
- **源码双向编辑**：右下角代码窗格可直接编辑当前页 Python 源码；点击“应用”（或 Ctrl+S）经引擎 `edit_source` 写回工作副本并重渲染，未连接引擎时保存到本地。
- **对象操作写回代码**：插入文本/公式/形状/图片/表格等操作由 `SourceCodeGenerator` 翻译成 Manim 代码并同步到源码窗格；连接引擎时经 `insert` / `edit_source` 真正写回。
- **动画预览**：`播放` 菜单可打开独立预览窗口，按 build 逐帧播放（真实引擎帧或本地对象帧），支持播放/暂停、上一帧/下一帧、进度，ESC 退出；连接引擎后走 `present` / `export_movie` 生成真实片段与 MP4。
- **可交互终端**：调用本机真实的 `cmd.exe` / `powershell.exe`（`:help` / `:slides` / `:insert` / `:render` / `:play` / `:connect` 等应用内命令，Windows 命令直接敲），↑↓ 翻历史、回车执行；每个标签是独立会话，互不影响。
- **依赖检测与引导**：首次启动弹出环境准备向导，菜单 `引擎 › 依赖中心` 随时可打开，检测 Python / Manim / FFmpeg / LaTeX 状态并引导安装；Manim 栈可一键安装到项目独立虚拟环境。
- **全中文界面**：菜单、工具栏、面板、提示均为中文（Python 代码、快捷键与 Manim/LaTeX 等专有名词保留原文）。
- **原型交互保留**：未连接引擎时自动回退到本地示例数据，界面与全部点击交互仍可用，便于无 Python 环境时演示。

## 快速开始

**环境**：Windows 10/11 + .NET 8 SDK（或 Visual Studio 2022，勾选 “.NET 桌面开发”）。

> **关于 `dotnet build` 报错 “No .NET SDKs were found”**：如果你的系统 PATH 里的 `dotnet` 是纯运行时（常见于同时装过运行时与独立 SDK 的机器），裸 `dotnet build` 会失败。本项目提供自包含脚本，**自动定位带 SDK 的 dotnet**，直接用即可：

```powershell
# PowerShell（若被执行策略拦截，用：powershell -ExecutionPolicy Bypass -File .\build.ps1）
.\build.ps1          # 一键编译
.\run.ps1            # 编译并运行
```

```bash
# Git Bash
./build.sh
./run.sh
```

也可以指定完整路径手动构建：

```bash
# Windows（PowerShell）
& "$env:USERPROFILE\.dotnet\dotnet.exe" build ManimbleWin.sln -c Debug
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --project src/ManimbleWin/ManimbleWin.csproj

# 若 SDK 装在标准位置（C:\Program Files\dotnet），直接：
dotnet build ManimbleWin.sln -c Debug
dotnet run --project src/ManimbleWin/ManimbleWin.csproj
```

或在 Visual Studio 中打开 `ManimbleWin.sln`，按 F5。

## 环境依赖（真实渲染）

应用**首次启动会自动弹出“环境准备”向导**，检测并引导安装以下依赖；也可随时从菜单 `引擎 › 依赖中心…` 打开依赖中心。

| 依赖 | 用途 | 获取方式 |
|---|---|---|
| Python 3.11+ | 运行引擎 | 依赖中心引导下载；或 [python.org](https://www.python.org/downloads/windows/)（勾选 Add to PATH） |
| Manim Community 0.21.0 | 场景渲染 | 依赖中心“安装/修复”一键 `pip install -r engine/requirements.txt` 到项目 `.venv`/用户 venv |
| FFmpeg | 导出 MP4 | pip 的 `imageio-ffmpeg` 自带；或手动安装 |
| LaTeX（TinyTeX 优先） | 公式与 Beamer | [TinyTeX](https://yihui.org/tinytex/) 或已有 TeX Live / MiKTeX；依赖中心可“补装 LaTeX 宏包” |

安装步骤：

1. 首次启动向导会列出各项状态（✓ 就绪 / ✕ 未安装 / ! 版本或宏包不符）。
2. 点击缺失项的“安装 / 修复”：Manim 会自动创建虚拟环境并安装全部 Python 依赖；Python/FFmpeg/LaTeX 会打开对应下载页。
3. 装完点“重新检测”，全部核心项就绪后点“启动 Manimble”。
4. 主窗口状态栏右侧的绿点表示引擎已连接；从菜单 `引擎 › 连接 Manim 引擎` 可手动连接。

> 未安装任何依赖也能启动应用：此时使用内置示例数据（纯界面演示）。连接引擎后会加载 `samples/hello/deck` 并渲染真实帧。

手动安装（等价于依赖中心的“一键安装”）：

```powershell
# 1) 创建虚拟环境（放在 %LOCALAPPDATA%\ManimbleWin\venv）
py -3.12 -m venv "$env:LOCALAPPDATA\ManimbleWin\venv"
# 2) 安装 Manim 栈
& "$env:LOCALAPPDATA\ManimbleWin\venv\Scripts\python.exe" -m pip install -r engine\requirements.txt
# 3) LaTeX：安装 TinyTeX 后补装宏包
"$env:LOCALAPPDATA\ManimbleWin\venv\Scripts\python.exe" -c "import manim; print(manim.__version__)"
```

引擎通信实现见 `src/ManimbleWin/Services/EngineService.cs`，数据映射见 `ViewModels/EngineIntegration.cs`。

## 构建 exe 与安装程序

无需安装 .NET SDK 也能运行——直接下载安装包（见下方 GitHub Releases，或本地构建产物）。

一键产出**自包含单文件 exe**（约 155 MB，内含 .NET 运行时，目标机器无需装 .NET）：

```powershell
.\publish.ps1     # 生成 dist/ManimbleWin.exe
```

一键产出**exe 安装程序**（需先装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)，脚本 `installer.iss`）：

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
# 生成 Output/ManimbleWin-setup.exe（含桌面/开始菜单快捷方式与卸载器）
```

本机已实测：`dist/ManimbleWin.exe`（155 MB）与 `Output/ManimbleWin-setup.exe`（48 MB）均成功产出。

### 关于安装权限

安装包采用**用户级安装**（`PrivilegesRequired=lowest`），双击即可安装：

- **无需管理员权限，不弹 UAC 提权**——因此不会触发"Setup was unable to create the directory … Error 5: 拒绝访问"这类提权后无法访问 `%TEMP%` 的问题。
- 默认安装到 `%LOCALAPPDATA%\ManimbleWin`（当前用户目录），并创建桌面/开始菜单快捷方式与卸载器。
- 适合受限环境、公司托管机器或无管理员权限的账户。

如需改为全机器安装（需管理员），把 `installer.iss` 中的 `PrivilegesRequired=lowest` 改为 `admin`、`DefaultDirName` 改回 `{autopf}\{#MyAppName}` 后重新编译即可。

## 发布到 GitHub

项目已配置 GitHub Actions（`.github/workflows/build.yml`），实现一键构建与自动发布：

- **推送到 `main` 或 PR**：自动执行 `dotnet build` 验证
- **推送 `v*` 标签**（如 `git tag v0.4.7 && git push origin v0.4.7`）：自动发布自包含 exe + 用 Inno Setup 生成安装包，创建 GitHub Release 并附上 `ManimbleWin.exe` 与 `ManimbleWin-setup.exe`

新用户从 Releases 下载 `ManimbleWin-setup.exe` 双击安装即可，无需任何开发环境。

## 本次更新（v0.4）

- **菜单下拉主题适配**：自绘 MenuItem/ContextMenu/Separator 模板，下拉不再跟随系统白底，深色下为深色面板 + 悬停高亮 + 勾选标记，明暗主题都跟随切换。
- **真实 LaTeX 公式**：新增 `LatexService`（`latex` → `dvi` → `dvipng`），把公式编译成透明 PNG 并按内容哈希缓存；「插入 ▸ 公式…」打开带实时预览的公式对话框，支持 `amsmath/amssymb` 语法；本机无 LaTeX 时自动降级为 Unicode 近似显示。
- **动画预览**：新增 `AnimatedStage`，在构建之间做**对象级动画**（淡入 + 上浮 + 缩放回弹、位置/尺寸插值、退出淡出），不再是逐帧切图；预览窗可拖动进度条跳转，并可用「用 Manim 渲染真实动画」调用引擎的 `animation_preview` 播放真实 mp4 片段。
- **演示程序内置 manim 动画**：示例 deck 新增 `Animation` 场景（Write / Create / Transform / Indicate），本地演示稿也新增同名动画页；`common.py` 在 Windows 下改用 `Segoe UI`，避免缺字体渲染失败。
- **拖动写回代码坐标**：拖动对象（或方向键微调）松手后，自动把 `move_to((x, y, 0))` 写回源码对应行；行内没有 `move_to` 会追加一个；由 `self.play/self.add/VGroup().arrange` 摆放的对象会提示「由剧本语句摆放」，不会硬改坏代码。引擎已连接时再节流回写到工作副本。
- **Python 语法高亮**：代码面板改为 RichTextBox + 自研 tokenizer（关键字/字符串/注释/数字/内置名/Manim 类名/装饰器/def 名），带行号、滚动同步、Tab 缩进、回车自动缩进、Ctrl+Z/Y 撤销重做。
- **终端多标签（VSCode 式）**：终端停靠在中栏底部（工具栏「终端」/ 菜单「显示终端」/ `Ctrl+\`` 打开，Esc 关闭），不再是盖住画布的浮层。标签栏里每个标签是一个**独立会话**：各自的 `cmd.exe` / `powershell.exe`、工作目录、命令历史、输出缓冲和运行状态。切标签只是换显示对象，不会重启进程、不会清屏，后台标签里正在跑的命令会继续跑并只写自己的缓冲；`+` 新建标签（可选 CMD / PowerShell，沿用当前目录），标签上的 `✕` 只关自己，运行中的标签标题带 `●`。

## 已还原的界面

- 菜单栏：File / Edit / Insert / Slide / Format / Arrange / View / Play / Help（含快捷键提示）
- 工具栏：新建幻灯片/构建、展开、表格/文本/公式/形状/BioCartoon/图片、主题下拉、Python Plot、缩放、暗色、终端、Play、Share
- Home 格式栏：字体/字号、B/I/U/S、高亮/颜色、列表、对齐、网格/吸附/标尺、Script/Feedback、查找框
- 左侧 Navigator：幻灯片缩略图 + build 列表，选中高亮、隐藏/错误标记
- 中间 Canvas：16:9 幻灯片，文本/公式/形状/图片/表格对象，可点选、拖动，标尺、网格、缩放、提示条、忙碌胶囊
- 右侧 Inspector：对象类型、内容、外观、格式开关、位置尺寸、源码行号；无选中时显示提示
- 右下 Source：带语法着色的 Python 源码，选中对象的行高亮
- 底部抽屉：Slide Notes / Build Notes / Alt Text / Terminal（内置模拟的 AI agent 会话）
- 状态栏：deck 信息、许可证与版本

## 交互说明（原型行为）

- 点击 Navigator 缩略图切换幻灯片；点击 build 行切换构建
- 点击画布对象选中，拖动移动（Shift 吸附 10px），方向键微调（Shift 大步），Delete 删除，Esc 取消选择
- 工具栏/菜单/格式栏按钮均会触发对应的视觉反馈（Toast）或状态变化
- 主题下拉切换 deck 主题（背景/文字配色），暗色按钮切换应用明暗（基于原版精确配色）
- 缩放按钮改变画布比例；网格/标尺/吸附可切换

## 目录结构

```
ManimbleWin/
├── ManimbleWin.sln
├── LICENSE                    BSD 2-Clause（含原项目版权声明）
├── build.ps1 / build.sh       一键编译（自动定位带 SDK 的 dotnet）
├── run.ps1 / run.sh           一键运行
├── publish.ps1 / publish.sh   一键发布自包含 exe 到 dist/
├── installer.iss             Inno Setup 安装包脚本 → Output/
├── .github/workflows/build.yml  CI 构建 + 标签触发 Release
├── src/ManimbleWin/
│   ├── Models/        数据模型（CanvasObject / Build / Slide / DeckTheme）
│   ├── ViewModels/    MainViewModel（全部交互逻辑）
│   ├── Views/         Navigator / Canvas / Inspector / Source / Drawer / Terminal
│   ├── Helpers/       MVVM 基础、转换器、主题管理、模板选择器
│   ├── Resources/     画刷 / 样式 / 图标几何
│   ├── MainWindow.xaml(.cs)
│   └── App.xaml(.cs)
├── engine/
│   ├── manimble/       原版 Python 引擎（场景渲染 / LaTeX / 导出 / JSON-lines RPC）
│   └── requirements.txt
└── samples/hello/      示例 deck（deck/slides.py、deck/common.py）
```
（dist/ 与 Output/ 为构建产物，已在 .gitignore 中排除）

## 未来对接真实引擎（不在本原型范围）

原版 `apps/windows/README.md` 已给出路线：Windows 前端作为共享 Python 引擎的第二个客户端，通过 JSON-lines 协议（原仓库的 `docs/developers/protocol.md`）通信。本原型中 `MainViewModel.ExecuteMenu()` 的每个 action 均注释了对应的引擎 `cmd`（open/state/scene/edit/insert/slide/notes/undo/redo/present/export…），可作为对接锚点。工作副本管理需实现“打开副本 → 编辑 → 写回原始 deck”。

## 配色来源

原版 `apps/macos/Sources/Manimble/Theme.swift` 的精确 sRGB 值：

- 强调色 `#FF6C0C`，辅助蓝 `#0C8CFF`
- 画布明暗 `#F6F6F6→#EAEAEA` / `#262626→#141414`
- 代码窗格 `#FCFCFC` / `#1B1B1B`，栏 `#F1F1F1` / `#222222`，侧栏 `#EDEDED` / `#2B2B2B`

Deck 主题取自 `engine/manimble/themes/*.py`。
