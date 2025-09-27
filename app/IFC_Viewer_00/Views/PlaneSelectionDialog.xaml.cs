using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace IFC_Viewer_00.Views
{
    public partial class PlaneSelectionDialog : Window
    {
        public string SelectedPlane { get; private set; } = "XY";

        public PlaneSelectionDialog()
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var checkedRb = LogicalTreeHelper.GetChildren(this)
                .OfType<DependencyObject>()
                .SelectMany(GetAllChildren)
                .OfType<RadioButton>()
                .FirstOrDefault(r => r.IsChecked == true);
            if (checkedRb != null && checkedRb.Tag is string tag)
            {
                SelectedPlane = tag;
            }
            DialogResult = true;
            Close();
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> GetAllChildren(DependencyObject parent)
        {
            if (parent == null) yield break;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child != null)
                {
                    yield return child;
                    foreach (var g in GetAllChildren(child)) yield return g;
                }
            }
        }
    }
}