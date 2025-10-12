using System.Windows.Media.Media3D;
using System.Windows;
using System.Collections.Generic;
using Xbim.Common;
using IXbimEntity = Xbim.Common.IPersistEntity;

namespace IFC_Viewer_00.Models
{
    public class SchematicNode
    {
        // 節點類型（快速版管網分類）
        public enum SchematicNodeKind { PipeEnd, Fitting, Valve, Terminal, Other }
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string IfcType { get; set; } = string.Empty;
        public Point3D Position3D { get; set; }
        public Point Position2D { get; set; }
        // SOP 2.0：採用 IXbimEntity（以別名方式對應 IPersistEntity）
        public IXbimEntity Entity { get; set; } = default!;
        // SOP 2.0：在節點上維持其相鄰邊清單
        public List<SchematicEdge> Edges { get; } = new List<SchematicEdge>();
        // 新增：節點分類
        public SchematicNodeKind NodeKind { get; set; } = SchematicNodeKind.Other;

        // 新增：宿主元素 IfcType（若 Node 代表的是 Port，可顯示其母元素類型）
        public string? HostIfcType { get; set; }
        // 新增：是否來自 PipeSegment（供顏色判斷）
        public bool IsFromPipeSegment { get; set; }
        // 新增：Port 的 EntityLabel（若是點陣列投影 V1 只有座標時也可填）
        public int? PortLabel { get; set; }
        // 新增：宿主元素 EntityLabel
        public int? HostLabel { get; set; }
        // Ports 快速資訊（暫不展開 Port 節點）
        public int? PortCount { get; set; }
        public bool HasPorts => (PortCount ?? 0) > 0;

        // --- Sprint 1: 資料擴充 ---
        // 系統/樓層分類（若不可得則為 null）
        public string? SystemName { get; set; }
        public string? LevelName { get; set; }
        // 系統縮寫與類型（可用於 UI 圖例與篩選）
        public string? SystemAbbreviation { get; set; }
        public string? SystemType { get; set; }
        // 統一系統 key（優先 Abbreviation > Name > 未指定）
        public string? SystemKey { get; set; }
        // Run 分組識別（Connected Component ID）
        public int? RunId { get; set; }
        // Segment 拆端點用旗標
        public bool IsSegmentCenter { get; set; } = false; // 初始建立 FlowSegment 時標記
        public bool IsSegmentEndpoint { get; set; } = false; // 拆分後的端點

        // 離線重接線輔助：此節點是否可視為可被穿越的配件（只對 Fitting 類型有效）
        public bool IsFittingCollapsible { get; set; } = false;
    }
}
