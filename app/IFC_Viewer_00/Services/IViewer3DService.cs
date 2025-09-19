using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.Common;

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

        // 實體維持使用 IIfcObject（UI 層主要與 Ifc 介面互動）
        void HighlightEntity(IIfcObject? entity, bool clearPrevious = true);
        void Isolate(IIfcObject? entity);
        void Hide(IIfcObject? entity, bool recursive = true);
        void ShowAll();

        IIfcObject? HitTest(double x, double y);
    }
}
