using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using IFC_Viewer_00.Models;
using IFC_Viewer_00.Services;
using System;
using System.IO;
using System.Linq;
using System.Collections.Specialized;
using System.Windows.Threading;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Media3D;
using IFC_Viewer_00.Views;
using IfcSchemaViewer.Views;

namespace IFC_Viewer_00.ViewModels
{

    public partial class MainViewModel : ObservableObject
    {
    private readonly IViewer3DService _viewer3D;
    private readonly ISelectionService _selection;
        // 顯示 3D Overlay 時的透明度快照（用於清除時還原）
        private double? _opacityBeforeOverlay;
        // 用於避免 VM 與 SelectionService 之間的無限循環
        private bool _isSyncingSelection = false;
        // Sprint 1: 3D 物件高亮
        [ObservableProperty]
    private IIfcObject? highlightedEntity;

        // Sprint 1: 物件隔離/隱藏命令
        public RelayCommand IsolateSelectionCommand { get; }
        public RelayCommand HideSelectionCommand { get; }
        public RelayCommand ShowAllCommand { get; }
        [ObservableProperty]
        private IfcStore? model;

        [ObservableProperty]
        private string? statusMessage;

    // IFC Schema Viewer 視窗（可重用）
    private IfcSchemaViewerWindow? _schemaWindow;

    // 3D 模型透明度（0~1），變更時即時套用到 3D 控制項
    [ObservableProperty]
    private double modelOpacity = 1.0;

    // 3D Overlay 視覺參數（可由 UI 調整）
    [ObservableProperty]
    private double overlayLineThickness = 2.0;

    [ObservableProperty]
    private double overlayPointSize = 3.0;

    [ObservableProperty]
    private ObservableCollection<ElementProperty> selectedElementProperties = new();

        [ObservableProperty]
    private ObservableCollection<SpatialNode> hierarchy = new();

    [ObservableProperty]
    private SpatialNode? selectedNode;

        // 任務 2：TreeView 多選集合
        public ObservableCollection<SpatialNode> SelectedNodes { get; } = new();

        public RelayCommand OpenFileCommand { get; }
    public RelayCommand GenerateSchematicCommand { get; }
    public RelayCommand GenerateASSchematicCommand { get; }
    public RelayCommand GenerateSchematicV1Command { get; }
    public RelayCommand GeneratePipeAxesCommand { get; }
    public RelayCommand ShowPipeOverlay3DCommand { get; }
    public RelayCommand ShowTestOverlay3DCommand { get; }
    public RelayCommand ShowAxesOverlay3DCommand { get; }
    public RelayCommand ClearOverlay3DCommand { get; }
    public RelayCommand ShowSchemaViewerCommand { get; }
    public RelayCommand ShowFlowTerminalAnchorsCommand { get; }

        public MainViewModel() : this(new StubViewer3DService(), new SelectionService()) { }

        public MainViewModel(IViewer3DService viewer3D, ISelectionService? selectionService = null)
        {
            _viewer3D = viewer3D;
            _selection = selectionService ?? new SelectionService();
            OpenFileCommand = new RelayCommand(async () => await OnOpenFileAsync());
            GenerateSchematicCommand = new RelayCommand(async () => await OnGenerateSchematicAsync());
            GenerateASSchematicCommand = new RelayCommand(async () => await OnGenerateASSchematicAsync());
            GenerateSchematicV1Command = new RelayCommand(async () => await OnGenerateSchematicV1Async());
            GeneratePipeAxesCommand = new RelayCommand(async () => await OnGeneratePipeAxesAsync());
            ShowPipeOverlay3DCommand = new RelayCommand(async () => await OnShowPipeOverlay3DAsync());
            ShowTestOverlay3DCommand = new RelayCommand(OnShowTestOverlay3D);
            ShowAxesOverlay3DCommand = new RelayCommand(() => OnShowAxesOverlay3D());
            ClearOverlay3DCommand = new RelayCommand(OnClearOverlay3D);
            ShowSchemaViewerCommand = new RelayCommand(OnShowSchemaViewer);
            ShowFlowTerminalAnchorsCommand = new RelayCommand(async () => await OnShowFlowTerminalAnchorsAsync());

            IsolateSelectionCommand = new RelayCommand(OnIsolateSelection);
            HideSelectionCommand = new RelayCommand(OnHideSelection);
            ShowAllCommand = new RelayCommand(OnShowAll);
            StatusMessage = "就緒";

            // TreeView 多選集合變更 → 立即同步 3D 高亮（多筆）
            SelectedNodes.CollectionChanged += OnSelectedNodesChanged;

            // 監聽全域選取變更，更新屬性面板摘要或單選詳情（避免循環）
            _selection.SelectionChanged += OnGlobalSelectionChanged;
            // 初始化預設透明度
            _viewer3D.SetModelOpacity(ModelOpacity);
        }

        // 顯示 FlowTerminal 紅點（2D Canvas，使用現有投影管線）
        private async Task OnShowFlowTerminalAnchorsAsync()
        {
            try
            {
                if (Model == null)
                {
                    StatusMessage = "尚未載入模型";
                    MessageBox.Show("尚未載入模型", "FlowTerminal 紅點", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 選擇投影平面（若對話框存在）
                string plane = "XY";
                try
                {
                    var dlg = new PlaneSelectionDialog { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
                    if (dlg.ShowDialog() == true) plane = dlg.SelectedPlane;
                }
                catch { }

                var service = new SchematicService();
                var pts3D = await service.GetFlowTerminalAnchorsAsync(Model);
                var details = service.LastFlowTerminalAnchorDetails?.ToList() ?? new List<SchematicService.FlowTerminalAnchorDetail>();
                if (pts3D == null || pts3D.Count == 0)
                {
                    MessageBox.Show("模型中沒有 IfcFlowTerminal 或無可用定位資料。", "FlowTerminal 紅點", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var metaList = new System.Collections.Generic.List<(int? portLabel, string? name, string? hostType, int? hostLabel, bool isFromPipeSegment)>();
                for (int i = 0; i < pts3D.Count; i++)
                {
                    SchematicService.FlowTerminalAnchorDetail? d = (i < details.Count) ? details[i] : null;
                    int? label = d?.TerminalLabel;
                    string? name = d?.TerminalName ?? d?.PortName;
                    string hostType = "IfcFlowTerminal";
                    int? hostLabel = d?.TerminalLabel;
                    bool isPipe = false; // FlowTerminal 非管段
                    metaList.Add((label, name, hostType, hostLabel, isPipe));
                }

                var svm = new IFC_Viewer_00.ViewModels.SchematicViewModel(service, _selection)
                {
                    CanvasWidth = 1200,
                    CanvasHeight = 800,
                    CanvasPadding = 40
                };
                svm.AddLog($"FlowTerminal 數量: {pts3D.Count}");
                svm.AddLog($"投影平面: {plane}");

                await svm.LoadPointsFrom3DAsync(pts3D, plane, metaList, flipX: false, flipY: true, tryBestIfDegenerate: true);
                var view = new SchematicView { DataContext = svm };
                view.Title = $"FlowTerminal 紅點 - {pts3D.Count} 個 ({plane})";
                view.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"FlowTerminal 紅點顯示失敗: {ex.Message}", "FlowTerminal 紅點", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        // Sprint 1: 3D 物件高亮 - 供未來擴充
    partial void OnHighlightedEntityChanged(IIfcObject? value)
        {
            // 呼叫 3D 服務高亮（Stub 為 no-op，未來替換實作）
            if (value == null) return;
            var lbl = TryGetEntityLabel(value);
            if (lbl.HasValue)
            {
                _viewer3D.HighlightEntities(new[] { lbl.Value }, clearPrevious: true);
            }
            else if (value is Xbim.Common.IPersistEntity pe)
            {
                _viewer3D.HighlightEntities(new[] { pe });
            }

            // 若 Schema 視窗已開啟，跟隨目前高亮/選取自動更新內容（不自動開新視窗）
            try
            {
                if (_schemaWindow != null)
                {
                    _schemaWindow.ShowEntity(value);
                }
            }
            catch { }
        }

        // Sprint 1: 物件隔離/隱藏命令 stub
        private void OnIsolateSelection()
        {
            var target = HighlightedEntity ?? (SelectedNode?.Entity as IIfcObject);
            _viewer3D?.Isolate(target);
            StatusMessage = target != null ? "已隔離選取項" : "沒有可隔離的選取";
        }
        private void OnHideSelection()
        {
            var target = HighlightedEntity ?? (SelectedNode?.Entity as IIfcObject);
            _viewer3D?.Hide(target, recursive: true);
            StatusMessage = target != null ? "已隱藏選取項" : "沒有可隱藏的選取";
        }
        private void OnShowAll()
        {
            _viewer3D?.ShowAll();
            StatusMessage = "已顯示全部";
        }

        // Sprint 1: 3D/TreeView 雙向連動
        public void SyncTreeViewSelection(IIfcObject entityToSelect)
        {
            Hierarchy ??= new ObservableCollection<SpatialNode>();
            var targetLabel = TryGetEntityLabel(entityToSelect);
            var targetGid = TryGetGlobalId(entityToSelect);
            var found = FindSpatialNodeByEntity(Hierarchy, entityToSelect, targetLabel, targetGid);
            if (found != null)
                SelectedNode = found;

            // 同步 SelectionService 單選
            if (targetLabel.HasValue)
            {
                _selection.SetSelection(new[] { targetLabel.Value }, SelectionOrigin.Programmatic);
            }
        }
    private SpatialNode? FindSpatialNodeByEntity(ObservableCollection<SpatialNode> nodes, IIfcObject entity, int? targetLabel, string? targetGid)
        {
            foreach (var node in nodes)
            {
                if (node.Entity is IIfcObject o)
                {
                    if (ReferenceEquals(o, entity)) return node;
                    var lbl = TryGetEntityLabel(o);
                    if (lbl.HasValue && targetLabel.HasValue && lbl.Value == targetLabel.Value) return node;
                    var gid = TryGetGlobalId(o);
                    if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(targetGid) && string.Equals(gid, targetGid, StringComparison.OrdinalIgnoreCase)) return node;
                }
                var found = FindSpatialNodeByEntity(node.Children, entity, targetLabel, targetGid);
                if (found != null) return found;
            }
            return null;
        }

        private static int? TryGetEntityLabel(IIfcObject obj)
        {
            try
            {
                if (obj is Xbim.Common.IPersistEntity pe)
                    return pe.EntityLabel;
            }
            catch { }
            try
            {
                var pi = obj.GetType().GetProperty("EntityLabel");
                if (pi != null)
                {
                    var v = pi.GetValue(obj);
                    if (v is int i) return i;
                }
            }
            catch { }
            return null;
        }

        private static string? TryGetGlobalId(IIfcObject obj)
        {
            try
            {
                if (obj is Xbim.Ifc4.Interfaces.IIfcRoot root)
                {
                    // xBIM 6: GlobalId 是 IfcGloballyUniqueId 類型
                    return IfcStringHelper.FromValue(root.GlobalId);
                }
            }
            catch { }
            return null;
        }

        private async Task OnOpenFileAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "IFC 檔案 (*.ifc;*.xbim)|*.ifc;*.xbim|所有檔案 (*.*)|*.*",
                Title = "選擇 IFC 或 xBIM 檔案"
            };
            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                await OpenFileByPathAsync(dialog.FileName);
            }
        }

        /// <summary>
        /// 直接以檔案路徑開啟 IFC/xbim（供自動化或啟動時載入）。
        /// </summary>
        public async Task OpenFileByPathAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    StatusMessage = "檔案不存在";
                    return;
                }
                await LoadModelAsync(filePath);
            }
            catch (Exception ex)
            {
                StatusMessage = "模型載入失敗";
                System.Diagnostics.Debug.WriteLine($"[IFC載入失敗] {ex}");
            }
        }

        private async Task LoadModelAsync(string filePath)
        {
            StatusMessage = "正在載入模型...";
            var loadedModel = await Task.Run(() => IfcStore.Open(filePath));
            var old = Model; // 保留舊模型以便釋放
            Model = loadedModel; // 觸發 OnModelChanged → SetModel/ResetCamera/BuildHierarchy
            old?.Dispose();
            StatusMessage = "模型載入成功！";
            // 套用當前透明度
            _viewer3D.SetModelOpacity(ModelOpacity);
        }

        // 公開方法：顯示某個 IIfcObject 的 IFC Schema 視窗
        public void ShowSchema(IIfcObject entity)
        {
            // 若視窗曾被關閉，舊實例不可再 Show；Closed 後將欄位設為 null。
            if (_schemaWindow == null)
            {
                _schemaWindow = new IfcSchemaViewerWindow
                {
                    Owner = System.Windows.Application.Current?.Windows
                        .OfType<System.Windows.Window>()
                        .FirstOrDefault(w => w.IsActive)
                };
                _schemaWindow.Closed += (_, __) => _schemaWindow = null;
            }

            // 顯示並帶到前景
            _schemaWindow.ShowEntity(entity);
        }

        // 以命令方式手動開啟 IFC Schema Viewer（優先使用目前選取/高亮的物件）
        private void OnShowSchemaViewer()
        {
            try
            {
                IIfcObject? target = HighlightedEntity ?? (SelectedNode?.Entity as IIfcObject);
                if (target != null)
                {
                    ShowSchema(target);
                    StatusMessage = $"已顯示 IFC Schema: {target.GetType().Name}";
                    return;
                }

                // 無選取：改為開啟空白視窗，不再自動挑第一個 IIfcWall/IIfcProduct
                if (_schemaWindow == null)
                {
                    _schemaWindow = new IfcSchemaViewerWindow
                    {
                        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    };
                    _schemaWindow.Closed += (_, __) => _schemaWindow = null;
                }
                _schemaWindow.Show();
                _schemaWindow.Activate();
                _schemaWindow.Topmost = true; _schemaWindow.Topmost = false;
                StatusMessage = "IFC Schema Viewer 已開啟（無選取項目）";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"開啟 IFC Schema Viewer 失敗: {ex.Message}", "IFC Schema Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Sprint 2/3：系統優先的原理圖生成與顯示
        private async Task OnGenerateSchematicAsync()
        {
            try
            {
                if (Model == null)
                {
                    StatusMessage = "尚未載入模型";
                    MessageBox.Show("尚未載入模型", "原理圖", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var service = new SchematicService();
                var list = await service.GenerateFromSystemsAsync(Model);
                if (list == null || list.Count == 0)
                {
                    MessageBox.Show("未在模型中找到可用的管線系統。", "原理圖", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                SchematicData selected;
                if (list.Count == 1)
                {
                    selected = list[0];
                }
                else
                {
                    var dlg = new SystemSelectionDialog(list) { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
                    var ok = dlg.ShowDialog();
                    if (ok != true || dlg.SelectedData == null) return;
                    selected = dlg.SelectedData;
                }

                // 顯示視窗
                     var svm = new IFC_Viewer_00.ViewModels.SchematicViewModel(service, _selection);
                         await svm.LoadFromDataAsync(selected);
                var win = new SchematicView { DataContext = svm };
                if (!string.IsNullOrWhiteSpace(selected.SystemName)) win.Title = selected.SystemName;
                win.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"原理圖產生失敗: {ex.Message}", "原理圖", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 新增：AS原理圖工作流程入口
        private Task OnGenerateASSchematicAsync()
        {
            try
            {
                if (Model == null)
                {
                    StatusMessage = "尚未載入模型";
                    MessageBox.Show("尚未載入模型", "AS原理圖", MessageBoxButton.OK, MessageBoxImage.Information);
                    return Task.CompletedTask;
                }

                // 開啟非模式選擇面板，交由面板與 SelectionService 引導使用者在 3D 中選兩段管件
                var vm = new IFC_Viewer_00.ViewModels.SelectSegmentsForASSchematicViewModel(Model, _selection);
                var win = new IFC_Viewer_00.Views.SelectSegmentsForASSchematicWindow { DataContext = vm };
                win.Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
                win.Show();

                // 非同步等待 ViewModel 完成選擇並產出結果（不阻塞 UI）
                _ = vm.WhenDone.ContinueWith(t =>
                {
                    try
                    {
                        var result = t.Result;
                        if (result == null) return;
                        Application.Current?.Dispatcher.Invoke(async () =>
                        {
                            var service = new SchematicService();
                            // 直接載入已投影的點資料
                            var svm = new IFC_Viewer_00.ViewModels.SchematicViewModel(service, _selection);
                            await svm.LoadProjectedAsync(result);
                            var view = new IFC_Viewer_00.Views.SchematicView { DataContext = svm };
                            if (!string.IsNullOrWhiteSpace(result.SystemName)) view.Title = $"AS原理圖 - {result.SystemName}";
                            view.Show();
                        });
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"AS原理圖流程啟動失敗: {ex.Message}", "AS原理圖", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return Task.CompletedTask;
        }

        // 新增：V1 平面投影（所有系統 Ports → 2D 點）
        private async Task OnGenerateSchematicV1Async()
        {
            try
            {
                if (Model == null)
                {
                    StatusMessage = "尚未載入模型";
                    MessageBox.Show("尚未載入模型", "AS原理圖V1", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var service = new SchematicService();
                var systems = Model.Instances.OfType<IIfcSystem>().ToList();
                if (systems.Count == 0)
                {
                    MessageBox.Show("模型中沒有 IfcSystem。", "AS原理圖V1", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // 系統選擇對話框（多選）
                var pick = new SystemsPickDialog(systems) { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
                if (pick.ShowDialog() != true || pick.SelectedSystems.Count == 0) return;

                // 平面選擇
                var dlg = new PlaneSelectionDialog { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
                if (dlg.ShowDialog() != true) return;
                string plane = dlg.SelectedPlane;

                var all3D = new System.Collections.Generic.List<Point3D>();
                var metaList = new System.Collections.Generic.List<(int? portLabel,string? name,string? hostType,int? hostLabel,bool isFromPipeSegment)>();
                var svm = new IFC_Viewer_00.ViewModels.SchematicViewModel(service, _selection)
                {
                    CanvasWidth = 1200,
                    CanvasHeight = 800,
                    CanvasPadding = 40
                };
                svm.AddLog($"選擇平面: {plane}");
                foreach (var sys in pick.SelectedSystems)
                {
                    svm.AddLog($"處理系統: {IfcStringHelper.FromValue(sys.Name) ?? IfcStringHelper.FromValue(sys.GlobalId)}");
                    var pts3D = await service.GetAllPortCoordinatesAsync(Model, sys);
                    var stats = service.LastPortExtractionStats;
                    if (pts3D.Count == 0)
                    {
                        svm.AddLog("  -> 無 Port (HasPorts/Nested/Nests Fallback 全部失敗)");
                        continue;
                    }
                    svm.AddLog($"  -> Ports: distinct={stats?.DistinctPorts} viaHasPorts={stats?.ViaHasPorts} viaNested={stats?.ViaNested} viaFallback={stats?.ViaFallback}");
                    // 對應 LastPortDetails（順序：distinctPorts 的順序）
                    var portDetails = service.LastPortDetails;
                    foreach (var p in pts3D) all3D.Add(new Point3D(p.X, p.Y, p.Z));
                    // 與 all3D 同序 push meta
                    if (portDetails != null)
                    {
                        foreach (var d in portDetails)
                        {
                            bool isPipe = !string.IsNullOrWhiteSpace(d.HostType) && d.HostType.IndexOf("pipesegment", StringComparison.OrdinalIgnoreCase) >= 0;
                            metaList.Add((d.PortLabel, d.PortName, d.HostType, d.HostLabel, isPipe));
                            svm.AddLog($"    Port L{d.PortLabel} '{d.PortName}' Host=({d.HostLabel}:{d.HostType}) Src={d.SourcePath} XYZ=({d.X:0.##},{d.Y:0.##},{d.Z:0.##})");
                        }
                    }
                }
                if (all3D.Count == 0)
                {
                    MessageBox.Show("所選系統皆無可用 DistributionPort。", "AS原理圖V1", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // 使用新 API：從 3D 投影 → 2D（支援退化檢測與軸向翻轉）
                await svm.LoadPointsFrom3DAsync(all3D, plane, metaList, flipX: false, flipY: true, tryBestIfDegenerate: true);
                svm.AddLog($"總計投影點數: {all3D.Count}");
                var view = new SchematicView { DataContext = svm };
                view.Title = $"AS原理圖V1 - {pick.SelectedSystems.Count} 系統 Ports ({plane})";
                view.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"AS原理圖V1 生成失敗: {ex.Message}", "AS原理圖V1", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 新增：生成 PipeSegment 軸線線圖（無 Port 模型也可）
        private async Task OnGeneratePipeAxesAsync()
        {
            try
            {
                if (Model == null)
                {
                    StatusMessage = "尚未載入模型";
                    MessageBox.Show("尚未載入模型", "PipeAxis", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // 重用已有 PlaneSelectionDialog (若存在)；若無則預設 XY
                string plane = "XY";
                try
                {
                    var dlg = new PlaneSelectionDialog { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
                    if (dlg.ShowDialog() == true) plane = dlg.SelectedPlane;
                }
                catch { }

                var service = new SchematicService();
                var data = await service.GeneratePipeAxesAsync(Model, plane, flipY: true);
                if (data.Nodes.Count == 0)
                {
                    MessageBox.Show("模型中沒有可解析的 IfcPipeSegment 幾何。", "PipeAxis", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // 在 3D 檢視器疊加中線與端點（使用絕對 3D 座標）
                try
                {
                    var axes = data.Edges
                        .Where(e => e.StartNode != null && e.EndNode != null)
                        .Select(e => (Start: new Point3D(e.StartNode.Position3D.X, e.StartNode.Position3D.Y, e.StartNode.Position3D.Z),
                                      End: new Point3D(e.EndNode.Position3D.X, e.EndNode.Position3D.Y, e.EndNode.Position3D.Z)))
                        .ToList();
                    var endpoints = data.Nodes.Select(n => new Point3D(n.Position3D.X, n.Position3D.Y, n.Position3D.Z)).ToList();
                    _viewer3D.ShowOverlayPipeAxes(axes, endpoints, lineColor: System.Windows.Media.Colors.DeepSkyBlue, lineThickness: 2.0, pointColor: System.Windows.Media.Colors.Black, pointSize: 3.0);
                    // 顯示 Overlay 後自動降低透明度（與「單獨顯示 3D Overlay」一致）
                    try
                    {
                        if (_opacityBeforeOverlay == null)
                            _opacityBeforeOverlay = ModelOpacity;
                        ModelOpacity = 0.3; // 觸發 OnModelOpacityChanged
                    }
                    catch { }
                }
                catch { }
                var svm = new IFC_Viewer_00.ViewModels.SchematicViewModel(service, _selection)
                {
                    CanvasWidth = 1200,
                    CanvasHeight = 800,
                    CanvasPadding = 40
                };
                await svm.LoadPipeAxesAsync(data);
                svm.AddLog($"生成管段軸線：Segments={data.Edges.Count} Plane={plane}");
                var view = new SchematicView { DataContext = svm };
                view.Title = $"PipeAxes - {data.Edges.Count} 段 ({plane})";
                view.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"PipeAxes 生成失敗: {ex.Message}", "PipeAxis", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 單獨顯示 3D 中線/端點 Overlay（不開啟 2D 視圖）
        private async Task OnShowPipeOverlay3DAsync()
        {
            try
            {
                if (Model == null)
                {
                    StatusMessage = "尚未載入模型";
                    MessageBox.Show("尚未載入模型", "3D Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var service = new SchematicService();
                // 平面參數僅影響 2D，不影響 3D；這裡任意給值
                var data = await service.GeneratePipeAxesAsync(Model, "XY", flipY: false);
                if (data.Nodes.Count == 0)
                {
                    MessageBox.Show("模型中沒有可解析的 IfcPipeSegment 幾何。", "3D Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var axes = data.Edges
                    .Where(e => e.StartNode != null && e.EndNode != null)
                    .Select(e => (Start: new Point3D(e.StartNode.Position3D.X, e.StartNode.Position3D.Y, e.StartNode.Position3D.Z),
                                  End: new Point3D(e.EndNode.Position3D.X, e.EndNode.Position3D.Y, e.EndNode.Position3D.Z)))
                    .ToList();
                var endpoints = data.Nodes.Select(n => new Point3D(n.Position3D.X, n.Position3D.Y, n.Position3D.Z)).ToList();
                _viewer3D.ShowOverlayPipeAxes(axes, endpoints,
                    lineColor: System.Windows.Media.Colors.DeepSkyBlue,
                    lineThickness: Math.Max(0.5, OverlayLineThickness),
                    pointColor: System.Windows.Media.Colors.Black,
                    pointSize: Math.Max(0.5, OverlayPointSize));

                // 顯示 Overlay 後，自動將模型透明度降到 0.3，並保存原值以便清除時還原
                try
                {
                    if (_opacityBeforeOverlay == null)
                        _opacityBeforeOverlay = ModelOpacity;
                    ModelOpacity = 0.3; // 觸發 OnModelOpacityChanged → _viewer3D.SetModelOpacity
                }
                catch { }
                StatusMessage = $"3D Overlay: 中線 {axes.Count} 條，端點 {endpoints.Count} 個";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"3D Overlay 失敗: {ex.Message}", "3D Overlay", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 顯示測試用 3D Overlay：在 (0,0,0) 畫一個黑點，並從 (0,0,0) 沿 +Z 畫一條 1000m 的黑線
        private void OnShowTestOverlay3D()
        {
            try
            {
                if (_viewer3D == null)
                {
                    MessageBox.Show("3D 服務未初始化", "3D Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // 單位：xBIM 幾何通常以 mm 表示（OneMetre=1000），這裡用 1000m = 1,000,000 mm
                var origin = new Point3D(0, 0, 0);
                var endZ = new Point3D(0, 0, 1_000_000);
                var axes = new List<(Point3D Start, Point3D End)> { (origin, endZ) };
                var endpoints = new List<Point3D> { origin };
                _viewer3D.ShowOverlayPipeAxes(
                    axes,
                    endpoints,
                    lineColor: System.Windows.Media.Colors.Black,
                    lineThickness: Math.Max(0.5, OverlayLineThickness),
                    pointColor: System.Windows.Media.Colors.Black,
                    pointSize: Math.Max(1.0, OverlayPointSize));

                // 顯示 Overlay 後，與正式流程一致：自動降不透明度，清除時還原
                if (_opacityBeforeOverlay == null)
                    _opacityBeforeOverlay = ModelOpacity;
                ModelOpacity = 0.3;
                StatusMessage = "3D 測試 Overlay：原點黑點 + Z向 1000m 黑線";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"3D 測試 Overlay 失敗: {ex.Message}", "3D Overlay", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 顯示測試：三軸座標箭頭（原點出發）
        private void OnShowAxesOverlay3D(double metres = 100)
        {
            try
            {
                if (_viewer3D == null)
                {
                    MessageBox.Show("3D 服務未初始化", "3D Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // 以 mm 為單位：1m = 1000mm
                var L = Math.Max(1.0, metres) * 1000.0;
                var O = new Point3D(0, 0, 0);
                var X = new Point3D(L, 0, 0);
                var Y = new Point3D(0, L, 0);
                var Z = new Point3D(0, 0, L);
                var axes = new List<(Point3D Start, Point3D End)>
                {
                    (O, X), // X 紅
                    (O, Y), // Y 綠
                    (O, Z)  // Z 藍
                };
                var endpoints = new List<Point3D> { O, X, Y, Z };

                // 我們用三次呼叫來分色繪製，先清一次 overlay 子項再加入，保持顯示一致
                // X 紅
                _viewer3D.ShowOverlayPipeAxes(new []{ (O, X) }, new []{ O, X },
                    lineColor: System.Windows.Media.Colors.Red,
                    lineThickness: Math.Max(0.5, OverlayLineThickness),
                    pointColor: System.Windows.Media.Colors.Black,
                    pointSize: Math.Max(1.0, OverlayPointSize),
                    applyCameraOffset: false);
                // Y 綠（在既有 overlay 上追加）
                _viewer3D.ShowOverlayPipeAxes(new []{ (O, Y) }, new []{ Y },
                    lineColor: System.Windows.Media.Colors.Green,
                    lineThickness: Math.Max(0.5, OverlayLineThickness),
                    pointColor: System.Windows.Media.Colors.Black,
                    pointSize: Math.Max(1.0, OverlayPointSize),
                    applyCameraOffset: false);
                // Z 藍（在既有 overlay 上追加）
                _viewer3D.ShowOverlayPipeAxes(new []{ (O, Z) }, new []{ Z },
                    lineColor: System.Windows.Media.Colors.Blue,
                    lineThickness: Math.Max(0.5, OverlayLineThickness),
                    pointColor: System.Windows.Media.Colors.Black,
                    pointSize: Math.Max(1.0, OverlayPointSize),
                    applyCameraOffset: false);

                if (_opacityBeforeOverlay == null)
                    _opacityBeforeOverlay = ModelOpacity;
                ModelOpacity = 0.3;
                StatusMessage = $"3D 測試 Overlay：座標軸 X/Y/Z = {metres:0} m";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"3D 測試座標軸失敗: {ex.Message}", "3D Overlay", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 清除 3D Overlay，並還原顯示 Overlay 前的模型透明度
        private void OnClearOverlay3D()
        {
            try
            {
                _viewer3D.ClearOverlay();
                if (_opacityBeforeOverlay.HasValue)
                {
                    var restore = _opacityBeforeOverlay.Value;
                    _opacityBeforeOverlay = null;
                    ModelOpacity = restore; // 觸發 OnModelOpacityChanged
                }
            }
            catch { }
        }

        // 階段 4/6：模型變更通知，載入空間結構樹
        partial void OnModelChanged(IfcStore? value)
        {
            // 通知 3D 服務載入模型（Stub 為 no-op）
            if (value is not null)
            {
                _viewer3D?.SetModel(value);
            }
            else
            {
                // 明確清空（避免多載挑到非 nullable 版本）
                _viewer3D?.SetModel((IfcStore?)null);
            }
            _viewer3D?.ResetCamera();
            // 模型變更後套用透明度
            if (_viewer3D != null) _viewer3D.SetModelOpacity(ModelOpacity);
            BuildHierarchy();

            // 重新訂閱可見性與選取事件
            SubscribeHierarchyNodeEvents();
        }

        partial void OnModelOpacityChanged(double value)
        {
            // 透明度限界並套用
            var v = Math.Max(0.0, Math.Min(1.0, value));
            if (Math.Abs(v - value) > double.Epsilon)
            {
                ModelOpacity = v;
                return;
            }
            _viewer3D?.SetModelOpacity(v);
            StatusMessage = $"模型透明度: {v:0.00}";
        }

        /// <summary>
        /// 建立 IFC 空間結構樹狀資料
        /// </summary>
        private void BuildHierarchy()
        {
            Hierarchy ??= new ObservableCollection<SpatialNode>();
            Hierarchy.Clear();
            if (Model == null) return;
            // 尋找 IfcProject 作為根節點
            var project = Model.Instances.OfType<Xbim.Ifc4.Interfaces.IIfcProject>().FirstOrDefault();
            if (project == null) return;
            var projectNode = new SpatialNode
            {
                Name = !string.IsNullOrEmpty(IfcStringHelper.FromValue(project.Name)) ? IfcStringHelper.FromValue(project.Name) : (IfcStringHelper.FromValue(project.GlobalId) ?? "Project"),
                Entity = project,
                Children = new ObservableCollection<SpatialNode>()
            };

            // 將專案下的頂層空間節點（Site/Building…）加入
            var topSpatials = project.IsDecomposedBy
                .SelectMany(r => r.RelatedObjects)
                .OfType<Xbim.Ifc4.Interfaces.IIfcSpatialStructureElement>()
                .ToList();
            foreach (var s in topSpatials)
            {
                projectNode.Children.Add(BuildSpatialNode(s));
            }
            Hierarchy.Add(projectNode);
            // 建立後立即訂閱事件
            SubscribeHierarchyNodeEvents();
        }

        private SpatialNode BuildSpatialNode(Xbim.Ifc4.Interfaces.IIfcSpatialStructureElement spatial)
        {
            var node = new SpatialNode
            {
                Name = !string.IsNullOrEmpty(IfcStringHelper.FromValue(spatial.Name))
                    ? IfcStringHelper.FromValue(spatial.Name)
                    : (!string.IsNullOrEmpty(IfcStringHelper.FromValue(spatial.GlobalId))
                        ? IfcStringHelper.FromValue(spatial.GlobalId)
                        : "Unnamed"),
                Entity = spatial,
                Children = new ObservableCollection<SpatialNode>()
            };
            // 遍歷空間結構的子空間（用 IsDecomposedBy）
            var childSpatials = spatial.IsDecomposedBy?
                .SelectMany(r => r.RelatedObjects)
                .OfType<Xbim.Ifc4.Interfaces.IIfcSpatialStructureElement>()
                ?? Enumerable.Empty<Xbim.Ifc4.Interfaces.IIfcSpatialStructureElement>();
            foreach (var child in childSpatials)
            {
                node.Children.Add(BuildSpatialNode(child));
            }
            // 加入該空間直接包含的產品（非空間）
            var directElements = spatial.ContainsElements?
                .SelectMany(r => r.RelatedElements)
                .Where(e => !(e is Xbim.Ifc4.Interfaces.IIfcSpatialStructureElement))
                ?? Enumerable.Empty<Xbim.Ifc4.Interfaces.IIfcProduct>();
            foreach (var elem in directElements)
            {
                node.Children.Add(new SpatialNode
                {
                    Name = !string.IsNullOrEmpty(IfcStringHelper.FromValue(elem.Name))
                        ? IfcStringHelper.FromValue(elem.Name)
                        : (!string.IsNullOrEmpty(IfcStringHelper.FromValue(elem.GlobalId))
                            ? IfcStringHelper.FromValue(elem.GlobalId)
                            : "Unnamed"),
                    Entity = elem,
                    Children = new ObservableCollection<SpatialNode>()
                });
            }
            return node;
        }

        private void SubscribeHierarchyNodeEvents()
        {
            try
            {
                if (Hierarchy == null) return;
                void Walk(ObservableCollection<SpatialNode> nodes)
                {
                    foreach (var n in nodes)
                    {
                        // 可見性改變時更新 3D 隱藏清單
                        n.PropertyChanged -= SpatialNode_PropertyChanged;
                        n.PropertyChanged += SpatialNode_PropertyChanged;
                        if (n.Children != null && n.Children.Count > 0)
                            Walk(n.Children);
                    }
                }
                Walk(Hierarchy);
                // 初始同步一次
                Update3DVisibility();
            }
            catch { }
        }

        private void SpatialNode_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                if (sender is SpatialNode node)
                {
                    if (e.PropertyName == nameof(SpatialNode.IsVisible))
                    {
                        OnVisibilityChanged();
                    }
                    else if (e.PropertyName == nameof(SpatialNode.IsSelected))
                    {
                        // 任務 2：同步 SelectedNodes 集合
                        if (node.IsSelected)
                        {
                            if (!SelectedNodes.Contains(node)) SelectedNodes.Add(node);
                        }
                        else
                        {
                            if (SelectedNodes.Contains(node)) SelectedNodes.Remove(node);
                        }
                        // 立即更新 3D 多選高亮
                        Update3DHighlight();
                    }
                }
            }
            catch { }
        }

        private void Update3DVisibility()
        {
            try
            {
                if (Model == null || Hierarchy == null) return;
                var hidden = new System.Collections.Generic.List<Xbim.Common.IPersistEntity>();
                void Walk(ObservableCollection<SpatialNode> nodes, bool parentHidden)
                {
                    foreach (var n in nodes)
                    {
                        var isHidden = parentHidden || (n.IsVisible == false);
                        if (isHidden && n.Entity is Xbim.Common.IPersistEntity pe)
                            hidden.Add(pe);
                        if (n.Children != null && n.Children.Count > 0)
                            Walk(n.Children, isHidden);
                    }
                }
                Walk(Hierarchy, parentHidden: false);
                _viewer3D.UpdateHiddenList(hidden);
            }
            catch { }
        }

        // 任務 3：可讀性：提供 OnVisibilityChanged()
        private void OnVisibilityChanged() => Update3DVisibility();

        // 簡單節流：合併高頻率的 SelectedNodes 變化，降低 UI 抖動
        private DispatcherTimer? _selectionCoalesceTimer;
        private IReadOnlyList<int> _lastHighlightedLabels = Array.Empty<int>();
        private void OnSelectedNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isSyncingSelection) return;
            try
            {
                try { System.Diagnostics.Trace.WriteLine($"[ViewModel] OnSelectedNodesChanged triggered. Reason: Local TreeView selection changed."); } catch { }
                _isSyncingSelection = true;
                try { Trace.WriteLine($"[ViewModel] OnSelectedNodesChanged triggered. Selected count: {SelectedNodes?.Count ?? 0}"); } catch { }
                // 合併多次事件：延後 50ms 執行一次 Update3DHighlight
                _selectionCoalesceTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (s2, e2) =>
                {
                    try { Update3DHighlight(); }
                    finally { _selectionCoalesceTimer?.Stop(); }
                }, Dispatcher.CurrentDispatcher);
                _selectionCoalesceTimer.Stop();
                _selectionCoalesceTimer.Start();

                // 同步 SelectionService（以 TreeView 為來源）
                try
                {
                    if (SelectedNodes != null && SelectedNodes.Count >= 0)
                    {
                        var labels = SelectedNodes
                            .Select(n => (n?.Entity as Xbim.Common.IPersistEntity)?.EntityLabel)
                            .Where(id => id.HasValue && id.Value != 0)
                            .Select(id => id!.Value)
                            .ToArray();
                        _selection.SetSelection(labels, SelectionOrigin.TreeView);
                    }
                }
                catch { }

                // 右側面板：多選摘要（延後到合併計時器觸發時處理）
            }
            finally
            {
                _isSyncingSelection = false;
            }
        }

        private void OnGlobalSelectionChanged(object? s, SelectionSetChangedEventArgs e)
        {
            // 來自 TreeView 的全域選取事件不需在 ViewModel 再處理，避免重複觸發 3D 高亮
            if (e != null && e.Origin == SelectionOrigin.TreeView) return;
            if (_isSyncingSelection) return;
            try
            {
                try { System.Diagnostics.Trace.WriteLine($"[ViewModel] OnGlobalSelectionChanged triggered. Reason: Global selection changed by '{e?.Origin}' ."); } catch { }
                _isSyncingSelection = true;
                if (e == null) return;
                var ids = _selection.Selected;

                // 右側面板：單選詳情、多選摘要（適度延後，減少抖動）
                if (ids.Count == 0)
                {
                    // 直接清除即可：無昂貴計算
                    SelectedElementProperties.Clear();
                }
                else if (ids.Count == 1 && Model != null)
                {
                    var pe = Model.Instances[ids.FirstOrDefault()] as IIfcObject;
                    if (pe != null) HighlightedEntity = pe;
                }
                else if (Model != null)
                {
                    // 多選摘要延後由 SelectedNodes 的合併計時器觸發時統一處理
                }

                // 只處理非 TreeView 來源（例如 3DView）之 3D 高亮
                if (ids.Count > 0)
                    _viewer3D.HighlightEntities(ids, clearPrevious: true);
                else
                    _viewer3D.HighlightEntities(Array.Empty<int>(), clearPrevious: true);
            }
            finally
            {
                _isSyncingSelection = false;
            }
        }

        // 將目前多選集合完整傳遞給 3D 服務以高亮
        private void Update3DHighlight()
        {
            try
            {
                try { Trace.WriteLine($"[ViewModel] Update3DHighlight begin. Current Selected count: {SelectedNodes?.Count ?? 0}"); } catch { }
                if (SelectedNodes == null || SelectedNodes.Count == 0)
                {
                    // 清空高亮
                    try { Trace.WriteLine("[ViewModel] Calling HighlightEntities with 0 (clear)"); } catch { }
                    _viewer3D.HighlightEntities(Array.Empty<int>(), clearPrevious: true);
                    _lastHighlightedLabels = Array.Empty<int>();
                    SelectedElementProperties.Clear();
                    return;
                }

                // 首選：以 labels 驅動（跨版本最穩定）
                var labels = SelectedNodes
                    .Select(n => (n?.Entity as Xbim.Common.IPersistEntity)?.EntityLabel)
                    .Where(id => id.HasValue && id.Value != 0)
                    .Select(id => id!.Value)
                    .ToArray();
                if (labels.Length > 0)
                {
                    // 若與上次相同，則略過重複高亮
                    if (!_lastHighlightedLabels.SequenceEqual(labels))
                    {
                        try { Trace.WriteLine($"[ViewModel] Calling HighlightEntities(labels) with {labels.Length} ids."); } catch { }
                        _viewer3D.HighlightEntities(labels, clearPrevious: true);
                        _lastHighlightedLabels = labels;
                    }
                    // 更新右側：根據 SelectedNodes 建立摘要（或單選詳情）
                    try
                    {
                        var objs = SelectedNodes.Select(n => n?.Entity).OfType<IIfcObject>().ToList();
                        if (objs.Count == 1) UpdateSelectedElementProperties(objs[0]);
                        else if (objs.Count > 1) BuildMultiSelectionSummaryFromObjects(objs);
                    }
                    catch { }
                    return;
                }

                // 後援：以 IPersistEntity 清單
                var entities = SelectedNodes
                    .Select(n => n?.Entity)
                    .OfType<Xbim.Common.IPersistEntity>()
                    .ToList();
                if (entities.Count > 0)
                {
                    try { Trace.WriteLine($"[ViewModel] Calling HighlightEntities(entities) with {entities.Count} entities."); } catch { }
                    _viewer3D.HighlightEntities(entities);
                }
                else
                {
                    // 兩條路都拿不到 → 清空
                    try { Trace.WriteLine("[ViewModel] No labels or entities available; clearing highlight."); } catch { }
                    _viewer3D.HighlightEntities(Array.Empty<int>(), clearPrevious: true);
                    _lastHighlightedLabels = Array.Empty<int>();
                    SelectedElementProperties.Clear();
                }
            }
            catch { }
        }

        // 任務 2：清空選取（點擊空白處）
        [RelayCommand]
        private void ClearSelection()
        {
            try
            {
                // 清 VM 的 SelectedNodes 與節點 IsSelected
                if (Hierarchy != null)
                {
                    void Walk(ObservableCollection<SpatialNode> nodes)
                    {
                        foreach (var n in nodes)
                        {
                            n.IsSelected = false;
                            if (n.Children != null && n.Children.Count > 0) Walk(n.Children);
                        }
                    }
                    Walk(Hierarchy);
                }
                SelectedNodes.Clear();
                // 清全域選取集
                _selection.Clear(SelectionOrigin.TreeView);
                // 清 3D 高亮
                _viewer3D.HighlightEntities(Array.Empty<int>(), clearPrevious: true);
            }
            catch { }
        }

        // TreeView 選擇連動（單選不再直接觸發 3D 高亮，改由 SelectedNodes 集合統一處理）
        partial void OnSelectedNodeChanged(SpatialNode? value)
        {
            if (value?.Entity is IIfcObject obj)
            {
                // 僅更新屬性面板；高亮改由 SelectedNodes → Update3DHighlight 統一處理
                UpdateSelectedElementProperties(obj);
            }
        }

        /// <summary>
        /// 更新被選取 IFC 元件的屬性集合
        /// </summary>
    public void UpdateSelectedElementProperties(IIfcObject selectedEntity)
        {
            SelectedElementProperties ??= new ObservableCollection<ElementProperty>();
            SelectedElementProperties.Clear();
            if (selectedEntity == null) return;

            // xBIM 6.x: 取得屬性集 (Property Sets)
            // 需自行遍歷 IIfcPropertySet 與 IIfcProperty
            if (selectedEntity is IIfcObject obj)
            {
                foreach (var rel in obj.IsDefinedBy)
                {
                    // xBIM 6.x: RelatingPropertyDefinition 取代 RelatingDefinition
                    if (rel.RelatingPropertyDefinition is IIfcPropertySet pset)
                    {
                        foreach (var prop in pset.HasProperties)
                        {
                            string name = IfcStringHelper.FromValue(prop.Name);
                            string value = string.Empty;
                            if (prop is IIfcPropertySingleValue sv && sv.NominalValue != null)
                            {
                                object? raw = sv.NominalValue.Value;
                                value = IfcStringHelper.FromValue(raw);
                            }
                            SelectedElementProperties.Add(new ElementProperty { Name = name, Value = value });
                        }
                    }
                }
            }
        }

        // 建立多選摘要：
        // - 第一行：已選取項目 = N
        // - 第二行：類型分布 = IfcWall(5), IfcDoor(2) ...
        // - 後續：共同屬性（名稱 -> 值），對於存在但值不同的屬性以「(多值)」顯示
        private void BuildMultiSelectionSummaryFromObjects(System.Collections.Generic.IEnumerable<IIfcObject> objects)
        {
            SelectedElementProperties ??= new ObservableCollection<ElementProperty>();
            SelectedElementProperties.Clear();
            if (objects == null) return;
            var list = objects.ToList();
            if (list.Count == 0) return;

            // 1) 數量
            SelectedElementProperties.Add(new ElementProperty { Name = "已選取項目", Value = list.Count.ToString() });

            // 2) 類型分布
            try
            {
                var typeGroups = list
                    .Select(o => o?.GetType()?.Name ?? "?")
                    .GroupBy(n => n)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .Select(g => $"{g.Key}({g.Count()})");
                SelectedElementProperties.Add(new ElementProperty { Name = "類型分布", Value = string.Join(", ", typeGroups) });
            }
            catch { }

            // 3) 屬性彙總
            try
            {
                // 每個物件的屬性字典：key = "Pset.Property"，value = string
                var dicts = list.Select(BuildPropertyDictionary).ToList();
                if (dicts.Count == 0) return;

                // 所有鍵集合
                var allKeys = new System.Collections.Generic.HashSet<string>(dicts.SelectMany(d => d.Keys));
                // 共同屬性（所有物件都有此鍵，且值一致）
                foreach (var key in allKeys.OrderBy(k => k))
                {
                    var values = dicts
                        .Select(d => d.TryGetValue(key, out var v) ? (v ?? string.Empty) : null)
                        .ToList();
                    if (values.Any(v => v == null))
                    {
                        // 至少一個物件沒有此屬性 → 略過或標示多值；這裡略過
                        continue;
                    }
                    var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinct.Count == 1)
                    {
                        // 共同屬性（相同值）
                        SelectedElementProperties.Add(new ElementProperty { Name = key, Value = distinct[0] ?? string.Empty });
                    }
                }

                // 顯示存在但值不同的屬性（可選）：列出前若干個
                var differing = new System.Collections.Generic.List<string>();
                foreach (var key in allKeys.OrderBy(k => k))
                {
                    var values = dicts
                        .Select(d => d.TryGetValue(key, out var v) ? (v ?? string.Empty) : null)
                        .ToList();
                    if (values.Any(v => v == null)) continue; // 不是所有都有
                    var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinct.Count > 1) differing.Add(key);
                }
                if (differing.Count > 0)
                {
                    // 插入一條分隔提示
                    SelectedElementProperties.Add(new ElementProperty { Name = "—", Value = "—" });
                    foreach (var key in differing)
                    {
                        SelectedElementProperties.Add(new ElementProperty { Name = key, Value = "(多值)" });
                    }
                }
            }
            catch { }
        }

        // 將單一物件的屬性集轉為字典（key: Pset.Property, value: string）
        private static System.Collections.Generic.Dictionary<string, string> BuildPropertyDictionary(IIfcObject obj)
        {
            var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var rel in obj.IsDefinedBy)
                {
                    if (rel.RelatingPropertyDefinition is IIfcPropertySet pset)
                    {
                        var psetName = IfcStringHelper.FromValue(pset.Name) ?? "(PSet)";
                        foreach (var prop in pset.HasProperties)
                        {
                            string propName = IfcStringHelper.FromValue(prop.Name) ?? "(Prop)";
                            string key = string.IsNullOrEmpty(psetName) ? propName : ($"{psetName}.{propName}");
                            string value = string.Empty;
                            if (prop is IIfcPropertySingleValue sv && sv.NominalValue != null)
                            {
                                object? raw = sv.NominalValue.Value;
                                value = IfcStringHelper.FromValue(raw);
                            }
                            // 僅在尚未存在時加入（避免重複鍵覆蓋）
                            if (!dict.ContainsKey(key)) dict[key] = value ?? string.Empty;
                        }
                    }
                }
            }
            catch { }
            return dict;
        }
    }
}
