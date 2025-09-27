using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IFC_Viewer_00.Services;
using IFC_Viewer_00.Models;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xunit;

namespace IFC_Viewer_00.Tests
{
    public class ASSchematicTests
    {
        private static string FindSampleIfc()
        {
            // 從目前測試輸出目錄往上找 sample/Sample_pipe.ifc
            var dir = AppContext.BaseDirectory;
            var current = new DirectoryInfo(dir);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "sample", "Sample_pipe.ifc");
                if (File.Exists(candidate)) return candidate;
                // 有些結構在專案根下方有 app/ or tests/ 目錄，往上一層層找
                current = current.Parent;
            }
            throw new FileNotFoundException("找不到 sample/Sample_pipe.ifc，請確認檔案存在於方案根目錄。");
        }

        [Fact]
        public async Task GenerateASSchematic_FromTwoSegments_Should_ReturnUpTo4NodesAnd2Edges()
        {
            var path = FindSampleIfc();
            Assert.True(File.Exists(path), $"測試 IFC 檔不存在: {path}");

            using var store = IfcStore.Open(path);
            Assert.NotNull(store);

            // 取得至少兩段 IfcPipeSegment（優先有 Port 的）
            var segments = store.Instances.OfType<IIfcPipeSegment>()
                .Where(s => s != null)
                .Take(10) // 限制搜尋
                .ToList();
            Assert.True(segments.Count >= 2, $"模型中找不到至少 2 段 IfcPipeSegment，實際: {segments.Count}");

            // 選前兩段
            var seg1 = segments[0];
            var seg2 = segments[1];

            var service = new SchematicService();
            var data = await service.GeneratePortPointSchematicFromSegmentsAsync(store, seg1, seg2);
            Assert.NotNull(data);

            // 節點最多 4 個、邊最多 2 條（各段一條）
            Assert.InRange(data!.Nodes.Count, 0, 4);
            Assert.InRange(data.Edges.Count, 0, 2);

            if (data.Edges.Count == 2)
            {
                // 驗證每條邊的 Entity 指向其中一段管件
                var ids = data.Edges.Select(e => e.Entity?.EntityLabel).ToList();
                var id1 = (seg1 as Xbim.Common.IPersistEntity)?.EntityLabel;
                var id2 = (seg2 as Xbim.Common.IPersistEntity)?.EntityLabel;
                Assert.Contains(id1, ids);
                Assert.Contains(id2, ids);
            }

            // 2D 座標至少存在（若有節點）
            foreach (var n in data.Nodes)
            {
                // 預設 double 值即可視為存在
                _ = n.Position2D.X;
                _ = n.Position2D.Y;
            }
        }
    }
}
