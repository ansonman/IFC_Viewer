namespace IFC_Viewer_00.Services
{
    /// <summary>
    /// 選項：Pipe Axes / Pipe Axes + Terminals 生成時的可選功能。
    /// P4 (S1) 需求：加入 Fittings 節點，不影響既有 P3 入口。
    /// </summary>
    public class PipeAxesOptions
    {
        /// <summary>
        /// 是否加入 IfcPipeFitting 節點（以 Placement 位置投影為點）。
        /// 預設 false 以確保舊行為不變。
        /// </summary>
        public bool IncludeFittings { get; set; } = false;

        /// <summary>
        /// 是否加入 FlowTerminal（目前既有管線流程中始終加入；此處保留擴充彈性）。
        /// </summary>
        public bool IncludeTerminals { get; set; } = true;
    }
}
