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
