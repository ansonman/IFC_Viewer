using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using Xbim.Common;
using IFC_Viewer_00.Models;
using IFC_Viewer_00.Services;
using Xbim.Ifc4.Interfaces;

namespace IFC_Viewer_00.ViewModels
{
    public partial class SchematicViewModel : INotifyPropertyChanged
    {
        // 新增：供快速管網建構使用的目前模型引用
    public Xbim.Common.IModel? CurrentModel { get; set; }
        // 新增：緩存目前畫面所對應的 SchematicData（可作為離線種子）
        private SchematicData? _currentData;

        // 簡化：若原本另一 partial 已實作，請整合；此處提供 fallback 以免編譯錯誤
        private void LoadSchematicData(SchematicData data)
        {
            // 若已有正式實作，刪除此暫存；否則最少更新集合供 UI 繫結
            Nodes.Clear();
            foreach (var n in data.Nodes) Nodes.Add(new SchematicNodeView { Node = n });
            Edges.Clear();
            foreach (var e in data.Edges) Edges.Add(new SchematicEdgeView { Edge = e });
        }
        private readonly SchematicService _service;

        public ObservableCollection<SchematicNodeView> Nodes { get; } = new();
    public ObservableCollection<SchematicEdgeView> Edges { get; } = new();
    public ObservableCollection<LevelLineView> LevelLines { get; } = new();
    // P3: 系統過濾選項
    public ObservableCollection<SystemFilterOption> Systems { get; } = new();
        // V1: 日誌文字顯示
        public ObservableCollection<string> Logs { get; } = new();
        // 2D 後處理參數（畫布像素）
        private bool _hideTinyStubs = true;
        public bool HideTinyStubs
        {
            get => _hideTinyStubs;
            set { if (_hideTinyStubs != value) { _hideTinyStubs = value; OnPropertyChanged(nameof(HideTinyStubs)); SaveColorsToDisk(); } }
        }

        private double _minSegmentScreenLengthPx = 4.0; // 小於此長度視為短邊段
        public double MinSegmentScreenLengthPx
        {
            get => _minSegmentScreenLengthPx;
            set
            {
                double v = double.IsNaN(value) || double.IsInfinity(value) ? _minSegmentScreenLengthPx : Math.Clamp(value, 0.0, 100.0);
                if (Math.Abs(_minSegmentScreenLengthPx - v) > double.Epsilon)
                { _minSegmentScreenLengthPx = v; OnPropertyChanged(nameof(MinSegmentScreenLengthPx)); SaveColorsToDisk(); }
            }
        }

        private double _nodeMergeScreenDistancePx = 6.0; // 兩節點距離小於此值時合併座標
        public double NodeMergeScreenDistancePx
        {
            get => _nodeMergeScreenDistancePx;
            set
            {
                double v = double.IsNaN(value) || double.IsInfinity(value) ? _nodeMergeScreenDistancePx : Math.Clamp(value, 0.0, 100.0);
                if (Math.Abs(_nodeMergeScreenDistancePx - v) > double.Epsilon)
                { _nodeMergeScreenDistancePx = v; OnPropertyChanged(nameof(NodeMergeScreenDistancePx)); SaveColorsToDisk(); }
            }
        }

        private double _endpointSnapDistancePx = 5.0; // 邊端點與鄰近節點距離小於此值時吸附
        public double EndpointSnapDistancePx
        {
            get => _endpointSnapDistancePx;
            set
            {
                double v = double.IsNaN(value) || double.IsInfinity(value) ? _endpointSnapDistancePx : Math.Clamp(value, 0.0, 100.0);
                if (Math.Abs(_endpointSnapDistancePx - v) > double.Epsilon)
                { _endpointSnapDistancePx = v; OnPropertyChanged(nameof(EndpointSnapDistancePx)); SaveColorsToDisk(); }
            }
        }
        // 右側屬性面板：選取與屬性
        public ObservableCollection<PropertyEntry> Properties { get; } = new();
        public string SelectedTitle { get; private set; } = "(未選取)";
    private string _lastGraphReportJson = string.Empty;
    public string LastGraphReportJson { get => _lastGraphReportJson; private set { if (_lastGraphReportJson != value) { _lastGraphReportJson = value; OnPropertyChanged(nameof(LastGraphReportJson)); } } }
        // UI 切換：穿越配件連線（代理至 Service）
        private bool _throughFittingRewireEnabled;
        public bool ThroughFittingRewireEnabled
        {
            get => _throughFittingRewireEnabled;
            set
            {
                if (_throughFittingRewireEnabled != value)
                {
                    _throughFittingRewireEnabled = value;
                    // 同步到 Service
                    try { _service.ThroughFittingRewireEnabled = value; } catch { }
                    OnPropertyChanged(nameof(ThroughFittingRewireEnabled));
                    AddLog($"[Net] 穿越配件連線: {(value ? "On" : "Off")}");
                    // 若已載入模型，立即重建一次以反映更動
                    if (CurrentModel != null)
                    {
                        try { BuildFittingNetworkCommand.Execute(null); }
                        catch { }
                    }
                }
            }
        }

        public void AddLog(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            if (Logs.Count > 500) Logs.Clear();
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }

        public double Scale { get; set; } = 0.001; // 粗略縮放，將毫米→公尺（視模型而定）
    // Canvas 尺寸與邊距（可由 View 綁定）
    public double CanvasWidth { get; set; } = 1600;
    public double CanvasHeight { get; set; } = 1000;
    public double CanvasPadding { get; set; } = 40;

    // 點擊互動：由 Window/Owner 註冊，轉送到 3D 服務
        public event Action<IPersistEntity, bool>? RequestHighlight; // bool: 是否要求縮放
    private readonly ISelectionService? _selection;

    public ICommand NodeClickCommand { get; }
    public ICommand EdgeClickCommand { get; }
    // 視覺調整命令
    public ICommand IncreaseNodeSizeCommand { get; }
    public ICommand DecreaseNodeSizeCommand { get; }
    public ICommand IncreaseLineWidthCommand { get; }
    public ICommand DecreaseLineWidthCommand { get; }
    public ICommand ToggleSnapCommand { get; }
    public ICommand ToggleLevelsCommand { get; }
    public ICommand ComputeRunsCommand { get; }
    public ICommand ExportRunsToCsvCommand { get; }
    public ICommand DumpRewiredEdgesCommand { get; }
    // Phase 0: 顏色切換命令
    public ICommand CycleTerminalColorCommand { get; }
    public ICommand CyclePipeColorCommand { get; }
    public ICommand CyclePipeNodeColorCommand { get; }
    public ICommand CyclePipeEdgeColorCommand { get; }
    // 新增：配件為中心建構命令（配件線）
    public ICommand BuildFittingNetworkCommand { get; }

    // 視覺預設
    private double _defaultNodeSize = 8.0; // px
    private double _defaultEdgeThickness = 2.0; // px
    public bool SnapEnabled { get; private set; } = true;
    public bool LevelsVisible { get; private set; } = true;
    // 顯示管徑標籤（置於邊的中點）
    private bool _showPipeSizeTags = true;
    public bool ShowPipeSizeTags { get => _showPipeSizeTags; set { if (_showPipeSizeTags != value) { _showPipeSizeTags = value; OnPropertyChanged(nameof(ShowPipeSizeTags)); } } }

    // 手動控制：管徑標籤縮放倍率（不再自動依縮放調整）
    private double _tagScale = 1.0; // 1.0 = 原始大小
    public double TagScale
    {
        get => _tagScale;
        set
        {
            double v = double.IsNaN(value) || double.IsInfinity(value) ? _tagScale : Math.Clamp(value, 0.1, 5.0);
            if (Math.Abs(_tagScale - v) > double.Epsilon)
            {
                _tagScale = v; OnPropertyChanged(nameof(TagScale));
            }
        }
    }

    // 新增：短邊自動隱藏管徑標籤設定與閾值（像素）
    private bool _autoHideShortPipeSizeLabels = false;
    public bool AutoHideShortPipeSizeLabels
    {
        get => _autoHideShortPipeSizeLabels;
        set
        {
            if (_autoHideShortPipeSizeLabels != value)
            {
                _autoHideShortPipeSizeLabels = value;
                OnPropertyChanged(nameof(AutoHideShortPipeSizeLabels));
                // 變更即保存設定
                SaveColorsToDisk();
            }
        }
    }

    private double _shortPipeLabelMinLengthPx = 40.0;
    public double ShortPipeLabelMinLengthPx
    {
        get => _shortPipeLabelMinLengthPx;
        set
        {
            double v = double.IsNaN(value) || double.IsInfinity(value) ? _shortPipeLabelMinLengthPx : Math.Clamp(value, 0.0, 500.0);
            if (Math.Abs(_shortPipeLabelMinLengthPx - v) > double.Epsilon)
            {
                _shortPipeLabelMinLengthPx = v;
                OnPropertyChanged(nameof(ShortPipeLabelMinLengthPx));
                // 變更即保存設定
                SaveColorsToDisk();
            }
        }
    }

    // Phase 2: 圖層切換（預設全開）
    private bool _showTerminals = true;
    public bool ShowTerminals
    {
        get => _showTerminals;
        set { if (_showTerminals != value) { _showTerminals = value; OnPropertyChanged(nameof(ShowTerminals)); } }
    }
    private bool _showPipes = true;
    public bool ShowPipes
    {
        get => _showPipes;
        set { if (_showPipes != value) { _showPipes = value; OnPropertyChanged(nameof(ShowPipes)); } }
    }
    private bool _showLabels = true;
    public bool ShowLabels
    {
        get => _showLabels;
        set { if (_showLabels != value) { _showLabels = value; OnPropertyChanged(nameof(ShowLabels)); } }
    }

    // 顯示 Rewired 端點診斷標記（僅視覺輔助）
    private bool _showRewiredEndpoints = false;
    public bool ShowRewiredEndpoints
    {
        get => _showRewiredEndpoints;
        set
        {
            if (_showRewiredEndpoints != value)
            {
                _showRewiredEndpoints = value;
                OnPropertyChanged(nameof(ShowRewiredEndpoints));
                // 友善提示：若只開端點而關閉「管線」，會看到端點但看不到線條
                if (_showRewiredEndpoints && !ShowPipes)
                {
                    AddLog("[Rewired] 端點標記 On，但『管線』層目前關閉，線條不會顯示（請勾選『管線』層）");
                }
            }
        }
    }

    // 只顯示 Rewired（僅顯示虛線及其端點）
    private bool _showOnlyRewired = false;
    public bool ShowOnlyRewired
    {
        get => _showOnlyRewired;
        set
        {
            if (_showOnlyRewired != value)
            {
                _showOnlyRewired = value;
                OnPropertyChanged(nameof(ShowOnlyRewired));
                AddLog($"[View] 只顯示 Rewired: {(value ? "On" : "Off")}");
                ApplySystemVisibility();
                // 額外：切換後即時輸出 Rewired 摘要，協助診斷「沒有任何輸出」情況
                try
                {
                    var all = Edges.Where(e => e.Edge?.Origin == Models.SchematicEdge.EdgeOriginKind.Rewired).ToList();
                    var vis = Edges.Where(e => e.Edge?.Origin == Models.SchematicEdge.EdgeOriginKind.Rewired && e.Visible).ToList();
                    AddLog($"[Rewired] 全部={all.Count}, 目前可見={vis.Count}");
                    if (all.Count == 0)
                    {
                        AddLog("[Rewired] 目前沒有任何 Rewired 邊。若剛勾選『穿越配件連線』，請先執行『管網建構(Quick)』或重新載入模型。");
                    }
                }
                catch { }
            }
        }
    }

    // 顯示/隱藏所有點（Nodes）
    private bool _showAllNodes = true;
    public bool ShowAllNodes
    {
        get => _showAllNodes;
        set { if (_showAllNodes != value) { _showAllNodes = value; OnPropertyChanged(nameof(ShowAllNodes)); } }
    }

    // 合併視角模式：A=跨樓層合併；B=按樓層分組
    public enum MergeViewMode { A_CrossLevels, B_PerFloor }
    private MergeViewMode _mergeMode = MergeViewMode.A_CrossLevels;
    public MergeViewMode Mode
    {
        get => _mergeMode;
        set
        {
            if (_mergeMode != value)
            {
                _mergeMode = value;
                OnPropertyChanged(nameof(Mode));
                OnPropertyChanged(nameof(MergeAcrossLevels));
                SaveColorsToDisk();
            }
        }
    }
    // 便捷：用 CheckBox 切換 A/B
    public bool MergeAcrossLevels
    {
        get => Mode == MergeViewMode.A_CrossLevels;
        set { Mode = value ? MergeViewMode.A_CrossLevels : MergeViewMode.B_PerFloor; OnPropertyChanged(nameof(MergeAcrossLevels)); }
    }

    // 幾何容差（像素；用於 2D 節點鄰近合併）預設為 0=僅關係連接
    private double _geometryTolerancePx = 0.0;
    public double GeometryTolerancePx
    {
        get => _geometryTolerancePx;
        set
        {
            double v = double.IsNaN(value) || double.IsInfinity(value) ? _geometryTolerancePx : Math.Clamp(value, 0.0, 500.0);
            if (Math.Abs(_geometryTolerancePx - v) > double.Epsilon)
            {
                _geometryTolerancePx = v;
                OnPropertyChanged(nameof(GeometryTolerancePx));
                UpdateRunGroupingModeDescription();
                SaveColorsToDisk();
            }
        }
    }

    // 是否存在由 Ports 關係導出的邊（載入後設定）
    private bool _hasPortEdges = false;
    public bool HasPortEdges { get => _hasPortEdges; private set { if (_hasPortEdges != value) { _hasPortEdges = value; OnPropertyChanged(nameof(HasPortEdges)); UpdateRunGroupingModeDescription(); } } }

    private string _runGroupingModeDescription = string.Empty;
    public string RunGroupingModeDescription { get => _runGroupingModeDescription; private set { if (_runGroupingModeDescription != value) { _runGroupingModeDescription = value; OnPropertyChanged(nameof(RunGroupingModeDescription)); } } }

    private void UpdateRunGroupingModeDescription()
    {
        try
        {
            if (!HasPortEdges)
            {
                if (GeometryTolerancePx <= 0.0001)
                    RunGroupingModeDescription = "模式：未偵測到 IfcRelConnectsPorts；請設定 GeometryTolerancePx > 0 以使用幾何補橋";
                else
                    RunGroupingModeDescription = $"模式：無 Ports，使用幾何補橋 (tol={GeometryTolerancePx:0.##} px)";
            }
            else
            {
                if (GeometryTolerancePx <= 0.0001)
                    RunGroupingModeDescription = "模式：System 分桶 + 關係連接 (GeometryTolerance=0 不做幾何橋接)";
                else
                    RunGroupingModeDescription = $"模式：System 分桶 + 關係連接 + 幾何補橋 (tol={GeometryTolerancePx:0.##} px)";
            }
        }
        catch { }
    }

    // 診斷用：是否顯示縮放錨點（預設關閉）
    private bool _showZoomAnchor = false;
    public bool ShowZoomAnchor
    {
        get => _showZoomAnchor;
        set { if (_showZoomAnchor != value) { _showZoomAnchor = value; OnPropertyChanged(nameof(ShowZoomAnchor)); } }
    }

    // Phase 0: 顏色設定（含圖例綁定）
    private Brush _terminalBrush = Brushes.Red;
    public Brush TerminalBrush { get => _terminalBrush; set { if (_terminalBrush != value) { _terminalBrush = value; OnPropertyChanged(nameof(TerminalBrush)); } } }
    private Brush _pipeNodeBrush = Brushes.Black;
    public Brush PipeNodeBrush { get => _pipeNodeBrush; set { if (_pipeNodeBrush != value) { _pipeNodeBrush = value; OnPropertyChanged(nameof(PipeNodeBrush)); } } }
    // Phase 4: 延遲初始化的專用節點顏色（Fitting / Valve）
    private Brush? _fittingBrush; // 預設: #3399CC
    private Brush? _valveBrush;   // 預設: DarkGoldenrod
    private Brush _pipeEdgeBrush = Brushes.DarkSlateGray;
    public Brush PipeEdgeBrush { get => _pipeEdgeBrush; set { if (_pipeEdgeBrush != value) { _pipeEdgeBrush = value; OnPropertyChanged(nameof(PipeEdgeBrush)); } } }

    // 動態圖例顯示用旗標：載入資料時計算（B）
    private bool _hasFittingNodes;
    public bool HasFittingNodes { get => _hasFittingNodes; private set { if (_hasFittingNodes != value) { _hasFittingNodes = value; OnPropertyChanged(nameof(HasFittingNodes)); } } }
    private bool _hasValveNodes;
    public bool HasValveNodes { get => _hasValveNodes; private set { if (_hasValveNodes != value) { _hasValveNodes = value; OnPropertyChanged(nameof(HasValveNodes)); } } }

    // Run 著色切換與圖例
    private bool _colorByRunId = false;
    public bool ColorByRunId
    {
        get => _colorByRunId;
        set
        {
            if (_colorByRunId != value)
            {
                _colorByRunId = value;
                OnPropertyChanged(nameof(ColorByRunId));
                if (_colorByRunId) ApplyRunColorsAndLegend();
                else { ClearRunColorsToDefault(); RunLegend.Clear(); }
                SaveColorsToDisk();
            }
        }
    }
    public ObservableCollection<RunLegendItem> RunLegend { get; } = new();
    public class RunLegendItem
    {
        public int RunId { get; set; }
        public Brush Color { get; set; } = Brushes.Gray;
        public int Count { get; set; }
        public string Label => $"Run {RunId} ({Count})";
    }
    public enum RunLegendSortMode { ByRunIdAsc, ByCountDesc }
    private RunLegendSortMode _runLegendSort = RunLegendSortMode.ByRunIdAsc;
    public RunLegendSortMode RunLegendSort
    {
        get => _runLegendSort;
        set { if (_runLegendSort != value) { _runLegendSort = value; OnPropertyChanged(nameof(RunLegendSort)); RefreshRunLegendView(); } }
    }
    private int _runLegendPageSize = 10;
    public int RunLegendPageSize
    {
        get => _runLegendPageSize;
        set { int v = Math.Clamp(value, 1, 200); if (_runLegendPageSize != v) { _runLegendPageSize = v; OnPropertyChanged(nameof(RunLegendPageSize)); RefreshRunLegendView(); } }
    }
    private int _runLegendPage = 1;
    public int RunLegendPage
    {
        get => _runLegendPage;
        set { int v = Math.Max(1, value); if (_runLegendPage != v) { _runLegendPage = v; OnPropertyChanged(nameof(RunLegendPage)); RefreshRunLegendView(); } }
    }
    public ObservableCollection<RunLegendItem> RunLegendView { get; } = new();
    public ICommand RunLegendPrevPageCommand { get; }
    public ICommand RunLegendNextPageCommand { get; }

    // Phase 0: 顏色預設清單與索引
    private static readonly Brush[] PresetBrushes = new Brush[]
    {
        Brushes.Red, Brushes.OrangeRed, Brushes.Orange, Brushes.Gold,
        Brushes.YellowGreen, Brushes.SeaGreen, Brushes.SteelBlue, Brushes.MediumPurple,
        Brushes.DeepPink
    };
    private int _terminalBrushIndex = 0;
    private int _pipeBrushIndex = 0; // legacy combined pipe index（保留相容）
    private int _pipeNodeBrushIndex = 0;
    private int _pipeEdgeBrushIndex = 0;
    

    // Settings persistence
    private static readonly string ColorSettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IFC_Viewer_00", "schematic_colors.json");
    private class ColorSettings
    {
        public string? Terminal { get; set; }
        public string? PipeNode { get; set; }
        public string? PipeEdge { get; set; }
        // 新增：短邊隱藏與閾值（使用可為空以相容舊檔）
        public bool? AutoHideShortPipeSizeLabels { get; set; }
        public double? ShortPipeLabelMinLengthPx { get; set; }
        public double? TagScale { get; set; }
        public bool? ShowAllNodes { get; set; }
        public double? GeometryTolerancePx { get; set; }
        public string? MergeMode { get; set; }
        public bool? ColorByRunId { get; set; }
        // 新增：2D 後處理參數（可為空以相容舊檔）
        public bool? HideTinyStubs { get; set; }
        public double? MinSegmentScreenLengthPx { get; set; }
        public double? NodeMergeScreenDistancePx { get; set; }
        public double? EndpointSnapDistancePx { get; set; }
    }

        public SchematicViewModel(SchematicService service, ISelectionService? selection = null)
        {
            _service = service;
            _selection = selection;
            LastGraphReportJson = string.Empty;
            // 初始化 UI 旗標（與 Service 同步）
            try { _throughFittingRewireEnabled = _service.ThroughFittingRewireEnabled; }
            catch { _throughFittingRewireEnabled = false; }
            // 配件線：優先從模型直接抽取「端口→配件中心」星狀連線；無模型時退回 seed 路徑
            BuildFittingNetworkCommand = new SchematicCommand(async _ =>
            {
                try
                {
                    if (CurrentModel != null)
                    {
                        // 鎖定 YZ 平面以符合你的視圖（可後續做成設定）
                        var (data, report) = await _service.BuildFittingNetworkFromModelAsync(CurrentModel, SchematicService.UserProjectionPlane.YZ, onlyElbow: false, flipY: true);
                        // 已含投影座標，使用 LoadProjected 以避免再挑平面
                        await LoadProjectedAsync(data);
                        _currentData = data;
                        AddLog($"[FittingHub] 完成（FromModel, plane=YZ, onlyElbow=N, flipY=Y）：Nodes={report.TotalNodes} Edges={report.TotalEdges} (Rewired={report.RewiredEdges})");
                        LastGraphReportJson = System.Text.Json.JsonSerializer.Serialize(report);
                        OnPropertyChanged(nameof(LastGraphReportJson));
                        return;
                    }

                    // 無模型：退回 seed 星狀構建（沿用現有資料座標）
                    AddLog("[FittingHub] 無模型可用，改用現有資料種子建立配件線（配件中心⇄端點）");
                    var seed = _currentData ?? _service.LastBuiltData;
                    if (seed == null || (seed.Nodes?.Count ?? 0) == 0)
                    { AddLog("[FittingHub] 失敗：沒有可用資料"); return; }
                    {
                        var (data2, report2) = await _service.BuildFittingNetworkFromSeedAsync(seed);
                        await LoadProjectedAsync(data2);
                        _currentData = data2;
                        AddLog($"[FittingHub] 完成（FromSeed）：Nodes={report2.TotalNodes} Edges={report2.TotalEdges} (Rewired={report2.RewiredEdges})");
                        LastGraphReportJson = System.Text.Json.JsonSerializer.Serialize(report2);
                        OnPropertyChanged(nameof(LastGraphReportJson));
                    }
                }
                catch (Exception ex)
                {
                    AddLog("[FittingHub] 失敗: " + ex.Message);
                }
            });
            NodeClickCommand = new SchematicCommand(obj =>
            {
                if (obj is SchematicNodeView nv && nv.Node?.Entity != null)
                {
                    var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;
                    var lbl = (nv.Node.Entity as IPersistEntity)?.EntityLabel ?? 0;
                    if (_selection != null && lbl != 0)
                    {
                        if (ctrl)
                        {
                            if (_selection.Selected.Contains(lbl)) _selection.Remove(lbl, SelectionOrigin.Schematic);
                            else _selection.Add(lbl, SelectionOrigin.Schematic);
                        }
                        else
                        {
                            _selection.SetSelection(new[] { lbl }, SelectionOrigin.Schematic);
                        }
                    }
                    RequestHighlight?.Invoke(nv.Node.Entity, !ctrl);
                    // 屬性面板（單選）
                    SetSelection(nv, null);
                }
            });
            EdgeClickCommand = new SchematicCommand(obj =>
            {
                if (obj is SchematicEdgeView ev && ev.Start?.Node?.Entity != null)
                {
                    // 以兩端節點加入選取集合
                    var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;
                    var ids = new List<int>();
                    var sId = (ev.Start.Node.Entity as IPersistEntity)?.EntityLabel ?? 0;
                    var tId = (ev.End?.Node?.Entity as IPersistEntity)?.EntityLabel ?? 0;
                    if (sId != 0) ids.Add(sId);
                    if (tId != 0) ids.Add(tId);
                    if (_selection != null && ids.Count > 0)
                    {
                        if (ctrl) _selection.AddRange(ids, SelectionOrigin.Schematic);
                        else _selection.SetSelection(ids, SelectionOrigin.Schematic);
                    }
                    RequestHighlight?.Invoke(ev.Start.Node.Entity, !ctrl);
                    SetSelection(null, ev);
                }
            });

            DumpRewiredEdgesCommand = new SchematicCommand(_ =>
            {
                try
                {
                    var list = Edges.Where(e => e.Edge?.Origin == Models.SchematicEdge.EdgeOriginKind.Rewired)
                                     .Select(e => new
                                     {
                                         S = e.Start?.Node?.Id ?? "(null)",
                                         T = e.End?.Node?.Id ?? "(null)",
                                         Sys = !string.IsNullOrWhiteSpace(e.Edge?.SystemAbbreviation) ? e.Edge!.SystemAbbreviation! : (e.Edge?.SystemName ?? ""),
                                         SX = e.Start?.X ?? double.NaN,
                                         SY = e.Start?.Y ?? double.NaN,
                                         TX = e.End?.X ?? double.NaN,
                                         TY = e.End?.Y ?? double.NaN
                                     })
                                     .ToList();
                    AddLog($"[Rewired] Count={list.Count}");
                    // 先列系統分桶，便於快速確認 VP / SWP / WP 是否皆有
                    var bySys = list
                        .GroupBy(x => string.IsNullOrWhiteSpace(x.Sys) ? "(未指定)" : x.Sys.Trim())
                        .OrderBy(g => g.Key)
                        .Select(g => new { Sys = g.Key, Count = g.Count() })
                        .ToList();
                    if (bySys.Count > 0)
                    {
                        AddLog("[Rewired] By System:");
                        foreach (var g in bySys)
                            AddLog($"[Rewired]   System={g.Sys}, Count={g.Count}");
                    }
                    foreach (var it in list.Take(50))
                    {
                        AddLog($"[Rewired] {it.S} -> {it.T} | Sys={it.Sys} | S=({it.SX:0.##},{it.SY:0.##}) T=({it.TX:0.##},{it.TY:0.##})");
                    }
                    if (list.Count > 50) AddLog($"[Rewired] ... 其餘 {list.Count - 50} 條省略");
                }
                catch (Exception ex)
                {
                    AddLog($"[Rewired] 列印失敗: {ex.Message}");
                }
            });

            IncreaseNodeSizeCommand = new SchematicCommand(_ => AdjustNodeSize(+1));
            DecreaseNodeSizeCommand = new SchematicCommand(_ => AdjustNodeSize(-1));
            IncreaseLineWidthCommand = new SchematicCommand(_ => AdjustLineWidth(+1));
            DecreaseLineWidthCommand = new SchematicCommand(_ => AdjustLineWidth(-1));
            ToggleSnapCommand = new SchematicCommand(_ =>
            {
                SnapEnabled = !SnapEnabled;
                if (SnapEnabled) SnapToPixelGrid(Nodes);
                LogVisualSettings();
            });
            ToggleLevelsCommand = new SchematicCommand(_ =>
            {
                LevelsVisible = !LevelsVisible;
                foreach (var lv in LevelLines) lv.Visible = LevelsVisible;
                AddLog($"[View] Levels {(LevelsVisible ? "On" : "Off")}");
            });

            // Run 分組命令初始化
            ComputeRunsCommand = new SchematicCommand(_ =>
            {
                try { ComputeRuns(); AddLog("[Run] 已完成分組"); }
                catch (Exception ex) { AddLog($"[Run] 分組失敗: {ex.Message}"); }
            });

            // 顏色切換（簡易用預設色循環）
            CycleTerminalColorCommand = new SchematicCommand(_ =>
            {
                _terminalBrushIndex = (_terminalBrushIndex + 1) % PresetBrushes.Length;
                TerminalBrush = PresetBrushes[_terminalBrushIndex];
                ApplyCurrentColorsToViews();
                AddLog($"[View] Terminal 色切換 → {TerminalBrush}");
                SaveColorsToDisk();
            });
            CyclePipeColorCommand = new SchematicCommand(_ =>
            {
                _pipeBrushIndex = (_pipeBrushIndex + 1) % PresetBrushes.Length;
                // 同步節點與邊：節點走 PipeNodeBrush、邊走 PipeEdgeBrush（可相同色或不同色；此處同色）
                PipeNodeBrush = PresetBrushes[_pipeBrushIndex];
                PipeEdgeBrush = PresetBrushes[_pipeBrushIndex];
                ApplyCurrentColorsToViews();
                AddLog($"[View] Pipe 色切換 → {PipeEdgeBrush}");
                SaveColorsToDisk();
            });
            CyclePipeNodeColorCommand = new SchematicCommand(_ =>
            {
                _pipeNodeBrushIndex = (_pipeNodeBrushIndex + 1) % PresetBrushes.Length;
                PipeNodeBrush = PresetBrushes[_pipeNodeBrushIndex];
                ApplyCurrentColorsToViews();
                AddLog($"[View] Pipe 節點色切換 → {PipeNodeBrush}");
                SaveColorsToDisk();
            });
            CyclePipeEdgeColorCommand = new SchematicCommand(_ =>
            {
                _pipeEdgeBrushIndex = (_pipeEdgeBrushIndex + 1) % PresetBrushes.Length;
                PipeEdgeBrush = PresetBrushes[_pipeEdgeBrushIndex];
                ApplyCurrentColorsToViews();
                AddLog($"[View] Pipe 線色切換 → {PipeEdgeBrush}");
                SaveColorsToDisk();
            });

            RunLegendPrevPageCommand = new SchematicCommand(_ => { RunLegendPage = Math.Max(1, RunLegendPage - 1); });
            RunLegendNextPageCommand = new SchematicCommand(_ => { RunLegendPage = RunLegendPage + 1; });

            // 匯出 Run CSV
            ExportRunsToCsvCommand = new SchematicCommand(_ =>
            {
                try { ExportRunsToCsv(); }
                catch (Exception ex) { AddLog($"[Run] 匯出失敗: {ex.Message}"); }
            });

            // 啟動載入既有顏色設定
            LoadColorsFromDisk();
        }

        // ===== 右側屬性面板 =====
        public class PropertyEntry
        {
            public string Key { get; set; } = string.Empty;
            public string? Value { get; set; }
        }

        private void SetSelection(SchematicNodeView? node, SchematicEdgeView? edge)
        {
            try
            {
                Properties.Clear();
                if (node != null)
                {
                    SelectedTitle = $"節點: {node.Node?.Name}";
                    if (node.Node != null) BuildPropertiesFromNode(node.Node!);
                    OnPropertyChanged(nameof(SelectedTitle));
                    return;
                }
                if (edge != null)
                {
                    SelectedTitle = $"邊: {edge.Edge?.Id}";
                    if (edge.Edge != null) BuildPropertiesFromEdge(edge.Edge!);
                    OnPropertyChanged(nameof(SelectedTitle));
                    return;
                }
                SelectedTitle = "(未選取)";
                OnPropertyChanged(nameof(SelectedTitle));
            }
            catch { }
        }

        private void Add(string key, object? value)
        {
            try
            {
                string v = value switch
                {
                    null => string.Empty,
                    double d => d.ToString("0.###"),
                    System.Windows.Media.Media3D.Point3D p => $"({p.X:0.##},{p.Y:0.##},{p.Z:0.##})",
                    System.Windows.Point p2 => $"({p2.X:0.##},{p2.Y:0.##})",
                    bool b => b ? "True" : "False",
                    _ => value.ToString() ?? string.Empty
                };
                Properties.Add(new PropertyEntry { Key = key, Value = v });
            }
            catch { }
        }

        private void BuildPropertiesFromNode(SchematicNode n)
        {
            if (n == null) return;
            Add("Id", n.Id);
            Add("Name", n.Name);
            Add("IfcType", n.IfcType);
            Add("HostIfcType", n.HostIfcType);
            Add("HostLabel", n.HostLabel);
            Add("PortLabel", n.PortLabel);
            Add("System", n.SystemName);
            Add("System Abbreviation", n.SystemAbbreviation);
            Add("System Type", n.SystemType);
            Add("Level", n.LevelName);
            Add("Pos3D", n.Position3D);
            Add("Pos2D", n.Position2D);
            // IFC Psets
            AppendIfcPsets(n.Entity);
        }

        private void BuildPropertiesFromEdge(SchematicEdge e)
        {
            if (e == null) return;
            Add("Id", e.Id);
            Add("Start", e.StartNode?.Name);
            Add("End", e.EndNode?.Name);
            Add("System", e.SystemName);
            Add("System Abbreviation", e.SystemAbbreviation);
            Add("System Type", e.SystemType);
            Add("Level", e.LevelName);
            Add("Orientation", e.Orientation.ToString());
            Add("IsMainPipe", e.IsMainPipe);
            Add("NominalDiameter(mm)", e.NominalDiameterMm);
            Add("OuterDiameter(mm)", e.OuterDiameterMm);
            Add("DN Source", e.ValueSourceNominalDiameter);
            Add("OD Source", e.ValueSourceOuterDiameter);
            // IFC Psets（優先使用 Edge.Entity）
            if (e.Entity != null) AppendIfcPsets(e.Entity);
            else if (e.Connection != null) AppendIfcPsets(e.Connection);
        }

        private static string? FormatPipeSizeLabel(SchematicEdge e)
        {
            try
            {
                double? dn = e.NominalDiameterMm;
                double? od = e.OuterDiameterMm;
                string fmt(double v)
                {
                    var vi = Math.Round(v);
                    return Math.Abs(vi - v) < 0.05 ? vi.ToString("0") : v.ToString("0.#");
                }
                if (dn.HasValue && dn.Value > 0) return $"DN{fmt(dn.Value)}";
                if (od.HasValue && od.Value > 0) return $"Ø{fmt(od.Value)}mm";
                return null;
            }
            catch { return null; }
        }

        private void AppendIfcPsets(IPersistEntity? ent)
        {
            if (ent == null) return;
            try
            {
                // 如果是 IIfcObjectDefinition，列出其 IsDefinedBy -> IIfcPropertySet
                if (ent is IIfcObject obj)
                {
                    foreach (var rel in obj.IsDefinedBy ?? Enumerable.Empty<IIfcRelDefines>())
                    {
                        if (rel is IIfcRelDefinesByProperties rdp)
                        {
                            var pset = rdp.RelatingPropertyDefinition as IIfcPropertySet;
                            if (pset == null) continue;
                            var pname = IFC_Viewer_00.Services.IfcStringHelper.FromValue(pset.Name) ?? "Pset";
                            foreach (var p in pset.HasProperties ?? Enumerable.Empty<IIfcProperty>())
                            {
                                var n = IFC_Viewer_00.Services.IfcStringHelper.FromValue(p?.Name) ?? "(prop)";
                                string val = string.Empty;
                                if (p is IIfcPropertySingleValue sv)
                                {
                                    try { val = sv.NominalValue?.ToString() ?? string.Empty; } catch { }
                                }
                                Add($"Pset[{pname}].{n}", val);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Phase 2: 由框選等行為觸發的批次選取（支援取代/累加）
        public void SelectByEntityLabels(IEnumerable<int> labels, bool additive)
        {
            if (labels == null) return;
            var ids = labels.Where(id => id != 0).Distinct().ToList();
            if (ids.Count == 0) return;
            if (_selection != null)
            {
                if (additive) _selection.AddRange(ids, SelectionOrigin.Schematic);
                else _selection.SetSelection(ids, SelectionOrigin.Schematic);
            }
            // 本地同步選取樣式（即便沒有 selection service 也能看到效果）
            HashSet<int> finalSet = new HashSet<int>();
            if (_selection != null)
            {
                // 若有 selection service，直接以其集合為準（已被上面更新過）
                finalSet = _selection.Selected.ToHashSet();
            }
            else
            {
                // 無 selection service：用本次 ids 模擬，若 additive 則累積當前已選
                if (additive)
                {
                    foreach (var nv in Nodes)
                    {
                        var lbl0 = (nv.Node.Entity as IPersistEntity)?.EntityLabel ?? 0;
                        if (lbl0 != 0 && nv.IsSelected) finalSet.Add(lbl0);
                    }
                }
                foreach (var id in ids) finalSet.Add(id);
            }
            foreach (var nv in Nodes)
            {
                var lbl = (nv.Node.Entity as IPersistEntity)?.EntityLabel ?? 0;
                nv.IsSelected = lbl != 0 && finalSet.Contains(lbl);
            }
            foreach (var ev in Edges)
            {
                var sid = (ev.Start.Node.Entity as IPersistEntity)?.EntityLabel ?? 0;
                var tid = (ev.End.Node.Entity as IPersistEntity)?.EntityLabel ?? 0;
                ev.IsSelected = (sid != 0 && finalSet.Contains(sid)) || (tid != 0 && finalSet.Contains(tid));
            }
        }

        public async Task LoadAsync(IModel model)
        {
            // 設定目前模型引用，供後續『管網建構(Quick)』與穿越配件切換即時重建使用
            try { CurrentModel = model; } catch { }
            var data = await _service.GenerateTopologyAsync(model);
            LoadData(data);
            _currentData = data;
        }

        public Task LoadFromDataAsync(SchematicData data)
        {
            LoadData(data);
            _currentData = data;
            return Task.CompletedTask;
        }

        // 新增：載入管段軸線結果（節點+邊），並套用 FitToCanvas
        public Task LoadPipeAxesAsync(SchematicData data)
        {
            if (data == null) return Task.CompletedTask;
            Nodes.Clear();
            Edges.Clear();
            LevelLines.Clear();
            // 直接將 data.Nodes 轉成 NodeView
            var map = new Dictionary<SchematicNode, SchematicNodeView>();
            foreach (var n in data.Nodes)
            {
                // 決定節點顏色（Phase 4：加入 Fitting 類別顏色）
                // 規則：FlowTerminal → TerminalBrush；IfcPipeFitting → FittingBrush (新增)；Valve → DarkGoldenrod；其餘（含 PipeSegment 端點）→ PipeNodeBrush
                var ifcType = n.IfcType ?? string.Empty;
                System.Windows.Media.Brush brush;
                if (ifcType.IndexOf("FlowTerminal", StringComparison.OrdinalIgnoreCase) >= 0)
                    brush = TerminalBrush;
                else if (ifcType.IndexOf("PipeFitting", StringComparison.OrdinalIgnoreCase) >= 0)
                    brush = _fittingBrush ??= new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x99, 0xCC));
                else if (ifcType.IndexOf("Valve", StringComparison.OrdinalIgnoreCase) >= 0)
                    brush = _valveBrush ??= new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkGoldenrod);
                else
                    brush = PipeNodeBrush;
                var nv = new SchematicNodeView
                {
                    Node = n,
                    X = n.Position2D.X,
                    Y = n.Position2D.Y,
                    NodeBrush = brush,
                    NodeSize = _defaultNodeSize
                };
                map[n] = nv;
                Nodes.Add(nv);
            }
            foreach (var e in data.Edges)
            {
                if (e.StartNode == null || e.EndNode == null) continue;
                if (!map.TryGetValue(e.StartNode, out var s) || !map.TryGetValue(e.EndNode, out var t)) continue;
                var ev = new SchematicEdgeView
                {
                    Edge = e,
                    Start = s,
                    End = t,
                    EdgeBrush = PipeEdgeBrush,
                    Thickness = _defaultEdgeThickness
                };
                ev.SizeLabel = FormatPipeSizeLabel(e);
                Edges.Add(ev);
            }
            FitToCanvas(Nodes, CanvasWidth, CanvasHeight, CanvasPadding);
            if (SnapEnabled) SnapToPixelGrid(Nodes);
            // 關鍵：回寫新座標到 Node.Position2D，因 XAML 的 Line 綁定讀的是 Node.Position2D
            foreach (var nv in Nodes)
            {
                nv.Node.Position2D = new System.Windows.Point(nv.X, nv.Y);
            }
            BuildLevelLines(data);
            // P3: 建立系統過濾並套用可見性
            BuildSystemFiltersFrom(data);
            ApplySystemVisibility();
            // 動態圖例旗標：統計節點型別
            try
            {
                HasFittingNodes = Nodes.Any(nv => (nv.Node.IfcType ?? string.Empty).IndexOf("PipeFitting", StringComparison.OrdinalIgnoreCase) >= 0);
                HasValveNodes = Nodes.Any(nv => (nv.Node.IfcType ?? string.Empty).IndexOf("Valve", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { HasFittingNodes = HasValveNodes = false; }
            LogVisualSettings();
            return Task.CompletedTask;
        }

        // V1：從 3D 點集合載入（指定投影平面，支援方向翻轉與退化平面自動建議）
        public Task LoadPointsFrom3DAsync(IEnumerable<System.Windows.Media.Media3D.Point3D> points3D,
            string plane,
            IEnumerable<(int? portLabel, string? name, string? hostType, int? hostLabel, bool isFromPipeSegment)>? meta = null,
            bool flipX = false,
            bool flipY = true,
            bool tryBestIfDegenerate = true)
        {
            if (points3D == null) return Task.CompletedTask;
            var list3 = points3D.ToList();
            if (list3.Count == 0) return Task.CompletedTask;

            // 解析平面字串
            ProjectionPlane pl = ProjectionPlane.XY;
            var pstr = (plane ?? string.Empty).Trim().ToUpperInvariant();
            if (pstr == "XZ") pl = ProjectionPlane.XZ; else if (pstr == "YZ") pl = ProjectionPlane.YZ; else pl = ProjectionPlane.XY;

            // 投影 + 可選退化檢測
            var projected = Project3DTo2D(list3, pl, flipX, flipY, tryBestIfDegenerate, out var finalPlane, out var wasDegenerate);
            if (wasDegenerate)
            {
                AddLog($"[V1] 所選平面 {plane} 在資料上退化（跨度極小），已自動建議採用 {finalPlane}。");
            }

            // 使用既有載入流程（確保畫布適配一致）
            return LoadPointsAsync(projected, meta);
        }

        // V1：僅載入 2D 點集合（無邊）
        // points: 已投影後的 2D 座標
        // meta: 與 points 同序的 Port 詳細資料 (index 對 index)，避免以 EntityLabel 當 key 造成落差
        public Task LoadPointsAsync(IEnumerable<Point> points, IEnumerable<(int? portLabel,string? name,string? hostType,int? hostLabel,bool isFromPipeSegment)>? meta = null)
        {
            if (points == null) return Task.CompletedTask;
            var list = points.ToList();
            Nodes.Clear();
            Edges.Clear();
            int i = 0;
            var metaList = meta?.ToList();
            foreach (var p in list)
            {
                string name = $"P{i}";
                string hostType = string.Empty;
                bool isPipe = false;
                int? hostLbl = null;
                if (metaList != null && i < metaList.Count)
                {
                    var md = metaList[i];
                    name = string.IsNullOrWhiteSpace(md.name) ? (md.portLabel?.ToString() ?? name) : md.name!;
                    hostType = md.hostType ?? string.Empty;
                    hostLbl = md.hostLabel;
                    isPipe = md.isFromPipeSegment;
                }
                var node = new SchematicNode
                {
                    Id = $"P{i}",
                    Name = name,
                    IfcType = "Port",
                    Position3D = new System.Windows.Media.Media3D.Point3D(p.X, p.Y, 0),
                    Position2D = p,
                    HostIfcType = hostType,
                    HostLabel = hostLbl,
                    IsFromPipeSegment = isPipe,
                    PortLabel = metaList != null && i < metaList.Count ? metaList[i].portLabel : i
                };
                i++;
                Nodes.Add(new SchematicNodeView
                {
                    Node = node,
                    X = p.X,
                    Y = p.Y,
                    NodeBrush = isPipe ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.Red,
                    NodeSize = _defaultNodeSize
                });
            }
            // 適配畫布
            FitToCanvas(Nodes, CanvasWidth, CanvasHeight, CanvasPadding);
            if (SnapEnabled) SnapToPixelGrid(Nodes);
            LogVisualSettings();
            return Task.CompletedTask;
        }

        // 3D → 2D 投影與適配的核心（不直接改變 Nodes，由呼叫端決定後續流程）
        // - plane: XY / XZ / YZ
        // - flipX/flipY: 是否鏡像對應軸（通常建議 flipY=true 以符合 Canvas Y 向下）
        // - tryBestIfDegenerate: 若在選定平面上寬或高的跨度過小，嘗試選擇跨度更大的平面
        // 回傳：尚未做 FitToCanvas 的 2D 點（統一交給 LoadPointsAsync → FitToCanvas）
        private IList<Point> Project3DTo2D(
            IList<System.Windows.Media.Media3D.Point3D> pts,
            ProjectionPlane plane,
            bool flipX,
            bool flipY,
            bool tryBestIfDegenerate,
            out string finalPlane,
            out bool wasDegenerate)
        {
            wasDegenerate = false;

            // 計算各軸跨度
            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            double minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
            double rx = Math.Max(1e-12, maxX - minX);
            double ry = Math.Max(1e-12, maxY - minY);
            double rz = Math.Max(1e-12, maxZ - minZ);

            // 若退化（寬或高極小），可改採兩軸跨度和最大的平面
            bool IsDegenerate(ProjectionPlane pl)
            {
                return pl switch
                {
                    ProjectionPlane.XY => (rx < 1e-6 || ry < 1e-6),
                    ProjectionPlane.XZ => (rx < 1e-6 || rz < 1e-6),
                    _ => (ry < 1e-6 || rz < 1e-6),
                };
            }

            if (tryBestIfDegenerate && IsDegenerate(plane))
            {
                wasDegenerate = true;
                // 挑跨度和最大的二軸平面
                double sXY = rx + ry, sXZ = rx + rz, sYZ = ry + rz;
                if (sXY >= sXZ && sXY >= sYZ) plane = ProjectionPlane.XY;
                else if (sXZ >= sXY && sXZ >= sYZ) plane = ProjectionPlane.XZ;
                else plane = ProjectionPlane.YZ;
            }
            finalPlane = plane.ToString();

            var raw2d = new List<Point>(pts.Count);
            foreach (var p in pts)
            {
                double u, v;
                switch (plane)
                {
                    case ProjectionPlane.XZ: u = p.X; v = p.Z; break;
                    case ProjectionPlane.YZ: u = p.Y; v = p.Z; break;
                    default: u = p.X; v = p.Y; break; // XY
                }
                if (flipX) u = -u;
                if (flipY) v = -v; // Canvas Y 向下
                raw2d.Add(new Point(u, v));
            }
            return raw2d;
        }

        // 已投影資料載入：保留既有 Position2D，僅做 FitToCanvas 與（必要時）同步回 Position2D
        public Task LoadProjectedAsync(SchematicData data)
        {
            if (data == null) return Task.CompletedTask;

            Nodes.Clear();
            Edges.Clear();
            LevelLines.Clear();

            var nodeMap = new Dictionary<object, SchematicNodeView>();
            foreach (var n in data.Nodes)
            {
                object key = (object?)n.Entity ?? (object)n.Id;
                if (nodeMap.ContainsKey(key))
                {
                    key = (n.Entity != null) ? (object)$"{n.Entity.EntityLabel}:{n.Id}" : (object)$"dup:{n.Id}:{nodeMap.Count}";
                }
                var nv = new SchematicNodeView
                {
                    Node = n,
                    X = n.Position2D.X,
                    Y = n.Position2D.Y,
                    NodeBrush = GetBrushByIfcType(n.IfcType),
                    NodeSize = _defaultNodeSize
                };
                nodeMap[key] = nv;
            }
            foreach (var nv in nodeMap.Values) Nodes.Add(nv);

            foreach (var e in data.Edges)
            {
                if (e.StartNode == null || e.EndNode == null) continue;
                var s = FindNodeView(nodeMap, e.StartNode);
                var t = FindNodeView(nodeMap, e.EndNode);
                if (s == null || t == null) continue;
                var ev = new SchematicEdgeView
                {
                    Edge = e,
                    Start = s,
                    End = t,
                    EdgeBrush = GetDarkerBrush(s.NodeBrush),
                    Thickness = _defaultEdgeThickness
                };
                ev.SizeLabel = FormatPipeSizeLabel(e);
                Edges.Add(ev);
            }

            // 僅做適配畫布
            FitToCanvas(Nodes, CanvasWidth, CanvasHeight, CanvasPadding);
            if (SnapEnabled) SnapToPixelGrid(Nodes);
            // 為了讓 Edge 繪製位置與節點一致，將新的 X/Y 回寫到 Node.Position2D
            foreach (var nv in Nodes)
            {
                nv.Node.Position2D = new System.Windows.Point(nv.X, nv.Y);
            }
            // 建立樓層線（若有）
            BuildLevelLines(data);
            // P3: 建立系統過濾並套用可見性
            BuildSystemFiltersFrom(data);
            ApplySystemVisibility();
            LogVisualSettings();
            // 標記是否有 Ports 邊（以是否存在任何邊為簡化判據）
            HasPortEdges = data.Edges != null && data.Edges.Count > 0; // TODO: 可再細化來源判斷
            UpdateRunGroupingModeDescription();

            return Task.CompletedTask;
        }

        // 依需求新增：在填充 this.Nodes 後，執行 Fit to Canvas 的座標變換，並再更新節點與邊
        public void LoadData(SchematicData data)
        {
            if (data == null) return;

            Nodes.Clear();
            Edges.Clear();
            LevelLines.Clear();

            // 1) 先將 data.Nodes 轉為 NodeView（先用原始 Position2D）
            var nodeMap = new Dictionary<object, SchematicNodeView>();
            foreach (var n in data.Nodes)
            {
                object key = (object?)n.Entity ?? (object)n.Id;
                if (nodeMap.ContainsKey(key))
                {
                    key = (n.Entity != null) ? (object)$"{n.Entity.EntityLabel}:{n.Id}" : (object)$"dup:{n.Id}:{nodeMap.Count}";
                }
                var nv = new SchematicNodeView
                {
                    Node = n,
                    X = n.Position2D.X,
                    Y = n.Position2D.Y,
                    NodeBrush = GetBrushByIfcType(n.IfcType),
                    NodeSize = _defaultNodeSize
                };
                nodeMap[key] = nv;
            }

            foreach (var nv in nodeMap.Values)
                Nodes.Add(nv);

            // 2) Fit to Canvas（最佳投影面）：以 3D → 2D 自動選擇 XY/XZ/YZ 面中面積最大者後再適配畫布
            if (Nodes.Count > 0)
            {
                FitToCanvasBestProjection(Nodes, canvasWidth: 800, canvasHeight: 600, padding: 20);
            }

            // 3) 依據映射建立 EdgeView
            foreach (var e in data.Edges)
            {
                if (e.StartNode == null || e.EndNode == null) continue;
                var s = FindNodeView(nodeMap, e.StartNode);
                var t = FindNodeView(nodeMap, e.EndNode);
                if (s == null || t == null) continue;
                var ev = new SchematicEdgeView
                {
                    Edge = e,
                    Start = s,
                    End = t,
                    EdgeBrush = GetDarkerBrush(s.NodeBrush),
                    Thickness = _defaultEdgeThickness
                };
                ev.SizeLabel = FormatPipeSizeLabel(e);
                Edges.Add(ev);
            }
            BuildLevelLines(data);
            // P3: 建立系統過濾並套用可見性
            BuildSystemFiltersFrom(data);
            ApplySystemVisibility();
        }

        private void BuildFromData(SchematicData data)
        {
            Nodes.Clear();
            Edges.Clear();

            // 建立 NodeView，先以 3D → 2D 的「最佳投影面」決定初始 X/Y（避免壓扁成線）
            var nodeMap = new Dictionary<object, SchematicNodeView>();

            // 計算最佳投影面（僅一次），用於初始座標：
            // 策略：找出 X/Y/Z 三軸跨度，捨棄跨度最小的軸，使用其餘兩軸構成的平面
            ProjectionPlane planeForSeed = ProjectionPlane.XY;
            if (data.Nodes.Count > 0)
            {
                double minX = data.Nodes.Min(n => n.Position3D.X);
                double maxX = data.Nodes.Max(n => n.Position3D.X);
                double minY = data.Nodes.Min(n => n.Position3D.Y);
                double maxY = data.Nodes.Max(n => n.Position3D.Y);
                double minZ = data.Nodes.Min(n => n.Position3D.Z);
                double maxZ = data.Nodes.Max(n => n.Position3D.Z);

                double rangeX = Math.Max(1e-12, maxX - minX);
                double rangeY = Math.Max(1e-12, maxY - minY);
                double rangeZ = Math.Max(1e-12, maxZ - minZ);

                if (rangeX <= rangeY && rangeX <= rangeZ)
                    planeForSeed = ProjectionPlane.YZ; // 捨 X
                else if (rangeY <= rangeX && rangeY <= rangeZ)
                    planeForSeed = ProjectionPlane.XZ; // 捨 Y
                else
                    planeForSeed = ProjectionPlane.XY; // 捨 Z
            }

            foreach (var n in data.Nodes)
            {
                object key = (object?)n.Entity ?? (object)n.Id;
                if (nodeMap.ContainsKey(key))
                {
                    key = (n.Entity != null) ? (object)$"{n.Entity.EntityLabel}:{n.Id}" : (object)$"dup:{n.Id}:{nodeMap.Count}";
                }
                // 優先使用已有的 2D；否則依最佳投影面從 3D 取兩軸作為初始座標
                double x, y;
                if (n.Position2D.X != 0 || n.Position2D.Y != 0)
                {
                    x = n.Position2D.X;
                    y = n.Position2D.Y;
                }
                else
                {
                    switch (planeForSeed)
                    {
                        case ProjectionPlane.XZ: x = n.Position3D.X; y = n.Position3D.Z; break;
                        case ProjectionPlane.YZ: x = n.Position3D.Y; y = n.Position3D.Z; break;
                        default: x = n.Position3D.X; y = n.Position3D.Y; break;
                    }
                }
                var nv = new SchematicNodeView
                {
                    Node = n,
                    X = x * Scale,
                    Y = y * Scale,
                    NodeBrush = GetBrushByIfcType(n.IfcType),
                    NodeSize = _defaultNodeSize
                };
                nodeMap[key] = nv;
            }

            foreach (var nv in nodeMap.Values)
                Nodes.Add(nv);

            // 建立 EdgeView（參考 NodeView）
            foreach (var e in data.Edges)
            {
                if (e.StartNode == null || e.EndNode == null) continue;
                var s = FindNodeView(nodeMap, e.StartNode);
                var t = FindNodeView(nodeMap, e.EndNode);
                if (s == null || t == null) continue;
                var ev = new SchematicEdgeView
                {
                    Edge = e,
                    Start = s,
                    End = t,
                    EdgeBrush = GetDarkerBrush(s.NodeBrush),
                    Thickness = _defaultEdgeThickness
                };
                ev.SizeLabel = FormatPipeSizeLabel(e);
                Edges.Add(ev);
            }

            ApplyForceDirectedLayout(Nodes.ToList(), Edges.ToList(), iterations: 200);
            FitToCanvas(Nodes, CanvasWidth, CanvasHeight, CanvasPadding);
            if (SnapEnabled) SnapToPixelGrid(Nodes);
            LogVisualSettings();
            HasPortEdges = data.Edges != null && data.Edges.Count > 0;
            UpdateRunGroupingModeDescription();
        }

        private static SchematicNodeView? FindNodeView(Dictionary<object, SchematicNodeView> map, SchematicNode node)
        {
            if (node.Entity != null && map.TryGetValue(node.Entity, out var byEnt))
                return byEnt;
            // 回退：比對 Id 或組合鍵
            foreach (var kv in map)
            {
                if (ReferenceEquals(kv.Value.Node, node)) return kv.Value;
                if (!string.IsNullOrEmpty(node.Id) && kv.Value.Node.Id == node.Id) return kv.Value;
            }
            return null;
        }

        // 為避免奇數粗細與半像素導致的視覺偏差，將座標對齊到 0.5px 網格
        private static void SnapToPixelGrid(IEnumerable<SchematicNodeView> nodes)
        {
            foreach (var nv in nodes)
            {
                double sx = Math.Round(nv.X * 2.0, MidpointRounding.AwayFromZero) / 2.0;
                double sy = Math.Round(nv.Y * 2.0, MidpointRounding.AwayFromZero) / 2.0;
                nv.X = sx;
                nv.Y = sy;
            }
        }

        private void AdjustNodeSize(int delta)
        {
            double newSize = Math.Clamp(_defaultNodeSize + delta, 2.0, 32.0);
            _defaultNodeSize = newSize;
            foreach (var nv in Nodes) nv.NodeSize = newSize;
            LogVisualSettings();
        }

        private void AdjustLineWidth(int delta)
        {
            double newThick = Math.Clamp(_defaultEdgeThickness + delta, 1.0, 20.0);
            _defaultEdgeThickness = newThick;
            foreach (var ev in Edges) ev.Thickness = newThick;
            LogVisualSettings();
        }

        private void LogVisualSettings()
        {
            AddLog($"[View] NodeSize={_defaultNodeSize:0.#} px | LineWidth={_defaultEdgeThickness:0.#} px | Snap={(SnapEnabled ? "On" : "Off")}");
        }

        // 重新套用當前配置的顏色到現有節點與邊（用於切色後即時更新）
        private void ApplyCurrentColorsToViews()
        {
            foreach (var nv in Nodes)
            {
                var isTerminal = nv.Node?.IfcType?.IndexOf("FlowTerminal", StringComparison.OrdinalIgnoreCase) >= 0;
                nv.NodeBrush = isTerminal ? TerminalBrush : PipeNodeBrush;
            }
            if (!ColorByRunId)
            {
                foreach (var ev in Edges) ev.EdgeBrush = PipeEdgeBrush;
            }
        }

        // 供 View 呼叫：直接指定顏色（若傳入 null 則維持原值）
        public void SetColors(Brush? terminal = null, Brush? pipeNode = null, Brush? pipeEdge = null)
        {
            if (terminal != null) TerminalBrush = terminal;
            if (pipeNode != null) PipeNodeBrush = pipeNode;
            if (pipeEdge != null) PipeEdgeBrush = pipeEdge;
            ApplyCurrentColorsToViews();
            SaveColorsToDisk();
            AddLog("[View] 顏色已更新並保存");
        }

        // ===== 顏色持久化 =====
        private static string BrushToHex(Brush b)
        {
            if (b is SolidColorBrush scb)
            {
                var c = scb.Color; return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            return "#000000";
        }

        private static Brush HexToBrush(string? hex, Brush fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return fallback;
                if (hex.StartsWith("#"))
                {
                    var col = (Color)ColorConverter.ConvertFromString(hex);
                    return new SolidColorBrush(col);
                }
            }
            catch { }
            return fallback;
        }

        private void SaveColorsToDisk()
        {
            try
            {
                var dir = Path.GetDirectoryName(ColorSettingsPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var obj = new ColorSettings
                {
                    Terminal = BrushToHex(TerminalBrush),
                    PipeNode = BrushToHex(PipeNodeBrush),
                    PipeEdge = BrushToHex(PipeEdgeBrush),
                    AutoHideShortPipeSizeLabels = AutoHideShortPipeSizeLabels,
                    ShortPipeLabelMinLengthPx = ShortPipeLabelMinLengthPx,
                    TagScale = TagScale,
                    ShowAllNodes = ShowAllNodes,
                    GeometryTolerancePx = GeometryTolerancePx,
                    MergeMode = Mode.ToString(),
                    ColorByRunId = ColorByRunId,
                    HideTinyStubs = HideTinyStubs,
                    MinSegmentScreenLengthPx = MinSegmentScreenLengthPx,
                    NodeMergeScreenDistancePx = NodeMergeScreenDistancePx,
                    EndpointSnapDistancePx = EndpointSnapDistancePx
                };
                var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ColorSettingsPath, json);
            }
            catch { }
        }

        private void LoadColorsFromDisk()
        {
            try
            {
                if (!File.Exists(ColorSettingsPath)) return;
                var json = File.ReadAllText(ColorSettingsPath);
                var obj = JsonSerializer.Deserialize<ColorSettings>(json);
                if (obj == null) return;
                // 基本筆刷
                TerminalBrush = HexToBrush(obj.Terminal, TerminalBrush);
                PipeNodeBrush = HexToBrush(obj.PipeNode, PipeNodeBrush);
                PipeEdgeBrush = HexToBrush(obj.PipeEdge, PipeEdgeBrush);
                ApplyCurrentColorsToViews();
                // 其它設定（相容舊檔，逐一檢）
                if (obj.AutoHideShortPipeSizeLabels.HasValue)
                { _autoHideShortPipeSizeLabels = obj.AutoHideShortPipeSizeLabels.Value; OnPropertyChanged(nameof(AutoHideShortPipeSizeLabels)); }
                if (obj.ShortPipeLabelMinLengthPx.HasValue)
                { _shortPipeLabelMinLengthPx = Math.Clamp(obj.ShortPipeLabelMinLengthPx.Value, 0.0, 500.0); OnPropertyChanged(nameof(ShortPipeLabelMinLengthPx)); }
                if (obj.TagScale.HasValue)
                { _tagScale = Math.Clamp(obj.TagScale.Value, 0.1, 5.0); OnPropertyChanged(nameof(TagScale)); }
                if (obj.ShowAllNodes.HasValue)
                { _showAllNodes = obj.ShowAllNodes.Value; OnPropertyChanged(nameof(ShowAllNodes)); }
                if (obj.GeometryTolerancePx.HasValue)
                { _geometryTolerancePx = Math.Clamp(obj.GeometryTolerancePx.Value, 0.0, 500.0); OnPropertyChanged(nameof(GeometryTolerancePx)); }
                if (!string.IsNullOrWhiteSpace(obj.MergeMode) && Enum.TryParse<MergeViewMode>(obj.MergeMode, true, out var m))
                { _mergeMode = m; OnPropertyChanged(nameof(Mode)); OnPropertyChanged(nameof(MergeAcrossLevels)); }
                if (obj.ColorByRunId.HasValue)
                { _colorByRunId = obj.ColorByRunId.Value; OnPropertyChanged(nameof(ColorByRunId)); }
                // 2D 後處理參數
                if (obj.HideTinyStubs.HasValue)
                { _hideTinyStubs = obj.HideTinyStubs.Value; OnPropertyChanged(nameof(HideTinyStubs)); }
                if (obj.MinSegmentScreenLengthPx.HasValue)
                { _minSegmentScreenLengthPx = Math.Clamp(obj.MinSegmentScreenLengthPx.Value, 0.0, 100.0); OnPropertyChanged(nameof(MinSegmentScreenLengthPx)); }
                if (obj.NodeMergeScreenDistancePx.HasValue)
                { _nodeMergeScreenDistancePx = Math.Clamp(obj.NodeMergeScreenDistancePx.Value, 0.0, 100.0); OnPropertyChanged(nameof(NodeMergeScreenDistancePx)); }
                if (obj.EndpointSnapDistancePx.HasValue)
                { _endpointSnapDistancePx = Math.Clamp(obj.EndpointSnapDistancePx.Value, 0.0, 100.0); OnPropertyChanged(nameof(EndpointSnapDistancePx)); }
                AddLog("[View] 已載入顏色設定");
            }
            catch { }
        }

        // INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SchematicNodeView : INotifyPropertyChanged
    {
        public SchematicNode Node { get; set; } = null!;
        private double _x;
        // X/Y 表示圓心座標，供邊線與定位之用
        public double X
        {
            get => _x;
            set
            {
                if (_x != value)
                {
                    _x = value;
                    OnPropertyChanged(nameof(X));
                    OnPropertyChanged(nameof(TopLeftX)); // 依賴 X
                }
            }
        }
        private double _y;
        public double Y
        {
            get => _y;
            set
            {
                if (_y != value)
                {
                    _y = value;
                    OnPropertyChanged(nameof(Y));
                    OnPropertyChanged(nameof(TopLeftY)); // 依賴 Y
                }
            }
        }

    // 節點的繪製尺寸（像素）。預設 8px 直徑（偶數，較不易出現半像素誤差）。
    private double _nodeSize = 8.0;
        public double NodeSize
        {
            get => _nodeSize;
            set
            {
                if (Math.Abs(_nodeSize - value) > double.Epsilon)
                {
                    _nodeSize = value;
                    OnPropertyChanged(nameof(NodeSize));
                    OnPropertyChanged(nameof(TopLeftX));
                    OnPropertyChanged(nameof(TopLeftY));
                }
            }
        }

        // 供 Canvas.Left/Top 綁定使用的左上角座標（將中心減去半徑）
        public double TopLeftX => X - NodeSize / 2.0;
        public double TopLeftY => Y - NodeSize / 2.0;
        private Brush _nodeBrush = Brushes.SteelBlue;
        public Brush NodeBrush { get => _nodeBrush; set { if (_nodeBrush != value) { _nodeBrush = value; OnPropertyChanged(nameof(NodeBrush)); } } }
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } } }
    private bool _visible = true;
    public bool Visible { get => _visible; set { if (_visible != value) { _visible = value; OnPropertyChanged(nameof(Visible)); } } }

        // Phase 2: 類別標誌，供圖層切換判斷
        public bool IsTerminal
        {
            get
            {
                try
                {
                    var t = Node?.IfcType;
                    return !string.IsNullOrEmpty(t) && t.IndexOf("FlowTerminal", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { return false; }
            }
        }
        public bool IsPipeNode => !IsTerminal; // 目前視終端以外者皆視為管線節點

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SchematicEdgeView : INotifyPropertyChanged
    {
        public SchematicEdge Edge { get; set; } = null!;
        private SchematicNodeView _start = null!;
        public SchematicNodeView Start
        {
            get => _start;
            set
            {
                if (!ReferenceEquals(_start, value))
                {
                    if (_start != null) _start.PropertyChanged -= OnEndpointChanged;
                    _start = value;
                    if (_start != null) _start.PropertyChanged += OnEndpointChanged;
                    OnPropertyChanged(nameof(Start));
                    OnPropertyChanged(nameof(MidX));
                    OnPropertyChanged(nameof(MidY));
                    OnPropertyChanged(nameof(AngleDeg));
                    OnPropertyChanged(nameof(EdgeLength));
                }
            }
        }

        private SchematicNodeView _end = null!;
        public SchematicNodeView End
        {
            get => _end;
            set
            {
                if (!ReferenceEquals(_end, value))
                {
                    if (_end != null) _end.PropertyChanged -= OnEndpointChanged;
                    _end = value;
                    if (_end != null) _end.PropertyChanged += OnEndpointChanged;
                    OnPropertyChanged(nameof(End));
                    OnPropertyChanged(nameof(MidX));
                    OnPropertyChanged(nameof(MidY));
                    OnPropertyChanged(nameof(AngleDeg));
                    OnPropertyChanged(nameof(EdgeLength));
                }
            }
        }
        private Brush _edgeBrush = Brushes.DarkSlateGray;
        public Brush EdgeBrush { get => _edgeBrush; set { if (_edgeBrush != value) { _edgeBrush = value; OnPropertyChanged(nameof(EdgeBrush)); } } }
        private double _thickness = 2.0;
        public double Thickness { get => _thickness; set { if (Math.Abs(_thickness - value) > double.Epsilon) { _thickness = value; OnPropertyChanged(nameof(Thickness)); } } }
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } } }
    private bool _visible = true;
    public bool Visible { get => _visible; set { if (_visible != value) { _visible = value; OnPropertyChanged(nameof(Visible)); } } }

        // 供管徑標籤使用
        private string? _sizeLabel;
        public string? SizeLabel { get => _sizeLabel; set { if (_sizeLabel != value) { _sizeLabel = value; OnPropertyChanged(nameof(SizeLabel)); OnPropertyChanged(nameof(MidX)); OnPropertyChanged(nameof(MidY)); } } }
        public double MidX => ((Start?.X ?? 0.0) + (End?.X ?? 0.0)) / 2.0;
        public double MidY => ((Start?.Y ?? 0.0) + (End?.Y ?? 0.0)) / 2.0;
        public double AngleDeg
        {
            get
            {
                double sx = Start?.X ?? 0.0;
                double sy = Start?.Y ?? 0.0;
                double ex = End?.X ?? 0.0;
                double ey = End?.Y ?? 0.0;
                double dx = ex - sx;
                double dy = ey - sy;
                return Math.Atan2(dy, dx) * 180.0 / Math.PI;
            }
        }

        // 供隱藏短邊標籤判斷的像素長度
        public double EdgeLength
        {
            get
            {
                double sx = Start?.X ?? 0.0;
                double sy = Start?.Y ?? 0.0;
                double ex = End?.X ?? 0.0;
                double ey = End?.Y ?? 0.0;
                double dx = ex - sx;
                double dy = ey - sy;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        private void OnEndpointChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SchematicNodeView.X) || e.PropertyName == nameof(SchematicNodeView.Y))
            {
                OnPropertyChanged(nameof(MidX));
                OnPropertyChanged(nameof(MidY));
                OnPropertyChanged(nameof(AngleDeg));
                OnPropertyChanged(nameof(EdgeLength));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // 輕量 ICommand 實作
    public class SchematicCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        public SchematicCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute; _canExecute = canExecute;
        }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
    }

    public class LevelLineView : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public double Elevation { get; set; }
        private double _y;
        public double Y { get => _y; set { if (Math.Abs(_y - value) > double.Epsilon) { _y = value; OnPropertyChanged(nameof(Y)); OnPropertyChanged(nameof(LabelY)); } } }
        public double X1 { get; set; }
        public double X2 { get; set; }
        public double LabelX { get; set; }
        public double LabelY { get; set; }
        private bool _visible = true;
        public bool Visible { get => _visible; set { if (_visible != value) { _visible = value; OnPropertyChanged(nameof(Visible)); } } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ===== 內部輔助：分色與佈局 =====
    public partial class SchematicViewModel
    {
        private static readonly Brush[] Palette = new Brush[]
        {
            (Brush)new SolidColorBrush(Color.FromRgb(0x2E,0x86,0xC1)), // 藍
            (Brush)new SolidColorBrush(Color.FromRgb(0x28,0xA7,0x45)), // 綠
            (Brush)new SolidColorBrush(Color.FromRgb(0xE6,0x7E,0x22)), // 橘
            (Brush)new SolidColorBrush(Color.FromRgb(0x8E,0x44,0xAD)), // 紫
            (Brush)new SolidColorBrush(Color.FromRgb(0xC0,0x39,0x2B)), // 紅
            (Brush)new SolidColorBrush(Color.FromRgb(0x16,0xA0,0x85))  // 青
        };

        private static Brush GetBrushByIfcType(string ifcType)
        {
            if (string.IsNullOrWhiteSpace(ifcType)) return Palette[0];
            var t = ifcType.ToLowerInvariant();
            if (t.Contains("pipesegment")) return Palette[0];
            if (t.Contains("flowfitting") || t.Contains("fitting")) return Palette[1];
            if (t.Contains("valve")) return Palette[2];
            if (t.Contains("pump") || t.Contains("compressor")) return Palette[3];
            if (t.Contains("fan") || t.Contains("duct")) return Palette[4];
            // fallback: hash to palette
            int h = Math.Abs(t.GetHashCode());
            return Palette[h % Palette.Length];
        }

        private static Brush GetDarkerBrush(Brush brush)
        {
            if (brush is SolidColorBrush scb)
            {
                byte d(byte v) => (byte)(v * 0.7);
                var c = scb.Color;
                return new SolidColorBrush(Color.FromRgb(d(c.R), d(c.G), d(c.B)));
            }
            return Brushes.DarkSlateGray;
        }

        private static void ApplyForceDirectedLayout(IList<SchematicNodeView> nodes, IList<SchematicEdgeView> edges, int iterations = 200)
        {
            if (nodes.Count == 0) return;
            // 參考 Fruchterman-Reingold：k = sqrt(area / n)
            // 先估一個初始區域大小
            double minX = nodes.Min(n => n.X), maxX = nodes.Max(n => n.X);
            double minY = nodes.Min(n => n.Y), maxY = nodes.Max(n => n.Y);
            double width = Math.Max(1, maxX - minX);
            double height = Math.Max(1, maxY - minY);
            double area = width * height;
            if (double.IsInfinity(area) || area <= 0) { area = Math.Max(1, nodes.Count) * 1000.0; width = height = Math.Sqrt(area); }
            double k = Math.Sqrt(area / Math.Max(1, nodes.Count));
            double temperature = Math.Max(width, height) / 10.0;

            var disp = new (double dx, double dy)[nodes.Count];
            var index = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);

            double Repulsive(double dist) => (k * k) / Math.Max(0.001, dist);
            double Attractive(double dist) => (dist * dist) / k;

            for (int it = 0; it < iterations; it++)
            {
                // reset
                for (int i = 0; i < nodes.Count; i++) disp[i] = (0, 0);

                // repulsive forces
                for (int i = 0; i < nodes.Count; i++)
                {
                    for (int j = i + 1; j < nodes.Count; j++)
                    {
                        var v = nodes[i]; var u = nodes[j];
                        double dx = v.X - u.X; double dy = v.Y - u.Y;
                        double dist = Math.Sqrt(dx * dx + dy * dy) + 0.001;
                        double f = Repulsive(dist);
                        double rx = (dx / dist) * f;
                        double ry = (dy / dist) * f;
                        disp[i] = (disp[i].dx + rx, disp[i].dy + ry);
                        disp[j] = (disp[j].dx - rx, disp[j].dy - ry);
                    }
                }

                // attractive forces (edges)
                foreach (var e in edges)
                {
                    var v = e.Start; var u = e.End;
                    if (v == null || u == null) continue;
                    int iv = index[v], iu = index[u];
                    double dx = v.X - u.X; double dy = v.Y - u.Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy) + 0.001;
                    double f = Attractive(dist);
                    double ax = (dx / dist) * f;
                    double ay = (dy / dist) * f;
                    disp[iv] = (disp[iv].dx - ax, disp[iv].dy - ay);
                    disp[iu] = (disp[iu].dx + ax, disp[iu].dy + ay);
                }

                // limit by temperature and update positions
                for (int i = 0; i < nodes.Count; i++)
                {
                    var v = nodes[i];
                    double dx = disp[i].dx; double dy = disp[i].dy;
                    double dispLen = Math.Sqrt(dx * dx + dy * dy);
                    if (dispLen > 0)
                    {
                        double lim = Math.Min(dispLen, temperature);
                        v.X += dx / dispLen * lim;
                        v.Y += dy / dispLen * lim;
                    }
                }

                // cool
                temperature *= 0.95;
                if (temperature < 0.01) break;
            }

            // 佈局後不在此處規模化，交由 FitToCanvas 統一處理
        }

        private static void FitToCanvas(IList<SchematicNodeView> nodes, double canvasWidth, double canvasHeight, double padding)
        {
            if (nodes == null || nodes.Count == 0) return;
            double minX = nodes.Min(n => n.X);
            double maxX = nodes.Max(n => n.X);
            double minY = nodes.Min(n => n.Y);
            double maxY = nodes.Max(n => n.Y);
            double width = Math.Max(1e-9, maxX - minX);
            double height = Math.Max(1e-9, maxY - minY);

            double targetW = Math.Max(1, canvasWidth - 2 * padding);
            double targetH = Math.Max(1, canvasHeight - 2 * padding);
            double scale = Math.Min(targetW / width, targetH / height);
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;

            foreach (var v in nodes)
            {
                double nx = (v.X - minX) * scale + padding;
                double ny = (v.Y - minY) * scale + padding;
                v.X = nx;
                v.Y = ny;
                if (v.Node != null) v.Node.Position2D = new Point(nx, ny);
            }
        }
        // ===== 2D 後處理（短邊段、端點吸附/節點合併、與 ThroughFitting 虛線整合）=====
        private void Apply2DPostprocess()
        {
            try
            {
                if (Nodes.Count == 0) return;

                // 建立度數快取
                var incident = new Dictionary<SchematicNodeView, List<SchematicEdgeView>>();
                foreach (var nv in Nodes) incident[nv] = new List<SchematicEdgeView>();
                foreach (var ev in Edges)
                {
                    if (ev.Start != null) incident[ev.Start].Add(ev);
                    if (ev.End != null) incident[ev.End].Add(ev);
                }

                // 1) 節點合併（純 2D 座標合併，不改 ID/拓樸）
                double mergeTol = Math.Max(0.0, NodeMergeScreenDistancePx);
                if (mergeTol > 0.0)
                {
                    // 以 PipeSegment 端點優先為錨點
                    int Deg(SchematicNodeView v) => (incident.TryGetValue(v, out var list) ? list.Count : 0);
                    bool IsPipeSeg(SchematicNodeView v) => v?.Node?.IsFromPipeSegment == true;
                    var ordered = Nodes.OrderByDescending(IsPipeSeg).ThenByDescending(Deg).ToList();
                    var used = new HashSet<SchematicNodeView>();
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        var a = ordered[i];
                        if (used.Contains(a)) continue;
                        for (int j = i + 1; j < ordered.Count; j++)
                        {
                            var b = ordered[j];
                            if (used.Contains(b)) continue;
                            double dx = a.X - b.X, dy = a.Y - b.Y;
                            if ((dx * dx + dy * dy) <= mergeTol * mergeTol)
                            {
                                // 將 b 吸附到 a
                                b.X = a.X; b.Y = a.Y;
                                b.Node.Position2D = new Point(b.X, b.Y);
                                used.Add(b);
                            }
                        }
                    }
                }

                // 2) 端點吸附（以邊端點為中心，若另一端點距離在閾值內，則兩者對齊）
                double snapTol = Math.Max(0.0, EndpointSnapDistancePx);
                if (snapTol > 0.0)
                {
                    foreach (var ev in Edges)
                    {
                        var s = ev.Start; var t = ev.End; if (s == null || t == null) continue;
                        double dx = s.X - t.X, dy = s.Y - t.Y;
                        if ((dx * dx + dy * dy) <= snapTol * snapTol)
                        {
                            // 以度數較高者為基準
                            int ds = incident.TryGetValue(s, out var ls) ? ls.Count : 0;
                            int dt = incident.TryGetValue(t, out var lt) ? lt.Count : 0;
                            var anchor = ds >= dt ? s : t;
                            var follower = ds >= dt ? t : s;
                            follower.X = anchor.X; follower.Y = anchor.Y;
                            follower.Node.Position2D = new Point(follower.X, follower.Y);
                        }
                    }
                }

                // 3) 短邊段處理與 ThroughFitting 虛線整合
                double minLen = Math.Max(0.0, MinSegmentScreenLengthPx);
                if (HideTinyStubs && minLen > 0.0)
                {
                    // 3.1 一般短邊：直接隱藏（視覺層，不改拓樸）
                    foreach (var ev in Edges)
                    {
                        double dx = (ev.End?.X ?? 0) - (ev.Start?.X ?? 0);
                        double dy = (ev.End?.Y ?? 0) - (ev.Start?.Y ?? 0);
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        // 不隱藏 Rewired（虛線）邊，避免誤殺穿越配件連線
                        if (len < minLen)
                        {
                            if (ev.Edge?.Origin == Models.SchematicEdge.EdgeOriginKind.Rewired)
                                ev.Visible = true; // 保持可見
                            else
                                ev.Visible = false;
                        }
                    }

                    // 3.2 配件度數=2 且兩端皆短 → 若存在 Rewired 邊，隱藏兩段 stub
                    bool IsFitting(SchematicNodeView v)
                    {
                        var t = v?.Node?.IfcType ?? string.Empty;
                        return t.IndexOf("PipeFitting", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("FlowFitting", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    var rewiredPairs = new HashSet<(SchematicNodeView a, SchematicNodeView b)>(
                        Edges.Where(e => e.Edge?.Origin == Models.SchematicEdge.EdgeOriginKind.Rewired && e.Start != null && e.End != null)
                             .Select(e => (a: e.Start, b: e.End))
                    );

                    foreach (var nv in Nodes)
                    {
                        if (!IsFitting(nv)) continue;
                        if (!incident.TryGetValue(nv, out var inc) || inc.Count != 2) continue;
                        var e1 = inc[0]; var e2 = inc[1];
                        double l1 = Math.Sqrt(Math.Pow((e1.End.X - e1.Start.X), 2) + Math.Pow((e1.End.Y - e1.Start.Y), 2));
                        double l2 = Math.Sqrt(Math.Pow((e2.End.X - e2.Start.X), 2) + Math.Pow((e2.End.Y - e2.Start.Y), 2));
                        if (l1 >= minLen || l2 >= minLen) continue;

                        // 找出兩端的「非配件」鄰居
                        SchematicNodeView other1 = ReferenceEquals(e1.Start, nv) ? e1.End : e1.Start;
                        SchematicNodeView other2 = ReferenceEquals(e2.Start, nv) ? e2.End : e2.Start;
                        if (other1 == null || other2 == null) continue;

                        bool hasRewired = rewiredPairs.Any(p =>
                            (ReferenceEquals(p.a, other1) && ReferenceEquals(p.b, other2)) ||
                            (ReferenceEquals(p.a, other2) && ReferenceEquals(p.b, other1))
                        );
                        if (hasRewired)
                        {
                            e1.Visible = false; e2.Visible = false;
                        }
                    }

                    // 3.3 安全網：確保所有 Rewired 邊保持可見（即便極短）
                    foreach (var ev in Edges)
                    {
                        if (ev.Edge?.Origin == Models.SchematicEdge.EdgeOriginKind.Rewired)
                            ev.Visible = true;
                    }
                }
            }
            catch { }
        }

        // 建立樓層線視圖（需要在節點座標最終定案之後呼叫）
        private void BuildLevelLines(SchematicData data)
        {
            try
            {
                LevelLines.Clear();
                if (data?.Levels == null || data.Levels.Count == 0 || Nodes.Count == 0) return;

                // 使用 3D Z 與目前 2D Y 的線性對應（最小平方法）
                var pts = Nodes.Select(n => (Z: n.Node.Position3D.Z, Y: n.Y)).ToList();
                double m = pts.Count;
                double sumZ = pts.Sum(p => p.Z);
                double sumY = pts.Sum(p => p.Y);
                double sumZZ = pts.Sum(p => p.Z * p.Z);
                double sumZY = pts.Sum(p => p.Z * p.Y);
                double denom = m * sumZZ - sumZ * sumZ;
                double a = 0, b = 0; // Y ≈ a*Z + b
                if (Math.Abs(denom) > 1e-9)
                {
                    a = (m * sumZY - sumZ * sumY) / denom;
                    b = (sumY - a * sumZ) / m;
                }
                else
                {
                    // 退化：以 Z/Y 範圍比例對應
                    double minZ = Nodes.Min(v => v.Node.Position3D.Z);
                    double maxZ = Nodes.Max(v => v.Node.Position3D.Z);
                    double minY = Nodes.Min(v => v.Y);
                    double maxY = Nodes.Max(v => v.Y);
                    double rz = Math.Max(1e-9, maxZ - minZ);
                    double ry = Math.Max(1e-9, maxY - minY);
                    a = ry / rz;
                    b = minY - a * minZ;
                }

                double contentMinX = Nodes.Min(v => v.X);
                double contentMaxX = Nodes.Max(v => v.X);
                double labelX = contentMinX + 6; // 文字靠左側

                foreach (var lv in data.Levels.OrderBy(l => l.Elevation))
                {
                    double y = a * lv.Elevation + b;
                    LevelLines.Add(new LevelLineView
                    {
                        Name = string.IsNullOrWhiteSpace(lv.Name) ? $"Level {lv.Elevation:0.##}" : lv.Name,
                        Elevation = lv.Elevation,
                        Y = y,
                        X1 = contentMinX,
                        X2 = contentMaxX,
                        LabelX = labelX,
                        LabelY = y - 10,
                        Visible = LevelsVisible
                    });
                }
            }
            catch { }
        }

    private enum ProjectionPlane { XY, XZ, YZ }

        // 以 3D 座標自動選擇最佳投影面：
        // 策略：計算 X/Y/Z 三軸的跨度 (range)；選擇跨度最小的軸作為「厚度」方向，捨棄之，
        // 以其餘兩軸的平面作為投影面，然後適配畫布。
        private static void FitToCanvasBestProjection(IList<SchematicNodeView> nodes, double canvasWidth, double canvasHeight, double padding)
        {
            if (nodes == null || nodes.Count == 0) return;

            // 1) 取得 3D 範圍
            double minX = nodes.Min(n => n.Node.Position3D.X);
            double maxX = nodes.Max(n => n.Node.Position3D.X);
            double minY = nodes.Min(n => n.Node.Position3D.Y);
            double maxY = nodes.Max(n => n.Node.Position3D.Y);
            double minZ = nodes.Min(n => n.Node.Position3D.Z);
            double maxZ = nodes.Max(n => n.Node.Position3D.Z);

            // 2) 計算三軸的跨度（range），選擇跨度最小的軸捨棄
            double rangeX = Math.Max(1e-12, maxX - minX);
            double rangeY = Math.Max(1e-12, maxY - minY);
            double rangeZ = Math.Max(1e-12, maxZ - minZ);

            // 選擇兩個跨度較大的軸組成平面；若相等，偏好 XY → XZ → YZ
            ProjectionPlane plane;
            double chosenWidth, chosenHeight;
            double minU, minV; // 與 plane 對應之軸的最小值

            if (rangeX <= rangeY && rangeX <= rangeZ)
            {
                // 捨棄 X，投影 YZ
                plane = ProjectionPlane.YZ;
                chosenWidth = rangeY; chosenHeight = rangeZ;
                minU = minY; minV = minZ;
            }
            else if (rangeY <= rangeX && rangeY <= rangeZ)
            {
                // 捨棄 Y，投影 XZ
                plane = ProjectionPlane.XZ;
                chosenWidth = rangeX; chosenHeight = rangeZ;
                minU = minX; minV = minZ;
            }
            else
            {
                // 捨棄 Z，投影 XY
                plane = ProjectionPlane.XY;
                chosenWidth = rangeX; chosenHeight = rangeY;
                minU = minX; minV = minY;
            }

            // 4) 適配畫布（等比縮放 + 邊距）
            double targetW = Math.Max(1, canvasWidth - 2 * padding);
            double targetH = Math.Max(1, canvasHeight - 2 * padding);
            double scale = Math.Min(targetW / Math.Max(chosenWidth, 1e-12), targetH / Math.Max(chosenHeight, 1e-12));
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1.0;

            foreach (var v in nodes)
            {
                var p = v.Node.Position3D;
                double u, w;
                switch (plane)
                {
                    case ProjectionPlane.XZ:
                        u = p.X; w = p.Z; break;
                    case ProjectionPlane.YZ:
                        u = p.Y; w = p.Z; break;
                    default: // XY
                        u = p.X; w = p.Y; break;
                }

                double newX = (u - minU) * scale + padding;
                double newY = (w - minV) * scale + padding;
                v.Node.Position2D = new Point(newX, newY);
                v.X = newX;
                v.Y = newY;
            }
        }

        // 對外公開：重置視圖，依 Canvas 尺寸重新 Fit
        public void RefitToCanvas()
        {
            FitToCanvas(Nodes, CanvasWidth, CanvasHeight, CanvasPadding);
        }

        // 對外公開：重新跑力導向佈局，必要時再 FitToCanvas
        public void Relayout(int iterations = 200, bool refit = true)
        {
            ApplyForceDirectedLayout(Nodes.ToList(), Edges.ToList(), iterations);
            if (refit) FitToCanvas(Nodes, CanvasWidth, CanvasHeight, CanvasPadding);
        }

        // ===== P3: 系統過濾 =====
        public class SystemFilterOption : INotifyPropertyChanged
        {
            private bool _isChecked = true;
            // Key 用於邏輯比對（以縮寫為主，無則用全名，再無則未指定）
            public string Key { get; set; } = string.Empty;
            // Display 顯示：縮寫 – 全名（盡量完整）
            public string Display { get; set; } = string.Empty;
            public bool IsChecked
            {
                get => _isChecked;
                set { if (_isChecked != value) { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
            }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private const string UnassignedSystem = "(未指定)";

        private void BuildSystemFiltersFrom(SchematicData data)
        {
            try
            {
                Systems.Clear();
                // 彙整（key=縮寫或名稱；display=縮寫 – 全名）
                var map = new Dictionary<string, (string abbr, string name)>(StringComparer.OrdinalIgnoreCase);
                void addEntry(string? abbr, string? name)
                {
                    string key = !string.IsNullOrWhiteSpace(abbr)
                        ? abbr!.Trim()
                        : (!string.IsNullOrWhiteSpace(name) ? name!.Trim() : UnassignedSystem);
                    string a = abbr?.Trim() ?? string.Empty;
                    string n = name?.Trim() ?? string.Empty;
                    if (map.TryGetValue(key, out var existing))
                    {
                        // 補全缺漏（例如原本只有縮寫，遇到含全名的則合併）
                        var na = string.IsNullOrWhiteSpace(existing.abbr) ? a : existing.abbr;
                        var nn = string.IsNullOrWhiteSpace(existing.name) ? n : existing.name;
                        map[key] = (na, nn);
                    }
                    else
                    {
                        map[key] = (a, n);
                    }
                }

                foreach (var n in data.Nodes) addEntry(n.SystemAbbreviation, n.SystemName);
                foreach (var e in data.Edges) addEntry(e.SystemAbbreviation, e.SystemName);

                foreach (var kv in map.OrderBy(k => k.Key))
                {
                    var (abbr, name) = kv.Value;
                    string disp = !string.IsNullOrWhiteSpace(abbr) && !string.IsNullOrWhiteSpace(name)
                        ? $"{abbr} – {name}"
                        : (!string.IsNullOrWhiteSpace(abbr) ? abbr : (!string.IsNullOrWhiteSpace(name) ? name : UnassignedSystem));
                    // 預設只勾選 SWP / VP / WP 三類，其餘不勾選（可於 UI 再調整）
                    var preset = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SWP", "VP", "WP" };
                    bool isChecked = preset.Contains(kv.Key);
                    var opt = new SystemFilterOption { Key = kv.Key, Display = disp, IsChecked = isChecked };
                    opt.PropertyChanged += (_, __) => ApplySystemVisibility();
                    Systems.Add(opt);
                }
            }
            catch { }
        }

        private void ApplySystemVisibility()
        {
            try
            {
                if (Systems.Count == 0)
                {
                    foreach (var nv in Nodes) nv.Visible = true;
                    foreach (var ev in Edges) ev.Visible = true;
                    // 仍可能需要再套 ShowOnlyRewired
                    if (!ShowOnlyRewired) return;
                }
                var allowed = Systems.Where(s => s.IsChecked).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var nv in Nodes)
                {
                    string key = !string.IsNullOrWhiteSpace(nv.Node?.SystemAbbreviation)
                        ? nv.Node!.SystemAbbreviation!
                        : (!string.IsNullOrWhiteSpace(nv.Node?.SystemName) ? nv.Node!.SystemName! : UnassignedSystem);
                    nv.Visible = allowed.Contains(key);
                }
                foreach (var ev in Edges)
                {
                    // 邊要同時考量兩端節點是否可見
                    bool endpointsVisible = (ev.Start?.Visible != false) && (ev.End?.Visible != false);
                    // Rewired 邊：以端點可見為準，避免因邊缺少/不同系統而被誤隱藏
                    if (ev.Edge?.Origin == Models.SchematicEdge.EdgeOriginKind.Rewired)
                    {
                        ev.Visible = endpointsVisible;
                        continue;
                    }

                    string key = !string.IsNullOrWhiteSpace(ev.Edge?.SystemAbbreviation)
                        ? ev.Edge!.SystemAbbreviation!
                        : (!string.IsNullOrWhiteSpace(ev.Edge?.SystemName) ? ev.Edge!.SystemName! : UnassignedSystem);
                    ev.Visible = allowed.Contains(key) && endpointsVisible;
                }

                // 若只顯示 Rewired：將非 Rewired 邊隱藏，且節點只保留與可見 Rewired 邊相連者
                if (ShowOnlyRewired)
                {
                    // 隱藏所有非 Rewired 邊；保留已判定可見的 Rewired 邊
                    foreach (var ev in Edges)
                    {
                        if (ev.Edge?.Origin != Models.SchematicEdge.EdgeOriginKind.Rewired)
                            ev.Visible = false;
                    }
                    // 節點：僅保留為「任何可見 Rewired 邊」之端點，且維持其原本可見狀態條件
                    var nodesWithRewired = new HashSet<SchematicNodeView>();
                    foreach (var ev in Edges)
                    {
                        if (!ev.Visible) continue;
                        if (ev.Edge?.Origin != Models.SchematicEdge.EdgeOriginKind.Rewired) continue;
                        if (ev.Start != null) nodesWithRewired.Add(ev.Start);
                        if (ev.End != null) nodesWithRewired.Add(ev.End);
                    }
                    foreach (var nv in Nodes)
                    {
                        nv.Visible = nv.Visible && nodesWithRewired.Contains(nv);
                    }
                }
            }
            catch { }
        }

        // 提供外部（View/對話窗）在更新勾選後呼叫以套用可見性
        public void ApplySystemVisibilityNow()
        {
            ApplySystemVisibility();
        }

        // ===== Run 分組：依系統 + 尺寸 + （視模式）樓層 + 幾何相連 =====
        private void ComputeRuns()
        {
            if (Edges.Count == 0) return;
            double tol = Math.Max(0.0, GeometryTolerancePx);

            // 快取端點座標
            var nodePt = Nodes.ToDictionary(n => n, n => new Point(n.X, n.Y));

            string SysKey(SchematicEdgeView ev)
            {
                var ab = ev.Edge?.SystemAbbreviation; var nm = ev.Edge?.SystemName;
                return !string.IsNullOrWhiteSpace(ab) ? ab!.Trim() : (!string.IsNullOrWhiteSpace(nm) ? nm!.Trim() : UnassignedSystem);
            }
            // 分組改為僅以 System 做分桶，忽略 Size 與 Level

            var buckets = new Dictionary<string, List<SchematicEdgeView>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ev in Edges)
            {
                var sys = SysKey(ev);
                var key = sys; // 僅以 System 分桶
                if (!buckets.TryGetValue(key, out var list)) { list = new List<SchematicEdgeView>(); buckets[key] = list; }
                list.Add(ev);
            }

            int nextRunId = 1;
            foreach (var kv in buckets)
            {
                var list = kv.Value;
                if (list.Count == 0) continue;

                // 節點集合
                var endpoints = new List<SchematicNodeView>();
                foreach (var ev in list)
                { if (ev.Start != null) endpoints.Add(ev.Start); if (ev.End != null) endpoints.Add(ev.End); }
                endpoints = endpoints.Distinct().ToList();

                var index = endpoints.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
                var parent = Enumerable.Range(0, endpoints.Count).ToArray();
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[rb] = ra; }

                // 端點鄰近（<= tol）合併
                for (int i = 0; i < endpoints.Count; i++)
                {
                    var pi = nodePt[endpoints[i]];
                    for (int j = i + 1; j < endpoints.Count; j++)
                    {
                        var pj = nodePt[endpoints[j]];
                        var dx = pi.X - pj.X; var dy = pi.Y - pj.Y;
                        if (dx * dx + dy * dy <= tol * tol) Union(i, j);
                    }
                }
                // 同條邊兩端也聯集
                foreach (var ev in list)
                {
                    if (ev.Start == null || ev.End == null) continue;
                    Union(index[ev.Start], index[ev.End]);
                }

                // 指派 RunId 以集合代表為鍵
                var rep2Run = new Dictionary<int, int>();
                foreach (var ev in list)
                {
                    if (ev.Start == null) continue;
                    int rep = Find(index[ev.Start]);
                    if (!rep2Run.TryGetValue(rep, out var run)) { run = nextRunId++; rep2Run[rep] = run; }
                    ev.Edge.RunId = run;
                }
            }

            // 通知 UI（屬性面板）
            foreach (var ev in Edges) { ev.Edge = ev.Edge; }

            if (ColorByRunId) ApplyRunColorsAndLegend();
        }

        private void ApplyRunColorsAndLegend()
        {
            try
            {
                var groups = Edges.Where(e => e.Edge?.RunId != null)
                                   .GroupBy(e => e.Edge!.RunId!.Value)
                                   .OrderBy(g => g.Key)
                                   .ToList();
                // 著色
                foreach (var g in groups)
                {
                    var brush = BrushForRunId(g.Key);
                    foreach (var e in g) e.EdgeBrush = brush;
                }
                foreach (var e in Edges.Where(e => e.Edge?.RunId == null)) e.EdgeBrush = PipeEdgeBrush;

                // 圖例
                RunLegend.Clear();
                foreach (var g in groups)
                {
                    RunLegend.Add(new RunLegendItem { RunId = g.Key, Color = BrushForRunId(g.Key), Count = g.Count() });
                }
                RefreshRunLegendView();
            }
            catch { }
        }

        private int _runLegendTotalPages = 1;
        public int RunLegendTotalPages { get => _runLegendTotalPages; private set { if (_runLegendTotalPages != value) { _runLegendTotalPages = Math.Max(1, value); OnPropertyChanged(nameof(RunLegendTotalPages)); } } }
        private int _runLegendTotalItems = 0;
        public int RunLegendTotalItems { get => _runLegendTotalItems; private set { if (_runLegendTotalItems != value) { _runLegendTotalItems = Math.Max(0, value); OnPropertyChanged(nameof(RunLegendTotalItems)); } } }

        private void RefreshRunLegendView()
        {
            try
            {
                IEnumerable<RunLegendItem> all = RunLegend;
                // 排序
                all = RunLegendSort == RunLegendSortMode.ByCountDesc
                    ? all.OrderByDescending(x => x.Count).ThenBy(x => x.RunId)
                    : all.OrderBy(x => x.RunId);

                // 計算總頁數
                var total = all.Count();
                RunLegendTotalItems = total;
                int pageSize = Math.Max(1, RunLegendPageSize);
                int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
                RunLegendTotalPages = totalPages;
                if (RunLegendPage > totalPages) RunLegendPage = totalPages;
                if (RunLegendPage < 1) RunLegendPage = 1;

                // 分頁
                var page = RunLegendPage;
                var src = all.Skip((page - 1) * pageSize).Take(pageSize);

                RunLegendView.Clear();
                foreach (var it in src) RunLegendView.Add(it);
            }
            catch { }
        }

        private void ClearRunColorsToDefault()
        {
            foreach (var e in Edges) e.EdgeBrush = PipeEdgeBrush;
        }

        private static readonly Brush[] RunPalette = new Brush[]
        {
            new SolidColorBrush(Color.FromRgb(0x1f,0x77,0xb4)), // blue
            new SolidColorBrush(Color.FromRgb(0xff,0x7f,0x0e)), // orange
            new SolidColorBrush(Color.FromRgb(0x2c,0xa0,0x2c)), // green
            new SolidColorBrush(Color.FromRgb(0xd6,0x27,0x28)), // red
            new SolidColorBrush(Color.FromRgb(0x94,0x67,0xbd)), // purple
            new SolidColorBrush(Color.FromRgb(0x8c,0x56,0x4b)), // brown
            new SolidColorBrush(Color.FromRgb(0xe3,0x77,0xc2)), // pink
            new SolidColorBrush(Color.FromRgb(0x7f,0x7f,0x7f)), // gray
            new SolidColorBrush(Color.FromRgb(0xbc,0xbd,0x22)), // olive
            new SolidColorBrush(Color.FromRgb(0x17,0xbe,0xcf))  // cyan
        };
        private static Brush BrushForRunId(int runId)
        {
            if (runId <= 0) return Brushes.DarkSlateGray;
            int idx = (runId - 1) % RunPalette.Length;
            return RunPalette[idx];
        }

        private void ExportRunsToCsv()
        {
            var runs = Edges.Where(e => e.Edge?.RunId != null).GroupBy(e => e.Edge!.RunId!.Value);
            if (!runs.Any()) { AddLog("[Run] 無可匯出的分組（請先執行 Run 分組）"); return; }

            var rows = new List<string>();
            rows.Add("RunId,System,Size,Floors,Length,Count");
            string Safe(string? s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Replace("\"", "\"\"");

            foreach (var g in runs.OrderBy(g => g.Key))
            {
                int runId = g.Key;
                var first = g.First();
                string sys = !string.IsNullOrWhiteSpace(first.Edge.SystemAbbreviation) ? first.Edge.SystemAbbreviation! : (first.Edge.SystemName ?? string.Empty);
                string size = FormatPipeSizeLabel(first.Edge) ?? string.Empty;
                var floors = g.Select(e => e.Edge.LevelName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s).ToList();
                string floorsStr = string.Join("/", floors);
                double totalLen = 0.0;
                foreach (var e in g)
                {
                    var s = e.Start?.Node?.Position3D; var t = e.End?.Node?.Position3D;
                    if (s != null && t != null)
                    {
                        double dx = t.Value.X - s.Value.X;
                        double dy = t.Value.Y - s.Value.Y;
                        double dz = t.Value.Z - s.Value.Z;
                        totalLen += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    }
                }
                int count = g.Count();
                rows.Add($"{runId},\"{Safe(sys)}\",\"{Safe(size)}\",\"{Safe(floorsStr)}\",{totalLen:0.###},{count}");
            }

            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "匯出 Run 分組 CSV",
                    Filter = "CSV 檔 (*.csv)|*.csv|所有檔案 (*.*)|*.*",
                    FileName = $"runs_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };
                if (dlg.ShowDialog() == true)
                {
                    File.WriteAllLines(dlg.FileName, rows);
                    AddLog($"[Run] 已匯出 CSV: {dlg.FileName}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"[Run] 匯出失敗: {ex.Message}");
            }
        }
    }
}
