using System;
using System.Globalization;
using System.Windows.Data;

namespace IFC_Viewer_00.Converters
{
    // 將 Canvas 的縮放值轉為其倒數，用於讓文字在縮放時保持視覺字級不變
    public class InverseScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double s = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (s == 0) return 1.0;
                return 1.0 / s;
            }
            catch { return 1.0; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
