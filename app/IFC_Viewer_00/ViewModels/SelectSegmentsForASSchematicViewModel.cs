using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using Xbim.Common;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc;
using IFC_Viewer_00.Models;
using IFC_Viewer_00.Services;

namespace IFC_Viewer_00.ViewModels
{
    public partial class SelectSegmentsForASSchematicViewModel : ObservableObject
    {
        private readonly IfcStore _model;
        private readonly ISelectionService _selection;

        [ObservableProperty]
        private string seg1Display = "(未選取)";
        [ObservableProperty]
        private string seg2Display = "(未選取)";

        private IIfcPipeSegment? _seg1;
        private IIfcPipeSegment? _seg2;

        public RelayCommand ResetCommand { get; }

        // 完成 Task：產生的點位原理圖（SchematicData，節點為 Ports，無邊或可略）
        public Task<SchematicData?> WhenDone => _tcs.Task;
        private readonly TaskCompletionSource<SchematicData?> _tcs = new();

        public SelectSegmentsForASSchematicViewModel(IfcStore model, ISelectionService selection)
        {
            _model = model;
            _selection = selection;
            _selection.SelectionChanged += OnSelectionChanged;
            ResetCommand = new RelayCommand(Reset);
        }

        private void Reset()
        {
            _seg1 = null; _seg2 = null;
            Seg1Display = "(未選取)"; Seg2Display = "(未選取)";
        }

        private void OnSelectionChanged(object? sender, SelectionSetChangedEventArgs e)
        {
            if (_tcs.Task.IsCompleted) return; // 已完成則不再處理
            if (e.Origin != SelectionOrigin.Viewer3D && e.Origin != SelectionOrigin.TreeView && e.Origin != SelectionOrigin.Programmatic)
                return; // 僅接受 3D/TreeView/程式來源的選擇

            if (_model == null || _model.Instances == null) return;
            var ids = _selection.Selected;
            if (ids.Count == 0) return;
            // 取最後一個選到的作為本次候選
            var last = ids.Last();
            var ent = _model.Instances[last] as IIfcObject;
            if (ent is not IIfcPipeSegment seg) return;

            if (_seg1 == null)
            {
                _seg1 = seg;
                Seg1Display = BuildSegmentLabel(seg);
                return;
            }
            if (_seg2 == null && !ReferenceEquals(seg, _seg1))
            {
                _seg2 = seg;
                Seg2Display = BuildSegmentLabel(seg);
                // 已有兩段 → 啟動計算
                _ = ComputeAndFinishAsync();
            }
        }

        private string BuildSegmentLabel(IIfcPipeSegment seg)
        {
            try
            {
                var name = IfcStringHelper.FromValue((seg as IIfcRoot)?.Name) ?? seg.GetType().Name;
                var gid = IfcStringHelper.FromValue((seg as IIfcRoot)?.GlobalId) ?? (seg as IPersistEntity)?.EntityLabel.ToString();
                return $"{name} ({gid})";
            }
            catch { return seg.GetType().Name; }
        }

        private async Task ComputeAndFinishAsync()
        {
            try
            {
                if (_seg1 == null || _seg2 == null)
                {
                    _tcs.TrySetResult(null);
                    return;
                }
                var service = new SchematicService();
                var data = await service.GeneratePortPointSchematicFromSegmentsAsync(_model, _seg1, _seg2);
                _tcs.TrySetResult(data);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"AS原理圖計算失敗：{ex.Message}");
                _tcs.TrySetResult(null);
            }
        }
    }
}
