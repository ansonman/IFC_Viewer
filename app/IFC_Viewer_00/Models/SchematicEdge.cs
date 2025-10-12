using Xbim.Common;
using IXbimEntity = Xbim.Common.IPersistEntity;

namespace IFC_Viewer_00.Models
{
    public class SchematicEdge
    {
    public enum EdgeOriginKind { Ports, Geometry, Segment, Rewired, RewiredViaFittingHub }
        public string Id { get; set; } = string.Empty;
        public string StartNodeId { get; set; } = string.Empty;
        public string EndNodeId { get; set; } = string.Empty;
        public SchematicNode StartNode { get; set; } = default!;
        public SchematicNode EndNode { get; set; } = default!;
        // 舊字段：Entity（保留以相容既有程式）
        public IXbimEntity Entity { get; set; } = default!;
        // SOP 2.0：Connection 指向 IfcRelConnectsPorts 實體（或相容的連線實體）
        public IXbimEntity Connection { get; set; } = default!;
        // 由幾何鄰近性推斷出的邊（非 IfcRelConnectsPorts）
        public bool IsInferred { get; set; } = false;
    public EdgeOriginKind Origin { get; set; } = EdgeOriginKind.Ports;

        // --- Sprint 1: 資料擴充 ---
        // 系統/樓層分類（若不可得則為 null）
        public string? SystemName { get; set; }
        public string? LevelName { get; set; }

        // 走向（水平/垂直/傾斜）
        public PipeOrientation Orientation { get; set; } = PipeOrientation.Sloped;

        // 主路管標記
        public bool IsMainPipe { get; set; } = false;

        // 管徑資訊（mm），可為 null（來源不足時）
        public double? NominalDiameterMm { get; set; }
        public double? OuterDiameterMm { get; set; }
        public string? ValueSourceNominalDiameter { get; set; }
        public string? ValueSourceOuterDiameter { get; set; }

        // 系統縮寫與類型（例如 CWS / CHW 等；Type 可來自 IfcDistributionSystemEnum 或名稱推導）
        public string? SystemAbbreviation { get; set; }
        public string? SystemType { get; set; }

        // Run 分組識別（依系統 + 尺寸 + 幾何相連/同節點相連而分群）
        public int? RunId { get; set; }

        // 新增：長度 (mm)（若可計算）
        public double? LengthMm { get; set; }

        // 離線重接線輔助：若此邊是由哪些 Fitting 透接而成，紀錄其 HostLabel（或 0）
        public int[]? RewiredViaFittingLabels { get; set; }
        // 離線重接線輔助：用於追蹤來源（例如 "Ports", "Segment", "Geometry", "ThroughFitting"）
        public string? SourceTag { get; set; }
        // 配件為中心模式：邊歸屬的 Fitting 標識（如可取得）
        public int? FittingId { get; set; }
    }
}
