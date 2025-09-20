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
            // 為避免 Id 衝突，優先使用實體參照或 EntityLabel 作為 key
            var nodeMap = new Dictionary<object, SchematicNodeView>();
            foreach (var n in data.Nodes)
            {
                object key = (object?)n.Entity ?? (object)n.Id;
                if (nodeMap.ContainsKey(key))
                {
                    // 退而求其次：用組合鍵防止碰撞
                    key = (n.Entity != null) ? (object)$"{n.Entity.EntityLabel}:{n.Id}" : (object)$"dup:{n.Id}:{nodeMap.Count}";
                }
                nodeMap[key] = new SchematicNodeView
                {
                    Node = n,
                    X = n.Position3D.X * Scale,
                    Y = -n.Position3D.Y * Scale
                };
            }

            foreach (var nv in nodeMap.Values)
                Nodes.Add(nv);

            // 建立 EdgeView（參考 NodeView）
            foreach (var e in data.Edges)
            {
                if (e.StartNode == null || e.EndNode == null) continue;
                var s = FindNodeView(nodeMap, e.StartNode);
                var t = FindNodeView(nodeMap, e.EndNode);
                if (s == null || t == null) continue;
                Edges.Add(new SchematicEdgeView
                {
                    Edge = e,
                    Start = s,
                    End = t
                });
            }
        }

        private static SchematicNodeView? FindNodeView(Dictionary<object, SchematicNodeView> map, SchematicNode node)
        {
            if (node.Entity != null && map.TryGetValue(node.Entity, out var byEnt))
                return byEnt;
            // 回退：比對 Id 或組合鍵
            foreach (var kv in map)
            {
                if (ReferenceEquals(kv.Value.Node, node)) return kv.Value;
                if (!string.IsNullOrEmpty(node.Id) && kv.Value.Node.Id == node.Id) return kv.Value;
            }
            return null;
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
