using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using IFC_Viewer_00.Models;
using Xbim.Common;

// 專案內既有慣例：以 IXbimEntity = IPersistEntity 作為別名型別使用
using IXbimEntity = Xbim.Common.IPersistEntity;

namespace IFC_Viewer_00.Services
{
    public class SchemaService
    {
        public SchemaNode GenerateSchemaTree(IXbimEntity entity, string rootName = "Root")
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            var root = new SchemaNode
            {
                PropertyName = rootName,
                PropertyType = entity.GetType().Name,
                PropertyValue = DescribeEntity(entity)
            };
            BuildTreeRecursive(entity, root);
            return root;
        }

        private void BuildTreeRecursive(IXbimEntity entity, SchemaNode parentNode, int maxDepth = 5, int currentDepth = 0)
        {
            if (entity == null) return;
            if (currentDepth >= maxDepth) return;

            var type = entity.GetType();
            PropertyInfo[] props;
            try { props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public); }
            catch { return; }

            foreach (var pi in props)
            {
                SchemaNode node = new SchemaNode
                {
                    PropertyName = pi.Name,
                    PropertyType = SafeTypeName(pi.PropertyType)
                };

                object? value = null;
                try { value = pi.GetValue(entity); }
                catch { /* 一些屬性可能 throw，忽略並以空值呈現 */ }

                // 判斷型態
                if (value == null || IsSimple(pi.PropertyType))
                {
                    node.PropertyValue = value == null ? "null" : ToScalarString(value);
                }
                else if (value is IXbimEntity childEnt)
                {
                    node.PropertyValue = DescribeEntity(childEnt);
                    BuildTreeRecursive(childEnt, node, maxDepth, currentDepth + 1);
                }
                else if (value is IEnumerable enumerable && !(value is string))
                {
                    // 集合型別：逐項處理，若項目是 IXbimEntity 則遞迴
                    int idx = 0;
                    foreach (var item in enumerable)
                    {
                        var child = new SchemaNode
                        {
                            PropertyName = $"[{idx}]",
                            PropertyType = item?.GetType().Name ?? "null",
                            PropertyValue = item == null ? "null" : (IsSimple(item.GetType()) ? ToScalarString(item) : DescribeEntityIfPossible(item))
                        };
                        idx++;

                        if (item is IXbimEntity entItem)
                        {
                            BuildTreeRecursive(entItem, child, maxDepth, currentDepth + 1);
                        }

                        node.Children.Add(child);
                    }
                }
                else
                {
                    // 複合但非 IXbimEntity：以 ToString 略述
                    node.PropertyValue = DescribeEntityIfPossible(value);
                }

                parentNode.Children.Add(node);
            }
        }

        private static bool IsSimple(Type t)
        {
            if (t.IsPrimitive) return true;
            if (t.IsEnum) return true;
            if (t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid)) return true;
            // xBIM 常見簡單值類型（如 IfcLabel/IfcText 等）通常可用 ToString 呈現，這裡歸為非複合處理
            return false;
        }

        private static string ToScalarString(object v)
        {
            try { return v?.ToString() ?? string.Empty; } catch { return string.Empty; }
        }

        private static string SafeTypeName(Type t)
        {
            try { return t.Name; } catch { return "UnknownType"; }
        }

        private static string DescribeEntity(IXbimEntity e)
        {
            try
            {
                var typeName = e.GetType().Name;
                var label = (e as IPersistEntity)?.EntityLabel;
                return label.HasValue ? $"#{label.Value} - {typeName}" : typeName;
            }
            catch { return "Entity"; }
        }

        private static string DescribeEntityIfPossible(object obj)
        {
            if (obj is IXbimEntity ent) return DescribeEntity(ent);
            return ToScalarString(obj);
        }
    }
}
