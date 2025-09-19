using Xbim.Ifc4.Interfaces;
using System.Collections.ObjectModel;

namespace IFC_Viewer_00.Models
{
    /// <summary>
    /// IFC 空間結構樹狀節點資料模型
    /// </summary>
    public class SpatialNode
    {
        public string Name { get; set; } = string.Empty;
        // 使用 IIfcObjectDefinition 以涵蓋 IfcProject/IfcContext 與一般 IIfcObject
        public IIfcObjectDefinition? Entity { get; set; } = null;
        public ObservableCollection<SpatialNode> Children { get; set; } = new();
    }
}
