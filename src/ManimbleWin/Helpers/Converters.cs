using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ManimbleWin.Helpers
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        /// <summary>设为 "Invert" 可反转（作为资源声明时无法传 ConvertParameter，故此属性等价）。</summary>
        public string ParameterFallback { get; set; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool v && v;
            string p = parameter?.ToString() ?? ParameterFallback;
            if (p != null && p.Equals("Invert", StringComparison.OrdinalIgnoreCase)) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility vis && vis == Visibility.Visible;
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public string ParameterFallback { get; set; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool has = value != null;
            string p = parameter?.ToString() ?? ParameterFallback;
            if (p != null && p.Equals("Invert", StringComparison.OrdinalIgnoreCase)) has = !has;
            return has ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class StringToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Brush br) return br;
            string s = value as string;
            if (!string.IsNullOrEmpty(s))
            {
                try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)); }
                catch { }
            }
            return new SolidColorBrush(Colors.Transparent);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>字符串等于参数时返回 true。用于让一组 RadioButton 绑定同一个字符串属性（BgMode、PagePos）。</summary>
    public class StringEqualConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? parameter?.ToString() : Binding.DoNothing;
    }

    public class BoolToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && !b) return 0.4;
            return 1.0;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class IntToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value == null ? "" : value.ToString();
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class ZoomConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double z = value is double d ? d : 1.0;
            return ((int)Math.Round(z * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class EnumEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value != null && parameter != null && value.ToString() == parameter.ToString();
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
