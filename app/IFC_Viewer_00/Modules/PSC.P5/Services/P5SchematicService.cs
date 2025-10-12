using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using IFC_Viewer_00.Services;
using IFC_Viewer_00.ViewModels;
using IFC_Viewer_00.Views;
using IFC_Viewer_00.Utils;
using Xbim.Ifc;

namespace IFC_Viewer_00.Modules.PSC.P5.Services
{
    /// <summary>
    /// PSC P5 入口服務：功能等同 P4（PipeAxes + Terminals + Fittings），作為未來擴充之模組化入口。
    /// </summary>
    public class P5SchematicService
    {
        private readonly ISelectionService _selection;
        public P5SchematicService(ISelectionService selection)
        {
            _selection = selection;
        }

        /// <summary>
        /// 顯示 P5 視圖（會詢問投影平面，預設 XY；flipY=true 對齊 Canvas）。
        /// </summary>
        public async Task ShowAsync(IfcStore model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            // 統一使用 UIHelpers 進行平面選擇（取消或錯誤則回傳 XY）
            string plane = UIHelpers.SelectPlaneOrDefault();

            var service = new SchematicService();
            var options = new PipeAxesOptions { IncludeFittings = true, IncludeTerminals = true };
            var data = await service.GeneratePipeAxesWithTerminalsAsync(model, plane, flipY: true, options);
            if (data.Nodes.Count == 0)
            {
                MessageBox.Show("PSC P5：模型中沒有可解析的資料。", "PSC P5", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var svm = new SchematicViewModel(service, _selection)
            {
                CanvasWidth = 1200,
                CanvasHeight = 800,
                CanvasPadding = 40
            };
            svm.AddLog("[PSC P5] 入口：功能等同 P4（保留未來擴充）");
            try { svm.CurrentModel = model; }
            catch { try { svm.AddLog("[PSC P5] 設定 CurrentModel 失敗（非致命）"); } catch { } }
            await svm.LoadPipeAxesAsync(data);
            svm.AddLog($"生成管段軸線+終端紅點+Fittings（P5）：Nodes={data.Nodes.Count} Plane={plane}");
            var view = new SchematicView { DataContext = svm };
            view.Title = $"PSC P5 - {data.Edges.Count} 段 ({plane})";
            view.Show();
        }
    }
}
