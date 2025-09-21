using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using IFC_Viewer_00.Models;

namespace IFC_Viewer_00.Views
{
    public partial class TreeViewTestView : UserControl
    {
        public TreeViewTestView()
        {
            // 嘗試以反射呼叫 InitializeComponent（在某些臨時專案/分析器環境中，XAML 可能未產生 partial 類別）
            try
            {
                var init = GetType().GetMethod("InitializeComponent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                init?.Invoke(this, null);
            }
            catch { }

            // 建立三層模擬資料
            var root = new SpatialNode { Name = "Project A" };
            var b1 = new SpatialNode { Name = "Building 1" };
            var b2 = new SpatialNode { Name = "Building 2" };
            var l1 = new SpatialNode { Name = "Level 1" };
            var l2 = new SpatialNode { Name = "Level 2" };

            b1.Children.Add(l1);
            b1.Children.Add(l2);
            root.Children.Add(b1);
            root.Children.Add(b2);

            var data = new ObservableCollection<SpatialNode> { root };

            // 以名稱查找 XAML 中的 TreeView；若不存在，則動態建立一個
            TreeView? tv = null;
            try
            {
                var findName = GetType().GetMethod("FindName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var obj = findName?.Invoke(this, new object?[] { "TestTreeView" });
                tv = obj as TreeView;
            }
            catch { }
            if (tv == null)
            {
                tv = new TreeView { Margin = new Thickness(8) };
                this.Content = tv;
            }
            tv.ItemsSource = data;
        }
    }
}