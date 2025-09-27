using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using IFC_Viewer_00.Models;

namespace IFC_Viewer_00.Services
{
    /// <summary>
    /// 臨時用的假資料服務：回傳固定的節點與邊，供前端先行開發與驗證。
    /// </summary>
    public class MockSchematicService
    {
        public Task<SchematicData> GetMockDataAsync()
        {
            var data = new SchematicData();

            // 建立 6 個分散的節點
            var nodes = new List<SchematicNode>
            {
                new SchematicNode { Id = "N1", Name = "Pump-01", IfcType = "IfcFlowController", Position2D = new Point(50, 80) },
                new SchematicNode { Id = "N2", Name = "Valve-01", IfcType = "IfcValve", Position2D = new Point(220, 60) },
                new SchematicNode { Id = "N3", Name = "Pipe-01", IfcType = "IfcPipeSegment", Position2D = new Point(380, 100) },
                new SchematicNode { Id = "N4", Name = "Elbow-01", IfcType = "IfcPipeFitting", Position2D = new Point(540, 180) },
                new SchematicNode { Id = "N5", Name = "Terminal-01", IfcType = "IfcFlowTerminal", Position2D = new Point(720, 220) },
                new SchematicNode { Id = "N6", Name = "Valve-02", IfcType = "IfcValve", Position2D = new Point(520, 40) }
            };

            foreach (var n in nodes)
                data.Nodes.Add(n);

            // 建立邊（以 Id 參照，並連回物件引用）
            void addEdge(string id, string s, string t)
            {
                var start = nodes.Find(n => n.Id == s)!;
                var end = nodes.Find(n => n.Id == t)!;
                data.Edges.Add(new SchematicEdge
                {
                    Id = id,
                    StartNodeId = s,
                    EndNodeId = t,
                    StartNode = start,
                    EndNode = end,
                    Entity = null!,
                    IsInferred = false
                });
            }

            addEdge("E1", "N1", "N2");
            addEdge("E2", "N2", "N3");
            addEdge("E3", "N3", "N4");
            addEdge("E4", "N4", "N5");
            addEdge("E5", "N2", "N6");

            return Task.FromResult(data);
        }
    }
}
