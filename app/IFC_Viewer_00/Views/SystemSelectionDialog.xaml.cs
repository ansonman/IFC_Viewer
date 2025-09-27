using System.Collections.Generic;
using System.Linq;
using System.Windows;
using IFC_Viewer_00.Models;

namespace IFC_Viewer_00.Views
{
    public partial class SystemSelectionDialog : Window
    {
        public SchematicData? SelectedData { get; private set; }
        public SystemSelectionDialog(IEnumerable<SchematicData> items)
        {
            InitializeComponent();
            LstSystems.ItemsSource = items.ToList();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedData = LstSystems.SelectedItem as SchematicData;
            if (SelectedData == null)
            {
                MessageBox.Show(this, "請先選擇一個系統。", "選擇系統", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
