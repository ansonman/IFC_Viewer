# IFC Schema Viewer 模組（可攜版）

這個資料夾提供一個可獨立移植的 WPF 視窗與 ViewModel，用於顯示任一 `IIfcObject` 的 IFC Schema 資訊（基本屬性、Express 屬性、Inverse 關聯、Property Sets）。

已在 .NET 8 WPF + xBIM 6.x 環境下驗證。模組本身不依賴 3D Viewer，因此可以在任何 WPF 專案中使用。

---

## 功能
- 顯示：
  - Basic（類型、GlobalId、ExpressType、Entity Label）
  - Attributes（優先使用 xBIM ExpressType 中繼資料；失敗則反射公有屬性）
  - Inverses（反向關聯摘要：項目數或簡述）
  - PropertySets（每個 PSet 底下的屬性與值）
- 搜尋/篩選：輸入文字即時過濾樹狀節點
- 展開/收合全部
- 右鍵複製：名稱、值或「名稱=值」到剪貼簿

---

## 相依套件（NuGet）
請在你的 WPF 專案中安裝：

- CommunityToolkit.Mvvm（MVVM 屬性與 ObservableObject）
- Xbim.Essentials（提供 IIfcObject / IPersistEntity 與 PSet 模型）

範例（.csproj 片段）：

```xml
<ItemGroup>
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
  <PackageReference Include="Xbim.Essentials" Version="6.*" />
</ItemGroup>
```

> 備註：不需要 HelixToolkit 或 Xbim.WindowsUI，因為本模組不直接渲染 3D。

---

## 檔案結構
- `Views/IfcSchemaViewerWindow.xaml`：WPF 視窗（UI + TreeView）
- `Views/IfcSchemaViewerWindow.xaml.cs`：視窗事件與外部 API（`ShowEntity(IIfcObject)`）
- `ViewModels/IfcSchemaViewerViewModel.cs`：樹狀資料建構、搜尋過濾、字串摘要
- `Services/IfcStringHelper.cs`：字串安全轉換小工具

命名空間已統一為：
- `IfcSchemaViewer.Views`
- `IfcSchemaViewer.ViewModels`
- `IfcSchemaViewer.Services`

方便你直接加入任何專案，而不會與現有命名空間衝突。

---

## 安裝（拷貝整合）
1) 將本資料夾 `modules/IfcSchemaViewer` 底下的 `Views/`, `ViewModels/`, `Services/` 複製到你的 WPF 專案目錄中。
2) 在專案檔（.csproj）中確認包含這些檔案；一般使用「加入現有項目」即可。
3) 安裝相依 NuGet 套件（見上節）。
4) 編譯專案，確保沒有命名空間衝突或遺漏參考。

---

## 程式整合方式

你可以在任何取得 `IIfcObject` 的地方（例如 3D 點擊、TreeView 選擇等）呼叫下列程式碼：

```csharp
using IfcSchemaViewer.Views;
using Xbim.Ifc4.Interfaces;

// 建議在 MainWindow 或你的 ViewModel 中維持單一視窗實例
private IfcSchemaViewerWindow? _schemaWindow;

void ShowSchema(IIfcObject entity)
{
    _schemaWindow ??= new IfcSchemaViewerWindow
    {
        Owner = System.Windows.Application.Current?.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(w => w.IsActive)
    };
    _schemaWindow.ShowEntity(entity);
}
```

- 若你的 3D 檢視器是 xBIM WindowsUI（DrawingControl3D），可以在 HitTest 或 SelectedEntity 變更時取得 `IIfcObject` 後呼叫 `ShowSchema`。
- 視窗已內建「若最小化則還原、BringToFront」的處理。

---

## 使用說明
- 開啟視窗後，點選專案內任一 `IIfcObject` → 呼叫 `ShowEntity` 會覆寫目前內容。
- 輸入「搜尋」即時過濾節點（名稱或值比對）。
- 右鍵可複製「名稱 / 值 / 名稱=值」。
- 展開/收合全部，可快速瀏覽大型節點樹。

---

## 常見問題（FAQ）
- 看不到 Property Sets？
  - 請確認你的 `IIfcObject` 本身有 `IsDefinedBy` 與 `IIfcPropertySet`，且 `NominalValue` 不是空值。
- Attributes 顯示太多或太少？
  - 模組會優先使用 xBIM 的 ExpressType 屬性清單；若該資料在你的環境不可用，會改用反射列舉公有屬性（可能較雜）。可依需求自行裁剪 `BuildAttributes`。
- 能不能改成 UserControl？
  - 可以。把 `Window` 改為 `UserControl`，將 `ShowEntity` 換成公開方法，容器端自行管理。

---

## 授權與致謝
- 本模組以程式碼片段方式提供，依你專案授權整合即可。
- 感謝 xBIM Toolkit 與 CommunityToolkit.Mvvm 提供的生態。

---

## AI 安裝整合 Prompt（直接複製貼給 VS Code Copilot/ChatGPT）

以下是一段可直接貼給 AI 助手的指令，協助在你的 .NET 8 WPF 專案中安裝與整合本模組（假設你的工作區已包含本資料夾 `modules/IfcSchemaViewer`）。

```
你是 VS Code 內的 AI 導引助理，請把這個 WPF 模組整合到目前的 .NET 8 WPF 專案：

目標：安裝與整合 modules/IfcSchemaViewer 的 IFC Schema Viewer。

條件：
- Windows + VS Code + .NET 8 WPF 專案
- 工作區內已有資料夾 modules/IfcSchemaViewer（含 Views、ViewModels、Services 與 README）

要求與步驟：
1) 在現有 WPF 專案（含 App.xaml 的那個）底下建立資料夾 Modules/IfcSchemaViewer，將以下三個子資料夾整份複製進專案：
  - modules/IfcSchemaViewer/Views → <WPF 專案>/Modules/IfcSchemaViewer/Views
  - modules/IfcSchemaViewer/ViewModels → <WPF 專案>/Modules/IfcSchemaViewer/ViewModels
  - modules/IfcSchemaViewer/Services → <WPF 專案>/Modules/IfcSchemaViewer/Services

2) 確認新檔案被專案包含（必要時更新 .csproj Include），保留原命名空間：
  - IfcSchemaViewer.Views
  - IfcSchemaViewer.ViewModels
  - IfcSchemaViewer.Services

3) 在專案 .csproj 加入相依套件（若尚未有）：
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
    <PackageReference Include="Xbim.Essentials" Version="6.*" />
  </ItemGroup>

4) 建置專案一次；若有遺漏參考或命名衝突，請自動修正並回報。

5) 在主視窗（或你的 MainViewModel）加入可重用的視窗實例與一個公開方法用來顯示 Schema：
  - using IfcSchemaViewer.Views;
  - 新增欄位：private IfcSchemaViewerWindow? _schemaWindow;
  - 新增方法：
    void ShowSchema(Xbim.Ifc4.Interfaces.IIfcObject entity) {
     _schemaWindow ??= new IfcSchemaViewerWindow {
      Owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
     };
     _schemaWindow.ShowEntity(entity);
    }

6) 幫我加一個簡單驗證：載入 IFC 模型後，挑第一個 IIfcWall（若沒有就 IIfcProduct），呼叫 ShowSchema 顯示視窗，並在完成後移除或改成綁選取事件。

7) 完成後：
  - 回報你新增/修改了哪些檔案
  - 執行一次建置，貼出摘要（成功/失敗與警告數）
  - 說明如何從 TreeView 或 3D 點選取得 IIfcObject 並呼叫 ShowSchema

請直接執行上述步驟，不要只提供建議；任何錯誤請修正後重試再回報。
```
