# Manimble Windows 版 — 产品需求文档（简单 PRD）

> 文档类型：简单 PRD（界面交互原型）｜撰写人：产品经理 许清楚（Xu）｜语言：中文

---

## 1. 项目信息

| 项 | 内容 |
|---|---|
| Language | 中文 |
| Programming Language | C# + XAML，Windows WPF 桌面应用，采用 MVVM 架构模式 |
| Project Name | `manimble_win` |
| 运行/交付环境 | Windows 桌面。当前机器无 .NET SDK、仅有 .NET Framework 4.0 运行时；因此交付物为**完整可编译的 WPF 项目源码**，编译与运行在装有 Visual Studio 的环境中完成，本阶段不做本机编译验证。 |
| 项目性质 | **UI 交互原型**：高度还原界面外观与点击交互用于演示，**不接真实 Manim 渲染，不做源码写回，不安装 manim / TinyTeX**。 |

**原始需求复述（逐字）**：用户给出 GitHub 项目 https://github.com/pachterlab/Manimble（一个 macOS 桌面应用），说"我想复刻一个 windows 桌面应用"。经澄清确认：复刻范围为 UI 交互原型，用于演示；技术栈为 Windows WPF（C# + XAML，MVVM）；原项目依赖 manim + TinyTeX 属真实引擎依赖，原型无需安装。

---

## 2. 产品定义

### 2.1 产品背景与定位

Manimble 是一个"用 Manim 制作的演示文稿的幻灯片编辑器"。核心理念：每张幻灯片底层是一段简短可读的 Manim Python 程序；用户既能像 PowerPoint 一样在画布上用鼠标编辑（移动/缩放/重打文本、公式、图片、形状、表格），又能在内置代码窗格改 Python，还能在内置终端跑 AI 编程代理（Claude Code / Codex / Gemini CLI）让代理改代码——三者同时作用于同一份 deck。此外支持真实 Manim 动画演示、Presenter View、导入导出 PPT/Keynote/Beamer/PDF/MP4。

本项目为上述产品的 **Windows 版交互原型**：所有"能力"均表现为可点击的界面与模拟反馈，不真正执行 Python/Manim、不写回源码。

### 2.2 Product Goals（3 个正交目标）

1. **高保真外观还原**：在 Windows/WPF 上高度还原 Manimble macOS 版的主窗口布局、精确配色、工具栏/格式栏/菜单/图标外观，使演示者一眼即可识别为 Manimble。
2. **可演示的核心交互**：对关键界面元素提供真实的点击与视觉反馈——Navigator 切换、Canvas 对象选中/拖动/缩放、Inspector 属性展示、Source 代码联动、抽屉标签切换、菜单弹出、主题与明暗切换、缩放、幻灯片新建/复制/删除、终端面板——足以独立走通一轮典型演示流程。
3. **清晰的原型边界**：明确不执行真实 Python/Manim 渲染、不写回源码、不依赖 manim/TinyTeX；播放、导出、AI 终端等均以模拟反馈呈现，确保原型可独立编译运行、行为可控可预期。

### 2.3 User Stories

1. **As a** 产品演示者，**I want** 在 Windows 上打开一个高度还原 Manimble 界面的应用，**so that** 我可以脱离 macOS 向观众展示产品的外观与交互。
2. **As a** 产品演示者，**I want** 点击左侧 Navigator 切换幻灯片与 build，**so that** 我可以模拟浏览一份演示文稿的结构与层级。
3. **As a** 产品演示者，**I want** 在画布上选中、拖动、缩放对象并在 Inspector 看到属性变化，**so that** 我可以演示"鼠标直接编辑 Manim 对象"的核心体验。
4. **As a** 产品演示者，**I want** 切换 deck 主题、明暗模式和画布缩放，**so that** 我可以展示产品的视觉定制与查看能力。
5. **As a** 产品演示者，**I want** 通过菜单与工具栏触发新建/复制/删除幻灯片、打开终端等操作，**so that** 我可以完整走通一轮典型操作流程而不出错。

---

## 3. 技术规范

### 3.1 Requirements Pool（优先级）

约定：**P0 = Must have（必须）**，**P1 = Should have（应该）**，**P2 = Nice to have（可选）**。本原型的需求聚焦于"界面还原 + 点击/交互反馈"。

#### P0 — 必须（原型核心，验收基线）

| # | 需求 | 验收要点 |
|---|---|---|
| P0-1 | 主窗口三栏布局 | Toolbar + Home bar + Navigator（左）/ Canvas（中）/ Inspector + Source（右）/ Drawer（底部）；面板间分隔条可拖动调整大小，应用记住尺寸。 |
| P0-2 | 工具栏外观 | 按 §3.2.3 从左到右渲染全部按钮，含图标、下拉箭头与 Theme 色块下拉菜单；仅外观与点击反馈，不做真实功能。 |
| P0-3 | Home bar 格式栏外观 | 按 §3.2.4 渲染字体/字号/粗斜体下划线/删除线/上下标/高亮/字体颜色/清除格式/列表缩进/对齐/行距/分栏、网格吸附参考线、Script 与 View Feedback、右端 Find 框。 |
| P0-4 | 完整菜单栏 | 按 §3.2.5 渲染 File/Edit/Insert/Slide/Format/Arrange/View/Play/Help；Windows 风格，⌘→Ctrl；点击弹出下拉菜单。 |
| P0-5 | Navigator 切换 | 列出幻灯片（缩略图+编号）与其 build（更小缩略图）；当前项高亮；点击切换；出错幻灯片标记错误；隐藏幻灯片变暗。 |
| P0-6 | Canvas 模拟编辑 | 显示当前 build；点击选中对象（显示选中框/控制点）、拖动移动、拖动控制点缩放、双击进入文本编辑态、右键上下文菜单；标尺显示；缩放（放大/缩小/适应窗口）与放大后滚动平移。 |
| P0-7 | Inspector 属性展示 | 选中时显示类型、生成它的代码行、文本/公式、外观、位置与尺寸；无选中时显示幻灯片名与提示文案。 |
| P0-8 | Source 代码窗格 | 显示当前幻灯片 Python 代码；选中对象时高亮对应代码行；最近编辑改动行高亮；点击代码进入编辑态（仅视觉，不解析执行）。 |
| P0-9 | 底部抽屉 Drawer | 标签页 Slide Notes / Build Notes / Alt Text / Terminal 1/2…，点击切换内容；Terminal 支持 `+` 新建标签。 |
| P0-10 | 主题与明暗切换 | Theme 下拉含 7 套 deck 主题（§3.2.6），点击改变画布配色；Dark Mode 仅切换界面明暗显示效果，不改数据。 |
| P0-11 | 幻灯片增删改模拟 | 新建（含布局下拉）、复制、删除幻灯片的模拟行为；Navigator 与画布同步更新。 |
| P0-12 | 终端视觉呈现 | 终端面板呈现命令行外观（提示符、历史文字、光标），模拟呈现不执行。 |
| P0-13 | 精确配色 | 全局采用 §3.2.2 给定色值（accent `#FF6C0C`、warm `#0C8CFF`、各栏/画布/代码窗格明暗背景等）。 |

#### P1 — 应该

| # | 需求 |
|---|---|
| P1-1 | 演示播放模拟：Play from Beginning / from Current、Presenter View、动画预览——仅界面与转场视觉模拟，非真实渲染。 |
| P1-2 | 导出对话框：PDF / PPT / Keynote / Beamer / MP4 的界面与点击反馈（不产出真实文件）。 |
| P1-3 | 查找与替换：Find 框、Find…(Ctrl+F)、Find and Replace…(Ctrl+Alt+F) 面板。 |
| P1-4 | AI 终端文字模拟：Claude Code / Codex / Gemini CLI 的伪对话与"代码改动"文字输出。 |
| P1-5 | 拖动重排：Navigator 中拖动重排幻灯片/build；右键菜单完整项（复制/隐藏/删除/展开全部/导出幻灯片为影片）。 |
| P1-6 | 格式栏真实样式作用：粗体/斜体/下划线/对齐等对选中文本产生视觉变化。 |
| P1-7 | 全屏演示视图与 `$`/动画开关的视觉反馈。 |

#### P2 — 可选

| # | 需求 |
|---|---|
| P2-1 | BioCartoon 图标搜索面板的模拟结果网格。 |
| P2-2 | Python Plot 图表占位渲染。 |
| P2-3 | Shape 绘图工具/画笔的笔迹模拟。 |
| P2-4 | Import 对话框、Package Project for Sharing 对话框。 |
| P2-5 | Show Unused Slides、Invert Colors 等次要 View 项反馈。 |
| P2-6 | 面板尺寸/明暗偏好的本地持久化（若 P0-1 未涵盖）。 |

### 3.2 UI Design Draft

#### 3.2.1 主窗口布局

```
┌──────────────────────────────────────────────────────────────┐
│ Toolbar（工具栏：新建/插入/主题/缩放/明暗/终端/分享…）          │
├──────────────────────────────────────────────────────────────┤
│ Home bar（格式栏：字体 字号 B I U S 上标 下标 … 对齐 行距 …）    │  右端 Find 查找框
├──────────────┬──────────────────────────────┬────────────────┤
│ Navigator    │  Canvas（幻灯片画布，可显示标尺）│ Inspector      │
│ 幻灯片列表    │                              │ 属性            │
│ + build 列表  │                              ├────────────────┤
│              │                              │ Source pane    │
│              ├──────────────────────────────┤ Python 代码     │
│              │ Drawer 底部抽屉：              │                │
│              │ 备注·替代文本·终端            │                │
└──────────────┴──────────────────────────────┴────────────────┘
```

- 各面板间分隔条可拖动调整大小，应用需记住尺寸。
- View 菜单可隐藏/显示 Navigator、Inspector 与 Source，给画布更大空间。
- 画布背景：浅色 `#F6F6F6 → #EAEAEA` 渐变；深色 `#262626 → #141414` 渐变。

#### 3.2.2 精确配色（来自源码 Theme.swift）

| 角色 | 浅色 | 深色 |
|---|---|---|
| 主强调色 accent | `#FF6C0C`（橙） | 同（accentSoft = 同色 18% 透明） |
| 辅助色 warm | `#0C8CFF`（蓝，克制使用） | 同 |
| 画布背景 | `rgb(0.965,0.965,0.965)`≈`#F6F6F6` → `rgb(0.918,…)`≈`#EAEAEA` 渐变 | `rgb(0.15,…)`≈`#262626` → `rgb(0.08,…)`≈`#141414` |
| 代码窗格背景 | `#FCFCFC` | `#1B1B1B` |
| 栏背景 | `#F1F1F1` | `#222222` |
| 侧栏背景 | `#EDEDED`（白 93%） | `#2B2B2B`（白 17%） |
| 文字 ink | 黑（带透明度） | 白（带透明度） |

应用 logo 与海报为**黑底橙字 "Manimble"**。

#### 3.2.3 工具栏按钮（从左到右）

New Slide（带下拉箭头选布局）、New Build、Expand All（展开/折叠全部 build）、Table、Text、Formula、Shape（含绘图工具与画笔）、BioCartoon（图标搜索）、Image、Theme（下拉色块菜单）、Python Plot、Zoom、Dark Mode（仅显示效果）、Terminal（显示/隐藏）、Share（导出 PDF/PPT/Keynote/Beamer/MP4）。

#### 3.2.4 Home bar 格式栏

字体、字号、粗体、斜体、下划线、删除线、上标、下标、高亮、字体颜色、清除格式、列表与缩进、对齐、行距、分栏；网格、吸附、参考线控制；Script 与 View Feedback 按钮；右端 Find 查找框。

#### 3.2.5 菜单栏（Windows 风格，⌘→Ctrl）

- **File**：New Deck…(Ctrl+N)、Open Deck or Project…(Ctrl+O)、Save to Original(Ctrl+S)、Close Window(Ctrl+W)、Import、Export、Package Project for Sharing、Quit(Ctrl+Q)
- **Edit**：Undo(Ctrl+Z)、Redo(Ctrl+Shift+Z)、Cut/Copy/Paste(Ctrl+X/C/V)、Select All(Ctrl+A)、Find…(Ctrl+F)、Find and Replace…(Ctrl+Alt+F)
- **Insert**：Text(Ctrl+Shift+T)、Formula(Ctrl+Shift+E)、Image(Ctrl+Shift+I)、Draw > Pen(Ctrl+Shift+P)、Table、Shape、BioCartoon、Python Plot
- **Slide**：New Slide(Ctrl+Shift+N)、Duplicate(Ctrl+D)、Delete、Hide/Unhide(Ctrl+Shift+H)、Move Up/Down(Ctrl+Alt+↑/↓)
- **Format**：Bold(Ctrl+B)、Italic(Ctrl+I)、Underline(Ctrl+U)、Superscript、Subscript、Bigger/Smaller、Hyperlink(Ctrl+K)、Bulleted/Numbered List、Align Left/Center/Right(Ctrl+L/E/R)、Theme、Apply Dark Mode
- **Arrange**：Bring to Front/Forward、Send Backward/to Back、Group(Ctrl+G)/Ungroup(Ctrl+Shift+G)、Apply Move to All Builds
- **View**：Show Objects、Grid Lines、Ruler、Snap to Objects、Speaker Notes、Dark Mode、Invert Colors、Zoom In/Out、Fit in Window、Show Terminal、Hide/Show Navigator、Hide/Show Inspector and Source、Show Unused Slides
- **Play**：Play from Beginning、Play from Current Slide、Presenter View、Preview Current Slide's Animations、Show Animations on/off
- **Help**：文档、关于

#### 3.2.6 Deck 主题色板（Theme 下拉菜单，精确值取自 `engine/manimble/themes/*.py`）

| id | 名称 | 背景 Background | 文字色 Text | 静音色 Muted | 强调色 Accent | 字体 |
|---|---|---|---|---|---|---|
| midnight | Midnight | `#000000` | `#FFFFFF` | `#BBBBBB` | `#7C5CFF` | Helvetica Neue |
| paper | Paper | `#FFFFFF` | `#1D1D1F` | `#6E6E73` | `#0A66C2` | Helvetica Neue |
| chalkboard | Chalkboard | `#23352B` | `#F4F1E8` | `#B9C2B0` | `#F2C14E` | Chalkboard SE |
| ember | Ember | `#17140F` | `#FBF6F0` | `#B8AEA3` | `#FF6C0C` | Helvetica Neue |
| slate | Slate | `#1E2430` | `#E8ECF2` | `#9AA5B5` | `#4FD1C5` | Avenir Next |
| parchment | Parchment | `#F5EFE3` | `#3B2F24` | `#8A7A66` | `#A23B2A` | Palatino |
| terminal | Terminal | `#0D1117` | `#E6EDF3` | `#8B949E` | `#3FB950` | Menlo |

#### 3.2.7 交互行为说明

- **Navigator**：点击幻灯片/build 切换；当前高亮；拖动重排（P1）；右键菜单（新建/build/复制/隐藏/删除/展开全部/导出为影片，P1）；出错标记；隐藏变暗。
- **Canvas**：点击选中（控制点）、双击编辑文本/公式、拖动移动、Shift+拖动限制水平/垂直或画正方/正圆、Alt+拖动不吸附、右键菜单；缩放与平移。
- **Inspector**：随选中对象联动；Script、反馈列表、BioCartoon 搜索在 Inspector 位置打开。
- **Source pane**：选中对象行高亮；最近编辑行高亮；点击可编辑（仅视觉）。
- **Drawer**：标签切换；Terminal `+` 新开标签。

#### 3.2.8 画布快捷键（Windows 化：⌘→Ctrl, ⌥→Alt, ⇧→Shift, ↩→Enter, ⌫→Delete, ⌃→Ctrl）

方向键微调；Shift+方向键大步；Enter 编辑选中对象文本；Delete 删除；Esc 取消选择/收起绘图工具；Shift+点击加选；Shift+拖动限制方向/等比；Alt+拖动不吸附；幻灯片导航 Ctrl+↑/↓；build 导航 Ctrl+[/]。

### 3.3 Open Questions / 待确认问题

1. **示例 deck 内容**：使用官方示例，还是自拟一组含占位文本/公式/图片/表格的示例幻灯片？建议自拟，便于覆盖各类对象的交互演示。
2. **面板尺寸持久化**：P0-1 要求"记住尺寸"，是仅会话内记忆，还是落地到配置文件（同时记忆明暗偏好）？建议会话内记忆即可，持久化列为 P2。
3. **Source 行级联动**：P0-8 的"选中对象高亮对应代码行"是否需在原型阶段做到精确行号联动？建议做到模拟联动（预置映射）。
4. **终端内容**：终端面板是否预置一段演示用伪 AI 对话脚本（含"代码改动"文字），还是空白提示符？建议预置简短脚本以便演示。
5. **高 DPI 适配**：Windows 高 DPI 缩放适配是否纳入本原型范围？建议按系统 DPI 自适应。
6. **交付形式确认**：确认本阶段仅交付完整可编译 WPF 源码、不在本机编译（本机无 .NET SDK）；编译与运行验收在装有 Visual Studio 的环境完成。
