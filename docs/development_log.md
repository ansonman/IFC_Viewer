# 開發日誌（development_log）

記錄主要里程碑、重要改動與回歸修復。

## 2025-10-09
### PSC P4：管線示意（Pipe Schematic Canvas）階段化擴充 – S1（僅加入配件節點）
本日新增一個獨立入口「PSC P4」以隔離現有 PSC P3 行為：
- P4 內部沿用 P3 的主流程（拓撲 / Run 分組 / 終端點投影），再套用階段化策略 S1：插入 PipeFitting 類型節點（PipeFitting, Valve, 其餘後續再評估）。
- 新增 `PipeAxesOptions`（IncludeFittings / IncludeTerminals），入口 P4 以 `IncludeFittings=true` 啟用配件節點注入；P3 保持舊簽名不受影響。
- System 命名：當含配件節點時在顯示名稱附加「+Fittings」標記以利視覺辨識（不影響既有 SystemId / 後續查詢）。

### 視覺與互動
- 新增節點分類著色：FlowTerminal / PipeFitting / Valve / Pipe 端點 (預設)。
- 圖例（Legend）加入 Fitting (#3399CC) 與 Valve (DarkGoldenrod) 條目；節點 ToolTip 直接顯示 IfcType 分類文字，利於比對 IFC 原始型別。
- 此階段圖例條目為靜態；後續（B）將改為「僅在該分類至少出現一個節點時才顯示」。

### 風險 / 限制
- 僅完成 S1：以 IFC 中 PipeFitting/Valve 元件中心點投影為節點，尚未重寫邊界（Edge）或以配件取代原端點（S2）。
- Ports 拓撲仍沿用 P3 版本；複雜三通 / 彎頭的方向語意尚未揭露。
- NU1701 相容性警告仍存在（第三方套件目標框架差異），本變更未觸及封裝層，不影響。

### 後續里程碑（草案）
1. (B) 動態圖例：根據當前載入資料計算 HasFittingNodes / HasValveNodes → 控制可見性。
2. S2：以配件節點替換原管端點簡化冗餘（避免「接頭兩側 + 原端點」重複）。
3. S3：完整 Ports 拓撲（多連接 / 精確方向 / 夾角呈現），並為三通/四通建立幾何方向標記。
4. 增加單元測試：驗證在 IncludeFittings=true / false 下節點計數與 SystemName 標記差異。
5. 文件：在 Schematic 模組 README 增補「階段化策略（S1~S3）」章節與使用者驗收清單。

### 驗收檢查（今日）
- P3 與舊按鈕行為無變化（回歸手測：節點計數、無 +Fittings 後綴）。
- P4 產出節點計數 ≥ P3（至少多出所有 PipeFitting/Valve 節點數）。
- 圖例與 ToolTip 出現新分類文字且無崩潰 / 無 NRE。
- Build 成功；無新增警告（僅既有 NU1701）。

### 待追蹤議題
- 若後續實作 S2，需重新檢視現行選取同步與 ZoomSelected 是否會因節點替換造成映射遺失；可能須維護「原端點 → 配件節點」對應表。

（以上條目對應需求：A 完成）

## 2025-09-21
  - 若非純集合，嘗試其內部屬性或方法（Add/AddRange/Set/EntityLabels/SelectedEntities 等）
  - 全部路徑失敗則退回 SelectedEntity，確保至少單選可見
  - 清空選取：清空 Selection 或 SelectedEntity=null
  - 寫入後一律做輕量 UI 重繪（僅 InvalidateVisual，避免同步 UpdateLayout）
## 2025-09-21（效能）
### 3D 單擊高亮加速
- 反射快取：選取/隔離/隱藏集合成員的 MemberInfo 快取，避免重複反射查找
- Viewport 快取：快取 HelixViewport3D 加速 HitTest
- 輕量重繪：多處移除同步 UpdateLayout，統一以 InvalidateVisual 交由排程重繪
- 去重：Highlight labels 去重，減少集合 Add 與 UI 更新

### UI 行為與事件
- 單擊僅選取，雙擊才縮放；避免兩者互相干擾
- 選取變更合併/節流（DispatcherTimer ≈50ms），降低重複高亮與右側面板重算
- TreeView 開啟虛擬化（Recycling），避免大樹整體重繪

### 新增/變更
- 強化 `StrongWindowsUiViewer3DService` 的命中測試與選取相容：
  - `HitTest`/`FindHit`/`GetClickedEntity` 增加對非 FrameworkElement 與 Tag 讀取失敗的保護，避免 3D 單擊崩潰。
  - 增加 viewport 為 null 與 `TranslatePoint` 失敗保護。
  - 增加 Trace 記錄，用於問題重現與診斷。
- 多選相容性：
  - `DrawingControl3D.Selection` 若為 `EntitySelection`，嘗試其可用成員與方法；若仍不支援多選，退回 `SelectedEntity`。
- Schematic：
  - `GenerateTopologyAsync` 加入 `RelatingPort/RelatedPort` null 防護，避免 NRE；保留幾何鄰近（<10mm）fallback。

### 風險/注意事項
- xBIM 控制版本差異造成 Selection 與集合 API 行為不一致；已以反射與 fallback 改善，但仍需在目標環境驗證。
- NU1701 警告源於套件目標框架差異，為相容性警告；目前可忽略，若轉換為 native 目標框架版本可逐步移除。

### 待辦/後續
- 減少對 Visual.Tag 的依賴，嘗試以 fragment-to-entity 對應表或事件回流（`SelectedEntityChanged`）提高健壯性。
- 擴充 README 與 docs 的疑難排解與驗收腳本，持續同步落地做法。
- 更新 README：補上回歸修復說明與疑難排解段落

## 2025-09-18 ～ 2025-09-20
- 建立多選資料管線（TreeView ↔ 3D）與 SelectionService，同步來源追蹤避免雙向迴圈
- IViewer3DService 擴充：以 labels/entities 高亮、Isolate/Hide/ShowAll、UpdateHiddenList、HitTest
- 強化 3D 服務跨版本相容：
  - ReloadModel 多種簽名回退（含 ViewPreserveCameraPosition）
  - 隱藏/隔離集合名稱容錯（HiddenInstances/HiddenEntities、IsolateInstances/IsolatedInstances…）
  - Highlight 集合支援 int 與 IPersistEntity 兩種
- TreeView：
  - Shift 範圍選取、Ctrl 切換、點空白清空
  - 可見性勾選遞迴傳播
- Schematic 模組：
  - Ports 關係缺失時採幾何鄰近 fallback（10mm）
  - 雙向選取同步，支援 ZoomSelected

## 2025-09-15 ～ 2025-09-17
- 專案初始化：WPF + xBIM + HelixToolkit；建立 MVVM 骨架與基本檔案佈局
- 初步 3D 操作（ViewHome/ShowAll/ZoomSelected）與模型載入流程
