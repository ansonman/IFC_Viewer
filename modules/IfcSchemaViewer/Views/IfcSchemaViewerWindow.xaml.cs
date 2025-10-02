using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IfcSchemaViewer.ViewModels;
using Xbim.Ifc4.Interfaces;

namespace IfcSchemaViewer.Views
{
    public partial class IfcSchemaViewerWindow : Window
    {
        public IfcSchemaViewerViewModel ViewModel { get; }

        public IfcSchemaViewerWindow()
        {
            InitializeComponent();
            ViewModel = new IfcSchemaViewerViewModel();
            this.DataContext = ViewModel;
        }

        public void ShowEntity(IIfcObject entity)
        {
            ViewModel.LoadFrom(entity);
            if (this.WindowState == WindowState.Minimized) this.WindowState = WindowState.Normal;
            if (this.Owner == null)
            {
                this.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            }
            this.Show();
            this.Activate();
            this.Topmost = true; this.Topmost = false;
        }

        private void ClearFilter_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.FilterText = string.Empty;
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            ExpandOrCollapseAll(expand: true);
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            ExpandOrCollapseAll(expand: false);
        }

        private void ExpandOrCollapseAll(bool expand)
        {
            try
            {
                if (Tree.Items == null) return;
                foreach (var item in Tree.Items)
                {
                    var tvi = Tree.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                    if (tvi != null) SetExpandedRecursive(tvi, expand);
                }
            }
            catch { }
        }

        private void SetExpandedRecursive(TreeViewItem item, bool expand)
        {
            item.IsExpanded = expand;
            item.UpdateLayout();
            for (int i = 0; i < item.Items.Count; i++)
            {
                var child = item.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
                if (child != null) SetExpandedRecursive(child, expand);
            }
        }

        private void CopyName_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.CommandParameter is IfcSchemaNode n)
            {
                Clipboard.SetText(n.NameOnly);
            }
        }

        private void CopyValue_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.CommandParameter is IfcSchemaNode n)
            {
                Clipboard.SetText(n.ValueOnly);
            }
        }

        private void CopyPair_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as MenuItem)?.CommandParameter is IfcSchemaNode n)
            {
                Clipboard.SetText(n.NameEqualsValue);
            }
        }

        private void Node_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // 可依需要調整選單可用性
        }
    }
}
