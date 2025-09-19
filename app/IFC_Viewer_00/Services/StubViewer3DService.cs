using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.Common;

namespace IFC_Viewer_00.Services
{
    /// <summary>
    /// 開發初期的暫時實作，不做任何實際 3D 操作。
    /// 未來以 WindowsUI 控制項包裝實作替換。
    /// </summary>
    public class StubViewer3DService : IViewer3DService
    {
        public void SetModel(IModel? model) { /* no-op */ }
        public void SetModel(IfcStore? model) { /* no-op */ }
        public void ResetCamera() { /* no-op */ }
        public void HighlightEntity(IIfcObject? entity, bool clearPrevious = true) { /* no-op */ }
        public void Isolate(IIfcObject? entity) { /* no-op */ }
        public void Hide(IIfcObject? entity, bool recursive = true) { /* no-op */ }
        public void ShowAll() { /* no-op */ }
        public IIfcObject? HitTest(double x, double y) => null;
    }
}
