using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ManimbleWin.Helpers
{
    /// <summary>
    /// 把图片文件路径转成可绑定的 BitmapImage。用 MemoryStream 拷贝字节，
    /// 避免引擎进程持有文件句柄导致锁定；路径为空时返回 null（配合 Visibility 使用）。
    /// </summary>
    public class PathToImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string path = value as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;   // 加载到内存后释放文件
                using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                {
                    bi.StreamSource = ms;
                    bi.EndInit();
                }
                bi.Freeze();
                return bi;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
