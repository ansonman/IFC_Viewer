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

## 技術棧
- .NET 8 WPF (net8.0-windows)
- xBIM: Xbim.Presentation (WindowsUI), Xbim.Essentials, Xbim.ModelGeometry.Scene, Xbim.Geometry.Engine.Interop
- HelixToolkit.Wpf

## 原理圖生成模組（已提供 MVP）
- 目標：從已載入的 IFC 模型提取管線拓撲，生成 2D 符號化原理圖。
- 架構：遵循 MVVM + Services，`SchematicService.GenerateTopologyAsync(IModel)` 產生 `SchematicData`（Nodes/Edges）。
- 行為：
  - 節點來源：`IIfcDistributionElement`、`IIfcPipeSegment` 等；使用 `EntityLabel` 去重。
  - 拓撲邊：依 `IfcRelConnectsPorts` 的 `RelatingPort`/`RelatedPort` 建立連線，透過 `HasPorts` 對回節點。
  - 位置：從 `ObjectPlacement → IfcLocalPlacement → IfcAxis2Placement3D` 取得 XYZ，初始投影至 2D（XY）。
  - 自動佈局：啟動後在初始位置基礎上執行力導向迭代，降低疊線與重叠。
  - 分色：依 IfcType 產生節點顏色，邊線使用更深色階。
  - 互動：點擊節點/邊會同步 3D 高亮，並嘗試執行 `ZoomSelected`。
  - Fallback：當 IFC 缺少 `IfcRelConnectsPorts` 時，採用幾何鄰近（目前閾值 10mm）推斷相鄰節點，並以 `IsInferred = true` 標示推斷邊。

### 使用方式
1. 載入 IFC 後，點工具列的「生成原理圖」。
2. 觀察節點與邊線出現並自動展開；不同 IfcType 呈現不同顏色。
3. 在原理圖點選節點（或邊），主畫面的 3D 視圖會高亮相對應物件並縮放至選取。

> 限制：力導向為快速近似；大型網路可調低迭代次數或調整 `SchematicViewModel.Scale`。未實作自動避線與群集，但可後續擴充。

## 建置與執行
- 以 PowerShell 在專案根目錄執行：

```powershell
# 建置
dotnet build .\app\IFC_Viewer_00\IFC_Viewer_00.csproj --nologo

# 執行（可指定啟動 IFC）
$env:IFC_STARTUP_FILE='j:\AI_Project\IFC_Viewer_00\Project1.ifc'; dotnet run --project .\app\IFC_Viewer_00\IFC_Viewer_00.csproj --no-build --nologo
```

## 右鍵功能（Sprint 1）
- 僅顯示選取項（Isolate）
  - 清空 Isolate 集合並加入目標；清空 Hidden 集合
  - 呼叫 `ReloadModel(ViewPreserveCameraPosition)`；最後 `ZoomSelected()`
- 隱藏選取項（Hide）
  - 將目標累加到 Hidden 集合
  - 呼叫 `ReloadModel(ViewPreserveCameraPosition)`
- 全部顯示（ShowAll）
  - 清空 Isolate/Hidden 集合
  - 呼叫 `ReloadModel()`；最後 `ShowAll()` + `ViewHome()`

> 註：不同版本集合名稱可能為 `IsolateInstances`/`IsolatedInstances` 與 `HiddenInstances`/`HiddenEntities`，本專案已做反射容錯。

## TreeView 操作指南（多選與可見性）
- 多選：
  - 單擊：僅選該節點（清空舊選取）
  - Ctrl+點擊：切換該節點的選取狀態
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
  - 對於缺 Ports 的模型，仍會透過幾何鄰近（<10mm）畫出推斷邊且標記 IsInferred。

## 已知差異與相容性
- 不同 xBIM 版本的 API 與集合命名不同，本專案透過反射與多種 fallback 覆蓋：
  - ReloadModel 的 enum 解析（含巢狀型別 ModelRefreshOptions）
  - 無參與列舉版本互為備援
  - 額外的 Refresh/Redraw/ApplyFilters 鏈作最後保險

## 授權
- 參考 xBIM 專案授權規範。
````