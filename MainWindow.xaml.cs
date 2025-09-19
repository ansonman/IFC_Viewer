using System.Windows;
using IFC_Viewer_00.ViewModels;

namespace IFC_Viewer_00.Views
{
    /// <summary>
    /// MainWindow.xaml 的互動邏輯
    /// 遵循 MVVM 架構，UI 邏輯主要由 MainViewModel 處理
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}