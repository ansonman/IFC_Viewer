````markdown
# 檔案與目錄結構說明 (FILE_ORG)

本文件對目前的專案目錄與主要檔案提供總覽，並標示關鍵服務/模型的用途，以利協作與維護。

## 目錄概覽

- app/IFC_Viewer_00/
  - Views/
    - MainWindow.xaml：主視窗（Menu/主內容/狀態列）。目前 3D 控制項以 XAML 直接宣告（可後續改為 ContentControl 動態載入）。
    - MainWindow.xaml.cs：設定 DataContext、接住後續 3D/TreeView 事件（如需要）。
  - ViewModels/
    - MainViewModel.cs：核心 VM，提供 Model 載入（OpenFileCommand）、狀態列訊息（StatusMessage）。
  - Services/（規劃/擴充中）
    - IViewer3DService.cs、WindowsUiViewer3DService.cs、StrongWindowsUiViewer3DService.cs、StubViewer3DService.cs：3D 服務抽象與實作（集合高亮、Isolate/Hide/ShowAll、UpdateHiddenList、HitTest）。
    - IfcStringHelper.cs：xBIM 型別/值 → string 的統一轉換。
  - Models/
    - SpatialNode.cs、ElementProperty.cs：TreeView 與屬性面板資料模型（含 IsVisible/IsSelected 以支援多選與可見性）。

- README.md：功能與操作說明（多選、可見性、3D 右鍵功能、Schematic fallback）。
- DEVELOPMENT_LOG.md：每日開發紀錄與品質狀態。
- SchematicModule_Report.md：原理圖模組報告（拓撲生成、fallback、互動）。
- Debug Report.md：錯誤診斷與驗證清單。

> 註：部分檔案目前為規劃/文件描述狀態，會於後續迭代中補齊程式碼與對應 XAML。

## 互動流程（重點）

- TreeView 多選：
  - 單擊單選、Ctrl 切換、Shift 範圍選取（以最後一次 Anchor 為基準）。
  - ViewModel 維護 SelectedNodes；同步 3D 以集合高亮。
- 可見性切換：
  - 勾選 IsVisible 遞迴影響子節點；ViewModel 蒐集 Hidden 清單呼叫 3D 的 UpdateHiddenList。
- 3D 右鍵：
  - Isolate/Hide/ShowAll 透過集合更新 + ReloadModel（保留相機/選取）+ ZoomSelected/ViewHome。

## 相容性與外部來源

- xBIM WindowsUI（DrawingControl3D）在不同版本中集合與 enum 簽章可能不同：
  - 集合命名：IsolateInstances/IsolatedInstances、HiddenInstances/HiddenEntities
  - ReloadModel enum：ModelRefreshOptions（含巢狀成員）
  - 已以反射 fallback 與多層 Refresh 鏈處理相容性

## 後續規劃

- 將階層建置改為非同步，避免 UI 凍結
- 完成集合高亮 API 的全域替換與呼叫點收斂
- 原理圖視圖（SchematicView）整合與 3D 雙向同步
````

## 互動流程（重點）

- 3D 點選（左鍵）：
  1. `MainWindow.xaml.cs` 以 HitTest 取得 `IIfcObject`。
  - Isolate/Hide：以集合驅動更新（Clear + Add），相容 `IsolateInstances/IsolatedInstances` 與 `HiddenInstances/HiddenEntities`，支援 `List<IPersistEntity>` 或 `List<int>`；執行於 UI 執行緒；刷新採 ReloadModel（偏好 View/Filter/PreserveCamera）與多層備援；Isolate 後 `ZoomSelected`。
  - ShowAll：清空隔離/隱藏（Clear），刷新（同上），再 `ShowAll` 與 `ViewHome`。


  - 可能名稱：`IsolateInstances` / `IsolatedInstances`、`HiddenInstances` / `HiddenEntities`。
  - 更新策略：採用清空（Clear）後新增（Add），避免將集合設為 null 造成內部刷新忽略。
## 注意事項
- Hover 自動選取已關閉，避免頻繁觸發；現僅支援點擊/右鍵/雙擊。
# 專案目錄與檔案結構說明（FILE_ORG.md）

本文件說明 `IFC_Viewer_00` 專案的目錄結構與各資料夾、檔案的用途，方便團隊協作與維護。
---

## 根目錄（`./`）
- `IFC_Viewer_00.sln`：Visual Studio/VS Code 解決方案檔，統整所有專案。
- `FILE_ORG.md`：本檔案，說明目錄與檔案用途。
- `DEVELOPMENT_LOG.md`：開發日誌，記錄每次技術變更（如 xBIM 6.x API 遷移、型別修正、建置結果）。
- `Project1.ifc`：測試用 IFC 檔案，可透過環境變數 `IFC_STARTUP_FILE` 自動載入。
- 根目錄下的 `MainViewModel.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs` 為舊版遺留檔案，已搬移至 `app/IFC_Viewer_00/Views`、`app/IFC_Viewer_00/ViewModels`；不再參與編譯，可日後清理。
 - `New Home.ifc`, `New Home.ifc.log`：其他範例檔與其產生之日誌（如有）。
 - `Others/`：放置其他文件或素材的資料夾（內容視專案演進）。



## app/
- **用途**：作為所有應用程式專案的統一容器，便於多專案管理與隔離。

### 目錄與檔案一覽

```
app/
├── IFC_Viewer_00/           # 主 WPF 專案目錄
│   ├── IFC_Viewer_00.csproj  # WPF 專案檔，定義目標框架、NuGet 套件、編譯規則
│   ├── App.xaml              # 應用程式進入點，定義全域資源、啟動視窗
│   ├── App.xaml.cs           # App.xaml 的 code-behind，處理全域事件
  │   ├── Views/                # 所有 UI 視圖（XAML 與 code-behind）
  │   │   ├── MainWindow.xaml       # 主視窗 UI 佈局，含 3D 檢視器、TreeView 結構樹、屬性面板
  │   │   └── MainWindow.xaml.cs    # 主視窗 code-behind，初始化、DataContext 設定、3D/TreeView 事件橋接
  │   ├── ViewModels/           # 所有 ViewModel 類別，負責 UI 綁定邏輯
  │   │   └── MainViewModel.cs      # 主視窗的 ViewModel，模型載入、命令、狀態、結構樹、屬性同步
  │   ├── Models/               # 自訂資料結構、BIM/IFC 物件封裝
  │   │   ├── ElementProperty.cs    # 屬性面板顯示用的簡化屬性資料模型（Name/Value）
  │   │   └── SpatialNode.cs        # 結構樹用的節點資料模型（Name/Entity/Children）
  │   ├── Services/             # 共用服務與抽象層（避免 UI 相依）
  │   │   ├── IViewer3DService.cs           # 3D 操作抽象介面（SetModel/Highlight/Isolate/Hide/ShowAll/HitTest）
  │   │   ├── StrongWindowsUiViewer3DService.cs # 強型別優先的 Xbim.Presentation.DrawingControl3D 包裝
  │   │   ├── WindowsUiViewer3DService.cs   # 反射式 Xbim.WindowsUI 包裝（跨版本相容回退）
  │   │   ├── StubViewer3DService.cs        # 暫時實作（no-op）
  │   │   └── IfcStringHelper.cs            # 統一 object/強型別 → string 的安全轉換
  │   └── Resources/            # 靜態資源（圖片、樣式、icons、字典）
├── tests/                      # 測試專案根目錄
│   └── IFC_Viewer_00.Tests/        # xUnit 測試專案（WPF 相容設定）
│       ├── IFC_Viewer_00.Tests.csproj # 測試專案檔，含必要相依與設定
│       └── *.cs                   # 測試內容（ViewModel 論理、3D 服務反射行為等）
```

#### 主要檔案說明（現況）

- `IFC_Viewer_00.csproj`：
  - 定義專案型別（WPF）、目標框架（net8.0-windows）、平台目標（x64）、NuGet 相依（xBIM、MVVM Toolkit）
  - 控制編譯、資源嵌入、WPF 啟用等
- `App.xaml` / `App.xaml.cs`：
  - 設定全域資源（樣式、字典）、應用啟動流程、全域例外處理
  - `StartupUri="Views/MainWindow.xaml"` 指向正確的主視窗位置
- `Views/MainWindow.xaml`：
  - 主 UI 佈局（選單、3D 檢視器宿主、TreeView 結構樹、屬性面板、狀態列），XAML 綁定 ViewModel 屬性/命令/結構樹/屬性集合
  - 使用 `ContentControl x:Name="ViewerHost"` 作為 3D 控制項宿主，避免 XAML 時期對外部控制項的直接相依
- `Views/MainWindow.xaml.cs`：
  - 初始化與 DataContext 設定：優先以強型別建立 `Xbim.Presentation.DrawingControl3D` 並注入 `StrongWindowsUiViewer3DService`；若失敗回退為反射式 `WindowsUiViewer3DService`；仍無法則注入 `StubViewer3DService`。為避免設計期/建置期相依問題，`InitializeComponent()` 以反射呼叫。
  - UI 事件橋接：掛載 `MouseMove`/`MouseLeftButtonDown`（雙擊檢測）呼叫 3D 服務的 `HitTest`，更新 ViewModel 的 `HighlightedEntity`、屬性面板與樹選取同步。
  - 環境變數自動載入：`Loaded` 事件檢查 `IFC_STARTUP_FILE` 並自動開檔。
  - 啟動記錄：應用在啟動時初始化 `Trace` 檔案輸出（`viewer3d.log`）。
- `ViewModels/MainViewModel.cs`：
  - MVVM 核心：檔案開啟、模型載入、狀態訊息、命令邏輯、結構樹建構、TreeView/3D/屬性面板連動
  - 已完成 xBIM 6.x 型別遷移（IIfcObject 等），轉字串策略集中至 `Services/IfcStringHelper.FromValue(object?)`，避免 CS0266。
  - 以建構子注入 `IViewer3DService`，在 Model/選取/命令中呼叫服務（目前為 Stub 實作）。
- `Models/ElementProperty.cs`：
  - 用於屬性面板顯示的簡化屬性資料模型（Name/Value）
- `Models/SpatialNode.cs`：
  - 用於結構樹的節點資料模型（Name/Entity/Children），支援遞迴層級
- `Services/`：
  - 放置 3D 操作抽象（`IViewer3DService`）、強型別/反射式/Stub 三種 3D 服務實作，以及字串轉換 helper（`IfcStringHelper`）
  - 強型別服務：直接使用 `Xbim.Presentation.DrawingControl3D` 與 `Xbim3DModelContext` 建幾何；反射式服務：最大化兼容不同 WindowsUI 版本的成員簽章；皆支援相機擬合、Refresh 與 `ShowAll` 的回退序列。
- `Resources/`：
  - 可放圖片、icons、樣式、語系檔等靜態資源

執行期日誌
-----------
- `bin/Debug|Release/net8.0-windows/viewer3d.log`：3D 與部分服務 Trace。
- Schematic/AS 流程會輸出 Ports 與 Edges 蒐集/配對的統計，協助驗收（若缺 `IfcRelConnectsPorts`，UI 會顯示提醒 Banner）。
FILE_ORG
========

本檔案說明此工作區的資料夾與重要檔案用途，並標註外部來源的參考原始碼位置，協助快速導覽。

根目錄
------
- README.md：使用說明、快速開始、疑難排解與相容性提醒。
- DEVELOPMENT_LOG.md：每日開發記錄與重大變更、既知議題、下一步規劃。
- FILE_ORG.md：本文件，檔案與資料夾導覽。
- Master Prompt.md：需求/想法彙整（若有）。
- Project1.ifc：範例 IFC 檔案（啟動時可用 IFC_STARTUP_FILE 自動載入）。
- New Home.ifc / .log：另個 IFC 測試檔與其日誌（若有）。

應用程式（WPF）
---------------
app/IFC_Viewer_00/
- IFC_Viewer_00.csproj：.NET 8 WPF 專案檔，目標 `net8.0-windows`、x64。
- Views/
  - MainWindow.xaml / .cs：主視窗（包含 3D Viewer 容器、結構樹、屬性面板、工具列與狀態列）。
- ViewModels/
  - MainViewModel.cs：載入 IFC、建構空間結構樹、屬性整理與 3D 服務呼叫。
- Models/
  - SpatialNode.cs：結構樹節點資料模型。
  - ElementProperty.cs：屬性面板資料模型。
- Services/
  - IViewer3DService.cs：3D 檢視抽象介面。
  - StrongWindowsUiViewer3DService.cs：強型別整合 Xbim.Presentation.DrawingControl3D 與 Xbim3DModelContext 的主要實作。
  - WindowsUiViewer3DService.cs：反射式整合，跨版本相容備援，亦提供 `LastAssignedModel` 診斷欄位。
  - StubViewer3DService.cs：無操作備援實作。
  - IfcStringHelper.cs：object/強型別值 → string 的統一轉換。
- Resources/：樣式、圖片與其他資源。
- bin/Debug|Release/net8.0-windows/：輸出與 `viewer3d.log`（執行期診斷）。

測試
----
- tests/IFC_Viewer_00.Tests/
  - IFC_Viewer_00.Tests.csproj：測試專案。
  - FakeViewerControl.cs（若有）：測試用假控制項，避免對實體 Xbim.WindowsUI 的強依賴。
  - 測試聚焦於：模型載入流程、服務層是否把 IfcStore 傳遞到控制層（檢查 Model/Tag/LastAssignedModel）。

---

## 新增：原理圖模組檔案（2025-09-20）

為支援「從 IFC 模型生成原理圖（Schematic）」功能，新增下列檔案：

- 根目錄
  - `SchematicModule_Report.md`：原理圖模組的階段性報告（進度、挑戰、截圖、下一步）

– `app/IFC_Viewer_00/Models/`
  - `SchematicNode.cs`：原理圖節點（Id/Name/IfcType/Position3D、Entity 參照）。
  - `SchematicEdge.cs`：原理圖連線（Id/StartNode/EndNode、Entity 參照）。
  - `SchematicData.cs`：原理圖資料封裝（Nodes/Edges，唯讀 List）。

– `app/IFC_Viewer_00/Services/`
  - `SchematicService.cs`：`GenerateTopologyAsync(IModel)` 解析 `IIfcDistributionElement` 與 `IfcRelConnectsPorts` 生成拓撲；以 EntityLabel 去重；位置取自 LocalPlacement。
  - 進階：`GenerateFromSystemsAsync(IStepModel)`（系統優先 Ports-only）；AS 流程：`GeneratePortPointSchematicFromSegmentsAsync(...)` 與 `ProjectAllSystemPortsToData(...)`（三層 Ports 蒐集、以 `IfcRelConnectsPorts` 建邊、最佳投影面為最小跨度軸剔除）。

– `app/IFC_Viewer_00/ViewModels/`
  - `SchematicViewModel.cs`：載入拓撲、投影 2D，提供 NodeView/EdgeView（含 Brush）；內建力導向自動佈局與點擊命令；`RequestHighlight` 事件給視窗層同步 3D。
  - `SelectSegmentsForASSchematicViewModel.cs`：AS 流程用，監聽 3D 選取、收集兩段 `IIfcPipeSegment` 後呼叫服務產生 AS 原理圖。

– `app/IFC_Viewer_00/Views/`
  - `SchematicView.xaml` / `.xaml.cs`：Canvas + ItemsControl 呈現節點/邊；點擊節點/邊透過命令觸發 `RequestHighlight`，由視窗轉呼叫 3D 服務高亮與縮放；主視窗工具列有「生成原理圖」。
  - `ReferencePipeSelectionView.xaml`：AS 流程的兩段管件選取視窗（補齊 Window 根元素避免建置錯誤）。

---

補充（2025-09-22：Schematic 視圖與互動更新）

- `Views/SchematicView.xaml`：
  - 繪製層級：使用兩個 ItemsControl，邊線（Line）在下層、節點在上層，避免節點被線覆蓋。
  - 邊線綁定：改綁 EdgeView 的節點視圖座標，`X1={Binding Start.X}`、`Y1={Binding Start.Y}`、`X2={Binding End.X}`、`Y2={Binding End.Y}`。
  - 視圖變換：Canvas 套用 TransformGroup（ScaleTransform 作為 Zoom、TranslateTransform 作為 Pan）。
  - 互動：滑鼠滾輪縮放（以指標為中心）、中鍵拖曳平移；提供「重置視圖」與「重新布局」按鈕。
  - 空邊提示：若 `Edges.Count == 0` 顯示提示 Banner（模型未含 Ports 關係）。

- `ViewModels/SchematicViewModel.cs`：
  - 佈局：力導向（預設 200 次迭代）。
  - Fit-to-Canvas：
    - 一般方法：`FitToCanvas()` 依 `CanvasWidth/CanvasHeight/CanvasPadding`（預設 1600/1000/40）將節點座標縮放/平移至畫布範圍。
    - 載入方法：`LoadData(SchematicData)` 採固定 800x600 畫布、padding 20 的適配，先更新 `Node.Position2D`，再同步 NodeView `X/Y` 與建立邊視圖。
  - 公開方法：`RefitToCanvas()` 再次依目前畫布設定適配；`Relayout(int iterations=200, bool refit=true)` 重新佈局並可選擇自動適配。

- `Services/SchematicService.cs`：
  - 系統優先：`GenerateFromSystemsAsync(IStepModel)` 依 `IfcRelAssignsToGroup` 取得系統成員，以 `IfcRelConnectsPorts` 建立邊；強化 Group 匹配（EntityLabel/GlobalId 字串），並以 `Trace` 輸出統計摘要。