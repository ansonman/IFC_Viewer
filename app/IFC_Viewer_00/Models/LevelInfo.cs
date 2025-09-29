using System;

namespace IFC_Viewer_00.Models
{
    public class LevelInfo
    {
        public string Name { get; set; } = string.Empty;
        // Elevation in model units (typically mm in xBIM contexts where OneMetre=1000)
        public double Elevation { get; set; }
    }
}
