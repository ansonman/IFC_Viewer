using System;

namespace IFC_Viewer_00.Tests
{
    // 簡易假控制項：具備可寫 Model 與 Tag 屬性，讓 WindowsUiViewer3DService 可以設定與驗證
    public class FakeViewerControl
    {
        public object? Model { get; set; }
        public object? Tag { get; set; }

        // 提供一些服務會探測的方法雛型（不實作，只為反射找到）
        public void ResetCamera() { }
        public void ShowAll() { }
        public void Refresh() { }
    }
}
