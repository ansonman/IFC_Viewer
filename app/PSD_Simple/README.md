# PSD Simple 現況紀錄（2025-10-08）

本檔案用於記錄「PSD Simple」獨立可執行模組的嘗試、目前狀態、驗收結論與暫停決策，方便後續回顧或再啟。

## 目標（原始需求）
- 無需 3D 匯入/顯示。
- 有簡易 UI：開啟 IFC → 生成 2D Canvas。
- 預設投影面為 YZ，載入後可切換 XY/XZ/YZ。
- 功能與 PSD P3 的 2D 模組一致（管徑標籤、系統篩選、Run 分組/著色/圖例/CSV、設定持久化等）。
- 獨立資料夾與專案，未來可整合回主系統。

## 已完成工作
- 建置獨立 WPF 專案 `app/PSD_Simple`，連結既有 Schematic 模組（Models/Services/ViewModels/Views/Dialogs/Converters）。
- UI：主視窗（開檔、投影選擇、開啟原理圖）+ 2D 視圖沿用 P3 工具列。
- 2D/3D 解耦：`SchematicView.xaml.cs` 可在無 3D 服務下運作（透過反射檢查，找不到即跳過），主系統仍保留 3D 同步。
- 資料管線對齊 P3：採用 `GeneratePipeAxesWithTerminalsAsync(model, plane, flipY:true)`，切換投影面時重新生成資料。
- 發行設定：提供 FDD 與 Self-contained（單檔）兩種；可產生 `app/PSD_Simple/bin/Publish/SelfContained/PSD_Simple.exe`。

## 驗證結果（關鍵差異）
- 期望：PSD Simple 與 PSD P3 在相同 IFC 與投影面下，2D 視圖應一致。
- 實際：即便改用與 P3 相同資料管線並於切面切換時重算，視覺結果仍與 P3 不一致（使用者回饋「結果一樣，未能成功」）。
- 初判：差異非僅因缺少 3D 資訊；更可能源於載入/佈局/適配順序、回寫 Position2D 的時機、或設定持久化預設值／讀取時機差異。

## 暫停決策（2025-10-08）
- 結論：本階段 PSD Simple 與 PSD P3 的 2D 視圖仍存在可見差異，未達「完全一致」的驗收標準。
- 決議：暫停 PSD Simple 的開發，回歸 PSD P3 進行作業。

## 日後再啟建議
- 準備最小重現檔案與操作腳本（兩端重放比對）。
- 序列化比對 `SchematicData` 與 `SchematicViewModel` 關鍵欄位（座標、顏色、開關、Run/Legend）。
- 固化 Fit/Relayout/同步回寫 Position2D 的順序，提取為共用服務避免路徑差異。
- 固定畫布與種子，輸出 PNG 快照並以 SSIM/像素差量化差異。

## 檔案/路徑
- 自包含 EXE：`app/PSD_Simple/bin/Publish/SelfContained/PSD_Simple.exe`
- 主程式：`app/PSD_Simple/PSD_Simple.csproj`
- 主視窗：`app/PSD_Simple/MainWindow.xaml(.cs)`
- 2D 視圖：`app/IFC_Viewer_00/Views/SchematicView.xaml(.cs)`
- 服務：`app/IFC_Viewer_00/Services/SchematicService.cs`
- ViewModel：`app/IFC_Viewer_00/ViewModels/SchematicViewModel.cs`
