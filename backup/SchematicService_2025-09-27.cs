using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Media3D;
using IFC_Viewer_00.Models;
using Xbim.Common;
using Xbim.Ifc;
using Xbim.Ifc4;
using Xbim.Ifc4.Interfaces;
// 設別名：避免在設計期臨時專案解析 Xbim.Ifc.IStepModel 失敗，這裡以 IModel 作為別名使用
using IStepModel = Xbim.Common.IModel;

namespace IFC_Viewer_00.Services
{
    public class SchematicService
    {
        private enum ProjectionPlane { XY, XZ, YZ }
        // 新增：對外公開可選平面枚舉（供 V1 流程使用）
        public enum UserProjectionPlane { XY, XZ, YZ }

        public record PortExtractionStats(
            string SystemName,
            int MemberProducts,
            int ViaHasPorts,
            int ViaNested,
            int ViaFallback,
            int DistinctPorts,
            int ResultPoints
        );

        public PortExtractionStats? LastPortExtractionStats { get; private set; }
        // 供 UI 顯示：每個抽取到的 Port 的詳細資訊
        public IReadOnlyList<PortDetail>? LastPortDetails { get; private set; }

        public record PortDetail(
            int PortLabel,
            string? PortName,
            string? PortType,
            double X,
            double Y,
            double Z,
            int? HostLabel,
            string? HostType,
            string SourcePath // HasPorts / Nested / Fallback
        );

        /// <summary>
        /// 取得指定系統中所有 IfcDistributionPort 的 3D 絕對座標（若無系統成員 Ports，回傳空集合）。
        /// 僅做資料擷取，不進行任何投影或拓撲建構。
        /// </summary>
        public Task<List<Point3D>> GetAllPortCoordinatesAsync(IStepModel ifcModel, IIfcSystem system)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));
            if (system == null) throw new ArgumentNullException(nameof(system));

            return Task.Run(() =>
            {
                var result = new List<Point3D>();
                var details = new List<PortDetail>();
                try
                {
                    // 找出屬於該系統的成員（RelAssignsToGroup.RelatingGroup = system）
                    var assigns = ifcModel.Instances.OfType<IIfcRelAssignsToGroup>()?.ToList() ?? new List<IIfcRelAssignsToGroup>();
                    int sysLbl = (system as IPersistEntity)?.EntityLabel ?? 0;
                    string? sysGid = null; try { sysGid = IfcStringHelper.FromValue(system.GlobalId); } catch { }
                    bool MatchGroup(IIfcGroup? g)
                    {
                        if (g == null) return false;
                        try
                        {
                            if (sysLbl != 0 && (g as IPersistEntity)?.EntityLabel == sysLbl) return true;
                            if (!string.IsNullOrWhiteSpace(sysGid))
                            {
                                string? gid2 = null; try { gid2 = IfcStringHelper.FromValue((g as IIfcRoot)?.GlobalId); } catch { }
                                if (!string.IsNullOrWhiteSpace(gid2) && string.Equals(gid2, sysGid, StringComparison.OrdinalIgnoreCase)) return true;
                            }
                        }
                        catch { }
                        return ReferenceEquals(g, system);
                    }

                    var memberProducts = new HashSet<IIfcProduct>();
                    foreach (var rel in assigns.Where(a => MatchGroup(a.RelatingGroup)))
                    {
                        foreach (var obj in rel.RelatedObjects?.OfType<IIfcProduct>() ?? Enumerable.Empty<IIfcProduct>())
                            memberProducts.Add(obj);
                    }

                    // 從所有成員收集 DistributionPort
                    var allPorts = new List<IIfcDistributionPort>();
                    int viaHasPorts = 0, viaNested = 0, viaFallback = 0;
                    foreach (var prod in memberProducts.OfType<IIfcDistributionElement>())
                    {
                        // Path 1: HasPorts（Revit 有時不輸出）
                        if (prod.HasPorts != null)
                        {
                            foreach (var hp in prod.HasPorts)
                            {
                                if (hp?.RelatingPort is IIfcDistributionPort dp)
                                { allPorts.Add(dp); viaHasPorts++; }
                            }
                        }
                        // Path 2: Nested ports (IfcRelNests) 常見於 Revit 匯出
                        if (prod.IsNestedBy != null)
                        {
                            foreach (var nestRel in prod.IsNestedBy)
                            {
                                foreach (var ro in nestRel.RelatedObjects?.OfType<IIfcDistributionPort>() ?? Enumerable.Empty<IIfcDistributionPort>())
                                { allPorts.Add(ro); viaNested++; }
                            }
                        }
                    }

                    // Path 3 Fallback: 若前兩條途徑空/極少，嘗試全域掃描 DistributionPort，利用 RelNests 的 RelatingObject 回推父產品是否在系統中
                    if (allPorts.Count == 0)
                    {
                        var globalPorts = ifcModel.Instances.OfType<IIfcDistributionPort>();
                        foreach (var gp in globalPorts)
                        {
                            // 往上找 IfcRelNests / IfcRelConnectsPorts -> 其中一端若屬系統產品即可收錄
                            bool belongs = false;
                            try
                            {
                                // 1) 由 Nests 回溯
                                foreach (var rNest in gp.Nests ?? Enumerable.Empty<IIfcRelNests>())
                                {
                                    if (rNest.RelatingObject is IIfcDistributionElement de && memberProducts.Contains(de)) { belongs = true; break; }
                                }
                                if (!belongs)
                                {
                                    // 2) 由 RelConnectsPorts 取得另一 Port，再回溯其宿主
                                    foreach (var rc in gp.ConnectedTo ?? Enumerable.Empty<IIfcRelConnectsPorts>())
                                    {
                                        var other = rc.RelatedPort as IIfcDistributionPort;
                                        if (other != null)
                                        {
                                            foreach (var rNest in other.Nests ?? Enumerable.Empty<IIfcRelNests>())
                                            {
                                                if (rNest.RelatingObject is IIfcDistributionElement de2 && memberProducts.Contains(de2)) { belongs = true; break; }
                                            }
                                            if (belongs) break;
                                        }
                                    }
                                }
                            }
                            catch { }
                            if (belongs) { allPorts.Add(gp); viaFallback++; }
                        }
                    }

                    // 若系統本身未指派任何成員但模型仍有 Ports，決定是否回傳空集合或採用全域；這裡依需求：保持最小責任 → 不做全域 fallback。
                    var distinctPorts = allPorts.Distinct().ToList();
                    foreach (var port in distinctPorts)
                    {
                        var pt = GetPortPoint3D(port);
                        if (pt.HasValue)
                        {
                            result.Add(pt.Value);
                                // 強化：嘗試解析更準確的宿主（優先 ContainedIn，其次 RelNests 回溯，再次從連接的另一端回溯）
                                var hostProd = ResolveHostProductForPort(port) ?? TryGetRelatedProductFromContainedIn(port);
                                bool hostIsSeg = hostProd is IIfcPipeSegment;
                                details.Add(new PortDetail(
                                    (port as IPersistEntity)?.EntityLabel ?? 0,
                                    SafeName(port as IIfcRoot),
                                    port?.ExpressType?.Name,
                                    pt.Value.X, pt.Value.Y, pt.Value.Z,
                                    (hostProd as IPersistEntity)?.EntityLabel,
                                    hostProd?.ExpressType?.Name,
                                    SourceFor(port!)
                                ));
                        }
                        else
                        {
                            // 後援：使用宿主元素位置
                            var host = (ResolveHostProductForPort(port) ?? TryGetRelatedProductFromContainedIn(port)) as IIfcProduct;
                            if (host != null)
                            {
                                var hp = GetElementPoint(host);
                                result.Add(hp);
                                details.Add(new PortDetail(
                                    (port as IPersistEntity)?.EntityLabel ?? 0,
                                    SafeName(port as IIfcRoot),
                                    port?.ExpressType?.Name,
                                    hp.X, hp.Y, hp.Z,
                                    (host as IPersistEntity)?.EntityLabel,
                                    host?.ExpressType?.Name,
                                    "HostPointFallback"
                                ));
                            }
                        }
                    }
                    LastPortExtractionStats = new PortExtractionStats(
                        SystemName: TrySystemName(system),
                        MemberProducts: memberProducts.Count,
                        ViaHasPorts: viaHasPorts,
                        ViaNested: viaNested,
                        ViaFallback: viaFallback,
                        DistinctPorts: distinctPorts.Count,
                        ResultPoints: result.Count);
                    LastPortDetails = details;
                    System.Diagnostics.Trace.WriteLine($"[Service][V1] Ports extracted for system: count={result.Count}, viaHasPorts={viaHasPorts}, viaNested={viaNested}, viaFallback={viaFallback}, memberProducts={memberProducts.Count}, distinct={distinctPorts.Count}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[Service][V1] ERROR extracting ports: {ex.Message}");
                }
                return result;
            });
        }

        private static string SafeName(IIfcRoot? r)
        {
            try { return IfcStringHelper.FromValue(r?.Name) ?? IfcStringHelper.FromValue(r?.GlobalId) ?? string.Empty; } catch { return string.Empty; }
        }

        private static string SourceFor(IIfcDistributionPort port)
        {
            // 粗略：因為我們沒有逐一記錄每個 port 的來源路徑，只能再判斷：
            try
            {
                var host = TryGetRelatedProductFromContainedIn(port);
                if (host is IIfcDistributionElement de)
                {
                    if (de.HasPorts != null && de.HasPorts.Any(h => ReferenceEquals(h.RelatingPort, port))) return "HasPorts";
                    if (de.IsNestedBy != null && de.IsNestedBy.Any(r => r.RelatedObjects?.OfType<IIfcDistributionPort>().Any(p => ReferenceEquals(p, port)) == true)) return "Nested";
                }
            }
            catch { }
            return "Unknown";
        }

        // 強化：解析 Port 的宿主產品。
        // 優先使用 ContainedIn；若無，回溯 RelNests 的 RelatingObject；
        // 若仍無，沿 IfcRelConnectsPorts 的另一端 Port 再回溯其 Nests 宿主。
        private static IIfcProduct? ResolveHostProductForPort(IIfcPort port)
        {
            try
            {
                var host = TryGetRelatedProductFromContainedIn(port);
                if (host != null) return host;

                // 回溯自身 Nests
                foreach (var rNest in (port as IIfcDistributionPort)?.Nests ?? Enumerable.Empty<IIfcRelNests>())
                {
                    if (rNest.RelatingObject is IIfcProduct p) return p;
                }

                // 沿連接關係找到另一端，再回溯其 Nests
                foreach (var rc in (port as IIfcDistributionPort)?.ConnectedTo ?? Enumerable.Empty<IIfcRelConnectsPorts>())
                {
                    var other = ReferenceEquals(rc.RelatingPort, port) ? rc.RelatedPort : rc.RelatingPort;
                    if (other == null) continue;
                    foreach (var rNest in (other as IIfcDistributionPort)?.Nests ?? Enumerable.Empty<IIfcRelNests>())
                    {
                        if (rNest.RelatingObject is IIfcProduct p2) return p2;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string TrySystemName(IIfcSystem sys)
        {
            try { return IfcStringHelper.FromValue(sys.Name) ?? IfcStringHelper.FromValue(sys.GlobalId) ?? "UnnamedSystem"; } catch { return "UnnamedSystem"; }
        }
        // SOP 2.0：系統優先，邏輯優先
        // 針對模型中的每一個 IfcSystem / IfcDistributionSystem 產生獨立的 SchematicData
    public async Task<List<SchematicData>> GenerateFromSystemsAsync(IStepModel ifcModel)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));

            return await Task.Run(() =>
            {
                var results = new List<SchematicData>();

                // 1) 尋找系統實例（IfcSystem 與 IfcDistributionSystem）
                var sysList = new List<IIfcSystem>();
                try { sysList.AddRange(ifcModel.Instances.OfType<IIfcSystem>()); } catch { }
                try { sysList.AddRange(ifcModel.Instances.OfType<IIfcDistributionSystem>()); } catch { }
                // 去重
                sysList = sysList.Distinct().ToList();

                foreach (var sys in sysList)
                {
                    var data = new SchematicData();
                    try
                    {
                        var sysName = IfcStringHelper.FromValue(sys.Name) ?? IfcStringHelper.FromValue(sys.GlobalId) ?? sys.GetType().Name;
                        data.SystemName = sysName;
                        if (sys is IPersistEntity peSys) data.SystemEntity = peSys;
                    }
                    catch { }
                    var nodeMap = new Dictionary<IPersistEntity, SchematicNode>();
                    var memberSet = new HashSet<IPersistEntity>();

                    // a) 透過 IfcRelAssignsToGroup 找到此系統的所有成員（一般為 IfcProduct/IfcElement）
                    try
                    {
                        int sysLabel = (sys as IPersistEntity)?.EntityLabel ?? 0;
                        string? sysGid = null; try { sysGid = IfcStringHelper.FromValue(sys.GlobalId); } catch { sysGid = null; }
                        bool MatchGroup(IIfcGroup? g)
                        {
                            if (g == null) return false;
                            try
                            {
                                if (sysLabel != 0 && (g as IPersistEntity)?.EntityLabel == sysLabel) return true;
                                if (!string.IsNullOrWhiteSpace(sysGid))
                                {
                                    string? gid = null; try { gid = IfcStringHelper.FromValue((g as IIfcRoot)?.GlobalId); } catch { gid = null; }
                                    if (!string.IsNullOrWhiteSpace(gid) && string.Equals(gid, sysGid, StringComparison.OrdinalIgnoreCase)) return true;
                                }
                            }
                            catch { }
                            return ReferenceEquals(g, sys);
                        }

                        var assigns = ifcModel.Instances.OfType<IIfcRelAssignsToGroup>()
                            .Where(r => MatchGroup(r.RelatingGroup))
                            .ToList();
                        foreach (var rel in assigns)
                        {
                            if (rel.RelatedObjects == null) continue;
                            foreach (var obj in rel.RelatedObjects)
                            {
                                if (obj is IPersistEntity pe)
                                {
                                    memberSet.Add(pe);
                                }
                            }
                        }
                    }
                    catch { }

                    // 第一遍：建立所有「非管段」節點（只處理 IfcPipeFitting / IfcFlowTerminal / IfcValve）
                    var portToNode = new Dictionary<IIfcPort, SchematicNode>();
                    foreach (var pe in memberSet)
                    {
                        if (pe is IIfcPipeFitting pf)
                        {
                            var node = CreateNodeFromElement(pf);
                            data.Nodes.Add(node);
                            nodeMap[pe] = node;
                            foreach (var p in GetPorts(pf)) portToNode[p] = node;
                        }
                        else if (pe is IIfcFlowTerminal term)
                        {
                            var node = CreateNodeFromElement(term);
                            data.Nodes.Add(node);
                            nodeMap[pe] = node;
                            foreach (var p in GetPorts(term)) portToNode[p] = node;
                        }
                        else if (pe is IIfcValve valve)
                        {
                            var node = CreateNodeFromElement(valve);
                            data.Nodes.Add(node);
                            nodeMap[pe] = node;
                            foreach (var p in GetPorts(valve)) portToNode[p] = node;
                        }
                        // 其餘成員（含 IfcPipeSegment 與非管線相關元件）在此遍略過
                    }

                    // 預先取出全域的 IfcRelConnectsPorts（後續過濾）
                    var allPortRels = ifcModel.Instances.OfType<IIfcRelConnectsPorts>()?.ToList() ?? new List<IIfcRelConnectsPorts>();

                    // 第二遍：將 IfcPipeSegment 轉換為邊（Edges）
                    foreach (var pe in memberSet)
                    {
                        if (pe is not IIfcPipeSegment seg) continue;

                        // 取得管段的兩端 Port
                        var segPorts = GetPorts(seg).ToList();
                        if (segPorts.Count == 0) continue; // 無Port則略過

                        // 尋找每個 Port 所連至的「其他元件的 Port」，再映射到我們第一遍建立的 Node
                        var connectedNodes = new List<SchematicNode>();
                        foreach (var sp in segPorts)
                        {
                            // 找出關聯關係
                            var rels = allPortRels.Where(r => ReferenceEquals(r.RelatingPort, sp) || ReferenceEquals(r.RelatedPort, sp));
                            foreach (var r in rels)
                            {
                                var other = ReferenceEquals(r.RelatingPort, sp) ? r.RelatedPort : r.RelatingPort;
                                if (other == null) continue;
                                if (portToNode.TryGetValue(other, out var node))
                                {
                                    // 收集第一個命中的節點；避免重複加入
                                    if (!connectedNodes.Contains(node)) connectedNodes.Add(node);
                                }
                            }
                        }

                        if (connectedNodes.Count >= 2)
                        {
                            var start = connectedNodes[0];
                            var end = connectedNodes[1];
                            if (!ReferenceEquals(start, end))
                            {
                                string edgeId = Guid.NewGuid().ToString();
                                try { edgeId = IfcStringHelper.FromValue((seg as IIfcRoot)?.GlobalId) ?? (seg as IPersistEntity)?.EntityLabel.ToString() ?? edgeId; }
                                catch { }

                                var edge = new SchematicEdge
                                {
                                    Id = edgeId,
                                    StartNodeId = start.Id,
                                    EndNodeId = end.Id,
                                    StartNode = start,
                                    EndNode = end,
                                    // 將 IfcPipeSegment 本身作為 Edge.Entity（核心需求）
                                    Entity = seg as IPersistEntity ?? start.Entity,
                                    // Connection 欄位目前不特別使用，沿用同一實體以避免 null
                                    Connection = seg as IPersistEntity ?? start.Entity,
                                    IsInferred = false
                                };
                                data.Edges.Add(edge);
                                start.Edges.Add(edge);
                                end.Edges.Add(edge);
                            }
                        }
                    }

                    // d) 診斷日誌：統計 Nodes/Edges 與型別分布
                    try
                    {
                        var totalNodes = data.Nodes.Count;
                        var totalEdges = data.Edges.Count;
                        var typeGroups = data.Nodes
                            .GroupBy(n => n.IfcType ?? "?")
                            .Select(g => $"{g.Key}({g.Count()})");
                        var typesStr = string.Join(", ", typeGroups);
                        var sysName = data.SystemName ?? (sys as IIfcRoot)?.Name?.ToString() ?? sys.GetType().Name;
                        System.Diagnostics.Trace.WriteLine($"[Service] Topology generated for system '{sysName}'. Nodes: {totalNodes}, Edges: {totalEdges}. Types: {typesStr}.");
                    }
                    catch { }

                    // e) 收集完成後加入結果
                    results.Add(data);
                }

                return results;
            });
        }

        // 核心：從 IFC 模型提取拓撲（僅使用 IfcRelConnectsPorts 建邊；若模型無此關係則回傳無邊的節點集）
        public Task<SchematicData> GenerateTopologyAsync(IModel ifcModel)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));

            var data = new SchematicData();
            var visited = new HashSet<int>(); // 使用 EntityLabel 去重

            // Port → Node 索引
            var portToNode = new Dictionary<IIfcPort, SchematicNode>();

            // 2. 節點：僅聚焦管線相關元件
            //    例如：IfcPipeSegment, IfcPipeFitting, IfcFlowTerminal, IfcValve 等
            var pipeRelated = new List<IIfcElement>();
            pipeRelated.AddRange(ifcModel.Instances.OfType<IIfcPipeSegment>());
            pipeRelated.AddRange(ifcModel.Instances.OfType<IIfcPipeFitting>());
            pipeRelated.AddRange(ifcModel.Instances.OfType<IIfcFlowTerminal>());
            pipeRelated.AddRange(ifcModel.Instances.OfType<IIfcValve>());

            // 可依需要擴充更多型別
            foreach (var elem in pipeRelated)
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
                        var relId = IfcStringHelper.FromValue(rel.GlobalId);
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

                var gid = IfcStringHelper.FromValue(rel.GlobalId);
                var edge = new SchematicEdge
                {
                    Id = string.IsNullOrWhiteSpace(gid) ? Guid.NewGuid().ToString() : gid,
                    StartNodeId = start.Id,
                    EndNodeId = end.Id,
                    StartNode = start,
                    EndNode = end,
                    Entity = rel as IPersistEntity ?? start.Entity ?? end.Entity,
                    IsInferred = false
                };
                data.Edges.Add(edge);
            }

            return Task.FromResult(data);
        }

        // AS原理圖（精簡版）：
        // 1) 以兩段管件的四個 Port 建立參考平面
        // 2) 對齊 WPF Canvas（V 軸取反，使 Y 向下）
        // 3) 只投影這四個 Port 成為黑點
        // 4) 針對每段管件，各自將兩個 Port 連成一條線
        public async Task<SchematicData?> GeneratePortPointSchematicFromSegmentsAsync(IStepModel ifcModel, IIfcPipeSegment seg1, IIfcPipeSegment seg2)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));
            if (seg1 == null || seg2 == null) throw new ArgumentNullException("segment");

            return await Task.Run(() =>
            {
                // 1) 收集四個參考 Port 的 3D 座標（各取每段的兩個端點 Port，若超過兩個取前兩個）
                var p1 = GetPorts(seg1).Take(2).Select(p => GetPortPoint3D(p)).Where(p => p.HasValue).Select(p => p!.Value).ToList();
                var p2 = GetPorts(seg2).Take(2).Select(p => GetPortPoint3D(p)).Where(p => p.HasValue).Select(p => p!.Value).ToList();
                var refPts = new List<Point3D>();
                refPts.AddRange(p1);
                refPts.AddRange(p2);
                if (refPts.Count < 3)
                {
                    // 退化：若不足三點，補點以避免零法向
                    var a = GetElementPoint(seg1);
                    var b = GetElementPoint(seg2);
                    if (refPts.Count == 0) { refPts.Add(a); refPts.Add(b); }
                    if (refPts.Count == 1) { refPts.Add(b); }
                    refPts.Add(new Point3D(refPts[0].X + 0.01, refPts[0].Y + 0.01, refPts[0].Z + 0.01));
                }

                // 2) 以前三點建立當地平面，並對齊 Canvas（V 軸向下）
                var o = refPts[0];
                var v1 = Sub(refPts[1], o);
                var v2 = Sub(refPts[2], o);
                var n = Cross(v1, v2);

                if (Length(n) < 1e-9)
                {
                    // 再退：以最佳全域軸面，但仍只投影兩段管的 Port
                    var plane = BestAxisPlane(new[] { seg1, seg2 });
                    var axes = BuildAxisUV(plane);
                    var uAxisF = axes.u;
                    var vAxisF = new Vector3D(-axes.v.X, -axes.v.Y, -axes.v.Z); // Canvas Y 向下
                    var originF = refPts.FirstOrDefault();
                    return ProjectSelectedSegmentsPortsToData(ifcModel, seg1, seg2, originF, uAxisF, vAxisF);
                }

                var uAxis = Normalize(v1);
                var vAxis = Normalize(Cross(n, uAxis));
                var vCanvas = new Vector3D(-vAxis.X, -vAxis.Y, -vAxis.Z); // Canvas Y 向下

                return ProjectSelectedSegmentsPortsToData(ifcModel, seg1, seg2, o, uAxis, vCanvas);
            });
        }

        private static (Point3D origin, Vector3D u, Vector3D v) BuildAxisUV(ProjectionPlane plane)
        {
            var origin = new Point3D(0, 0, 0);
            return plane switch
            {
                ProjectionPlane.XY => (origin, new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)),
                ProjectionPlane.XZ => (origin, new Vector3D(1, 0, 0), new Vector3D(0, 0, 1)),
                _ => (origin, new Vector3D(0, 1, 0), new Vector3D(0, 0, 1)), // YZ
            };
        }

        private static ProjectionPlane BestAxisPlane(IEnumerable<IIfcElement> elems)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            foreach (var e in elems)
            {
                var p = GetElementPoint(e);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            double rx = Math.Max(1e-12, maxX - minX);
            double ry = Math.Max(1e-12, maxY - minY);
            double rz = Math.Max(1e-12, maxZ - minZ);
            if (rx <= ry && rx <= rz) return ProjectionPlane.YZ;
            if (ry <= rx && ry <= rz) return ProjectionPlane.XZ;
            return ProjectionPlane.XY;
        }

        private static SchematicData ProjectAllSystemPortsToData(IStepModel ifcModel, IIfcPipeSegment seedSegment, Point3D origin, Vector3D u, Vector3D v)
        {
            var data = new SchematicData();
            try
            {
                // 找到 seedSegment 所屬的系統（If RelAssignsToGroup → group = system）
                var systems = new List<IIfcSystem>();
                try
                {
                    var assigns = ifcModel.Instances.OfType<IIfcRelAssignsToGroup>()?.ToList() ?? new List<IIfcRelAssignsToGroup>();
                    int seedLbl = (seedSegment as IPersistEntity)?.EntityLabel ?? 0;
                    foreach (var rel in assigns)
                    {
                        if (rel.RelatingGroup is IIfcSystem sys)
                        {
                            bool containsSeed = rel.RelatedObjects
                                ?.OfType<IPersistEntity>()
                                .Any(o => o != null && o.EntityLabel == seedLbl) == true;
                            if (containsSeed) systems.Add(sys);
                        }
                    }
                }
                catch { }

                IIfcSystem? system = systems.FirstOrDefault();
                if (system != null)
                {
                    data.SystemName = IfcStringHelper.FromValue(system.Name) ?? IfcStringHelper.FromValue(system.GlobalId) ?? system.GetType().Name;
                    if (system is IPersistEntity peSys) data.SystemEntity = peSys;
                }

                // 收集此系統相關的 Port：
                var members = new HashSet<IIfcProduct>();
                if (system != null)
                {
                    try
                    {
                        int sysLabel = (system as IPersistEntity)?.EntityLabel ?? 0;
                        string? sysGid = null; try { sysGid = IfcStringHelper.FromValue(system.GlobalId); } catch { sysGid = null; }
                        bool MatchGroup(IIfcGroup? g)
                        {
                            if (g == null) return false;
                            try
                            {
                                if (sysLabel != 0 && (g as IPersistEntity)?.EntityLabel == sysLabel) return true;
                                if (!string.IsNullOrWhiteSpace(sysGid))
                                {
                                    string? gid = null; try { gid = IfcStringHelper.FromValue((g as IIfcRoot)?.GlobalId); } catch { gid = null; }
                                    if (!string.IsNullOrWhiteSpace(gid) && string.Equals(gid, sysGid, StringComparison.OrdinalIgnoreCase)) return true;
                                }
                            }
                            catch { }
                            return ReferenceEquals(g, system);
                        }

                        var assigns = ifcModel.Instances.OfType<IIfcRelAssignsToGroup>()
                            .Where(r => MatchGroup(r.RelatingGroup))
                            .ToList();
                        foreach (var rel in assigns)
                        {
                            foreach (var obj in rel.RelatedObjects?.OfType<IIfcProduct>() ?? Enumerable.Empty<IIfcProduct>())
                                members.Add(obj);
                        }
                    }
                    catch { }
                }
                // 若仍無法找到成員，退回全模型的配電元件集合（保守）
                if (members.Count == 0)
                {
                    try
                    {
                        foreach (var de in ifcModel.Instances.OfType<IIfcDistributionElement>())
                            members.Add(de);
                    }
                    catch { }
                }

                // 收集所有 Port → Node 的映射
                var portNodeMap = new Dictionary<IIfcPort, SchematicNode>();
                var allPorts = new List<IIfcPort>();
                int portsFromAssigns = 0;
                int portsFromMembers = 0;

                // 1) 若系統存在：直接收集被指派到該系統的 Ports
                try
                {
                    if (system != null)
                    {
                        int sysLabel = (system as IPersistEntity)?.EntityLabel ?? 0;
                        string? sysGid = null; try { sysGid = IfcStringHelper.FromValue(system.GlobalId); } catch {}
                        bool MatchGroup(IIfcGroup? g)
                        {
                            if (g == null) return false;
                            try
                            {
                                if (sysLabel != 0 && (g as IPersistEntity)?.EntityLabel == sysLabel) return true;
                                if (!string.IsNullOrWhiteSpace(sysGid))
                                {
                                    string? gid = null; try { gid = IfcStringHelper.FromValue((g as IIfcRoot)?.GlobalId); } catch {}
                                    if (!string.IsNullOrWhiteSpace(gid) && string.Equals(gid, sysGid, StringComparison.OrdinalIgnoreCase)) return true;
                                }
                            }
                            catch { }
                            return false;
                        }

                        var assigns2 = ifcModel.Instances.OfType<IIfcRelAssignsToGroup>()
                            .Where(r => MatchGroup(r.RelatingGroup))
                            .ToList();
                        foreach (var rel in assigns2)
                        {
                            foreach (var p in rel.RelatedObjects?.OfType<IIfcDistributionPort>() ?? Enumerable.Empty<IIfcDistributionPort>())
                            {
                                allPorts.Add(p);
                                portsFromAssigns++;
                            }
                        }
                    }
                }
                catch { }

                // 2) 後援：從 members 的 HasPorts 取得
                foreach (var m in members)
                {
                    var added = GetPortsFromProduct(m).ToList();
                    portsFromMembers += added.Count;
                    allPorts.AddRange(added);
                }

                // 2.5) 若仍為 0，保底：全模型直接取所有 IfcDistributionPort
                if (allPorts.Count == 0)
                {
                    try
                    {
                        var anyPorts = ifcModel.Instances.OfType<IIfcDistributionPort>()?.ToList() ?? new List<IIfcDistributionPort>();
                        allPorts.AddRange(anyPorts);
                        System.Diagnostics.Trace.WriteLine($"[Service][AS] Fallback: using ALL model ports, count={anyPorts.Count}");
                    }
                    catch { }
                }

                // 建立 2D 點的節點（黑點）
                int idx = 0;
                var distinctPorts = allPorts.Where(p => p != null).Distinct().ToList();
                System.Diagnostics.Trace.WriteLine($"[Service][AS] Ports collected: assigns={portsFromAssigns}, fromMembers={portsFromMembers}, distinctTotal={distinctPorts.Count}");
                foreach (var port in distinctPorts)
                {
                    if (port == null) continue;
                    Point3D pt3;
                    var host = TryGetRelatedProductFromContainedIn(port) as IIfcProduct;
                    var maybe = GetPortPoint3D(port);
                    if (maybe.HasValue)
                        pt3 = maybe.Value;
                    else if (host != null)
                        pt3 = GetElementPoint(host);
                    else
                        pt3 = new Point3D(0, 0, 0);

                    var uv = ProjectToPlane(pt3, origin, u, v);
                    var node = new SchematicNode
                    {
                        Id = $"port:{(port as IPersistEntity)?.EntityLabel ?? ++idx}",
                        Name = IfcStringHelper.FromValue((port as IIfcRoot)?.Name) ?? "Port",
                        IfcType = port?.ExpressType?.Name ?? "IfcPort",
                        Position3D = pt3,
                        Position2D = new System.Windows.Point(uv.X, uv.Y),
                        Entity = (port as IPersistEntity) ?? default!
                    };
                    data.Nodes.Add(node);
                    portNodeMap[port!] = node;
                }

                // 3) 使用 IfcRelConnectsPorts 建立邊（Port 對 Port 的邊）
                int edgesAdded = 0;
                try
                {
                    var relPorts = ifcModel.Instances.OfType<IIfcRelConnectsPorts>()?.ToList() ?? new List<IIfcRelConnectsPorts>();
                    System.Diagnostics.Trace.WriteLine($"[Service][AS] IfcRelConnectsPorts total in model: {relPorts.Count}");
                    foreach (var rp in relPorts)
                    {
                        var a = rp.RelatingPort; var b = rp.RelatedPort;
                        if (a == null || b == null) continue;
                        if (!portNodeMap.TryGetValue(a, out var na) || !portNodeMap.TryGetValue(b, out var nb)) continue;
                        if (ReferenceEquals(na, nb)) continue;
                        string edgeId = IfcStringHelper.FromValue(rp.GlobalId) ?? Guid.NewGuid().ToString();
                        var e = new SchematicEdge
                        {
                            Id = edgeId,
                            StartNode = na, EndNode = nb,
                            StartNodeId = na.Id, EndNodeId = nb.Id,
                            Entity = rp as IPersistEntity ?? default!,
                            Connection = rp as IPersistEntity ?? default!,
                            IsInferred = false
                        };
                        data.Edges.Add(e);
                        na.Edges.Add(e); nb.Edges.Add(e);
                        edgesAdded++;
                    }
                }
                catch { }

                // 若依據目前節點集沒有建立出任何邊，再次保底：
                // 嘗試用所有 IfcRelConnectsPorts，對其兩端若在全模型 port 節點映射中，則建邊。
                if (edgesAdded == 0 && portNodeMap.Count > 0)
                {
                    try
                    {
                        var relPorts = ifcModel.Instances.OfType<IIfcRelConnectsPorts>()?.ToList() ?? new List<IIfcRelConnectsPorts>();
                        foreach (var rp in relPorts)
                        {
                            var a = rp.RelatingPort; var b = rp.RelatedPort;
                            if (a == null || b == null) continue;
                            if (!portNodeMap.TryGetValue(a, out var na) || !portNodeMap.TryGetValue(b, out var nb)) continue;
                            if (ReferenceEquals(na, nb)) continue;
                            string edgeId = IfcStringHelper.FromValue(rp.GlobalId) ?? Guid.NewGuid().ToString();
                            var e = new SchematicEdge
                            {
                                Id = edgeId,
                                StartNode = na, EndNode = nb,
                                StartNodeId = na.Id, EndNodeId = nb.Id,
                                Entity = rp as IPersistEntity ?? default!,
                                Connection = rp as IPersistEntity ?? default!,
                                IsInferred = false
                            };
                            data.Edges.Add(e);
                            na.Edges.Add(e); nb.Edges.Add(e);
                            edgesAdded++;
                        }
                    }
                    catch { }
                }

                System.Diagnostics.Trace.WriteLine($"[Service][AS] Result: Nodes={data.Nodes.Count}, Edges={data.Edges.Count} (edgesAdded={edgesAdded})");
            }
            catch { }
            return data;
        }

        // 僅投影兩段管的四個 Port 並連線（Canvas 對齊：V 軸已倒置）
        private static SchematicData ProjectSelectedSegmentsPortsToData(IStepModel ifcModel, IIfcPipeSegment seg1, IIfcPipeSegment seg2, Point3D origin, Vector3D u, Vector3D vCanvas)
        {
            var data = new SchematicData();
            try
            {
                // 嘗試標記 System 資訊（非必要）
                try
                {
                    var assigns = ifcModel.Instances.OfType<IIfcRelAssignsToGroup>()?.ToList() ?? new List<IIfcRelAssignsToGroup>();
                    int seedLbl = (seg1 as IPersistEntity)?.EntityLabel ?? 0;
                    var sys = assigns.Where(r => r.RelatedObjects?.OfType<IPersistEntity>().Any(o => o.EntityLabel == seedLbl) == true)
                                     .Select(r => r.RelatingGroup as IIfcSystem)
                                     .FirstOrDefault(s => s != null);
                    if (sys != null)
                    {
                        data.SystemName = IfcStringHelper.FromValue(sys.Name) ?? IfcStringHelper.FromValue(sys.GlobalId) ?? sys.GetType().Name;
                        if (sys is IPersistEntity peSys) data.SystemEntity = peSys;
                    }
                }
                catch { }

                var nodes = new List<SchematicNode>();
                var portNodeMap = new Dictionary<IIfcPort, SchematicNode>();

                void addSegmentPorts(IIfcPipeSegment seg)
                {
                    var ports = GetPorts(seg).Take(2).ToList();
                    // 後援：若該段沒有 Port，改從與該段相連的元件上挑選距離最近的 2 個 Port 當端點
                    if (ports.Count < 2)
                    {
                        var fallback = GetNearestPortsFromConnectedElements(ifcModel, seg, 2).ToList();
                        if (fallback.Count > 0)
                        {
                            System.Diagnostics.Trace.WriteLine($"[Service][AS-min] Fallback using connected-elements' ports for seg label={(seg as IPersistEntity)?.EntityLabel}: found {fallback.Count}");
                            ports = fallback.Take(2).ToList();
                        }
                        // 最後保底：全模型中距離該段最近的 Ports
                        if (ports.Count < 2)
                        {
                            var segPt = GetElementPoint(seg as IIfcProduct ?? throw new InvalidOperationException("Segment is not a product"));
                            var globalFallback = GetNearestPortsGlobal(ifcModel, segPt, 2).ToList();
                            if (globalFallback.Count > 0)
                            {
                                System.Diagnostics.Trace.WriteLine($"[Service][AS-min] Fallback using GLOBAL nearest ports for seg label={(seg as IPersistEntity)?.EntityLabel}: found {globalFallback.Count}");
                                ports = globalFallback.Take(2).ToList();
                            }
                        }
                    }
                    int idx = 0;
                    foreach (var port in ports)
                    {
                        if (port == null) continue;
                        var host = TryGetRelatedProductFromContainedIn(port) as IIfcProduct ?? seg as IIfcProduct;
                        var pt3 = GetPortPoint3D(port) ?? (host != null ? GetElementPoint(host) : new Point3D(0, 0, 0));
                        var uv = ProjectToPlane(pt3, origin, u, vCanvas);
                        var node = new SchematicNode
                        {
                            Id = $"port:{(port as IPersistEntity)?.EntityLabel ?? (++idx)}",
                            Name = IfcStringHelper.FromValue((port as IIfcRoot)?.Name) ?? "Port",
                            IfcType = port?.ExpressType?.Name ?? "IfcPort",
                            Position3D = pt3,
                            Position2D = new System.Windows.Point(uv.X, uv.Y),
                            Entity = (port as IPersistEntity) ?? default!
                        };
                        nodes.Add(node);
                        portNodeMap[port!] = node;
                    }
                }

                addSegmentPorts(seg1);
                addSegmentPorts(seg2);
                data.Nodes.AddRange(nodes);

                void connectTwoPorts(IIfcPipeSegment seg)
                {
                    var ports = GetPorts(seg).Take(2).ToList();
                    if (ports.Count < 2)
                    {
                        ports = GetNearestPortsFromConnectedElements(ifcModel, seg, 2).Take(2).ToList();
                        if (ports.Count < 2)
                        {
                            var segPt = GetElementPoint(seg as IIfcProduct ?? throw new InvalidOperationException("Segment is not a product"));
                            ports = GetNearestPortsGlobal(ifcModel, segPt, 2).Take(2).ToList();
                        }
                    }
                    if (ports.Count < 2) return;
                    if (!portNodeMap.TryGetValue(ports[0], out var a)) return;
                    if (!portNodeMap.TryGetValue(ports[1], out var b)) return;
                    if (ReferenceEquals(a, b)) return;

                    string edgeId = IfcStringHelper.FromValue((seg as IIfcRoot)?.GlobalId) ?? (seg as IPersistEntity)?.EntityLabel.ToString() ?? Guid.NewGuid().ToString();
                    var e = new SchematicEdge
                    {
                        Id = edgeId,
                        StartNode = a, EndNode = b,
                        StartNodeId = a.Id, EndNodeId = b.Id,
                        Entity = seg as IPersistEntity ?? default!,
                        Connection = seg as IPersistEntity ?? default!,
                        IsInferred = false
                    };
                    data.Edges.Add(e);
                    a.Edges.Add(e); b.Edges.Add(e);
                }

                connectTwoPorts(seg1);
                connectTwoPorts(seg2);

                System.Diagnostics.Trace.WriteLine($"[Service][AS-min] Result: Nodes={data.Nodes.Count}, Edges={data.Edges.Count} (expected <=4 nodes, 2 edges)");

                // 最終保底：若仍沒有任何節點（常見於模型未建立段的 Ports 與缺少相鄰關係）
                if (data.Nodes.Count == 0)
                {
                    try
                    {
                        var c1 = GetElementPoint(seg1 as IIfcProduct ?? default!);
                        var c2 = GetElementPoint(seg2 as IIfcProduct ?? default!);
                        var near1 = GetNearestPortsGlobal(ifcModel, c1, 2).ToList();
                        var near2 = GetNearestPortsGlobal(ifcModel, c2, 2).ToList();

                        var picked = new List<IIfcPort>();
                        picked.AddRange(near1);
                        picked.AddRange(near2);
                        picked = picked.Where(p => p != null).Distinct().Take(4).ToList();

                        foreach (var port in picked)
                        {
                            var host = TryGetRelatedProductFromContainedIn(port) as IIfcProduct;
                            var pt3 = GetPortPoint3D(port) ?? (host != null ? GetElementPoint(host) : new Point3D(0, 0, 0));
                            var uv = ProjectToPlane(pt3, origin, u, vCanvas);
                            var node = new SchematicNode
                            {
                                Id = $"port:{((port as IPersistEntity)?.EntityLabel.ToString() ?? Guid.NewGuid().ToString())}",
                                Name = IfcStringHelper.FromValue((port as IIfcRoot)?.Name) ?? "Port",
                                IfcType = port?.ExpressType?.Name ?? "IfcPort",
                                Position3D = pt3,
                                Position2D = new System.Windows.Point(uv.X, uv.Y),
                                Entity = (port as IPersistEntity) ?? default!
                            };
                            data.Nodes.Add(node);
                            portNodeMap[port!] = node;
                        }

                        // 仍依規格：各段連一條線（選出該段中心最近的兩點）
                        SchematicNode? pickClosestNode(Point3D center, IEnumerable<SchematicNode> pool, HashSet<SchematicNode> used)
                        {
                            SchematicNode? best = null;
                            double bestD2 = double.PositiveInfinity;
                            foreach (var n in pool)
                            {
                                if (used.Contains(n)) continue;
                                var p = n.Position3D;
                                var d2 = Dist2(p, center);
                                if (d2 < bestD2) { bestD2 = d2; best = n; }
                            }
                            if (best != null) used.Add(best);
                            return best;
                        }

                        void connectByNearest(Point3D center, string edgeId, IPersistEntity? entity)
                        {
                            var used = new HashSet<SchematicNode>();
                            var a = pickClosestNode(center, data.Nodes, used);
                            var b = pickClosestNode(center, data.Nodes, used);
                            if (a == null || b == null || ReferenceEquals(a, b)) return;
                            var e = new SchematicEdge
                            {
                                Id = edgeId,
                                StartNode = a, EndNode = b,
                                StartNodeId = a.Id, EndNodeId = b.Id,
                                Entity = entity ?? default!,
                                Connection = entity ?? default!,
                                IsInferred = true
                            };
                            data.Edges.Add(e);
                            a.Edges.Add(e); b.Edges.Add(e);
                        }

                        connectByNearest(c1, IfcStringHelper.FromValue((seg1 as IIfcRoot)?.GlobalId) ?? Guid.NewGuid().ToString(), seg1 as IPersistEntity);
                        connectByNearest(c2, IfcStringHelper.FromValue((seg2 as IIfcRoot)?.GlobalId) ?? Guid.NewGuid().ToString(), seg2 as IPersistEntity);

                        System.Diagnostics.Trace.WriteLine($"[Service][AS-min][RESCUE] Used global-nearest fallback. Nodes={data.Nodes.Count}, Edges={data.Edges.Count}");
                    }
                    catch { }
                }
            }
            catch { }
            return data;
        }

        // 後援：若管段沒明確 Port，透過 IfcRelConnectsElements 找到相連元件，從其 Port 中挑選距離管段最近的幾個
        private static IEnumerable<IIfcPort> GetNearestPortsFromConnectedElements(IStepModel ifcModel, IIfcElement segment, int count)
        {
            var result = new List<(IIfcPort port, double d2)>();
            try
            {
                var segLabel = (segment as IPersistEntity)?.EntityLabel ?? 0;
                var connects = ifcModel.Instances.OfType<IIfcRelConnectsElements>()?.ToList() ?? new List<IIfcRelConnectsElements>();
                var neighbors = new List<IIfcElement>();
                foreach (var rel in connects)
                {
                    var a = rel.RelatingElement as IIfcElement;
                    var b = rel.RelatedElement as IIfcElement;
                    if (a == null || b == null) continue;
                    if ((a as IPersistEntity)?.EntityLabel == segLabel && !ReferenceEquals(b, segment)) neighbors.Add(b);
                    else if ((b as IPersistEntity)?.EntityLabel == segLabel && !ReferenceEquals(a, segment)) neighbors.Add(a);
                }

                if (neighbors.Count == 0) return Enumerable.Empty<IIfcPort>();

                var segPt = GetElementPoint(segment as IIfcProduct ?? throw new InvalidOperationException("Segment is not a product"));
                foreach (var n in neighbors)
                {
                    foreach (var p in GetPorts(n))
                    {
                        var pt3 = GetPortPoint3D(p) ?? GetElementPoint(n as IIfcProduct ?? segment as IIfcProduct);
                        var d2 = Dist2(pt3, segPt);
                        result.Add((p, d2));
                    }
                }
            }
            catch { }

            return result
                .OrderBy(t => t.d2)
                .Select(t => t.port)
                .Take(Math.Max(0, count));
        }

        // 全模型最近的 Ports（不限制系統）。僅保底使用。
        private static IEnumerable<IIfcPort> GetNearestPortsGlobal(IStepModel ifcModel, Point3D pivot, int count)
        {
            var result = new List<(IIfcPort port, double d2)>();
            try
            {
                var ports = ifcModel.Instances.OfType<IIfcDistributionPort>()?.ToList() ?? new List<IIfcDistributionPort>();
                foreach (var p in ports)
                {
                    var pt3 = GetPortPoint3D(p) ??
                              (TryGetRelatedProductFromContainedIn(p) is IIfcProduct host ? GetElementPoint(host) : new Point3D(0, 0, 0));
                    var d2 = Dist2(pt3, pivot);
                    result.Add((p, d2));
                }
            }
            catch { }

            return result
                .OrderBy(t => t.d2)
                .Select(t => t.port)
                .Take(Math.Max(0, count));
        }

        private static System.Windows.Point ProjectToPlane(Point3D p, Point3D origin, Vector3D u, Vector3D v)
        {
            var w = new Vector3D(p.X - origin.X, p.Y - origin.Y, p.Z - origin.Z);
            double x = Vector3D.DotProduct(w, u);
            double y = Vector3D.DotProduct(w, v);
            return new System.Windows.Point(x, y);
        }

        private static Point3D? GetPortPoint3D(IIfcPort? port)
        {
            if (port == null) return null;
            try
            {
                // 1) 直接嘗試 Port 自身的 ObjectPlacement（若 schema 支援）
                var lp = (port as IIfcProduct)?.ObjectPlacement as IIfcLocalPlacement;
                var rp = lp?.RelativePlacement as IIfcAxis2Placement3D;
                var p = rp?.Location?.Coordinates;
                if (p != null && p.Count >= 3)
                    return new Point3D(p[0], p[1], p[2]);

                // 2) 後援：由 ContainedIn 找宿主元素位置
                var prod = TryGetRelatedProductFromContainedIn(port);
                if (prod is IIfcProduct prod1) return GetElementPoint(prod1);
            }
            catch { }
            return null;
        }

        private static IIfcProduct? TryGetRelatedProductFromContainedIn(IIfcPort port)
        {
            try
            {
                var contained = port.ContainedIn;
                if (contained == null) return null;
                if (contained is IIfcRelConnectsPortToElement single)
                    return single.RelatedElement as IIfcProduct;
                if (contained is IEnumerable<IIfcRelConnectsPortToElement> many)
                    return many.FirstOrDefault()?.RelatedElement as IIfcProduct;
            }
            catch { }
            return null;
        }

        private static Vector3D Sub(Point3D a, Point3D b) => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static Vector3D Cross(Vector3D a, Vector3D b) => Vector3D.CrossProduct(a, b);
        private static double Length(Vector3D v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        private static Vector3D Normalize(Vector3D v) { var len = Length(v); return len > 1e-12 ? new Vector3D(v.X / len, v.Y / len, v.Z / len) : new Vector3D(1, 0, 0); }

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
            var id = root != null ? (IfcStringHelper.FromValue(root.GlobalId) ?? elem.EntityLabel.ToString()) : elem.EntityLabel.ToString();
            var name = root != null ? (IfcStringHelper.FromValue(root.Name) ?? elem.GetType().Name) : elem.GetType().Name;
            var type = elem.ExpressType?.Name ?? elem.GetType().Name;

            // 3D 位置：先用 LocalPlacement 的 Location（若無則 (0,0,0)）
            var p3 = GetElementPoint(elem);
            var p2 = new Point(p3.X, p3.Y);

            return new SchematicNode
            {
                Id = id,
                Name = name,
                IfcType = type,
                Position3D = p3,
                Position2D = p2,
                Entity = elem as IPersistEntity ?? default!
            };
        }

        private static SchematicNode CreateNodeFromProduct(IIfcProduct prod)
        {
            var root = prod as IIfcRoot;
            var id = root != null ? (IfcStringHelper.FromValue(root.GlobalId) ?? (prod as IPersistEntity)?.EntityLabel.ToString() ?? Guid.NewGuid().ToString()) : (prod as IPersistEntity)?.EntityLabel.ToString() ?? Guid.NewGuid().ToString();
            var name = root != null ? (IfcStringHelper.FromValue(root.Name) ?? prod.GetType().Name) : prod.GetType().Name;
            var type = prod.ExpressType?.Name ?? prod.GetType().Name;

            var p3 = GetElementPoint(prod);
            var p2 = new Point(p3.X, p3.Y);

            return new SchematicNode
            {
                Id = id,
                Name = name,
                IfcType = type,
                Position3D = p3,
                Position2D = p2,
                Entity = prod as IPersistEntity ?? default!
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

        private static IEnumerable<IIfcPort> GetPortsFromProduct(IIfcProduct prod)
        {
            // 產品若為配電元件，盡可能透過 HasPorts 取得
            if (prod is IIfcDistributionElement de)
            {
                var ports = new List<IIfcPort>();
                if (de.HasPorts != null)
                {
                    foreach (var hp in de.HasPorts)
                    {
                        if (hp?.RelatingPort != null) ports.Add(hp.RelatingPort);
                    }
                }
                return ports.Distinct();
            }
            return Enumerable.Empty<IIfcPort>();
        }

        // =============================================================
        // 新增：管段軸線（無 Port 亦可）→ 以 IfcExtrudedAreaSolid 深度方向建立線段
        // 使用者可指定平面 (XY/XZ/YZ) 做 2D 投影，結果回傳為 SchematicData：
        //  - 每個 IfcPipeSegment 產生 2 個節點 (Start/End) 與 1 條 Edge
        //  - Node.IfcType 使用管段型別名稱；Edge.Entity 綁定該管段 (可高亮)
        //  - 若無法解析幾何，跳過該段
        // 改善：完整套用 IfcLocalPlacement 鏈的旋轉/平移，並套用 IfcExtrudedAreaSolid.Position 的座標軸與位置，
        //      將 ExtrudedDirection 正確轉換至世界座標，避免水平管誤畫成垂直線。
        // =============================================================
        public Task<SchematicData> GeneratePipeAxesAsync(IModel ifcModel, string plane, bool flipY = true)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));
            return Task.Run(() =>
            {
                var data = new SchematicData();
                var planeNorm = (plane ?? "XY").Trim().ToUpperInvariant();
                if (planeNorm != "XY" && planeNorm != "XZ" && planeNorm != "YZ") planeNorm = "XY";

                var segments = ifcModel.Instances.OfType<IIfcPipeSegment>()?.ToList() ?? new List<IIfcPipeSegment>();
                int segCount = 0, segSkipped = 0;
                foreach (var seg in segments)
                {
                    try
                    {
                        // 取得幾何：找 SweptSolid 下的 IfcExtrudedAreaSolid
                        var body = seg.Representation?.Representations?
                            .FirstOrDefault(r => string.Equals(r.RepresentationType, "SweptSolid", StringComparison.OrdinalIgnoreCase));
                        var extrude = body?.Items?.OfType<IIfcExtrudedAreaSolid>().FirstOrDefault();
                        if (extrude == null)
                        {
                            segSkipped++; continue;
                        }

                        // 1) 先計算 product 的世界座標基底（含平移與旋轉）
                        var productBasis = GetBasisFromPlacement(seg.ObjectPlacement as IIfcObjectPlacement);

                        // 2) 套用 solid 的 Position（若有）到 product 基底，得到該 solid 的世界基底
                        var pos = extrude.Position as IIfcAxis2Placement3D;
                        var solidBasis = pos != null ? ApplyAxis2Placement(productBasis, pos) : productBasis;

                        // 3) 計算起點（世界座標）：即 solid 基底的原點
                        var start3 = solidBasis.O;

                        // 4) 計算拉伸方向（世界座標）：將 ExtrudedDirection 以 solid 基底座標轉成世界向量
                        var dLocal = GetDirectionVector(extrude.ExtrudedDirection) ?? new Vector3D(0, 0, 1);
                        var dWorld = dLocal.X * solidBasis.U + dLocal.Y * solidBasis.V + dLocal.Z * solidBasis.W;
                        if (dWorld.LengthSquared < 1e-16) dWorld = solidBasis.W; // 後援：用基底 Z 軸
                        dWorld.Normalize();
                        var end3 = new Point3D(start3.X + dWorld.X * extrude.Depth,
                                               start3.Y + dWorld.Y * extrude.Depth,
                                               start3.Z + dWorld.Z * extrude.Depth);

                        // 2D 投影
                        (double px, double py) proj(Point3D p)
                        {
                            return planeNorm switch
                            {
                                "XZ" => (p.X, flipY ? -p.Z : p.Z),
                                "YZ" => (p.Y, flipY ? -p.Z : p.Z),
                                _ => (p.X, flipY ? -p.Y : p.Y),
                            };
                        }
                        var p2dStart = proj(start3); var p2dEnd = proj(end3);

                        string segName = IfcStringHelper.FromValue(seg.Name) ?? IfcStringHelper.FromValue((seg as IIfcRoot)?.GlobalId) ?? "PipeSegment";
                        string segId = IfcStringHelper.FromValue((seg as IIfcRoot)?.GlobalId) ?? (seg as IPersistEntity)?.EntityLabel.ToString() ?? Guid.NewGuid().ToString();

                        var startNode = new SchematicNode
                        {
                            Id = segId + ":S",
                            Name = segName + "-Start",
                            IfcType = seg.ExpressType?.Name ?? "IfcPipeSegment",
                            Position3D = start3,
                            Position2D = new Point(p2dStart.px, p2dStart.py),
                            Entity = (seg as IPersistEntity)!,
                            HostIfcType = seg.ExpressType?.Name,
                            HostLabel = (seg as IPersistEntity)?.EntityLabel
                        };
                        var endNode = new SchematicNode
                        {
                            Id = segId + ":E",
                            Name = segName + "-End",
                            IfcType = seg.ExpressType?.Name ?? "IfcPipeSegment",
                            Position3D = end3,
                            Position2D = new Point(p2dEnd.px, p2dEnd.py),
                            Entity = (seg as IPersistEntity)!,
                            HostIfcType = seg.ExpressType?.Name,
                            HostLabel = (seg as IPersistEntity)?.EntityLabel
                        };
                        data.Nodes.Add(startNode); data.Nodes.Add(endNode);

                        var edge = new SchematicEdge
                        {
                            Id = segId,
                            StartNode = startNode,
                            EndNode = endNode,
                            StartNodeId = startNode.Id,
                            EndNodeId = endNode.Id,
                            Entity = (seg as IPersistEntity)!,
                            Connection = (seg as IPersistEntity)!,
                            IsInferred = false
                        };
                        data.Edges.Add(edge);
                        startNode.Edges.Add(edge); endNode.Edges.Add(edge);
                        segCount++;
                    }
                    catch
                    {
                        segSkipped++;
                    }
                }

                System.Diagnostics.Trace.WriteLine($"[Service][PipeAxes] Segments processed={segments.Count}, linesBuilt={segCount}, skipped={segSkipped}, plane={planeNorm}");
                data.SystemName = $"PipeAxes({planeNorm})";
                return data;
            });
        }

        // ======== 幾何輔助：Placement/Basis 與方向處理 ========
        private struct Basis3D
        {
            public Point3D O; // 原點（世界座標）
            public Vector3D U; // X 軸（世界向量）
            public Vector3D V; // Y 軸（世界向量）
            public Vector3D W; // Z 軸（世界向量）
        }

        private static Basis3D IdentityBasis()
            => new Basis3D
            {
                O = new Point3D(0, 0, 0),
                U = new Vector3D(1, 0, 0),
                V = new Vector3D(0, 1, 0),
                W = new Vector3D(0, 0, 1)
            };

        // 從 IIfcObjectPlacement 取得世界基底（遞迴沿 PlacementRelTo 鏈組合）
        private static Basis3D GetBasisFromPlacement(IIfcObjectPlacement? placement)
        {
            if (placement == null) return IdentityBasis();
            if (placement is IIfcLocalPlacement lp)
            {
                var parent = lp.PlacementRelTo != null ? GetBasisFromPlacement(lp.PlacementRelTo) : IdentityBasis();
                var rp = lp.RelativePlacement as IIfcAxis2Placement3D;
                return rp != null ? ApplyAxis2Placement(parent, rp) : parent;
            }
            return IdentityBasis();
        }

        // 將 Axis2Placement3D 套用在 parent 基底上，回傳新的世界基底
        private static Basis3D ApplyAxis2Placement(Basis3D parent, IIfcAxis2Placement3D axis)
        {
            // 抽取 Axis/RefDirection 與 Location
            var loc = axis.Location?.Coordinates;
            var axisDir = axis.Axis?.DirectionRatios;         // Z 軸方向
            var refDir = axis.RefDirection?.DirectionRatios;  // X 軸參考方向

            // parent 座標系下的向量值
            Vector3D Zp = parent.W; // 預設 Z
            if (axisDir != null && axisDir.Count >= 3)
            {
                var z = new Vector3D(axisDir[0], axisDir[1], axisDir[2]);
                // 轉為世界：z_world = z.X*Up + z.Y*Vp + z.Z*Wp
                Zp = z.X * parent.U + z.Y * parent.V + z.Z * parent.W;
            }
            if (Zp.LengthSquared < 1e-16) Zp = parent.W;
            Zp.Normalize();

            Vector3D Xp = parent.U; // 預設 X
            if (refDir != null && refDir.Count >= 3)
            {
                var x = new Vector3D(refDir[0], refDir[1], refDir[2]);
                Xp = x.X * parent.U + x.Y * parent.V + x.Z * parent.W;
            }
            // Gram-Schmidt：確保 X 與 Z 正交
            Xp = Xp - Vector3D.Multiply(Vector3D.DotProduct(Xp, Zp), Zp);
            if (Xp.LengthSquared < 1e-16)
            {
                // 若退化，選擇一個與 Z 不平行的向量
                Xp = Math.Abs(Zp.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
                Xp = Xp - Vector3D.Multiply(Vector3D.DotProduct(Xp, Zp), Zp);
            }
            Xp.Normalize();
            var Yp = Vector3D.CrossProduct(Zp, Xp); // 右手座標系
            Yp.Normalize();

            // 位置：parent 原點加上 parent 基底縮放的位移
            double lx = 0, ly = 0, lz = 0;
            if (loc != null && loc.Count >= 3) { lx = loc[0]; ly = loc[1]; lz = loc[2]; }
            var O = new Point3D(
                parent.O.X + parent.U.X * lx + parent.V.X * ly + parent.W.X * lz,
                parent.O.Y + parent.U.Y * lx + parent.V.Y * ly + parent.W.Y * lz,
                parent.O.Z + parent.U.Z * lx + parent.V.Z * ly + parent.W.Z * lz
            );

            return new Basis3D { O = O, U = Xp, V = Yp, W = Zp };
        }

        // 從 IfcDirection 取向量
        private static Vector3D? GetDirectionVector(IIfcDirection? dir)
        {
            try
            {
                var r = dir?.DirectionRatios;
                if (r == null || r.Count == 0) return null;
                double x = 0, y = 0, z = 0;
                if (r.Count > 0) x = r[0];
                if (r.Count > 1) y = r[1];
                if (r.Count > 2) z = r[2];
                var v = new Vector3D(x, y, z);
                if (v.LengthSquared < 1e-16) return null;
                return v;
            }
            catch { return null; }
        }
    }
}
