using System;
using System.Globalization;
using System.Windows.Data;

namespace IFC_Viewer_00.Converters
{
    // 將尺寸值轉為 -尺寸/2，用於將元素以自身寬高的中心置中
    public class HalfNegativeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double v = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
                return -v / 2.0;
            }
            catch { return 0.0; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
