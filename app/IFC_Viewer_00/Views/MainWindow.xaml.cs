using System.Windows;
using IFC_Viewer_00.Services;
using System;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows.Media;
using IFC_Viewer_00.ViewModels;

namespace IFC_Viewer_00.Views
{
    /// <summary>
    /// MainWindow.xaml 的互動邏輯
    /// 遵循 MVVM 架構，UI 邏輯主要由 MainViewModel 處理
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IViewer3DService _viewerService;
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
                        // 優先使用 (IViewer3DService) 建構子
                        var ctor = vmType.GetConstructor(new[] { typeof(IViewer3DService) });
                        vm = ctor != null ? ctor.Invoke(new object?[] { _viewerService }) : Activator.CreateInstance(vmType);
                    }
                }
                catch { }
                DataContext = vm;
                // 監聽 SelectedNode 變更，將 TreeView 的 UI 選取同步
                if (DataContext is INotifyPropertyChanged inpc)
                {
                    inpc.PropertyChanged += ViewModel_PropertyChanged;
                }
                // 嘗試掛載事件（UIElement）
                var uiElem = (host?.Content) as System.Windows.UIElement;
                if (uiElem != null)
                {
                    // 僅在點擊時才選取，避免 hover 自動選取
                    uiElem.MouseLeftButtonDown += (s, e) =>
                    {
                        // 單擊：以 HitTest 設定 HighlightedEntity；雙擊在下方處理 ZoomSelected
                        TryPickUnderMouseAndSetSelection(uiElem, e.GetPosition(uiElem));
                        if (e.ClickCount == 2)
                        {
                            Viewer3D_MouseDoubleClick(s, e);
                        }
                    };
                    uiElem.MouseRightButtonDown += (s, e) =>
                    {
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
                var svm = new SchematicViewModel(service);
                await svm.LoadAsync(iModel);

                // 顯示視窗
                var win = new SchematicView { DataContext = svm, Owner = this };
                win.Show();
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
            if (DataContext != null && this.FindName("ViewerHost") is ContentControl host && host.Content is System.Windows.UIElement viewer)
            {
                // 直接使用目前的 HighlightedEntity 或控制項 SelectedEntity 做為選取
                object? selectedEntity = null;
                try { selectedEntity = ((dynamic)DataContext).HighlightedEntity; } catch { }
                if (selectedEntity == null)
                {
                    // 若 VM 無選取，先嘗試從控制項抓 SelectedEntity
                    try
                    {
                        var pi = host.Content.GetType().GetProperty("SelectedEntity");
                        selectedEntity = pi?.GetValue(host.Content);
                    }
                    catch { }
                }
                if (selectedEntity == null)
                {
                    // 再後援：以當下滑鼠位置 HitTest 一次
                    var p = e.GetPosition(viewer);
                    selectedEntity = _viewerService.HitTest(p.X, p.Y);
                    try { ((dynamic)DataContext).HighlightedEntity = selectedEntity; } catch { }
                }
                if (selectedEntity == null) return;
                // 同步屬性與樹狀；確保 3D 的 SelectedEntity 也設好
                try { ((dynamic)DataContext).UpdateSelectedElementProperties(selectedEntity); } catch { }
                try { ((dynamic)DataContext).SyncTreeViewSelection(selectedEntity); } catch { }
                try { _viewerService.HighlightEntity((Xbim.Ifc4.Interfaces.IIfcObject)selectedEntity, true); } catch { }
                // 排程到下一個 UI 迴圈再 ZoomSelected，避免 Highlight 幾何尚未建立
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TryZoomSelectedOrHome(host.Content);
                    UpdateDiagnostics(host.Content);
                }), DispatcherPriority.Background);
            }
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

        private void Strong_SelectedEntityChanged(object sender, SelectionChangedEventArgs e)
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
                    try { ((dynamic)DataContext).HighlightedEntity = obj; } catch { }
                    try { ((dynamic)DataContext).UpdateSelectedElementProperties(obj); } catch { }
                    try { ((dynamic)DataContext).SyncTreeViewSelection(obj); } catch { }
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

        // TreeView 選取同步（SelectedItemChanged 事件橋接）
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
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
    }
}
