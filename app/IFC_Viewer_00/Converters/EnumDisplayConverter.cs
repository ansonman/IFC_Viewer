using System;
using System.Globalization;
using System.Windows.Data;
using IFC_Viewer_00.ViewModels;

namespace IFC_Viewer_00.Converters
{
    // 將 RunLegendSortMode 與 ComboBox.SelectedIndex 互轉：
    // 0 -> ByRunIdAsc, 1 -> ByCountDesc
    public class EnumDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is SchematicViewModel.RunLegendSortMode mode)
            {
                return mode == SchematicViewModel.RunLegendSortMode.ByCountDesc ? 1 : 0;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is int idx)
                {
                    return idx == 1 ? SchematicViewModel.RunLegendSortMode.ByCountDesc : SchematicViewModel.RunLegendSortMode.ByRunIdAsc;
                }
                if (value is string s && int.TryParse(s, out var i))
                {
                    return i == 1 ? SchematicViewModel.RunLegendSortMode.ByCountDesc : SchematicViewModel.RunLegendSortMode.ByRunIdAsc;
                }
            }
            catch { }
            return SchematicViewModel.RunLegendSortMode.ByRunIdAsc;
        }
    }
}
