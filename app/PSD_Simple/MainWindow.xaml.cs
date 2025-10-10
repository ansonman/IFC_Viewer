using Microsoft.Win32;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using IFC_Viewer_00.Services;
using IFC_Viewer_00.ViewModels;
using Xbim.Ifc;

namespace PSD_Simple
{
    public partial class MainWindow : Window
    {
        private SchematicService _service = new SchematicService();
        private IFC_Viewer_00.Models.SchematicData? _data;
        private SchematicViewModel? _vm;
        private IFC_Viewer_00.Views.SchematicView? _view;
        private string _currentPlane = "YZ"; // 預設 YZ
        private string? _ifcPath; // 記錄已開啟檔案路徑，切換投影面時可重算

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void OpenIfc_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "IFC files (*.ifc)|*.ifc|All files (*.*)|*.*" };
            if (ofd.ShowDialog() != true) return;
            try
            {
                _ifcPath = ofd.FileName;
                using var model = IfcStore.Open(_ifcPath);
                // 與 PSD P3 一致：管段軸線 + FlowTerminal 紅點（依選面投影）
                _data = await _service.GeneratePipeAxesWithTerminalsAsync(model, _currentPlane, flipY: true);
                await EnsureViewModelAndLoadAsync();
                MessageBox.Show(this, "IFC 載入完成。可開啟原理圖視窗或調整投影面重載。", "完成");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"載入失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task EnsureViewModelAndLoadAsync()
        {
            if (_data == null) return;
            if (_vm == null)
            {
                _vm = new SchematicViewModel(_service, selection: null)
                {
                    // 預設值：依需求覆寫
                    GeometryTolerancePx = 10,
                    MergeAcrossLevels = true,
                };
            }
            await _vm.LoadProjectedAsync(_data);
        }

        // P3 模式下不需要手動 Reproject，資料服務已依 plane 投影完成

        private async void PlaneCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var idx = PlaneCombo.SelectedIndex;
            _currentPlane = idx switch { 0 => "XY", 1 => "XZ", _ => "YZ" };
            // 依新平面重算（與 PSD P3 同步）：重新開檔以產生新資料
            if (!string.IsNullOrEmpty(_ifcPath))
            {
                try
                {
                    using var model = IfcStore.Open(_ifcPath);
                    _data = await _service.GeneratePipeAxesWithTerminalsAsync(model, _currentPlane, flipY: true);
                    if (_vm != null && _data != null)
                    {
                        await _vm.LoadProjectedAsync(_data);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"重新投影失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void OpenSchematic_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null)
            {
                if (_data == null)
                {
                    MessageBox.Show(this, "請先開啟 IFC 檔。", "提示");
                    return;
                }
                await EnsureViewModelAndLoadAsync();
            }
            if (_view == null)
            {
                _view = new IFC_Viewer_00.Views.SchematicView { DataContext = _vm };
                _view.Owner = this;
                _view.Closed += (s, _) => { _view = null; };
                _view.Show();
            }
            else
            {
                _view.Activate();
            }
        }
    }
}
