using Xbim.Common;

namespace IFC_Viewer_00.Models
{
    public class SchematicEdge
    {
        public string Id { get; set; } = string.Empty;
        public SchematicNode StartNode { get; set; } = default!;
        public SchematicNode EndNode { get; set; } = default!;
        public IPersistEntity Entity { get; set; } = default!;
    }
}
