using System.Windows.Media.Media3D;
using Xbim.Common;

namespace IFC_Viewer_00.Models
{
    public class SchematicNode
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string IfcType { get; set; } = string.Empty;
        public Point3D Position3D { get; set; }
        public IPersistEntity Entity { get; set; } = default!;
    }
}
