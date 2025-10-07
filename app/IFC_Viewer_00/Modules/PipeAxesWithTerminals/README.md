# Pipe 軸線 + FlowTerminal 紅點 模組（可攜版）

這個模組將「管段軸線（IfcPipeSegment 中線）」與「FlowTerminal 終端紅點」投影到同一 2D 平面，並以現有的 `SchematicView` 顯示。

- 平台：.NET 8 WPF + xBIM 6.x
- 依賴：
  - `IFC_Viewer_00.Services.SchematicService`
  - `IFC_Viewer_00.ViewModels.SchematicViewModel`
  - `IFC_Viewer_00.Views.SchematicView`
  - 選取服務 `IFC_Viewer_00.Services.ISelectionService`

---

## 檔案
- `Services/PipeAxesWithTerminalsService.cs`
  - 門面服務：封裝平面選擇 → 資料生成 → 開窗顯示流程。

---

## 安裝（拷貝整合）
參考 `Modules/IfcSchemaViewer` 的結構，把本資料夾複製到你的 WPF 專案：

1) 將本模組資料夾 `Modules/PipeAxesWithTerminals` 直接拷貝到目標專案（含子資料夾 `Services/`）。
2) 確認 `.csproj` 有包含新檔案（通常 VS/VS Code 會自動包含）。
3) 你的專案需已包含或能參考下列類別：
   - `IFC_Viewer_00.Services.SchematicService`
   - `IFC_Viewer_00.ViewModels.SchematicViewModel`
   - `IFC_Viewer_00.Views.SchematicView`
   - `IFC_Viewer_00.Services.ISelectionService`（用於 2D 與 3D 之間的選取同步）

若這些類別不在目標專案，請一併拷貝或改以你專案對應的實作替換（只要具備相同責任即可）。

---

## 使用方式（程式碼）

```csharp
using IFC_Viewer_00.Modules.PipeAxesWithTerminals.Services;
using IFC_Viewer_00.Services; // ISelectionService
using Xbim.Ifc;

// 在你的 ViewModel 建構子注入或建立一次 SelectionService
private readonly ISelectionService _selection;

// 觸發顯示
async Task ShowPipeAxesWithTerminalsAsync(IfcStore model)
{
    var svc = new PipeAxesWithTerminalsService(_selection);
    await svc.ShowAsync(model);
}
```

---

## UI 整合建議
- 工具列或選單新增一個按鈕/選項，綁定你的 VM 指令（內部呼叫 `PipeAxesWithTerminalsService.ShowAsync`）。
- 可加入快捷鍵（例：Ctrl+Shift+P）。

---

## AI 安裝整合 Prompt（直接複製給 VS Code Copilot/ChatGPT）

```
你是 VS Code 內的 AI 導引助理，請把這個 WPF 模組整合到目前的 .NET 8 WPF 專案：

目標：安裝與整合 Modules/PipeAxesWithTerminals 模組（管段軸線 + FlowTerminal 紅點到同一平面）。

條件：
- Windows + VS Code + .NET 8 WPF 專案
- 工作區內已有資料夾 Modules/PipeAxesWithTerminals（含 Services 與 README）

要求與步驟：
1) 在現有 WPF 專案底下建立資料夾 Modules/PipeAxesWithTerminals，將此模組整份複製進專案。
2) 確認新檔案被專案包含（必要時更新 .csproj Include）。
3) 專案需能引用/存取下列類別（若未包含，請一併導入或替換）：
   - IFC_Viewer_00.Services.SchematicService
   - IFC_Viewer_00.ViewModels.SchematicViewModel
   - IFC_Viewer_00.Views.SchematicView
   - IFC_Viewer_00.Services.ISelectionService
4) 在主 ViewModel 新增一個指令與處理器，呼叫 PipeAxesWithTerminalsService.ShowAsync(currentModel)。
5) 在主視窗加入選單/工具列項目與快捷鍵，綁定此指令。
6) 建置專案一次；若有遺漏參考或命名衝突，請自動修正並回報。
7) 完成後：
  - 回報新增/修改的檔案
  - 執行一次建置，貼出摘要（成功/失敗與警告數）
  - 說明如何從 UI 觸發與切換平面
```

---

## 注意事項與相依
- 此模組不直接處理 3D Overlay；若需要 3D 疊加，請使用你專案現有的 3D 服務。
- FlowTerminal 紅點座標取得優先序：Ports（含 IfcRelNests）→ LocalPlacement；位置會沿完整 placement chain 計算世界座標。
- 投影與 2D 顯示沿用 `SchematicViewModel` 的投影管線；終端點在 `LoadPipeAxesAsync` 中會以紅色呈現。

---

## 快捷鍵（Keyboard Shortcuts）
以下為目前應用程式中已綁定，且與本模組相關或常用的快捷鍵：


若要自訂或在其他專案中加入這些快捷鍵，可在主視窗 XAML 的 `<Window.InputBindings>` 區塊綁定對應命令。

---

## 診斷選項：顯示縮放錨點（P2）

在 Schematic 視窗工具列可勾選「顯示縮放錨點」來顯示一個短暫的半透明藍點，代表本次滾輪縮放的錨點（滑鼠所在內容座標）。
- 預設關閉，僅建議於測試/診斷時開啟。
- 若發現錨點位置與預期不符，請搭配 DEVELOPMENT_LOG 的 Debug 計畫收集 [FTA] 日誌。

---

## P3 狀態與功能一覽（PSC P3）

目前已提供一個獨立的 Phase 3 入口「PSC P3」，與既有 PSC / PSC P2 隔離，作為 P3 能力的實驗場域。

- 入口位置：
  - 主視窗工具列按鈕「PSC P3」
  - 對應命令：`GeneratePipeAxesWithTerminalsP3Command`
  - 視窗標題：`PSC P3 - ...`

- 已具功能（繼承自 P1/P2 並持續強化）：
  - 2D 原理圖顯示：管段軸線 + FlowTerminal 紅點（投影平面可選 XY/YZ/ZX）
  - 互動：
    - 滑鼠置中縮放（滾輪以滑鼠所在內容點為錨點）
    - 中鍵平移
    - 單擊選取節點/邊，並同步清除後套用 3D 高亮
  - 圖層開關與圖例：終端/管線/標籤 顯示控制、顏色可設定與保存
  - 匯出：PNG 圖片（背景透明，解析度可選）
  - 診斷：可選的「顯示縮放錨點」藍點（預設關閉）

- P3 核心新能力（進度中）：
  - 編輯模式（IsEditMode）：切換後可在 2D 視圖中進行版面調整與規則套用
  - 主路管選取（SetAsMainPipe）：將選定的邊設為主幹（支援垂直 / 水平）
  - 規則佈局服務（RuleBasedLayoutService）：
    - AlignBranches：將分支沿主幹對齊（正交化、間距控制）
    - ConnectTerminals：以曼哈頓（直角）路徑連接終端至主幹（簡化避碰）
  - 命令入口（ViewModel）：
    - `ApplyLayoutRulesCommand`：一鍵套用版面規則（對齊分支 + 連接終端）

以上功能在 P3 中會逐步細化與增強，並盡量保持與 P2 的使用體驗一致。

---

## P3 操作指南（快速上手）

1) 在主視窗點擊「PSC P3」→ 選擇投影平面（預設 XY）→ 開啟 2D 視窗。
2) 以滑鼠滾輪縮放（焦點跟隨滑鼠），中鍵拖移平移畫面。
3) 按一下節點或邊可選取，3D 視圖會同步高亮；可用工具列清空選取。
4) 切換「編輯模式」：
   - 點選你要設為主幹的邊 → 按「設為主路管」（可指定為垂直或水平主幹）。
   - 按「套用版面規則」：
     - 分支會沿主幹對齊（直角/間距）
     - FlowTerminal 會以直角路徑接到最近主幹（簡化避碰）
5) 需要輸出時，使用工具列的匯出 PNG（未來將加入 SVG）。

提示：若要觀察縮放錨點是否正確，可暫時打開「顯示縮放錨點」。

---

## 快捷鍵（建議）

應用層目前已有：

- Ctrl+Shift+F：FlowTerminal 紅點
- Ctrl+Shift+P：Pipe 軸線 + 紅點（PSC）

建議新增（P3）：

- Ctrl+F：搜尋/定位（名稱、Label、HostType）
- F：Fit to selection（置中至當前選取）
- + / -：縮放
- Esc：清除選取

將以上綁到 `MainWindow.xaml` 的 `<Window.InputBindings>` 並對應到 P3 視圖的命令，即可生效。

---

## 限制與注意事項（P3 現階段）

- 規則佈局的避碰為簡化版：靠近主幹與既有節點時會以網格距離偏移，尚未導入完整路徑尋優。
- 目前匯出以 PNG 為主；SVG 正在規劃（將含線段、節點與文字標籤）。
- 多選/框選已被關閉（依 P2 設定），維持單選互動與 3D 高亮同步清空-再套用的策略。
- MSAGL 未導入；P3 走「手動 2D 編輯 + 規則連接」路線以降低相依與複雜度。

---

## 路線圖（下一步）

- 搜尋/定位（Ctrl+F）：
  - 提供關鍵字篩選、高亮匹配、上一筆/下一筆導覽，導覽時自動置中。
- Fit to selection：
  - 選取節點/邊時一鍵置中並採用合適縮放。
- SVG 匯出：
  - 依據畫面狀態輸出矢量圖，包含線段、節點、標籤，可設定是否輸出圖例。
- 資料持久化：
  - 以 JSON 儲存/載入目前的 2D 版面（含主幹、連線結果與樣式）。
- 編輯體驗：
  - Undo/Redo（20 步）、標籤重疊改善、參考網格/吸附。

若要優先哪一項，請在 Issue 或 DEV LOG 中標註；模組會以可回退的步驟逐項落地。
