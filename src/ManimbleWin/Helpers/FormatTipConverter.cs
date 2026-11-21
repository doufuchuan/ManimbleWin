using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ManimbleWin.Services;

namespace ManimbleWin.Helpers
{
    /// <summary>
    /// 把「基础提示（按钮本来的说明）」与「当前为什么不可用」合成一段 ToolTip 文本。
    /// <para>为什么需要一个多值转换器：格式栏有 12 个控件，每个都要显示
    /// 「基础提示」+「可用/禁用两种原因」，如果不在一个地方合，XAML 里就会出现
    /// 24 套 DataTrigger 文案，改一句提示要动十几处。</para>
    /// </summary>
    public class FormatTipConverter : IMultiValueConverter
    {
        /// <param name="values">
        /// [0] 基础提示（控件 Tag）；[1] 不可用原因（为空表示当前可用）。
        /// </param>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string baseTip = values != null && values.Length > 0 ? values[0]?.ToString() ?? "" : "";
            string reason = values != null && values.Length > 1 ? values[1]?.ToString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(reason)) return baseTip;
            if (string.IsNullOrWhiteSpace(baseTip)) return reason;
            return baseTip + "\n" + reason;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 取色弹层里「当前色块的描边」：命中当前色就返回强调色，否则透明。
    /// 为什么不用 DataTrigger：勾选/描边要同时看「预设色」和「VM 当前色」两个源，
    /// 多值绑定 + 一个转换器的可读性远好过在每个 ItemTemplate 里铺两层 Trigger。
    /// </summary>
    public class SwatchRingConverter : IMultiValueConverter
    {
        /// <param name="values">[0] 预设色 #RRGGBB；[1] 当前色（可能为 null 或 "on"）。</param>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => Matches(values) ? Resource("AccentBrush", Brushes.Orange) : Brushes.Transparent;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        /// <summary>给外部（小勾的 Visibility 转换器）复用同一套比较规则。</summary>
        internal static bool Matches(object[] values)
        {
            if (values == null || values.Length < 2) return false;
            string preset = values[0]?.ToString();
            string current = values[1]?.ToString();
            return FormatPalette.Matches(current, preset);
        }

        /// <summary>从应用资源里取主题画笔：写死颜色会在浅色主题下突兀。</summary>
        internal static Brush Resource(string key, Brush fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? fallback;
    }

    /// <summary>当前色块上的小勾：命中当前色显示，否则隐藏。</summary>
    public class SwatchTickConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => SwatchRingConverter.Matches(values) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 数值小于参数时返回 true。
    /// 用途：格式栏在窄窗口下要先收掉右端的次要按钮（脚本/反馈），
    /// 而 XAML 的 DataTrigger 只能做相等比较，比大小必须借一个转换器。
    /// </summary>
    public class NumberLessThanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is double d)) return false;
            if (!double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double limit))
                return false;
            return d < limit;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>
    /// 取色按钮下方 3px 色条的颜色。
    /// <para>三种输入都要有交代：合法 hex → 该颜色；<c>"on"</c>（源码有底块但没写颜色）或
    /// 空/非法 → 回退为次要文字色。绝不能回退成透明：那会让「有高亮」和「没高亮」看起来一样。</para>
    /// </summary>
    public class ColorStripBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string norm = FormatPalette.NormalizeHex(value as string, out bool ok);
            if (ok)
            {
                try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(norm)); }
                catch { /* 归一化通过但 WPF 解析不了：落到下面的回退色 */ }
            }
            return SwatchRingConverter.Resource("TextSecondaryBrush", Brushes.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
