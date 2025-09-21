# 除錯報告（debug_report）

本報告聚焦最近的「TreeView 單選無法在 3D 高亮」回歸問題的診斷與修復。

## 症狀
- 從 TreeView 點選單一元素時，3D 視圖未見任何高亮反應。

## 初步線索（viewer3d.log）
- 看到控制項屬性：`Selection: EntitySelection`、`SelectedEntity: IPersistEntity` 可寫
- 模型載入、CreateContext、ShowAll、ViewHome 都正常
- 未見預期的 `[Service] HighlightEntities(labels)` 或 `[StrongViewer] Using member for selection: ...` 訊息

## 研判
- Highlight 流程可能未到達 3D 服務，或 3D 服務把 `Selection` 視為集合而實際是 `EntitySelection`，導致設定不生效。

## 修復策略
1. 強化 StrongWindowsUiViewer3DService：
   - `HighlightEntities(labels/entities)`：
     - 先嘗試集合成員；若 member 對應物件不是集合，視其為 `EntitySelection` 類型，嘗試其內部屬性（SelectedEntities/EntityLabels/Items…）或方法（Add/AddRange/Set/SetRange/SetLabels/SetEntities，以及 (model, id) 簽名）
     - 若全部嘗試失敗，退回 `SelectedEntity` 指派第一筆，確保至少單選可見
     - 清空選取時清空 Selection 或 `SelectedEntity = null`
   - 寫入或清空後刷新（InvalidateVisual + UpdateLayout）
2. 增強日誌：在服務層記錄呼叫與後援路徑

## 結果
- 專案建置成功（僅 NU1701 相容性警告），無語法/連結錯誤
- 預期可恢復 TreeView 單選 → 3D 單項高亮
- 多選：若控制版本支援集合會生效；否則退回顯示第一個選取

## 待驗證項目
- 單選、Ctrl 多選、點空白清空
- 若仍未高亮，請提供最新 `viewer3d.log` 中與選取/集合相關的段落，以便針對你的控制版本作更進一步適配

---

## 2025-09-21：3D 視圖單擊崩潰（Crash）

### 症狀
- 在 3D 視圖單擊實體或場景時，應用程式立即崩潰。

### 研判（root cause）
- HitTest 命中物件有機會不是 `FrameworkElement`，但流程會嘗試以 `FrameworkElement.TagProperty` 讀取 `GetValue`，導致拋出例外。
- 視口（viewport）在少數時機可能為 null，或 `TranslatePoint` 失敗未被保護，例外泡泡至 UI 執行緒。

### 修補
- 檔案：`Services/StrongWindowsUiViewer3DService.cs`
- 範圍：`HitTest`、`FindHit`、`GetClickedEntity`
- 作法：
  - 對 Tag 讀取全面 try/catch；沒有 Tag 或讀取異常即忽略該命中（不再拋例外）。
  - 新增 `viewport == null` 與 `TranslatePoint` 的保護與 Trace。
  - 於關鍵節點加入 Trace，以利問題再現時快速定位。

### 驗證
1. 建置專案（Debug/Release 任一）。
2. 載入 IFC 後在 3D 單擊/空白區域點擊數次：不應崩潰；未命中時應無選取變化。
3. 雙擊實體：應縮放至所選（ZoomSelected）。
4. 檢視 `viewer3d.log`：可見 HitTest/FindHit 相關 Trace 與忽略命中的原因。

### 2025-09-21：單擊高亮延遲（Performance）

#### 症狀
- 3D 單擊高亮有明顯延遲，尤其連續點擊或多選時。

#### 改善
- 在 3D 服務加入以下最佳化：
  - 反射快取：Selected/Hidden/Isolated 成員、Viewport
  - 輕量重繪：僅 InvalidateVisual；避免 UpdateLayout
  - labels 去重：降低集合更新量

#### 驗證建議
- 快速連點多個元件，觀察高亮是否更即時
- 同步觀察右側面板是否因選取合併而更穩定（減少抖動）

### Trace 關鍵字（示例）
- `HitTest viewport is null`、`TranslatePoint failed`、`Tag read threw`、`no Tag on hit visual`、`returning null entity`。
