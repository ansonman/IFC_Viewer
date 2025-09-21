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

namespace IFC_Viewer_00.ViewModels
{

    public partial class MainViewModel : ObservableObject
    {
    private readonly IViewer3DService _viewer3D;
    private readonly ISelectionService _selection;
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

    [ObservableProperty]
    private ObservableCollection<ElementProperty> selectedElementProperties = new();

        [ObservableProperty]
    private ObservableCollection<SpatialNode> hierarchy = new();

    [ObservableProperty]
    private SpatialNode? selectedNode;

        // 任務 2：TreeView 多選集合
        public ObservableCollection<SpatialNode> SelectedNodes { get; } = new();

        public RelayCommand OpenFileCommand { get; }

        public MainViewModel() : this(new StubViewer3DService(), new SelectionService()) { }

        public MainViewModel(IViewer3DService viewer3D, ISelectionService? selectionService = null)
        {
            _viewer3D = viewer3D;
            _selection = selectionService ?? new SelectionService();
            OpenFileCommand = new RelayCommand(async () => await OnOpenFileAsync());

            IsolateSelectionCommand = new RelayCommand(OnIsolateSelection);
            HideSelectionCommand = new RelayCommand(OnHideSelection);
            ShowAllCommand = new RelayCommand(OnShowAll);
            StatusMessage = "就緒";

            // TreeView 多選集合變更 → 立即同步 3D 高亮（多筆）
            SelectedNodes.CollectionChanged += OnSelectedNodesChanged;

            // 監聽全域選取變更，更新屬性面板摘要或單選詳情（避免循環）
            _selection.SelectionChanged += OnGlobalSelectionChanged;
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
            BuildHierarchy();

            // 重新訂閱可見性與選取事件
            SubscribeHierarchyNodeEvents();
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
