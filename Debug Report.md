```markdown
# IFC Viewer 除錯報告（同步 2025-09-23）

> 目的：快速對齊狀態、重現步驟、收集必要日誌；新增多選/可見性測試清單與 Schematic fallback 說明。

---

## 摘要（TL;DR）
- 右鍵 Isolate/Hide/ShowAll 綁定已修正（PlacementTarget.DataContext）；3D 可見度應有變化並有 log 記錄。
- 已加入 TreeView 多選（Ctrl/Shift）與可見性勾選；多選時 3D 採集合高亮；可見性會更新 Hidden 清單。
- Schematic 採 Ports-only（不啟用幾何鄰近推斷）；若缺 `IfcRelConnectsPorts` 關係，視圖僅顯示節點並於 UI 顯示提示 Banner。
 - 多選高亮相容性已加強：若控制項集合要求 `IPersistEntity`，會把 Label 轉換為實體加入集合，並做輕量更新（InvalidateVisual + UpdateLayout），確保即時可見。

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

---

## 日誌與輸出位置
- viewer3d.log（預設）：app/IFC_Viewer_00/bin/Debug/net8.0-windows/viewer3d.log
- 內容關鍵：
  - [StrongViewer] Isolate/Hide/ShowAll 進入點
  - 隔離/隱藏集合 count 前後
  - ReloadModel(...) 與相機呼叫（ZoomSelected / ViewHome）
  - HighlightEntities：是否找到 `SelectedEntities`/`HighlightedEntities`、集合型別、加入數量，以及是否有 InvalidateVisual/UpdateLayout。

---

## 重現步驟（最小）
1) 設定環境變數 IFC_STARTUP_FILE 指向範例 IFC；啟動 App。
2) 3D 左鍵選一個元件 → 右鍵 Isolate → 應只顯示該元件並 Zoom。
3) 右鍵 ShowAll → 應全顯並回 Home。
4) TreeView 依序測 Ctrl/Shift 多選；觀察 3D 是否集合高亮。
5) 勾/取消節點 IsVisible；觀察 3D Hidden 更新。

---

## 已知相容性差異與建置注意
- NU1701 警告：xBIM/HelixToolkit 以 .NET Framework 為目標，相容還原於 net8；不影響功能。
- 若 EXE 被占用導致 MSB3026/MSB3027：請先關閉執行中的應用再建置。
- 集合命名：IsolateInstances/IsolatedInstances、HiddenInstances/HiddenEntities
- ReloadModel 的 enum（ModelRefreshOptions）可能為巢狀型別；已以反射與多層 Refresh Fallback 處理

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
