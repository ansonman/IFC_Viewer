# IFC_Viewer_00

一個基於 .NET 8 WPF + xBIM WindowsUI (DrawingControl3D) + HelixToolkit 的 IFC 檢視器（MVVM）。

## 特色
- 強型別包裝 `DrawingControl3D` 的 3D 服務，支援：
  - 左鍵點選同步屬性面板
  - 右鍵功能：僅顯示選取項（Isolate）、隱藏選取項（Hide）、全部顯示（ShowAll）
  - 相機控制：ViewHome、ZoomSelected
  - 日誌追蹤與跨版本容錯（反射）
- HelixToolkit.Wpf 輔助 HitTest，精準解析點選的 Ifc 實體

## 技術棧
- .NET 8 WPF (net8.0-windows)
- xBIM: Xbim.Presentation (WindowsUI), Xbim.Essentials, Xbim.ModelGeometry.Scene, Xbim.Geometry.Engine.Interop
- HelixToolkit.Wpf

## 建置與執行
- 以 PowerShell 在專案根目錄執行：

```powershell
# 建置
 dotnet build .\app\IFC_Viewer_00\IFC_Viewer_00.csproj --nologo

# 執行（可指定啟動 IFC）
 $env:IFC_STARTUP_FILE='j:\AI_Project\IFC_Viewer_00\Project1.ifc'; dotnet run --project .\app\IFC_Viewer_00\IFC_Viewer_00.csproj --no-build --nologo
```

## 右鍵功能（Sprint 1）
- 僅顯示選取項（Isolate）
  - 清空 Isolate 集合並加入目標；清空 Hidden 集合
  - 呼叫 `ReloadModel(ViewPreserveCameraPosition)`；最後 `ZoomSelected()`
- 隱藏選取項（Hide）
  - 將目標累加到 Hidden 集合
  - 呼叫 `ReloadModel(ViewPreserveCameraPosition)`
- 全部顯示（ShowAll）
  - 清空 Isolate/Hidden 集合
  - 呼叫 `ReloadModel()`；最後 `ShowAll()` + `ViewHome()`

> 註：不同版本集合名稱可能為 `IsolateInstances`/`IsolatedInstances` 與 `HiddenInstances`/`HiddenEntities`，本專案已做反射容錯。

## 診斷日誌與疑難排解
- 啟動後會輸出 `viewer3d.log`（位於 Debug 輸出目錄）。
- 右鍵操作將新增詳細 Trace，例如：
  - `[StrongViewer] Isolate() called for entity label: 348711.`
  - `[StrongViewer] Isolate: IsolateInstances collection count before: 0. After: 1.`
  - `[StrongViewer] Invoking ReloadModel(ViewPreserveCameraPosition)...`
  - `[StrongViewer] Invoking ZoomSelected()...`
- 若 3D 無變化，請檢查：
  - 集合成員是否找到（log 會顯示 member not found）
  - 集合 count 是否有變化
  - 是否有成功呼叫 `ReloadModel(...)` 與 `ZoomSelected`/`ViewHome`

## 已知差異與相容性
- 不同 xBIM 版本的 API 與集合命名不同，本專案透過反射與多種 fallback 覆蓋：
  - ReloadModel 的 enum 解析（含巢狀型別 ModelRefreshOptions）
  - 無參與列舉版本互為備援
  - 額外的 Refresh/Redraw/ApplyFilters 鏈作最後保險

## 授權
- 參考 xBIM 專案授權規範。