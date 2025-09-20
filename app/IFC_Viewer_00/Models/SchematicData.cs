using System.Collections.Generic;

namespace IFC_Viewer_00.Models
{
    public class SchematicData
    {
        public List<SchematicNode> Nodes { get; } = new List<SchematicNode>();
        public List<SchematicEdge> Edges { get; } = new List<SchematicEdge>();
    }
}
