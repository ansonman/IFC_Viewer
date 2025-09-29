using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.Common;
using System.Windows.Media;
using System.Windows.Media.Media3D;

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
    public void SetModelOpacity(double opacity) { /* no-op */ }
        public void HighlightEntity(IIfcObject? entity, bool clearPrevious = true) { /* no-op */ }
        public void HighlightEntities(IEnumerable<int> entityLabels, bool clearPrevious = true) { /* no-op */ }
    public void HighlightEntities(IEnumerable<IPersistEntity> entitiesToHighlight) { /* no-op */ }
        public void Isolate(IIfcObject? entity) { /* no-op */ }
        public void Isolate(IEnumerable<int> entityLabels) { /* no-op */ }
        public void Hide(IIfcObject? entity, bool recursive = true) { /* no-op */ }
        public void Hide(IEnumerable<int> entityLabels, bool recursive = true) { /* no-op */ }
    public void UpdateHiddenList(IEnumerable<IPersistEntity> hiddenEntities) { /* no-op */ }
        public void ShowAll() { /* no-op */ }
        public IIfcObject? HitTest(double x, double y) => null;

        public void ShowOverlayPipeAxes(
            IEnumerable<(Point3D Start, Point3D End)> axes,
            IEnumerable<Point3D>? endpoints = null,
            Color? lineColor = null,
            double lineThickness = 2.0,
            Color? pointColor = null,
            double pointSize = 3.0,
              bool applyCameraOffset = false
        ) { /* no-op */ }

        public void ClearOverlay() { /* no-op */ }
    }
}
