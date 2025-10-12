using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
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
        // 是否啟用：穿越配件連線（ThroughFitting / Rewire）
        // 說明：在 Ports-only 的拓撲上，若某個 IfcPipeFitting 節點的度數=2，
        // 會將其兩端相鄰節點以一條推導邊直接相連（視覺上將 elbow 視為一段連續 segment）。
        // 預設為關閉以避免影響既有視覺。
        public bool ThroughFittingRewireEnabled { get; set; } = false;
        // === Projection coverage report (diagnostics for 2D canvas placement) ===
        public record Element2DPoint(
            int Label,
            string IfcType,
            string? Name,
            double X2D,
            double Y2D,
            double XCanvas,
            double YCanvas,
            bool InCanvas
        );

        public record ProjectionCoverageReport(
            double CanvasWidth,
            double CanvasHeight,
            double Padding,
            string ProjectionPlane,
            int Total,
            int InCount,
            int OutCount,
            int Segments,
            int Fittings,
            int Terminals,
            IReadOnlyList<Element2DPoint> Samples
        );

        // 快速版管網建構參數
        public class PipeNetworkOptions
        {
            public bool IncludeFittings { get; set; } = true;
            public bool UsePorts { get; set; } = true;
            public double MergeToleranceMm { get; set; } = 50.0; // 3D 距離閾值（mm）
            public bool CollapseEndpoints { get; set; } = true;
            public bool AddSegmentEdgesIfNoPorts { get; set; } = true;
            public int MaxFittingStarDegree { get; set; } = 8; // P1 參數化
            public int MaxFittingPairs { get; set; } = 20;
            public bool PropagateSystemFromNeighbors { get; set; } = true; // 新增：鄰接單一系統自動填補
            // 新增：以畫布平面為主的 2D 幾何補橋（優先以 2D 投影距離判斷）
            public UserProjectionPlane? CanvasPlane { get; set; } = null; // null=沿現有策略（若為 null 則仍可採 3D）
            public double PlanarToleranceMm { get; set; } = 500.0; // 2D 平面距離閾值（mm）
            public bool Use2DGeometryFallback { get; set; } = true; // 幾何補橋改以 2D 為主
            public bool EnableStarFittingRewire { get; set; } = false; // 預設關閉，避免非預期連線
            public BuildModeKind BuildMode { get; set; } = BuildModeKind.Full;
        }

        public enum BuildModeKind { Full, RewiredOnly }

        // 建圖統計回報
        public class GraphBuildReport
        {
            public int TotalNodes { get; set; }
            public int TotalEdges { get; set; }
            public int PortEdges { get; set; }
            public int GeometryEdges { get; set; }
            public int SegmentEdges { get; set; }
            public int RewiredEdges { get; set; }
            public int CollapsedEndpoints { get; set; }
            public int Systems { get; set; }
            public int Runs { get; set; }
            public int ConvertedSegments { get; set; }
            public int EndpointNodes { get; set; }
            public bool UsedPorts { get; set; }
            public double BuildMs { get; set; }
            public string? Notes { get; set; }
            // Integrity metrics
            public int CrossSystemEdges { get; set; }
            public int IsolatedNodes { get; set; }
            public double FittingAvgDegree { get; set; }
            public int FittingMaxDegree { get; set; }
            public int RunMaxNodes { get; set; }
            public double RunAvgNodes { get; set; }
            public int PropagatedSystemAssignments { get; set; } // 新增：鄰接系統自動填補的節點數
            public double? SegmentAvgLengthMm { get; set; }
            public double? TolMm { get; set; }
        }

    // 最近一次建圖的資料快取（供離線 Quick 使用）
    public SchematicData? LastBuiltData { get; private set; }

        // 快速版：建立管網節點 / 邊（Ports 優先 + 幾何補橋）
    public async Task<(SchematicData data, GraphBuildReport report)> BuildPipeNetworkAsync(IModel model, PipeNetworkOptions? options = null)
        {
            var opt = options ?? new PipeNetworkOptions();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = new SchematicData();
            var report = new GraphBuildReport();
            await Task.Yield(); // 允許方法真正以非同步進入，避免 CS1998 警告

            // 1) 收集元素（Segments / Fittings / Valves / Terminals）
            //    現階段先以單節點代表元素；Segment 端點細化延後到 Fitting 插入階段（避免前期複雜度）。
            //    若後續需要端點語意，可在資料層新增 2 端點拆分函式。
            var elements = model.Instances.OfType<IIfcElement>().ToList();
            var elementToNode = new Dictionary<IPersistEntity, SchematicNode>();
            foreach (var e in elements)
            {
                try
                {
                    // 篩掉非相關類型（僅保留管線相關 Distribution） — 簡化：若 IncludeFittings=false 可跳過 Fitting/Valve。
                    var t = e.ExpressType?.Name ?? string.Empty;
                    bool isFitting = t.Contains("FlowFitting", StringComparison.OrdinalIgnoreCase) || t.EndsWith("Fitting", StringComparison.OrdinalIgnoreCase);
                    bool isValve = t.Contains("Valve", StringComparison.OrdinalIgnoreCase) || t.Contains("FlowController", StringComparison.OrdinalIgnoreCase);
                    if ((isFitting || isValve) && !opt.IncludeFittings) continue;

                    var node = CreateNodeFromElement(e);
                    // SystemKey 初步：使用 SystemAbbreviation or SystemName（稍後可擴充真正的系統關聯解析）
                    node.SystemKey = node.SystemAbbreviation ?? node.SystemName ?? "(未指定)";
                    elementToNode[e as IPersistEntity ?? throw new InvalidOperationException()] = node;
                    data.Nodes.Add(node);
                }
                catch { }
            }

            // 1.1) Segment 端點拆分（方案 2: 使用 MergeToleranceMm 作為虛擬長度 L） + 建立 A-B SegmentEdge (附長度/直徑統計)
            try
            {
                double virtualLenMm = Math.Max(1e-3, opt.MergeToleranceMm); // 使用者選擇方案 2
                double scale = GetLengthToMillimetreScale(model); // model 單位 -> mm 的比例 (例: m -> 1000)
                double mmToModel = scale > 1e-9 ? 1.0 / scale : 1.0; // mm 轉模型單位
                double half = (virtualLenMm * mmToModel) * 0.5;
                var segmentCenters = data.Nodes.Where(n => n.NodeKind == SchematicNode.SchematicNodeKind.PipeEnd && !n.IsSegmentEndpoint).ToList();
                var newNodes = new List<SchematicNode>();
                int converted = 0;
                int segEdges = 0;
                // 嘗試為每個 segment 抓取元件直徑（因 node.Entity 指回原 PipeSegment）
                foreach (var seg in segmentCenters)
                {
                    seg.IsSegmentCenter = true;
                    var basePos = seg.Position3D;
                    var aPos = new Point3D(basePos.X - half, basePos.Y, basePos.Z);
                    var bPos = new Point3D(basePos.X + half, basePos.Y, basePos.Z);
                    var aNode = new SchematicNode
                    {
                        Id = seg.Id + "_A",
                        Name = seg.Name + "-A",
                        IfcType = seg.IfcType,
                        Position3D = aPos,
                        Position2D = new System.Windows.Point(aPos.X, aPos.Y),
                        Entity = seg.Entity ?? default!,
                        NodeKind = SchematicNode.SchematicNodeKind.PipeEnd,
                        SystemKey = seg.SystemKey,
                        SystemName = seg.SystemName,
                        SystemAbbreviation = seg.SystemAbbreviation,
                        SystemType = seg.SystemType,
                        LevelName = seg.LevelName,
                        IsSegmentEndpoint = true
                    };
                    var bNode = new SchematicNode
                    {
                        Id = seg.Id + "_B",
                        Name = seg.Name + "-B",
                        IfcType = seg.IfcType,
                        Position3D = bPos,
                        Position2D = new System.Windows.Point(bPos.X, bPos.Y),
                        Entity = seg.Entity ?? default!,
                        NodeKind = SchematicNode.SchematicNodeKind.PipeEnd,
                        SystemKey = seg.SystemKey,
                        SystemName = seg.SystemName,
                        SystemAbbreviation = seg.SystemAbbreviation,
                        SystemType = seg.SystemType,
                        LevelName = seg.LevelName,
                        IsSegmentEndpoint = true
                    };
                    newNodes.Add(aNode);
                    newNodes.Add(bNode);
                    if (seg.Entity != null)
                    {
                        elementToNode[seg.Entity] = aNode; // 暫時綁定第一端點供後續 Ports 邏輯
                    }
                    // 建立 Segment A-B 邊 (Origin=Segment)（含虛擬長度 / 直徑若可）
                    double? nom=null, outer=null; string? srcNom=null, srcOuter=null;
                    try { if (seg.Entity is IIfcPipeSegment pipeSeg) ExtractDiameters(model, pipeSeg, out nom, out outer, out srcNom, out srcOuter); } catch { }
                    var segEdge = new SchematicEdge
                    {
                        Id = $"S_{aNode.Id}_{bNode.Id}_{data.Edges.Count + segEdges}",
                        StartNode = aNode,
                        EndNode = bNode,
                        StartNodeId = aNode.Id,
                        EndNodeId = bNode.Id,
                        IsInferred = false,
                        Origin = SchematicEdge.EdgeOriginKind.Segment,
                        SystemName = aNode.SystemName ?? bNode.SystemName,
                        SystemAbbreviation = aNode.SystemAbbreviation ?? bNode.SystemAbbreviation,
                        SystemType = aNode.SystemType ?? bNode.SystemType,
                        NominalDiameterMm = nom,
                        OuterDiameterMm = outer,
                        ValueSourceNominalDiameter = srcNom,
                        ValueSourceOuterDiameter = srcOuter,
                        LengthMm = virtualLenMm
                    };
                    aNode.Edges.Add(segEdge);
                    bNode.Edges.Add(segEdge);
                    data.Edges.Add(segEdge);
                    segEdges++;
                    converted++;
                }
                if (converted > 0)
                {
                    foreach (var center in segmentCenters)
                        data.Nodes.Remove(center);
                    foreach (var nn in newNodes)
                        data.Nodes.Add(nn);
                    report.ConvertedSegments = converted;
                    report.EndpointNodes = newNodes.Count;
                    report.SegmentEdges += segEdges;
                    if (segEdges > 0)
                    {
                        // 目前每條虛擬段長度相同 virtualLenMm，可直接設定平均長度
                        report.SegmentAvgLengthMm = virtualLenMm; // 若日後改為真實長度，需改成 totalSegLenMm/segEdges
                    }
                }
            }
            catch (Exception segEx)
            {
                report.Notes = (report.Notes == null ? string.Empty : report.Notes + ";") + $"SegmentSplitError:{segEx.Message}";
            }

            // 2) Ports 邊（若啟用）
            if (opt.UsePorts)
            {
                try
                {
                    var rels = model.Instances.OfType<IIfcRelConnectsPorts>().ToList();
                    foreach (var rel in rels)
                    {
                        var p1 = rel.RelatingPort as IIfcPort;
                        var p2 = rel.RelatedPort as IIfcPort;
                        if (p1 == null || p2 == null) continue;
                        // 更健壯：不再強制要求 ContainedIn；使用 ResolveHostProductForPort 解析宿主
                        var host1 = ResolveHostProductForPort(p1);
                        var host2 = ResolveHostProductForPort(p2);
                        if (host1 == null || host2 == null || ReferenceEquals(host1, host2)) continue;
                        if (elementToNode.TryGetValue(host1 as IPersistEntity ?? default!, out var n1) &&
                            elementToNode.TryGetValue(host2 as IPersistEntity ?? default!, out var n2))
                        {
                            var edge = new SchematicEdge
                            {
                                Id = $"E_{n1.Id}_{n2.Id}_{data.Edges.Count}",
                                StartNode = n1,
                                EndNode = n2,
                                StartNodeId = n1.Id,
                                EndNodeId = n2.Id,
                                Connection = rel as IPersistEntity ?? default!,
                                Entity = rel as IPersistEntity ?? default!,
                                IsInferred = false,
                                Origin = SchematicEdge.EdgeOriginKind.Ports,
                                SystemName = n1.SystemName ?? n2.SystemName,
                                SystemAbbreviation = n1.SystemAbbreviation ?? n2.SystemAbbreviation,
                                SystemType = n1.SystemType ?? n2.SystemType
                            };
                            data.Edges.Add(edge);
                            n1.Edges.Add(edge);
                            n2.Edges.Add(edge);
                            report.PortEdges++;
                        }
                    }
                    report.UsedPorts = report.PortEdges > 0;
                }
                catch { }
            }

            // 3) 幾何補橋（依選項，可改為依 CanvasPlane 的 2D 距離判定）
            try
            {
                if (opt.BuildMode != BuildModeKind.RewiredOnly)
                {
                    // 選 2D 或 3D 容差
                    double tolMm = Math.Max(1e-3, opt.Use2DGeometryFallback ? opt.PlanarToleranceMm : opt.MergeToleranceMm);
                    double tol2 = tolMm * tolMm;
                // 依 SystemKey 分桶
                var buckets = data.Nodes.GroupBy(n => n.SystemKey ?? "(未指定)");
                foreach (var bucket in buckets)
                {
                    var list = bucket.ToList();
                    if (list.Count <= 1) continue;
                    // 建立已有度數判斷：孤立節點優先補
                    // 先建立 spatial hash：以 tolMm 為 cell 尺寸
                    double cell = tolMm;
                    var grid = new Dictionary<(int,int,int), List<SchematicNode>>();
                    (int,int,int) keyFor(SchematicNode n)
                    {
                        var p = n.Position3D; return ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell), (int)Math.Floor(p.Z / cell));
                    }
                    void addToGrid(SchematicNode n)
                    {
                        var k = keyFor(n); if (!grid.TryGetValue(k, out var lst)) { lst = new List<SchematicNode>(); grid[k] = lst; } lst.Add(n);
                    }
                    foreach (var n in list) addToGrid(n);

                    var existingEdgeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in data.Edges)
                    {
                        // 無方向 key
                        var a = e.StartNodeId; var b = e.EndNodeId;
                        if (string.Compare(a, b, StringComparison.Ordinal) > 0) (a, b) = (b, a);
                        existingEdgeSet.Add($"{a}__{b}");
                    }

                        var addedPairs = 0;
                    foreach (var n in list)
                    {
                        // 只對度數=0 或 度數過低的節點做鄰近補橋（避免全圖完全平方級）
                        int deg = n.Edges.Count;
                        if (deg > 2) continue;
                        var pk = keyFor(n);
                        for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            var nk = (pk.Item1 + dx, pk.Item2 + dy, pk.Item3 + dz);
                            if (!grid.TryGetValue(nk, out var candidates)) continue;
                            foreach (var m in candidates)
                            {
                                if (ReferenceEquals(n, m)) continue;
                                // 相同元素避免多餘邊
                                var aId = n.Id; var bId = m.Id;
                                if (string.Compare(aId, bId, StringComparison.Ordinal) > 0) (aId, bId) = (bId, aId);
                                var key = $"{aId}__{bId}";
                                if (existingEdgeSet.Contains(key)) continue;
                                    bool within;
                                    if (opt.Use2DGeometryFallback && opt.CanvasPlane.HasValue)
                                    {
                                        // 以畫布平面計算 2D 距離
                                        static (double u,double v) proj(Point3D p, UserProjectionPlane plane)
                                            => plane switch { UserProjectionPlane.XZ => (p.X, p.Z), UserProjectionPlane.YZ => (p.Y, p.Z), _ => (p.X, p.Y) };
                                        var p1 = proj(n.Position3D, opt.CanvasPlane.Value);
                                        var p2 = proj(m.Position3D, opt.CanvasPlane.Value);
                                        var du = p1.u - p2.u; var dv = p1.v - p2.v;
                                        within = (du*du + dv*dv) <= tol2;
                                    }
                                    else
                                    {
                                        within = Dist2(n.Position3D, m.Position3D) <= tol2;
                                    }
                                    if (!within) continue;
                                // 建立補橋邊
                                var edge = new SchematicEdge
                                {
                                    Id = $"G_{aId}_{bId}_{data.Edges.Count}",
                                    StartNode = n,
                                    EndNode = m,
                                    StartNodeId = n.Id,
                                    EndNodeId = m.Id,
                                    IsInferred = true,
                                    Origin = SchematicEdge.EdgeOriginKind.Geometry,
                                    SystemName = n.SystemName ?? m.SystemName,
                                    SystemAbbreviation = n.SystemAbbreviation ?? m.SystemAbbreviation,
                                    SystemType = n.SystemType ?? m.SystemType
                                };
                                data.Edges.Add(edge);
                                n.Edges.Add(edge);
                                m.Edges.Add(edge);
                                existingEdgeSet.Add(key);
                                report.GeometryEdges++;
                                addedPairs++;
                                if (addedPairs > 1000 && list.Count > 5000) break; // 粗略節流，避免爆炸
                            }
                            if (addedPairs > 1000 && list.Count > 5000) break;
                        }
                        if (addedPairs > 1000 && list.Count > 5000) break;
                    }
                }
                }
            }
            catch (Exception geoEx)
            {
                report.Notes = (report.Notes == null ? string.Empty : report.Notes + ";") + $"GeometryFallbackError:{geoEx.Message}";
            }

            // 4) Fitting 重寫 (策略 B)：
            //    允許在距離內(<=tol)的端點/節點集合中，若兩節點之間尚無 a--b 直邊也可主動建立 a-fit 與 fit-b 組合，
            //    但需控制：
            //      * 每個 fitting 最大 star 度數 (maxStarDegree)
            //      * 每個 fitting 產生的 (a,b) 配對數上限 (maxPairs)
            //      * 避免重複邊 / 自連
            try
            {
                if (opt.EnableStarFittingRewire && opt.BuildMode != BuildModeKind.RewiredOnly)
                {
                double tol = Math.Max(1e-3, opt.MergeToleranceMm);
                double tol2 = tol * tol;
                var fittings = data.Nodes.Where(n => n.NodeKind == SchematicNode.SchematicNodeKind.Fitting).ToList();
                if (fittings.Count > 0)
                {
                    // 建立鄰接快速查詢
                    var edgeSet = new HashSet<string>(data.Edges.Select(e =>
                    {
                        var a = e.StartNodeId; var b = e.EndNodeId; if (string.Compare(a, b, StringComparison.Ordinal) > 0) (a, b) = (b, a); return $"{a}__{b}";
                    }), StringComparer.OrdinalIgnoreCase);
                    var edgesToAdd = new List<SchematicEdge>();
                    int maxStarDegree = Math.Max(1, opt.MaxFittingStarDegree);   // 每個 fitting 最多連幾個外部節點（>=1）
                    int maxPairs = Math.Max(1, opt.MaxFittingPairs);             // 每個 fitting 最多產生多少 (a,b) 組合（>=1）
                    foreach (var fit in fittings)
                    {
                        var near = data.Nodes.Where(n => !ReferenceEquals(n, fit) && n.SystemKey == fit.SystemKey && Dist2(n.Position3D, fit.Position3D) <= tol2).ToList();
                        if (near.Count < 1) continue;
                        // 依距離排序，取前 maxStarDegree
                        near = near.OrderBy(n => Dist2(n.Position3D, fit.Position3D)).Take(maxStarDegree).ToList();

                        // 先建立 star 邊 (node - fit) 如果不存在
                        var starEnds = new List<SchematicNode>();
                        foreach (var n in near)
                        {
                            var x1 = n.Id; var x2 = fit.Id; if (string.Compare(x1, x2, StringComparison.Ordinal) > 0) (x1, x2) = (x2, x1);
                            var k = $"{x1}__{x2}";
                            if (edgeSet.Contains(k)) continue; // 已存在任一方向邊
                            var eStar = new SchematicEdge
                            {
                                Id = $"R_{x1}_{x2}_{data.Edges.Count + edgesToAdd.Count}",
                                StartNode = n,
                                EndNode = fit,
                                StartNodeId = n.Id,
                                EndNodeId = fit.Id,
                                IsInferred = true,
                                Origin = SchematicEdge.EdgeOriginKind.Rewired,
                                SystemName = n.SystemName ?? fit.SystemName,
                                SystemAbbreviation = n.SystemAbbreviation ?? fit.SystemAbbreviation,
                                SystemType = n.SystemType ?? fit.SystemType
                            };
                            edgesToAdd.Add(eStar);
                            edgeSet.Add(k);
                            starEnds.Add(n);
                            report.RewiredEdges++;
                        }

                        // 再嘗試為 star 端點之間（透過 fitting）建立「隱式」替換語意：
                        // 若存在直接 a--b Ports/Geometry 邊，理論上可刪除；策略 B 先不刪除，僅在缺時保留 star。
                        // （保守：不刪原邊，以免失去長度/方向資訊。）
                        // 可選：計數對數，不實際新增 a-b 邊。
                        int pairCount = 0;
                        for (int i = 0; i < starEnds.Count && pairCount < maxPairs; i++)
                        {
                            for (int j = i + 1; j < starEnds.Count && pairCount < maxPairs; j++)
                            {
                                // 僅計數，不新增新邊；未來若需要可插入特殊 EdgeOriginKind (e.g., ThroughFitting)
                                pairCount++;
                            }
                        }
                    }
                    if (edgesToAdd.Count > 0)
                    {
                        foreach (var ad in edgesToAdd)
                        {
                            data.Edges.Add(ad);
                            ad.StartNode.Edges.Add(ad);
                            ad.EndNode.Edges.Add(ad);
                        }
                    }
                }
                }
            }
            catch (Exception fitEx)
            {
                report.Notes = (report.Notes == null ? string.Empty : report.Notes + ";") + $"FittingRewireError:{fitEx.Message}";
            }

            // 4.5)（新增）穿越配件連線：當啟用旗標時，對於度數=2 的配件，直接將兩端鄰居節點以 Rewired 邊相連。
            // 注意：此為 P4 Quick 的補強，與 Ports-only 流程中的 ThroughFitting 行為對齊。
            if (ThroughFittingRewireEnabled)
            {
                try
                {
                    var existingPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in data.Edges)
                    {
                        var a = e.StartNodeId; var b = e.EndNodeId;
                        if (string.Compare(a, b, StringComparison.Ordinal) > 0) (a, b) = (b, a);
                        existingPairs.Add($"{a}__{b}");
                    }

                    int created = 0;
                    var degree2Fittings = data.Nodes
                        .Where(n => n.NodeKind == SchematicNode.SchematicNodeKind.Fitting && n.Edges.Count == 2)
                        .ToList();
                    foreach (var fit in degree2Fittings)
                    {
                        var e1 = fit.Edges[0];
                        var e2 = fit.Edges[1];
                        var n1 = ReferenceEquals(e1.StartNode, fit) ? e1.EndNode : e1.StartNode;
                        var n2 = ReferenceEquals(e2.StartNode, fit) ? e2.EndNode : e2.StartNode;
                        if (n1 == null || n2 == null) continue;
                        if (ReferenceEquals(n1, n2)) continue;

                        var aId = n1.Id; var bId = n2.Id;
                        if (string.Compare(aId, bId, StringComparison.Ordinal) > 0) (aId, bId) = (bId, aId);
                        var key = $"{aId}__{bId}";
                        if (existingPairs.Contains(key)) continue; // 已有更高優先邊（Segment/Ports）

                        var edge = new SchematicEdge
                        {
                            Id = $"TF_{aId}_{bId}_{data.Edges.Count}",
                            StartNode = n1,
                            EndNode = n2,
                            StartNodeId = n1.Id,
                            EndNodeId = n2.Id,
                            Connection = fit.Entity ?? default!,
                            Entity = fit.Entity ?? default!,
                            IsInferred = true,
                            Origin = SchematicEdge.EdgeOriginKind.Rewired,
                            SystemName = fit.SystemName ?? n1.SystemName ?? n2.SystemName,
                            SystemAbbreviation = fit.SystemAbbreviation ?? n1.SystemAbbreviation ?? n2.SystemAbbreviation,
                            SystemType = fit.SystemType ?? n1.SystemType ?? n2.SystemType
                        };
                        if (opt.BuildMode != BuildModeKind.RewiredOnly)
                        {
                            // Full 模式：以補邊加入，之後由優先序去重
                            data.Edges.Add(edge);
                        }
                        else
                        {
                            // RewiredOnly：只加入 Rewired，不會有 Segment/Ports/Geometry
                            data.Edges.Add(edge);
                        }
                        n1.Edges.Add(edge);
                        n2.Edges.Add(edge);
                        existingPairs.Add(key);
                        created++;
                    }
                    if (created > 0)
                    {
                        report.RewiredEdges += created;
                        data.Metadata["ThroughFittingPairsCreated"] = created.ToString();
                    }
                }
                catch (Exception tfEx)
                {
                    report.Notes = (report.Notes == null ? string.Empty : report.Notes + ";") + $"ThroughFittingError:{tfEx.Message}";
                }
            }

            // 5) 系統抽取（以 IfcRelAssignsToGroup 為主，回填更精準的 SystemKey / Name / Abbreviation / Type）
            try
            {
                var assignsAll = model.Instances.OfType<IIfcRelAssignsToGroup>()?.ToList() ?? new List<IIfcRelAssignsToGroup>();
                // 建立 Element -> Systems (多系統時先選第一個，可擴充)
                var elementToSystem = new Dictionary<IPersistEntity, IIfcSystem>();
                foreach (var rel in assignsAll)
                {
                    if (rel.RelatingGroup is IIfcSystem sys && rel.RelatedObjects != null)
                    {
                        foreach (var robj in rel.RelatedObjects)
                        {
                            if (robj is IPersistEntity pe && !elementToSystem.ContainsKey(pe))
                            {
                                elementToSystem[pe] = sys;
                            }
                        }
                    }
                }
                foreach (var kv in elementToNode)
                {
                    var elem = kv.Key; var node = kv.Value;
                    if (elementToSystem.TryGetValue(elem, out var sys))
                    {
                        var (sName, sAbbr, sType) = GetSystemMetadata(sys);
                        node.SystemName = sName;
                        node.SystemAbbreviation = sAbbr;
                        node.SystemType = sType;
                        node.SystemKey = sAbbr ?? sName ?? node.SystemKey;
                    }
                    // 若仍未取得 SystemKey，保持原值或設為 "(未指定)"
                    node.SystemKey ??= node.SystemAbbreviation ?? node.SystemName ?? "(未指定)";
                }
                // 5.1 SystemKey 正規化 (去頭尾空白, collapse 空白, 全大寫)
                foreach (var n in data.Nodes)
                {
                    if (!string.IsNullOrWhiteSpace(n.SystemKey))
                        n.SystemKey = NormalizeSystemKey(n.SystemKey);
                    else
                        n.SystemKey = "(未指定)";
                }
                // 5.2 系統鄰接傳播（單一鄰居系統一致時）
                if (opt.PropagateSystemFromNeighbors)
                {
                    int propagated = 0;
                    foreach (var node in data.Nodes)
                    {
                        if (node.SystemKey != "(未指定)") continue;
                        var neighborKeys = node.Edges
                            .Select(e => ReferenceEquals(e.StartNode, node) ? e.EndNode : e.StartNode)
                            .Where(nn => nn != null)
                            .Select(nn => nn!.SystemKey)
                            .Where(k => !string.IsNullOrWhiteSpace(k) && k != "(未指定)")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(2)
                            .ToList();
                        if (neighborKeys.Count == 1)
                        {
                            node.SystemKey = neighborKeys[0]!;
                            propagated++;
                        }
                    }
                    if (propagated > 0) report.PropagatedSystemAssignments = propagated;
                }

                // 5.3（新增）邊的系統回填：若邊缺少 SystemAbbreviation/Name，從兩端節點回填（避免被系統過濾隱藏）
                try
                {
                    foreach (var e in data.Edges)
                    {
                        bool missingSys = string.IsNullOrWhiteSpace(e.SystemAbbreviation) && string.IsNullOrWhiteSpace(e.SystemName);
                        if (!missingSys) continue;
                        var a = e.StartNode; var b = e.EndNode;
                        // 先取縮寫，若皆空再取名稱；優先非「(未指定)」
                        string? abbr = null;
                        if (!string.IsNullOrWhiteSpace(a?.SystemAbbreviation) && !string.Equals(a!.SystemAbbreviation, "(未指定)", StringComparison.OrdinalIgnoreCase)) abbr = a!.SystemAbbreviation;
                        if (string.IsNullOrWhiteSpace(abbr) && !string.IsNullOrWhiteSpace(b?.SystemAbbreviation) && !string.Equals(b!.SystemAbbreviation, "(未指定)", StringComparison.OrdinalIgnoreCase)) abbr = b!.SystemAbbreviation;

                        string? name = null;
                        if (!string.IsNullOrWhiteSpace(a?.SystemName) && !string.Equals(a!.SystemName, "(未指定)", StringComparison.OrdinalIgnoreCase)) name = a!.SystemName;
                        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(b?.SystemName) && !string.Equals(b!.SystemName, "(未指定)", StringComparison.OrdinalIgnoreCase)) name = b!.SystemName;

                        e.SystemAbbreviation ??= abbr;
                        e.SystemName ??= name;
                        // 若仍為空，最後以 a/b 任一現值回填（可能為 "(未指定)"）
                        e.SystemAbbreviation ??= a?.SystemAbbreviation ?? b?.SystemAbbreviation;
                        e.SystemName ??= a?.SystemName ?? b?.SystemName;
                        e.SystemType ??= a?.SystemType ?? b?.SystemType;
                    }
                }
                catch { }
            }
            catch (Exception sysEx)
            {
                report.Notes = (report.Notes == null ? string.Empty : report.Notes + ";") + $"SystemExtractError:{sysEx.Message}";
            }

            // 6) Run (Connected Components) 計算：以 SystemKey 先分桶，再做 BFS/DFS (蒐集 run 節點數)
            try
            {
                int globalRunSeed = 0;
                var systemBuckets = data.Nodes.GroupBy(n => n.SystemKey ?? "(未指定)");
                var runCounts = new List<int>();
                foreach (var bucket in systemBuckets)
                {
                    var unvisited = new HashSet<SchematicNode>(bucket);
                    while (unvisited.Count > 0)
                    {
                        var start = unvisited.First();
                        unvisited.Remove(start);
                        int runId = ++globalRunSeed;
                        var stack = new Stack<SchematicNode>();
                        stack.Push(start);
                        start.RunId = runId;
                        int rc = 0;
                        while (stack.Count > 0)
                        {
                            var curr = stack.Pop();
                            rc++;
                            foreach (var e in curr.Edges)
                            {
                                var other = e.StartNode == curr ? e.EndNode : e.StartNode;
                                if (!ReferenceEquals(curr.SystemKey, other.SystemKey)) continue; // 不跨系統
                                if (other.RunId == 0)
                                {
                                    other.RunId = runId;
                                    if (unvisited.Remove(other)) stack.Push(other);
                                }
                                // 邊 RunId 指派（若雙端皆已確立 run）
                                if (curr.RunId == runId && other.RunId == runId)
                                {
                                    e.RunId = runId;
                                }
                            }
                        }
                        runCounts.Add(rc);
                    }
                }
                report.Runs = data.Nodes.Select(n => n.RunId).Where(id => id > 0).Distinct().Count();
                if (runCounts.Count > 0)
                {
                    report.RunMaxNodes = runCounts.Max();
                    report.RunAvgNodes = runCounts.Average();
                }
            }
            catch (Exception runEx)
            {
                report.Notes = (report.Notes == null ? string.Empty : report.Notes + ";") + $"RunCalcError:{runEx.Message}";
            }

            // 7) 邊去重與優先排序 (Segment>Ports>Rewired>Geometry) + 統計重算
            try
            {
                var priority = new Dictionary<SchematicEdge.EdgeOriginKind, int>
                {
                    { SchematicEdge.EdgeOriginKind.Segment, 0 },
                    { SchematicEdge.EdgeOriginKind.Ports, 1 },
                    { SchematicEdge.EdgeOriginKind.Rewired, 2 },
                    { SchematicEdge.EdgeOriginKind.Geometry, 3 }
                };
                var pairBest = new Dictionary<string, SchematicEdge>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in data.Edges)
                {
                    var a = e.StartNodeId; var b = e.EndNodeId; if (string.Compare(a, b, StringComparison.Ordinal) > 0) (a, b) = (b, a);
                    var key = a + "__" + b;
                    if (!pairBest.TryGetValue(key, out var exist)) pairBest[key] = e; else if (priority[e.Origin] < priority[exist.Origin]) pairBest[key] = e;
                }
                if (pairBest.Count != data.Edges.Count)
                {
                    foreach (var n in data.Nodes) n.Edges.Clear();
                    data.Edges.Clear();
                    foreach (var kv in pairBest)
                    {
                        var e = kv.Value; data.Edges.Add(e); e.StartNode.Edges.Add(e); e.EndNode.Edges.Add(e);
                    }
                }
                report.PortEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Ports);
                report.GeometryEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Geometry);
                report.SegmentEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Segment);
                report.RewiredEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Rewired);
            }
            catch (Exception dedupEx)
            {
                report.Notes = (report.Notes == null ? string.Empty : report.Notes + ";") + $"DedupError:{dedupEx.Message}";
            }

            // 8) Integrity 檢查（去重後）
            try
            {
                report.CrossSystemEdges = data.Edges.Count(e => !string.Equals(e.StartNode.SystemKey, e.EndNode.SystemKey, StringComparison.OrdinalIgnoreCase));
                report.IsolatedNodes = data.Nodes.Count(n => n.Edges.Count == 0);
                var fittingNodes = data.Nodes.Where(n => n.NodeKind == SchematicNode.SchematicNodeKind.Fitting).ToList();
                if (fittingNodes.Count > 0)
                {
                    report.FittingMaxDegree = fittingNodes.Max(n => n.Edges.Count);
                    report.FittingAvgDegree = fittingNodes.Average(n => n.Edges.Count);
                }
                var cross = data.Edges.Where(e => !string.Equals(e.StartNode.SystemKey, e.EndNode.SystemKey, StringComparison.OrdinalIgnoreCase))
                                       .Take(20)
                                       .Select(e => new { e.StartNodeId, e.EndNodeId, SK1 = e.StartNode.SystemKey, SK2 = e.EndNode.SystemKey, e.Origin })
                                       .ToList();
                if (cross.Count > 0)
                {
                    data.Metadata["CrossSystemEdges"] = System.Text.Json.JsonSerializer.Serialize(cross);
                }
            }
            catch (Exception integEx)
            {
                report.Notes = (report.Notes == null ? string.Empty : report.Notes + ";") + $"IntegrityError:{integEx.Message}";
            }

            // TODO: 幾何補橋、SegmentEdges、Fitting 插入、Run 計算、Report 填寫
            report.TotalNodes = data.Nodes.Count;
            report.TotalEdges = data.Edges.Count;
            report.TolMm = opt.MergeToleranceMm;
            report.Systems = data.Nodes.Select(n => n.SystemKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            sw.Stop();
            report.BuildMs = sw.Elapsed.TotalMilliseconds;
            // 補充 notes
            var notesParts = new List<string>();
            if (!report.UsedPorts) notesParts.Add("NoPortEdgesOrPortsDisabled");
            notesParts.Add($"Strategy=Ports+GeomFallback+FittingRewire+SegmentSplit");
            notesParts.Add($"Runs={report.Runs}");
            if (report.ConvertedSegments > 0) notesParts.Add($"SegmentSplit={report.ConvertedSegments}");
            notesParts.Add($"CrossSysEdges={report.CrossSystemEdges}");
            if (report.IsolatedNodes > 0) notesParts.Add($"Isolated={report.IsolatedNodes}");
            if (report.TolMm.HasValue) notesParts.Add($"TolMm={report.TolMm:0.#}");
            notesParts.Add($"StarConfig={opt.MaxFittingStarDegree}/{opt.MaxFittingPairs}");
            if (report.RunMaxNodes > 0) notesParts.Add($"RunMaxNodes={report.RunMaxNodes}");
            if (report.RunAvgNodes > 0) notesParts.Add($"RunAvgNodes={report.RunAvgNodes:0.#}");
            if (report.SegmentAvgLengthMm.HasValue) notesParts.Add($"SegAvgLenMm={report.SegmentAvgLengthMm:0.#}");
            if (!string.IsNullOrWhiteSpace(report.Notes)) notesParts.Add(report.Notes);
            report.Notes = string.Join(";", notesParts.Distinct());
            data.Metadata["GraphBuildReport"] = System.Text.Json.JsonSerializer.Serialize(report);
            // 標記可作為離線種子並快取
            try { data.HasOfflineRewireSeed = true; } catch { }
            LastBuiltData = data;
            return (data, report);
        }

        /// <summary>
        /// 使用現有的 SchematicData（通常來自先前有模型的建構結果）進行離線重建/重接線，
        /// 不需要 IModel。僅在資料具有足夠種子資訊時可得到實質變更。
        /// </summary>
        public Task<(SchematicData data, GraphBuildReport report)> BuildPipeNetworkFromSeedAsync(SchematicData seed, PipeNetworkOptions? options = null)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            var opt = options ?? new PipeNetworkOptions();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 基於傳入 seed 複製（淺拷貝節點與邊引用，避免破壞呼叫端）
            var data = new SchematicData();
            foreach (var n in seed.Nodes) data.Nodes.Add(n);
            foreach (var e in seed.Edges) data.Edges.Add(e);
            foreach (var lv in seed.Levels) data.Levels.Add(lv);
            foreach (var kv in seed.Metadata) data.Metadata[kv.Key] = kv.Value;
            data.SystemName = seed.SystemName; data.SystemEntity = seed.SystemEntity;

            var report = new GraphBuildReport();

            // 只做離線 ThroughFitting：度數=2 的 Fitting 以 Rewired 補邊（若開啟）
            if (ThroughFittingRewireEnabled)
            {
                try
                {
                    var existingPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in data.Edges)
                    {
                        var a = e.StartNodeId; var b = e.EndNodeId;
                        if (string.Compare(a, b, StringComparison.Ordinal) > 0) (a, b) = (b, a);
                        existingPairs.Add($"{a}__{b}");
                    }
                    int created = 0;
                    var deg2Fits = data.Nodes.Where(n => n.NodeKind == SchematicNode.SchematicNodeKind.Fitting && n.Edges.Count == 2).ToList();
                    foreach (var fit in deg2Fits)
                    {
                        var e1 = fit.Edges[0];
                        var e2 = fit.Edges[1];
                        var n1 = ReferenceEquals(e1.StartNode, fit) ? e1.EndNode : e1.StartNode;
                        var n2 = ReferenceEquals(e2.StartNode, fit) ? e2.EndNode : e2.StartNode;
                        if (n1 == null || n2 == null) continue;
                        if (ReferenceEquals(n1, n2)) continue;
                        var aId = n1.Id; var bId = n2.Id;
                        if (string.Compare(aId, bId, StringComparison.Ordinal) > 0) (aId, bId) = (bId, aId);
                        var key = $"{aId}__{bId}";
                        if (existingPairs.Contains(key)) continue;
                        var edge = new SchematicEdge
                        {
                            Id = $"TF_{aId}_{bId}_{data.Edges.Count}",
                            StartNode = n1,
                            EndNode = n2,
                            StartNodeId = n1.Id,
                            EndNodeId = n2.Id,
                            IsInferred = true,
                            Origin = SchematicEdge.EdgeOriginKind.Rewired,
                            SystemName = fit.SystemName ?? n1.SystemName ?? n2.SystemName,
                            SystemAbbreviation = fit.SystemAbbreviation ?? n1.SystemAbbreviation ?? n2.SystemAbbreviation,
                            SystemType = fit.SystemType ?? n1.SystemType ?? n2.SystemType,
                            SourceTag = "ThroughFitting",
                            RewiredViaFittingLabels = new int[] { fit.HostLabel ?? 0 }
                        };
                        data.Edges.Add(edge);
                        n1.Edges.Add(edge); n2.Edges.Add(edge);
                        existingPairs.Add(key);
                        created++;
                    }
                    report.RewiredEdges = created;
                    if (created > 0) data.Metadata["OfflineThroughFitting"] = created.ToString();
                }
                catch { }
            }

            // 邊去重與優先排序
            try
            {
                var priority = new Dictionary<SchematicEdge.EdgeOriginKind, int>
                {
                    { SchematicEdge.EdgeOriginKind.Segment, 0 },
                    { SchematicEdge.EdgeOriginKind.Ports, 1 },
                    { SchematicEdge.EdgeOriginKind.Rewired, 2 },
                    { SchematicEdge.EdgeOriginKind.Geometry, 3 }
                };
                var pairBest = new Dictionary<string, SchematicEdge>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in data.Edges)
                {
                    var a = e.StartNodeId; var b = e.EndNodeId; if (string.Compare(a, b, StringComparison.Ordinal) > 0) (a, b) = (b, a);
                    var key = a + "__" + b;
                    if (!pairBest.TryGetValue(key, out var exist)) pairBest[key] = e; else if (priority[e.Origin] < priority[exist.Origin]) pairBest[key] = e;
                }
                if (pairBest.Count != data.Edges.Count)
                {
                    foreach (var n in data.Nodes) n.Edges.Clear();
                    data.Edges.Clear();
                    foreach (var kv in pairBest)
                    {
                        var e = kv.Value; data.Edges.Add(e); e.StartNode.Edges.Add(e); e.EndNode.Edges.Add(e);
                    }
                }
            }
            catch { }

            // 簡化統計
            report.TotalNodes = data.Nodes.Count;
            report.TotalEdges = data.Edges.Count;
            report.PortEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Ports);
            report.GeometryEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Geometry);
            report.SegmentEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Segment);
            report.RewiredEdges += data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Rewired);
            report.Systems = data.Nodes.Select(n => n.SystemKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            sw.Stop(); report.BuildMs = sw.Elapsed.TotalMilliseconds;
            data.HasOfflineRewireSeed = true; // 標記可離線重建
            LastBuiltData = data;
            return Task.FromResult((data, report));
        }

        /// <summary>
        /// 以「配件為中心」從既有 SchematicData 生成配件星狀連線：
        /// 對每一個 Fitting 節點，建立所有「鄰接端點」到該 Fitting 的邊（Origin=Rewired）。
        /// 僅輸出這些邊（不包含原本的 Segment/Ports/Geometry），以供 UI 做檢核視圖。
        /// 此流程不依賴 IModel，不改變節點座標，等同沿用目前畫布投影平面。
        /// </summary>
        public Task<(SchematicData data, GraphBuildReport report)> BuildFittingNetworkFromSeedAsync(SchematicData seed)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = new SchematicData();
            // 保留全部節點與樓層/中繼資料（不變動座標）；邊將重建為配件星狀
            foreach (var n in seed.Nodes) data.Nodes.Add(n);
            foreach (var lv in seed.Levels) data.Levels.Add(lv);
            foreach (var kv in seed.Metadata) data.Metadata[kv.Key] = kv.Value;
            data.SystemName = seed.SystemName; data.SystemEntity = seed.SystemEntity;

            // 建立鄰接索引（以 seed.Edges 為準）
            var adj = new Dictionary<SchematicNode, List<SchematicNode>>();
            void addAdj(SchematicNode a, SchematicNode b)
            {
                if (!adj.TryGetValue(a, out var list)) { list = new List<SchematicNode>(); adj[a] = list; }
                if (!list.Contains(b)) list.Add(b);
            }
            foreach (var e in seed.Edges)
            {
                if (e.StartNode == null || e.EndNode == null) continue;
                addAdj(e.StartNode, e.EndNode);
                addAdj(e.EndNode, e.StartNode);
            }

            // 產生星狀邊：Neighbor → FittingCenter
            int created = 0;
            int fitCount = 0;
            int maxDegree = 0;
            foreach (var fit in seed.Nodes.Where(n => n.NodeKind == SchematicNode.SchematicNodeKind.Fitting))
            {
                fitCount++;
                if (!adj.TryGetValue(fit, out var neigh)) continue;
                maxDegree = Math.Max(maxDegree, neigh.Count);
                // 建立集合避免重複邊（同對多次）
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var nb in neigh)
                {
                    if (ReferenceEquals(nb, fit)) continue;
                    var a = nb.Id; var b = fit.Id; if (string.Compare(a, b, StringComparison.Ordinal) > 0) (a, b) = (b, a);
                    var key = $"{a}__{b}";
                    if (existing.Contains(key)) continue;
                    var edge = new SchematicEdge
                    {
                        Id = $"FH_{nb.Id}_{fit.Id}_{data.Edges.Count}",
                        StartNode = nb,
                        EndNode = fit,
                        StartNodeId = nb.Id,
                        EndNodeId = fit.Id,
                        IsInferred = true,
                        Origin = SchematicEdge.EdgeOriginKind.Rewired,
                        SystemName = nb.SystemName ?? fit.SystemName,
                        SystemAbbreviation = nb.SystemAbbreviation ?? fit.SystemAbbreviation,
                        SystemType = nb.SystemType ?? fit.SystemType,
                        SourceTag = "FittingHub"
                    };
                    data.Edges.Add(edge);
                    nb.Edges.Add(edge);
                    fit.Edges.Add(edge);
                    existing.Add(key);
                    created++;
                }
            }

            var report = new GraphBuildReport
            {
                TotalNodes = data.Nodes.Count,
                TotalEdges = data.Edges.Count,
                RewiredEdges = data.Edges.Count,
                Systems = data.Nodes.Select(n => n.SystemKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                FittingMaxDegree = maxDegree,
                FittingAvgDegree = fitCount > 0 ? (double)created / Math.Max(1, fitCount) : 0
            };
            sw.Stop(); report.BuildMs = sw.Elapsed.TotalMilliseconds;
            try { BackfillEdgeSystems(data); } catch { }
            try
            {
                // 穩定輸出：排序節點/邊（有助於重跑一致性）
                var sortedNodes = data.Nodes.OrderBy(n => n.HostLabel ?? 0).ThenBy(n => n.Id).ToList();
                var sortedEdges = data.Edges.OrderBy(e => e.Id).ToList();
                data.Nodes.Clear(); data.Nodes.AddRange(sortedNodes);
                data.Edges.Clear(); data.Edges.AddRange(sortedEdges);
            }
            catch { }
            try { report.Notes = string.IsNullOrWhiteSpace(report.Notes) ? "FromSeed(FittingHub)" : (report.Notes + ";FromSeed(FittingHub)"); } catch { }
            data.HasOfflineRewireSeed = true;
            LastBuiltData = data;
            return Task.FromResult((data, report));
        }

        /// <summary>
        /// 從 IModel 直接建立「配件為中心」的星狀連線：以配件的 Ports 作為端點（投影至指定平面），
        /// 並連到該配件中心（LocalPlacement 為主）。預設僅輸出 Elbow；可擴充 Tee/Cross。
        /// 回傳的資料將只包含這些星狀邊以及必要節點（不混入 Segment/Ports 直連）。
        /// </summary>
        public Task<(SchematicData data, GraphBuildReport report)> BuildFittingNetworkFromModelAsync(IModel model, UserProjectionPlane plane, bool onlyElbow = true, bool flipY = true)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            return Task.Run<(SchematicData, GraphBuildReport)>(() =>
            {
                var data = new SchematicData();
                var report = new GraphBuildReport();

                // 收集樣本點以偵測退化投影（使用配件中心與其 Ports 為主）
                var samplePts = new List<Point3D>(256);
                try
                {
                    foreach (var pf0 in model.Instances.OfType<IIfcPipeFitting>())
                    {
                        try { samplePts.Add(GetElementPoint(pf0 as IIfcProduct)); } catch { }
                        try
                        {
                            foreach (var dp in GetPorts(pf0).OfType<IIfcDistributionPort>())
                            {
                                var p = GetPortPoint3D(dp); if (p.HasValue) samplePts.Add(p.Value);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                bool planeWasDegenerate = false;
                var finalPlane = ChooseBestPlaneForPoints(samplePts, plane, out planeWasDegenerate);

                (double u, double v) Proj(Point3D p, UserProjectionPlane pl)
                {
                    var (uu, vv) = pl switch { UserProjectionPlane.XZ => (p.X, p.Z), UserProjectionPlane.YZ => (p.Y, p.Z), _ => (p.X, p.Y) };
                    if (flipY) vv = -vv; // Canvas Y 向下，翻轉以符合既有視圖
                    return (uu, vv);
                }

                int created = 0;
                int fitCount = 0;
                int maxDeg = 0;

                // 快取：若同一個配件/Port 被多次使用，避免重建節點
                var centerMap = new Dictionary<int, SchematicNode>(); // fitting label → center node
                var portMap = new Dictionary<int, SchematicNode>();   // port label → endpoint node

                IEnumerable<IIfcPipeFitting> fittings;
                try { fittings = model.Instances.OfType<IIfcPipeFitting>().ToList(); }
                catch { fittings = Enumerable.Empty<IIfcPipeFitting>(); }

                foreach (var pf in fittings)
                {
                    fitCount++;
                    int fitLbl = (pf as IPersistEntity)?.EntityLabel ?? 0;

                    // 類型判斷：優先 PredefinedType，其次名稱包含 Elbow
                    bool isElbow = false;
                    try
                    {
                        var pt = (pf as dynamic)?.PredefinedType; // IfcPipeFittingTypeEnum
                        string? pts = pt?.ToString();
                        if (!string.IsNullOrWhiteSpace(pts) && pts.Contains("ELBOW", StringComparison.OrdinalIgnoreCase)) isElbow = true;
                    }
                    catch { }
                    if (!isElbow)
                    {
                        try
                        {
                            var name = IfcStringHelper.FromValue((pf as IIfcRoot)?.Name) ?? string.Empty;
                            if (name.IndexOf("elbow", StringComparison.OrdinalIgnoreCase) >= 0) isElbow = true;
                        }
                        catch { }
                    }
                    if (onlyElbow && !isElbow) continue;

                    // 中心點（LocalPlacement → 投影）
                    Point3D center3 = GetElementPoint(pf as IIfcProduct);
                    var (cu, cv) = Proj(center3, finalPlane);
                    var center = new SchematicNode
                    {
                        Id = $"F_{fitLbl}",
                        Name = IfcStringHelper.FromValue((pf as IIfcRoot)?.Name) ?? ($"Fitting_{fitLbl}"),
                        IfcType = pf.ExpressType?.Name ?? "IfcPipeFitting",
                        Position3D = center3,
                        Position2D = new System.Windows.Point(cu, cv),
                        Entity = pf as IPersistEntity,
                        NodeKind = SchematicNode.SchematicNodeKind.Fitting
                    };
                    // 補系統（若可）
                    {
                        string? sn = null, sa = null, st = null;
                        PopulateSystemFromPsets(pf as IIfcProduct, ref sn, ref sa, ref st);
                        center.SystemName = sn; center.SystemAbbreviation = sa; center.SystemType = st; center.SystemKey = sa ?? sn ?? "(未指定)";
                    }
                    centerMap[fitLbl] = center;
                    data.Nodes.Add(center);

                    // 端點：取 Ports → 投影
                    var ports = GetPorts(pf).OfType<IIfcDistributionPort>().ToList();
                    int attached = 0;
                    foreach (var port in ports)
                    {
                        int pLbl = (port as IPersistEntity)?.EntityLabel ?? 0;
                        if (pLbl == 0) continue;
                        Point3D? pp3 = GetPortPoint3D(port);
                        if (!pp3.HasValue) continue; // 無座標則略過
                        var (pu, pv) = Proj(pp3.Value, finalPlane);
                        var pn = new SchematicNode
                        {
                            Id = $"P_{pLbl}",
                            Name = IfcStringHelper.FromValue((port as IIfcRoot)?.Name) ?? ($"Port_{pLbl}"),
                            IfcType = "Port",
                            Position3D = pp3.Value,
                            Position2D = new System.Windows.Point(pu, pv),
                            Entity = port as IPersistEntity,
                            NodeKind = SchematicNode.SchematicNodeKind.PipeEnd
                        };
                        pn.SystemName = center.SystemName; pn.SystemAbbreviation = center.SystemAbbreviation; pn.SystemType = center.SystemType; pn.SystemKey = center.SystemKey;
                        portMap[pLbl] = pn;
                        data.Nodes.Add(pn);

                        var e = new SchematicEdge
                        {
                            Id = $"FH_{pLbl}_{fitLbl}_{data.Edges.Count}",
                            StartNode = pn,
                            EndNode = center,
                            StartNodeId = pn.Id,
                            EndNodeId = center.Id,
                            IsInferred = true,
                            Origin = SchematicEdge.EdgeOriginKind.Rewired,
                            SystemName = center.SystemName,
                            SystemAbbreviation = center.SystemAbbreviation,
                            SystemType = center.SystemType,
                            SourceTag = isElbow ? "FittingHub-Elbow" : "FittingHub"
                        };
                        data.Edges.Add(e);
                        pn.Edges.Add(e); center.Edges.Add(e);
                        attached++;
                        created++;
                    }
                    maxDeg = Math.Max(maxDeg, attached);
                }

                // 追加：管段端點→端點的實線（Segment）
                IEnumerable<IIfcPipeSegment> segments;
                try { segments = model.Instances.OfType<IIfcPipeSegment>().ToList(); } catch { segments = Enumerable.Empty<IIfcPipeSegment>(); }
                double unitScaleMm = GetLengthToMillimetreScale(model);
                var edgePairSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in data.Edges)
                {
                    var a = e.StartNodeId; var b = e.EndNodeId; if (string.Compare(a, b, StringComparison.Ordinal) > 0) (a, b) = (b, a);
                    edgePairSet.Add($"{a}__{b}");
                }
                foreach (var seg in segments)
                {
                    var segPorts = GetPorts(seg).OfType<IIfcDistributionPort>().Take(2).ToList();

                    Point3D p3a, p3b; int la = 0, lb = 0; string? portAName = null, portBName = null; bool usedFallback = false;
                    IIfcDistributionPort? portA = null, portB = null;
                    if (segPorts.Count >= 2)
                    {
                        var pts3 = segPorts.Select(GetPortPoint3D).ToList();
                        if (pts3[0].HasValue && pts3[1].HasValue)
                        {
                            p3a = pts3[0]!.Value; p3b = pts3[1]!.Value;
                            la = (segPorts[0] as IPersistEntity)?.EntityLabel ?? 0;
                            lb = (segPorts[1] as IPersistEntity)?.EntityLabel ?? 0;
                            portA = segPorts[0]; portB = segPorts[1];
                            try { portAName = IfcStringHelper.FromValue((segPorts[0] as IIfcRoot)?.Name); } catch { }
                            try { portBName = IfcStringHelper.FromValue((segPorts[1] as IIfcRoot)?.Name); } catch { }
                        }
                        else usedFallback = true;
                    }
                    else
                    {
                        usedFallback = true;
                    }

                    if (usedFallback)
                    {
                        // 幾何後援：利用擠出方向與深度估算兩端點
                        if (!TryGetSegmentDirectionAndLength(seg, out var dir, out var length)) continue;
                        var center = GetElementPoint(seg as IIfcProduct);
                        var half = Math.Max(0.0, length) * 0.5;
                        var n = dir; if (n.LengthSquared > 1e-16) { n.Normalize(); } else { continue; }
                        p3a = new Point3D(center.X - n.X * half, center.Y - n.Y * half, center.Z - n.Z * half);
                        p3b = new Point3D(center.X + n.X * half, center.Y + n.Y * half, center.Z + n.Z * half);
                        // 合成臨時標籤，避免與 Port 衝突
                        int segLbl = (seg as IPersistEntity)?.EntityLabel ?? 0;
                        la = unchecked(segLbl * 2 + 0x10000000);
                        lb = unchecked(segLbl * 2 + 1 + 0x10000000);
                        portAName = "SegEnd-A"; portBName = "SegEnd-B";
                    }

                    var (ua, va) = Proj(p3a, finalPlane);
                    var (ub, vb) = Proj(p3b, finalPlane);

                    // 建立或取用兩端節點（Port 優先；後援端點以合成 Id）
                    var keyA = la != 0 ? $"P_{la}" : (IfcStringHelper.FromValue((seg as IIfcRoot)?.GlobalId) ?? $"SEG_{(seg as IPersistEntity)?.EntityLabel}_A");
                    var keyB = lb != 0 ? $"P_{lb}" : (IfcStringHelper.FromValue((seg as IIfcRoot)?.GlobalId) ?? $"SEG_{(seg as IPersistEntity)?.EntityLabel}_B");

                    if (!portMap.TryGetValue(la, out var na))
                    {
                        var nodeA = new SchematicNode
                        {
                            Id = keyA,
                            Name = portAName ?? keyA,
                            IfcType = la != 0 ? "Port" : "PipeSegmentEnd",
                            Position3D = p3a,
                            Position2D = new System.Windows.Point(ua, va),
                            Entity = (la != 0 ? (portA as IPersistEntity) : null) ?? (seg as IPersistEntity),
                            NodeKind = SchematicNode.SchematicNodeKind.PipeEnd
                        };
                        string? sn = null, sa = null, st = null; PopulateSystemFromPsets(seg as IIfcProduct, ref sn, ref sa, ref st);
                        nodeA.SystemName = sn; nodeA.SystemAbbreviation = sa; nodeA.SystemType = st; nodeA.SystemKey = sa ?? sn ?? "(未指定)";
                        data.Nodes.Add(nodeA);
                        if (la != 0) portMap[la] = nodeA; // 只有真正 Port 才加入 portMap
                        na = nodeA;
                    }
                    if (!portMap.TryGetValue(lb, out var nb))
                    {
                        var nodeB = new SchematicNode
                        {
                            Id = keyB,
                            Name = portBName ?? keyB,
                            IfcType = lb != 0 ? "Port" : "PipeSegmentEnd",
                            Position3D = p3b,
                            Position2D = new System.Windows.Point(ub, vb),
                            Entity = (lb != 0 ? (portB as IPersistEntity) : null) ?? (seg as IPersistEntity),
                            NodeKind = SchematicNode.SchematicNodeKind.PipeEnd
                        };
                        string? sn = null, sa = null, st = null; PopulateSystemFromPsets(seg as IIfcProduct, ref sn, ref sa, ref st);
                        nodeB.SystemName = sn; nodeB.SystemAbbreviation = sa; nodeB.SystemType = st; nodeB.SystemKey = sa ?? sn ?? "(未指定)";
                        data.Nodes.Add(nodeB);
                        if (lb != 0) portMap[lb] = nodeB;
                        nb = nodeB;
                    }

                    if (ReferenceEquals(na, nb)) continue;
                    var aId = na.Id; var bId = nb.Id; if (string.Compare(aId, bId, StringComparison.Ordinal) > 0) (aId, bId) = (bId, aId);
                    var key = $"{aId}__{bId}";
                    if (edgePairSet.Contains(key)) continue;

                    // 建立 Segment 邊
                    string edgeId = IfcStringHelper.FromValue((seg as IIfcRoot)?.GlobalId) ?? $"SEG_{aId}_{bId}_{data.Edges.Count}";
                    var vec = new Vector3D(p3b.X - p3a.X, p3b.Y - p3a.Y, p3b.Z - p3a.Z);
                    double lenMm = vec.Length * unitScaleMm;
                    ExtractDiameters(model, seg, out var dnMm2, out var doMm2, out var srcDn2, out var srcDo2);
                    var eSeg = new SchematicEdge
                    {
                        Id = edgeId,
                        StartNode = na,
                        EndNode = nb,
                        StartNodeId = na.Id,
                        EndNodeId = nb.Id,
                        IsInferred = false,
                        Origin = SchematicEdge.EdgeOriginKind.Segment,
                        SystemName = na.SystemName ?? nb.SystemName,
                        SystemAbbreviation = na.SystemAbbreviation ?? nb.SystemAbbreviation,
                        SystemType = na.SystemType ?? nb.SystemType,
                        NominalDiameterMm = dnMm2,
                        OuterDiameterMm = doMm2,
                        ValueSourceNominalDiameter = srcDn2,
                        ValueSourceOuterDiameter = srcDo2,
                        LengthMm = lenMm
                    };
                    data.Edges.Add(eSeg); na.Edges.Add(eSeg); nb.Edges.Add(eSeg);
                    edgePairSet.Add(key);
                }

                report.TotalNodes = data.Nodes.Count;
                report.TotalEdges = data.Edges.Count;
                report.RewiredEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Rewired);
                report.SegmentEdges = data.Edges.Count(e => e.Origin == SchematicEdge.EdgeOriginKind.Segment);
                report.Systems = data.Nodes.Select(n => n.SystemKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                report.FittingMaxDegree = maxDeg;
                report.FittingAvgDegree = (fitCount > 0) ? (double)created / Math.Max(1, fitCount) : 0;
                report.Notes = $"FittingHub FromModel: onlyElbow={(onlyElbow?"Y":"N")}, plane={finalPlane}, flipY={(flipY?"Y":"N")}" + (planeWasDegenerate?";PlaneAutoSwitch":"");
                try { BackfillEdgeSystems(data); } catch { }
                try
                {
                    var sortedNodes2 = data.Nodes.OrderBy(n => n.HostLabel ?? 0).ThenBy(n => n.Id).ToList();
                    var sortedEdges2 = data.Edges.OrderBy(e => e.Id).ToList();
                    data.Nodes.Clear(); data.Nodes.AddRange(sortedNodes2);
                    data.Edges.Clear(); data.Edges.AddRange(sortedEdges2);
                }
                catch { }
                data.HasOfflineRewireSeed = true;
                LastBuiltData = data;
                return (data, report);
            });
        }

        // 邊的系統欄位回填（若缺失，從兩端節點取值）
        private static void BackfillEdgeSystems(SchematicData data)
        {
            if (data?.Edges == null) return;
            foreach (var e in data.Edges)
            {
                bool missingSys = string.IsNullOrWhiteSpace(e.SystemAbbreviation) && string.IsNullOrWhiteSpace(e.SystemName);
                if (!missingSys) continue;
                var a = e.StartNode; var b = e.EndNode;
                string? abbr = null;
                if (!string.IsNullOrWhiteSpace(a?.SystemAbbreviation) && !string.Equals(a!.SystemAbbreviation, "(未指定)", StringComparison.OrdinalIgnoreCase)) abbr = a!.SystemAbbreviation;
                if (string.IsNullOrWhiteSpace(abbr) && !string.IsNullOrWhiteSpace(b?.SystemAbbreviation) && !string.Equals(b!.SystemAbbreviation, "(未指定)", StringComparison.OrdinalIgnoreCase)) abbr = b!.SystemAbbreviation;

                string? name = null;
                if (!string.IsNullOrWhiteSpace(a?.SystemName) && !string.Equals(a!.SystemName, "(未指定)", StringComparison.OrdinalIgnoreCase)) name = a!.SystemName;
                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(b?.SystemName) && !string.Equals(b!.SystemName, "(未指定)", StringComparison.OrdinalIgnoreCase)) name = b!.SystemName;

                e.SystemAbbreviation ??= abbr;
                e.SystemName ??= name;
                e.SystemAbbreviation ??= a?.SystemAbbreviation ?? b?.SystemAbbreviation;
                e.SystemName ??= a?.SystemName ?? b?.SystemName;
                e.SystemType ??= a?.SystemType ?? b?.SystemType;
            }
        }

        // 基於樣本 3D 點決定最佳投影平面；若使用者選的平面退化，則改採跨度和最大的平面
        private static UserProjectionPlane ChooseBestPlaneForPoints(IList<Point3D> pts, UserProjectionPlane chosen, out bool wasDegenerate)
        {
            wasDegenerate = false;
            if (pts == null || pts.Count == 0) return chosen;
            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            double minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
            double rx = Math.Max(1e-12, maxX - minX);
            double ry = Math.Max(1e-12, maxY - minY);
            double rz = Math.Max(1e-12, maxZ - minZ);
            bool IsDeg(UserProjectionPlane pl) => pl switch
            {
                UserProjectionPlane.XY => (rx < 1e-6 || ry < 1e-6),
                UserProjectionPlane.XZ => (rx < 1e-6 || rz < 1e-6),
                _ => (ry < 1e-6 || rz < 1e-6)
            };
            if (!IsDeg(chosen)) return chosen;
            wasDegenerate = true;
            double sXY = rx + ry, sXZ = rx + rz, sYZ = ry + rz;
            if (sXY >= sXZ && sXY >= sYZ) return UserProjectionPlane.XY;
            if (sXZ >= sXY && sXZ >= sYZ) return UserProjectionPlane.XZ;
            return UserProjectionPlane.YZ;
        }

        private static string NormalizeSystemKey(string key)
        {
            try
            {
                var trimmed = key.Trim();
                // Collapse internal whitespace
                trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, "\u3000", " "); // full width space
                // 將連續空白 (含 Tab / 換行) 摺疊為單一空白
                trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ");
                trimmed = trimmed.ToUpperInvariant();
                return trimmed;
            }
            catch { return key?.ToUpperInvariant() ?? "(未指定)"; }
        }

        // 取得 Port 的宿主元素（若為 IfcDistributionPort 通常 ContainedIn.RelatingElement）
        private IIfcElement? TryGetHostElement(IIfcPort port)
        {
            try
            {
                var relObj = port.ContainedIn;
                // 單一關聯
                if (relObj is IIfcRelConnectsPortToElement single)
                {
                    var elem = single.RelatedElement as IIfcElement;
                    if (elem != null) return elem;
                }
                // 多關聯集合（某些資料載入器可能包成集合）
                if (relObj is IEnumerable<IIfcRelConnectsPortToElement> many)
                {
                    var first = many.FirstOrDefault();
                    if (first != null)
                    {
                        var elem = first.RelatedElement as IIfcElement;
                        if (elem != null) return elem;
                    }
                }
                // Dynamic 後備
                try { return (relObj as dynamic)?.RelatingElement as IIfcElement; } catch { }
                try { return (relObj as dynamic)?.RelatedElement as IIfcElement; } catch { }
            }
            catch { }
            return null;
        }
        private enum ProjectionPlane { XY, XZ, YZ }
        // 新增：對外公開可選平面枚舉（供 V1 流程使用）
        public enum UserProjectionPlane { XY, XZ, YZ }
        
        // === Sprint 1 輔助：單位、樓層、系統、管段方向/長度、管徑 ===
        private static double GetLengthToMillimetreScale(IModel model)
        {
            try
            {
                var proj = model.Instances.OfType<IIfcProject>()?.FirstOrDefault();
                var units = proj?.UnitsInContext?.Units;
                if (units != null)
                {
                    // 直接找 SI 長度單位
                    foreach (var u in units)
                    {
                        if (u is IIfcSIUnit si && si.UnitType == IfcUnitEnum.LENGTHUNIT)
                        {
                            var name = si.Name;
                            if (name == IfcSIUnitName.METRE) return 1000.0; // m -> mm
                            // 罕見：MILLIMETRE
                            if (name.ToString().Equals("MILLIMETRE", StringComparison.OrdinalIgnoreCase)) return 1.0;
                        }
                        // 常見 conversion-based: FOOT/INCH
                        if (u is IIfcConversionBasedUnit conv && conv.UnitType == IfcUnitEnum.LENGTHUNIT)
                        {
                            var uname = (IfcStringHelper.FromValue(conv.Name) ?? string.Empty).ToUpperInvariant();
                            if (uname == "FOOT" || uname == "FEET") return 304.8; // 1 ft = 304.8 mm
                            if (uname == "INCH") return 25.4; // 1 in = 25.4 mm
                        }
                    }
                }
            }
            catch { }
            // 預設：當作 mm
            return 1.0;
        }

        private static string? GetLevelNameForProduct(IModel model, IIfcProduct prod)
        {
            try
            {
                // 直接掃描 ContainedInSpatialStructure（保守但穩定）
                var rels = model.Instances.OfType<IIfcRelContainedInSpatialStructure>()?.ToList() ?? new List<IIfcRelContainedInSpatialStructure>();
                foreach (var r in rels)
                {
                    if (r.RelatedElements == null) continue;
                    foreach (var e in r.RelatedElements)
                    {
                        if (!ReferenceEquals(e, prod)) continue;
                        var storey = r.RelatingStructure as IIfcBuildingStorey;
                        if (storey != null)
                        {
                            try { return IfcStringHelper.FromValue((storey as IIfcRoot)?.Name) ?? IfcStringHelper.FromValue((storey as IIfcRoot)?.GlobalId) ?? null; } catch { return null; }
                        }
                        // 若非樓層，沿父層往上找樓層
                        var spatial = r.RelatingStructure as IIfcSpatialStructureElement;
                        while (spatial != null)
                        {
                            if (spatial is IIfcBuildingStorey st)
                            {
                                try { return IfcStringHelper.FromValue((st as IIfcRoot)?.Name) ?? IfcStringHelper.FromValue((st as IIfcRoot)?.GlobalId) ?? null; } catch { return null; }
                            }
                            try { spatial = (spatial as dynamic).Decomposes?.FirstOrDefault()?.RelatingObject as IIfcSpatialStructureElement; } catch { spatial = null; }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static (string sysName, string? sysAbbrev, string? sysType) GetSystemMetadata(IIfcSystem sys)
        {
            string name = TrySystemName(sys);
            string? type = null;
            try
            {
                if (sys is IIfcDistributionSystem ds)
                {
                    var pt = ds.PredefinedType; // IfcDistributionSystemEnum?
                    type = pt?.ToString();
                }
            }
            catch { }
            string? abbr = DeriveSystemAbbreviation(name, type);
            return (name, abbr, type);
        }

        private static string? DeriveSystemAbbreviation(string? name, string? sysType)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(sysType)) return null;
            var s = (name ?? sysType ?? string.Empty).Trim();
            // 1) 取連續大寫字母
            var upper = new string(s.Where(char.IsUpper).ToArray());
            if (upper.Length >= 2 && upper.Length <= 6) return upper;
            // 2) 取每個詞的首字母（英數）
            var parts = s.Split(new[] { ' ', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var initials = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0])));
            if (initials.Length >= 2 && initials.Length <= 6) return initials;
            // 3) 前 3 字
            return s.Length >= 3 ? s.Substring(0, 3).ToUpperInvariant() : s.ToUpperInvariant();
        }

        private static PipeOrientation ClassifyOrientationFromVector(Vector3D v)
        {
            if (v.LengthSquared < 1e-12) return PipeOrientation.Sloped;
            var nv = v; nv.Normalize();
            var zAbs = Math.Abs(nv.Z);
            if (zAbs >= 0.9) return PipeOrientation.Vertical;
            if (zAbs <= 0.1) return PipeOrientation.Horizontal;
            return PipeOrientation.Sloped;
        }

        private static bool TryGetSegmentDirectionAndLength(IIfcPipeSegment seg, out Vector3D dir, out double length)
        {
            dir = new Vector3D(); length = 0;
            try
            {
                var body = seg.Representation?.Representations?
                    .FirstOrDefault(r => string.Equals(r.RepresentationType, "SweptSolid", StringComparison.OrdinalIgnoreCase));
                var extrude = body?.Items?.OfType<IIfcExtrudedAreaSolid>()?.FirstOrDefault();
                if (extrude != null)
                {
                    var productBasis = GetBasisFromPlacement(seg.ObjectPlacement as IIfcObjectPlacement);
                    var pos = extrude.Position as IIfcAxis2Placement3D;
                    var solidBasis = pos != null ? ApplyAxis2Placement(productBasis, pos) : productBasis;
                    var dLocal = GetDirectionVector(extrude.ExtrudedDirection) ?? new Vector3D(0, 0, 1);
                    var dWorld = dLocal.X * solidBasis.U + dLocal.Y * solidBasis.V + dLocal.Z * solidBasis.W;
                    if (dWorld.LengthSquared < 1e-16) dWorld = solidBasis.W;
                    dir = dWorld; length = extrude.Depth;
                    return true;
                }
            }
            catch { }
            // 後援：使用兩端節點或 Port 的向量（可能不可得）
            try
            {
                var ports = GetPorts(seg).OfType<IIfcPort>().Take(2).ToList();
                if (ports.Count == 2)
                {
                    var a = GetPortPoint3D(ports[0]);
                    var b = GetPortPoint3D(ports[1]);
                    if (a.HasValue && b.HasValue)
                    {
                        dir = new Vector3D(b.Value.X - a.Value.X, b.Value.Y - a.Value.Y, b.Value.Z - a.Value.Z);
                        length = dir.Length; return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void ExtractDiameters(IModel model, IIfcPipeSegment seg, out double? nominalMm, out double? outerMm, out string? srcNom, out string? srcOuter)
        {
            nominalMm = null; outerMm = null; srcNom = null; srcOuter = null;
            double scale = GetLengthToMillimetreScale(model);
            try
            {
                // 幾何 Profile：IfcCircleProfileDef/IfcRectangleProfileDef 等
                var body = seg.Representation?.Representations?
                    .FirstOrDefault(r => string.Equals(r.RepresentationType, "SweptSolid", StringComparison.OrdinalIgnoreCase));
                var extrude = body?.Items?.OfType<IIfcExtrudedAreaSolid>()?.FirstOrDefault();
                var area = extrude?.SweptArea;
                if (area is IIfcCircleProfileDef circle)
                {
                    var rVal = TryConvertIfcValueToDouble(circle.Radius);
                    if (rVal.HasValue)
                    {
                        var r = rVal.Value * scale; // 以 mm 換算
                        outerMm = 2.0 * r; srcOuter = "Profile.Circle.Radius";
                    }
                }
                else if (area is IIfcArbitraryClosedProfileDef arb)
                {
                    // 無明確半徑；略過
                }
            }
            catch { }

            // 屬性集：擴充對常見鍵的支援（NominalDiameter/DN、Diameter、Outside/Outer Diameter、Inside Diameter 等）
            try
            {
                // 規則：若存在 Pset[Mechanical].Diameter，優先作為 NominalDiameter(mm)
                // 其他來源（NominalDiameter/DN/NPS/Diameter/InsideDiameter）為候補
                int nomPriority = 0; // 0: 未設定; 1: 一般來源; 2: Mechanical.Diameter
                static double? ParseNumberWithUnits(object? v, double mmScale)
                {
                    if (v == null) return null;
                    try
                    {
                        // 優先讀 IfcMeasure.Value
                        try { return (double)(v as dynamic).Value * mmScale; } catch { }
                        var s = v.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(s)) return null;
                        var sNorm = s.Trim();
                        // 偵測英吋
                        bool isInch = sNorm.Contains("\"") || Regex.IsMatch(sNorm, "(?i)(inch|inches|in)\b");
                        // 取第一個數字（允許小數）
                        var m = Regex.Match(sNorm, @"[-+]?[0-9]*\.?[0-9]+");
                        if (!m.Success) return null;
                        if (!double.TryParse(m.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num))
                        {
                            if (!double.TryParse(m.Value, out num)) return null;
                        }
                        if (isInch) return num * 25.4; // inch → mm
                        // 偵測是否已有 mm 單位（或未標註，視為模型長度單位刻度）
                        // 若字串包含 mm 就不再乘以 scale（通常 IfcValue 已有實體單位，字串才會帶單位）
                        if (Regex.IsMatch(sNorm, "(?i)\bmm\b")) return num; // 已是 mm
                        return num * mmScale;
                    }
                    catch { return null; }
                }

                foreach (var defBy in seg.IsDefinedBy ?? Enumerable.Empty<IIfcRelDefinesByProperties>())
                {
                    var pset = defBy.RelatingPropertyDefinition as IIfcPropertySet;
                    if (pset == null) continue;
                    var psetName = IFC_Viewer_00.Services.IfcStringHelper.FromValue(pset.Name) ?? string.Empty;
                    bool isMechanicalPset = psetName.IndexOf("Mechanical", StringComparison.OrdinalIgnoreCase) >= 0;
                    foreach (var p in pset.HasProperties ?? Enumerable.Empty<IIfcProperty>())
                    {
                        var name = (IfcStringHelper.FromValue(p?.Name) ?? string.Empty).Trim();
                        if (p is not IIfcPropertySingleValue sv) continue;
                        var val = sv.NominalValue;

                        // Nominal / DN / NPS / Diameter → nominalMm
                        {
                            bool nameMatches =
                                string.Equals(name, "NominalDiameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Nominal Diameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "DN", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Diameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Nominal Pipe Size", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "NPS", StringComparison.OrdinalIgnoreCase);
                            if (nameMatches)
                            {
                                int candPri = (isMechanicalPset && string.Equals(name, "Diameter", StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
                                var num = TryConvertIfcValueToDouble(val) ?? ParseNumberWithUnits(val, scale);
                                if (num.HasValue && num.Value > 0 && candPri >= nomPriority)
                                {
                                    nominalMm = num.Value;
                                    nomPriority = candPri;
                                    srcNom = $"Pset.{pset.Name}." + name;
                                }
                            }
                        }

                        // Outer / Outside Diameter → outerMm
                        if (outerMm == null)
                        {
                            if (string.Equals(name, "OutsideDiameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Outside Diameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "OuterDiameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Outer Diameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "OD", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Outside Dia", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Outside Dia.", StringComparison.OrdinalIgnoreCase))
                            {
                                var num = TryConvertIfcValueToDouble(val) ?? ParseNumberWithUnits(val, scale);
                                if (num.HasValue && num.Value > 0)
                                {
                                    outerMm = num.Value;
                                    srcOuter = $"Pset.{pset.Name}." + name;
                                }
                            }
                        }

                        // Inside Diameter（一般候補，優先級=1；若已採用 Mechanical.Diameter 則不覆蓋）
                        {
                            if (string.Equals(name, "InsideDiameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Inside Diameter", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "ID", StringComparison.OrdinalIgnoreCase))
                            {
                                var num = TryConvertIfcValueToDouble(val) ?? ParseNumberWithUnits(val, scale);
                                if (num.HasValue && num.Value > 0 && nomPriority < 2)
                                {
                                    nominalMm = num.Value;
                                    nomPriority = Math.Max(nomPriority, 1);
                                    srcNom = $"Pset.{pset.Name}." + name;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static double? TryConvertIfcValueToDouble(object? ifcValue)
        {
            try
            {
                if (ifcValue == null) return null;
                // xBIM 將 IfcValue 拓展為具體型別（如 IfcLengthMeasure/IfcPositiveLengthMeasure）
                // 嘗試以 dynamic 讀取 .Value；失敗則用 ToString 解析
                try { return (double)(ifcValue as dynamic).Value; } catch { }
                if (double.TryParse(ifcValue.ToString(), out var d)) return d; 
            }
            catch { }
            return null;
        }

        // 取得樓層 Elevation（同一型別 IModel，亦兼容 IStepModel 別名）
        private static void PopulateLevels(IModel ifcModel, SchematicData data)
        {
            if (ifcModel == null || data == null) return;
            try
            {
                var storeys = ifcModel.Instances.OfType<IIfcBuildingStorey>()?.ToList() ?? new List<IIfcBuildingStorey>();
                var levels = new List<LevelInfo>();
                foreach (var s in storeys)
                {
                    string name = string.Empty;
                    try { name = IfcStringHelper.FromValue((s as IIfcRoot)?.Name) ?? IfcStringHelper.FromValue((s as IIfcRoot)?.GlobalId) ?? ""; } catch { }
                    double? elev = null;
                    try { elev = (s as dynamic).Elevation; } catch { }
                    if (!elev.HasValue)
                    {
                        try
                        {
                            if (s is IIfcProduct sp)
                            {
                                var p = GetElementPoint(sp);
                                elev = p.Z;
                            }
                        }
                        catch { }
                    }
                    levels.Add(new LevelInfo { Name = name, Elevation = elev ?? 0.0 });
                }
                data.Levels.AddRange(levels.OrderBy(l => l.Elevation));
            }
            catch { }
        }

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

        public record FlowTerminalAnchorDetail(
            int TerminalLabel,
            string? TerminalName,
            double X,
            double Y,
            double Z,
            int? PortLabel,
            string? PortName,
            string Source // Port | Placement | BBox
        );

        public IReadOnlyList<FlowTerminalAnchorDetail>? LastFlowTerminalAnchorDetails { get; private set; }

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

        /// <summary>
        /// 取得模型中所有 IfcFlowTerminal 的 3D 定位點（優先 Port → LocalPlacement → BBox 中心）。
        /// 僅回傳座標，詳細對應可從 LastFlowTerminalAnchorDetails 取得。
        /// </summary>
        public Task<List<Point3D>> GetFlowTerminalAnchorsAsync(IStepModel ifcModel)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));
            return Task.Run(() =>
            {
                var pts = new List<Point3D>();
                var details = new List<FlowTerminalAnchorDetail>();
                int viaPort = 0, viaPlacement = 0, viaUnknown = 0, termsWithNoPorts = 0;
                try
                {
                    var terms = ifcModel.Instances.OfType<IIfcFlowTerminal>()?.ToList() ?? new List<IIfcFlowTerminal>();
                    int totalPorts = 0;
                    try { totalPorts = ifcModel.Instances.OfType<IIfcDistributionPort>()?.Count() ?? 0; } catch { }
                    System.Diagnostics.Trace.WriteLine($"[Service][FTA] Model scan: FlowTerminal={terms.Count}, DistributionPort={totalPorts}");
                    foreach (var t in terms)
                    {
                        Point3D? anchor = null;
                        int? portLbl = null; string? portName = null; string source = "";

                        // 1) Port 優先：選擇 SOURCE 或 SINK 的主要 Port，取其點
                        try
                        {
                            var ports = GetPorts(t).OfType<IIfcDistributionPort>().ToList();
                            if (ports.Count == 0) termsWithNoPorts++;
                            IIfcDistributionPort? pick = ports.FirstOrDefault(p => p.FlowDirection == IfcFlowDirectionEnum.SOURCE)
                                                        ?? ports.FirstOrDefault(p => p.FlowDirection == IfcFlowDirectionEnum.SINK)
                                                        ?? ports.FirstOrDefault();
                            if (pick != null)
                            {
                                var pp = GetPortPoint3D(pick);
                                if (pp.HasValue)
                                {
                                    anchor = pp.Value;
                                    portLbl = (pick as IPersistEntity)?.EntityLabel;
                                    try { portName = IfcStringHelper.FromValue((pick as IIfcRoot)?.Name); } catch { }
                                    source = "Port";
                                    viaPort++;
                                }
                            }
                        }
                        catch { }

                        // 2) 後援：LocalPlacement
                        if (!anchor.HasValue)
                        {
                            try
                            {
                                anchor = GetElementPoint(t as IIfcProduct);
                                source = "Placement";
                                viaPlacement++;
                            }
                            catch { }
                        }

                        // 3)（預留）BBox 中心：目前不在此處建立幾何內容，避免昂貴依賴；若之後加入現有幾何管線可補強。

                        var a = anchor ?? new Point3D(0, 0, 0);
                        pts.Add(a);
                        details.Add(new FlowTerminalAnchorDetail(
                            TerminalLabel: (t as IPersistEntity)?.EntityLabel ?? 0,
                            TerminalName: SafeName(t as IIfcRoot),
                            X: a.X, Y: a.Y, Z: a.Z,
                            PortLabel: portLbl, PortName: portName,
                            Source: string.IsNullOrEmpty(source) ? "Unknown" : source
                        ));
                        if (string.IsNullOrEmpty(source)) viaUnknown++;
                    }
                }
                catch { }
                LastFlowTerminalAnchorDetails = details;
                try
                {
                    System.Diagnostics.Trace.WriteLine($"[Service][FTA] Anchors built: total={pts.Count}, viaPort={viaPort}, viaPlacement={viaPlacement}, viaUnknown={viaUnknown}, terminalsWithNoPorts={termsWithNoPorts}");
                    // 範例列印前幾筆（避免太多）
                    foreach (var d in details.Take(5))
                    {
                        System.Diagnostics.Trace.WriteLine($"[Service][FTA]  • T(L{d.TerminalLabel}) '{d.TerminalName}' -> {d.Source} XYZ=({d.X:0.##},{d.Y:0.##},{d.Z:0.##}) PortLabel={(d.PortLabel?.ToString() ?? "-")}" );
                    }
                }
                catch { }
                return pts;
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

        // 後援：自 Pset 擷取系統資訊（縮寫/名稱/型別），常見於未建立 IfcSystem 分組的模型
        private static void PopulateSystemFromPsets(IIfcProduct? prod, ref string? sysName, ref string? sysAbbrev, ref string? sysType)
        {
            if (prod == null) return;
            try
            {
                // 僅在目前為空時才以 Pset 補值
                bool needName = string.IsNullOrWhiteSpace(sysName);
                bool needAbbr = string.IsNullOrWhiteSpace(sysAbbrev);
                bool needType = string.IsNullOrWhiteSpace(sysType);
                if (!needName && !needAbbr && !needType) return;

                foreach (var def in (prod as IIfcObject)?.IsDefinedBy ?? Enumerable.Empty<IIfcRelDefines>())
                {
                    if (def is not IIfcRelDefinesByProperties rdp) continue;
                    var pset = rdp.RelatingPropertyDefinition as IIfcPropertySet;
                    if (pset == null) continue;
                    foreach (var p in pset.HasProperties ?? Enumerable.Empty<IIfcProperty>())
                    {
                        if (p is not IIfcPropertySingleValue sv) continue;
                        string pname = (IfcStringHelper.FromValue(p.Name) ?? string.Empty).Trim();
                        string pval = string.Empty; try { pval = sv.NominalValue?.ToString() ?? string.Empty; } catch { }
                        if (string.IsNullOrWhiteSpace(pval)) continue;

                        // 名稱鍵
                        if (needName)
                        {
                            if (pname.Equals("System Name", StringComparison.OrdinalIgnoreCase) ||
                                pname.Equals("SystemName", StringComparison.OrdinalIgnoreCase) ||
                                pname.Equals("System", StringComparison.OrdinalIgnoreCase))
                            {
                                sysName = pval; needName = false;
                            }
                        }
                        // 縮寫鍵
                        if (needAbbr)
                        {
                            if (pname.Equals("System Abbreviation", StringComparison.OrdinalIgnoreCase) ||
                                pname.Equals("SystemAbbreviation", StringComparison.OrdinalIgnoreCase) ||
                                pname.Equals("Abbreviation", StringComparison.OrdinalIgnoreCase) ||
                                pname.Equals("Abbrev", StringComparison.OrdinalIgnoreCase))
                            {
                                sysAbbrev = pval; needAbbr = false;
                            }
                        }
                        // 型別鍵（可選）
                        if (needType)
                        {
                            if (pname.Equals("System Type", StringComparison.OrdinalIgnoreCase) ||
                                pname.Equals("SystemType", StringComparison.OrdinalIgnoreCase) ||
                                pname.Equals("DistributionSystemType", StringComparison.OrdinalIgnoreCase))
                            {
                                sysType = pval; needType = false;
                            }
                        }

                        if (!needName && !needAbbr && !needType) break;
                    }
                    if (!needName && !needAbbr && !needType) break;
                }
            }
            catch { }
        }
        // SOP 2.0：系統優先，邏輯優先
        // 針對模型中的每一個 IfcSystem / IfcDistributionSystem 產生獨立的 SchematicData
    public async Task<List<SchematicData>> GenerateFromSystemsAsync(IStepModel ifcModel)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));

            return await Task.Run(() =>
            {
                var results = new List<SchematicData>();

                // 模型掃描與報告：統計模型中三類元件的總數量
                try
                {
                    int totalFittings = 0, totalTerminals = 0, totalSegments = 0;
                    try { totalFittings = ifcModel.Instances.OfType<IIfcPipeFitting>()?.Count() ?? 0; } catch { }
                    try { totalTerminals = ifcModel.Instances.OfType<IIfcFlowTerminal>()?.Count() ?? 0; } catch { }
                    try { totalSegments = ifcModel.Instances.OfType<IIfcPipeSegment>()?.Count() ?? 0; } catch { }
                    System.Diagnostics.Trace.WriteLine($"[Service][GenSys] Model scan: IfcPipeFitting={totalFittings}, IfcFlowTerminal={totalTerminals}, IfcPipeSegment={totalSegments}");
                }
                catch { }

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
                    var (metaName, metaAbbr, metaType) = GetSystemMetadata(sys);
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
                            node.SystemName = metaName; node.SystemAbbreviation = metaAbbr; node.SystemType = metaType;
                            // Pset 後援
                            {
                                string? sn = node.SystemName, sa = node.SystemAbbreviation, st = node.SystemType;
                                PopulateSystemFromPsets(pf as IIfcProduct, ref sn, ref sa, ref st);
                                node.SystemName = sn; node.SystemAbbreviation = sa; node.SystemType = st;
                            }
                            node.LevelName = GetLevelNameForProduct(ifcModel, pf);
                            data.Nodes.Add(node);
                            nodeMap[pe] = node;
                            foreach (var p in GetPorts(pf)) { if (p != null) portToNode[p] = node; }
                        }
                        else if (pe is IIfcFlowTerminal term)
                        {
                            var node = CreateNodeFromElement(term);
                            node.SystemName = metaName; node.SystemAbbreviation = metaAbbr; node.SystemType = metaType;
                            {
                                string? sn = node.SystemName, sa = node.SystemAbbreviation, st = node.SystemType;
                                PopulateSystemFromPsets(term as IIfcProduct, ref sn, ref sa, ref st);
                                node.SystemName = sn; node.SystemAbbreviation = sa; node.SystemType = st;
                            }
                            node.LevelName = GetLevelNameForProduct(ifcModel, term);
                            data.Nodes.Add(node);
                            nodeMap[pe] = node;
                            foreach (var p in GetPorts(term)) { if (p != null) portToNode[p] = node; }
                        }
                        else if (pe is IIfcValve valve)
                        {
                            var node = CreateNodeFromElement(valve);
                            node.SystemName = metaName; node.SystemAbbreviation = metaAbbr; node.SystemType = metaType;
                            {
                                string? sn = node.SystemName, sa = node.SystemAbbreviation, st = node.SystemType;
                                PopulateSystemFromPsets(valve as IIfcProduct, ref sn, ref sa, ref st);
                                node.SystemName = sn; node.SystemAbbreviation = sa; node.SystemType = st;
                            }
                            node.LevelName = GetLevelNameForProduct(ifcModel, valve);
                            data.Nodes.Add(node);
                            nodeMap[pe] = node;
                            foreach (var p in GetPorts(valve)) { if (p != null) portToNode[p] = node; }
                        }
                        // 其餘成員（含 IfcPipeSegment 與非管線相關元件）在此遍略過
                    }

                    // 預先取出全域的 IfcRelConnectsPorts（後續過濾）
                    var allPortRels = ifcModel.Instances.OfType<IIfcRelConnectsPorts>()?.ToList() ?? new List<IIfcRelConnectsPorts>();

                    // 第二遍：將 IfcPipeSegment 轉換為邊（Edges）
                    var mainPipeCandidates = new List<SchematicEdge>();
                    double unitScaleMm = GetLengthToMillimetreScale(ifcModel);
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
                                if (start == null || end == null) continue;
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
                                    IsInferred = false,
                                    SystemName = metaName,
                                    SystemAbbreviation = metaAbbr,
                                    SystemType = metaType
                                };
                                // Pset 後援：若系統資訊仍缺，從管段 Pset 補上
                                {
                                    string? esn = edge.SystemName, esa = edge.SystemAbbreviation, est = edge.SystemType;
                                    PopulateSystemFromPsets(seg as IIfcProduct, ref esn, ref esa, ref est);
                                    edge.SystemName = esn; edge.SystemAbbreviation = esa; edge.SystemType = est;
                                }
                                // 同步到兩端節點（避免節點被視為未指定而被隱藏）
                                if (start != null)
                                {
                                    string? sn = start.SystemName, sa = start.SystemAbbreviation, st = start.SystemType;
                                    PopulateSystemFromPsets(seg as IIfcProduct, ref sn, ref sa, ref st);
                                    start.SystemName = sn; start.SystemAbbreviation = sa; start.SystemType = st;
                                }
                                if (end != null)
                                {
                                    string? sn = end.SystemName, sa = end.SystemAbbreviation, st = end.SystemType;
                                    PopulateSystemFromPsets(seg as IIfcProduct, ref sn, ref sa, ref st);
                                    end.SystemName = sn; end.SystemAbbreviation = sa; end.SystemType = st;
                                }
                                // Level：以管段為主；若無則回退起點/終點
                                edge.LevelName = GetLevelNameForProduct(ifcModel, seg) ?? start?.LevelName ?? end?.LevelName;

                                // Orientation：優先幾何，次選起迄
                                if (TryGetSegmentDirectionAndLength(seg, out var d, out var lenModel))
                                {
                                    edge.Orientation = ClassifyOrientationFromVector(d);
                                    // 記下長度（用於主幹評分）；以 mm
                                    double lengthMm = lenModel * unitScaleMm;
                                    // Diameters
                                    ExtractDiameters(ifcModel, seg, out var dnMm, out var doMm, out var srcDn, out var srcDo);
                                    edge.NominalDiameterMm = dnMm; edge.OuterDiameterMm = doMm;
                                    edge.ValueSourceNominalDiameter = srcDn; edge.ValueSourceOuterDiameter = srcDo;
                                    // 先暫存於候選清單，後續以分數挑選主幹
                                    mainPipeCandidates.Add(edge);
                                    // 用 Tag 暫存分數（避免改模型）：以 Outer 或 Nominal 作半徑參與
                                    double dia = doMm ?? dnMm ?? 0;
                                    double score = (dia > 0 ? dia : 1.0) * Math.Max(1.0, lengthMm);
                                    // 將暫存分數以 Connection 為 key 放入字典不可行；改用內部列表並二次排序
                                    // 在此無需保存得分欄位；後續排序直接重算
                                }
                                else
                                {
                                    // 後援：用節點座標向量
                                    var vec = new Vector3D(
                                        (end?.Position3D.X ?? 0) - (start?.Position3D.X ?? 0),
                                        (end?.Position3D.Y ?? 0) - (start?.Position3D.Y ?? 0),
                                        (end?.Position3D.Z ?? 0) - (start?.Position3D.Z ?? 0));
                                    edge.Orientation = ClassifyOrientationFromVector(vec);
                                }
                                data.Edges.Add(edge);
                                start?.Edges.Add(edge);
                                end?.Edges.Add(edge);
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

                    // e) 結果統計與報告（每個系統）：統計 Nodes 中三類 IfcType 的數量
                    try
                    {
                        int nFittings = 0, nTerminals = 0, nSegments = 0;
                        try { nFittings = data.Nodes.Count(n => string.Equals(n.IfcType, "IfcPipeFitting", StringComparison.OrdinalIgnoreCase)); } catch { }
                        try { nTerminals = data.Nodes.Count(n => string.Equals(n.IfcType, "IfcFlowTerminal", StringComparison.OrdinalIgnoreCase)); } catch { }
                        try { nSegments = data.Nodes.Count(n => string.Equals(n.IfcType, "IfcPipeSegment", StringComparison.OrdinalIgnoreCase)); } catch { }
                        var sysNameForLog = data.SystemName;
                        if (string.IsNullOrWhiteSpace(sysNameForLog))
                        {
                            try { sysNameForLog = IfcStringHelper.FromValue((sys as IIfcRoot)?.Name) ?? IfcStringHelper.FromValue((sys as IIfcRoot)?.GlobalId) ?? sys.GetType().Name; } catch { sysNameForLog = sys.GetType().Name; }
                        }
                        System.Diagnostics.Trace.WriteLine($"[Service][GenSys] Result stats for system '{sysNameForLog}': IfcPipeFitting={nFittings}, IfcFlowTerminal={nTerminals}, IfcPipeSegment={nSegments}, TotalNodes={data.Nodes.Count}, Edges={data.Edges.Count}");
                    }
                    catch { }

                    // f) 主幹標記（以系統內邊的直徑*長度打分，取前 15%）
                    try
                    {
                        if (data.Edges.Count > 0)
                        {
                            var ranked = data.Edges
                                .Select(e => new
                                {
                                    E = e,
                                    Dia = e.OuterDiameterMm ?? e.NominalDiameterMm ?? 0,
                                    Len = (e.StartNode != null && e.EndNode != null)
                                        ? Math.Sqrt(Dist2(e.StartNode.Position3D, e.EndNode.Position3D)) * unitScaleMm
                                        : 0
                                })
                                .OrderByDescending(x => (x.Dia > 0 ? x.Dia : 1.0) * Math.Max(1.0, x.Len))
                                .ToList();
                            int pick = Math.Max(1, (int)Math.Ceiling(ranked.Count * 0.15));
                            foreach (var x in ranked.Take(pick)) x.E.IsMainPipe = true;
                        }
                    }
                    catch { }

                    // g) 收集完成後加入結果
                    try { PopulateLevels(ifcModel, data); } catch { }
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
                // Pset 後援：若模型未使用 IfcSystem 分組，嘗試從元素 Pset 補系統資訊
                if (elem is IIfcProduct prod0)
                {
                    string? sn = null, sa = null, st = null;
                    PopulateSystemFromPsets(prod0, ref sn, ref sa, ref st);
                    node.SystemName = sn; node.SystemAbbreviation = sa; node.SystemType = st;
                }
                data.Nodes.Add(node);

                // 掛 Port 對應
                foreach (var p in GetPorts(elem))
                {
                    // 同一個元素的多個 Port 都指到相同 Node
                    if (p != null) portToNode[p] = node;
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
                // 邊的系統資訊：若可，沿用兩端節點其一的系統（偏好起點）
                edge.SystemAbbreviation = !string.IsNullOrWhiteSpace(start?.SystemAbbreviation) ? start?.SystemAbbreviation : end?.SystemAbbreviation;
                edge.SystemName = !string.IsNullOrWhiteSpace(start?.SystemName) ? start?.SystemName : end?.SystemName;
                edge.SystemType = !string.IsNullOrWhiteSpace(start?.SystemType) ? start?.SystemType : end?.SystemType;
                data.Edges.Add(edge);
            }

            // 4.5 依需要：對「度數=2 的 IfcPipeFitting」做穿越配件連線（ThroughFitting）
            // 僅在啟用選項時進行，避免 Tee/Cross 被誤連；此處限制為度數=2。
            if (ThroughFittingRewireEnabled)
            {
                try
                {
                    // 建立快速查詢以避免重複邊
                    var existingPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in data.Edges)
                    {
                        var s = e.StartNodeId; var t = e.EndNodeId;
                        if (string.Compare(s, t, StringComparison.Ordinal) > 0) (s, t) = (t, s);
                        existingPairs.Add($"{s}__{t}");
                    }

                    // 篩選出 Fitting 節點且度數==2
                    var fittings = data.Nodes.Where(n => n.NodeKind == SchematicNode.SchematicNodeKind.Fitting && n.Edges.Count == 2).ToList();
                    foreach (var fit in fittings)
                    {
                        var e1 = fit.Edges[0];
                        var e2 = fit.Edges[1];
                        var n1 = ReferenceEquals(e1.StartNode, fit) ? e1.EndNode : e1.StartNode;
                        var n2 = ReferenceEquals(e2.StartNode, fit) ? e2.EndNode : e2.StartNode;
                        if (n1 == null || n2 == null) continue;
                        if (ReferenceEquals(n1, n2)) continue;

                        // 檢查是否已有 n1-n2 之間的邊
                        var aId = n1.Id; var bId = n2.Id;
                        if (string.Compare(aId, bId, StringComparison.Ordinal) > 0) (aId, bId) = (bId, aId);
                        var key = $"{aId}__{bId}";
                        if (existingPairs.Contains(key)) continue;

                        // 建立推導邊（Rewired / ThroughFitting）
                        var edge = new SchematicEdge
                        {
                            Id = $"TF_{aId}_{bId}_{data.Edges.Count}",
                            StartNode = n1,
                            EndNode = n2,
                            StartNodeId = n1.Id,
                            EndNodeId = n2.Id,
                            // 將 Connection/Entity 指向該 fitting 實體以利追蹤（可視為來源）
                            Connection = fit.Entity ?? default!,
                            Entity = fit.Entity ?? default!,
                            IsInferred = true,
                            Origin = SchematicEdge.EdgeOriginKind.Rewired,
                            SystemName = fit.SystemName ?? n1.SystemName ?? n2.SystemName,
                            SystemAbbreviation = fit.SystemAbbreviation ?? n1.SystemAbbreviation ?? n2.SystemAbbreviation,
                            SystemType = fit.SystemType ?? n1.SystemType ?? n2.SystemType
                        };
                        data.Edges.Add(edge);
                        n1.Edges.Add(edge);
                        n2.Edges.Add(edge);
                        existingPairs.Add(key);
                    }
                }
                catch { }
            }

            // 樓層資訊
            try { PopulateLevels(ifcModel, data); } catch { }
            return Task.FromResult(data);
        }

        // 兼容封裝：舊命名 BuildP5PointProjectionAsync → 轉呼叫新的 CheckProjectionCoverageAsync
        public Task<ProjectionCoverageReport> BuildP5PointProjectionAsync(
            IStepModel ifcModel,
            double canvasWidth,
            double canvasHeight,
            double padding,
            UserProjectionPlane? plane = null)
            => CheckProjectionCoverageAsync(ifcModel, canvasWidth, canvasHeight, padding, plane);

        // 進一步相容：無參數版本，回傳 SchematicData（僅節點）
        // 作法：重算投影 + Fit-to-Canvas，建立三類元素的節點（無邊），並在 Metadata 中附上 Coverage 報表 JSON。
        public async Task<SchematicData> BuildP5PointProjectionAsync(IStepModel ifcModel)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));
            // 預設畫布（與 ViewModel 常用值一致）
            double canvasWidth = 1600, canvasHeight = 1000, padding = 40;

            // 1) 收集三類元素
            var all = new List<(IIfcProduct prod, string ifcType)>();
            try { foreach (var x in ifcModel.Instances.OfType<IIfcPipeSegment>()) all.Add((x, x.ExpressType?.Name ?? "IfcPipeSegment")); } catch { }
            try { foreach (var x in ifcModel.Instances.OfType<IIfcPipeFitting>()) all.Add((x, x.ExpressType?.Name ?? "IfcPipeFitting")); } catch { }
            try { foreach (var x in ifcModel.Instances.OfType<IIfcFlowTerminal>()) all.Add((x, x.ExpressType?.Name ?? "IfcFlowTerminal")); } catch { }

            var data = new SchematicData();
            if (all.Count == 0)
            {
                data.SystemName = "P5: No elements";
                return data;
            }

            // 2) 自動平面：最小跨度軸剔除
            var sampleElems = all.Take(100).Select(t => (IIfcElement)t.prod).ToList();
            var plane = BestAxisPlane(sampleElems);

            // 3) 投影（未 fit）
            static (double u, double v) Proj(Point3D pt, ProjectionPlane plane)
                => plane switch
                {
                    ProjectionPlane.XZ => (pt.X, pt.Z),
                    ProjectionPlane.YZ => (pt.Y, pt.Z),
                    _ => (pt.X, pt.Y)
                };

            var uv = new List<(double u, double v, IIfcProduct prod, string ifcType, Point3D p3)>();
            foreach (var it in all)
            {
                try
                {
                    var p3 = GetElementPoint(it.prod);
                    var (u, v) = Proj(p3, plane);
                    uv.Add((u, v, it.prod, it.ifcType, p3));
                }
                catch { }
            }

            if (uv.Count == 0) return data;

            // 4) Fit-to-Canvas
            double minU = uv.Min(t => t.u), maxU = uv.Max(t => t.u);
            double minV = uv.Min(t => t.v), maxV = uv.Max(t => t.v);
            double spanU = Math.Max(1e-9, maxU - minU);
            double spanV = Math.Max(1e-9, maxV - minV);
            double availW = Math.Max(1e-6, canvasWidth - 2 * padding);
            double availH = Math.Max(1e-6, canvasHeight - 2 * padding);
            double scale = Math.Min(availW / spanU, availH / spanV);
            double offsetX = padding - minU * scale;
            double offsetY = padding - minV * scale;

            int inCount = 0, outCount = 0;
            foreach (var t in uv)
            {
                double xC = offsetX + t.u * scale;
                double yC = offsetY + t.v * scale;
                bool inside = xC >= 0 && xC <= canvasWidth && yC >= 0 && yC <= canvasHeight;
                if (inside) inCount++; else outCount++;

                // 建立節點（以實體 Label 當 Id）
                string id = ((t.prod as IPersistEntity)?.EntityLabel ?? 0).ToString();
                string name = SafeName(t.prod as IIfcRoot);
                var node = new SchematicNode
                {
                    Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString() : id,
                    Name = string.IsNullOrWhiteSpace(name) ? t.ifcType : name,
                    IfcType = t.ifcType,
                    Position3D = t.p3,
                    Position2D = new System.Windows.Point(xC, yC),
                    Entity = (t.prod as IPersistEntity) ?? default!
                };
                ClassifyNodeKind(node);
                data.Nodes.Add(node);
            }

            // 5) Coverage 報表（放入 Metadata）
            var report = await CheckProjectionCoverageAsync(ifcModel, canvasWidth, canvasHeight, padding,
                plane: plane == ProjectionPlane.XY ? UserProjectionPlane.XY : (plane == ProjectionPlane.XZ ? UserProjectionPlane.XZ : UserProjectionPlane.YZ));
            try { data.Metadata["ProjectionCoverage"] = System.Text.Json.JsonSerializer.Serialize(report); } catch { }
            data.SystemName = $"P5 Points ({plane})";
            return data;
        }

        /// <summary>
        /// 檢查 IfcPipeSegment / IfcPipeFitting / IfcFlowTerminal 代表點的 3D→2D 投影是否能正常落在 Canvas 範圍內。
        /// - 3D 點：使用元素的 LocalPlacement 世界原點（GetElementPoint）。
        /// - 投影平面：可指定 XY/XZ/YZ；若未指定，使用最小跨度軸剔除（BestAxisPlane）。
        /// - Fit-to-Canvas：將所有 2D 點以 padding 內縮後縮放至畫布；回傳每點 Canvas 座標與 InCanvas 標記。
        /// </summary>
        public Task<ProjectionCoverageReport> CheckProjectionCoverageAsync(IStepModel ifcModel, double canvasWidth, double canvasHeight, double padding, UserProjectionPlane? plane = null)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));
            if (canvasWidth <= 0 || canvasHeight <= 0) throw new ArgumentOutOfRangeException("Canvas size must be positive.");

            return Task.Run(() =>
            {
                // 1) 收集三類元素與 3D 點
                var all = new List<(IIfcProduct prod, string ifcType)>();
                int nSeg = 0, nFit = 0, nTerm = 0;
                try { foreach (var x in ifcModel.Instances.OfType<IIfcPipeSegment>()) { all.Add((x, x.ExpressType?.Name ?? "IfcPipeSegment")); nSeg++; } } catch { }
                try { foreach (var x in ifcModel.Instances.OfType<IIfcPipeFitting>()) { all.Add((x, x.ExpressType?.Name ?? "IfcPipeFitting")); nFit++; } } catch { }
                try { foreach (var x in ifcModel.Instances.OfType<IIfcFlowTerminal>()) { all.Add((x, x.ExpressType?.Name ?? "IfcFlowTerminal")); nTerm++; } } catch { }

                if (all.Count == 0)
                {
                    return new ProjectionCoverageReport(
                        CanvasWidth: canvasWidth,
                        CanvasHeight: canvasHeight,
                        Padding: padding,
                        ProjectionPlane: (plane?.ToString() ?? "AUTO"),
                        Total: 0,
                        InCount: 0,
                        OutCount: 0,
                        Segments: nSeg,
                        Fittings: nFit,
                        Terminals: nTerm,
                        Samples: Array.Empty<Element2DPoint>()
                    );
                }

                // 2) 決定投影平面
                ProjectionPlane p;
                if (plane.HasValue)
                {
                    p = plane.Value switch
                    {
                        UserProjectionPlane.XZ => ProjectionPlane.XZ,
                        UserProjectionPlane.YZ => ProjectionPlane.YZ,
                        _ => ProjectionPlane.XY
                    };
                }
                else
                {
                    // auto：取部分樣本決定最小跨度軸
                    var sampleElems = all.Take(100).Select(t => (IIfcElement)t.prod).ToList();
                    p = BestAxisPlane(sampleElems);
                }

                // 3) 投影到 2D（未 fit）
                static (double u, double v) Proj(Point3D pt, ProjectionPlane plane)
                    => plane switch
                    {
                        ProjectionPlane.XZ => (pt.X, pt.Z),
                        ProjectionPlane.YZ => (pt.Y, pt.Z),
                        _ => (pt.X, pt.Y)
                    };

                var uvList = new List<((double u, double v) uv, (IIfcProduct prod, string ifcType) src, Point3D p3)>();
                foreach (var it in all)
                {
                    try
                    {
                        var p3 = GetElementPoint(it.prod);
                        var uv = Proj(p3, p);
                        uvList.Add((uv, it, p3));
                    }
                    catch { }
                }

                if (uvList.Count == 0)
                {
                    return new ProjectionCoverageReport(
                        CanvasWidth: canvasWidth,
                        CanvasHeight: canvasHeight,
                        Padding: padding,
                        ProjectionPlane: p.ToString(),
                        Total: 0,
                        InCount: 0,
                        OutCount: 0,
                        Segments: nSeg,
                        Fittings: nFit,
                        Terminals: nTerm,
                        Samples: Array.Empty<Element2DPoint>()
                    );
                }

                // 4) Fit-to-Canvas（padding 內縮）
                double minU = uvList.Min(t => t.uv.u);
                double maxU = uvList.Max(t => t.uv.u);
                double minV = uvList.Min(t => t.uv.v);
                double maxV = uvList.Max(t => t.uv.v);
                double spanU = Math.Max(1e-9, maxU - minU);
                double spanV = Math.Max(1e-9, maxV - minV);
                double availW = Math.Max(1e-6, canvasWidth - 2 * padding);
                double availH = Math.Max(1e-6, canvasHeight - 2 * padding);
                double scale = Math.Min(availW / spanU, availH / spanV);
                double offsetX = padding - minU * scale;
                double offsetY = padding - minV * scale;

                int inCount = 0, outCount = 0;
                var sample = new List<Element2DPoint>();
                foreach (var t in uvList)
                {
                    double xC = offsetX + t.uv.u * scale;
                    double yC = offsetY + t.uv.v * scale;
                    bool inside = xC >= 0 && xC <= canvasWidth && yC >= 0 && yC <= canvasHeight;
                    if (inside) inCount++; else outCount++;

                    // 只保留部分樣本（避免爆量）
                    if (sample.Count < 200)
                    {
                        int lbl = 0; string? name = null;
                        try { lbl = (t.src.prod as IPersistEntity)?.EntityLabel ?? 0; } catch { }
                        try { name = IfcStringHelper.FromValue((t.src.prod as IIfcRoot)?.Name); } catch { }
                        sample.Add(new Element2DPoint(
                            Label: lbl,
                            IfcType: t.src.ifcType,
                            Name: name,
                            X2D: t.uv.u,
                            Y2D: t.uv.v,
                            XCanvas: xC,
                            YCanvas: yC,
                            InCanvas: inside
                        ));
                    }
                }

                return new ProjectionCoverageReport(
                    CanvasWidth: canvasWidth,
                    CanvasHeight: canvasHeight,
                    Padding: padding,
                    ProjectionPlane: p.ToString(),
                    Total: uvList.Count,
                    InCount: inCount,
                    OutCount: outCount,
                    Segments: nSeg,
                    Fittings: nFit,
                    Terminals: nTerm,
                    Samples: sample
                );
            });
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
                    var node = new SchematicNode
                    {
                        Id = $"port:{(port as IPersistEntity)?.EntityLabel ?? (++idx)}",
                        Name = IfcStringHelper.FromValue((port as IIfcRoot)?.Name) ?? "Port",
                        IfcType = port?.ExpressType?.Name ?? "IfcPort",
                        Position3D = pt3,
                        Position2D = new System.Windows.Point(pt3.X, pt3.Y),
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
            // 樓層資訊
            try { PopulateLevels(ifcModel, data); } catch { }
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
            // 樓層資訊
            try { PopulateLevels(ifcModel, data); } catch { }
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
                // 1) 直接嘗試 Port 自身的 ObjectPlacement（若 schema 支援）→ 使用完整 Placement 鏈求世界座標
                var prodPort = port as IIfcProduct;
                if (prodPort?.ObjectPlacement is IIfcObjectPlacement portPlacement)
                {
                    var basis = GetBasisFromPlacement(portPlacement);
                    return basis.O;
                }

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
                // 嘗試直接 dynamic 取 RelatedElement（常見）與 RelatingElement（部分資料供應商）
                try { return (contained as dynamic)?.RelatedElement as IIfcProduct; } catch { }
                try { return (contained as dynamic)?.RelatingElement as IIfcProduct; } catch { }
                // 若 ContainedIn 是集合（極少數情形），取第一筆能解析的
                if (contained is System.Collections.IEnumerable many)
                {
                    foreach (var item in many)
                    {
                        try
                        {
                            var prod = (item as dynamic)?.RelatedElement as IIfcProduct;
                            if (prod != null) return prod;
                        }
                        catch { }
                        try
                        {
                            var prod = (item as dynamic)?.RelatingElement as IIfcProduct;
                            if (prod != null) return prod;
                        }
                        catch { }
                    }
                }
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

            var node = new SchematicNode
            {
                Id = id,
                Name = name,
                IfcType = type,
                Position3D = p3,
                Position2D = p2,
                Entity = elem as IPersistEntity ?? default!
            };
            ClassifyNodeKind(node);
            return node;
        }

        private static SchematicNode CreateNodeFromProduct(IIfcProduct prod)
        {
            var root = prod as IIfcRoot;
            var id = root != null ? (IfcStringHelper.FromValue(root.GlobalId) ?? (prod as IPersistEntity)?.EntityLabel.ToString() ?? Guid.NewGuid().ToString()) : (prod as IPersistEntity)?.EntityLabel.ToString() ?? Guid.NewGuid().ToString();
            var name = root != null ? (IfcStringHelper.FromValue(root.Name) ?? prod.GetType().Name) : prod.GetType().Name;
            var type = prod.ExpressType?.Name ?? prod.GetType().Name;

            var p3 = GetElementPoint(prod);
            var p2 = new Point(p3.X, p3.Y);

            var node = new SchematicNode
            {
                Id = id,
                Name = name,
                IfcType = type,
                Position3D = p3,
                Position2D = p2,
                Entity = prod as IPersistEntity ?? default!
            };
            ClassifyNodeKind(node);
            return node;
        }

        private static void ClassifyNodeKind(SchematicNode node)
        {
            try
            {
                var t = (node.IfcType ?? string.Empty).ToLowerInvariant();
                if (t.Contains("flowterminal")) node.NodeKind = SchematicNode.SchematicNodeKind.Terminal;
                else if (t.Contains("valve")) node.NodeKind = SchematicNode.SchematicNodeKind.Valve;
                else if (t.Contains("flowfitting") || t.Contains("fitting")) node.NodeKind = SchematicNode.SchematicNodeKind.Fitting;
                else if (t.Contains("pipesection") || t.Contains("pipesegment") || t.Contains("flowsegment") || t.Contains("pipesegment")) node.NodeKind = SchematicNode.SchematicNodeKind.PipeEnd; // PipeEnd 之後仍會細分為兩端點
                else node.NodeKind = SchematicNode.SchematicNodeKind.Other;
            }
            catch { node.NodeKind = SchematicNode.SchematicNodeKind.Other; }
        }

        private static Point3D GetElementPoint(IIfcProduct prod)
        {
            // 使用完整 Placement 鏈（包含 PlacementRelTo）計算世界座標原點
            try
            {
                if (prod?.ObjectPlacement is IIfcObjectPlacement placement)
                {
                    var basis = GetBasisFromPlacement(placement);
                    return basis.O;
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
                // 2) 嵌套關聯：IfcRelNests（Revit 常見）
                if (de.IsNestedBy != null)
                {
                    foreach (var nestRel in de.IsNestedBy)
                    {
                        foreach (var ro in nestRel.RelatedObjects?.OfType<IIfcDistributionPort>() ?? Enumerable.Empty<IIfcDistributionPort>())
                        {
                            ports.Add(ro);
                        }
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
        // 舊簽章：維持相容；內部轉呼叫含 options 版本
        public Task<SchematicData> GeneratePipeAxesAsync(IModel ifcModel, string plane, bool flipY = true)
            => GeneratePipeAxesAsync(ifcModel, plane, flipY, null);

        /// <summary>
        /// 新版：支援 PipeAxesOptions（P4：IncludeFittings）。
        /// </summary>
        public Task<SchematicData> GeneratePipeAxesAsync(IModel ifcModel, string plane, bool flipY, PipeAxesOptions? options)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));
            return Task.Run(() =>
            {
                var data = new SchematicData();
                var planeNorm = (plane ?? "XY").Trim().ToUpperInvariant();
                if (planeNorm != "XY" && planeNorm != "XZ" && planeNorm != "YZ") planeNorm = "XY";
                double unitScaleMm = GetLengthToMillimetreScale(ifcModel);

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
                        // Pset 後援：補上系統資訊（節點）
                        {
                            string? sn = null, sa = null, st = null;
                            PopulateSystemFromPsets(seg as IIfcProduct, ref sn, ref sa, ref st);
                            startNode.SystemName = sn; startNode.SystemAbbreviation = sa; startNode.SystemType = st;
                            endNode.SystemName = sn; endNode.SystemAbbreviation = sa; endNode.SystemType = st;
                        }
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
                        // Pset 後援：補上系統資訊（邊）
                        edge.SystemAbbreviation = startNode.SystemAbbreviation ?? endNode.SystemAbbreviation;
                        edge.SystemName = startNode.SystemName ?? endNode.SystemName;
                        edge.SystemType = startNode.SystemType ?? endNode.SystemType;
                        // Sprint 1：方向/管徑/樓層
                        edge.Orientation = ClassifyOrientationFromVector(dWorld);
                        ExtractDiameters(ifcModel, seg, out var dn, out var dOuter, out var srcDn, out var srcDo);
                        edge.NominalDiameterMm = dn; edge.OuterDiameterMm = dOuter;
                        edge.ValueSourceNominalDiameter = srcDn; edge.ValueSourceOuterDiameter = srcDo;
                        edge.LevelName = GetLevelNameForProduct(ifcModel, seg);
                        data.Edges.Add(edge);
                        startNode.Edges.Add(edge); endNode.Edges.Add(edge);
                        segCount++;
                    }
                    catch
                    {
                        segSkipped++;
                    }
                }

                // (P4 S1) 追加：Fitting 節點（不影響既有 segment 邊）
                try
                {
                    if (options?.IncludeFittings == true)
                    {
                        var fittings = ifcModel.Instances.OfType<IIfcPipeFitting>()?.ToList() ?? new List<IIfcPipeFitting>();
                        int added = 0;
                        foreach (var ft in fittings)
                        {
                            try
                            {
                                var p3 = GetElementPoint(ft);
                                // 簡易重算 2D 投影（與前面一致）
                                (double px, double py) projLocal(System.Windows.Media.Media3D.Point3D p)
                                {
                                    return planeNorm switch
                                    {
                                        "XZ" => (p.X, flipY ? -p.Z : p.Z),
                                        "YZ" => (p.Y, flipY ? -p.Z : p.Z),
                                        _ => (p.X, flipY ? -p.Y : p.Y),
                                    };
                                }
                                var uv = projLocal(p3);
                                string id = IfcStringHelper.FromValue((ft as IIfcRoot)?.GlobalId) ?? (ft as IPersistEntity)?.EntityLabel.ToString() ?? Guid.NewGuid().ToString();
                                string name = IfcStringHelper.FromValue((ft as IIfcRoot)?.Name) ?? "PipeFitting";
                                var node = new SchematicNode
                                {
                                    Id = id,
                                    Name = name,
                                    IfcType = ft.ExpressType?.Name ?? "IfcPipeFitting",
                                    Position3D = p3,
                                    Position2D = new System.Windows.Point(uv.px, uv.py),
                                    Entity = ft as IPersistEntity ?? default!
                                };
                                // 系統資訊後援（保留與 segment 一致策略）
                                string? sn = null, sa = null, st = null;
                                PopulateSystemFromPsets(ft as IIfcProduct, ref sn, ref sa, ref st);
                                node.SystemName = sn; node.SystemAbbreviation = sa; node.SystemType = st;
                                data.Nodes.Add(node);
                                added++;
                            }
                            catch { }
                        }
                        try { System.Diagnostics.Trace.WriteLine($"[Service][PipeAxes][P4] Fittings added={added}"); } catch { }
                    }
                }
                catch { }

                System.Diagnostics.Trace.WriteLine($"[Service][PipeAxes] Segments processed={segments.Count}, linesBuilt={segCount}, skipped={segSkipped}, plane={planeNorm}, fittings={(options?.IncludeFittings == true ? "on" : "off")}");
                data.SystemName = $"PipeAxes({planeNorm})" + (options?.IncludeFittings == true ? "+Fittings" : "");
                // 樓層資訊
                try { PopulateLevels(ifcModel, data); } catch { }
                return data;
            });
        }

        // 舊簽章保留（相容）：不帶 options
        public Task<SchematicData> GeneratePipeAxesWithTerminalsAsync(IModel ifcModel, string plane, bool flipY = true)
            => GeneratePipeAxesWithTerminalsAsync(ifcModel, plane, flipY, null);

        /// <summary>
        /// 新版：支援 PipeAxesOptions（IncludeFittings 會透過下層生成一併處理；IncludeTerminals 控制是否加終端節點）。
        /// </summary>
        public async Task<SchematicData> GeneratePipeAxesWithTerminalsAsync(IModel ifcModel, string plane, bool flipY, PipeAxesOptions? options)
        {
            if (ifcModel == null) throw new ArgumentNullException(nameof(ifcModel));
            // 先建出管段軸線資料（含可能的 fittings）
            var data = await GeneratePipeAxesAsync(ifcModel, plane, flipY, options);

            // 收集 Terminal 3D 定位點
            var termPts = await GetFlowTerminalAnchorsAsync(ifcModel);
            var termDetails = LastFlowTerminalAnchorDetails?.ToList() ?? new List<FlowTerminalAnchorDetail>();

            // 在相同平面下做 3D→2D 投影（與 GeneratePipeAxesAsync 使用一致的對應）
            (double px, double py) proj(Point3D p)
            {
                switch ((plane ?? "XY").Trim().ToUpperInvariant())
                {
                    case "XZ": return (p.X, flipY ? -p.Z : p.Z);
                    case "YZ": return (p.Y, flipY ? -p.Z : p.Z);
                    default:    return (p.X, flipY ? -p.Y : p.Y);
                }
            }

            if (options?.IncludeTerminals != false) // 預設 true
            {
                for (int i = 0; i < termPts.Count; i++)
                {
                    var p3 = termPts[i];
                    var uv = proj(p3);
                    var d = (i < termDetails.Count) ? termDetails[i] : null;
                    var n = new SchematicNode
                    {
                        Id = d?.TerminalLabel != null ? $"term:{d.TerminalLabel}" : $"term:{i}",
                        Name = d?.TerminalName ?? $"Terminal-{i}",
                        IfcType = "IfcFlowTerminal",
                        Position3D = p3,
                        Position2D = new System.Windows.Point(uv.px, uv.py),
                        HostIfcType = "IfcFlowTerminal",
                        HostLabel = d?.TerminalLabel
                    };
                    // 嘗試掛回實體（若能取得）
                    try
                    {
                        if (d?.TerminalLabel is int lbl && lbl != 0)
                        {
                            var pe = ifcModel.Instances[lbl] as IPersistEntity;
                            if (pe != null) n.Entity = pe;
                            if (pe is IIfcProduct prod)
                            {
                                n.LevelName = GetLevelNameForProduct(ifcModel, prod);
                                // Pset 後援：終端的系統資訊
                                string? sn = null, sa = null, st = null;
                                PopulateSystemFromPsets(prod, ref sn, ref sa, ref st);
                                n.SystemName = sn; n.SystemAbbreviation = sa; n.SystemType = st;
                            }
                        }
                    }
                    catch { }
                    data.Nodes.Add(n);
                }
            }

            data.SystemName = $"PipeAxes+Terminals({(plane ?? "XY").Trim().ToUpperInvariant()})" + (options?.IncludeFittings == true ? "+Fittings" : "");
            return data;
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
