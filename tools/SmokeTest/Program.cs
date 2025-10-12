using System;
using System.IO;
using System.Linq;
using IFC_Viewer_00.Services;
using Xbim.Ifc;

class Program
{
    static int Main(string[] args)
    {
        try
        {
            // 向上尋找含 sample 目錄的工作區根
            string p = AppContext.BaseDirectory;
            string? foundRoot = null;
            for (int i = 0; i < 8; i++)
            {
                var cand = Path.Combine(p, "sample");
                if (Directory.Exists(cand)) { foundRoot = p; break; }
                var parent = Directory.GetParent(p);
                if (parent == null) break;
                p = parent.FullName;
            }
            var repoRoot = foundRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
            var ifcPath = Path.Combine(repoRoot, "sample", "SAMPLE_Drainage.ifc");
            if (!File.Exists(ifcPath))
            {
                Console.WriteLine($"IFC not found: {ifcPath}");
                return 2;
            }

            using var model = IfcStore.Open(ifcPath);
            var svc = new SchematicService();
            var opt = new SchematicService.PipeNetworkOptions
            {
                BuildMode = SchematicService.BuildModeKind.FittingNetwork,
                CanvasPlane = SchematicService.UserProjectionPlane.YZ,
                Use2DGeometryFallback = true,
                PlanarToleranceMm = 500
            };
            var (data, report) = svc.BuildPipeNetworkAsync(model, opt).GetAwaiter().GetResult();

            Console.WriteLine("=== Fitting-Centric Smoke Test ===");
            Console.WriteLine($"Nodes={report.TotalNodes} Edges={report.TotalEdges} Rewired={report.RewiredEdges}");
            var byKind = data.Nodes
                .Where(n => n.NodeKind.ToString().Contains("Fitting"))
                .GroupBy(n => n.FittingKind ?? n.IfcType ?? "?")
                .Select(g => ($"{g.Key}", g.Count()));
            Console.WriteLine("Fittings(by kind): " + string.Join(", ", byKind.Select(kv => $"{kv.Item1}={kv.Item2}")));
            var hubEdges = data.Edges.Where(e => e.Origin == IFC_Viewer_00.Models.SchematicEdge.EdgeOriginKind.RewiredViaFittingHub).Count();
            Console.WriteLine($"HubEdges={hubEdges}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }
}
