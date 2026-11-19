using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ManimbleWin.Helpers
{
    /// <summary>bool -> 画刷。parameter="err" 时：true 红 / false 常规色。</summary>
    public class BoolToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool v && v;
            bool errMode = parameter != null && parameter.ToString() == "err";
            if (errMode)
                return new SolidColorBrush(b ? Color.FromRgb(0xF8, 0x51, 0x49) : Color.FromRgb(0xC9, 0xD1, 0xD9));
            return new SolidColorBrush(b ? Color.FromRgb(0xFF, 0x6C, 0x0C) : Colors.Transparent);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class BoolToFontWeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? FontWeights.Bold : FontWeights.Normal;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class BoolToFontStyleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? FontStyles.Italic : FontStyles.Normal;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class BoolToTextDecorationsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? TextDecorations.Underline : null;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class PassThroughConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value;
    }

    public class ShapeLabelVisibilityConverter : IValueConverter
    {
        public string Target { get; set; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string wanted = parameter?.ToString() ?? Target;
            return value != null && value.ToString() == wanted ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>矩形/圆/椭圆可见，线与箭头隐藏。</summary>
    public class BoxShapeVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string s = value?.ToString();
            return (s == "Rectangle" || s == "Circle" || s == "Ellipse" || s == "Triangle")
                ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>线可见（箭头除外）。</summary>
    public class PlainLineVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value?.ToString() == "Line" ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>当前索引是否等于参数索引（用于 RadioButton IsChecked）。</summary>
    public class IndexEqualConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int cur = value is int i ? i : -1;
            int target = int.TryParse(parameter?.ToString(), out int p) ? p : -1;
            return cur == target;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && int.TryParse(parameter?.ToString(), out int p)) return p;
            return Binding.DoNothing;
        }
    }

    /// <summary>当前索引等于参数索引时可见。</summary>
    public class IndexToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int cur = value is int i ? i : -1;
            int target = int.TryParse(parameter?.ToString(), out int p) ? p : -1;
            return cur == target ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

}
