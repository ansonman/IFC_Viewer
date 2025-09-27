# 檔案與模組結構（file_org）

本文件概述專案的主要檔案結構與各模組職責，聚焦於 3D 檢視與多選同步相關區塊。

## 目錄/檔案總覽

  - Views/
    - MainWindow.xaml(.cs): 應用程式主視圖，承載 3D 控制與 TreeView、右側面板
  - ViewModels/
    - MainViewModel.cs: 負責模型載入、TreeView 資料、跨視圖選取同步、右側面板（單選詳情/多選摘要）
  - Services/
    - IViewer3DService.cs: 3D 控制抽象介面（highlight、isolate、hide、hit test、show all 等）
    - StrongWindowsUiViewer3DService.cs: 面向 xBIM WindowsUI (DrawingControl3D) 的強型別/反射整合；多選相容、UI 刷新、診斷
    - WindowsUiViewer3DService.cs: 反射為主的輕量備援服務
    - SchematicService.cs: 從 IFC 模型萃取管線拓撲（僅 IfcRelConnectsPorts 建邊），回傳 SchematicData
    - MockSchematicService.cs: 臨時用原理圖假資料（6 節點 / 5 邊），供前端先行開發
  - Models/
    - SpatialNode.cs：TreeView 使用的樹節點模型
    - SchematicNode.cs：原理圖節點（Id/Name/IfcType/Position3D/Position2D/Entity）
    - SchematicEdge.cs：原理圖邊（Id/StartNodeId/EndNodeId/StartNode/EndNode/Entity/IsInferred）
    - SchematicData.cs：原理圖資料封裝（Nodes/Edges）
  - Diagnostics/
    - viewer3d.log（輸出）：啟動、控制面狀態、選取/隔離/隱藏操作等追蹤

## 關鍵檔案與事件流（補充 2025-09-21）
- `app/IFC_Viewer_00/Services/StrongWindowsUiViewer3DService.cs`
  - 封裝 xBIM `DrawingControl3D`：模型載入、Reload/相機、選取（含多選相容）與 HitTest。
  - 近期修補：HitTest/Tag/viewport null 防護、Trace 增補；反射快取（Selected/Hidden/Isolated 成員、Viewport）；
    高亮後改為單次 Invalidate，避免同步 UpdateLayout 造成 UI 卡頓；高亮 labels 去重。
- `app/IFC_Viewer_00/Views/MainWindow.xaml.cs`
  - 3D 預覽滑鼠事件（PreviewMouseLeft/Right）→ 呼叫 StrongViewer.HitTest/Highlight；
  - 單擊僅選取，多擊不縮放；雙擊才縮放（Click vs DoubleClick 責任分離，避免互相干擾）。
  - 同步 SelectionService 與 ViewModel；TreeView 多選互動優化。
## 關鍵模組職責

- MainViewModel
  - SelectedNodes（多選集合）變動 → 呼叫 IViewer3DService.HighlightEntities(labels)
  - 單選：更新右側詳情，多選：建立摘要
  - 透過 SelectionService 同步來源（TreeView/3D/程式）避免雙向迴圈
  - 近期優化：選取變更合併/節流（DispatcherTimer），減少重複高亮與面板更新；TreeView 開啟虛擬化（Recycling）。

- StrongWindowsUiViewer3DService
  - SetModel：建立 Xbim3DModelContext、指派模型、呼叫 ReloadModel 與 ViewHome/ShowAll
  - HighlightEntities(labels/entities)：
    - 先嘗試集合成員（SelectedEntities/HighlightedEntities…），支援 Add(int)/Add(IPersistEntity)/直接 List 置換
    - 若成員是 Selection(EntitySelection)，嘗試其內部屬性或方法（Add/AddRange/Set 等）
    - 全部失敗時退回 SelectedEntity（保證至少單選可見）
    - 清空時清空集合或 SelectedEntity=null；最後刷新 UI
  - Isolate/Hide/ShowAll/UpdateHiddenList：跨版本嘗試不同集合與方法名稱，最後以 ReloadModel 刷新
  - HitTest：使用 HelixToolkit 將 Mesh tag 反查 IPersistEntity

- SelectionService
  - 儲存目前選取與來源，提供事件 SelectionChanged；避免 TreeView 與 3D 之間的重複觸發

- SchematicService
  - 解析目標元素型別（PipeSegment/PipeFitting/FlowTerminal/Valve）建立節點，並以 HasPorts 建立 Port→Node 對應
  - 遍歷 IfcRelConnectsPorts 建立邊，並在邊上填入 Start/End NodeId 與參照
  - 將元素 LocalPlacement 之 XYZ 投影為 Position2D（XY）供前端初始位置使用
  - 若模型中沒有任何 IfcRelConnectsPorts，回傳只有節點、空邊集合的 SchematicData

- MockSchematicService
  - 回傳固定 6 節點/5 邊資料，`Position2D` 已離散分布，便於前端先行畫面驗證

## 重要約定

- 優先以 Label 進行 3D 高亮（通用性最佳）
- 任何集合寫入後僅做 InvalidateVisual（避免同步 UpdateLayout）確保可見與順暢
- 不同版本 API 名稱不一，皆以反射尋找多組候選名稱，並保留日誌
 - 原理圖拓撲遵循「Ports-only」SOP：不做幾何鄰近推斷；若無 Ports 關係，僅顯示節點

