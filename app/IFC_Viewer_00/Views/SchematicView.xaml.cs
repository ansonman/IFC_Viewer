using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WF = System.Windows.Forms;
using IFC_Viewer_00.Services;
using IFC_Viewer_00.ViewModels;

namespace IFC_Viewer_00.Views
{
    public partial class SchematicView : Window
    {
        private Point? _lastPanPoint;
        private double _currentScale = 1.0;
        private const double MinScale = 0.1;
        private const double MaxScale = 10.0;
        private const double ZoomFactor = 1.15; // 每格滾輪的縮放倍率
        // Phase 2: 橡皮筋框選
        private bool _isRubberBandSelecting = false;
        private Point _rubberStart;

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
        }

        private void SchematicView_Loaded(object sender, RoutedEventArgs e)
        {
            // 嘗試從 Owner 取得 3D 服務與 VM 以進行同步
            try
            {
                if (this.DataContext is SchematicViewModel svm)
                {
                    // 綁定 Canvas 的預覽滑鼠事件，以確保不被子元素（節點/邊）攔截
                    try
                    {
                        var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                        if (canvas != null)
                        {
                            canvas.PreviewMouseLeftButtonDown += Canvas_PreviewMouseLeftButtonDown;
                            canvas.PreviewMouseMove += Canvas_PreviewMouseMove;
                            canvas.PreviewMouseLeftButtonUp += Canvas_PreviewMouseLeftButtonUp;
                        }
                    }
                    catch { }

                    // 初始化縮放中心為畫布中心
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
                                    sc.CenterX = canvas.ActualWidth / 2.0;
                                    sc.CenterY = canvas.ActualHeight / 2.0;
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
                                }
                                catch { }
                            };
                        }
                    }
                }
            }
            catch { }
        }

        // 取得 Canvas 內容座標（將 RenderTransform 反轉套用）
        private System.Windows.Point GetContentPosition(System.Windows.Point pOnCanvas)
        {
            try
            {
                var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                if (canvas == null) return pOnCanvas;
                if (canvas.RenderTransform is System.Windows.Media.TransformGroup tg)
                {
                    var m = tg.Value; // Scale 然後 Translate
                    if (m.HasInverse)
                    {
                        m.Invert();
                        return m.Transform(pOnCanvas);
                    }
                }
            }
            catch { }
            return pOnCanvas;
        }

        private void Canvas_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var canvas = sender as System.Windows.Controls.Canvas;
                if (canvas == null) return;
                var p = e.GetPosition(canvas);
                // 使用內容座標（未縮放/平移前的邏輯座標）
                var content = GetContentPosition(p);
                _rubberStart = content;
                _isRubberBandSelecting = true;
                var rect = this.FindName("RubberBand") as System.Windows.Shapes.Rectangle;
                if (rect != null)
                {
                    System.Windows.Controls.Canvas.SetLeft(rect, content.X);
                    System.Windows.Controls.Canvas.SetTop(rect, content.Y);
                    rect.Width = 0;
                    rect.Height = 0;
                    rect.Visibility = Visibility.Visible;
                }
                canvas.CaptureMouse();
                e.Handled = true;
            }
            catch { }
        }

        private void Canvas_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (!_isRubberBandSelecting || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
                var canvas = sender as System.Windows.Controls.Canvas;
                var rect = this.FindName("RubberBand") as System.Windows.Shapes.Rectangle;
                if (canvas == null || rect == null) return;
                var p = e.GetPosition(canvas);
                var content = GetContentPosition(p);
                double x = Math.Min(content.X, _rubberStart.X);
                double y = Math.Min(content.Y, _rubberStart.Y);
                double w = Math.Abs(content.X - _rubberStart.X);
                double h = Math.Abs(content.Y - _rubberStart.Y);
                System.Windows.Controls.Canvas.SetLeft(rect, x);
                System.Windows.Controls.Canvas.SetTop(rect, y);
                rect.Width = w;
                rect.Height = h;
                e.Handled = true;
            }
            catch { }
        }

        private void Canvas_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var canvas = sender as System.Windows.Controls.Canvas;
                var rect = this.FindName("RubberBand") as System.Windows.Shapes.Rectangle;
                if (canvas == null || rect == null) return;
                var x = System.Windows.Controls.Canvas.GetLeft(rect);
                var y = System.Windows.Controls.Canvas.GetTop(rect);
                var w = rect.Width;
                var h = rect.Height;
                rect.Visibility = Visibility.Collapsed;
                _isRubberBandSelecting = false;
                canvas.ReleaseMouseCapture();

                if (w > 2 && h > 2 && this.DataContext is SchematicViewModel vm)
                {
                    var selected = vm.Nodes.Where(n => n.X >= x && n.X <= x + w && n.Y >= y && n.Y <= y + h)
                                           .Select(n => (n.Node.Entity as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0)
                                           .Where(id => id != 0)
                                           .ToList();
                    bool additive = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control
                                 || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift;
                    vm.SelectByEntityLabels(selected, additive);
                }
                e.Handled = true;
            }
            catch { }
        }

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

                var mousePos = e.GetPosition(canvas);
                double zoom = e.Delta > 0 ? ZoomFactor : (1.0 / ZoomFactor);

                double newScale = Math.Max(MinScale, Math.Min(MaxScale, _currentScale * zoom));
                zoom = newScale / _currentScale; // clamp 後的實際倍率
                _currentScale = newScale;

                // 以滑鼠位置為中心縮放（保持滑鼠下的內容固定）
                translate.X = (translate.X - mousePos.X) * zoom + mousePos.X;
                translate.Y = (translate.Y - mousePos.Y) * zoom + mousePos.Y;
                scale.ScaleX = _currentScale;
                scale.ScaleY = _currentScale;

                e.Handled = true;
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
                else if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                {
                    var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                    if (canvas == null) return;
                    var p = e.GetPosition(canvas);
                    _rubberStart = p;
                    _isRubberBandSelecting = true;
                    var rect = this.FindName("RubberBand") as System.Windows.Shapes.Rectangle;
                    if (rect != null)
                    {
                        System.Windows.Controls.Canvas.SetLeft(rect, p.X);
                        System.Windows.Controls.Canvas.SetTop(rect, p.Y);
                        rect.Width = 0;
                        rect.Height = 0;
                        rect.Visibility = Visibility.Visible;
                    }
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
                else if (_isRubberBandSelecting && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                    var rect = this.FindName("RubberBand") as System.Windows.Shapes.Rectangle;
                    if (canvas == null || rect == null) return;
                    var p = e.GetPosition(canvas);
                    double x = Math.Min(p.X, _rubberStart.X);
                    double y = Math.Min(p.Y, _rubberStart.Y);
                    double w = Math.Abs(p.X - _rubberStart.X);
                    double h = Math.Abs(p.Y - _rubberStart.Y);
                    System.Windows.Controls.Canvas.SetLeft(rect, x);
                    System.Windows.Controls.Canvas.SetTop(rect, y);
                    rect.Width = w;
                    rect.Height = h;
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
                else if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                {
                    var rect = this.FindName("RubberBand") as System.Windows.Shapes.Rectangle;
                    var canvas = this.FindName("Canvas") as System.Windows.Controls.Canvas;
                    if (rect != null && canvas != null)
                    {
                        var x = System.Windows.Controls.Canvas.GetLeft(rect);
                        var y = System.Windows.Controls.Canvas.GetTop(rect);
                        var w = rect.Width;
                        var h = rect.Height;
                        rect.Visibility = Visibility.Collapsed;
                        _isRubberBandSelecting = false;

                        if (w > 2 && h > 2 && this.DataContext is SchematicViewModel vm)
                        {
                            // 計算落在矩形中的節點，注意節點位置是以中心點 X/Y
                            var selected = vm.Nodes.Where(n => n.X >= x && n.X <= x + w && n.Y >= y && n.Y <= y + h)
                                                   .Select(n => (n.Node.Entity as Xbim.Common.IPersistEntity)?.EntityLabel ?? 0)
                                                   .Where(id => id != 0)
                                                   .ToList();
                            bool additive = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control
                                         || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift;
                            vm.SelectByEntityLabels(selected, additive);
                        }
                    }
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
    }
}
