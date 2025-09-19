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

namespace IFC_Viewer_00.ViewModels
{

    public partial class MainViewModel : ObservableObject
    {
        private readonly IViewer3DService _viewer3D;
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

        public RelayCommand OpenFileCommand { get; }

        public MainViewModel() : this(new StubViewer3DService()) { }

        public MainViewModel(IViewer3DService viewer3D)
        {
            _viewer3D = viewer3D;
            OpenFileCommand = new RelayCommand(async () => await OnOpenFileAsync());

            IsolateSelectionCommand = new RelayCommand(OnIsolateSelection);
            HideSelectionCommand = new RelayCommand(OnHideSelection);
            ShowAllCommand = new RelayCommand(OnShowAll);
            StatusMessage = "就緒";
        }
        // Sprint 1: 3D 物件高亮 - 供未來擴充
    partial void OnHighlightedEntityChanged(IIfcObject? value)
        {
            // 呼叫 3D 服務高亮（Stub 為 no-op，未來替換實作）
            _viewer3D?.HighlightEntity(value, clearPrevious: true);
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

        // TreeView 選擇連動
        partial void OnSelectedNodeChanged(SpatialNode? value)
        {
            // 可在此加入 3D 高亮、屬性同步等邏輯
            if (value?.Entity is IIfcObject obj)
            {
                _viewer3D?.HighlightEntity(obj, clearPrevious: true);
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
    }
}
