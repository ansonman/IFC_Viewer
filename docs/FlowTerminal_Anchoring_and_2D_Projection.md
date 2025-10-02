# IfcFlowTerminal 定位與 2D 投影指引

本文說明如何在本專案中，將 IfcFlowTerminal 的 3D 資料定位為「代表點」，並投影到 2D Canvas 顯示（例如以紅點標記），同時說明與既有原理圖模組如何整合。

---

## 定位資料的優先序

為了得到穩定、語意合理的定位點（anchor），建議按以下優先序取得 3D 代表點：

1. DistributionPort 幾何（最佳）
   - 若 FlowTerminal 具備 `IfcDistributionPort`，以「主要流向」的 Port 做代表。
   - 建議優先挑 `FlowDirection = SOURCE` 或 `SINK` 的 Port；若皆無則取第一個。
   - 代表點可用：
     - 該 Port 幾何中心（若能從幾何或拓樸取得），或
     - `Port.ObjectPlacement` 的世界座標原點。

2. 產品幾何的包圍盒中心（次佳）
   - 使用 xBIM 幾何（`Xbim.ModelGeometry.Scene`）建立形狀後，採用 BoundingBox 中心作為代表點。

3. LocalPlacement 原點（備援）
   - 透過 `IfcLocalPlacement` 及其 `PlacementRelTo` 鏈結，換算到世界座標系，取原點作為代表點。

> 實務上 1) 就能涵蓋多數情境，且與流向語意最貼近。

---

## 3D → 2D 投影

- 選擇投影平面：`XY`、`XZ`、`YZ`（專案中常用 `XY`）。
- 取兩軸為 2D 坐標（如 `XY` ⇒ `(X, Y)`）。
- 正規化與縮放到 Canvas：
  - 先計算該批點的 min/max 範圍。
  - 依 `CanvasWidth/CanvasHeight/CanvasPadding` 將 3D 坐標線性映射至像素位置。
  - 若畫布座標以左上為 (0,0)，可 `flipY = true` 讓 Y 軸向下。
- 本專案已有 API：`SchematicViewModel.LoadPointsFrom3DAsync(points, plane, flipX, flipY, tryBestIfDegenerate)` 可一次完成投影與退化平面處理，建議直接使用。

---

## 與現有模組整合

- 原理圖模組（`SchematicViewModel`）已具備投影與縮放能力，可直接將 FlowTerminal 的 3D 代表點（多筆）餵入。
- 若僅需在 3D 檢視上疊加顯示，則可改用 `IViewer3DService.ShowOverlayPipeAxes` 的點集合功能，只畫端點（顏色設紅色），避免 2D Canvas。

---

## 參考實作（概念碼）

```csharp
// 1) 取得 FlowTerminal 的 3D 代表點（mm）
Point3D? GetFlowTerminalAnchor3D(IIfcFlowTerminal term, IfcStore model)
{
    // A. DistributionPort（優先）
    var ports = TryGetPorts(term); // 依專案工具或自寫
    var main = ports.FirstOrDefault(p => p.FlowDirection == IfcFlowDirectionEnum.SOURCE)
            ?? ports.FirstOrDefault(p => p.FlowDirection == IfcFlowDirectionEnum.SINK)
            ?? ports.FirstOrDefault();
    if (main != null)
    {
        var p = GetPortWorldPoint(main); // 取世界座標
        if (p != null) return new Point3D(p.X, p.Y, p.Z);
    }

    // B. 幾何包圍盒中心
    var ctx = new Xbim3DModelContext(model); ctx.CreateContext();
    var bb = XbimBBox3D.Empty;
    foreach (var rep in ctx.ShapeGeometry(term))
        bb = bb.Union(rep.BoundingBox());
    if (!bb.IsEmpty)
        return new Point3D((bb.XMin+bb.XMax)/2.0,(bb.YMin+bb.YMax)/2.0,(bb.ZMin+bb.ZMax)/2.0);

    // C. LocalPlacement 原點（世界座標）
    return PlacementToWorld(term.ObjectPlacement);
}
```

```csharp
// 2) 投影到 2D（利用既有 API）
var points3D = new List<Point3D>();
var pt = GetFlowTerminalAnchor3D(flowTerminal, model);
if (pt != null) points3D.Add(pt.Value);
await svm.LoadPointsFrom3DAsync(points3D, plane: "XY", metaList: null, flipX: false, flipY: true, tryBestIfDegenerate: true);
```

```csharp
// 3) 在 Canvas 畫紅點（若需手動繪製）
var size = 6.0;
var dot = new Ellipse { Width = size, Height = size, Fill = Brushes.Red, Stroke = Brushes.White, StrokeThickness = 1 };
Canvas.SetLeft(dot, mappedX - size/2);
Canvas.SetTop(dot, mappedY - size/2);
myCanvas.Children.Add(dot);
```

---

## 邊界與建議

- 單位：xBIM 幾何通常為 mm，Canvas 請以同單位基準進行映射。
- 多 Port 的終端：可選主流向 Port 或取所有 Port 的平均點。
- 無幾何/無位置：至少能用 LocalPlacement；仍可被投影。
- 一次處理多顆終端：先收集所有 3D 點再投影，能得到一致的縮放與畫布分布。
- 性能：若一次處理大量終端，建議以批次投影並啟用虛擬化/分頁呈現。

---

## 後續擴充

- 將上述流程包裝成 `SchematicService.GetFlowTerminalAnchorsAsync(IfcStore model)`，并提供不同投影平面與樣式配置。
- 在 UI 加入「顯示 FlowTerminal 紅點」命令，與 3D/2D 檢視聯動。
