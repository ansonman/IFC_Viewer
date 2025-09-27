using Xbim.Common;
using IXbimEntity = Xbim.Common.IPersistEntity;

namespace IFC_Viewer_00.Models
{
    public class SchematicEdge
    {
        public string Id { get; set; } = string.Empty;
        public string StartNodeId { get; set; } = string.Empty;
        public string EndNodeId { get; set; } = string.Empty;
        public SchematicNode StartNode { get; set; } = default!;
        public SchematicNode EndNode { get; set; } = default!;
        // 舊字段：Entity（保留以相容既有程式）
        public IXbimEntity Entity { get; set; } = default!;
        // SOP 2.0：Connection 指向 IfcRelConnectsPorts 實體（或相容的連線實體）
        public IXbimEntity Connection { get; set; } = default!;
        // 由幾何鄰近性推斷出的邊（非 IfcRelConnectsPorts）
        public bool IsInferred { get; set; } = false;
    }
}
