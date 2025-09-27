# Schematic 模組報告（schematicmodule_report）

本報告說明原理圖（Schematic）模組的目標、資料流程、fallback 策略與目前狀態。

## 目標
- 從 IFC 模型萃取 MEP/Piping 的拓撲結構，生成可互動的 2D 原理圖視圖。
- 與 3D 視圖雙向同步選取（點選節點/邊 → 3D 高亮；3D 點選 → 原理圖標示）。

## 資料流程（SOP：Ports-only）
1. 輸入：`IModel`（xBIM IfcStore）
2. 解析：
  - 節點：聚焦管線相關構件（`IIfcPipeSegment`、`IIfcPipeFitting`、`IIfcFlowTerminal`、`IIfcValve`），以 `EntityLabel` 去重
  - 連線：僅使用 `IfcRelConnectsPorts` 的 `RelatingPort`/`RelatedPort`
  - 位置：`ObjectPlacement → IfcLocalPlacement → IfcAxis2Placement3D` 取 XYZ，同步存為 3D 與 2D（XY 投影）
3. 佈局：
  - 以力導向（簡化版）疏離節點，降低重疊；可依規模調整迭代/阻尼
4. 輸出：`SchematicData`（Nodes/Edges）。若無 Ports 關係，Edges 為空。

## 無 Ports 的處理
- 若模型中找不到任何 `IfcRelConnectsPorts`，回傳只有節點、無邊的 `SchematicData`（不進行幾何推斷）。

### Null 防護（2025-09-21 補充）
- 在 `GenerateTopologyAsync` 的 `IfcRelConnectsPorts` 迴圈中，已加入對 `RelatingPort` 與 `RelatedPort` 的 null 檢查：
  - 缺任一端即略過該關係並寫入 Warning Trace，避免 NRE 打斷整體生成流程。
  - 若多筆關係缺失，幾何鄰近 fallback 仍會嘗試補足拓撲。

## 互動行為
- 點擊節點/邊：
  - 透過 SelectionService 推播 labels → `IViewer3DService.HighlightEntities(labels)`
  - 可選配呼叫 `ZoomSelected()`
- 3D 點選：
  - `HitTest` 取得實體後轉 labels，選取回拋至原理圖

## 現況與待辦
- 現況：後端 SOP 完成；多選同步機制既有（沿用 SelectionService 與 3D 服務）。
- 本次 3D 單擊高亮效能優化（反射快取、Viewport 快取、單次 Invalidate）不影響 Schematic 拓撲。
- 待辦：
  - 佈局優化（避免長邊重疊、分群）
  - 邊路由（避開節點與交叉）
  - 顏色主題與圖例

## 疑難排解
- 3D 未高亮：先看 `viewer3d.log` 是否有收到 labels 與選取成員設定成功；若控制項只提供 `Selection: EntitySelection`，需要擴充相容（專案已加強）。
- 邊太多/卡頓：降低力導向迭代數或增大阻尼係數；大型專案可分層或按系統篩選。

## 模擬資料（Mock）
- `MockSchematicService.GetMockDataAsync()`：回傳 6 節點/5 邊的固定資料；`Position2D` 已離散，可用於前端開發與視覺驗收。
