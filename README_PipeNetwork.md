# Pipe Network Quick Build (PSC P4)

本文檔描述 `SchematicService.BuildPipeNetworkAsync` 於 PSC P4 (Quick) 的管網建構流程、資料模型、選項與完整性指標，方便後續調整與除錯。

## 目標
在 **可接受的時間** 內，從 IFC 模型抽取管線(segments)、配件(fittings / valves)、端點與連接關係，構建一張 2D/3D schematic 圖：
- 優先利用 IFC 既有語義 (Ports, RelAssignsToGroup, RelConnectsPorts)
- 缺資料時使用幾何鄰近補橋 (Geometry fallback)
- 用 *Fitting Star Rewire* 快速「穿過」fitting 讓 run 更完整
- 拆分每個單一節點的 PipeSegment 為 A/B 虛擬端點以利後續延伸
- 計算系統/SystemKey、連通分量 (Runs)、統計並輸出報表

## 主要資料類型 (精簡)
| 類別 | 說明 | 關鍵欄位 |
|------|------|----------|
| SchematicNode | 圖上節點 (PipeEnd / Fitting / Valve / Terminal / 無類型) | Id, NodeKind, Position2D/3D, SystemKey, RunId, IsSegmentEndpoint |
| SchematicEdge | 連線 | Id, Start/EndNodeId, Origin(Segment/Ports/Geometry/Rewired), LengthMm, Nominal/OuterDiameter |
| PipeNetworkOptions | 建構選項 | IncludeFittings, UsePorts, MergeToleranceMm, MaxFittingStarDegree, MaxFittingPairs, PropagateSystemFromNeighbors |
| GraphBuildReport | 報表 | TotalNodes, TotalEdges, PortEdges, GeometryEdges, SegmentEdges, RewiredEdges, Systems, Runs, RunMaxNodes, RunAvgNodes, ConvertedSegments, EndpointNodes, CrossSystemEdges, IsolatedNodes, FittingAvgDegree, FittingMaxDegree, SegmentAvgLengthMm, PropagatedSystemAssignments, TolMm, Notes |

## 流程步驟
1. 收集元素 (IfcElement)
   - 過濾出與管網相關類型 (PipeSegment / FlowFitting / Valve / 末端元件等)
   - 轉為單一中心節點，初始 SystemKey＝(SystemAbbreviation or SystemName or "(未指定)")
2. Segment 端點拆分
   - 以 `MergeToleranceMm` 作為虛擬半長，中心點左右生成 A/B 端點；原中心節點標記 `IsSegmentCenter`
   - 建立一條 SegmentEdge (Origin=Segment, Length=虛擬長度、附直徑)
3. Ports 邊 (可關閉)
   - 使用 `IfcRelConnectsPorts` 產生 Port→Port 邊；若找不到對應節點再保底全模型掃描
4. 幾何補橋 (Geometry fallback)
   - 為未被 Ports 連結的鄰近節點建立推論邊 (Origin=Geometry)；控制上限避免 N^2 爆炸
5. Fitting Star Rewire
   - 對每個 Fitting：
     1. 找距離 ≤ tol 的同 SystemKey 節點集合
     2. 取距離最小前 `MaxFittingStarDegree` 節點建立 star 邊 (node↔fitting)
     3. 在 star 節點之間建立 (a,fitting,b) 隱含組合，計數 ≤ `MaxFittingPairs`
   - 目的：穿過 Fitting 提供 run 的潛在直達路徑 (即使 IFC 資料缺 Port)
6. 系統資訊抽取與正規化
   - 讀取 `IfcRelAssignsToGroup` 對應的 `IIfcSystem` 回填 Name/Abbreviation/Type → SystemKey
   - 正規化 (Trim + Collapse 空白 + Upper)
   - (新增) 鄰接傳播：未指定 SystemKey 且所有鄰居同一 SystemKey 時，自動繼承
7. Run (Connected Components)
   - 先依 SystemKey 分桶，再對每個桶 BFS/DFS 計算 RunId 及節點數統計 (RunMaxNodes / RunAvgNodes)
8. Edge 去重 (優先級)
   - 若 (Start,End) 重複：保留優先級 Segment > Ports > Rewired > Geometry
9. 完整性 / 品質指標
   - CrossSystemEdges：兩端 SystemKey 不同的邊
   - IsolatedNodes：無邊節點
   - FittingAvgDegree / FittingMaxDegree：評估 star 程度
   - PropagatedSystemAssignments：透過鄰接傳播補標註的節點數
10. 報表與備註 Notes 組裝
    - 記錄策略、執行時間、主要統計、容差、參數設定等

## 可能造成「Pipe Segment 未連上系統」的成因
| 類別 | 說明 | 排查建議 |
|------|------|----------|
| IFC 資料缺失 | 該 PipeSegment 未被任何 `IfcRelAssignsToGroup` 指到 | 用 IFC 檢視器查該 Element 是否在 System 組內 |
| SystemKey 正規化後分裂 | 原本名稱含全形/多空白導致正規化成不同字串 | 開啟報表 `Notes` 檢視是否多個相近 SystemKey |
| Fitting Star 過嚴 | `MaxFittingStarDegree` 太小未建立預期邊 | 調大參數 (例如 12) 重建 | 
| Ports 關閉 | `UsePorts=false` 導致缺少語義邊 | 開啟 Ports 或確認 geometry fallback 是否足夠 |
| 鄰接傳播未啟用 | PropagateSystemFromNeighbors 被關掉 | 開啟並重建 |
| System 斷鏈 | 中間節點因去重或未建立補橋導致孤立 | 檢查 IsolatedNodes、嘗試增加 MergeToleranceMm |

## 參數調校建議
- MergeToleranceMm：過小 → 端點分裂不易連成 star；過大 → 可能誤連不同管線
- MaxFittingStarDegree / MaxFittingPairs：控制複雜度；視資料稀疏程度調整
- PropagateSystemFromNeighbors：預設開啟；若想保守審核再關閉

## 未來擴充想法
1. 真實 Segment 長度：用幾何方向/實際端點 (若 IFC 有 Polyline) 代替虛擬長度
2. 多系統歧義處理：若鄰居系統 >1 不填補，並輸出 `AmbiguousSystemNodes` 列表
3. 直徑一致性驗證：檢查 Rewired chain 上是否存在劇烈跳變
4. UI 參數面板：即時調整並熱刷新
5. 快速診斷模式：輸出每個未指定 SystemKey 節點的鄰居系統清單

## 報表欄位速覽
```
TotalNodes / TotalEdges
PortEdges / GeometryEdges / SegmentEdges / RewiredEdges
ConvertedSegments / EndpointNodes / CollapsedEndpoints
Systems / Runs / RunMaxNodes / RunAvgNodes
CrossSystemEdges / IsolatedNodes
FittingAvgDegree / FittingMaxDegree
SegmentAvgLengthMm / TolMm
PropagatedSystemAssignments
Notes (策略與錯誤摘要)
```

## 使用範例（程式）
```csharp
var service = new SchematicService();
var (graph, report) = await service.BuildPipeNetworkAsync(model, new SchematicService.PipeNetworkOptions {
    MergeToleranceMm = 80,
    MaxFittingStarDegree = 10,
    MaxFittingPairs = 30,
    PropagateSystemFromNeighbors = true
});
Console.WriteLine($"Runs={report.Runs} Systems={report.Systems} Propagated={report.PropagatedSystemAssignments}");
```

---
若需加入更多完整性指標或輸出格式，請在報表類別擴充後於 Notes 中加上標籤，保持簡潔可檢索性。
