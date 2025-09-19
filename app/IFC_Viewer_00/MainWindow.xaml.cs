using System.Windows;

namespace IFC_Viewer_00
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
            
            // 設定 DataContext 為 MainViewModel 的新執行個體
            // 這樣 XAML 中的綁定就能與 ViewModel 連接
            DataContext = new MainViewModel();
        }
    }
}