using System.Linq;
using System.Windows;
using IFC_Viewer_00.ViewModels;

namespace IFC_Viewer_00.Views.Dialogs
{
    public partial class SystemFilterDialog : Window
    {
        public SystemFilterDialog()
        {
            InitializeComponent();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is System.Collections.IEnumerable items)
            {
                foreach (var it in items)
                {
                    if (it is SchematicViewModel.SystemFilterOption opt)
                        opt.IsChecked = true;
                }
            }
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is System.Collections.IEnumerable items)
            {
                foreach (var it in items)
                {
                    if (it is SchematicViewModel.SystemFilterOption opt)
                        opt.IsChecked = false;
                }
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
        }
    }
}
