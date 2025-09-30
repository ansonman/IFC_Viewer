using System.Collections.ObjectModel;

namespace IFC_Viewer_00.Models
{
    public class SchemaNode
    {
        // 屬性名稱（如 Name、GlobalId）
        public string PropertyName { get; set; } = string.Empty;
        // 屬性值的文字表示
        public string PropertyValue { get; set; } = string.Empty;
        // 屬性的 C# 型別名稱（如 IfcLabel、String）
        public string PropertyType { get; set; } = string.Empty;
        // 子節點集合
        public ObservableCollection<SchemaNode> Children { get; set; } = new();
    }
}
