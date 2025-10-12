using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace IFC_Viewer_00.Utils
{
    public static class UIHelpers
    {
        /// <summary>
        /// 取得作用中的 Owner 視窗；若無則回傳 MainWindow；若仍無則回傳 null。
        /// </summary>
        public static Window? GetActiveOwner()
        {
            try
            {
                var active = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
                if (active != null) return active;
                return Application.Current?.MainWindow;
            }
            catch { return null; }
        }

        /// <summary>
        /// 透過對話框選擇投影平面；若對話框失敗或取消，回傳 "XY"。
        /// </summary>
        public static string SelectPlaneOrDefault()
        {
            string plane = "XY";
            try
            {
                var owner = GetActiveOwner();
                var dlgType = Type.GetType("IFC_Viewer_00.Views.PlaneSelectionDialog, IFC_Viewer_00");
                if (dlgType != null)
                {
                    var dlg = Activator.CreateInstance(dlgType);
                    if (dlg is Window win)
                    {
                        win.Owner = owner;
                        var result = win.ShowDialog();
                        if (result == true)
                        {
                            var prop = dlgType.GetProperty("SelectedPlane");
                            if (prop != null)
                            {
                                var val = prop.GetValue(dlg) as string;
                                if (!string.IsNullOrWhiteSpace(val)) plane = val!;
                            }
                        }
                    }
                }
            }
            catch { /* fallback XY */ }
            return plane;
        }
    }
}
