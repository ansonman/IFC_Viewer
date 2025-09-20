# 原理圖生成模組 - 開發與分析報告

報告日期: 2025-09-20

## 1. 已完成的任務（MVP）
- Models：`SchematicNode.cs`、`SchematicEdge.cs`、`SchematicData.cs`
  - Node：Id/Name/IfcType/Position3D、`Entity` 參照。
  - Edge：Id/StartNode/EndNode、`Entity` 參照。
- Service：`SchematicService.GenerateTopologyAsync(IModel)`
  - 來源：`IIfcDistributionElement` 與 `IIfcPipeSegment` 建立節點；以 `EntityLabel` 去重。
  - 拓撲：依 `IfcRelConnectsPorts` 的 `RelatingPort`/`RelatedPort` 建立邊；Port 與節點以 `HasPorts` 映射。
  - 座標：從 LocalPlacement（`IfcAxis2Placement3D.Location`）取得 3D 位置。
- View + ViewModel：`SchematicView.xaml` + `SchematicViewModel` + 工具列「生成原理圖」
  - 顯示：Canvas + ItemsControl 以簡單樣式呈現 Nodes/Edges。
  - 分色：依 IfcType 給予不同色塊，邊線採深色階。
  - 佈局：在初始 XY 投影基礎上執行力導向迭代自動佈局（200 次）。
  - 互動：點擊節點或邊，透過 `RequestHighlight` 事件由視窗層呼叫 3D 服務高亮與 ZoomSelected。

## 2. 遇到的挑戰與解法
- xBIM v6 型別差異：以 `IModel`、`IPersistEntity` 取代早期類型；取值以 `ToString()` 與 `EntityLabel` 處理，避免 CS0246。
- 節點重複：元素可能同時符合多種型別，導致重複加入；以 `EntityLabel` 去重後解決。
- ViewModel 映射鍵碰撞：以實體參照為 map key，必要時落回組合鍵（`label:Id`）避免重複鍵例外。

## 3. 代表性程式片段
```csharp
// Services/SchematicService.cs（節錄）：
var distElems = ifcModel.Instances.OfType<IIfcDistributionElement>().ToList();
// ... CreateNodeFromElement(elem) 取 LocalPlacement 的 XYZ
var rels = ifcModel.Instances.OfType<IIfcRelConnectsPorts>().ToList();
// RelatingPort/RelatedPort → 透過 portToNode 對回 start/end 節點
```

```csharp
// ViewModels/SchematicViewModel.cs（節錄）：
// 佈局：Fruchterman-Reingold 簡化版，200 次迭代，最後正規化座標
ApplyForceDirectedLayout(Nodes.ToList(), Edges.ToList(), iterations: 200);
// 互動：點擊節點/邊 → RequestHighlight(entity, zoom:true)
```

## 4. 執行結果（MVP）
- 原理圖視窗可顯示節點與邊，分色與自動佈局生效。
- 點擊節點/邊能觸發 3D 高亮與縮放（ZoomSelected）。
- [Screenshot Placeholder 1]
- [Screenshot Placeholder 2]

## 5. 既知限制與後續建議
- 力導向為快速近似：大型網路可能需要更多迭代或替換為分層佈局；可引入 MSAGL 或 Sugiyama 風格的層級式佈局（以 Port 流向為層級）。
- 避線/重疊仍可能出現：可加入節點盒碰撞回退與邊路徑彎折。
- 邊對應的 3D 同步：目前採用起點節點代表；可改用邊的 `Entity` 或兩端共同父件以更精準。

## 6. 下一步計畫
1) 佈局策略選擇器（力導向/層級式），支援按鈕切換與參數調整（迭代數、理想距離 k）。
2) 節點/邊外觀樣式化（大小、圖示、文字裁切、工具列濾鏡）。
3) 3D ↔ 2D 雙向同步（在 3D 選取時自動在原理圖高亮對應節點）。
4) 邊對應 3D：若 `SchematicEdge.Entity` 可對應到管段幾何，改為該實體做 Highlight。
