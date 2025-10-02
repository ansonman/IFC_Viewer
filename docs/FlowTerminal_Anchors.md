# FlowTerminal 紅點顯示

本功能將模型中的 IfcFlowTerminal 以「紅點」的方式投影到 2D Canvas。可從主視窗「檢視 → FlowTerminal 紅點」開啟，或在 3D 區域的右鍵選單觸發。

## 定位資料來源（優先順序）
1. Port（推薦）：若 FlowTerminal 帶有 IfcDistributionPort，優先選擇 FlowDirection 為 SOURCE 或 SINK 的主要 Port，其 3D 位置作為錨點。
2. LocalPlacement：若沒有可用 Port，改用該元素的 IfcLocalPlacement 位置（Axis2Placement3D.Location）。
3. BBox 中心（預留）：目前不在服務中即時建立幾何以取 BBox，未來可改由既有幾何管線提供中心點後再補強。

每個點的詳細資料（例如 Terminal Label/Name、來源 Source=Port|Placement 等）會存到 `SchematicService.LastFlowTerminalAnchorDetails`。

## 投影與顯示
- 由 `SchematicViewModel.LoadPointsFrom3DAsync(points3D, plane, meta, flipX:false, flipY:true, tryBestIfDegenerate:true)` 完成 3D→2D 投影與畫布適配。
- `plane` 可為 XY/XZ/YZ。Y 預設翻轉以符合 WPF Canvas 座標向下。
- 每個紅點對應一個 `SchematicNode`，名稱優先使用 Terminal 名稱，次選 Port 名稱。

## 操作說明
- 於主視窗頂部選單：檢視 → FlowTerminal 紅點。
- 或在 3D 區域右鍵選單選擇同項目。
- 開啟時可（若對話框存在）選擇投影平面；如未選擇則預設 XY。

## 已知限制與後續規劃
- 若模型缺少 Port 且 LocalPlacement 皆為原點，點位可能聚在 (0,0,0)。可在未來加入 BBox 中心或系統化幾何中心作為後援。
- 紅點目前不自動分層或群組，後續可依樓層或系統分類改變顏色或符號大小。
- 如需同步 3D 高亮，點擊紅點已透過 SelectionService 進行選取與高亮。
