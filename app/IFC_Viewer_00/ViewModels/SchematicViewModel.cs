using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using Xbim.Common;
using IFC_Viewer_00.Models;
using IFC_Viewer_00.Services;

namespace IFC_Viewer_00.ViewModels
{
    public partial class SchematicViewModel
    {
        private readonly SchematicService _service;

        public ObservableCollection<SchematicNodeView> Nodes { get; } = new();
        public ObservableCollection<SchematicEdgeView> Edges { get; } = new();

        public double Scale { get; set; } = 0.001; // 粗略縮放，將毫米→公尺（視模型而定）

    // 點擊互動：由 Window/Owner 註冊，轉送到 3D 服務
        public event Action<IPersistEntity, bool>? RequestHighlight; // bool: 是否要求縮放
    private readonly ISelectionService? _selection;

        public ICommand NodeClickCommand { get; }
        public ICommand EdgeClickCommand { get; }

        public SchematicViewModel(SchematicService service, ISelectionService? selection = null)
        {
            _service = service;
            _selection = selection;
            NodeClickCommand = new SchematicCommand(obj =>
            {
                if (obj is SchematicNodeView nv && nv.Node?.Entity != null)
                {
                    var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;
                    var lbl = (nv.Node.Entity as IPersistEntity)?.EntityLabel ?? 0;
                    if (_selection != null && lbl != 0)
                    {
                        if (ctrl)
                        {
                            if (_selection.Selected.Contains(lbl)) _selection.Remove(lbl, SelectionOrigin.Schematic);
                            else _selection.Add(lbl, SelectionOrigin.Schematic);
                        }
                        else
                        {
                            _selection.SetSelection(new[] { lbl }, SelectionOrigin.Schematic);
                        }
                    }
                    RequestHighlight?.Invoke(nv.Node.Entity, !ctrl);
                }
            });
            EdgeClickCommand = new SchematicCommand(obj =>
            {
                if (obj is SchematicEdgeView ev && ev.Start?.Node?.Entity != null)
                {
                    // 以兩端節點加入選取集合
                    var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;
                    var ids = new List<int>();
                    var sId = (ev.Start.Node.Entity as IPersistEntity)?.EntityLabel ?? 0;
                    var tId = (ev.End?.Node?.Entity as IPersistEntity)?.EntityLabel ?? 0;
                    if (sId != 0) ids.Add(sId);
                    if (tId != 0) ids.Add(tId);
                    if (_selection != null && ids.Count > 0)
                    {
                        if (ctrl) _selection.AddRange(ids, SelectionOrigin.Schematic);
                        else _selection.SetSelection(ids, SelectionOrigin.Schematic);
                    }
                    RequestHighlight?.Invoke(ev.Start.Node.Entity, !ctrl);
                }
            });
        }

        public async Task LoadAsync(IModel model)
        {
            var data = await _service.GenerateTopologyAsync(model);
            Nodes.Clear();
            Edges.Clear();

            // 建立 NodeView 並以簡單縮放投影 X/Y
            // 為避免 Id 衝突，優先使用實體參照或 EntityLabel 作為 key
            var nodeMap = new Dictionary<object, SchematicNodeView>();
            foreach (var n in data.Nodes)
            {
                object key = (object?)n.Entity ?? (object)n.Id;
                if (nodeMap.ContainsKey(key))
                {
                    // 退而求其次：用組合鍵防止碰撞
                    key = (n.Entity != null) ? (object)$"{n.Entity.EntityLabel}:{n.Id}" : (object)$"dup:{n.Id}:{nodeMap.Count}";
                }
                var nv = new SchematicNodeView
                {
                    Node = n,
                    X = n.Position3D.X * Scale,
                    Y = -n.Position3D.Y * Scale,
                    NodeBrush = GetBrushByIfcType(n.IfcType)
                };
                nodeMap[key] = nv;
            }

            foreach (var nv in nodeMap.Values)
                Nodes.Add(nv);

            // 建立 EdgeView（參考 NodeView）
            foreach (var e in data.Edges)
            {
                if (e.StartNode == null || e.EndNode == null) continue;
                var s = FindNodeView(nodeMap, e.StartNode);
                var t = FindNodeView(nodeMap, e.EndNode);
                if (s == null || t == null) continue;
                var ev = new SchematicEdgeView
                {
                    Edge = e,
                    Start = s,
                    End = t,
                    EdgeBrush = GetDarkerBrush(s.NodeBrush)
                };
                Edges.Add(ev);
            }

            // 自動佈局（力導向）：在初始座標基礎上做微調，避免大量重疊
            ApplyForceDirectedLayout(Nodes.ToList(), Edges.ToList(), iterations: 200);
        }

        private static SchematicNodeView? FindNodeView(Dictionary<object, SchematicNodeView> map, SchematicNode node)
        {
            if (node.Entity != null && map.TryGetValue(node.Entity, out var byEnt))
                return byEnt;
            // 回退：比對 Id 或組合鍵
            foreach (var kv in map)
            {
                if (ReferenceEquals(kv.Value.Node, node)) return kv.Value;
                if (!string.IsNullOrEmpty(node.Id) && kv.Value.Node.Id == node.Id) return kv.Value;
            }
            return null;
        }
    }

    public class SchematicNodeView
    {
        public SchematicNode Node { get; set; } = null!;
        public double X { get; set; }
        public double Y { get; set; }
        public Brush NodeBrush { get; set; } = Brushes.SteelBlue;
        public bool IsSelected { get; set; }
    }

    public class SchematicEdgeView
    {
        public SchematicEdge Edge { get; set; } = null!;
        public SchematicNodeView Start { get; set; } = null!;
        public SchematicNodeView End { get; set; } = null!;
        public Brush EdgeBrush { get; set; } = Brushes.DarkSlateGray;
        public bool IsSelected { get; set; }
    }

    // 輕量 ICommand 實作
    public class SchematicCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        public SchematicCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute; _canExecute = canExecute;
        }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
    }

    // ===== 內部輔助：分色與佈局 =====
    public partial class SchematicViewModel
    {
        private static readonly Brush[] Palette = new Brush[]
        {
            (Brush)new SolidColorBrush(Color.FromRgb(0x2E,0x86,0xC1)), // 藍
            (Brush)new SolidColorBrush(Color.FromRgb(0x28,0xA7,0x45)), // 綠
            (Brush)new SolidColorBrush(Color.FromRgb(0xE6,0x7E,0x22)), // 橘
            (Brush)new SolidColorBrush(Color.FromRgb(0x8E,0x44,0xAD)), // 紫
            (Brush)new SolidColorBrush(Color.FromRgb(0xC0,0x39,0x2B)), // 紅
            (Brush)new SolidColorBrush(Color.FromRgb(0x16,0xA0,0x85))  // 青
        };

        private static Brush GetBrushByIfcType(string ifcType)
        {
            if (string.IsNullOrWhiteSpace(ifcType)) return Palette[0];
            var t = ifcType.ToLowerInvariant();
            if (t.Contains("pipesegment")) return Palette[0];
            if (t.Contains("flowfitting") || t.Contains("fitting")) return Palette[1];
            if (t.Contains("valve")) return Palette[2];
            if (t.Contains("pump") || t.Contains("compressor")) return Palette[3];
            if (t.Contains("fan") || t.Contains("duct")) return Palette[4];
            // fallback: hash to palette
            int h = Math.Abs(t.GetHashCode());
            return Palette[h % Palette.Length];
        }

        private static Brush GetDarkerBrush(Brush brush)
        {
            if (brush is SolidColorBrush scb)
            {
                byte d(byte v) => (byte)(v * 0.7);
                var c = scb.Color;
                return new SolidColorBrush(Color.FromRgb(d(c.R), d(c.G), d(c.B)));
            }
            return Brushes.DarkSlateGray;
        }

        private static void ApplyForceDirectedLayout(IList<SchematicNodeView> nodes, IList<SchematicEdgeView> edges, int iterations = 200)
        {
            if (nodes.Count == 0) return;
            // 參考 Fruchterman-Reingold：k = sqrt(area / n)
            // 先估一個初始區域大小
            double minX = nodes.Min(n => n.X), maxX = nodes.Max(n => n.X);
            double minY = nodes.Min(n => n.Y), maxY = nodes.Max(n => n.Y);
            double width = Math.Max(1, maxX - minX);
            double height = Math.Max(1, maxY - minY);
            double area = width * height;
            if (double.IsInfinity(area) || area <= 0) { area = Math.Max(1, nodes.Count) * 1000.0; width = height = Math.Sqrt(area); }
            double k = Math.Sqrt(area / Math.Max(1, nodes.Count));
            double temperature = Math.Max(width, height) / 10.0;

            var disp = new (double dx, double dy)[nodes.Count];
            var index = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);

            double Repulsive(double dist) => (k * k) / Math.Max(0.001, dist);
            double Attractive(double dist) => (dist * dist) / k;

            for (int it = 0; it < iterations; it++)
            {
                // reset
                for (int i = 0; i < nodes.Count; i++) disp[i] = (0, 0);

                // repulsive forces
                for (int i = 0; i < nodes.Count; i++)
                {
                    for (int j = i + 1; j < nodes.Count; j++)
                    {
                        var v = nodes[i]; var u = nodes[j];
                        double dx = v.X - u.X; double dy = v.Y - u.Y;
                        double dist = Math.Sqrt(dx * dx + dy * dy) + 0.001;
                        double f = Repulsive(dist);
                        double rx = (dx / dist) * f;
                        double ry = (dy / dist) * f;
                        disp[i] = (disp[i].dx + rx, disp[i].dy + ry);
                        disp[j] = (disp[j].dx - rx, disp[j].dy - ry);
                    }
                }

                // attractive forces (edges)
                foreach (var e in edges)
                {
                    var v = e.Start; var u = e.End;
                    if (v == null || u == null) continue;
                    int iv = index[v], iu = index[u];
                    double dx = v.X - u.X; double dy = v.Y - u.Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy) + 0.001;
                    double f = Attractive(dist);
                    double ax = (dx / dist) * f;
                    double ay = (dy / dist) * f;
                    disp[iv] = (disp[iv].dx - ax, disp[iv].dy - ay);
                    disp[iu] = (disp[iu].dx + ax, disp[iu].dy + ay);
                }

                // limit by temperature and update positions
                for (int i = 0; i < nodes.Count; i++)
                {
                    var v = nodes[i];
                    double dx = disp[i].dx; double dy = disp[i].dy;
                    double dispLen = Math.Sqrt(dx * dx + dy * dy);
                    if (dispLen > 0)
                    {
                        double lim = Math.Min(dispLen, temperature);
                        v.X += dx / dispLen * lim;
                        v.Y += dy / dispLen * lim;
                    }
                }

                // cool
                temperature *= 0.95;
                if (temperature < 0.01) break;
            }

            // 位置正規化到正座標區域（左上為最小）
            minX = nodes.Min(n => n.X); minY = nodes.Min(n => n.Y);
            double offX = -minX + 40; // 邊距
            double offY = -minY + 40;
            foreach (var v in nodes)
            {
                v.X += offX; v.Y += offY;
            }
        }
    }
}
