using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.Common;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace IFC_Viewer_00.Services
{
    /// <summary>
    /// 3D 檢視器抽象介面：封裝 3D 控制項常用操作，以便未來替換實作（xBIM WindowsUI 新版等）。
    /// </summary>
    public interface IViewer3DService
    {
        // 支援兩種模型多載：IModel 與 IfcStore，方便逐步過渡
        void SetModel(IModel? model);
        void SetModel(IfcStore? model);
        void ResetCamera();

        // 多筆版本（盡力而為：若控制項僅支援單選，至少會高亮最後一個）
        void HighlightEntities(IEnumerable<int> entityLabels, bool clearPrevious = true);
        // 依據 IXbimEntity（IPersistEntity/IIfcObject 皆可）直接高亮多筆
        void HighlightEntities(IEnumerable<IPersistEntity> entitiesToHighlight);
        void Isolate(IIfcObject? entity);
        void Isolate(IEnumerable<int> entityLabels);
        void Hide(IIfcObject? entity, bool recursive = true);
        void Hide(IEnumerable<int> entityLabels, bool recursive = true);
        // 任務 1：以完整隱藏清單覆寫控制項 HiddenInstances 並刷新
        void UpdateHiddenList(IEnumerable<IPersistEntity> hiddenEntities);
        void ShowAll();

        IIfcObject? HitTest(double x, double y);

        // 3D Overlay：顯示/清除管段中線與端點（以世界座標渲染於 HelixViewport3D）
        void ShowOverlayPipeAxes(
            IEnumerable<(Point3D Start, Point3D End)> axes,
            IEnumerable<Point3D>? endpoints = null,
            Color? lineColor = null,
            double lineThickness = 2.0,
            Color? pointColor = null,
            double pointSize = 3.0
        );

        void ClearOverlay();
    }
}
