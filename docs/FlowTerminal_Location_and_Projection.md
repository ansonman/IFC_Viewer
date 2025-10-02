# IfcFlowTerminal 資料定位與 2D 投影指引

本文說明如何在本專案中為 IfcFlowTerminal 取得穩健的 3D 代表點，並投影到 2D Canvas（例如用紅點標示）。

## 定位資料來源的優先序

為了取得最貼近語意、且在不同來源模型上穩定的定位點，建議以下優先序：

1) DistributionPort 幾何（最推薦）
- 若 FlowTerminal 具備 IfcDistributionPort，優先以 Port 的位置做為定位點。
- 若存在多個 Port，可依 FlowDirection（SOURCE / SINK）選主 Port；若沒有偏好，取第一個即可。
- 座標來源可用：
  - Port 幾何中心（若可得）
  - 或 Port 的 LocalPlacement 原點轉到世界座標

2) 產品幾何的重心/包圍盒中心（次佳）
- 使用 xBIM 幾何建立該元素的形狀，讀取 BoundingBox 中心點做為代表點。
- 優點：在沒有 Ports 的模型中仍可運作。

3) 產品的 LocalPlacement 原點（最後備援）
- 沿著 IfcLocalPlacement 的 PlacementRelTo 鏈結，一路轉換到世界座標，取其平移量即為定位點。
- 優點：即使無幾何與無 Port，仍能取得一個穩定的代表位置。

## 與現有 Schematic 模組整合

專案中 `SchematicService` 已有處理 Ports 與 3D→2D 投影的流程：
- `GetAllPortCoordinatesAsync`：示範如何從系統內蒐集 DistributionPort 的 3D 座標
- `LoadPointsFrom3DAsync`（`SchematicViewModel`）：將 3D 點投影到指定平面（XY/XZ/YZ），同時完成縮放與邊界對齊，並處理退化（所有點共線等）

建議重用上述 API 以快速完成定位與投影，確保與現有原理圖座標系統一致。

## 3D → 2D 投影流程（概要）

- 選擇投影平面：XY、XZ 或 YZ；本專案常用 XY，並翻轉 Y 軸至 Canvas 座標（左上為原點）。
- 收集 N 個 3D 代表點（Point3D，單位 mm）。
- 使用 `LoadPointsFrom3DAsync(points3D, plane, flipX, flipY, tryBestIfDegenerate)` 完成：
  - 依平面擷取 2 軸
  - 計算 min/max，映射到 CanvasWidth/CanvasHeight（含 Padding）
  - 處理退化（例如全部點同一條線）

## 代表點取得：範例（概念示意）

```csharp
// 偏好：Port → (BBox 中心) → LocalPlacement 原點
Point3D? GetFlowTerminalAnchor3D(IIfcFlowTerminal term, IfcStore model)
{
    // 1) DistributionPort：選主 Port
    var ports = SchematicPortsHelper.TryGetPorts(term); // 專案可複用既有取 Port 方法
    var mainPort = ports.FirstOrDefault(p => p.FlowDirection == IfcFlowDirectionEnum.SOURCE)
                 ?? ports.FirstOrDefault(p => p.FlowDirection == IfcFlowDirectionEnum.SINK)
                 ?? ports.FirstOrDefault();
    if (mainPort != null)
    {
        var p = SchematicPortsHelper.GetPortWorldPoint(mainPort); // 世界座標
        if (p != null) return new Point3D(p.X, p.Y, p.Z);
    }

    // 2) 幾何包圍盒中心
    var ctx = new Xbim3DModelContext(model);
    ctx.CreateContext();
    var bb = XbimBBox3D.Empty;
    foreach (var rep in ctx.ShapeGeometry(term))
        bb = bb.Union(rep.BoundingBox());
    if (!bb.IsEmpty)
        return new Point3D((bb.XMin + bb.XMax)/2.0, (bb.YMin + bb.YMax)/2.0, (bb.ZMin + bb.ZMax)/2.0);

    // 3) LocalPlacement 原點（世界座標）
    var origin = PlacementToWorld(term.ObjectPlacement);
    return origin;
}
```

> 備註：`PlacementToWorld` 需將 IfcAxis2Placement3D 轉為矩陣，並沿著 `PlacementRelTo` 鏈乘至世界座標；xBIM 6.x 提供相關 API（命名空間依版本略異），可參考現有程式碼。

## 在 Canvas 畫紅點（兩種做法）

A) 融入 SchematicView（推薦）
- 將 FlowTerminal 代表點加入 `LoadPointsFrom3DAsync` 的輸入，該方法會回傳對應的 2D 座標集合（或由 ViewModel 暴露可繪製的集合）。
- 在 `SchematicView` 的 Canvas 綁定這些 2D 點，使用紅色樣式繪製。

B) 獨立繪製（簡單示範）

```csharp
var dot = new Ellipse
{
    Width = 6, Height = 6,
    Fill = Brushes.Red,
    Stroke = Brushes.White,
    StrokeThickness = 1
};
Canvas.SetLeft(dot, x - 3);
Canvas.SetTop(dot, y - 3);
myCanvas.Children.Add(dot);
```

## 邊界情況與建議

- 單位：xBIM 幾何多為 mm，投影與縮放時保持一致單位。
- 多 Port：
  - 依 FlowDirection（SOURCE/SINK）挑主 Port；或取全部 Port 平均為代表點。
- 無幾何/無 Port：以 LocalPlacement 原點為備援。
- 多點同時顯示：一次收集所有點再投影，確保在同一套縮放與邊界下渲染。
- Flip Y：Canvas 座標系通常 Y 向下，常見做法是 flipY=true。

## 延伸

- 可將此流程包裝為 `SchematicService.GetFlowTerminalAnchorsAsync(model)`，再由 ViewModel/視圖統一繪製。
- 若要與 3D 高亮同步，可保存 FlowTerminal 與 2D 點的對應，點擊紅點時反向查回 3D 元件 Label 作互動。
