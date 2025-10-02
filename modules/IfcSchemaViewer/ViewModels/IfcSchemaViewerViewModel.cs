using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Reflection;
using Xbim.Ifc4.Interfaces;
using Xbim.Common;
using IfcSchemaViewer.Services;

namespace IfcSchemaViewer.ViewModels
{
    public class IfcSchemaNode : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string? Value { get; set; }
        public ObservableCollection<IfcSchemaNode> Children { get; set; } = new();
        public bool IsExpanded { get; set; }
        public string NameOnly => Name;
        public string ValueOnly => Value ?? string.Empty;
        public string NameEqualsValue => string.IsNullOrEmpty(Value) ? Name : ($"{Name}={Value}");
    }

    public partial class IfcSchemaViewerViewModel : ObservableObject
    {
        [ObservableProperty]
        private string title = "IFC Schema Viewer";

        [ObservableProperty]
        private ObservableCollection<IfcSchemaNode> rootNodes = new();

        [ObservableProperty]
        private string? filterText;

        [ObservableProperty]
        private ObservableCollection<IfcSchemaNode> viewNodes = new();

        public void LoadFrom(IIfcObject entity)
        {
            RootNodes.Clear();
            if (entity == null) return;

            var root = new IfcSchemaNode
            {
                Name = TryGetTypeName(entity),
                Value = TryGetEntityLabel(entity) is int lbl && lbl != 0 ? $"Label={lbl}" : null
            };
            var basic = new IfcSchemaNode { Name = "Basic" };
            basic.Children.Add(new IfcSchemaNode { Name = "Type", Value = TryGetTypeName(entity) });
            var gid = TryGetGlobalId(entity);
            if (!string.IsNullOrWhiteSpace(gid)) basic.Children.Add(new IfcSchemaNode { Name = "GlobalId", Value = gid });
            var etName = TryGetExpressTypeName(entity);
            if (!string.IsNullOrWhiteSpace(etName)) basic.Children.Add(new IfcSchemaNode { Name = "ExpressType", Value = etName });
            root.Children.Add(basic);

            var attrs = BuildAttributes(entity);
            if (attrs.Children.Count > 0) root.Children.Add(attrs);

            var inv = BuildInverses(entity);
            if (inv.Children.Count > 0) root.Children.Add(inv);

            var psets = BuildPropertySets(entity);
            if (psets.Children.Count > 0) root.Children.Add(psets);

            RootNodes.Add(root);
            ApplyFilter();
            Title = $"IFC Schema Viewer - {TryGetTypeName(entity)}";
        }

        partial void OnFilterTextChanged(string? value)
        {
            ApplyFilter();
        }

        public void ApplyFilter()
        {
            ViewNodes.Clear();
            var text = (FilterText ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                foreach (var r in RootNodes) ViewNodes.Add(CloneShallow(r, includeChildren: true));
                return;
            }
            bool Match(IfcSchemaNode n)
                => (n.Name?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || (n.Value?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

            IfcSchemaNode? FilterNode(IfcSchemaNode n)
            {
                var keep = Match(n);
                var copy = new IfcSchemaNode { Name = n.Name, Value = n.Value };
                foreach (var c in n.Children)
                {
                    var fc = FilterNode(c);
                    if (fc != null) { copy.Children.Add(fc); keep = true; }
                }
                return keep ? copy : null;
            }

            foreach (var r in RootNodes)
            {
                var fr = FilterNode(r);
                if (fr != null) ViewNodes.Add(fr);
            }
        }

        private static IfcSchemaNode CloneShallow(IfcSchemaNode n, bool includeChildren)
        {
            var copy = new IfcSchemaNode { Name = n.Name, Value = n.Value };
            if (includeChildren)
            {
                foreach (var c in n.Children)
                    copy.Children.Add(CloneShallow(c, includeChildren: true));
            }
            return copy;
        }

        private static IfcSchemaNode BuildAttributes(IIfcObject entity)
        {
            var node = new IfcSchemaNode { Name = "Attributes" };
            try
            {
                if (entity is IPersistEntity pe)
                {
                    var et = pe.GetType().GetProperty("ExpressType")?.GetValue(pe);
                    var props = et?.GetType().GetProperty("Properties")?.GetValue(et) as System.Collections.IEnumerable;
                    if (props != null)
                    {
                        foreach (var p in props)
                        {
                            try
                            {
                                var pName = p.GetType().GetProperty("Name")?.GetValue(p) as string;
                                var pi = p.GetType().GetProperty("PropertyInfo")?.GetValue(p) as PropertyInfo;
                                if (!string.IsNullOrWhiteSpace(pName) && pi != null)
                                {
                                    var val = SafeToDisplay(pi.GetValue(entity));
                                    node.Children.Add(new IfcSchemaNode { Name = pName!, Value = val });
                                }
                            }
                            catch { }
                        }
                        return node;
                    }
                }
            }
            catch { }

            try
            {
                foreach (var pi in entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!pi.CanRead) continue;
                    if (pi.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        var val = pi.GetValue(entity);
                        var str = SafeToDisplay(val);
                        node.Children.Add(new IfcSchemaNode { Name = pi.Name, Value = str });
                    }
                    catch { }
                }
            }
            catch { }
            return node;
        }

        private static IfcSchemaNode BuildInverses(IIfcObject entity)
        {
            var node = new IfcSchemaNode { Name = "Inverses" };
            try
            {
                if (entity is IPersistEntity pe)
                {
                    var et = pe.GetType().GetProperty("ExpressType")?.GetValue(pe);
                    object? invProp = et?.GetType().GetProperty("Inverses")?.GetValue(et)
                                   ?? et?.GetType().GetProperty("InverseProperties")?.GetValue(et);
                    if (invProp is System.Collections.IEnumerable invs)
                    {
                        foreach (var inv in invs)
                        {
                            try
                            {
                                var name = inv.GetType().GetProperty("Name")?.GetValue(inv) as string;
                                var pi = inv.GetType().GetProperty("PropertyInfo")?.GetValue(inv) as PropertyInfo;
                                if (!string.IsNullOrWhiteSpace(name) && pi != null)
                                {
                                    var val = pi.GetValue(entity);
                                    var str = SummarizeValue(val);
                                    node.Children.Add(new IfcSchemaNode { Name = name!, Value = str });
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return node;
        }

        private static IfcSchemaNode BuildPropertySets(IIfcObject entity)
        {
            var node = new IfcSchemaNode { Name = "PropertySets" };
            try
            {
                foreach (var rel in entity.IsDefinedBy)
                {
                    if (rel.RelatingPropertyDefinition is IIfcPropertySet pset)
                    {
                        var psetName = IfcStringHelper.FromValue(pset.Name) ?? "(PSet)";
                        var pnode = new IfcSchemaNode { Name = psetName };
                        foreach (var prop in pset.HasProperties)
                        {
                            var name = IfcStringHelper.FromValue(prop.Name) ?? "(Prop)";
                            string val = string.Empty;
                            if (prop is IIfcPropertySingleValue sv && sv.NominalValue != null)
                            {
                                object? raw = sv.NominalValue.Value;
                                val = IfcStringHelper.FromValue(raw);
                            }
                            pnode.Children.Add(new IfcSchemaNode { Name = name, Value = val });
                        }
                        node.Children.Add(pnode);
                    }
                }
            }
            catch { }
            return node;
        }

        private static string TryGetTypeName(IIfcObject entity)
        {
            try
            {
                if (entity is IPersistEntity pe)
                {
                    var et = pe.GetType().GetProperty("ExpressType")?.GetValue(pe);
                    var name = et?.GetType().GetProperty("Name")?.GetValue(et) as string;
                    if (!string.IsNullOrWhiteSpace(name)) return name!;
                }
            }
            catch { }
            return entity.GetType().Name;
        }

        private static string? TryGetExpressTypeName(IIfcObject entity)
        {
            try
            {
                if (entity is IPersistEntity pe)
                {
                    var et = pe.GetType().GetProperty("ExpressType")?.GetValue(pe);
                    var name = et?.GetType().GetProperty("Name")?.GetValue(et) as string;
                    return name;
                }
            }
            catch { }
            return null;
        }

        private static int? TryGetEntityLabel(IIfcObject entity)
        {
            try { return (entity as IPersistEntity)?.EntityLabel; } catch { return null; }
        }

        private static string? TryGetGlobalId(IIfcObject entity)
        {
            try
            {
                if (entity is IIfcRoot root) return IfcStringHelper.FromValue(root.GlobalId);
            }
            catch { }
            return null;
        }

        private static string SafeToDisplay(object? value)
        {
            if (value == null) return string.Empty;
            if (value is System.Collections.IEnumerable enu && value is not string)
            {
                int count = 0;
                string? sample = null;
                foreach (var item in enu)
                {
                    if (count == 0) sample = SummarizeItem(item);
                    count++;
                    if (count > 1) break;
                }
                return count == 0 ? "[]" : (count == 1 ? $"[1] {sample}" : $"[{count}] {sample} ...");
            }
            return SummarizeItem(value);
        }

        private static string SummarizeValue(object? value)
        {
            if (value == null) return string.Empty;
            if (value is System.Collections.IEnumerable enu && value is not string)
            {
                int count = 0;
                foreach (var _ in enu) count++;
                return $"[{count}] items";
            }
            return SummarizeItem(value);
        }

        private static string SummarizeItem(object? value)
        {
            if (value == null) return string.Empty;
            try
            {
                if (value is IIfcObject o)
                {
                    var t = TryGetTypeName(o);
                    var lbl = TryGetEntityLabel(o);
                    var gid = TryGetGlobalId(o);
                    return $"{t}{(lbl is int l && l != 0 ? $" (L{l})" : string.Empty)}{(string.IsNullOrWhiteSpace(gid) ? string.Empty : $", GID={gid}")}";
                }
                if (value is IPersistEntity pe)
                {
                    var t = pe.GetType().Name;
                    return $"{t} (L{pe.EntityLabel})";
                }
                return value.ToString() ?? string.Empty;
            }
            catch { return value?.ToString() ?? string.Empty; }
        }
    }
}
