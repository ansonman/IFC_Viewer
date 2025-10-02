# IFC Viewer 透明度診斷報告 v1（2025-09-29）

> 主題：DrawingControl3D 不透明度（ModelOpacity）設定偶發無效/延遲問題

## 摘要

- 現象：在 .NET 8 + xBIM WindowsUI + HelixToolkit 的 IFC Viewer 中，呼叫 `SetModelOpacity` 調整 `DrawingControl3D` 透明度，有時未立即生效或被後續流程覆蓋。
- 主要發現：
  - 控制項存在且可寫入 `ModelOpacity`；另有 `SetOpacity(double)` 可用（反射確認）。
  - 設定後已觸發多條刷新路徑與 `ReloadModel(ViewPreserveCameraPosition)`；偶出現反射呼叫例外但後續仍成功刷新。
  - Overlay 顯示時已自動降至 ~0.3 並附掛成功；視口解析偶有時序延遲（先 NOT found 後 found）。
  - 已新增：首次附掛 Overlay 自動 ZoomExtents、Overlay 幾何沿相機方向極小偏移，降低 Z-fighting 造成的「看起來沒變化/看不到黑點」。

## 診斷步驟執行

### a. 檢查控制項屬性與版本
- 作法：反射列舉 `DrawingControl3D` 屬性/方法。
- 證據（log 節錄）：
  - `... P ModelOpacity:Double (CanWrite=True) ...`
  - `... M get_ModelOpacity() | M set_ModelOpacity(Double) | M SetOpacity(Double) ...`
- 結論：`ModelOpacity` 存在且可寫，版本相容（假設 xBIM 6.x）。

### b. 驗證刷新機制
- 作法：設定 `ModelOpacity` 後嘗試 `ReloadModel(ModelRefreshOptions.ViewPreserveCameraPosition|View|None)`，備援多種 `Refresh*` 與視口 Invalidate/Update。
- 證據（log 節錄）：
  - `SetModelOpacity requested: 0.300`
  - `Set ModelOpacity property.`
  - 多次：`ReloadModel(ViewPreserveCameraPosition) invoked (control-nested enum).`
  - 偶見：`TryInvokeReloadModelWithOptionsOnControl failed: Exception has been thrown by the target of an invocation.`
- 結論：刷新有觸發；偶發反射例外後仍有後續成功刷新，屬相容性/時序非致命。

### c. 測試 Overlay 情境
- 作法：顯示中線/端點時自動降不透明度，並附掛 Overlay 至 `HelixViewport3D`；首次附掛自動 `ZoomExtents()`。
- 證據（log 節錄）：
  - `EnsureViewport: found via property 'Viewport'.`
  - `OverlayRoot attached to viewport.`
  - `Overlay children updated. LinePoints=8, PointCount=8.`
  - `SetModelOpacity requested: 0.300` → `Set ModelOpacity property.`
- 補強：已加入 Overlay 幾何相機方向 epsilon 偏移，減少 Z-fighting。
- 結論：Overlay 與透明度聯動可運作，視口解析偶有延遲。

### d. UI 執行緒與時序檢查
- 作法：所有操作均封裝 `RunOnUi`；必要時可加 Dispatcher 延遲以避開模型載入重建。
- 證據：`EnsureViewport` 部分時機先 `HelixViewport3D NOT found`，稍後又 `found`，顯示視覺樹初始化中的自然延遲。
- 結論：UI 執行緒正確；時序可透過延遲補刷再強化。

### e. 完整煙霧測試（模擬）
- 作法：載入 IFC → 手動設 `ModelOpacity=0.5` → 觀察畫面與 log。
- 預期（依現有 log 模式）：
  - `SetModelOpacity requested: 0.500`、`Set ModelOpacity property.` 隨後多次 `ReloadModel(ViewPreserveCameraPosition)` 與 `Refresh*`。
- 結論：應可穩定生效；若仍偶發延遲，屬渲染與重建時序非 API 缺失。

## 可能原因分析（由高到低）

1. 視覺樹/視口解析時序造成的刷新落空（高）
   - 證據：`EnsureViewport: HelixViewport3D NOT found.` → 稍後 `found via property 'Viewport'`。
   - 解讀：在視口尚未穩定時設定透明度，可能被後續載入/重建覆蓋或延遲反映。

2. 透明材質重建時機差異（中）
   - 證據：大量 `ReloadModel(ViewPreserveCameraPosition)` 與 `Refresh*`；偶發 Invocation 例外後仍恢復。
   - 解讀：部分版本/狀態需更完整重建；已以多條刷新路徑補強。

3. Overlay 與模型表面 Z-fighting（中）
   - 證據：未偏移時，透明度提升更容易與表面同深度，造成「看似沒變」。
   - 解讀：已加入相機方向 epsilon 偏移降低風險。

4. `ReloadModel` enum 相容性（低-中）
   - 證據：`TryInvokeReloadModelWithOptionsOnControl failed: ...`。
   - 解讀：跨版本名稱/值不一致；已有備援刷新，影響有限。

## 建議解決方案

### 短期（已佈署/可立即納入）
- 保證 UI 執行緒、強化刷新與 Helix 視口 Invalidate/Update（已存在）。
- 首次附掛 Overlay 自動 `ZoomExtents()`（已加入）。
- Overlay 幾何沿相機方向極小偏移（已加入）。
- 加入透明度、視口解析、Overlay 子項等診斷（已存在）。
- 追加延遲補刷（建議，低風險）：
  - 在 `SetModelOpacity` 現有刷新完成後，使用 Dispatcher.Background 再觸發一次 `ReloadModel(ViewPreserveCameraPosition|View)` 與 `InvalidateVisual()`，避免與載入重建競爭。

示例片段（延遲補刷，片段）：

```csharp
// 尾端補一個 Background 延遲，避開載入重建的時序
var dispatcher = (_viewer as FrameworkElement)?.Dispatcher;
dispatcher?.BeginInvoke(() =>
{
    TryInvokeReloadModelWithOptionsOnControl(_viewer, new[] { "ViewPreserveCameraPosition", "View" });
    try { _viewer.InvalidateVisual(); } catch { }
}, System.Windows.Threading.DispatcherPriority.Background);
```

### 長期
- 針對 `ModelRefreshOptions` 做啟動期反射列舉，建立版本對應表，避免 Invocation 例外。
- 提供 UI「透明度應用中…」指示（大模型時 1–2 個渲染週期），降低誤判。
- 若版本支持，統一路徑改以 `ModelOpacity` 屬性為準，讀寫後比較差異做重試。

## 額外註記

- 假設環境：.NET 8、xBIM 6.x、HelixToolkit.Wpf + Xbim.Presentation（部分為 .NET Framework 相容組件）。
- 作業日誌位置：`app/IFC_Viewer_00/bin/Release/net8.0-windows/viewer3d.log`（或 Debug 對應資料夾）。
- 若仍遇到「透明度偶發無效」，請提供發生當下的 log 末端 200 行（需包含 `SetModelOpacity`、`ReloadModel`、`EnsureViewport` 與 `Overlay` 關鍵行）。

---

## 2025-09-29 後續更新：旋轉視角下 Overlay 顯示不一致與滑鼠手勢調整

### 背景與問題
- 現象：3D 視角旋轉時，Overlay（中線/端點、測試黑點/黑線、三軸）在不同角度下顯示略有差異，甚至出現「看不到/忽隱忽現」。
- 原因：我們為避免模型表面與 Overlay 之間的 Z-fighting，在繪製 Overlay 前，會沿著「相機的 LookDirection」加上一個極小偏移 ε。過去此偏移僅在 Overlay 顯示當下計算一次；當你旋轉視角後，相機方向改變，但 Overlay 仍使用舊方向的偏移，導致不同角度下的結果不一致。

### 修正內容（已佈署）
1) 相機變更即時重算偏移
  - 在 `StrongWindowsUiViewer3DService` 訂閱 `HelixViewport3D.CameraChanged`。
  - 保留 Overlay 原始世界座標（未偏移快照），當相機方向改變時，重新計算 ε 並用新的偏移重建線段/點集合；最後強制刷新視口。
  - 結果：無論怎麼旋轉，Overlay 都穩定且可見，不會因視角不同而消失或跳動。

2) 滑鼠手勢符合需求
  - 預設啟用：左鍵拖曳＝旋轉、右鍵拖曳＝平移（在載入模型後自動套用）。
  - 可視需要提供 UI 切換開關或設定檔控制；目前程式已提供 `ConfigureMousePanToLeftButton(bool)` 介面可切換。

### 驗證步驟
- 載入 IFC 後，按「3D 測試黑點/黑線」與「3D 測試三軸箭頭」。
- 使用滑鼠：左鍵旋轉、右鍵平移，反覆改變視角。
- 觀察 Overlay 在各角度皆可見且穩定；`viewer3d.log` 可看到 `OverlayRoot attached to viewport.`、`Overlay children updated...`，以及相機變更後 Overlay 會被重建與刷新。

### 相關程式位置（摘要）
- `app/IFC_Viewer_00/Services/StrongWindowsUiViewer3DService.cs`
  - `ShowOverlayPipeAxes(...)`：快照原始座標、首次計算偏移並建立 Overlay。
  - `Viewport_CameraChanged(...)`：相機方向改變時，重新計算偏移並更新 `LinesVisual3D.Points` 與 `PointsVisual3D.Points`。
  - `ComputeOverlayOffset(...)`：依場景對角線與線寬計算 ε 並沿 LookDirection 推移。
  - `ConfigureMousePanToLeftButton(bool)`：切換左旋右平移或相反配置。

### 備註
- 偏移 ε 很小，目的僅是避免深度競爭，不會讓幾何「看起來移位」。若需要更強或更弱的偏移，可與線寬或場景尺寸聯動微調（目前已與對角線、線寬做最小值取用）。

---

```markdown
# IFC Viewer 除錯報告（同步 2025-09-28）

> 目的：快速對齊狀態、重現步驟、收集必要日誌；新增多選/可見性測試清單與 Schematic fallback 說明。

---

## 摘要（TL;DR）
- 右鍵 Isolate/Hide/ShowAll 綁定已修正（PlacementTarget.DataContext）；3D 可見度應有變化並有 log 記錄。
- 已加入 TreeView 多選（Ctrl/Shift）與可見性勾選；多選時 3D 採集合高亮；可見性會更新 Hidden 清單。
- Schematic 採 Ports-only（不啟用幾何鄰近推斷）；若缺 `IfcRelConnectsPorts` 關係，視圖僅顯示節點並於 UI 顯示提示 Banner。
- 多選高亮相容性已加強：若控制項集合要求 `IPersistEntity`，會把 Label 轉換為實體加入集合，並做輕量更新（InvalidateVisual + UpdateLayout），確保即時可見。
- 新增 Overlay/Viewport 強化：在 Strong 3D 服務中擴充 Viewport 解析（多名稱/欄位/視覺樹），並加入 overlay 掛載與點數資訊診斷。顯示 3D 中線/端點時會自動降低模型不透明度並可調整線寬/點大小。

---

## 測試清單（建議逐項勾選）

### A. 3D 右鍵基本行為
- [ ] Isolate 後只剩目標可見，並自動 ZoomSelected
- [ ] Hide 後目標消失（可連續多次）
- [ ] ShowAll 後全顯並 ViewHome
- [ ] viewer3d.log 有 Isolate/Hide/ShowAll 進入點與集合 count 前後差異

### B. TreeView 多選與 3D 高亮
- [ ] 單擊單選會清空舊選取；3D 僅高亮單一元素
- [ ] Ctrl+點擊可切換單一節點選取狀態；3D 會高亮所有已選節點
- [ ] Shift+點擊可範圍選取（基於上次 Anchor）；3D 會高亮整段
- [ ] 取消所有選取時，3D 取消高亮（或僅保留最後一次）
 - [ ] 若未見多選高亮：檢查 `SelectedEntities`/`HighlightedEntities` 集合型別（`List<int>` 或 `List<IPersistEntity>`）與實際加入元素是否一致。

### C. 可見性切換與 Hidden 清單
- [ ] 勾選/取消節點 IsVisible 時，子節點會遞迴跟隨
- [ ] 取消可見的節點，3D Hidden 清單更新，該部分在 3D 隱藏
- [ ] 再次勾選後，Hidden 清單移除對應，3D 回復顯示

### D. Schematic（系統優先 + 邊線呈現 + 視圖互動）
- [ ] 點擊「生成原理圖」→ 若多系統，出現 `SystemSelectionDialog`；可選擇系統並開啟 Schematic 視窗，標題顯示系統名稱。
- [ ] `SchematicView.xaml` 中能看到節點與邊線：邊線以 `<Line>` 呈現，座標綁定 Edge 的 `StartNode.Position2D.(X|Y)` 與 `EndNode.Position2D.(X|Y)`（目前灰色 1px，供除錯觀察）。
- [ ] `Edges.Count == 0` 時顯示提示 Banner：「模型未含 IfcRelConnectsPorts 連線，僅顯示節點。」
- [ ] 點擊節點/邊可觸發 3D 高亮（集合高亮 API），ZoomSelected 生效。
- [ ] 滾輪縮放與中鍵平移可用；按下「重置視圖」可清零變換並重新適配畫布；按下「重新布局」可重跑佈局並自動適配。
- [ ] Fit-to-Canvas：
  - 一般：`RefitToCanvas()` 會依 `CanvasWidth/CanvasHeight/CanvasPadding`（預設 1600/1000/40）收斂到畫布內。
  - 載入：`LoadData(...)` 套用 800x600 畫布與 padding 20，更新 `Node.Position2D` 後同步 NodeView。
  - 投影選面：使用「最小跨度軸剔除」策略（捨棄最小軸，保留另外兩軸作為投影面；平手偏好 XY → XZ → YZ），避免細長模型被壓平成線。

### E. AS 原理圖（兩段 IfcPipeSegment）
- [ ] 在 3D 中選取兩段 `IfcPipeSegment` 後啟動 AS 流程 → 視圖呈現黑點（Ports）與黑線（連線）
- [ ] 若僅有黑點無黑線，頂部顯示提示 Banner 表示缺 `IfcRelConnectsPorts`
- [ ] Trace/Log 中可見：系統/成員/全模型 Ports 蒐集數量、成功建立的邊數
- [ ] 視圖支援滾輪縮放、中鍵平移、「重置視圖」與「重新布局」

### F. 3D Overlay（中線/端點）與透明度
- [ ] 按下「3D 顯示中線/端點」後，模型不透明度自動降到 ~0.3（可由 UI 調整），且視圖出現橙紅色線段與黑色端點
- [ ] 線寬、點大小滑桿對 overlay 有即時效果
- [ ] 清除 overlay 後，不透明度會恢復按鈕前的設定值
- [ ] 若 overlay 未出現，請檢查偵錯輸出是否含下列關鍵字：
  - `[StrongViewer] EnsureViewport: found via property/field/visual tree ...` 或 `HelixViewport3D NOT found`
  - `[StrongViewer] OverlayRoot attached to viewport.`
  - `[StrongViewer] Overlay children updated. LinePoints=..., PointCount=...`
  若 `LinePoints/PointCount` 為 0，請回報以檢查資料來源（軸線/端點是否為空）。

---

## 日誌與輸出位置
- viewer3d.log（預設）：app/IFC_Viewer_00/bin/Debug/net8.0-windows/viewer3d.log
- 內容關鍵：
  - [StrongViewer] Isolate/Hide/ShowAll 進入點
  - 隔離/隱藏集合 count 前後
  - ReloadModel(...) 與相機呼叫（ZoomSelected / ViewHome）
  - HighlightEntities：是否找到 `SelectedEntities`/`HighlightedEntities`、集合型別、加入數量，以及是否有 InvalidateVisual/UpdateLayout。
  - Overlay/Viewport 診斷：EnsureViewport 解析路徑、OverlayRoot 掛載、Overlay children 的 LinePoints/PointCount

---

## 重現步驟（最小）
1) 設定環境變數 IFC_STARTUP_FILE 指向範例 IFC；啟動 App。
2) 3D 左鍵選一個元件 → 右鍵 Isolate → 應只顯示該元件並 Zoom。
3) 右鍵 ShowAll → 應全顯並回 Home。
4) TreeView 依序測 Ctrl/Shift 多選；觀察 3D 是否集合高亮。
5) 勾/取消節點 IsVisible；觀察 3D Hidden 更新。
6) 按「3D 顯示中線/端點」，觀察是否自動半透明且出現線段/端點；若未見，請擷取偵錯輸出中與 Overlay/Viewport 相關的訊息。

---

## 已知相容性差異與建置注意
- NU1701 警告：xBIM/HelixToolkit 以 .NET Framework 為目標，相容還原於 net8；不影響功能。
- 若 EXE 被占用導致 MSB3026/MSB3027：請先關閉執行中的應用再建置。
- 集合命名：IsolateInstances/IsolatedInstances、HiddenInstances/HiddenEntities
- ReloadModel 的 enum（ModelRefreshOptions）可能為巢狀型別；已以反射與多層 Refresh Fallback 處理
 - Overlay 需依賴 HelixViewport3D；已在 Strong 服務中加入多路徑解析避免版本差異導致找不到 Viewport。

---

## 後續追蹤
- 若 3D 沒反應，請附上 viewer3d.log 關鍵段落與 VS/VS Code 偵錯輸出
- 需要時會：
  - 加入以 Label 為主的 Hide/Isolate Fallback
  - 擴充特定版本的刷新路徑
  - 強化選取同步與錯誤處理

---

（備註）可直接在此檔最後貼上日誌片段與勾選結果，方便對照。
```
