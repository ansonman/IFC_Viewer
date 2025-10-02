# 資料定位方法 README

本文件集中說明本專案如何從 IFC 模型取得關鍵幾何定位（3D），以及如何投影為 2D 視圖（原理圖／紅點／管段軸線）。內容涵蓋資料來源優先序、核心 API、投影與樓層線對應，方便維護與擴充。

- 技術棧：.NET 8 WPF、xBIM 6.x
- 主要命名空間：
  - Services：`IFC_Viewer_00.Services`
  - ViewModels：`IFC_Viewer_00.ViewModels`

---

## FlowTerminal 紅點定位（錨點）

定位優先序：
1) Port（首選）
- 若 FlowTerminal 帶 `IfcDistributionPort`，優先挑 FlowDirection = SOURCE/SINK 的主要 Port。
- Port 座標：`Port.ObjectPlacement → IfcLocalPlacement → IfcAxis2Placement3D.Location`
- 若 Port 無定位，回退使用宿主 Product 的 LocalPlacement。

2) LocalPlacement（後援）
- 直接取 `IfcProduct.ObjectPlacement` 的 Location（`IfcAxis2Placement3D.Location`）。

3) BBox 中心（預留）
- 目前不在此 API 即時計算幾何 BBox（避免昂貴依賴）；未來可由既有幾何管線提供中心點作為最後後援。

相關 API／檔案：
- `SchematicService.GetFlowTerminalAnchorsAsync(IModel)`：回傳 `List<Point3D>`，並在 `LastFlowTerminalAnchorDetails` 提供 Terminal/Port/來源等 meta。
- 顯示：`MainViewModel.ShowFlowTerminalAnchorsCommand` → `SchematicViewModel.LoadPointsFrom3DAsync(...)` → 2D 紅點視圖。
- 詳細說明：`docs/FlowTerminal_Anchors.md`

---

## 系統／元素 Ports 定位

- 方法：`SchematicService.GetAllPortCoordinatesAsync(IModel, IIfcSystem)`
- 收集策略：
  - 由 `IfcRelAssignsToGroup` 找到系統成員（通常為 `IIfcProduct`/`IIfcElement`）。
  - 優先走成員的 `HasPorts`；若無，從 `IsNestedBy (IfcRelNests)` 收集；再無則保底走「全模型 DistributionPort 並回溯宿主」。
- 座標來源：優先 Port 自身定位，其次宿主 Product 的 LocalPlacement。
- 結果：回傳 3D 點清單，並於 `LastPortDetails` 提供每個 Port 的 Label/Name/Host/來源路徑等。

---

## 管段軸線（IfcPipeSegment）定位

- 幾何來源：`IfcExtrudedAreaSolid`（`Representation/Body` 的 `SweptSolid`）。
- 世界座標步驟：
  1) 以 `ObjectPlacement` 遞迴組合 parent → child，取得 Product 世界基底（座標軸 + 原點）。
  2) 套用 `ExtrudedAreaSolid.Position`（`IfcAxis2Placement3D`）得到 Solid 世界基底。
  3) 起點 = Solid 基底原點。
  4) 方向 = 將 `ExtrudedDirection` 以 Solid 基底轉為世界向量，乘上 `Depth` 得到終點。
- 產出：每段管 2 節點 + 1 邊，可用於 3D 疊加或 2D 顯示。

相關 API：
- `SchematicService.GeneratePipeAxesAsync(IModel, plane, flipY)` → `SchematicData`（節點/邊/樓層）。
- 3D 疊加：`IViewer3DService.ShowOverlayPipeAxes`（中線與端點）。
- 2D 顯示：`SchematicViewModel.LoadPipeAxesAsync`。

---

## 3D → 2D 投影與畫布適配

- 投影平面：支援 XY / XZ / YZ；WPF Canvas Y 向下，預設 `flipY=true`。
- 退化偵測：若在指定平面上寬或高跨度極小，會自動建議使用跨度和較大的平面。
- 畫布適配（Fit-to-Canvas）：等比縮放＋邊距，讓所有點落在畫布範圍。
- 像素對齊：可選對齊至 0.5px 網格，提升線條與圓點視覺銳利度。

相關 API：
- `SchematicViewModel.LoadPointsFrom3DAsync(points3D, plane, meta, flipX, flipY, tryBestIfDegenerate)`
- `SchematicViewModel.LoadProjectedAsync(...)`／`LoadPipeAxesAsync(...)`

---

## 樓層線（Level Lines）對應

- 來源：`IfcBuildingStorey` Elevation；若缺失，回退取該樓層物件的 LocalPlacement Z。
- 對映：以節點 3D Z 與 2D Y 進行近似線性對應（最小平方法），得到各樓層在 2D 的水平線位置。
- 退化：若線性解不穩定，改用 Z/Y 範圍比例對應。

相關實作：
- `SchematicService` 內部 `PopulateLevels`；顯示於 `SchematicViewModel` 的 `LevelLines`。

---

## 單位與比例

- 多數 Revit 導出的幾何單位為毫米（mm），但仍以模型實際資料為準。
- 2D 呈現會做 Fit-to-Canvas，因此 2D 尺度以像素為主；3D 疊加沿用模型原始單位。

---

## 主要 API 一覽

- Services：
  - `GetFlowTerminalAnchorsAsync(IModel)`：FlowTerminal 3D 錨點（Port → Placement → 預留 BBox）。
  - `GetAllPortCoordinatesAsync(IModel, IIfcSystem)`：系統 Ports 3D 定位。
  - `GeneratePipeAxesAsync(IModel, plane, flipY)`：管段軸線（2 點 + 1 邊）。
  - `GenerateTopologyAsync(IModel)`：以 `IfcRelConnectsPorts` 建拓撲（一般原理圖）。
- ViewModels：
  - `SchematicViewModel.LoadPointsFrom3DAsync(...)`：3D→2D 紅點。
  - `SchematicViewModel.LoadPipeAxesAsync(...)`：管段軸線 2D 顯示。
  - `SchematicViewModel.RefitToCanvas()`／`Relayout()`：重新適配與佈局。

---

## 延伸閱讀

- FlowTerminal 紅點流程與細節：`docs/FlowTerminal_Anchors.md`
- xBIM 相關：`IfcRelConnectsPorts`、`IfcRelAssignsToGroup`、`IfcExtrudedAreaSolid`、`IfcLocalPlacement` 等 API 與資料結構。