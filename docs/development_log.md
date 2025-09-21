# 開發日誌（development_log）

記錄主要里程碑、重要改動與回歸修復。

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
