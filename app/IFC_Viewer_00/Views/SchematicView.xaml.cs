using System.Windows;

namespace IFC_Viewer_00.Views
{
    public partial class SchematicView : Window
    {
        public SchematicView()
        {
            try
            {
                var init = GetType().GetMethod("InitializeComponent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                init?.Invoke(this, null);
            }
            catch { }
        }
    }
}
