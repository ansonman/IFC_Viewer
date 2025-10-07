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
        private readonly SchematicService _service;

        public ObservableCollection<SchematicNodeView> Nodes { get; } = new();
    public ObservableCollection<SchematicEdgeView> Edges { get; } = new();
    public ObservableCollection<LevelLineView> LevelLines { get; } = new();
    // P3: 系統過濾選項
    public ObservableCollection<SystemFilterOption> Systems { get; } = new();
        // V1: 日誌文字顯示
        public ObservableCollection<string> Logs { get; } = new();
        // 右側屬性面板：選取與屬性
        public ObservableCollection<PropertyEntry> Properties { get; } = new();
        public string SelectedTitle { get; private set; } = "(未選取)";

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
    // Phase 0: 顏色切換命令
    public ICommand CycleTerminalColorCommand { get; }
    public ICommand CyclePipeColorCommand { get; }
    public ICommand CyclePipeNodeColorCommand { get; }
    public ICommand CyclePipeEdgeColorCommand { get; }

    // 視覺預設
    private double _defaultNodeSize = 8.0; // px
    private double _defaultEdgeThickness = 2.0; // px
    public bool SnapEnabled { get; private set; } = true;
    public bool LevelsVisible { get; private set; } = true;

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
    private Brush _pipeEdgeBrush = Brushes.DarkSlateGray;
    public Brush PipeEdgeBrush { get => _pipeEdgeBrush; set { if (_pipeEdgeBrush != value) { _pipeEdgeBrush = value; OnPropertyChanged(nameof(PipeEdgeBrush)); } } }

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
    }

        public SchematicViewModel(SchematicService service, ISelectionService? selection = null)
        {
            _service = service;
            _selection = selection;
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
            var data = await _service.GenerateTopologyAsync(model);
            LoadData(data);
        }

        public Task LoadFromDataAsync(SchematicData data)
        {
            LoadData(data);
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
                var nv = new SchematicNodeView
                {
                    Node = n,
                    X = n.Position2D.X,
                    Y = n.Position2D.Y,
                    NodeBrush = (n.IfcType?.IndexOf("FlowTerminal", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? TerminalBrush
                        : PipeNodeBrush, // 管段端點以 Pipe 色表示，Terminal 用 Terminal 色
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
                Edges.Add(ev);
            }

            ApplyForceDirectedLayout(Nodes.ToList(), Edges.ToList(), iterations: 200);
            FitToCanvas(Nodes, CanvasWidth, CanvasHeight, CanvasPadding);
            if (SnapEnabled) SnapToPixelGrid(Nodes);
            LogVisualSettings();
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
            foreach (var ev in Edges)
            {
                ev.EdgeBrush = PipeEdgeBrush;
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
                    PipeEdge = BrushToHex(PipeEdgeBrush)
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
                var term = HexToBrush(obj.Terminal, TerminalBrush);
                var pn = HexToBrush(obj.PipeNode, PipeNodeBrush);
                var pe = HexToBrush(obj.PipeEdge, PipeEdgeBrush);
                TerminalBrush = term; PipeNodeBrush = pn; PipeEdgeBrush = pe;
                ApplyCurrentColorsToViews();
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
        public SchematicNodeView Start { get; set; } = null!;
        public SchematicNodeView End { get; set; } = null!;
        private Brush _edgeBrush = Brushes.DarkSlateGray;
        public Brush EdgeBrush { get => _edgeBrush; set { if (_edgeBrush != value) { _edgeBrush = value; OnPropertyChanged(nameof(EdgeBrush)); } } }
        private double _thickness = 2.0;
        public double Thickness { get => _thickness; set { if (Math.Abs(_thickness - value) > double.Epsilon) { _thickness = value; OnPropertyChanged(nameof(Thickness)); } } }
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } } }
    private bool _visible = true;
    public bool Visible { get => _visible; set { if (_visible != value) { _visible = value; OnPropertyChanged(nameof(Visible)); } } }

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

            double width = Math.Max(1e-6, maxX - minX);
            double height = Math.Max(1e-6, maxY - minY);

            double targetW = Math.Max(1, canvasWidth - 2 * padding);
            double targetH = Math.Max(1, canvasHeight - 2 * padding);
            double scale = Math.Min(targetW / width, targetH / height);
            if (double.IsInfinity(scale) || double.IsNaN(scale) || scale <= 0) scale = 1.0;

            foreach (var v in nodes)
            {
                v.X = (v.X - minX) * scale + padding;
                v.Y = (v.Y - minY) * scale + padding;
            }
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
                    var opt = new SystemFilterOption { Key = kv.Key, Display = disp, IsChecked = true };
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
                    return;
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
                    string key = !string.IsNullOrWhiteSpace(ev.Edge?.SystemAbbreviation)
                        ? ev.Edge!.SystemAbbreviation!
                        : (!string.IsNullOrWhiteSpace(ev.Edge?.SystemName) ? ev.Edge!.SystemName! : UnassignedSystem);
                    // 邊要同時考量兩端節點是否可見
                    ev.Visible = allowed.Contains(key) && (ev.Start?.Visible != false) && (ev.End?.Visible != false);
                }
            }
            catch { }
        }

        // 提供外部（View/對話窗）在更新勾選後呼叫以套用可見性
        public void ApplySystemVisibilityNow()
        {
            ApplySystemVisibility();
        }
    }
}
