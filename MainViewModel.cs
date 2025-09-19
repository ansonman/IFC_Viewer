using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xbim.Ifc;
using System.Threading.Tasks;

namespace IFC_Viewer_00
{
    /// <summary>
    /// 主視窗的 ViewModel，遵循 MVVM 架構模式
    /// 負責處理 UI 邏輯與模型之間的綁定
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        /// <summary>
        /// 目前載入的 IFC 模型
        /// </summary>
        [ObservableProperty]
        private IStepModel? model;

        /// <summary>
        /// 狀態列訊息（可為空）
        /// </summary>
        [ObservableProperty]
        private string? statusMessage;

        /// <summary>
        /// 開啟 IFC 檔案命令
        /// </summary>
        public RelayCommand OpenFileCommand { get; }

        public MainViewModel()
        {
            OpenFileCommand = new RelayCommand(async () => await OnOpenFileAsync());
        }

        /// <summary>
        /// 開啟檔案並載入 IFC 模型的非同步方法
        /// </summary>
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
                try
                {
                    StatusMessage = "正在載入模型...";
                    var filePath = dialog.FileName;
                    // IStepModel.OpenAsync 需在背景執行緒呼叫
                    var loadedModel = await Task.Run(() => Xbim.Ifc.IStepModel.OpenAsync(filePath));
                    Model = loadedModel;
                    StatusMessage = "模型載入成功！";
                }
                catch (Exception ex)
                {
                    StatusMessage = "模型載入失敗";
                    System.Diagnostics.Debug.WriteLine($"[IFC載入失敗] {ex}");
                }
            }
        }
        // ...existing code...
    }
}