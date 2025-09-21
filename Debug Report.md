```markdown
# IFC Viewer 除錯報告（同步 2025-09-21）

> 目的：快速對齊狀態、重現步驟、收集必要日誌；新增多選/可見性測試清單與 Schematic fallback 說明。

---

## 摘要（TL;DR）
- 右鍵 Isolate/Hide/ShowAll 綁定已修正（PlacementTarget.DataContext）；3D 可見度應有變化並有 log 記錄。
- 已加入 TreeView 多選（Ctrl/Shift）與可見性勾選；多選時 3D 採集合高亮；可見性會更新 Hidden 清單。
- Schematic 在缺 Ports 時使用幾何鄰近 fallback（< 10mm），推斷邊以 IsInferred 標註。
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

### D. Schematic Fallback（若已接上 UI）
- [ ] 缺 Ports 的模型資料下，仍能看到節點間的推斷邊
- [ ] 推斷邊具備 IsInferred 標記（UI 或報表可觀察）
- [ ] 點擊節點/邊可觸發 3D 高亮（集合高亮 API），ZoomSelected 生效

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

## 已知相容性差異
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
