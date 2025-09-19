using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using Xbim.Ifc;

namespace IFC_Viewer_00
{
    public partial class MainViewModel : ObservableObject
    {
    [ObservableProperty]
    private IfcStore? model;

        [ObservableProperty]
        private string? statusMessage;

        public RelayCommand OpenFileCommand { get; }

        public MainViewModel()
        {
            OpenFileCommand = new RelayCommand(async () => await OnOpenFileAsync());
        }

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
                    var loadedModel = await Task.Run(() => IfcStore.Open(filePath));
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
    }
}