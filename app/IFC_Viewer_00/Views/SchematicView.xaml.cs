using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using IFC_Viewer_00.Services;
using IFC_Viewer_00.ViewModels;
using IFC_Viewer_00.Views.Dialogs;

namespace IFC_Viewer_00.Views
{
    public partial class SchematicView : Window
    {
        private Point? _lastPanPoint;
        private double _currentScale = 1.0;
        private const double MinScale = 0.1;
        private const double MaxScale = 10.0;
        private const double ZoomFactor = 1.15; // 每格滾輪的縮放倍率
    // 已取消：橡皮筋框選
        // 測試用：顯示 Zoom 錨點
        private Ellipse? _zoomMarker;
        private DispatcherTimer? _zoomMarkerTimer;

        public SchematicView()
        {
            try
            {
                var init = GetType().GetMethod("InitializeComponent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                init?.Invoke(this, null);
            }
            catch { }
            this.Loaded += SchematicView_Loaded;
            this.PreviewMouseWheel += OnPreviewMouseWheel;
            // 中鍵平移仍用 Window 級事件（避免子元素干擾）
            this.MouseDown += OnMouseDown;
            this.MouseMove += OnMouseMove;
            this.MouseUp += OnMouseUp;

            // 初始化 Zoom Marker 計時器
            _zoomMarkerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _zoomMarkerTimer.Tick += (s, e) =>
            {
                try
                {
                    _zoomMarkerTimer?.Stop();
                    if (_zoomMarker != null) _zoomMarker.Visibility = Visibility.Collapsed;
                }
                catch { }
            };
        }

        private void SchematicView_Loaded(object sender, RoutedEventArgs e)
        {
            // 嘗試從 Owner 取得 3D 服務與 VM 以進行同步
            try
            {
                if (this.DataContext is SchematicViewModel svm)
                {
                    // 初始化縮放中心（使用 0,0；我們用 translate 配合滑鼠位置維持錨點）
                    try
                    {
                        var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                        if (canvas != null)
                        {
                            var tg = canvas.RenderTransform as System.Windows.Media.TransformGroup;
                            if (tg != null)
                            {
                                if (tg.Children.Count >= 2 && tg.Children[0] is System.Windows.Media.ScaleTransform sc)
                                {
                                    sc.CenterX = 0.0;
                                    sc.CenterY = 0.0;
                                }
                            }
                        }
                    }
                    catch { }

                    var owner = this.Owner as MainWindow;
                    if (owner != null)
                    {
                        var field = typeof(MainWindow).GetField("_viewerService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var svc = field?.GetValue(owner) as IViewer3DService;
                        var selField = typeof(MainWindow).GetField("_selectionService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var sel = selField?.GetValue(owner) as ISelectionService;
                        if (svc != null)
                        {
                            svm.RequestHighlight += (entity, zoom) =>
                            {
                                try
                                {
                                    // 以 IIfcObject 嘗試，若不是則忽略
                                    if (entity is Xbim.Ifc4.Interfaces.IIfcObject obj)
                                    {
                                        var lbl = (obj as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0;
                                        if (lbl != 0) svc.HighlightEntities(new[] { lbl }, true);
                                        else svc.HighlightEntities(new[] { (Xbim.Common.IPersistEntity)obj });
                                        if (zoom)
                                        {
                                            // 透過 MainWindow 的 ZoomSelected 流程
                                            var host = owner.FindName("ViewerHost") as System.Windows.Controls.ContentControl;
                                            if (host?.Content != null)
                                            {
                                                var mi = host.Content.GetType().GetMethod("ZoomSelected");
                                                mi?.Invoke(host.Content, null);
                                            }
                                        }
                                    }
                                }
                                catch { }
                            };
                        }

                        // 監聽全域選取變更，更新原理圖節點/邊的選取樣式
                        if (sel != null)
                        {
                            sel.SelectionChanged += (s2, e2) =>
                            {
                                try
                                {
                                    var set = sel.Selected.ToHashSet();
                                    foreach (var nv in svm.Nodes)
                                    {
                                        var id = (nv.Node.Entity as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0;
                                        nv.IsSelected = id != 0 && set.Contains(id);
                                    }
                                    foreach (var ev in svm.Edges)
                                    {
                                        var sid = (ev.Start.Node.Entity as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0;
                                        var tid = (ev.End.Node.Entity as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0;
                                        ev.IsSelected = (sid != 0 && set.Contains(sid)) || (tid != 0 && set.Contains(tid));
                                    }
                                    // 同步 3D 高亮（先清理，再套用新選取）
                                    if (svc != null)
                                    {
                                        // 先清空
                                        svc.HighlightEntities(Array.Empty<int>(), clearPrevious: true);
                                        // 再高亮目前選取
                                        var labels = sel.Selected?.ToArray() ?? Array.Empty<int>();
                                        if (labels.Length > 0)
                                            svc.HighlightEntities(labels, clearPrevious: false);
                                    }
                                }
                                catch { }
                            };
                        }
                    }
                }
            }
            catch { }
        }

        // 刪除：橡皮筋相關輔助與事件處理器（不再需要）

        private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            try
            {
                var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                if (canvas == null) return;
                var tg = canvas.RenderTransform as System.Windows.Media.TransformGroup;
                if (tg == null) return;
                var scale = tg.Children[0] as System.Windows.Media.ScaleTransform;
                var translate = tg.Children[1] as System.Windows.Media.TranslateTransform;
                if (scale == null || translate == null) return;

                // 以滑鼠位置為中心縮放（任何位置皆適用）
                var mousePos = e.GetPosition(canvas);
                // 取得當前內容座標（RenderTransform 之前的邏輯座標）
                var m = tg.Value;
                if (!m.HasInverse) return;
                m.Invert();
                var contentPt = m.Transform(mousePos);

                double zoom = e.Delta > 0 ? ZoomFactor : (1.0 / ZoomFactor);
                double newScale = Math.Max(MinScale, Math.Min(MaxScale, _currentScale * zoom));
                _currentScale = newScale;

                // 更新縮放
                scale.ScaleX = _currentScale;
                scale.ScaleY = _currentScale;
                // 設定平移，讓 contentPt 在縮放後仍落在 mousePos
                translate.X = mousePos.X - contentPt.X * _currentScale;
                translate.Y = mousePos.Y - contentPt.Y * _currentScale;

                // 顯示 Zoom 中心標記（半透明藍點）
                ShowZoomMarker(contentPt);

                e.Handled = true;
            }
            catch { }
        }

        private void ShowZoomMarker(Point contentPt)
        {
            try
            {
                // 若未開啟診斷顯示，隱藏並返回
                if (this.DataContext is SchematicViewModel vm && !vm.ShowZoomAnchor)
                {
                    if (_zoomMarker != null) _zoomMarker.Visibility = Visibility.Collapsed;
                    _zoomMarkerTimer?.Stop();
                    return;
                }
                var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                if (canvas == null) return;
                if (_zoomMarker == null)
                {
                    _zoomMarker = new Ellipse
                    {
                        Width = 10,
                        Height = 10,
                        Fill = new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x7A, 0xCC)), // 半透明藍
                        Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x7A, 0xCC)),
                        StrokeThickness = 1,
                        Visibility = Visibility.Collapsed,
                        IsHitTestVisible = false
                    };
                    canvas.Children.Add(_zoomMarker);
                }
                // 標記使用內容座標，受 RenderTransform 影響
                System.Windows.Controls.Canvas.SetLeft(_zoomMarker, contentPt.X - _zoomMarker.Width / 2);
                System.Windows.Controls.Canvas.SetTop(_zoomMarker, contentPt.Y - _zoomMarker.Height / 2);
                _zoomMarker.Visibility = Visibility.Visible;
                _zoomMarkerTimer?.Stop();
                _zoomMarkerTimer?.Start();
            }
            catch { }
        }

        private void OnMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton == System.Windows.Input.MouseButton.Middle)
                {
                    _lastPanPoint = e.GetPosition(this);
                    this.Cursor = System.Windows.Input.Cursors.SizeAll;
                    this.CaptureMouse();
                }
            }
            catch { }
        }

        private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (_lastPanPoint.HasValue && e.MiddleButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                    if (canvas == null) return;
                    var tg = canvas.RenderTransform as System.Windows.Media.TransformGroup;
                    if (tg == null) return;
                    var translate = tg.Children[1] as System.Windows.Media.TranslateTransform;
                    if (translate == null) return;

                    var pos = e.GetPosition(this);
                    var dx = pos.X - _lastPanPoint.Value.X;
                    var dy = pos.Y - _lastPanPoint.Value.Y;
                    _lastPanPoint = pos;

                    translate.X += dx;
                    translate.Y += dy;
                }
            }
            catch { }
        }

        private void OnMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton == System.Windows.Input.MouseButton.Middle)
                {
                    _lastPanPoint = null;
                    this.Cursor = System.Windows.Input.Cursors.Arrow;
                    this.ReleaseMouseCapture();
                }
            }
            catch { }
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                if (canvas != null)
                {
                    var tg = canvas.RenderTransform as System.Windows.Media.TransformGroup;
                    if (tg != null)
                    {
                        if (tg.Children.Count >= 2 && tg.Children[0] is System.Windows.Media.ScaleTransform sc && tg.Children[1] is System.Windows.Media.TranslateTransform tt)
                        {
                            _currentScale = 1.0;
                            sc.ScaleX = sc.ScaleY = 1.0;
                            tt.X = tt.Y = 0.0;
                        }
                    }
                }

                if (this.DataContext is SchematicViewModel vm)
                {
                    vm.RefitToCanvas();
                }
            }
            catch { }
        }

        private void Relayout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 先重置縮放/平移，避免視覺干擾
                var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                if (canvas != null)
                {
                    var tg = canvas.RenderTransform as System.Windows.Media.TransformGroup;
                    if (tg != null)
                    {
                        if (tg.Children.Count >= 2 && tg.Children[0] is System.Windows.Media.ScaleTransform sc && tg.Children[1] is System.Windows.Media.TranslateTransform tt)
                        {
                            _currentScale = 1.0;
                            sc.ScaleX = sc.ScaleY = 1.0;
                            tt.X = tt.Y = 0.0;
                        }
                    }
                }

                if (this.DataContext is SchematicViewModel vm)
                {
                    vm.Relayout();
                }
            }
            catch { }
        }

        private void SavePng_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                if (canvas == null) return;

                // 暫時移除平移與縮放，導出原始畫布內容（或可保留當前視圖，這裡選保留當前視圖）
                // 我們保留當前視圖效果：直接渲染當前 RenderTransform 後的外觀

                // 尺寸：使用實際呈現大小
                int width = (int)Math.Max(1, canvas.ActualWidth);
                int height = (int)Math.Max(1, canvas.ActualHeight);
                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

                // 需要測量/安排以確保 Visual 可被正確渲染
                var size = new Size(canvas.ActualWidth, canvas.ActualHeight);
                canvas.Measure(size);
                canvas.Arrange(new Rect(size));

                rtb.Render(canvas);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                // 儲存檔案（使用標準儲存對話框）
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PNG Image (*.png)|*.png",
                    FileName = $"Schematic_{DateTime.Now:yyyyMMdd_HHmmss}.png"
                };
                if (sfd.ShowDialog(this) == true)
                {
                    using var fs = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write);
                    encoder.Save(fs);
                }
            }
            catch { }
        }

        private void PickTerminalColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new WF.ColorDialog();
                if (dlg.ShowDialog() == WF.DialogResult.OK && this.DataContext is SchematicViewModel vm)
                {
                    var c = System.Windows.Media.Color.FromArgb(255, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    vm.SetColors(terminal: new System.Windows.Media.SolidColorBrush(c));
                }
            }
            catch { }
        }

        private void PickPipeNodeColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new WF.ColorDialog();
                if (dlg.ShowDialog() == WF.DialogResult.OK && this.DataContext is SchematicViewModel vm)
                {
                    var c = System.Windows.Media.Color.FromArgb(255, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    vm.SetColors(pipeNode: new System.Windows.Media.SolidColorBrush(c));
                }
            }
            catch { }
        }

        private void PickPipeEdgeColor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new WF.ColorDialog();
                if (dlg.ShowDialog() == WF.DialogResult.OK && this.DataContext is SchematicViewModel vm)
                {
                    var c = System.Windows.Media.Color.FromArgb(255, dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    vm.SetColors(pipeEdge: new System.Windows.Media.SolidColorBrush(c));
                }
            }
            catch { }
        }

        private void OpenSystemFilter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (this.DataContext is not SchematicViewModel vm) return;
                // 以相同集合做編輯（即時預覽），若要先預覽再套用可改成複本
                var dlg = new SystemFilterDialog { Owner = this, DataContext = vm.Systems };
                var ok = dlg.ShowDialog();
                if (ok == true)
                {
                    vm.ApplySystemVisibilityNow();
                }
                else
                {
                    // 取消不需特別處理（我們直接編輯同一集合，取消僅表示不額外動作）
                    vm.ApplySystemVisibilityNow();
                }
            }
            catch { }
        }
    }
}
