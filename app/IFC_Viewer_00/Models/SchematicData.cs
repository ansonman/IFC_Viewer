using System.Collections.Generic;
using Xbim.Common;

namespace IFC_Viewer_00.Models
{
    public class SchematicData
    {
        public List<SchematicNode> Nodes { get; } = new List<SchematicNode>();
        public List<SchematicEdge> Edges { get; } = new List<SchematicEdge>();
        // 樓層資訊（IfcBuildingStorey）
        public List<LevelInfo> Levels { get; } = new List<LevelInfo>();
        // SOP 2.0：當來源為系統時，帶出系統識別
        public string? SystemName { get; set; }
        public IPersistEntity? SystemEntity { get; set; }
    }
}
