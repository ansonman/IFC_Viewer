using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Xbim.Ifc4.Interfaces;
using System.Windows.Controls;
using IFC_Viewer_00.Services; // IfcStringHelper

namespace IFC_Viewer_00.Views
{
    public partial class SystemsPickDialog : Window
    {
    // InitializeComponent 由 XAML g.cs 生成
        public class SystemItem
        {
            public IIfcSystem System { get; set; } = null!;
            public string Name { get; set; } = string.Empty;
        }

        public List<IIfcSystem> SelectedSystems { get; } = new();

        public SystemsPickDialog(IEnumerable<IIfcSystem> systems)
        {
            // InitializeComponent 由 XAML 產生的同名 partial 負責; 若設計工具尚未生成也應於編譯時生成
            InitializeComponent();
            var items = systems.Select(s => new SystemItem { System = s, Name = TryName(s) })
                                .OrderBy(i => i.Name)
                                .ToList();
            // 防禦：尋找 ListBox 元件
            if (this.FindName("SystemsList") is ListBox lb)
                lb.ItemsSource = items;
        }

        private static string TryName(IIfcSystem s)
        {
            try { return IfcStringHelper.FromValue(s.Name) ?? IfcStringHelper.FromValue(s.GlobalId) ?? "Unnamed"; } catch { return "Unnamed"; }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (this.FindName("SystemsList") is ListBox lb)
            {
                foreach (var sel in lb.SelectedItems.OfType<SystemItem>())
                    SelectedSystems.Add(sel.System);
                if (SelectedSystems.Count == 0 && lb.Items.Count == 1)
                {
                    var only = lb.Items[0] as SystemItem;
                    if (only != null) SelectedSystems.Add(only.System);
                }
            }
            DialogResult = true;
            Close();
        }
    }
}