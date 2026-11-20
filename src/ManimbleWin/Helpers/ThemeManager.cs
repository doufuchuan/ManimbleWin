using System;
using System.Windows;
using System.Windows.Media;

namespace ManimbleWin.Helpers
{
    /// <summary>
    /// 应用明暗主题切换。原版 Theme.swift 用 NSColor 动态对（aqua / darkAqua），
    /// 这里用同名 DynamicResource 画刷 + 修改画刷颜色实现等价效果。
    /// </summary>
    public static class ThemeManager
    {
        // 取自 apps/macos/Sources/Manimble/Theme.swift 的精确 sRGB 值
        private static readonly Color Accent = Color.FromRgb(0xFF, 0x6C, 0x0C);   // #FF6C0C
        private static readonly Color Warm = Color.FromRgb(0x0C, 0x8C, 0xFF);     // #0C8CFF

        private static readonly Color BarLight = Color.FromRgb(0xF1, 0xF1, 0xF1);
        private static readonly Color BarDark = Color.FromRgb(0x22, 0x22, 0x22);
        private static readonly Color SideLight = Color.FromRgb(0xED, 0xED, 0xED);
        private static readonly Color SideDark = Color.FromRgb(0x2B, 0x2B, 0x2B);
        private static readonly Color CodeLight = Color.FromRgb(0xFC, 0xFC, 0xFC);
        private static readonly Color CodeDark = Color.FromRgb(0x1B, 0x1B, 0x1B);
        private static readonly Color CanvasTopLight = Color.FromRgb(0xF6, 0xF6, 0xF6);
        private static readonly Color CanvasTopDark = Color.FromRgb(0x26, 0x26, 0x26);
        private static readonly Color CanvasBottomLight = Color.FromRgb(0xEA, 0xEA, 0xEA);
        private static readonly Color CanvasBottomDark = Color.FromRgb(0x14, 0x14, 0x14);
        private static readonly Color InkLight = Color.FromRgb(0x1A, 0x1A, 0x1A);
        private static readonly Color InkDark = Color.FromRgb(0xF5, 0xF5, 0xF5);
        private static readonly Color ChromeLight = Color.FromRgb(0xFF, 0xFF, 0xFF);
        private static readonly Color ChromeDark = Color.FromRgb(0x1E, 0x1E, 0x1E);
        private static readonly Color SepLight = Color.FromRgb(0xD6, 0xD6, 0xD6);
        private static readonly Color SepDark = Color.FromRgb(0x3A, 0x3A, 0x3A);

        public static bool IsDark { get; private set; } = true;

        public static event Action ThemeChanged;

        public static void Apply(bool dark)
        {
            IsDark = dark;
            ResourceDictionary r = Application.Current?.Resources;
            if (r == null) return;

            Set(r, "AccentBrush", Accent);
            Set(r, "AccentSoftBrush", Color.FromArgb(0x2E, Accent.R, Accent.G, Accent.B)); // 18%
            Set(r, "WarmBrush", Warm);
            Set(r, "BarBackgroundBrush", dark ? BarDark : BarLight);
            Set(r, "SidebarBackgroundBrush", dark ? SideDark : SideLight);
            Set(r, "CodeBackgroundBrush", dark ? CodeDark : CodeLight);
            Set(r, "CanvasTopBrush", dark ? CanvasTopDark : CanvasTopLight);
            Set(r, "CanvasBottomBrush", dark ? CanvasBottomDark : CanvasBottomLight);
            // 画布底色渐变的两个停靠点绑的是这两条 Color 资源：画刷实例会被整体替换，按实例拿 Color 的
            // 绑定永远停在旧值；Color 是值类型、不会被冻结，替换值是最稳的做法。
            SetColor(r, "CanvasTopColor", dark ? CanvasTopDark : CanvasTopLight);
            SetColor(r, "CanvasBottomColor", dark ? CanvasBottomDark : CanvasBottomLight);
            Set(r, "TextPrimaryBrush", dark ? InkDark : InkLight);
            Set(r, "ChromeBackgroundBrush", dark ? ChromeDark : ChromeLight);
            Set(r, "SeparatorBrush", dark ? SepDark : SepLight);
            // 代码编辑器光标：暗色用亮橙、亮色用深橙，两种底色下都清晰可见
            Set(r, "CaretBrush", dark ? Color.FromRgb(0xFF, 0x9F, 0x5A) : Color.FromRgb(0xC1, 0x44, 0x0E));
            Set(r, "CodeSelectionBrush", dark
                ? Color.FromArgb(0x66, 0xFF, 0x6C, 0x0C)
                : Color.FromArgb(0x4D, 0xFF, 0x6C, 0x0C));
            Set(r, "TextSecondaryBrush", dark ? Color.FromRgb(0xAE, 0xAE, 0xAE) : Color.FromRgb(0x5A, 0x5A, 0x5A));
            Set(r, "SubtleBackgroundBrush", dark ? Color.FromRgb(0x2E, 0x2E, 0x2E) : Color.FromRgb(0xE6, 0xE6, 0xE6));
            ThemeChanged?.Invoke();
        }

        private static void Set(ResourceDictionary dict, string key, Color color)
        {
            if (!(dict[key] is SolidColorBrush brush)) return;
            if (brush.Color == color) return;

            // 资源里的画刷可能已被 WPF 冻结（插入 Application.Resources / BAML 加载时都会自动 Freeze）：
            // 对冻结实例赋 Color 会抛 InvalidOperationException（历史 crash.log 就有这一条）。冻结时改成
            // 替换实例——接在可视树里的 DynamicResource 引用会重新解析；会收不到通知的情形见 SetColor。
            if (brush.IsFrozen)
            {
                dict[key] = new SolidColorBrush(color);
                return;
            }
            brush.Color = color;
        }

        /// <summary>
        /// 写入一条 <see cref="Color"/> 类型的资源。Color 是 struct，不属于 Freezable，WPF 无法冻结它，
        /// 这里可以直接替换值。<c>DynamicResource</c> 引用会重新取值——但只对"已接进可视树、有窗口祖先"
        /// 的元素成立（WPF 的资源失效通知是从窗口往下广播的；资源字典里的 Freezable 与从未挂窗口的
        /// 分离态控件都收不到，实测如此）。
        /// </summary>
        private static void SetColor(ResourceDictionary dict, string key, Color color)
        {
            if (dict[key] is Color existing && existing == color) return;
            dict[key] = color;
        }
    }
}
