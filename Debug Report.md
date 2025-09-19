# IFC Viewer 除錯報告

> 目的：快速對齊目前狀態、重現步驟、蒐集必要日誌，並驗證「右鍵 Isolate/Hide/ShowAll 能正確影響 3D 視圖」的修正。

---

## 摘要（TL;DR）
- 問題：先前右鍵選單（Isolate/Hide/ShowAll）執行後「沒有任何 3D 視覺變化」。
- 根因（已修正）：WPF ContextMenu 不在視覺樹內，命令 Binding 未能綁到視窗 DataContext；已改用 `PlacementTarget.DataContext`。
- 目前狀態：
  - 應用可建置與執行；SetModel 與 ViewHome 已記錄在 viewer3d.log。
  - 正在等待使用者於 3D 視圖實際右鍵執行 Isolate/Hide/ShowAll 後提供最新日誌，以確認視覺與日誌均正確。

---

## 範圍與目標
- 目標：
  1) 右鍵 Isolate/Hide/ShowAll 會對 3D 視圖產生可見影響。
  2) 點選 3D 時可同步至屬性與樹狀（已完成屬性，同步樹狀已實作）。
  3) 每次可見度變更都在 viewer3d.log 有清楚 Trace（進入方法、集合 count 前後、呼叫 ReloadModel/相機）。

---

## 環境與版本
- OS：Windows（PowerShell v5.1）
- .NET：.NET 8（目標 net8.0-windows）
- WPF + MVVM
- 3D 控制：xBIM DrawingControl3D（HelixToolkit.Wpf 基底）
- 主要套件（版本請補）：
  - Xbim.Essentials / Xbim.Ifc
  - Xbim.Presentation（DrawingControl3D）
  - Xbim.ModelGeometry.Scene（Xbim3DModelContext）
  - Xbim.Geometry.Engine.Interop
  - HelixToolkit.Wpf

---

## 目前行為 vs 預期行為
- 預期：
  - Isolate：只顯示被隔離的實體（置換隔離集合；清空隱藏），並 ZoomSelected。
  - Hide：將該實體加入隱藏集合；其他內容保持。
  - ShowAll：清除隔離與隱藏；重載模型並 ViewHome。
- 目前（待驗證）：
  - 綁定已修正；需要實際右鍵操作一次，確認 3D 的可見度變化與相機動作確實發生，並在日誌中出現對應 Trace。

---

## 最小重現步驟
1) 啟動應用（以環境變數 `IFC_STARTUP_FILE` 指向 IFC 檔，例如 `Project1.ifc`）。
2) 模型載入後，在 3D 視圖中：
   - 左鍵點選一個構件（右側屬性表應更新；樹狀亦應同步到該節點）。
   - 在 3D 空間右鍵 → 選單：
     - 先按「Isolate」，預期只剩該構件顯示，並自動 ZoomSelected。
     - 再對另一構件右鍵「Hide」，預期該構件從場景消失。
     - 最後按「Show All」，預期回到全顯，並 ViewHome。

---

## 近期關鍵更動（可能影響行為）
- `Services/StrongWindowsUiViewer3DService.cs`
  - 新增/強化：SetModel、Highlight、Isolate、Hide、ShowAll、RefreshAfterFilterChange、HitTest、強韌的 ReloadModel options/fallback、詳細 Trace。
- `Views/MainWindow.xaml`
  - 修正 ContextMenu 綁定：改用 `PlacementTarget.DataContext` 以正確觸發 ViewModel 命令。
- `ViewModels/MainViewModel.cs`
  - 命令委派到 IViewer3DService；維護 HighlightedEntity；同步樹狀與屬性。
- `App.xaml.cs`
  - 初始化 Trace listener，輸出到 `viewer3d.log`。

---

## 日誌蒐集指引（請依序執行）
- 檔案 1：viewer3d.log
  - 位置（預設）：`app/IFC_Viewer_00/bin/Debug/net8.0-windows/viewer3d.log`
  - 期望新增的片段（請擷取貼上）：
    - `[StrongViewer] Isolate` / `Hide` / `ShowAll` 進入點
    - 變更前/後集合計數（Isolated/Hidden）
    - `ReloadModel(...)` 與相機呼叫（`ZoomSelected`、`ViewHome`）
- 檔案 2：Visual Studio（或 VS Code）偵錯輸出
  - 執行相同步驟後，將輸出視窗中相關 Trace 貼上（同樣會包含 `[StrongViewer] ...`）。
- 操作步驟（蒐集時）：
  1) 啟動應用 → 模型載入完成。
  2) 3D 右鍵依序執行：Isolate → Hide → ShowAll。
  3) 關閉應用後，開啟 `viewer3d.log`，擷取上述關鍵區段。

---

## 觀察與初步結論（模板）
- 執行 Isolate 後，3D：
  - [ ] 有明顯只顯示被選構件
  - [ ] 有自動 ZoomSelected
  - 日誌：
    - [ ] 出現 `Isolate` 進入點與集合 count 變化
- 執行 Hide 後，3D：
  - [ ] 構件消失
  - 日誌：
    - [ ] 出現 `Hide` 進入點與 Hidden 集合 count 增加
- 執行 ShowAll 後，3D：
  - [ ] 視圖全顯並回 Home 視角
  - 日誌：
    - [ ] 出現 `ShowAll` 與 `ViewHome`

---

## 驗證清單（逐項勾選）
- [ ] ContextMenu 命令在 3D 視圖上可觸發（可在日誌看到對應方法進入點）
- [ ] Isolate 置換隔離集合並清空隱藏集合
- [ ] Hide 加入隱藏集合（不影響既有隔離）
- [ ] ShowAll 清空隔離與隱藏；重載與 ViewHome 成功
- [ ] ReloadModel 使用「保留相機/選取」選項（若版本支援）或 fallback 路徑
- [ ] 相機行為：Isolate 後 ZoomSelected；ShowAll 後 ViewHome
- [ ] 3D 點選 → 右側屬性表更新；樹狀同步高亮

---

## 已知風險 / 待確認
- xBIM 版本差異：
  - 隔離/隱藏集合可能是 `IsolateInstances/IsolatedInstances` 與 `HiddenInstances/HiddenEntities`，元素型別為 `IPersistEntity` 或 `int`（Label）。
  - 已在服務中加入反射/雙制式處理，但仍需實機驗證。
- UI 執行緒：所有 3D 狀態變更需在 UI 執行緒；服務已防護，但仍需觀察。

---

## 附件與路徑（參考）
- 服務實作：`app/IFC_Viewer_00/Services/StrongWindowsUiViewer3DService.cs`
- 視圖 XAML：`app/IFC_Viewer_00/Views/MainWindow.xaml`
- ViewModel：`app/IFC_Viewer_00/ViewModels/MainViewModel.cs`
- 追蹤日誌：`app/IFC_Viewer_00/bin/Debug/net8.0-windows/viewer3d.log`

---

## 下一步
1) 請依「日誌蒐集指引」實測一次 Isolate → Hide → ShowAll，提供最新 `viewer3d.log` 與偵錯輸出片段（含 `[StrongViewer] ...`）。
2) 我將依日誌與畫面結果，必要時：
   - 加入更強的 fallback（例如以 Label 為主的 Hide/Isolate）
   - 追加針對特定 xBIM 版本的刷新路徑
   - 強化相機/選取同步邏輯與錯誤處理

---

（備註）你可直接在此檔案下方貼上日誌截圖或片段，並勾選驗證清單的項目，以便後續收斂。
