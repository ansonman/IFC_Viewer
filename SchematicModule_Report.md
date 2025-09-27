````markdown
# 原理圖生成模組 - 開發與分析報告

報告日期: 2025-09-24（含 V1 手動平面與 Port 詳細診斷更新）

## 1. MVP 與目前能力
- Node：Id/Name/IfcType/Position3D、Position2D、`Entity` 參照、`Edges`
- Edge：Id/StartNode/EndNode、`Entity` 參照、`Connection`（指向 `IfcRelConnectsPorts`）、`IsInferred`
- Graph：`SchematicData` 內含 `Nodes`、`Edges`、`SystemName`、`SystemEntity`
- 來源：`IIfcDistributionElement` / `IIfcPipeSegment` 建立節點（以 `EntityLabel` 去重）
- 拓撲：優先依 `IfcRelConnectsPorts` 建立連線，Port 與節點以 `HasPorts` 映射
- 座標：取 LocalPlacement（`IfcAxis2Placement3D.Location`）之 3D 位置；初步投影至 2D
- 顯示：Canvas + ItemsControl 呈現；依 IfcType 分色；力導向佈局（約 200 次迭代）；邊線以 `<Line>` 繪製（`StartNode.Position2D` → `EndNode.Position2D`）
- 互動：點擊節點/邊 → 視窗層觸發 3D 高亮與 ZoomSelected（透過集合高亮 API）

## 2. Ports/Edges 可用性與回退策略（SOP：不啟用幾何推斷）
- 主要依賴 `IfcRelConnectsPorts` 建立邊；若該關係缺失，視圖顯示節點（Ports）並於 UI 顯示「僅顯示節點」提示 Banner。
- AS 流程（兩段參考管件）之 Port 收集具三層回退：
  1) 由系統（`IfcRelAssignsToGroup`）直接收集 Ports
  2) 由系統成員（`IIfcDistributionElement.HasPorts`）收集
  3) 仍為 0 時，保底收集全模型 `IfcDistributionPort`
- 邊建立的回退：先以已收集節點對應的 `IfcRelConnectsPorts` 建線；若為 0，再對全模型的 `IfcRelConnectsPorts` 嘗試匹配兩端節點建立邊。
- `IsInferred` 欄位仍保留以供未來幾何鄰近性推斷之用（目前關閉）。

## 3. 多選同步與右側面板
- SelectionService：可同時選多個節點/邊，同步至 3D 與 TreeView 多高亮
- 右側面板：
  - 多選 → 顯示摘要（數量、類型分佈等）
  - 單選 → 顯示完整屬性
  - 3D 多選高亮：當 2D 多選時，系統會將選擇集合的 EntityLabel 轉換為 3D 控制項所需集合（`List<int>` 或 `List<IPersistEntity>`），並進行輕量更新以即時呈現。

## 4. SOP 2.0：系統優先、Ports-only（2025-09-23）
- 後端：`SchematicService.GenerateFromSystemsAsync(IStepModel)`
  - 發現 `IIfcSystem`/`IIfcDistributionSystem`；每個系統各自生成 `SchematicData`（Nodes/Edges）。
  - 以 `IfcRelAssignsToGroup` 取系統成員 → 建節點 → 以成員 `HasPorts` 建 `Port→Node` map → 依 `IfcRelConnectsPorts` 建 Edge。
  - `Edge.Connection` 指向 `IfcRelConnectsPorts`；`Node.Edges` 回填相鄰邊；`SchematicData.SystemName/SystemEntity` 用於 UI 命名與追溯。
- 前端：
  - `MainViewModel.GenerateSchematicCommand` 採用上述方法；多系統時彈出 `SystemSelectionDialog`。
  - `SchematicView.xaml`：節點以 6px 黑點顯示；邊線以黑色 1.5px `<Line>` 呈現，易於辨識。邊 ItemsControl 已就緒，綁定 `Edge.StartNode.Position2D` 與 `Edge.EndNode.Position2D`。
  - `Edges.Count == 0` 時，顯示「僅顯示節點」提示 Banner。

## 5. 互動與視圖控制
- 縮放/平移：Canvas 應用 `ScaleTransform + TranslateTransform`，支援滾輪縮放與中鍵平移。
- 視圖控制按鈕：重置視圖（復位變換並 RefitToCanvas）、重新布局（力導向 + Refit）。
- Fit-to-Canvas：
  - 一般方法：依 `CanvasWidth/CanvasHeight/CanvasPadding`（預設 1600/1000/40）縮放平移到畫布。
  - 於 `LoadData(...)`：使用固定 800x600 與 padding 20，先更新 `Node.Position2D` 再同步 NodeView。
  - 自動選擇投影面（Best Projection）：計算 X/Y/Z 三軸跨度（max-min），捨棄跨度最小的軸，以其餘兩軸形成的平面作為 2D 投影面（平手偏好順序：XY → XZ → YZ）。此策略套用於 `FitToCanvasBestProjection(...)` 與 `BuildFromData(...)` 的初始種子平面；舊有的「面積最大平面」策略已被取代。

## 6. 既知限制
- 力導向為快速近似：大圖可能需要更多迭代或使用層級式佈局（Sugiyama/MSAGL）
- 避線/重疊：可增補節點盒碰撞回退與曲線/折線路徑
- 邊對應 3D：目前以起點節點代表；後續可改以邊的 `Entity` 或兩端共同父件以更精準

## 7. 下一步
1) 佈局策略切換（力導向/層級式）與參數化（迭代數、理想距離）
2) 節點/邊樣式化（大小、圖示、文字裁切、濾鏡）
3) 3D ↔ 2D 雙向同步（3D 選擇時在原理圖高亮）
4) 若 `SchematicEdge.Entity` 可映射至管段幾何，改以該實體做 Highlight

---

## 8. 今日修補與驗證
- 修補重點：
  - AS 流程改以 `IfcRelConnectsPorts` 建邊；補齊系統不明與空集合的保底回退；新增診斷輸出（Trace）統計 Ports/Edges。
  - `SchematicView.xaml` 調整節點、邊樣式，以提升可視性。
  - 修正空白 XAML 根元素導致的建置錯誤（已補齊最小 Window 標記）。
- 建置狀態：成功（僅 NU1701 相容性警告）。
- 驗證建議：以 `sample/Sample_pipe.ifc` 執行 AS 原理圖；預期可見多個黑點（Ports）與若干黑線（RelConnectsPorts 邊）。若邊為 0，視窗將顯示提示 Banner，同時 Trace 可見收集統計。

---

## 9. V1 手動平面 + Port 詳細診斷（2025-09-24）

此版本（標記：AS 原理圖 V1）聚焦「系統全體 Ports 點雲」的人工平面投影與深入診斷；暫不繪製邊線，強調資料品質與來源追蹤。

### 9.1 功能概述
- 使用者啟動 V1 流程後：
  1) 彈出「選擇系統」對話框（支援多系統複選）
  2) 再彈出「選擇投影平面」對話框（XY / XZ / YZ）
  3) 後端對每個系統執行 Ports 抽取（多層 fallback）→ 投影 → 產生點集合
  4) UI 顯示：僅點（Ellipse）無邊線；並於右側/底部日誌區輸出 per-port 詳細紀錄

### 9.2 Ports 抽取多層策略（新版）
| 層級 | 描述 | 計數統計欄位 |
|------|------|--------------|
| 1 | 成員元素 `HasPorts` | viaHasPorts |
| 2 | `IfcRelNests`（Nested Ports，被包含於元素但不在 HasPorts） | viaNested |
| 3 | 全模型掃描（所有 `IfcDistributionPort`，再過濾系統成員關聯最近者） | viaFallback |

> 舊版僅處理 HasPorts，造成某些模型（僅使用 Nests 關係）完全無點。新版統計三層來源，利於快速評估 IFC 資料完整度。

### 9.3 PortDetail 記錄
為每個投影出的 Port 生成一筆 `PortDetail`：
```
Label, Name, HostEntityLabel, HostIfcType, Source(HasPorts|Nested|Fallback), XYZ(原始), Projected(x,y), HostName
```
同時保存在 `SchematicService.LastPortDetails` 供 UI 或匯出使用。

### 9.4 顏色與 Tooltip 規則
- 顏色：
  - 黑色：宿主為 `IfcPipeSegment`（視為管段端點）
  - 紅色：其他宿主型別（閥件、接頭、末端或無法判斷型別的後援 Port）
- Tooltip 顯示：`Port Name / Label / Host IfcType / Host Label / IsPipeSegment`。

### 9.5 已修復的「全部紅點」問題
症狀：所有點皆呈現紅色，表示判斷 `IsFromPipeSegment` 失效。
根因：前端以 PortLabel → Meta Dictionary 映射，實際渲染順序為點索引序；標籤排序與加入順序不一致導致錯配。
修復：
1. `LoadPointsAsync` 改為以「index 對齊」方式消費 meta list（metaList[i] 對應第 i 個點）。
2. `MainViewModel` 生成 meta 時保持與點加入順序一致；移除中途重組。
結果：黑/紅點分色恢復正常。

### 9.6 限制與後續優化
- 宿主判斷目前僅依 `ContainedIn`（或直屬 DistributionElement）；尚未反向穿透 RelNests 以找出真實宿主 → 導致極端嵌套模型中部分管段端點被標為紅色（Fallback）。
- 點過度重疊時視覺難以分辨；可加入 jitter 或互斥最小距離策略。
- 缺少圖例（Legend）說明黑/紅含意；建議置於視窗頂部或狀態列。

### 9.7 後續建議（Roadmap 增補）
1. RelNests 反向宿主解析：當 `ContainedIn` 缺失時回溯父元素樹。  
2. 匯出：PortDetail → CSV/JSON（單系統或多系統合併）。  
3. 圖例與篩選：切換「僅顯示管段端點」或「全部」。  
4. 重新投影：在不重新查 IFC 的情況下改變平面（XY/XZ/YZ）。  
5. 疊點偵測：自動標示重疊數量（例如加一個小計數 Badge）。  
6. Host 類型色盤擴充：未來可區分 Valve/Fitting/Terminal 等。

---

## 10. 版本里程碑摘要（更新）
| 里程碑 | 日期 | 重點 | 狀態 |
|--------|------|------|------|
| 初始拓撲 (Ports + Edges) | 09-21~22 | 系統優先、力導向、邊線繪製 | ✔ |
| AS Minimal 兩段管件 | 09-23 | 4 點 2 線，多層後援 | ✔ |
| V1 手動平面 Port 點雲 | 09-24 | 多系統、多層 Port 抽取、PortDetail | ✔ |
| RelNests 宿主強化 | (規劃) | 反向宿主精確分類 | ⏳ |
| 匯出 / 圖例 / 篩選 | (規劃) | 可用性與可視化增強 | ⏳ |

---

（本文件後續更新將以「新增章節」方式保持歷史可追溯性）
\n+---\n+## 11. Port 提取機制統一說明（新增 2025-09-24）
本節集中描述目前三條主要流程中（Topology, Systems, AS V1 / AS-min）如何取得與使用 IfcDistributionPort，並說明其差異、限制與後續強化方向。\n\n+### 11.1 方法總覽
| 方法 | 目的 | 節點語意 | 邊語意 | Port 來源層級 | 回退層級 | 是否保留 PortDetail |
|------|------|----------|--------|---------------|----------|----------------------|
| `GenerateTopologyAsync(IModel)` | 全局（不分系統）基礎拓撲 | 元素 (PipeSegment/Fitting/Valve/Terminal) | IfcRelConnectsPorts 映射的元素間連線 | 只透過元素 HasPorts → Port→Node | 無全域掃描 | 否 |
| `GenerateFromSystemsAsync(IStepModel)` | 依系統生成拓撲 | 同上但每系統一份 | 同上 | 只透過系統成員元素 HasPorts | 無（若少 Port 會造成邊減少） | 否 |
| `GetAllPortCoordinatesAsync(IStepModel, IIfcSystem)` (V1) | 系統全 Port 點雲（不建邊） | 單點（Port 本身） | 不產生 | HasPorts + Nested + 全域 fallback | 三層（HasPorts→Nested→Global） | 是 (`LastPortDetails`) |
| AS-min (`GeneratePortPointSchematicFromSegmentsAsync`) | 兩段參考簡化視圖 | Port（兩段管件的端點） | 管段自身兩端連線 | 直接取兩段之 Port；不足則鄰近; 最後全域 | 多重（段→鄰接→全域） | 否 |
| AS V1（平面手選） | 多系統 Port 診斷 | Port | 無 | HasPorts + Nested + 全域 | 三層 | 是 |
\n+### 11.2 Port 來源判定細節
1. HasPorts：直接枚舉元素的 `HasPorts.RelatingPort`。\n+2. Nested：針對元素 `IsNestedBy` 的 `IfcRelNests.RelatedObjects` 過濾 `IfcDistributionPort`。\n+3. Global Fallback（僅 V1 / GetAllPortCoordinatesAsync）：全模型掃描所有 `IfcDistributionPort`，再以：\n+   - 該 Port 的 `Nests.RelatingObject` 是否為系統成員元素，或\n+   - 透過其 `ConnectedTo.RelatedPort` 回溯另一端的 Nests 宿主\n+   來判斷是否屬於系統。\n+\n+### 11.3 Source 欄位與分類
`GetAllPortCoordinatesAsync` 為每個 Port 生成 `PortDetail.Source`：`HasPorts` / `Nested` / `Fallback` / `Unknown`。決策策略：\n+| 情境 | Source | 備註 |
|------|--------|------|
| 元素 HasPorts 直接匹配 | HasPorts | 最高優先，不再繼續檢查 Nested |
| 不在 HasPorts 但在元素 Nests | Nested | Revit 常見巢狀策略 |
| 以上皆否，但通過全域掃描回溯到系統成員 | Fallback | 資料品質警訊，需要 IFC 修補或強化解析 |
| 解析例外/宿主無法確定 | Unknown | 量少時可忽略，量大需調查 |
\n+### 11.4 與 Topology 方法的差異核心
- Topology (`GenerateTopologyAsync` / `GenerateFromSystemsAsync`) 不直接渲染 Ports；Ports 僅作為建立 Node（元素）間 Edge 的橋樑（Port→Node 映射）。缺少 Port 不會顯示孤點，而是造成邊不足。\n+- V1 / AS 系列將 Port 視為第一公民（Node=Port），因此資料品質問題（缺宿主、來源分佈）可直觀呈現。\n+\n+### 11.5 常見資料情境對應
| 症狀 | Topology 效果 | V1 效果 | 研判 | 建議 |
|------|---------------|---------|------|------|
| HasPorts=0 但 Nested 多 | 邊稀疏甚至為 0 | 多數標記 Nested | 模型採 Nested 結構 | 接受或調整建模規範 |
| HasPorts, Nested 皆少，Fallback 高 | 幾乎無邊 | 大量 Fallback | 系統關聯或巢狀遺失 | 補 IFC RelAssignsToGroup / RelNests |
| 管段端點比率低 | 邊數偏低 | 黑點比例低 | 管段 Ports 缺失 | 加入 RelNests 反向宿主解析 |
| 所有點幾乎同座標 | 邊擠壓重疊 | 點雲重疊 | LocalPlacement 異常 | 檢查建模/加 jitter |
\n+### 11.6 已知限制
| 項目 | 描述 | 影響 | 計畫 |
|------|------|------|------|
| 反向宿主解析缺失 | 未回溯 Nested Port 真實父級 | 部分黑點誤標為紅點 | 實作沿 RelNests 向上追溯第一個 DistributionElement |
| Fallback 粗粒度 | 以簡單關聯判斷系統歸屬 | 邊界 Port 可能誤收 | 增加空間距離/系統成員快取二次篩選 |
| 未快取全域 Port | 多系統連續操作重複掃描 | 效能 | 建置一次全域 Port 索引（Label→Port） |
| 疊點無處理 | 重疊視覺難辨 | 可讀性下降 | 實作 jitter/疊點計數 |
\n+### 11.7 強化 Roadmap（聚焦 Port）
1. RelNests 反向宿主解析（提升 `IsPipeSegment` 精度）\n+2. 全域 Port 索引與系統成員快取（降低多系統重複成本）\n+3. PortDetail 匯出（CSV/JSON，多系統合併 + Source 統計摘要）\n+4. 平面快速切換（重用 Raw XYZ + 投影函式）\n+5. 疊點處理：jitter + density cluster 指標輸出\n+\n+### 11.8 測試建議 (Port 抽取)
| 測試類型 | 操作 | 驗證點 |
|----------|------|--------|
| HasPorts 正常 | 載入標準機電 IFC → V1 | viaHasPorts > 0 且 Fallback 低 |
| Nested 專用 | 移除 HasPorts → 僅保留 RelNests | viaNested 上升且無崩潰 |
| 無系統關聯 | 移除 AssignsToGroup | Fallback 占比升高，仍顯示點 |
| 缺宿主 | 刻意刪除部分父元素 | 對應 Port Source=Fallback/Unknown |
| 重疊 | 多個 Port 放同坐標 | 點雲重疊（待後續 jitter） |
\n+### 11.9 風險評估摘要
| 風險 | 可能後果 | 緩解 |
|------|----------|------|
| 全域掃描過多 | 大型模型反覆操作延遲 | 引入快取/索引 |
| 宿主解析失敗 | 黑點統計失真 | 實作反向 RelNests |
| Source 判斷錯誤 | 錯誤診斷 | 增加 Trace + 單元測試 |\n+\n+（本章完）
````
