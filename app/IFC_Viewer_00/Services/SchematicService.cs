using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using IFC_Viewer_00.Models;
using Xbim.Common;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;

namespace IFC_Viewer_00.Services
{
    public class SchematicService
    {
        // 核心：從 IFC 模型提取拓撲（優先 IfcRelConnectsPorts，必要時可擴充為幾何鄰近性）
        public Task<SchematicData> GenerateTopologyAsync(IModel ifcModel)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));

            var data = new SchematicData();
            var visited = new HashSet<int>(); // 使用 EntityLabel 去重

            // Port → Node 索引
            var portToNode = new Dictionary<IIfcPort, SchematicNode>();

            // 2. 節點：所有 IfcDistributionElement（包含管件與設備）
            var distElems = ifcModel.Instances.OfType<IIfcDistributionElement>().ToList();
            foreach (var elem in distElems)
            {
                if (TryGetLabel(elem, out var lbl) && !visited.Add(lbl))
                    continue;
                var node = CreateNodeFromElement(elem);
                data.Nodes.Add(node);

                // 掛 Port 對應
                foreach (var p in GetPorts(elem))
                {
                    // 同一個元素的多個 Port 都指到相同 Node
                    portToNode[p] = node;
                }
            }

            // 3. 管段亦視為節點：IfcPipeSegment
            var segments = ifcModel.Instances.OfType<IIfcPipeSegment>().ToList();
            foreach (var seg in segments)
            {
                if (TryGetLabel(seg, out var lbl) && !visited.Add(lbl))
                    continue;
                var node = CreateNodeFromElement(seg);
                data.Nodes.Add(node);

                foreach (var p in GetPorts(seg))
                {
                    portToNode[p] = node;
                }
            }

            // 4. 建立連接：IfcRelConnectsPorts
            var rels = ifcModel.Instances.OfType<IIfcRelConnectsPorts>().ToList();
            foreach (var rel in rels)
            {
                var a = rel.RelatingPort;
                var b = rel.RelatedPort;
                if (a == null || b == null) continue;

                if (!portToNode.TryGetValue(a, out var start)) continue;
                if (!portToNode.TryGetValue(b, out var end)) continue;

                // 若起迄相同，略過
                if (ReferenceEquals(start, end)) continue;

                var gid = rel.GlobalId.Value as string;
                var edge = new SchematicEdge
                {
                    Id = string.IsNullOrWhiteSpace(gid) ? Guid.NewGuid().ToString() : gid,
                    StartNode = start,
                    EndNode = end,
                    Entity = rel as IPersistEntity ?? start.Entity ?? end.Entity
                };
                data.Edges.Add(edge);
            }

            return Task.FromResult(data);
        }

        private static bool TryGetLabel(IIfcElement elem, out int label)
        {
            if (elem is IPersistEntity pe)
            {
                label = pe.EntityLabel;
                return true;
            }
            label = -1;
            return false;
        }

        private static SchematicNode CreateNodeFromElement(IIfcElement elem)
        {
            var root = elem as IIfcRoot;
            var id = root != null ? ((root.GlobalId.Value as string) ?? elem.EntityLabel.ToString()) : elem.EntityLabel.ToString();
            var name = root != null ? (root.Name?.ToString() ?? elem.GetType().Name) : elem.GetType().Name;
            var type = elem.ExpressType?.Name ?? elem.GetType().Name;

            // 3D 位置：先用 LocalPlacement 的 Location（若無則 (0,0,0)）
            var p3 = GetElementPoint(elem);

            return new SchematicNode
            {
                Id = id,
                Name = name,
                IfcType = type,
                Position3D = p3,
                Entity = elem as IPersistEntity ?? default!
            };
        }

        private static Point3D GetElementPoint(IIfcProduct prod)
        {
            // 嘗試從 ObjectPlacement → IfcLocalPlacement → IfcAxis2Placement3D 取得座標
            try
            {
                var lp = prod.ObjectPlacement as IIfcLocalPlacement;
                var rp = lp?.RelativePlacement as IIfcAxis2Placement3D;
                var p = rp?.Location?.Coordinates;
                if (p != null && p.Count >= 3)
                {
                    return new Point3D(p[0], p[1], p[2]);
                }
            }
            catch { /* ignore */ }
            return new Point3D(0, 0, 0);
        }

        private static IEnumerable<IIfcPort> GetPorts(IIfcElement elem)
        {
            // 依照 IFC4，元素可透過 IfcRelConnectsPortToElement 或 IfcRelNests/Assigns 等關係取得 Port；
            // 這裡採常見路徑：遍歷所有與該元素相關聯的 Port 關係。
            // xBIM 提供了便捷屬性：IIfcDistributionElement?.HasPorts 等，在不同 schema 版本命名可能差異，這裡保守實作。
            var ports = new List<IIfcPort>();

            // 1) 直接關聯：IfcRelConnectsPortToElement
            if (elem is IIfcDistributionElement de)
            {
                if (de.HasPorts != null)
                {
                    foreach (var hp in de.HasPorts)
                    {
                        if (hp?.RelatingPort != null) ports.Add(hp.RelatingPort);
                    }
                }
            }

            // 2) 若元素本身是 IIfcElement，嘗試從 RelNests / RelConnects 派生（保守處理）
            // 註：這裡保持最小可行，後續可擴充更多關係。

            return ports.Distinct();
        }
    }
}
