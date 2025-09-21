using Xbim.Ifc4.Interfaces;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IFC_Viewer_00.Models
{
    /// <summary>
    /// IFC 空間結構樹狀節點資料模型
    /// </summary>
    public partial class SpatialNode : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        // 使用 IIfcObjectDefinition 以涵蓋 IfcProject/IfcContext 與一般 IIfcObject
        public IIfcObjectDefinition? Entity { get; set; } = null;
        public ObservableCollection<SpatialNode> Children { get; set; } = new();
        // 供多選（TreeView 核取方塊）用；由 ViewModel 維護與 SelectionService 同步
        public bool IsChecked { get; set; }

        // 任務 1：可見性控制（預設 true）
        [ObservableProperty]
        private bool isVisible = true;

        partial void OnIsVisibleChanged(bool value)
        {
            // 當前節點的可見性改變時，遞迴同步所有子節點
            if (Children == null || Children.Count == 0) return;
            foreach (var c in Children)
            {
                c.IsVisible = value;
            }
        }

        // 任務 2：TreeView 的多選（Shift/Click）狀態
        [ObservableProperty]
        private bool isSelected = false;
    }
}
