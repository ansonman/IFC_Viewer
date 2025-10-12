using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using IFC_Viewer_00.Services;
using IFC_Viewer_00.ViewModels;
using IFC_Viewer_00.Views;
using Xbim.Ifc;

namespace IFC_Viewer_00.Modules.PipeAxesWithTerminals.Services
{
    /// <summary>
    /// 可攜式門面服務：在相同投影平面生成管段軸線 + FlowTerminal 紅點，並以 SchematicView 顯示。
    /// 依賴現有的 SchematicService 與 SchematicViewModel，不直接依賴 3D Viewer。
    /// </summary>
    public class PipeAxesWithTerminalsService
    {
        private readonly ISelectionService _selection;
        public PipeAxesWithTerminalsService(ISelectionService selection)
        {
            _selection = selection;
        }

        /// <summary>
        /// 顯示「Pipe軸線 + 紅點」視圖（會詢問投影平面，預設 XY）。
        /// </summary>
        public async Task ShowAsync(IfcStore model)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            string plane = "XY";
            try
            {
                var dlg = new PlaneSelectionDialog { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
                if (dlg.ShowDialog() == true) plane = dlg.SelectedPlane;
            }
            catch { }

            var service = new SchematicService();
            var data = await service.GeneratePipeAxesWithTerminalsAsync(model, plane, flipY: true);
            if (data.Nodes.Count == 0)
            {
                MessageBox.Show("模型中沒有可解析的資料。", "PipeAxis+Terminals", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var svm = new SchematicViewModel(service, _selection)
            {
                CanvasWidth = 1200,
                CanvasHeight = 800,
                CanvasPadding = 40
            };
            // 設定目前模型引用，讓『管網建構(Quick)』可運作
            try { svm.CurrentModel = model; } catch { }
            await svm.LoadPipeAxesAsync(data);
            svm.AddLog($"生成管段軸線+終端紅點：Nodes={data.Nodes.Count} Plane={plane}");
            var view = new SchematicView { DataContext = svm };
            view.Title = $"PipeAxes+Terminals - {data.Edges.Count} 段 ({plane})";
            view.Show();
        }
    }
}
