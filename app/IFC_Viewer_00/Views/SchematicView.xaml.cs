using System;
using System.Windows;
using IFC_Viewer_00.Services;
using IFC_Viewer_00.ViewModels;

namespace IFC_Viewer_00.Views
{
    public partial class SchematicView : Window
    {
        public SchematicView()
        {
            try
            {
                var init = GetType().GetMethod("InitializeComponent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                init?.Invoke(this, null);
            }
            catch { }
            this.Loaded += SchematicView_Loaded;
        }

        private void SchematicView_Loaded(object sender, RoutedEventArgs e)
        {
            // 嘗試從 Owner 取得 3D 服務與 VM 以進行同步
            try
            {
                if (this.DataContext is SchematicViewModel svm)
                {
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
    }
}
