````markdown
# 原理圖生成模組 - 開發與分析報告

報告日期: 2025-09-21（同步文件與功能現況）

## 1. MVP 與目前能力
- Node：Id/Name/IfcType/Position3D、`Entity` 參照
- Edge：Id/StartNode/EndNode、`Entity` 參照
- 來源：`IIfcDistributionElement` / `IIfcPipeSegment` 建立節點（以 `EntityLabel` 去重）
- 拓撲：優先依 `IfcRelConnectsPorts` 建立連線，Port 與節點以 `HasPorts` 映射
- 座標：取 LocalPlacement（`IfcAxis2Placement3D.Location`）之 3D 位置；初步投影至 2D
- 顯示：Canvas + ItemsControl 簡易呈現；依 IfcType 分色；力導向佈局（約 200 次迭代）
- 互動：點擊節點/邊 → 視窗層觸發 3D 高亮與 ZoomSelected（透過集合高亮 API）

## 2. 缺 Ports 之幾何鄰近 Fallback（< 10mm）
- 問題：部分 IFC 模型缺少 IfcRelConnectsPorts，導致拓撲無法串接
- 解法：
  - 對孤立節點執行最近鄰搜尋；若距離 < 10mm，新增推斷邊並標註 `IsInferred = true`
  - 目的：提升拓撲可視化完整度，方便初步分析

## 3. 多選同步與右側面板
  - 多選 → 顯示摘要（數量、類型分布等）
  - 單選 → 顯示完整屬性
  - 3D 多選高亮：當 2D 多選時，系統會將選取集合的 EntityLabel 轉換為 3D 控制項所需集合（`List<int>` 或 `List<IPersistEntity>`），並進行輕量更新以即時呈現。若 3D 控制項的 `Selection` 為非集合的 `EntitySelection`，則以其內部 API 設定；若控制版本不支援多選，退回 `SelectedEntity` 確保至少單選可見。

## 4. 範例節錄
```csharp
// Services/SchematicService.cs（節錄）
// 當 ports 邊為空，對孤立節點進行鄰近性推斷（< 10mm）
// Edges.Add(new SchematicEdge { StartNode = a, EndNode = b, IsInferred = true });
```

```csharp
// ViewModels/SchematicViewModel.cs（節錄）
// 力導向佈局（簡化版），完成後正規化座標範圍
ApplyForceDirectedLayout(Nodes.ToList(), Edges.ToList(), iterations: 200);
// 互動：點擊節點/邊 → RequestHighlight(entity, zoom:true)
```

## 5. 既知限制
- 力導向為快速近似：大圖可能需要更多迭代或使用層級式佈局（Sugiyama/MSAGL）
- 避線/重疊：可增補節點盒碰撞回退與曲線/折線路徑
- 邊對應 3D：目前以起點節點代表；後續可改以邊的 `Entity` 或兩端共同父件以更精準

## 6. 下一步
1) 佈局策略切換（力導向/層級式）與參數化（迭代數、理想距離）
2) 節點/邊樣式化（大小、圖示、文字裁切、濾鏡）
3) 3D ↔ 2D 雙向同步（3D 選取時在原理圖高亮）
4) 若 `SchematicEdge.Entity` 可映射至管段幾何，改以該實體做 Highlight
 
---

更新紀錄（2025-09-21）
- 將 3D 多選/單選回退邏輯與主視窗一致化：在控制項不支援多選時，以 `SelectedEntity` 確保單選可視，避免 2D→3D 同步後無高亮的斷鏈情況。
````
