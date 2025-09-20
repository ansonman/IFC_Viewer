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
                        if (svc != null)
                        {
                            svm.RequestHighlight += (entity, zoom) =>
                            {
                                try
                                {
                                    // 以 IIfcObject 嘗試，若不是則忽略
                                    if (entity is Xbim.Ifc4.Interfaces.IIfcObject obj)
                                    {
                                        svc.HighlightEntity(obj, true);
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
                    }
                }
            }
            catch { }
        }
    }
}
