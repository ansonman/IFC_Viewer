using System.Windows;

namespace IFC_Viewer_00.Views
{
    // 臨時空殼：避免缺少 ColorOverrideWindow 造成編譯失敗。
    // 專案目前未提供顏色覆蓋視窗實作；若 UI 無使用此視窗，可後續移除此檔並刪除所有呼叫點。
    public class ColorOverrideWindow : Window
    {
        public ColorOverrideWindow() { this.Title = "Color Override (Stub)"; this.Width = 400; this.Height = 300; }
    }
}
