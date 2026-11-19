# ManimbleWin

Manimble 的 **Windows（WPF）UI 交互原型**：高度还原 macOS 版 Manimble 的界面与交互，用于演示；不接真实 Manim 引擎、不执行 Python、不写回源码。

基于原项目 [pachterlab/Manimble](https://github.com/pachterlab/Manimble)（BSD 2-Clause）的界面与文档复刻。原版是 Swift/AppKit/Metal 前端 + 共享 Python 引擎（JSON-lines 协议）；本原型用 C#/WPF 重建前端外观与交互，数据与行为为本地模拟。

## 快速开始

**环境**：Windows 10/11 + .NET 8 SDK（或 Visual Studio 2022，勾选 “.NET 桌面开发”）。

```bash
cd ManimbleWin
dotnet build ManimbleWin.sln -c Debug
dotnet run --project src/ManimbleWin/ManimbleWin.csproj
```

或在 Visual Studio 中打开 `ManimbleWin.sln`，按 F5。

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
├── src/ManimbleWin/
│   ├── Models/        数据模型（CanvasObject / Build / Slide / DeckTheme）
│   ├── ViewModels/    MainViewModel（全部交互逻辑）
│   ├── Views/         Navigator / Canvas / Inspector / Source / Drawer / Terminal
│   ├── Helpers/       MVVM 基础、转换器、主题管理、模板选择器
│   ├── Resources/     画刷 / 样式 / 图标几何
│   ├── MainWindow.xaml(.cs)
│   └── App.xaml(.cs)
├── deliverables/software-company/manimble-win/prd.md   产品需求文档
└── docs/              预留文档目录
```

## 未来对接真实引擎（不在本原型范围）

原版 `apps/windows/README.md` 已给出路线：Windows 前端作为共享 Python 引擎的第二个客户端，通过 JSON-lines 协议（`docs/developers/protocol.md`）通信。本原型中 `MainViewModel.ExecuteMenu()` 的每个 action 均注释了对应的引擎 `cmd`（open/state/scene/edit/insert/slide/notes/undo/redo/present/export…），可作为对接锚点。工作副本管理需实现“打开副本 → 编辑 → 写回原始 deck”。

## 配色来源

原版 `apps/macos/Sources/Manimble/Theme.swift` 的精确 sRGB 值：

- 强调色 `#FF6C0C`，辅助蓝 `#0C8CFF`
- 画布明暗 `#F6F6F6→#EAEAEA` / `#262626→#141414`
- 代码窗格 `#FCFCFC` / `#1B1B1B`，栏 `#F1F1F1` / `#222222`，侧栏 `#EDEDED` / `#2B2B2B`

Deck 主题取自 `engine/manimble/themes/*.py`。
