using System.Threading.Tasks;
using IFC_Viewer_00.Services;
using IFC_Viewer_00.Models;

namespace IFC_Viewer_00.ViewModels
{
    // Backward-compat extension: map old API name to the new one
    public static class SchematicViewModelExtensions
    {
        // Some legacy call sites may still reference this name.
        // Delegate to LoadProjectedAsync which expects pre-projected 2D positions.
        public static Task LoadFromDataWithProvided2DAsync(this SchematicViewModel vm, SchematicData data)
        {
            return vm.LoadProjectedAsync(data);
        }

        // Legacy overload: (data, canvasWidth, canvasHeight, padding)
        public static Task LoadFromDataWithProvided2DAsync(this SchematicViewModel vm, SchematicData data, double canvasWidth, double canvasHeight, double padding)
        {
            vm.CanvasWidth = canvasWidth;
            vm.CanvasHeight = canvasHeight;
            vm.CanvasPadding = padding;
            return vm.LoadProjectedAsync(data);
        }
    }
}
