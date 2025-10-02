namespace IfcSchemaViewer.Services
{
    public static class IfcStringHelper
    {
        public static string FromValue(object? raw)
            => raw as string ?? (raw != null ? raw.ToString() ?? string.Empty : string.Empty);
    }
}
