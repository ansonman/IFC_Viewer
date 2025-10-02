namespace IfcSchemaViewer.Services
{
    public static class IfcStringHelper
    {
        public static string FromValue(object? v)
        {
            try { return v?.ToString() ?? string.Empty; } catch { return string.Empty; }
        }
    }
}
