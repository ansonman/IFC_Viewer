````markdown
# IFC_Viewer_00

一個基於 .NET 8 WPF + xBIM WindowsUI (DrawingControl3D) + HelixToolkit 的 IFC 檢視器（MVVM）。

## 特色
- 強型別包裝 `DrawingControl3D` 的 3D 服務，支援：
  - 左鍵點選同步屬性面板
  - 右鍵功能：僅顯示選取項（Isolate）、隱藏選取項（Hide）、全部顯示（ShowAll）
  - 相機控制：ViewHome、ZoomSelected
  - 日誌追蹤與跨版本容錯（反射）
- HelixToolkit.Wpf 輔助 HitTest，精準解析點選的 Ifc 實體
- TreeView 多選與可見性：
  - Ctrl 切換、Shift 範圍選取、單擊單選；多選會同步 3D 多元素高亮
  - 每個節點提供「可見性」勾選框，會遞迴影響子節點，並即時更新 3D 隱藏清單

### 多選高亮相容性（重要）
- 3D 控制項的多選集合在不同版本可能為「整數 Label 集合」或「IPersistEntity 實體集合」。
- 本專案在多選高亮時會先以 Label 嘗試，若控制項集合需要實體，則自動將 Label 轉換為 `_viewer.Model.Instances[id]` 實體加入集合。
- 設定集合後會做輕量視圖更新（僅 InvalidateVisual，避免同步 UpdateLayout 卡頓），確保高亮即時可見；若控制項僅支援單選，則退回設置第一筆為 SelectedEntity（至少單選可見）。

#### 回歸修復（2025-09-21）
- 某些環境中 `DrawingControl3D.Selection` 是 EntitySelection 類型，非純集合，導致多選設定失效並影響單選。
- 已新增對 Selection 物件內部屬性/方法的相容處理；若仍無法多選，退回 SelectedEntity 單選確保可見。
- 清空選取時會嘗試清空 Selection 或將 SelectedEntity = null，並刷新 UI。
- 若需要進一步診斷，請提供 `viewer3d.log` 的選取相關片段。

##### 3D 點選崩潰修復（2025-09-21）
- 症狀：在 3D 視圖單擊時程式崩潰（部分 Visual 並非 FrameworkElement，或在讀取 Tag 時拋出例外；另有 viewport 為 null 的可能）。
- 修補：在 `StrongWindowsUiViewer3DService` 的 `HitTest`/`FindHit`/`GetClickedEntity` 中加入防禦性檢查：
  - 讀取 `TagProperty` 全面包覆 try/catch，無法存取則忽略該命中（不再泡泡例外）。
  - 對 `viewport == null` 與 `TranslatePoint` 失敗路徑加入保護與 Trace。
- 成果：建置成功且不再因點擊崩潰；若命中無法解析，會安全返回 null 並輸出 Trace，方便日後比對。

##### 3D 單擊高亮效能優化（2025-09-21）
- 背景：單擊後高亮偶有延遲，源於頻繁反射與同步布局刷新。
- 作法：
  - 反射快取：快取 `Selected/Hidden/Isolated` 相關集合成員，降低每次點擊反射成本。
  - Viewport 快取：快取 `HelixViewport3D` 實例以加速 HitTest。
  - 輕量重繪：更新選取後僅呼叫 `InvalidateVisual()`，避免 `UpdateLayout()` 的同步卡頓。
  - 資料去重：對高亮 labels 去重，減少集合操作與 UI 更新。
- 效果：3D 單擊高亮回應更即時、更順滑，特別是連續點擊或多選情境。

### 3D Overlay：管線中線與端點（2025-09-28）
- 功能：在 3D 視圖疊加橙紅色中線（LinesVisual3D）與黑色端點（PointsVisual3D），用於快速檢視管路走向與端點。
- 使用：按下「3D 顯示中線/端點」按鈕即可顯示；再按一次或按「清除 3D Overlay」可移除。
- 控制：提供「線寬」與「點大小」滑桿，調整即時生效。
- 透明度：顯示 overlay 時自動將模型不透明度降至 ~0.3；清除 overlay 後恢復原值。
- 相容性與解析：Strong 3D 服務會以多路徑解析 HelixViewport3D（屬性/欄位名稱：Viewport/Viewport3D/ViewPort/HelixViewport/HelixViewport3D；必要時沿視覺樹向下搜尋），確保 overlay 可正確掛載。
- 診斷：若未見 overlay，請查看 `viewer3d.log` 是否出現：
  - `[StrongViewer] EnsureViewport: ...`（含 found via ... 或 NOT found）
  - `[StrongViewer] OverlayRoot attached to viewport.`
  - `[StrongViewer] Overlay children updated. LinePoints=..., PointCount=...`
  若 LinePoints/PointCount 為 0，表示資料來源為空；若找不到 HelixViewport3D，請回報視圖控制項結構以擴充解析。

## 技術棧
- .NET 8 WPF (net8.0-windows)
- xBIM: Xbim.Presentation (WindowsUI), Xbim.Essentials, Xbim.ModelGeometry.Scene, Xbim.Geometry.Engine.Interop
- HelixToolkit.Wpf

## 原理圖生成模組（系統優先 + 互動強化）
- 目標：從已載入的 IFC 模型提取管線拓撲，生成 2D 符號化原理圖。
- 架構：遵循 MVVM + Services，`SchematicService.GenerateTopologyAsync(IModel)` 產生 `SchematicData`（Nodes/Edges）。
- 行為（SOP，嚴格遵守 IfcRelConnectsPorts）：
  - 節點來源：聚焦管線相關構件（目前包含 `IfcPipeSegment`, `IfcPipeFitting`, `IfcFlowTerminal`, `IfcValve`），以 `EntityLabel` 去重。
  - 拓撲邊：僅依 `IfcRelConnectsPorts` 的 `RelatingPort`/`RelatedPort` 建立連線，透過元素的 `HasPorts` 對回節點。
  - 位置：從 `ObjectPlacement → IfcLocalPlacement → IfcAxis2Placement3D` 取得 XYZ，並同步存入 3D（`Position3D`）與 2D（`Position2D`=XY）座標。
  - 自動佈局：啟動後在初始位置基礎上執行力導向迭代，降低疊線與重叠。
  - 分色：依 IfcType 產生節點顏色，邊線使用更深色階。
  - 互動：點擊節點/邊會透過 SelectionService 同步 3D 高亮，並可選配執行 `ZoomSelected`。
  - 無 Ports 狀況：若模型中找不到任何 `IfcRelConnectsPorts`，將回傳「僅節點、無邊」的 `SchematicData`（不再使用幾何推斷）。

### SOP 2.0：系統優先、Ports-only（2025-09-22）
- 新增服務方法 `SchematicService.GenerateFromSystemsAsync(IStepModel)`：
  - 先發現 `IIfcSystem`/`IIfcDistributionSystem`，對每個系統各自產生一份 `SchematicData`。
  - 以 `IfcRelAssignsToGroup` 取得該系統成員 → 建立節點 → 蒐集成員上的 `HasPorts` → 依 `IfcRelConnectsPorts` 建立邊。
  - `SchematicEdge.Connection` 會指向對應的 `IfcRelConnectsPorts` 實體；`SchematicNode.Edges` 回填相鄰邊。
  - `SchematicData.SystemName`/`SystemEntity` 會回傳系統名稱與實體，供 UI 顯示與追蹤。

- 前端整合：
  - 工具列「生成原理圖」改為呼叫 `GenerateSchematicCommand` → 使用上述 `GenerateFromSystemsAsync`。
  - 若僅一個系統，直接開啟；若偵測到多個系統，彈出 `SystemSelectionDialog` 讓使用者選擇。
  - Schematic 邊線繪製：`SchematicView.xaml` 使用第二個 `ItemsControl` 以 `<Line>` 呈現，座標綁定 EdgeView 上的起迄節點座標（`Start.X/Start.Y` 與 `End.X/End.Y`），確保與節點視圖座標一致；`Stroke="Black"`、`StrokeThickness="1.5"`，並置於節點下方分層顯示。
  - 當 `Edges.Count == 0` 時顯示提示 Banner：「模型未含 IfcRelConnectsPorts 連線，僅顯示節點。」

### 視圖互動與視角控制
- 縮放/平移：
  - 滑鼠滾輪縮放（以游標為中心）；按住滑鼠中鍵拖曳以平移。
  - 透過 `ScaleTransform + TranslateTransform` 構成的 `TransformGroup` 作用在 Canvas 上。
- 工具列按鈕：
  - 重置視圖：將縮放/平移復位，並呼叫 `RefitToCanvas()` 將當前節點座標重新適配畫布。
  - 重新布局：重新執行力導向佈局，並自動適配畫布（Refit）。
- Fit-to-Canvas：
  - 一般方法：`SchematicViewModel.FitToCanvas()` 會依據 `CanvasWidth/CanvasHeight/CanvasPadding`（預設 1600/1000/40）計算邊界框、縮放與偏移，更新 NodeView 的 `X/Y`。
  - 資料載入：`LoadData(SchematicData)` 版實作按需使用固定 800x600 畫布與 padding 20，先更新模型 `Node.Position2D`，再同步 NodeView `X/Y` 與建立 EdgeView；滿足「系統先、僅 Ports」流程下的即時適配需求。
  - 最佳投影面：以「最小跨度軸剔除」策略自動選擇投影平面（捨棄 X/Y/Z 中跨度最小者，保留另兩軸作為 2D）；平手時偏好 XY → XZ → YZ。

### AS 原理圖流程（兩段 IfcPipeSegment）
- 目的：以兩段參考管件推導投影平面，將系統 Ports 投影成 2D 黑點，並用黑線連接具有 `IfcRelConnectsPorts` 的 Port 對。
- 操作步驟：
  1) 在 3D 視圖中依序選取兩段 `IfcPipeSegment`（可從 UI 啟動「AS 原理圖」工具）。
  2) 系統會：
     - 從兩段管件推導最佳 2D 投影平面（最小跨度軸剔除）。
     - 收集目標系統的 Ports（AssignsToGroup → 成員 HasPorts → 全模型 Ports 保底）。
    # IFC_Viewer
     - 將 Ports 投影為 2D 黑點（6px）。
     - 依 `IfcRelConnectsPorts` 連結成對 Ports，繪製黑線（1.5px）。
  3) 視圖自動 Fit-to-Canvas（800x600、padding 20），可使用滑鼠滾輪縮放與中鍵平移；「重置視圖」與「重新布局」同樣可用。
- 疑難排解：
  - 完全沒有黑點：檢查 AssignsToGroup/HasPorts 是否為空；本流程仍會回退到「全模型 Ports」以保底。

### AS 原理圖 V1：手動平面 + 全系統 Ports 點雲（2025-09-24）
- 目的：快速以人工指定平面檢視一或多個系統的全部 Port 分佈與資料品質（不畫邊線，專注診斷與來源追蹤）。
- 流程：
  2) 選擇投影平面：XY / XZ / YZ
  3) 後端對每個系統執行 Ports 抽取（多層策略）→ 投影 → 顯示點集合
  4) 日誌區塊印出：系統統計（viaHasPorts / viaNested / viaFallback）與每個 PortDetail
- Ports 抽取三層策略：
  1) HasPorts：成員元素直接擁有的 Ports
  2) Nested：`IfcRelNests`（嵌套）關係中的 Ports（模型未使用 HasPorts 時常見）
  - 黑色：宿主為 IfcPipeSegment（視為管段端點）
  - 紅色：非 PipeSegment 或來自 fallback 的 Port
- Tooltip：顯示 Port 名稱、Label、Host IfcType、Host Label、是否 PipeSegment。
- 已修復問題：早期版本全部顯示紅色（原因：Port meta 與渲染順序錯配）→ 以 index 對齊修正。
  - 部分模型宿主需透過反向 RelNests 追溯（尚未實作）→ 可能造成本應為管段端點的 Port 被標為紅色。
  - 點過度重疊難以辨識（後續可加 jitter / 疊點計數）。
  - 尚無圖例 / 篩選；可後續加入顯示切換（只顯示管段端點）。

### 資料合約（Data Contracts）
- Node（`SchematicNode`）：`Id`, `Name`, `IfcType`, `Position3D`, `Position2D`, `Entity`, `Edges`
- Edge（`SchematicEdge`）：`Id`, `StartNodeId`, `EndNodeId`, `StartNode`, `EndNode`, `Entity`, `Connection`, `IsInferred`
  - 註：`Connection` 指向 `IfcRelConnectsPorts`，利於追溯原 IFC 關聯；目前 SOP 僅由 Ports 建立連線，因此 `IsInferred` 預期為 `false`，保留欄位以利未來擴充。
- Graph（`SchematicData`）：`Nodes`, `Edges`, `SystemName`, `SystemEntity`

### 模擬資料服務（Mock）

### 使用方式
2. 若偵測到多個系統，會跳出「系統選擇」對話框，選定一個系統後開啟原理圖視窗；視窗標題會顯示系統名稱。
3. 觀察節點與邊線出現並自動展開；不同 IfcType 呈現不同顏色。若 `Edges.Count == 0`，上方會顯示提示 Banner（僅節點、無連線）。
4. 在原理圖點選節點（或邊），主畫面的 3D 視圖會高亮相對應物件並縮放至選取。
> 限制：力導向為快速近似；大型網路可調低迭代次數或調整 `SchematicViewModel.Scale`。未實作自動避線與群集，但可後續擴充。

## 建置與執行
- 以 PowerShell 在專案根目錄執行：

dotnet build .\app\IFC_Viewer_00\IFC_Viewer_00.csproj --nologo

# 執行（可指定啟動 IFC）
$env:IFC_STARTUP_FILE='j:\AI_Project\IFC_Viewer_00\Project1.ifc'; dotnet run --project .\app\IFC_Viewer_00\IFC_Viewer_00.csproj --no-build --nologo
```

## 右鍵功能（Sprint 1）
- 僅顯示選取項（Isolate）
  - 清空 Isolate 集合並加入目標；清空 Hidden 集合
  - 呼叫 `ReloadModel(ViewPreserveCameraPosition)`；最後 `ZoomSelected()`
- 隱藏選取項（Hide）
  - 清空 Isolate/Hidden 集合
  - 呼叫 `ReloadModel()`；最後 `ShowAll()` + `ViewHome()`
- 多選：
  - 單擊：僅選該節點（清空舊選取）
  - Shift+點擊：以前序扁平順序做範圍選取（從上一次選到這次點擊）
- 可見性：
  - 勾選「可見性」會遞迴影響子節點，並更新 3D 的 Hidden 清單
  - 取消可見性不會刪除節點，只是從 3D 呈現中暫時隱藏

## 診斷日誌與疑難排解
- 啟動後會輸出 `viewer3d.log`（位於 Debug 輸出目錄）。
- 右鍵操作將新增詳細 Trace，例如：
  - `[StrongViewer] Isolate() called for entity label: 348711.`
  - `[StrongViewer] Isolate: IsolateInstances collection count before: 0. After: 1.`
  - `[StrongViewer] Invoking ReloadModel(ViewPreserveCameraPosition)...`
  - `[StrongViewer] Invoking ZoomSelected()...`
- 若 3D 無變化，請檢查：
  - 集合成員是否找到（log 會顯示 member not found）
  - 集合 count 是否有變化
  - 是否有成功呼叫 `ReloadModel(...)` 與 `ZoomSelected`/`ViewHome`
  - 多選高亮未生效：
    - 確認 `SelectedEntities`/`HighlightedEntities` 集合型別是否為 `List<int>` 或 `List<IPersistEntity>`；本專案已支援兩者（自動 Label→實體映射）。
    - 若控制項只提供 `Selection: EntitySelection`，請同時提供該型別可用屬性/方法的日誌，以便擴充適配。
  - V1 點全部為紅色：請確認是否已使用最新修正（index 對齊 meta）；若仍為紅色，表示模型中 Ports 未能由 HasPorts / Nested 正確找到宿主，需實作 RelNests 反向解析。

### 快速驗收（Smoke test）
1) 載入 IFC 後，在 3D 視圖單擊任一物件：
  - 不應崩潰；屬性/右側面板同步更新為單選內容。
2) 在 3D 視圖雙擊該物件：
  - 視角應縮放至所選物件（ZoomSelected）。
3) 在 TreeView：
  - Ctrl 多選與 Shift 範圍選取應同步 3D 多元素高亮；點空白處清除選取。
4) 右鍵 Isolate/Hide/ShowAll：
  - 3D 呈現應即時變更；log 可見 Reload/ZoomSelected 的 Trace。
5) 生成原理圖：
  - 若模型缺少 `IfcRelConnectsPorts`，本版將只呈現節點、邊線為空（SOP：不啟用幾何推斷）。

## 已知差異與相容性
- 不同 xBIM 版本的 API 與集合命名不同，本專案透過反射與多種 fallback 覆蓋：
  - ReloadModel 的 enum 解析（含巢狀型別 ModelRefreshOptions）
  - 無參與列舉版本互為備援
  - 額外的 Refresh/Redraw/ApplyFilters 鏈作最後保險

## 授權
- 參考 xBIM 專案授權規範。
````