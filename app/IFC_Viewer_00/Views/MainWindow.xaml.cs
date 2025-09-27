using System.Windows;
using IFC_Viewer_00.Services;
using System;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Media;
using IFC_Viewer_00.ViewModels;
using System.Windows.Input;
using System.Collections.ObjectModel;

namespace IFC_Viewer_00.Views
{
    /// <summary>
    /// MainWindow.xaml 的互動邏輯
    /// 遵循 MVVM 架構，UI 邏輯主要由 MainViewModel 處理
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IViewer3DService _viewerService;
        private readonly ISelectionService _selectionService = new SelectionService();
        private bool _suppressTreeViewEvent;
        public MainWindow()
        {
            // 以反射呼叫 InitializeComponent，避免在無 XAML 產生時的編譯錯誤
            try
            {
                var init = GetType().GetMethod("InitializeComponent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                init?.Invoke(this, null);
            }
            catch { }
            // 優先以強型別建立 DrawingControl3D 與服務；失敗則回退反射/Stub
            var host = this.FindName("ViewerHost") as ContentControl;
            IFC_Viewer_00.Services.IViewer3DService? svc = null;
            object? viewer = null;
            if (host != null)
            {
                try
                {
                    var strong = new Xbim.Presentation.DrawingControl3D();
                    // 預設視覺化與世界座標調整
                    try { strong.WcsAdjusted = true; } catch { }
                    try { strong.ShowFps = true; } catch { }
                    host.Content = strong;
                    svc = new StrongWindowsUiViewer3DService(strong);
                    System.Diagnostics.Trace.WriteLine("[MainWindow] Using strong-typed DrawingControl3D.");
                    // 嘗試在載入與尺寸變更時，回家視角並同步進度
                    strong.Loaded += (_, __) =>
                    {
                        TryViewHome(strong);
                        UpdateDiagnostics(strong);
                    };
                    strong.SizeChanged += (_, __) =>
                    {
                        TryViewHome(strong);
                        UpdateDiagnostics(strong);
                    };
                    // 同步控制項選取到 ViewModel（不依賴 HitTest）
                    try { strong.SelectedEntityChanged += Strong_SelectedEntityChanged; } catch { }
                }
                catch
                {
                    // 回退：反射建立控制項
                    try
                    {
                        var type = Type.GetType("Xbim.Presentation.DrawingControl3D, Xbim.Presentation");
                        if (type == null)
                        {
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try { type = asm.GetType("Xbim.Presentation.DrawingControl3D", throwOnError: false, ignoreCase: true); if (type != null) break; } catch { }
                            }
                        }
                        if (type != null)
                        {
                            viewer = Activator.CreateInstance(type);
                            if (viewer != null)
                            {
                                host.Content = viewer;
                                svc = new WindowsUiViewer3DService(viewer);
                            }
                            System.Diagnostics.Trace.WriteLine("[MainWindow] Using reflective DrawingControl3D service.");
                        }
                    }
                    catch { }
                }
            }

            if (svc != null)
            {
                _viewerService = svc;
                // 以反射建立 ViewModel，避免在 XAML 臨時專案編譯期的相依問題
                object? vm = null;
                try
                {
                    var vmType = Type.GetType("IFC_Viewer_00.ViewModels.MainViewModel, IFC_Viewer_00");
                    if (vmType != null)
                    {
                        // 優先使用 (IViewer3DService, ISelectionService) 建構子
                        var ctor2 = vmType.GetConstructor(new[] { typeof(IViewer3DService), typeof(ISelectionService) });
                        if (ctor2 != null)
                            vm = ctor2.Invoke(new object?[] { _viewerService, _selectionService });
                        else
                        {
                            var ctor = vmType.GetConstructor(new[] { typeof(IViewer3DService) });
                            vm = ctor != null ? ctor.Invoke(new object?[] { _viewerService }) : Activator.CreateInstance(vmType);
                        }
                    }
                }
                catch { }
                DataContext = vm;
                // 同步 SelectionService → TreeView 勾選
                _selectionService.SelectionChanged += (s, e) =>
                {
                    try
                    {
                        if (DataContext is IFC_Viewer_00.ViewModels.MainViewModel mvm && mvm.Hierarchy != null)
                        {
                            var set = _selectionService.Selected.ToHashSet();
                            void Walk(System.Collections.ObjectModel.ObservableCollection<IFC_Viewer_00.Models.SpatialNode> nodes)
                            {
                                foreach (var n in nodes)
                                {
                                    var id = (n.Entity as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0;
                                    n.IsChecked = id != 0 && set.Contains(id);
                                    if (n.Children != null && n.Children.Count > 0) Walk(n.Children);
                                }
                            }
                            Walk(mvm.Hierarchy);
                        }
                    }
                    catch { }
                };
                // 監聽 SelectedNode 變更，將 TreeView 的 UI 選取同步
                if (DataContext is INotifyPropertyChanged inpc)
                {
                    inpc.PropertyChanged += ViewModel_PropertyChanged;
                }
                // 任務 2：TreeView Shift/Ctrl 多選事件（PreviewMouseLeftButtonDown）
                if (this.FindName("TreeViewNav") is TreeView tv)
                {
                    tv.PreviewMouseLeftButtonDown += TreeView_PreviewMouseLeftButtonDown;
                }
                // 嘗試掛載事件（UIElement）
                var uiElem = (host?.Content) as System.Windows.UIElement;
                if (uiElem != null)
                {
                    // 僅在點擊時才選取，避免 hover 自動選取（使用 Preview 以避免控制項提前將事件標記為已處理）
                    uiElem.PreviewMouseLeftButtonDown += (s, e) =>
                    {
                        try { System.Diagnostics.Trace.WriteLine($"[View] 3D View selection changed. Source: User Action."); } catch { }
                        try { System.Diagnostics.Trace.WriteLine("[MainWindow] PreviewMouseLeftButtonDown on viewer."); } catch { }
                        try { uiElem.Focus(); } catch { }
                        // 單擊：以 HitTest 設定選取；雙擊改由 MouseDoubleClick 事件處理
                        var pos = e.GetPosition(uiElem);
                        var entity = _viewerService.HitTest(pos.X, pos.Y);
                        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;

                        if (entity != null)
                        {
                            if (ctrl)
                            {
                                // Ctrl 切換多選（保留既有行為，不清空）
                                var lbl = TryGetEntityLabel(entity);
                                if (lbl.HasValue)
                                {
                                    if (_selectionService.Selected.Contains(lbl.Value))
                                        _selectionService.Remove(lbl.Value, SelectionOrigin.Viewer3D);
                                    else
                                        _selectionService.Add(lbl.Value, SelectionOrigin.Viewer3D);
                                }
                            }
                            else
                            {
                                // 單選切換：先清空，再設定新單選，確保替換舊選區
                                try { _selectionService.SetSelection(Array.Empty<int>(), SelectionOrigin.Viewer3D); } catch { }
                                var lbl = TryGetEntityLabel(entity);
                                if (lbl.HasValue)
                                {
                                    _selectionService.SetSelection(new[] { lbl.Value }, SelectionOrigin.Viewer3D);
                                }
                                else
                                {
                                    // 沒有 label 時，仍視為清空並僅做視覺高亮
                                    if (DataContext is IFC_Viewer_00.ViewModels.MainViewModel mvm)
                                        mvm.HighlightedEntity = entity;
                                    else if (DataContext != null)
                                        try { ((dynamic)DataContext).HighlightedEntity = entity; } catch { }
                                }
                            }
                        }
                        else
                        {
                            // 點擊空白：清空選區
                            try { _selectionService.SetSelection(Array.Empty<int>(), SelectionOrigin.Viewer3D); } catch { }
                        }
                    };
                    // 顯式訂閱雙擊，只處理縮放（僅限 Control 類型提供 MouseDoubleClick）
                    if (host?.Content is System.Windows.Controls.Control ctrlDbl)
                    {
                        ctrlDbl.MouseDoubleClick += Viewer3D_MouseDoubleClick;
                    }
                    uiElem.PreviewMouseRightButtonDown += (s, e) =>
                    {
                        try { System.Diagnostics.Trace.WriteLine("[MainWindow] PreviewMouseRightButtonDown on viewer."); } catch { }
                        // 右鍵彈出選單前，先以 HitTest 設定 HighlightedEntity，讓命令有目標
                        TryPickUnderMouseAndSetSelection(uiElem, e.GetPosition(uiElem));
                    };
                }
                // 啟動後自動載入（若有提供 IFC_STARTUP_FILE 環境變數）
                this.Loaded += async (_, __) =>
                {
                    try
                    {
                        var path = Environment.GetEnvironmentVariable("IFC_STARTUP_FILE");
                        if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                        {
                            // 以 dynamic 方式呼叫 VM 方法
                            if (DataContext is not null)
                            {
                                try { await ((dynamic)DataContext).OpenFileByPathAsync(path); } catch { }
                            }
                        }
                    }
                    catch { /* ignore */ }
                };
            }
            else
            {
                // 建立失敗則退回 stub
                _viewerService = new StubViewer3DService();
                // 以反射建立 ViewModel（或忽略失敗）
                try
                {
                    var vmType = Type.GetType("IFC_Viewer_00.ViewModels.MainViewModel, IFC_Viewer_00");
                    if (vmType != null)
                    {
                        var ctor = vmType.GetConstructor(new[] { typeof(IViewer3DService) });
                        DataContext = ctor != null ? ctor.Invoke(new object?[] { _viewerService }) : Activator.CreateInstance(vmType);
                    }
                }
                catch { }
            }
        }

        // 工具列：重設視角
        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            var host = this.FindName("ViewerHost") as ContentControl;
            if (host?.Content is Xbim.Presentation.DrawingControl3D strong)
            {
                TryViewHome(strong);
                UpdateDiagnostics(strong);
            }
            else if (host?.Content != null)
            {
                // 反射呼叫 ViewHome 作為回退
                try { host.Content.GetType().GetMethod("ViewHome")?.Invoke(host.Content, null); } catch { }
            }
        }

        // 工具列：顯示全部
        private void ShowAll_Click(object sender, RoutedEventArgs e)
        {
            var host = this.FindName("ViewerHost") as ContentControl;
            if (host?.Content is Xbim.Presentation.DrawingControl3D strong)
            {
                try { strong.ShowAll(); } catch { }
                UpdateDiagnostics(strong);
            }
            else if (host?.Content != null)
            {
                try { host.Content.GetType().GetMethod("ShowAll")?.Invoke(host.Content, null); } catch { }
            }
        }

        private async void GenerateSchematic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 從 ViewModel 取得 IfcStore 與 IModel
                var vm = this.DataContext as dynamic;
                Xbim.Ifc.IfcStore? store = null;
                try { store = (Xbim.Ifc.IfcStore?)vm?.Model; } catch { }
                if (store == null)
                {
                    MessageBox.Show(this, "尚未載入模型", "原理圖", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var iModel = (Xbim.Common.IModel)store;

                // 建立 VM 與載入資料
                var service = new SchematicService();
                var svm = new SchematicViewModel(service, _selectionService);
                await svm.LoadAsync(iModel);

                // 顯示視窗
                var win = new SchematicView { DataContext = svm, Owner = this };
                win.Show();

                // 若為 ports-only 且模型未含 IfcRelConnectsPorts，Edges 會為 0：提示使用者
                try
                {
                    if (svm.Edges != null && svm.Edges.Count == 0)
                    {
                        MessageBox.Show(this, "模型未含 IfcRelConnectsPorts 連線，僅顯示節點。", "原理圖", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch { }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, $"原理圖產生失敗: {ex.Message}", "原理圖", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 工具列：切換顯示 FPS
        private void ChkShowFps_Click(object sender, RoutedEventArgs e)
        {
            var host = this.FindName("ViewerHost") as ContentControl;
            if (host?.Content is Xbim.Presentation.DrawingControl3D strong)
            {
                try
                {
                    if (this.FindName("ChkShowFps") is System.Windows.Controls.CheckBox chk)
                        strong.ShowFps = chk.IsChecked == true;
                }
                catch { }
            }
            else if (host?.Content != null)
            {
                try
                {
                    if (this.FindName("ChkShowFps") is System.Windows.Controls.CheckBox chk)
                        host.Content.GetType().GetProperty("ShowFps")?.SetValue(host.Content, chk.IsChecked == true);
                }
                catch { }
            }
        }

        private static void TryViewHome(object strong)
        {
            try
            {
                var mi = strong.GetType().GetMethod("ViewHome");
                mi?.Invoke(strong, null);
            }
            catch { }
        }

        private void UpdatePercentageLoadedText(object control)
        {
            try
            {
                var prop = control.GetType().GetProperty("PercentageLoaded");
                if (prop != null)
                {
                    var val = prop.GetValue(control);
                    if (this.FindName("TxtPercentageLoaded") is System.Windows.Controls.TextBlock txt)
                    {
                        if (val is double d)
                            txt.Text = Math.Round(d * 100.0, 0) + "%";
                        else
                            txt.Text = Convert.ToString(val) ?? "";
                    }
                }
            }
            catch { }
        }

        private void UpdateDiagnostics(object control)
        {
            try
            {
                UpdatePercentageLoadedText(control);
                // WcsAdjusted
                try
                {
                    var wcsProp = control.GetType().GetProperty("WcsAdjusted");
                    if (wcsProp != null && this.FindName("TxtWcsAdjusted") is TextBlock txtWcs)
                    {
                        var v = wcsProp.GetValue(control);
                        txtWcs.Text = v is bool b ? (b ? "True" : "False") : (v?.ToString() ?? "?");
                    }
                }
                catch { }
                // Scenes Count
                try
                {
                    int? scenesCount = null;
                    var scenesPi = control.GetType().GetProperty("Scenes");
                    if (scenesPi != null)
                    {
                        var val = scenesPi.GetValue(control) as System.Collections.IEnumerable;
                        if (val is System.Collections.ICollection coll) scenesCount = coll.Count;
                    }
                    else
                    {
                        var scenesFi = control.GetType().GetField("Scenes", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (scenesFi != null)
                        {
                            var val = scenesFi.GetValue(control) as System.Collections.IEnumerable;
                            if (val is System.Collections.ICollection coll) scenesCount = coll.Count;
                        }
                    }
                    if (this.FindName("TxtScenesCount") is TextBlock txtScenes)
                    {
                        txtScenes.Text = scenesCount?.ToString() ?? "?";
                    }
                }
                catch { }
            }
            catch { }
        }

        // Sprint 1: 3D 物件高亮
        // 已停用 hover 自動選取；保留方法作為未來擴充（目前不做任何事）
        private void Viewer3D_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { }

        private void MainViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // TODO: 依新版 xBIM/WindowsUI API 實作 3D 控制項資料同步
            // if (DataContext is MainViewModel vm)
            // {
            //     if (e.PropertyName == "Model")
            //     {
            //         Viewer3D.Model = vm.Model;
            //         Viewer3D.ResetCamera();
            //     }
            //     else if (e.PropertyName == "HighlightedEntity")
            //     {
            //         Viewer3D.HighlightEntity(vm.HighlightedEntity, clearPrevious: true);
            //     }
            // }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, "SelectedNode", StringComparison.Ordinal)) return;
            try
            {
                var tree = this.FindName("TreeViewNav") as TreeView;
                if (tree == null) return;
                var vm = DataContext as dynamic;
                var node = (IFC_Viewer_00.Models.SpatialNode?)vm?.SelectedNode;
                if (node == null) return;
                _suppressTreeViewEvent = true;
                try
                {
                    SelectTreeViewItemByData(tree, node);
                }
                finally { _suppressTreeViewEvent = false; }
            }
            catch { }
        }

        private static bool SelectTreeViewItemByData(ItemsControl parent, object data)
        {
            // 先確保容器已產生
            parent.ApplyTemplate();
            parent.UpdateLayout();

            for (int i = 0; i < parent.Items.Count; i++)
            {
                var item = parent.Items[i];
                var tvi = parent.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
                if (tvi == null)
                {
                    // 若容器尚未生成，嘗試強制產生
                    parent.UpdateLayout();
                    tvi = parent.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
                }
                if (tvi == null) continue;

                // 展開以確保子節點容器產生
                tvi.IsExpanded = true;
                tvi.UpdateLayout();

                if (ReferenceEquals(item, data))
                {
                    tvi.IsSelected = true;
                    tvi.BringIntoView();
                    tvi.Focus();
                    return true;
                }

                if (tvi.Items.Count > 0)
                {
                    if (SelectTreeViewItemByData(tvi, data))
                    {
                        // 確保父系節點保持展開
                        tvi.IsExpanded = true;
                        return true;
                    }
                }
            }
            return false;
        }

        private void Viewer3D_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 雙擊只負責縮放，不再做選取/同步
            var host = this.FindName("ViewerHost") as ContentControl;
            var target = host?.Content;
            if (target == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TryZoomSelectedOrHome(target);
                UpdateDiagnostics(target);
            }), DispatcherPriority.Background);
            System.Diagnostics.Trace.WriteLine("[View] DoubleClick Zoom executed.");
        }

        private void TryPickUnderMouseAndSetSelection(System.Windows.UIElement viewer, System.Windows.Point position)
        {
            if (DataContext == null) return;
            try
            {
                var hit = _viewerService.HitTest(position.X, position.Y);
                if (hit != null)
                {
                    ((dynamic)DataContext).HighlightedEntity = hit;
                    ((dynamic)DataContext).UpdateSelectedElementProperties(hit);
                    ((dynamic)DataContext).SyncTreeViewSelection(hit);
                }
            }
            catch { }
        }

        private static int? TryGetEntityLabel(Xbim.Ifc4.Interfaces.IIfcObject obj)
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

        private static void TryZoomSelectedOrHome(object control)
        {
            try
            {
                var t = control.GetType();
                var mi = t.GetMethod("ZoomSelected");
                if (mi != null)
                {
                    mi.Invoke(control, null);
                    return;
                }
            }
            catch { }
            TryViewHome(control);
        }

        private void Strong_SelectedEntityChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                if (DataContext == null) return;
                var ctrl = sender;
                var pi = ctrl?.GetType().GetProperty("SelectedEntity");
                var val = pi?.GetValue(ctrl);
                if (val is Xbim.Ifc4.Interfaces.IIfcObject obj)
                {
                    // 同步 VM 的高亮與屬性、樹狀
                    try { ((dynamic)DataContext)!.HighlightedEntity = obj; } catch { }
                    try { ((dynamic)DataContext)!.UpdateSelectedElementProperties(obj); } catch { }
                    try { ((dynamic)DataContext)!.SyncTreeViewSelection(obj); } catch { }
                    var lbl = TryGetEntityLabel(obj);
                    if (lbl.HasValue) _selectionService.SetSelection(new[] { lbl.Value }, SelectionOrigin.Viewer3D);
                }
            }
            catch { }
        }

        // Sprint 1: 物件隔離/隱藏命令監聽
        private void IsolateSelection()
        {
            // TODO: 依新版 xBIM/WindowsUI API 實作 Isolate
            // if (DataContext is MainViewModel vm && vm.SelectedNode != null)
            // {
            //     Viewer3D.Isolate(vm.SelectedNode.Entity);
            // }
        }
        private void HideSelection()
        {
            // TODO: 依新版 xBIM/WindowsUI API 實作 Hide
            // if (DataContext is MainViewModel vm && vm.SelectedNode != null)
            // {
            //     Viewer3D.Hide(vm.SelectedNode.Entity, true);
            // }
        }
        private void ShowAll()
        {
            // TODO: 依新版 xBIM/WindowsUI API 實作 ShowAll
            // Viewer3D.ShowAll();
        }

        private async void LoadMockSchematic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var mock = new IFC_Viewer_00.Services.MockSchematicService();
                var data = await mock.GetMockDataAsync();
                var service = new IFC_Viewer_00.Services.SchematicService();
                var svm = new IFC_Viewer_00.ViewModels.SchematicViewModel(service, _selectionService);
                // 直接從資料載入
                try { await svm.LoadFromDataAsync(data); }
                catch { /* 若未實作則退回直接設定 */
                    try
                    {
                        // 簡單注入：模擬 LoadFromDataAsync 的行為
                        svm.Nodes.Clear(); svm.Edges.Clear();
                        var map = new System.Collections.Generic.Dictionary<object, IFC_Viewer_00.ViewModels.SchematicNodeView>();
                        foreach (var n in data.Nodes)
                        {
                            var nv = new IFC_Viewer_00.ViewModels.SchematicNodeView
                            {
                                Node = n,
                                X = n.Position2D.X * svm.Scale,
                                Y = n.Position2D.Y * svm.Scale,
                                NodeBrush = System.Windows.Media.Brushes.SteelBlue
                            };
                            map[n] = nv; svm.Nodes.Add(nv);
                        }
                        foreach (var e2 in data.Edges)
                        {
                            if (e2.StartNode == null || e2.EndNode == null) continue;
                            if (!map.TryGetValue(e2.StartNode, out var s)) continue;
                            if (!map.TryGetValue(e2.EndNode, out var t)) continue;
                            var ev = new IFC_Viewer_00.ViewModels.SchematicEdgeView
                            {
                                Edge = e2,
                                Start = s,
                                End = t,
                                EdgeBrush = System.Windows.Media.Brushes.DarkSlateGray
                            };
                            svm.Edges.Add(ev);
                        }
                    }
                    catch { }
                }

                var win = new IFC_Viewer_00.Views.SchematicView { DataContext = svm, Owner = this };
                win.Show();
                if (data.Edges.Count == 0)
                {
                    MessageBox.Show(this, "模型未含 Ports 關係，僅顯示節點（Mock 仍可顯示邊）。", "原理圖", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, $"載入 Mock 原理圖失敗: {ex.Message}", "原理圖", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // TreeView 選取同步（SelectedItemChanged 事件橋接）
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            try { System.Diagnostics.Trace.WriteLine($"[View] TreeView selection changed. Source: User Action."); } catch { }
            if (_suppressTreeViewEvent) return;
            if (DataContext != null)
            {
                var newNode = e.NewValue as IFC_Viewer_00.Models.SpatialNode;
                if (newNode == null) return;
                // 避免無限迴圈：只有不同時才賦值
                try
                {
                    var current = ((dynamic)DataContext).SelectedNode;
                    if (!object.Equals(current, newNode))
                    {
                        ((dynamic)DataContext).SelectedNode = newNode;
                    }
                }
                catch { }
            }
        }

        private void TreeCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.CheckBox cb && cb.DataContext is IFC_Viewer_00.Models.SpatialNode node)
                {
                    var id = (node.Entity as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0;
                    if (id != 0) _selectionService.Add(id, SelectionOrigin.TreeView);
                }
            }
            catch { }
        }

        private void TreeCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.CheckBox cb && cb.DataContext is IFC_Viewer_00.Models.SpatialNode node)
                {
                    var id = (node.Entity as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0;
                    if (id != 0) _selectionService.Remove(id, SelectionOrigin.TreeView);
                }
            }
            catch { }
        }

        // 任務 2：Shift/Ctrl 多選
        private IFC_Viewer_00.Models.SpatialNode? _lastSelectedNode;
        private void TreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                try { System.Diagnostics.Trace.WriteLine($"[View] TreeView selection changed. Source: User Action."); } catch { }
                var tree = sender as TreeView;
                if (tree == null || DataContext == null) return;

                // 命中測試：找出被點擊的 TreeViewItem 與其 DataContext（SpatialNode）
                var orig = e.OriginalSource as DependencyObject;
                // 若點在 Expander（ToggleButton）或 CheckBox 上，讓 WPF 預設行為（展開/勾選）處理，不覆寫
                if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(orig) != null)
                {
                    return; // 不要攔截，確保展開/收合正常
                }
                if (FindAncestor<CheckBox>(orig) != null)
                {
                    return; // 不要攔截，讓勾選事件照常處理
                }
                var tvi = FindAncestor<TreeViewItem>(orig);
                if (tvi == null)
                {
                    // 點在空白處：清空選取（優先以命令執行）
                    try
                    {
                        var cmd = (System.Windows.Input.ICommand?)((dynamic)DataContext).ClearSelectionCommand;
                        if (cmd != null && cmd.CanExecute(null)) cmd.Execute(null);
                        else ((dynamic)DataContext).ClearSelection();
                    }
                    catch { }
                    e.Handled = true;
                    return;
                }
                var node = tvi.DataContext as IFC_Viewer_00.Models.SpatialNode;
                if (node == null) return;

                bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

                // 取得 VM 與完整扁平化列表（前序遍歷）
                var vm = DataContext as dynamic;
                ObservableCollection<IFC_Viewer_00.Models.SpatialNode>? root = null;
                try { root = (ObservableCollection<IFC_Viewer_00.Models.SpatialNode>?)vm?.Hierarchy; } catch { }
                if (root == null) return;
                var flat = new System.Collections.Generic.List<IFC_Viewer_00.Models.SpatialNode>();
                void Walk(System.Collections.ObjectModel.ObservableCollection<IFC_Viewer_00.Models.SpatialNode> nodes)
                {
                    foreach (var n in nodes)
                    {
                        flat.Add(n);
                        if (n.Children != null && n.Children.Count > 0) Walk(n.Children);
                    }
                }
                Walk(root);

                if (shift && _lastSelectedNode != null)
                {
                    // Shift：範圍選取（前序扁平化索引間的所有節點）
                    int i0 = flat.IndexOf(_lastSelectedNode);
                    int i1 = flat.IndexOf(node);
                    if (i0 >= 0 && i1 >= 0)
                    {
                        if (i0 > i1) { var tmp = i0; i0 = i1; i1 = tmp; }
                        for (int i = i0; i <= i1; i++) flat[i].IsSelected = true;
                    }
                }
                else if (ctrl)
                {
                    // Ctrl：切換目前節點
                    node.IsSelected = !node.IsSelected;
                    _lastSelectedNode = node.IsSelected ? node : _lastSelectedNode;
                }
                else
                {
                    // 單選：若原本就只有此節點選中，則不做全樹清空（避免大量變更）
                    bool alreadyOnlyThis = flat.Count(n => n.IsSelected) == 1 && node.IsSelected;
                    if (!alreadyOnlyThis)
                    {
                        foreach (var n in flat)
                        {
                            if (!ReferenceEquals(n, node) && n.IsSelected) n.IsSelected = false;
                        }
                    }
                    node.IsSelected = true;
                    _lastSelectedNode = node;
                }

                // 阻止 TreeView 預設選取行為，避免與自定邏輯衝突
                e.Handled = true;
            }
            catch { }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T ok) return ok;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
