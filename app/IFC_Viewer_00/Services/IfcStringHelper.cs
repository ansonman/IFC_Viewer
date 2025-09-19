namespace IFC_Viewer_00.Services
{
    public static class IfcStringHelper
    {
        public static string FromValue(object? raw)
            => raw as string ?? (raw != null ? raw.ToString() ?? string.Empty : string.Empty);
    }
}
