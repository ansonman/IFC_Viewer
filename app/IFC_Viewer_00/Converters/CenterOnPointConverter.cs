using System;
using System.Globalization;
using System.Windows.Data;

namespace IFC_Viewer_00.Converters
{
    // 將 (中心座標, 寬/高) 轉為左上角座標 = 中心 - 尺寸/2
    public class CenterOnPointConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double center = ToDouble(values, 0, 0.0);
                double size = ToDouble(values, 1, 0.0);
                if (double.IsNaN(size) || double.IsInfinity(size)) size = 0.0;
                return center - size / 2.0;
            }
            catch { return 0.0; }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static double ToDouble(object[] values, int index, double fallback)
        {
            if (values == null || index < 0 || index >= values.Length) return fallback;
            try
            {
                if (values[index] is double d) return d;
                if (values[index] is float f) return f;
                if (values[index] is int i) return i;
                if (values[index] is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
            }
            catch { }
            return fallback;
        }
    }
}
