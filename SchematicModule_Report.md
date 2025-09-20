# 原理圖生成模組 - 開發與分析報告

**報告日期**: 2025-09-20

## 1. 已完成的任務
- 任務 1：建立資料模型（Models）
  - 已新增 `SchematicNode.cs`、`SchematicEdge.cs`、`SchematicData.cs`
  - 資料結構：節點含 Id/Name/IfcType/Position2D/Children，邊含 Id/StartNode/EndNode

## 2. 遇到的挑戰與解決方案
- 挑戰：
  - 尚未（後續在任務 2 實作 `SchematicService.GenerateAsync` 時，若遇到 `IfcRelConnectsPorts` 多重連接與方向性判斷等情況再補充）
- 解決方案：
  - 尚未（預計：優先用 `IfcRelConnectsPorts`，必要時回退幾何鄰近性；以 async/await 包裝、加入最小快取以避免重複查詢）

## 3. 核心程式碼片段 (1-3 個)
- Models 節點/邊/封裝資料結構（節錄）：
```csharp
// app/IFC_Viewer_00/Models/SchematicNode.cs
using System.Collections.Generic;
using System.Windows;

namespace IFC_Viewer_00.Models
{
    public class SchematicNode
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string IfcType { get; set; }
        public Point Position2D { get; set; }
        public List<SchematicNode> Children { get; set; } = new List<SchematicNode>();
    }
}
```

```csharp
// app/IFC_Viewer_00/Models/SchematicEdge.cs
namespace IFC_Viewer_00.Models
{
    public class SchematicEdge
    {
        public string Id { get; set; }
        public SchematicNode StartNode { get; set; }
        public SchematicNode EndNode { get; set; }
    }
}
```

```csharp
// app/IFC_Viewer_00/Models/SchematicData.cs
using System.Collections.Generic;

namespace IFC_Viewer_00.Models
{
    public class SchematicData
    {
        public List<SchematicNode> Nodes { get; set; } = new List<SchematicNode>();
        public List<SchematicEdge> Edges { get; set; } = new List<SchematicEdge>();
    }
}
```

## 4. 執行結果與截圖
- 目前：尚未接上 UI 與服務（任務 2-4 進行中）。
- 預期：完成後按下「生成原理圖」按鈕，會彈出新視窗，顯示以 Canvas 繪製的節點（Ellipse）與連線（Edges 後續加入）。

## 5. 心得與疑問
- 心得：
  - 以簡單資料結構先交付 MVP，有助於快速驗證 `IfcRelConnectsPorts` 拓撲是否正確。
- 疑問與規劃：
  - 2D 佈局後續可能需要引入自動排版（例如 MSAGL）以避免節點重疊；待任務 2 完成後評估。

## 6. 下一步計畫
- 任務 2：建立 `Services/SchematicService.cs`，實作 `public async Task<SchematicData> GenerateAsync(IStepModel ifcModel)`
  - 遍歷：`IfcPipeSegment`、`IfcPipeFitting`、`IfcValve` 等
  - 核心：以 `IfcRelConnectsPorts` 建立拓撲；必要時回退到幾何鄰近性
  - 簡化：節點 Position2D 先取 3D XY 投影
- 任務 3：建立 `Views/SchematicView.xaml`（Canvas + ItemsControl）與 DataTemplate（Ellipse 綁 Position2D.X/Position2D.Y）
- 任務 4：建立 `ViewModels/SchematicViewModel.cs`（ObservableObject、Nodes/Edges、LoadSchematicAsync）與主視窗整合（按鈕、命令、彈窗顯示）
