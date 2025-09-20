using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Xbim.Common;
using IFC_Viewer_00.Models;
using IFC_Viewer_00.Services;

namespace IFC_Viewer_00.ViewModels
{
    public class SchematicViewModel
    {
        private readonly SchematicService _service;

        public ObservableCollection<SchematicNodeView> Nodes { get; } = new();
        public ObservableCollection<SchematicEdgeView> Edges { get; } = new();

        public double Scale { get; set; } = 0.001; // 粗略縮放，將毫米→公尺（視模型而定）

        public SchematicViewModel(SchematicService service)
        {
            _service = service;
        }

        public async Task LoadAsync(IModel model)
        {
            var data = await _service.GenerateTopologyAsync(model);
            Nodes.Clear();
            Edges.Clear();

            // 建立 NodeView 並以簡單縮放投影 X/Y
            var nodeMap = data.Nodes.ToDictionary(n => n.Id, n => new SchematicNodeView
            {
                Node = n,
                X = n.Position3D.X * Scale,
                Y = -n.Position3D.Y * Scale // 反轉 Y 以符合 UI 座標
            });

            foreach (var nv in nodeMap.Values)
                Nodes.Add(nv);

            // 建立 EdgeView（參考 NodeView）
            foreach (var e in data.Edges)
            {
                if (e.StartNode == null || e.EndNode == null) continue;
                if (!nodeMap.TryGetValue(e.StartNode.Id, out var s)) continue;
                if (!nodeMap.TryGetValue(e.EndNode.Id, out var t)) continue;
                Edges.Add(new SchematicEdgeView
                {
                    Edge = e,
                    Start = s,
                    End = t
                });
            }
        }
    }

    public class SchematicNodeView
    {
        public SchematicNode Node { get; set; } = null!;
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class SchematicEdgeView
    {
        public SchematicEdge Edge { get; set; } = null!;
        public SchematicNodeView Start { get; set; } = null!;
        public SchematicNodeView End { get; set; } = null!;
    }
}
