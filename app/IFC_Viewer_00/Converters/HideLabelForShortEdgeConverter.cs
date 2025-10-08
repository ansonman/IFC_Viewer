using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IFC_Viewer_00.Converters
{
    // 回傳 Visibility.Collapsed 表示要隱藏，Visible 表示顯示
    public class HideLabelForShortEdgeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (values == null || values.Length < 3)
                    return Visibility.Visible;

                // values[0]: AutoHide (bool)
                // values[1]: EdgeLength (double)
                // values[2]: Threshold (double)
                bool autoHide = values[0] is bool b && b;
                double length = values[1] is double d1 ? d1 : ToDouble(values[1]);
                double threshold = values[2] is double d2 ? d2 : ToDouble(values[2]);

                if (!autoHide) return Visibility.Visible;
                if (double.IsNaN(length) || double.IsInfinity(length)) return Visibility.Visible;
                if (double.IsNaN(threshold) || double.IsInfinity(threshold)) threshold = 0.0;

                return length < threshold ? Visibility.Collapsed : Visibility.Visible;
            }
            catch
            {
                return Visibility.Visible;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        private static double ToDouble(object v)
        {
            try
            {
                if (v == null) return double.NaN;
                if (v is IConvertible) return System.Convert.ToDouble(v, CultureInfo.InvariantCulture);
                return double.NaN;
            }
            catch { return double.NaN; }
        }
    }
}
