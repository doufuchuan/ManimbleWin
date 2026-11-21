using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ManimbleWin.Helpers;
using ManimbleWin.Models;
using ManimbleWin.Services;

namespace ManimbleWin.ViewModels
{
    /// <summary>
    /// 主视图模型的「格式通道」：格式栏命令、可用性判定、按钮灯、取色弹层状态。
    /// <para>单独一个 partial 文件：MainViewModel.cs 已经近三千行，格式逻辑本身约四百行，
    /// 混在一起改一处要滚很久，也更容易和别的功能撞车。</para>
    /// <para>三条铁律（与架构 §9 一致）：① 一次操作 = 一次 <see cref="FormatBatchAsync"/>；
    /// ② 提交前不改本地任何字段，灯只由引擎回填决定；③ 成功不弹 toast，失败弹引擎 error 原文。</para>
    /// </summary>
    public partial class MainViewModel
    {
        #region 命令字段与初始化

        public ICommand FormatToggleCommand { get; private set; }
        public ICommand FormatHighlightCommand { get; private set; }
        public ICommand FormatColorCommand { get; private set; }
        public ICommand FormatLineSpacingCommand { get; private set; }
        public ICommand FormatColumnsCommand { get; private set; }
        public ICommand FormatFontFamilyCommand { get; private set; }
        public ICommand FormatFontSizeCommand { get; private set; }
        public ICommand FormatFontStepCommand { get; private set; }
        public ICommand FormatPickerToggleCommand { get; private set; }

        /// <summary>弹层里点了某个色块（参数是 #RRGGBB；空参数 = 移除高亮）。</summary>
        public ICommand FormatPickedColorCommand { get; private set; }
        public ICommand FormatCustomColorCommand { get; private set; }
        public ICommand FormatCustomRowToggleCommand { get; private set; }

        /// <summary>由主构造函数调用：注册格式相关命令。</summary>
        private void InitFormatCommands()
        {
            FormatToggleCommand = new RelayCommand(p => RunFormatToggle(p?.ToString() ?? ""));
            FormatHighlightCommand = new RelayCommand(p => _ = ApplyHighlightAsync(p?.ToString()));
            FormatColorCommand = new RelayCommand(p => _ = ApplyColorAsync(p?.ToString()));
            FormatLineSpacingCommand = new RelayCommand(p => _ = ApplyLineSpacingAsync(p?.ToString()));
            FormatColumnsCommand = new RelayCommand(p => _ = ApplyColumnsAsync(p?.ToString()));
            FormatFontFamilyCommand = new RelayCommand(p => _ = ApplyFontFamilyAsync(p?.ToString()));
            FormatFontSizeCommand = new RelayCommand(p => _ = ApplyFontSizeAsync(p?.ToString()));
            FormatFontStepCommand = new RelayCommand(p => _ = StepFontSizeAsync(p?.ToString()));
            FormatPickerToggleCommand = new RelayCommand(p => ToggleColorPicker(p?.ToString() ?? ""));
            FormatPickedColorCommand = new RelayCommand(p => PickColor(p?.ToString()));
            FormatCustomColorCommand = new RelayCommand(_ => ApplyCustomColor());
            FormatCustomRowToggleCommand = new RelayCommand(_ =>
            {
                IsCustomColorRowOpen = !IsCustomColorRowOpen;
                if (IsCustomColorRowOpen) CustomColorError = "";
            });
        }

        #endregion

        #region 按钮灯（全部只读，来自引擎回填的 SelectedObject）

        private CanvasObject FmtTarget => SelectedObject;

        public bool FormatBold => FmtTarget?.Bold ?? false;
        public bool FormatItalic => FmtTarget?.Italic ?? false;
        public bool FormatUnderline => FmtTarget?.Underline ?? false;
        public bool FormatStrike => FmtTarget?.Strike ?? false;
        public bool FormatBulleted => TextTransform.IsBulleted(FmtTarget?.Value);
        public bool FormatNumbered => TextTransform.IsNumbered(FmtTarget?.Value);
        public bool FormatSuperscript => TextTransform.IsScripted(FmtTarget?.Value, true);
        public bool FormatSubscript => TextTransform.IsScripted(FmtTarget?.Value, false);

        /// <summary>
        /// 高亮的当前色（供弹层勾选比较）。<c>"on"</c>（有底块无颜色字面量）不返回颜色：
        /// 它匹配不上任何预设色块，但仍要让 H 灯亮（那是 <see cref="CanvasObject.HasHighlight"/> 的事）。
        /// </summary>
        public string FormatHighlightColor
        {
            get
            {
                string c = FmtTarget?.HighlightColor;
                return string.Equals(c, "on", StringComparison.Ordinal) ? null : c;
            }
        }

        /// <summary>字色（引擎回填的 color，已归一化为大写 #RRGGBB 或 null）。</summary>
        public string FormatTextColor => FmtTarget?.Color;

        public string FormatParagraphAlign => FmtTarget?.ParagraphAlign;

        public int FormatColumns => FmtTarget?.Columns ?? 1;

        public double? FormatLineSpacing => FmtTarget?.LineSpacing;

        /// <summary>字号输入框显示的文本（整数不显示小数点）。</summary>
        public string FormatFontSizeText => FmtTarget == null ? "" : FormatPalette.FormatSpacing(FmtTarget.FontSize);

        public string FormatFontFamily => FmtTarget?.Font;

        /// <summary>行距下拉的当前项文本；源码里没有 line_spacing 时显示「默认」。</summary>
        public string FormatLineSpacingText
            => FormatLineSpacing.HasValue ? FormatPalette.FormatSpacing(FormatLineSpacing.Value) : LineSpacingDefaultLabel;

        /// <summary>分栏下拉的当前项文本。</summary>
        public string FormatColumnsText => FormatColumns + " 栏";

        public const string LineSpacingDefaultLabel = "默认";

        /// <summary>行距档位。第一项是「默认」= 移除 line_spacing（D2 引擎补丁），不是 1.0。</summary>
        public IReadOnlyList<string> LineSpacingItems { get; } = new[]
        {
            LineSpacingDefaultLabel,
            "1", "1.15", "1.5", "2", "2.5", "3"
        };

        public IReadOnlyList<string> ColumnItems { get; } = new[] { "1 栏", "2 栏", "3 栏", "4 栏" };
        public IReadOnlyList<string> FontFamilyItems { get; } = new[] { "DejaVu Sans", "Segoe UI", "Georgia", "Cambria Math", "Consolas" };
        public IReadOnlyList<string> FontSizeItems { get; } = new[] { "18", "24", "28", "40", "56", "72" };

        /// <summary>取色弹层的 8 个预设色（顺序即 UI 顺序，与 macOS 原版一致）。</summary>
        public IReadOnlyList<PaletteSwatch> PickerPalette => FormatPalette.Presets;

        #endregion

        #region 可用性判定（唯一判定点）

        /// <summary>
        /// 唯一可用性判定。12 个控件的 IsEnabled 只绑它的三个投影，规则改一处即可。
        /// 第一条就是「引擎已连接」：未连引擎时不准有任何本地假效果（改了源码才算改了）。
        /// </summary>
        private bool CanFormat(FormatScope scope)
        {
            if (!EngineConnected) return false;
            if (CurrentSlide == null) return false;
            var targets = SelectedObjects;
            if (targets.Count == 0) return false;
            foreach (var o in targets)
            {
                if (o == null) return false;
                switch (scope)
                {
                    case FormatScope.TextOnly:
                        // 列表/缩进/上下标/行距/分栏/段落对齐/字体/字号：只有 Text
                        if (o.Kind != ObjectKind.Text) return false;
                        break;
                    case FormatScope.Weight:
                        // 字重与倾斜：只有 Text。Tex 走 style 的 weight/slant 必然渲染失败
                        //（MathTex 没有这两个参数），点了只会弹一段引擎报错，所以这里就不放行。
                        if (o.Kind != ObjectKind.Text) return false;
                        break;
                    default:
                        // 下划线/删除线/高亮/字色/清除格式：Text 与 Tex 都支持
                        if (o.Kind != ObjectKind.Text && o.Kind != ObjectKind.Tex) return false;
                        break;
                }
            }
            return true;
        }

        public bool CanFormatCharacter => CanFormat(FormatScope.Character);
        public bool CanFormatWeight => CanFormat(FormatScope.Weight);
        public bool CanFormatTextOnly => CanFormat(FormatScope.TextOnly);

        public string FormatHintCharacter => ExplainFormat(FormatScope.Character);
        public string FormatHintWeight => ExplainFormat(FormatScope.Weight);
        public string FormatHintTextOnly => ExplainFormat(FormatScope.TextOnly);

        /// <summary>当前为什么不可用（空字符串 = 可用，此时 ToolTip 只显示基础提示）。</summary>
        private string ExplainFormat(FormatScope scope)
        {
            if (!EngineConnected) return "连接引擎后可用";
            if (SelectedObjects.Count == 0) return "选中文本后可格式化";

            bool hasTex = SelectedObjects.Any(o => o != null && o.Kind == ObjectKind.Tex);
            bool hasOther = SelectedObjects.Any(o => o != null && o.Kind != ObjectKind.Text && o.Kind != ObjectKind.Tex);

            if (scope == FormatScope.Weight)
            {
                // 字重/倾斜在 Tex 上是「引擎明确报错」而不是「静默无效」，所以单独把原因说清楚（D1）
                if (hasTex) return "公式不支持字重与斜体";
                if (hasOther) return "该格式仅适用于文字";
            }
            else if (scope == FormatScope.TextOnly)
            {
                if (hasTex) return "该格式仅适用于文本（公式不支持）";
                if (hasOther) return "该格式仅适用于文字";
            }
            else if (hasOther)
            {
                return "该格式仅适用于文字";
            }
            return "";
        }

        /// <summary>本次可作用的对象：判定不过就是空列表（命令据此静默返回，不弹提示）。</summary>
        private IReadOnlyList<CanvasObject> FormatTargets(FormatScope scope)
            => CanFormat(scope) ? SelectedObjects.ToList() : new List<CanvasObject>();

        /// <summary>格式操作落在第几个 build 上（与既有 move/delete 的 stop 取法一致）。</summary>
        private int FormatStop => CurrentSlide?.CurrentBuildIndex ?? 0;

        #endregion

        #region 批量执行

        /// <summary>
        /// 一次格式操作的唯一通道：mark → 逐对象 edit → merge → 刷新。
        /// <para>为什么不先改本地：移动/对齐可以先动坐标再等刷新覆盖；格式若先翻灯，失败时用户会
        /// 看到"亮了又灭"，中间的灯与源码不一致。这里改成纯提交，失败路径天然干净。</para>
        /// <para><paramref name="precheck"/> 返回非空字符串表示该对象被就地拦下（不提交）；只有全部对象
        /// 都被拦下时才弹提示，部分被拦就静默跳过被拦的那些。</para>
        /// </summary>
        private async Task FormatBatchAsync(FormatScope scope,
                                            Func<CanvasObject, FormatRequest> build,
                                            string undoLabel,
                                            Func<CanvasObject, string> precheck = null)
        {
            var targets = FormatTargets(scope);
            if (targets.Count == 0) return;          // 禁用态已经用 hover 解释了原因，不再弹 toast

            var runnable = new List<CanvasObject>(targets.Count);
            string firstBlocked = null;
            foreach (var o in targets)
            {
                string why = precheck?.Invoke(o);
                if (!string.IsNullOrEmpty(why))
                {
                    if (firstBlocked == null) firstBlocked = why;
                    continue;
                }
                runnable.Add(o);
            }
            if (runnable.Count == 0)
            {
                if (!string.IsNullOrEmpty(firstBlocked)) ShowToast(firstBlocked);
                return;
            }

            try
            {
                long? mark = await EngineUndoMarkAsync();
                var errors = new List<string>();
                var usedOids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < runnable.Count; i++)
                {
                    var o = runnable[i];
                    FormatRequest req = build(o);
                    if (req == null) continue;      // 变换后与原文一致：不发请求，避免「没有可见变化」的回滚噪音

                    // 多选时第 2 个起的 oid 可能已经漂了（前一个对象若插入了一行，引擎会重新编号），
                    // 提交前用「loc + kind + 原文」从最新对象表里重新认领一次。
                    // 单个对象（绝大多数操作）不付这个成本。
                    if (i > 0) req.Retarget(await ReclaimOidAsync(o, usedOids));
                    usedOids.Add(o.Oid);

                    var outcome = await _engine.EditOneAsync(CurrentSlide.Name, req);
                    if (!outcome.Ok) errors.Add(outcome.Error);
                }
                // 多对象也要合成一步撤销：否则点一次 B 之后要按三次 Ctrl+Z。
                await EngineUndoMergeAsync(mark, undoLabel);
                // 刷新会重建对象列表并（在内部）恢复选区，灯随之回填成源码的真实状态。
                await RefreshAfterEngineEdit(null);

                if (errors.Count > 0) ShowToast("格式未生效：" + errors[0]);
            }
            catch (Exception ex)
            {
                ShowToast("格式未生效：" + ex.Message);
            }
        }

        /// <summary>
        /// 从引擎最新对象表里重新认领 <paramref name="target"/> 对应的 oid。
        /// <para>先用 loc + kind + 原文匹配（插入一行会让行号变化，所以 loc 只作首选），
        /// 匹配不到再退到 kind + 原文；仍不唯一就返回原 oid —— 猜错会改到别的对象，比失败更糟。</para>
        /// <para>读对象表用的是刚编辑完的页面记录，不会触发重新渲染，所以这一步很便宜。</para>
        /// </summary>
        private async Task<string> ReclaimOidAsync(CanvasObject target, HashSet<string> used)
        {
            try
            {
                var rows = await _engine.ReadObjectTableAsync(CurrentSlide.Name);

                string bestOid = null;
                int bestScore = 0, matches = 0;
                foreach (var row in rows)
                {
                    if (used.Contains(row.Oid)) continue;
                    if (row.Kind != target.Kind) continue;
                    int score = 0;
                    if (string.Equals(row.Loc, target.Loc, StringComparison.Ordinal)) score += 2;
                    if (string.Equals(row.Value, target.Value ?? "", StringComparison.Ordinal)) score += 1;
                    if (score == 0) continue;
                    if (score > bestScore) { bestScore = score; bestOid = row.Oid; matches = 1; }
                    else if (score == bestScore) matches++;
                }
                return matches == 1 && bestOid != null ? bestOid : target.Oid;
            }
            catch (Exception)
            {
                return target.Oid;      // 读表失败就用原 oid：让引擎去报错，比静默改错对象安全
            }
        }

        /// <summary>
        /// text op 的前置条件：这段文字必须能被引擎改写。
        /// 由辅助函数生成的文本（textOk=false）直接改字面量会破坏那处调用，
        /// 提前拦下比等一次 RPC + 回滚好读。
        /// </summary>
        private static string TextEditableBlocker(CanvasObject o)
            => o != null && o.Kind == ObjectKind.Text && !o.IsTextEditable
                ? "该文本由辅助函数生成，无法在此改写文字"
                : null;

        #endregion

        #region 格式动作（FormatToggleCommand 的分发）

        /// <summary>「点一下改一下」的全部格式动作都从这里进，参数见架构 §3.4 的映射表。</summary>
        private void RunFormatToggle(string action)
        {
            switch (action)
            {
                case "Bold": _ = ApplyStyleAsync(true); break;
                case "Italic": _ = ApplyStyleAsync(false); break;
                case "Underline": _ = ApplyDecorateAsync("underline", "下划线"); break;
                case "Strike": _ = ApplyDecorateAsync("strike", "删除线"); break;
                case "BulletedList": _ = ApplyListAsync(true); break;
                case "NumberedList": _ = ApplyListAsync(false); break;
                case "Superscript": _ = ApplyScriptAsync(true); break;
                case "Subscript": _ = ApplyScriptAsync(false); break;
                case "Indent": _ = ApplyIndentAsync(4); break;
                case "Outdent": _ = ApplyIndentAsync(-4); break;
                case "ClearFormat": _ = ApplyClearFormatAsync(); break;
                case "AlignLeftPara": _ = ApplyAlignAsync("left"); break;
                case "AlignCenterPara": _ = ApplyAlignAsync("center"); break;
                case "AlignRightPara": _ = ApplyAlignAsync("right"); break;
            }
        }

        /// <summary>粗体 / 斜体：同一个 <c>style</c> op，只换字段。</summary>
        private Task ApplyStyleAsync(bool bold)
        {
            string label = bold ? "粗体" : "斜体";
            return FormatBatchAsync(FormatScope.Weight, o =>
            {
                // 目标值按当前状态取反；NORMAL 是引擎认的"关"值。
                bool on = bold ? o.Bold : o.Italic;
                string value = on ? "NORMAL" : (bold ? "BOLD" : "ITALIC");
                return bold
                    ? FormatRequest.Style(o.Oid, FormatStop, weight: value)
                    : FormatRequest.Style(o.Oid, FormatStop, slant: value);
            }, label);
        }

        /// <summary>下划线 / 删除线：<c>decorate</c>，按当前状态取反。</summary>
        private Task ApplyDecorateAsync(string kind, string label)
        {
            return FormatBatchAsync(FormatScope.Character, o =>
            {
                bool on = kind == "underline" ? o.Underline : o.Strike;
                return FormatRequest.Decorate(o.Oid, FormatStop, kind, !on);
            }, label);
        }

        /// <summary>项目符号 / 编号：客户端文本变换 + <c>text</c> op。</summary>
        private Task ApplyListAsync(bool bullets)
        {
            string label = bullets ? "项目符号" : "编号";
            return FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                string before = o.Value ?? "";
                string after = bullets ? TextTransform.ToggleBullets(before) : TextTransform.ToggleNumbers(before);
                // 变换等于不改（例如全空行）：不发请求。
                return string.Equals(before, after, StringComparison.Ordinal)
                    ? null
                    : FormatRequest.Text(o.Oid, FormatStop, after);
            }, label, TextEditableBlocker);
        }

        /// <summary>上标 / 下标：Unicode 字符替换 + <c>text</c> op。无可转字符时就地提示，不发请求。</summary>
        private Task ApplyScriptAsync(bool superscript)
        {
            string label = superscript ? "上标" : "下标";
            string noForm = superscript ? "这些字符没有上标形式" : "这些字符没有下标形式";
            return FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                // precheck 已经保证至少有一个字符可转；这里再取一次结果。
                if (!TextTransform.Script(o.Value, superscript, out string converted)) return null;
                return string.Equals(converted, o.Value, StringComparison.Ordinal)
                    ? null
                    : FormatRequest.Text(o.Oid, FormatStop, converted);
            }, label, o =>
            {
                string blocked = TextEditableBlocker(o);
                if (blocked != null) return blocked;
                return TextTransform.Script(o.Value, superscript, out _) ? null : noForm;
            });
        }

        /// <summary>缩进 / 反缩进：客户端每行 ±4 空格。缩进到顶/剥无可剥时不发请求。</summary>
        private Task ApplyIndentAsync(int delta)
        {
            string label = delta > 0 ? "缩进" : "反缩进";
            return FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                string before = o.Value ?? "";
                string after = TextTransform.IndentAll(before, delta);
                return string.Equals(before, after, StringComparison.Ordinal)
                    ? null
                    : FormatRequest.Text(o.Oid, FormatStop, after);
            }, label, TextEditableBlocker);
        }

        /// <summary>清除格式：一个 op 去掉字重/倾斜/字色/逐段样式/行距/高亮/下划线/删除线，算一步撤销。</summary>
        private Task ApplyClearFormatAsync()
            => FormatBatchAsync(FormatScope.Character, o => FormatRequest.ClearFormat(o.Oid, FormatStop), "清除格式");

        /// <summary>
        /// 段落对齐。<paramref name="value"/> 已经是当前值时不发请求（点了同一个键不该产生一步空撤销）；
        /// 单行且没有文本框的对象就地提示 —— 引擎也会拒绝同样的事，但没必要为它跑一次 RPC + 回滚。
        /// </summary>
        private Task ApplyAlignAsync(string value)
        {
            return FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                if (string.Equals(o.ParagraphAlign, value, StringComparison.OrdinalIgnoreCase)) return null;
                return FormatRequest.Align(o.Oid, FormatStop, value);
            }, "文本对齐", o =>
            {
                bool multiline = (o.Value ?? "").IndexOf('\n') >= 0;
                return multiline || o.HasTextBox ? null : "对齐需要多行文本或文本框";
            });
        }

        #endregion

        #region 颜色、行距、分栏、字体、字号

        /// <summary>高亮。参数为空表示「无高亮」：同一个 op 不带 color，引擎就会删掉底块。</summary>
        private async Task ApplyHighlightAsync(string hex)
        {
            IsHighlightPickerOpen = false;
            string norm = null;
            if (!string.IsNullOrWhiteSpace(hex))
            {
                norm = FormatPalette.NormalizeHex(hex, out bool ok);
                if (!ok) return;                       // 非法色值：静默不提交
            }
            await FormatBatchAsync(FormatScope.Character,
                o => FormatRequest.Highlight(o.Oid, FormatStop, norm), "高亮");
        }

        /// <summary>字体颜色。</summary>
        private async Task ApplyColorAsync(string hex)
        {
            IsColorPickerOpen = false;
            string norm = FormatPalette.NormalizeHex(hex, out bool ok);
            if (!ok) return;
            await FormatBatchAsync(FormatScope.Character,
                o => FormatRequest.Color(o.Oid, FormatStop, norm), "字体颜色");
        }

        /// <summary>行距。参数「默认」= 移除 line_spacing（引擎补丁给出了这条通道）。</summary>
        private async Task ApplyLineSpacingAsync(string item)
        {
            if (string.IsNullOrWhiteSpace(item)) return;
            double? value = null;
            if (!string.Equals(item, LineSpacingDefaultLabel, StringComparison.Ordinal))
            {
                if (!double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
                    return;
                value = v;
            }
            await FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                // 已经是这个档位：不发请求，免得白跑一次 RPC
                bool same = value.HasValue
                    ? o.LineSpacing.HasValue && Math.Abs(o.LineSpacing.Value - value.Value) < 1e-6
                    : !o.LineSpacing.HasValue;
                if (same) return null;
                return FormatRequest.Spacing(o.Oid, FormatStop, value);
            }, "行距");
        }

        /// <summary>分栏数。</summary>
        private async Task ApplyColumnsAsync(string item)
        {
            int n = ParseLeadingInt(item);
            if (n < 1 || n > 4) return;
            await FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                if (o.Columns == n) return null;
                return FormatRequest.Columns(o.Oid, FormatStop, n);
            }, "分栏");
        }

        /// <summary>字体族。空值与非法值直接忽略（输入框失焦也会调到这里）。</summary>
        private async Task ApplyFontFamilyAsync(string family)
        {
            string f = (family ?? "").Trim();
            if (f.Length == 0) return;
            await FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                if (string.Equals(o.Font, f, StringComparison.Ordinal)) return null;
                return FormatRequest.Style(o.Oid, FormatStop, font: f);
            }, "字体");
        }

        /// <summary>
        /// 字号绝对值。非数字/≤0/离谱大的值直接拒绝并把输入框回显成当前值 ——
        /// 不写源码也不弹错误框（用户还在输入框里，弹窗会打断输入）。
        /// </summary>
        private async Task ApplyFontSizeAsync(string text)
        {
            if (!double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                || v <= 0 || v > 1000)
            {
                RaisePropertyChanged(nameof(FormatFontSizeText));   // 回显当前值，丢弃非法输入
                return;
            }
            await FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                if (Math.Abs(o.FontSize - v) < 1e-6) return null;
                return FormatRequest.FontSize(o.Oid, FormatStop, Math.Round(v, 2));
            }, "字号");
        }

        /// <summary>字号增大/减小：按 1.1 / 0.9 步进（与 macOS 版 stepper 的手感一致）。</summary>
        private async Task StepFontSizeAsync(string dir)
        {
            bool up = string.Equals(dir, "up", StringComparison.OrdinalIgnoreCase);
            await FormatBatchAsync(FormatScope.TextOnly, o =>
            {
                double now = o.FontSize > 0 ? o.FontSize : 28;
                double next = Math.Max(6, Math.Min(400, Math.Round(now * (up ? 1.1 : 0.9), 1)));
                if (Math.Abs(next - now) < 1e-6) return null;
                return FormatRequest.FontSize(o.Oid, FormatStop, next);
            }, up ? "增大字号" : "减小字号");
        }

        /// <summary>从「2 栏」这类下拉文本里取数字。</summary>
        private static int ParseLeadingInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out int n) ? n : 0;
        }

        #endregion

        #region 取色弹层

        private bool _isHighlightPickerOpen;
        public bool IsHighlightPickerOpen
        {
            get => _isHighlightPickerOpen;
            set
            {
                if (SetProperty(ref _isHighlightPickerOpen, value))
                {
                    if (value) { IsColorPickerOpen = false; SnapshotPickerColor(FormatHighlightColor); }
                    ResetCustomColorRow();
                }
            }
        }

        private bool _isColorPickerOpen;
        public bool IsColorPickerOpen
        {
            get => _isColorPickerOpen;
            set
            {
                if (SetProperty(ref _isColorPickerOpen, value))
                {
                    if (value) { IsHighlightPickerOpen = false; SnapshotPickerColor(FormatTextColor); }
                    ResetCustomColorRow();
                }
            }
        }

        /// <summary>
        /// 弹层打开瞬间的当前色快照。
        /// 为什么要快照：选色后引擎刷新会把当前色改掉，如果色块的描边/勾跟着实时值走，
        /// 用户会看到勾选在弹层里跳来跳去；单次选择语义下，勾选只该反映"打开那一刻"的状态。
        /// </summary>
        private string _pickerSnapshotColor;
        public string PickerSnapshotColor => _pickerSnapshotColor ?? "";

        private void SnapshotPickerColor(string hex)
        {
            _pickerSnapshotColor = hex;
            RaisePropertyChanged(nameof(PickerSnapshotColor));
        }

        private bool _isCustomColorRowOpen;
        public bool IsCustomColorRowOpen
        {
            get => _isCustomColorRowOpen;
            set => SetProperty(ref _isCustomColorRowOpen, value);
        }

        private string _customColorText = "";
        public string CustomColorText
        {
            get => _customColorText;
            set => SetProperty(ref _customColorText, value);
        }

        private string _customColorError = "";
        public string CustomColorError
        {
            get => _customColorError;
            set => SetProperty(ref _customColorError, value);
        }

        /// <summary>关闭弹层时把「自定义…」彻底复位，下次打开是干净的一行空白输入。</summary>
        private void ResetCustomColorRow()
        {
            if (IsHighlightPickerOpen || IsColorPickerOpen) return;
            IsCustomColorRowOpen = false;
            CustomColorText = "";
            CustomColorError = "";
            if (_pickerSnapshotColor != null) SnapshotPickerColor(null);
        }

        /// <summary>再点同一个按钮 = 关闭（StaysOpen=True 下这条要靠自己实现，见架构 §5.3）。</summary>
        private void ToggleColorPicker(string mode)
        {
            if (string.Equals(mode, "highlight", StringComparison.OrdinalIgnoreCase))
            {
                if (!CanFormatCharacter) return;    // 禁用态点了不弹层
                IsHighlightPickerOpen = !IsHighlightPickerOpen;
            }
            else if (string.Equals(mode, "color", StringComparison.OrdinalIgnoreCase))
            {
                if (!CanFormatCharacter) return;
                IsColorPickerOpen = !IsColorPickerOpen;
            }
        }

        /// <summary>
        /// 弹层里选中一个色块。走哪条通道由"当前打开的是哪个弹层"决定，
        /// 这样弹层的 XAML 不需要知道自己是高亮还是字色（一套模板两处复用）。
        /// </summary>
        private void PickColor(string hex)
        {
            if (IsHighlightPickerOpen) _ = ApplyHighlightAsync(hex);
            else if (IsColorPickerOpen) _ = ApplyColorAsync(hex);
        }

        /// <summary>关闭两个弹层（Esc / 点窗口别处时用）。</summary>
        public void CloseColorPickers()
        {
            IsHighlightPickerOpen = false;
            IsColorPickerOpen = false;
        }

        /// <summary>
        /// 「自定义…」提交：校验 #RRGGBB，非法就地红字提示且不发请求；
        /// 合法就按当前打开的弹层决定走高亮还是字色。
        /// </summary>
        private void ApplyCustomColor()
        {
            string norm = FormatPalette.NormalizeHex(CustomColorText, out bool ok);
            if (!ok)
            {
                CustomColorError = "请输入 #RRGGBB 格式的颜色，例如 #12AB34";
                return;
            }
            CustomColorError = "";
            if (IsHighlightPickerOpen) _ = ApplyHighlightAsync(norm);
            else if (IsColorPickerOpen) _ = ApplyColorAsync(norm);
        }

        #endregion

        #region 状态广播

        /// <summary>
        /// 让所有格式状态属性重新发一遍变更通知（WPF 不认方法，只能逐个属性来）。
        /// 三个时机调用：选中变化、引擎刷新完成、引擎连接状态变化。
        /// 这里只读内存里的 <see cref="SelectedObject"/>，不碰引擎，所以不存在刷新回路。
        /// </summary>
        private void RaiseFormatStateChanged()
        {
            RaisePropertyChanged(nameof(FormatBold));
            RaisePropertyChanged(nameof(FormatItalic));
            RaisePropertyChanged(nameof(FormatUnderline));
            RaisePropertyChanged(nameof(FormatStrike));
            RaisePropertyChanged(nameof(FormatBulleted));
            RaisePropertyChanged(nameof(FormatNumbered));
            RaisePropertyChanged(nameof(FormatSuperscript));
            RaisePropertyChanged(nameof(FormatSubscript));
            RaisePropertyChanged(nameof(FormatHighlightColor));
            RaisePropertyChanged(nameof(FormatTextColor));
            RaisePropertyChanged(nameof(FormatParagraphAlign));
            RaisePropertyChanged(nameof(FormatColumns));
            RaisePropertyChanged(nameof(FormatColumnsText));
            RaisePropertyChanged(nameof(FormatLineSpacing));
            RaisePropertyChanged(nameof(FormatLineSpacingText));
            RaisePropertyChanged(nameof(FormatFontFamily));
            RaisePropertyChanged(nameof(FormatFontSizeText));
            RaisePropertyChanged(nameof(CanFormatCharacter));
            RaisePropertyChanged(nameof(CanFormatWeight));
            RaisePropertyChanged(nameof(CanFormatTextOnly));
            RaisePropertyChanged(nameof(FormatHintCharacter));
            RaisePropertyChanged(nameof(FormatHintWeight));
            RaisePropertyChanged(nameof(FormatHintTextOnly));
        }

        #endregion
    }
}
