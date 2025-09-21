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
                // Null 檢查：若任一 Port 為 null，記錄警告並跳過
                if (a == null || b == null)
                {
                    try
                    {
                        var relId = rel.GlobalId.Value as string;
                        if (string.IsNullOrWhiteSpace(relId))
                        {
                            relId = (rel as IPersistEntity)?.EntityLabel.ToString() ?? "?";
                        }
                        System.Diagnostics.Trace.WriteLine($"[Service] WARNING: Skipping IfcRelConnectsPorts with ID {relId} due to a null port.");
                    }
                    catch { }
                    continue;
                }

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
                    Entity = rel as IPersistEntity ?? start.Entity ?? end.Entity,
                    IsInferred = false
                };
                data.Edges.Add(edge);
            }

            // 5. 備援：幾何鄰近性（for 缺少 IfcRelConnectsPorts 的模型，如 Sample_pipe.ifc）
            // 規則：對孤立節點（沒有任何邊相連），找出最近鄰節點；若距離 < 閾值（10mm = 0.01m），則推斷為相連並新增邊。
            const double threshold = 0.01; // 10mm in metres

            if (data.Nodes.Count > 1)
            {
                // 建立快速查找結構：依 X 座標排序，加速鄰域掃描
                var nodesByX = data.Nodes.OrderBy(n => n.Position3D.X).ToList();
                var indexByNode = nodesByX
                    .Select((n, i) => (n, i))
                    .ToDictionary(t => t.n, t => t.i);

                bool NodeHasEdge(SchematicNode n)
                {
                    foreach (var e in data.Edges)
                    {
                        if (ReferenceEquals(e.StartNode, n) || ReferenceEquals(e.EndNode, n))
                            return true;
                    }
                    return false;
                }

                foreach (var node in data.Nodes)
                {
                    if (NodeHasEdge(node))
                        continue; // 非孤立

                    if (!indexByNode.TryGetValue(node, out var idx))
                        continue;

                    var p = node.Position3D;
                    double bestDist2 = double.MaxValue;
                    SchematicNode? best = null;

                    // 從排序結果向左右擴散搜尋，使用 X 軸差作為剪枝
                    int left = idx - 1;
                    int right = idx + 1;

                    // 先在一個小視窗內快速搜尋（例如 +/- 16 個），避免 O(n^2)
                    int budget = 16;
                    while (budget > 0 && (left >= 0 || right < nodesByX.Count))
                    {
                        bool advanced = false;
                        if (left >= 0)
                        {
                            var candidate = nodesByX[left];
                            if (!ReferenceEquals(candidate, node))
                            {
                                var dx = Math.Abs(candidate.Position3D.X - p.X);
                                if (dx <= threshold || dx * dx <= bestDist2)
                                {
                                    var d2 = Dist2(p, candidate.Position3D);
                                    if (d2 < bestDist2)
                                    {
                                        bestDist2 = d2;
                                        best = candidate;
                                    }
                                }
                            }
                            left--;
                            advanced = true;
                            budget--;
                        }
                        if (right < nodesByX.Count && budget > 0)
                        {
                            var candidate = nodesByX[right];
                            if (!ReferenceEquals(candidate, node))
                            {
                                var dx = Math.Abs(candidate.Position3D.X - p.X);
                                if (dx <= threshold || dx * dx <= bestDist2)
                                {
                                    var d2 = Dist2(p, candidate.Position3D);
                                    if (d2 < bestDist2)
                                    {
                                        bestDist2 = d2;
                                        best = candidate;
                                    }
                                }
                            }
                            right++;
                            advanced = true;
                            budget--;
                        }
                        if (!advanced) break;
                    }

                    if (best != null)
                    {
                        var bestDist = Math.Sqrt(bestDist2);
                        if (bestDist < threshold)
                        {
                            // 避免重複邊：檢查是否已有 Start-End（無向）
                            bool ExistsEdge(SchematicNode a, SchematicNode b)
                                => data.Edges.Any(e => (ReferenceEquals(e.StartNode, a) && ReferenceEquals(e.EndNode, b))
                                                    || (ReferenceEquals(e.StartNode, b) && ReferenceEquals(e.EndNode, a)));

                            if (!ExistsEdge(node, best))
                            {
                                var edge = new SchematicEdge
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    StartNode = node,
                                    EndNode = best,
                                    // 無 IfcRelConnectsPorts 可對應，回退以端點任一實體做關聯（取 node.Entity 為主）
                                    Entity = node.Entity ?? best.Entity,
                                    IsInferred = true
                                };
                                data.Edges.Add(edge);
                            }
                        }
                    }
                }
            }

            return Task.FromResult(data);
        }

        private static double Dist2(Point3D a, Point3D b)
        {
            var dx = a.X - b.X; var dy = a.Y - b.Y; var dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
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
