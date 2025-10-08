using System;
using System.Globalization;
using System.Windows.Data;

namespace IFC_Viewer_00.Converters
{
    // 將 (尺寸, 縮放倍數) 轉成 -尺寸*縮放/2，用於在 RenderTransform 中最後做平移置中
    public class CenterOffsetWithScaleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double size = ToDouble(values, 0, 0.0);
                double scale = ToDouble(values, 1, 1.0);
                if (double.IsNaN(size) || double.IsInfinity(size)) size = 0.0;
                if (double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
                return -0.5 * size * scale;
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
