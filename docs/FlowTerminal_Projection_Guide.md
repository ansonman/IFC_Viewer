# IfcFlowTerminal 定位與投影到 2D Canvas 指南

本文說明在本專案中，如何將 IfcFlowTerminal 定位於 3D 空間並投影為 2D 畫布上的標記（紅點）。內容包含資料來源優先序、3D→2D 投影策略、與現有 Schematic 模組的整合方式，以及常見邊界情況。

## 一、定位資料來源優先序

建議依下列順序取得 FlowTerminal 的代表點（世界座標，單位通常為 mm）：

1. DistributionPort（最穩定）
   - 若終端件帶有 `IfcDistributionPort`，選擇主要流向的 Port（例如 `FlowDirection = SOURCE` 或 `SINK`）。
   - 以該 Port 的幾何中心或其 `LocalPlacement` 原點作為代表點。
   - 本專案已有 Port 擷取流程，可複用 `SchematicService` 中的 Port 抽取邏輯（AS V1 模組用於生成 Port 座標）。

2. 幾何包圍盒中心（次佳）
   - 使用 xBIM 幾何（`Xbim.ModelGeometry.Scene`）建立形狀，取其 BoundingBox 中心為代表點。

3. LocalPlacement 原點（備援）
   - 透過 `IfcLocalPlacement` 沿 `PlacementRelTo` 向上累積，轉換為世界座標矩陣後取平移向量。

> 實務上，1 已足夠涵蓋多數需求，且語意清楚。2 與 3 作為可靠後援。

## 二、3D → 2D 投影策略

- 投影平面：依場景採用 `XY`/`XZ`/`YZ` 之一；本專案常用 `XY`。
- 座標映射：選擇兩軸作為 2D（如 XY），其餘忽略。
- 畫布映射：將點集在投影平面的 min/max 範圍線性映射到 CanvasWidth/CanvasHeight，並預留 Padding；必要時 `flipY=true` 以符合畫布座標系。
- 本專案可直接使用 `SchematicViewModel.LoadPointsFrom3DAsync(points3D, plane, flipX, flipY, tryBestIfDegenerate)` 完成投影與退化處理。

## 三、與現有 Schematic 模組整合

1. 取得 FlowTerminal 的 3D 代表點集合。
2. 呼叫 `LoadPointsFrom3DAsync`，將 3D 座標投影並縮放到畫布座標。
3. 在 `SchematicView` 所用的 Canvas 上繪製紅點（或擴充 ViewModel 以資料驅動方式繪製）。

### 範例（概念碼）

```csharp
Point3D? GetFlowTerminalAnchor3D(IIfcFlowTerminal term, IfcStore model)
{
    // 1) Port
    var ports = PortsHelper.TryGetPorts(term);
    var mainPort = ports.FirstOrDefault(p => p.FlowDirection == IfcFlowDirectionEnum.SOURCE)
                ?? ports.FirstOrDefault(p => p.FlowDirection == IfcFlowDirectionEnum.SINK)
                ?? ports.FirstOrDefault();
    if (mainPort != null)
    {
        var p = PortsHelper.GetPortWorldPoint(mainPort);
        if (p != null) return new Point3D(p.X, p.Y, p.Z);
    }

    // 2) BBox center
    var ctx = new Xbim3DModelContext(model);
    ctx.CreateContext();
    var bb = XbimBBox3D.Empty;
    foreach (var rep in ctx.ShapeGeometry(term))
        bb = bb.Union(rep.BoundingBox());
    if (!bb.IsEmpty)
        return new Point3D((bb.XMin + bb.XMax)/2.0, (bb.YMin + bb.YMax)/2.0, (bb.ZMin + bb.ZMax)/2.0);

    // 3) LocalPlacement origin → world
    return PlacementToWorld(term.ObjectPlacement);
}
```

> 註：`PlacementToWorld` 需將 `IfcAxis2Placement3D` 轉 `Matrix3D` 並沿 `PlacementRelTo` 累積。xBIM 6.x 有相關 API；實作細節可依現有專案工具補齊。

## 四、紅點繪製（WPF）

- 使用 `Ellipse` 作為視覺化紅點（居中擺放）。
- 建議樣式：Fill=Red、Stroke=White、StrokeThickness=1，直徑 6~8px。

```csharp
var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Red, Stroke = Brushes.White, StrokeThickness = 1 };
Canvas.SetLeft(dot, x - 3);
Canvas.SetTop(dot, y - 3);
canvas.Children.Add(dot);
```

> 若以 MVVM 方式實作，則維護一個 `ObservableCollection<Point2D>` 與 DataTemplate 綁定樣式，避免直寫 UI。

## 五、邊界情況與注意事項

- 單位：xBIM 幾何多為 mm；與 2D 比例需要一致縮放。
- 多 Port：可依 `FlowDirection` 選擇 SOURCE/SINK；或取所有 Port 的平均中心。
- 無幾何/無定位：以 LocalPlacement 原點投影仍可工作。
- 大量點渲染：
  - 啟用 UI 虛擬化（或以 DrawingVisual/WriteableBitmap 畫點提效）。
  - 合併繪製（例如一個 PathGeometry 承載多點）。

## 六、延伸：一次處理多個終端

- 集合所有 FlowTerminal 的 3D 代表點，統一呼叫 `LoadPointsFrom3DAsync`，可保證同一個縮放與邊界。
- 可在 meta 隨附關聯資訊（Label、Name、SystemName），便於點擊回推 3D（反查 Label）。

---

如需我直接把上述流程包成 `SchematicService.GetFlowTerminalAnchorsAsync()` 與一個 UI 命令（例如「顯示 FlowTerminal 紅點」），可以再開一個任務我來完成。