using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using Xbim.Common;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;
using Xbim.ModelGeometry.Scene;
using Xbim.Presentation;
using Xbim.Presentation.ModelGeomInfo;

namespace IFC_Viewer_00.Services
{
    /// <summary>
    /// 以強型別 Xbim.Presentation.DrawingControl3D 為核心的 3D 服務。
    /// 在相容狀況下，直接呼叫屬性/方法以降低反射依賴；
    /// 對於跨版本差異較大的操作（Highlight/Isolate/Hide/HitTest），仍保留反射容錯。
    /// </summary>
    public class StrongWindowsUiViewer3DService : IViewer3DService
    {
        private readonly DrawingControl3D _viewer;
        private IModel? _model;
        private bool _dumped;
        // Overlay visuals (pipe axes and endpoints)
        private readonly object _overlayLock = new();
        private ModelVisual3D? _overlayRoot; // parent container
        private LinesVisual3D? _overlayLines;
        private PointsVisual3D? _overlayPoints;
        // 快取以降低反射與視覺樹查詢成本
        private (MemberInfo mi, bool isProperty)? _selMemberCached;
        private (MemberInfo mi, bool isProperty)? _isoMemberCached;
        private (MemberInfo mi, bool isProperty)? _hidMemberCached;
        private HelixViewport3D? _viewportCached;
        private bool _triedViewportCache;
        private static readonly string[] SelNames = new[] { "SelectedEntities", "HighlightedEntities", "SelectedInstances", "HighlightedInstances", "SelectedEntityLabels", "HighlightedEntityLabels", "Selection", "Selections", "Selected", "SelectedItems" };
        private static readonly string[] IsoNames = new[] { "IsolateInstances", "IsolatedInstances" };
        private static readonly string[] HidNames = new[] { "HiddenInstances", "HiddenEntities" };

        public StrongWindowsUiViewer3DService(DrawingControl3D viewer)
        {
            _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
        }

        public void SetModel(IModel? model)
        {
            _model = model;

            // 先在背景/呼叫執行緒建立幾何 Context，避免 UI 卡頓
            Xbim3DModelContext? ctx = null;
            if (model != null)
            {
                try
                {
                    ctx = new Xbim3DModelContext(model);
                    ctx.CreateContext();
                    Trace.WriteLine("[StrongViewer] Xbim3DModelContext created and CreateContext() succeeded (pre-assignment).");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[StrongViewer] CreateContext error (pre-assignment): {ex}");
                }
            }

            // 以下所有控制項操作改在 UI 執行緒
            RunOnUi(() =>
            {
                // 優先嘗試強型別屬性/方法；型別不合時回退反射
                try
                {
                    Trace.WriteLine("[StrongViewer] SetModel(IModel) start.");
                    DumpControlSurfaceOnce();

                    var prop = _viewer.GetType().GetProperty("Model", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (prop != null && prop.CanWrite)
                    {
                        if (model is IfcStore store && prop.PropertyType.IsAssignableFrom(typeof(IfcStore)))
                        {
                            prop.SetValue(_viewer, store);
                            Trace.WriteLine("[StrongViewer] Assigned IfcStore into control.Model (post-CreateContext).");
                        }
                        else if (model != null && prop.PropertyType.IsInstanceOfType(model))
                        {
                            prop.SetValue(_viewer, model);
                            Trace.WriteLine("[StrongViewer] Assigned IModel into control.Model (post-CreateContext).");
                        }
                        else
                        {
                            // 嘗試 SetModel(model)
                            var mi = _viewer.GetType().GetMethod("SetModel", BindingFlags.Public | BindingFlags.Instance);
                            if (mi != null)
                            {
                                var ps = mi.GetParameters();
                                if (ps.Length == 1 && model != null && ps[0].ParameterType.IsInstanceOfType(model))
                                {
                                    mi.Invoke(_viewer, new object?[] { model });
                                    Trace.WriteLine("[StrongViewer] Invoked control.SetModel(IModel) (post-CreateContext).\n");
                                }
                                else if (ps.Length == 1 && model is IfcStore s && ps[0].ParameterType.IsAssignableFrom(typeof(IfcStore)))
                                {
                                    mi.Invoke(_viewer, new object?[] { s });
                                    Trace.WriteLine("[StrongViewer] Invoked control.SetModel(IfcStore) (post-CreateContext).\n");
                                }
                                else if (ps.Length == 1 && model is IfcStore s2)
                                {
                                    // 嘗試以 IfcStore 內部可相容的基礎模型指派
                                    var cand = FindAssignableFromModel(s2, ps[0].ParameterType);
                                    if (cand != null)
                                    {
                                        mi.Invoke(_viewer, new object?[] { cand });
                                        Trace.WriteLine($"[StrongViewer] Invoked control.SetModel({cand.GetType().FullName}) (post-CreateContext).\n");
                                    }
                                }
                            }
                            else if (model is IfcStore s3)
                            {
                                // 無 SetModel 時，嘗試把底層可相容模型設給 Model 屬性
                                var cand = FindAssignableFromModel(s3, prop.PropertyType);
                                if (cand != null)
                                {
                                    prop.SetValue(_viewer, cand);
                                    Trace.WriteLine($"[StrongViewer] Assigned underlying model {cand.GetType().FullName} into control.Model (post-CreateContext).);");
                                }
                            }
                        }
                    }

                    // 指派完成後，確保 ReloadModel 被觸發一次（有些版本 OnModelChanged 已觸發，這裡再保險呼叫）
                    if (!TryInvokeReloadModelWithOptionsOnControl(_viewer, new[] { "ViewPreserveCameraPosition", "View", "None" }))
                    {
                        InvokeIfExists(_viewer, "ReloadModel");
                    }
                    // 幾何診斷：檢查控制項 Scenes 計數與 GeometryStore 狀態
                    try
                    {
                        var scenesField = _viewer.GetType().GetField("Scenes", BindingFlags.Public | BindingFlags.Instance);
                        var scenesPropInfo = scenesField == null ? _viewer.GetType().GetProperty("Scenes", BindingFlags.Public | BindingFlags.Instance) : null;
                        int? scenesCount = null;
                        if (scenesField is FieldInfo fi)
                        {
                            var val = fi.GetValue(_viewer) as System.Collections.IEnumerable;
                            if (val is System.Collections.ICollection coll) scenesCount = coll.Count;
                        }
                        else if (scenesPropInfo is PropertyInfo pi)
                        {
                            var val = pi.GetValue(_viewer) as System.Collections.IEnumerable;
                            if (val is System.Collections.ICollection coll) scenesCount = coll.Count;
                        }
                        var geoEmpty = (model as IfcStore)?.GeometryStore?.IsEmpty;
                        var mf = (model as IfcStore)?.ModelFactors;
                        Trace.WriteLine($"[StrongViewer] Diagnostics: Scenes.Count={(scenesCount?.ToString() ?? "?")}, GeometryStore.IsEmpty={geoEmpty}, OneMetre={mf?.OneMetre}, Precision={mf?.Precision}, DeflectionAngle={mf?.DeflectionAngle}");
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[StrongViewer] Error assigning model to control: {ex.Message}");
                }

                //（可選）嘗試把 context 指回控制項（若該版本提供），對某些舊版有用
                if (model != null)
                {
                    try
                    {
                        var controlType = _viewer.GetType();
                        var ctxProp = controlType.GetProperty("Context", BindingFlags.Public | BindingFlags.Instance)
                                     ?? controlType.GetProperty("ModelContext", BindingFlags.Public | BindingFlags.Instance);
                        if (ctxProp != null && ctxProp.CanWrite && ctxProp.PropertyType.IsAssignableFrom(typeof(Xbim3DModelContext)))
                        {
                            // 無法復用先前的 ctx 變數（可能為 null），保險重建一次
                            var ctx2 = new Xbim3DModelContext(model);
                            ctx2.CreateContext();
                            ctxProp.SetValue(_viewer, ctx2);
                            Trace.WriteLine("[StrongViewer] Assigned context into control.Context (optional). ");
                        }
                    }
                    catch { }
                }

                // 視角與刷新（以反射避免跨版本編譯期缺少方法）
                var camOk = false;
                // 先嘗試 Xbim.WindowsUI 官方流程的 ViewHome()
                if (InvokeIfExists(_viewer, "ViewHome"))
                {
                    camOk = true;
                    Trace.WriteLine("[StrongViewer] Invoked ViewHome().");
                }
                else
                {
                    string[] camNames = new[] { "ResetCamera", "ZoomExtents", "FitToView", "ZoomToFit", "FitAll", "BestFit" };
                    foreach (var n in camNames)
                    {
                        if (InvokeIfExists(_viewer, n)) { camOk = true; break; }
                    }
                }
                if (!camOk)
                {
                    camOk = TryHelixViewportZoomExtents(_viewer);
                    if (camOk) Trace.WriteLine("[StrongViewer] Camera fitted via HelixViewport3D fallback.");
                }

                // 嘗試設定顯示/幾何相關屬性（若存在）
                TrySetEnumPropertyByName(_viewer, "DisplayMode", new[] { "Shaded", "Solid", "Default" });
                TrySetEnumPropertyByName(_viewer, "GeometryType", new[] { "Triangulated", "Mesh", "Default" });
                TrySetBoolPropertyIfExists(_viewer, "Freeze", false);
                TrySetBoolPropertyIfExists(_viewer, "IsVisible", true);

                // 再次保險：優先使用 ReloadModel(ModelRefreshOptions.View*) 以觸發 LoadGeometry 與 RecalculateView（內含 ViewHome）
                if (!TryInvokeReloadModelWithOptionsOnControl(_viewer, new[] { "ViewPreserveCameraPosition", "View", "None" }))
                {
                    if (!InvokeIfExists(_viewer, "Refresh"))
                    {
                        // 嘗試其他更新/重建方法
                        InvokeIfExists(_viewer, "RebuildModel");
                        InvokeIfExists(_viewer, "BuildScene");
                        InvokeIfExists(_viewer, "RefreshScene");
                        InvokeIfExists(_viewer, "RefreshView");
                        InvokeIfExists(_viewer, "RefreshViewport");
                        InvokeIfExists(_viewer, "Redraw");
                        try { _viewer.InvalidateVisual(); } catch { }
                    }
                }
                var showOk = InvokeIfExists(_viewer, "ShowAll") || InvokeIfExists(_viewer, "Show") || InvokeIfExists(_viewer, "ShowModel");
                // 再次更新版面配置
                Trace.WriteLine($"[StrongViewer] Camera={camOk}, ShowAll={showOk}.");
                try { _viewer.UpdateLayout(); } catch { }
                try { _viewer.Focusable = true; } catch { }
                try { _viewer.Focus(); } catch { }
                try { _viewer.InvalidateVisual(); } catch { }
                try { (_viewer as FrameworkElement)?.InvalidateMeasure(); } catch { }
                try { (_viewer as FrameworkElement)?.InvalidateArrange(); } catch { }
                try { (_viewer as FrameworkElement)?.UpdateLayout(); } catch { }
                // 清除既有 overlay 以避免殘留
                try { ClearOverlay(); } catch { }
            });
        }

        public void SetModel(IfcStore? model)
        {
            SetModel((IModel?)model);
        }

    public void ResetCamera() => RunOnUi(() => InvokeIfExists(_viewer, "ResetCamera"));

        public void HighlightEntity(IIfcObject? entity, bool clearPrevious = true)
        {
            if (entity == null) return;
            RunOnUi(() =>
            {
                // 在 Xbim.Presentation.DrawingControl3D 裡沒有 HighlightEntity API；需改以 SelectedEntity 觸發高亮
                try
                {
                    var pi = _viewer.GetType().GetProperty("SelectedEntity", BindingFlags.Public | BindingFlags.Instance);
                    if (pi != null && pi.CanWrite)
                    {
                        // IIfcObject 實作 IPersistEntity，可直接指派
                        pi.SetValue(_viewer, entity);
                        return;
                    }
                }
                catch { }
                // 後援：若某些版本提供 HighlightEntity，仍嘗試
                var label = TryGetEntityLabel(entity);
                if (label.HasValue)
                {
                    if (InvokeIfExists(_viewer, "HighlightEntity", label.Value, clearPrevious)) return;
                    if (InvokeIfExists(_viewer, "HighlightEntity", label.Value)) return;
                    InvokeIfExists(_viewer, "HighlightEntity", new[] { label.Value }, clearPrevious);
                    return;
                }
                InvokeIfExists(_viewer, "HighlightEntity", entity, clearPrevious);
            });
        }

        public void HighlightEntities(IEnumerable<int> entityLabels, bool clearPrevious = true)
        {
            if (entityLabels == null) return;
            try
            {
                try { System.Diagnostics.Trace.WriteLine($"[Service] HighlightEntities called with {entityLabels.Count()} labels. clearPrevious={clearPrevious}"); } catch { }
                try { System.Diagnostics.Trace.WriteLine($"[Service] HighlightEntities(labels) received {entityLabels.Count()} ids. clearPrevious={clearPrevious}"); } catch { }
                RunOnUi(() =>
                {
                    // 去重並轉陣列；避免重複計算
                    var ids = entityLabels as int[] ?? entityLabels?.Distinct().ToArray() ?? Array.Empty<int>();
                    // 清除選取（ids 為空）
                    if (ids.Length == 0)
                    {
                        // 優先嘗試清空 Selection 或 SelectedEntity
                        try
                        {
                            if (TryGetSelectionMember(out var member))
                            {
                                var current = GetMemberValue(_viewer, member);
                                if (current != null)
                                {
                                    var mClear = current.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                                    if (mClear != null)
                                    {
                                        mClear.Invoke(current, null);
                                    }
                                    else
                                    {
                                        // 若沒有 Clear，嘗試以新實例或 null 置換
                                        var mt = GetMemberType(member);
                                        if (mt != null && !mt.IsAbstract && mt.GetConstructor(Type.EmptyTypes) != null)
                                        {
                                            SetMemberValue(_viewer, member, Activator.CreateInstance(mt));
                                        }
                                        else
                                        {
                                            SetMemberValue(_viewer, member, null);
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        // 後援：SelectedEntity = null
                        try { _viewer.GetType().GetProperty("SelectedEntity")?.SetValue(_viewer, null); } catch { }
                        // 單一 Invalidate，避免同步布局成本
                        TryInvalidateOnly();
                        return;
                    }

                    // 嘗試以 SelectedEntities/HighlightedEntities 或 Selection(EntitySelection) 設定多選
                    if (TryGetSelectionMember(out var selMember))
                    {
                        try { Trace.WriteLine($"[StrongViewer] Using member for selection: {selMember.mi.Name}"); } catch { }
                        var t = GetMemberType(selMember);
                        object? coll = GetMemberValue(_viewer, selMember);
                        // 情境 A：Selection 是 EntitySelection 之類（非集合）
                        if (coll != null && !IsCollectionLike(coll))
                        {
                            // 嘗試在 selection 物件上找內部集合/方法
                            if (TryPopulateSelectionObject(coll, ids))
                            {
                                // 將 selection 物件寫回（若可寫）
                                SetMemberValue(_viewer, selMember, coll);
                                TryInvalidateOnly();
                                return;
                            }
                            // 仍不支援，落到 SelectedEntity 後援
                        }
                        else
                        {
                            // 情境 B：selection member 是集合
                            if (coll == null && t != null && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                            {
                                coll = Activator.CreateInstance(t);
                            }
                            if (coll != null)
                            {
                                var mClear = coll.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                                mClear?.Invoke(coll, null);
                                var addInt = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                                if (addInt != null)
                                {
                                    foreach (var id in ids)
                                    {
                                        addInt.Invoke(coll, new object?[] { id });
                                    }
                                    SetMemberValue(_viewer, selMember, coll);
                                    TryInvalidateOnly();
                                    return;
                                }
                                // 若集合不支援 Add(int)，直接嘗試以 List<int> 替換（若相容）
                                try
                                {
                                    var list = ids.ToList();
                                    if (t != null && t.IsAssignableFrom(list.GetType()))
                                    {
                                        SetMemberValue(_viewer, selMember, list);
                                        TryInvalidateOnly();
                                        return;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        try { Trace.WriteLine("[StrongViewer] No multi-selection member found; falling back to single selection."); } catch { }
                    }
                    // 後援：SelectedEntity（至少讓單選可見）。若多筆，取第一筆。
                    try
                    {
                        var first = ids.FirstOrDefault();
                        if (first != 0 && _model != null)
                        {
                            var ent = _model.Instances[first] as IPersistEntity;
                            if (ent != null)
                            {
                                var piSel = _viewer.GetType().GetProperty("SelectedEntity", BindingFlags.Public | BindingFlags.Instance);
                                if (piSel != null && piSel.CanWrite)
                                {
                                    piSel.SetValue(_viewer, ent);
                                    try { Trace.WriteLine($"[StrongViewer] Fallback SelectedEntity assigned for label {first}."); } catch { }
                                    TryInvalidateOnly();
                                    return;
                                }
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        // 依據實體清單高亮（盡力對應控制項的 SelectedEntities/HighlightedEntities）
        public void HighlightEntities(IEnumerable<IPersistEntity> entitiesToHighlight)
        {
            if (entitiesToHighlight == null) return;
            try
            {
                try { System.Diagnostics.Trace.WriteLine($"[Service] HighlightEntities(entities) received {entitiesToHighlight.Count()} entities."); } catch { }
                RunOnUi(() =>
                {
                    var items = entitiesToHighlight.Distinct().ToList();
                    if (items.Count == 0)
                    {
                        // 清空
                        try
                        {
                            if (TryGetSelectionMember(out var member))
                            {
                                var current = GetMemberValue(_viewer, member);
                                if (current != null)
                                {
                                    var mClear = current.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                                    if (mClear != null) mClear.Invoke(current, null); else SetMemberValue(_viewer, member, null);
                                }
                            }
                            _viewer.GetType().GetProperty("SelectedEntity")?.SetValue(_viewer, null);
                        }
                        catch { }
                        TryInvalidateOnly();
                        return;
                    }

                    if (TryGetSelectionMember(out var selMember))
                    {
                        try { Trace.WriteLine($"[StrongViewer] Using member for selection: {selMember.mi.Name}"); } catch { }
                        object? coll = GetMemberValue(_viewer, selMember);
                        var t = GetMemberType(selMember);
                        // 情境 A：Selection 物件（非集合）
                        if (coll != null && !IsCollectionLike(coll))
                        {
                            var lbls = items.Select(pe => pe.EntityLabel).Where(i => i != 0).ToArray();
                            if (TryPopulateSelectionObject(coll, lbls))
                            {
                                SetMemberValue(_viewer, selMember, coll);
                                TryInvalidateOnly();
                                return;
                            }
                        }
                        else
                        {
                            // 情境 B：集合
                            if (coll == null && t != null && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                            {
                                coll = Activator.CreateInstance(t);
                            }
                            if (coll != null)
                            {
                                var mClear = coll.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                                mClear?.Invoke(coll, null);
                                var addPe = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IPersistEntity) }, null);
                                if (addPe != null)
                                {
                                    foreach (var pe in items) addPe.Invoke(coll, new object?[] { pe });
                                    SetMemberValue(_viewer, selMember, coll);
                                    TryInvalidateOnly();
                                    return;
                                }
                                // 若集合無 Add(IPersistEntity) 則嘗試 Add(int)
                                var addInt = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                                if (addInt != null)
                                {
                                    foreach (var pe in items)
                                    {
                                        var id = (pe as IPersistEntity)?.EntityLabel ?? 0;
                                        if (id != 0) addInt.Invoke(coll, new object?[] { id });
                                    }
                                    SetMemberValue(_viewer, selMember, coll);
                                    TryInvalidateOnly();
                                    return;
                                }
                                // 直接以 List<int> 取代（若型別相容）
                                try
                                {
                                    var list = items.Select(pe => pe.EntityLabel).Where(i => i != 0).ToList();
                                    if (t != null && t.IsAssignableFrom(list.GetType()))
                                    {
                                        SetMemberValue(_viewer, selMember, list);
                                        TryInvalidateOnly();
                                        return;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    // 後援：SelectedEntity（至少單選可見）
                    var firstEnt = items.FirstOrDefault();
                    if (firstEnt is IIfcObject obj)
                    {
                        TrySetSelectedEntity(obj);
                        TryInvalidateOnly();
                    }
                });
            }
            catch { }
        }

        public void Isolate(IIfcObject? entity)
        {
            if (entity == null) return;
            var lbl0 = TryGetEntityLabel(entity);
            Trace.WriteLine($"[StrongViewer] Isolate() called for entity label: { (lbl0.HasValue ? lbl0.Value.ToString() : "null") }.");
            try
            {
                RunOnUi(() =>
                {
                    // 設定選取，讓 ZoomSelected 與 Highlight 生效
                    TrySetSelectedEntity(entity);

                    // 設定隔離集合為單一目標
                    if (TryGetMember(_viewer, IsoNames, out var isoMember))
                    {
                        var isoBefore = GetCollectionCount(isoMember);
                        Trace.WriteLine($"[StrongViewer] Isolate: {isoMember.mi.Name} collection count before: { (isoBefore.HasValue ? isoBefore.Value.ToString() : "null") }.");
                        ReplaceCollectionWithSingle(isoMember, entity);
                        var isoAfter = GetCollectionCount(isoMember);
                        Trace.WriteLine($"[StrongViewer] Isolate: {isoMember.mi.Name} collection count after: { (isoAfter.HasValue ? isoAfter.Value.ToString() : "null") }.");
                        Trace.WriteLine("[StrongViewer] Isolate: set isolated collection to single target.");
                    }
                    else
                    {
                        Trace.WriteLine("[StrongViewer] Isolate: isolation member not found.");
                    }

                    // 清空隱藏集合（用 Clear 而非設 null）
                    if (TryGetMember(_viewer, HidNames, out var hidMember))
                    {
                        var hidBefore = GetCollectionCount(hidMember);
                        Trace.WriteLine($"[StrongViewer] Isolate: {hidMember.mi.Name} collection count before: { (hidBefore.HasValue ? hidBefore.Value.ToString() : "null") }.");
                        ClearCollection(hidMember);
                        var hidAfter = GetCollectionCount(hidMember);
                        Trace.WriteLine($"[StrongViewer] Isolate: {hidMember.mi.Name} collection count after: { (hidAfter.HasValue ? hidAfter.Value.ToString() : "null") }.");
                        Trace.WriteLine("[StrongViewer] Isolate: cleared hidden collection.");
                    }

                    // 刷新幾何
                    Trace.WriteLine("[StrongViewer] Invoking ReloadModel(ViewPreserveCameraPosition)...");
                    RefreshAfterFilterChange(preserveCamera: true);

                    // 聚焦選取
                    Trace.WriteLine("[StrongViewer] Invoking ZoomSelected()...");
                    InvokeIfExists(_viewer, "ZoomSelected");
                });
                return;
            }
            catch { }

            // 後援：若控制項提供 Isolate(...) API
            var label = TryGetEntityLabel(entity);
            if (label.HasValue)
            {
                if (InvokeIfExists(_viewer, "Isolate", label.Value)) return;
                InvokeIfExists(_viewer, "Isolate", new[] { label.Value });
                return;
            }
            InvokeIfExists(_viewer, "Isolate", entity);
        }

        public void Hide(IIfcObject? entity, bool recursive = true)
        {
            if (entity == null) return;
            var lbl0 = TryGetEntityLabel(entity);
            Trace.WriteLine($"[StrongViewer] Hide() called for entity label: { (lbl0.HasValue ? lbl0.Value.ToString() : "null") }.");
            try
            {
                RunOnUi(() =>
                {
                    // 設定選取，方便後續操作
                    TrySetSelectedEntity(entity);

                    // 累加到 Hidden 集合
                    if (TryGetMember(_viewer, HidNames, out var hidMember))
                    {
                        var hidBefore = GetCollectionCount(hidMember);
                        Trace.WriteLine($"[StrongViewer] Hide: {hidMember.mi.Name} collection count before: { (hidBefore.HasValue ? hidBefore.Value.ToString() : "null") }.");
                        AddToCollection(hidMember, entity);
                        var hidAfter = GetCollectionCount(hidMember);
                        Trace.WriteLine($"[StrongViewer] Hide: {hidMember.mi.Name} collection count after: { (hidAfter.HasValue ? hidAfter.Value.ToString() : "null") }.");
                        Trace.WriteLine("[StrongViewer] Hide: appended to hidden collection.");
                    }
                    else
                    {
                        Trace.WriteLine("[StrongViewer] Hide: hidden member not found.");
                    }

                    // 重新整理幾何，保留相機
                    Trace.WriteLine("[StrongViewer] Invoking ReloadModel(ViewPreserveCameraPosition)...");
                    RefreshAfterFilterChange(preserveCamera: true);
                });
                return;
            }
            catch { }

            // 後援：若控制項提供 Hide(...) API（舊版可能存在）
            var label = TryGetEntityLabel(entity);
            if (label.HasValue)
            {
                if (InvokeIfExists(_viewer, "Hide", label.Value, recursive)) return;
                if (InvokeIfExists(_viewer, "Hide", label.Value)) return;
                InvokeIfExists(_viewer, "Hide", new[] { label.Value }, recursive);
                return;
            }
            InvokeIfExists(_viewer, "Hide", entity, recursive);
        }

        public void Hide(IEnumerable<int> entityLabels, bool recursive = true)
        {
            if (entityLabels == null) return;
            try
            {
                RunOnUi(() =>
                {
                    if (TryGetMember(_viewer, HidNames, out var hidMember))
                    {
                        // 將多筆加入 Hidden 集合
                        var t = GetMemberType(hidMember);
                        object? coll = GetMemberValue(_viewer, hidMember);
                        if (coll == null && t != null && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                        {
                            coll = Activator.CreateInstance(t);
                        }
                        if (coll != null)
                        {
                            // 若需要清空前次，則 Clear
                            if (!recursive)
                            {
                                var mClear = coll.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                                mClear?.Invoke(coll, null);
                            }
                            var addInt = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                            if (addInt != null)
                            {
                                foreach (var id in entityLabels.Distinct())
                                {
                                    addInt.Invoke(coll, new object?[] { id });
                                }
                                SetMemberValue(_viewer, hidMember, coll);
                            }
                        }
                        RefreshAfterFilterChange(preserveCamera: true);
                    }
                    else
                    {
                        // 後援：逐一呼叫 Hide(int)
                        foreach (var id in entityLabels.Distinct())
                        {
                            InvokeIfExists(_viewer, "Hide", id, recursive);
                        }
                    }
                });
            }
            catch { }
        }

        public void ShowAll()
        {
            Trace.WriteLine("[StrongViewer] ShowAll() called.");
            // 優先以控制項欄位清空 Isolate/Hidden 並重建
            try
            {
                RunOnUi(() =>
                {
                    if (TryGetMember(_viewer, IsoNames, out var isoMember))
                    {
                        var isoBefore = GetCollectionCount(isoMember);
                        Trace.WriteLine($"[StrongViewer] ShowAll: {isoMember.mi.Name} collection count before: { (isoBefore.HasValue ? isoBefore.Value.ToString() : "null") }.");
                        ClearCollection(isoMember);
                        var isoAfter = GetCollectionCount(isoMember);
                        Trace.WriteLine($"[StrongViewer] ShowAll: {isoMember.mi.Name} collection count after: { (isoAfter.HasValue ? isoAfter.Value.ToString() : "null") }.");
                    }
                    else
                    {
                        Trace.WriteLine("[StrongViewer] ShowAll: isolation member not found.");
                    }
                    if (TryGetMember(_viewer, HidNames, out var hidMember))
                    {
                        var hidBefore = GetCollectionCount(hidMember);
                        Trace.WriteLine($"[StrongViewer] ShowAll: {hidMember.mi.Name} collection count before: { (hidBefore.HasValue ? hidBefore.Value.ToString() : "null") }.");
                        ClearCollection(hidMember);
                        var hidAfter = GetCollectionCount(hidMember);
                        Trace.WriteLine($"[StrongViewer] ShowAll: {hidMember.mi.Name} collection count after: { (hidAfter.HasValue ? hidAfter.Value.ToString() : "null") }.");
                    }
                    else
                    {
                        Trace.WriteLine("[StrongViewer] ShowAll: hidden member not found.");
                    }
                    Trace.WriteLine("[StrongViewer] ShowAll: cleared isolate & hidden collections.");

                    // 重新載入（依需求：呼叫無參數 ReloadModel）
                    Trace.WriteLine("[StrongViewer] Invoking ReloadModel()...");
                    if (!InvokeIfExists(_viewer, "ReloadModel"))
                    {
                        // 備援：若無參數版本不可用，再嘗試列舉版本（優先 None）或其他刷新
                        if (!TryInvokeReloadModelWithOptionsOnControl(_viewer, new[] { "None", "View" }))
                        {
                            InvokeIfExists(_viewer, "ApplyFilters");
                            InvokeIfExists(_viewer, "ApplyVisibility");
                            InvokeIfExists(_viewer, "UpdateVisibility");
                            InvokeIfExists(_viewer, "FilterScene");
                            InvokeIfExists(_viewer, "FilterScenes");
                            InvokeIfExists(_viewer, "RefreshScene");
                            InvokeIfExists(_viewer, "RebuildModel");
                            InvokeIfExists(_viewer, "Refresh");
                            InvokeIfExists(_viewer, "RefreshView");
                            InvokeIfExists(_viewer, "RefreshViewport");
                            InvokeIfExists(_viewer, "Redraw");
                        }
                    }

                    // 顯示全部類型（如有 ExcludedTypes 邏輯，XAML 的 ShowAll 按鈕也會作用）
                    Trace.WriteLine("[StrongViewer] Invoking ShowAll()...");
                    InvokeIfExists(_viewer, "ShowAll");
                    // 回家視角更直覺
                    Trace.WriteLine("[StrongViewer] Invoking ViewHome()...");
                    InvokeIfExists(_viewer, "ViewHome");
                });
                return;
            }
            catch { }
            // 後援
            InvokeIfExists(_viewer, "ShowAll");
        }

        // 任務 1：覆蓋 HiddenInstances 並刷新場景
        public void UpdateHiddenList(IEnumerable<IPersistEntity> hiddenEntities)
        {
            try
            {
                RunOnUi(() =>
                {
                    if (TryGetMember(_viewer, HidNames, out var hidMember))
                    {
                        object? coll = GetMemberValue(_viewer, hidMember);
                        var t = GetMemberType(hidMember);
                        if (coll == null && t != null && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                        {
                            coll = Activator.CreateInstance(t);
                        }
                        if (coll != null)
                        {
                            var mClear = coll.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                            mClear?.Invoke(coll, null);
                            var addPe = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IPersistEntity) }, null);
                            var addInt = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                            foreach (var pe in hiddenEntities.Distinct())
                            {
                                if (addPe != null)
                                {
                                    addPe.Invoke(coll, new object?[] { pe });
                                }
                                else if (addInt != null)
                                {
                                    var label = (pe as IPersistEntity)?.EntityLabel ?? 0;
                                    if (label != 0) addInt.Invoke(coll, new object?[] { label });
                                }
                            }
                            SetMemberValue(_viewer, hidMember, coll);
                        }
                    }
                    RefreshAfterFilterChange(preserveCamera: true);
                });
            }
            catch { }
        }

        public void Isolate(IEnumerable<int> entityLabels)
        {
            if (entityLabels == null) return;
            try
            {
                RunOnUi(() =>
                {
                    if (TryGetMember(_viewer, IsoNames, out var isoMember))
                    {
                        var t = GetMemberType(isoMember);
                        object? coll = GetMemberValue(_viewer, isoMember);
                        if (coll == null && t != null && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                        {
                            coll = Activator.CreateInstance(t);
                        }
                        if (coll != null)
                        {
                            var mClear = coll.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                            mClear?.Invoke(coll, null);
                            var addInt = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                            if (addInt != null)
                            {
                                foreach (var id in entityLabels.Distinct())
                                {
                                    addInt.Invoke(coll, new object?[] { id });
                                }
                                SetMemberValue(_viewer, isoMember, coll);
                            }
                        }
                    }
                    else
                    {
                        // 後援：逐一 Isolate；最後 ZoomSelected
                        foreach (var id in entityLabels.Distinct())
                        {
                            InvokeIfExists(_viewer, "Isolate", id);
                        }
                    }
                    RefreshAfterFilterChange(preserveCamera: true);
                    InvokeIfExists(_viewer, "ZoomSelected");
                });
            }
            catch { }
        }

        public IIfcObject? HitTest(double x, double y)
        {
            try
            {
                return RunOnUi(() =>
                {
                    // 使用快取的 Viewport 減少反射與屬性讀取成本
                    var viewport = _viewportCached;
                    if (!_triedViewportCache || viewport == null)
                    {
                        _triedViewportCache = true;
                        viewport = _viewer.GetType().GetProperty("Viewport")?.GetValue(_viewer) as HelixViewport3D;
                        _viewportCached = viewport;
                    }
                    if (viewport == null)
                    {
                        try { Trace.WriteLine("[StrongViewer] HitTest: Viewport is null."); } catch { }
                        return (IIfcObject?)null;
                    }
                    var pos = new Point(x, y);
                    var vpUi = viewport.Viewport as UIElement;
                    if (vpUi != null)
                    {
                        // 將 DrawingControl3D 座標轉換到 Helix Viewport3D
                        try
                        {
                            var owner = _viewer as UIElement;
                            if (owner != null) pos = owner.TranslatePoint(pos, vpUi);
                        }
                        catch { }
                    }
                    var hit = FindHit(viewport, pos);
                    var ent = GetClickedEntity(hit);
                    return ent as IIfcObject;
                });
            }
            catch { return null; }
        }

        // ========= 3D Overlay API =========
        public void ShowOverlayPipeAxes(
            IEnumerable<(Point3D Start, Point3D End)> axes,
            IEnumerable<Point3D>? endpoints = null,
            System.Windows.Media.Color? lineColor = null,
            double lineThickness = 2.0,
            System.Windows.Media.Color? pointColor = null,
            double pointSize = 3.0)
        {
            if (axes == null) return;
            RunOnUi(() =>
            {
                try
                {
                    var viewport = EnsureViewport();
                    if (viewport == null) return;
                    EnsureOverlayRoot(viewport);

                    // 計算視線方向微小偏移，避免 overlay 被實體遮擋
                    var cam = viewport.Camera as ProjectionCamera;
                    Vector3D viewDir = new Vector3D(0, 0, -1);
                    if (cam != null) viewDir = cam.LookDirection;
                    if (viewDir.LengthSquared < 1e-12) viewDir = new Vector3D(0, 0, -1);
                    viewDir.Normalize();
                    // 以輸入資料包圍盒對角線長度 * 0.5% 當偏移距離
                    var allPts = new List<Point3D>();
                    foreach (var (s, e) in axes) { allPts.Add(s); allPts.Add(e); }
                    if (endpoints != null) allPts.AddRange(endpoints);
                    double eps = 0.0;
                    if (allPts.Count >= 2)
                    {
                        double minX = allPts.Min(p => p.X), maxX = allPts.Max(p => p.X);
                        double minY = allPts.Min(p => p.Y), maxY = allPts.Max(p => p.Y);
                        double minZ = allPts.Min(p => p.Z), maxZ = allPts.Max(p => p.Z);
                        var dx = maxX - minX; var dy = maxY - minY; var dz = maxZ - minZ;
                        var diag = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        eps = Math.Max(1e-6, diag * 0.005);
                    }
                    var offset = -viewDir * eps; // 往相機方向挪動

                    // Build lines
                    _overlayLines ??= new LinesVisual3D();
                    _overlayLines.Thickness = Math.Max(0.5, lineThickness);
                    _overlayLines.Color = lineColor ?? System.Windows.Media.Colors.OrangeRed;
                    _overlayLines.Points = new Point3DCollection();
                    foreach (var (s, e) in axes)
                    {
                        _overlayLines.Points.Add(new Point3D(s.X + offset.X, s.Y + offset.Y, s.Z + offset.Z));
                        _overlayLines.Points.Add(new Point3D(e.X + offset.X, e.Y + offset.Y, e.Z + offset.Z));
                    }

                    // Build points
                    if (endpoints != null)
                    {
                        _overlayPoints ??= new PointsVisual3D();
                        _overlayPoints.Size = Math.Max(0.5, pointSize);
                        _overlayPoints.Color = pointColor ?? System.Windows.Media.Colors.Black;
                        var pts = new Point3DCollection();
                        foreach (var p in endpoints)
                        {
                            pts.Add(new Point3D(p.X + offset.X, p.Y + offset.Y, p.Z + offset.Z));
                        }
                        _overlayPoints.Points = pts;
                    }

                    // Attach to visual tree
                    AttachOverlayIfNeeded(viewport);
                }
                catch { }
            });
        }

        public void ClearOverlay()
        {
            RunOnUi(() =>
            {
                try
                {
                    var viewport = EnsureViewport();
                    if (viewport == null) return;
                    if (_overlayRoot != null)
                    {
                        try { viewport.Children.Remove(_overlayRoot); } catch { }
                        _overlayRoot = null;
                        _overlayLines = null;
                        _overlayPoints = null;
                    }
                }
                catch { }
            });
        }

        private HelixViewport3D? EnsureViewport()
        {
            if (_viewportCached != null) return _viewportCached;
            _triedViewportCache = true;
            try
            {
                _viewportCached = _viewer.GetType().GetProperty("Viewport")?.GetValue(_viewer) as HelixViewport3D;
            }
            catch { _viewportCached = null; }
            return _viewportCached;
        }

        private void EnsureOverlayRoot(HelixViewport3D viewport)
        {
            if (_overlayRoot != null) return;
            _overlayRoot = new ModelVisual3D();
            try { _overlayRoot.SetValue(FrameworkElement.TagProperty, "OverlayRoot"); } catch { }
        }

        private void AttachOverlayIfNeeded(HelixViewport3D viewport)
        {
            if (_overlayRoot == null) return;
            if (!viewport.Children.Contains(_overlayRoot))
            {
                viewport.Children.Add(_overlayRoot);
            }
            // Rebuild children
            try { _overlayRoot.Children.Clear(); } catch { }
            if (_overlayLines != null) _overlayRoot.Children.Add(_overlayLines);
            if (_overlayPoints != null) _overlayRoot.Children.Add(_overlayPoints);
        }

        private static RayMeshGeometry3DHitTestResult? FindHit(HelixViewport3D viewport, Point position)
        {
            RayMeshGeometry3DHitTestResult? result = null;
            HitTestFilterCallback hitFilterCallback = oFilter =>
            {
                if (oFilter.GetType() == typeof(MeshVisual3D))
                    return HitTestFilterBehavior.ContinueSkipSelfAndChildren;
                return HitTestFilterBehavior.Continue;
            };

            HitTestResultCallback hitTestCallback = hit =>
            {
                var rayHit = hit as RayMeshGeometry3DHitTestResult;
                if (rayHit?.MeshHit == null)
                    return HitTestResultBehavior.Continue;
                // 防禦：有些 Visual3D 不是 FrameworkElement，直接取 Tag 會例外
                try
                {
                    var tagObject = rayHit.ModelHit.GetValue(FrameworkElement.TagProperty);
                    if (tagObject == null)
                        return HitTestResultBehavior.Continue;
                }
                catch
                {
                    // 無法讀取 Tag，忽略此次命中
                    return HitTestResultBehavior.Continue;
                }
                result = rayHit;
                return HitTestResultBehavior.Stop;
            };

            var hitParams = new PointHitTestParameters(position);
            VisualTreeHelper.HitTest(viewport.Viewport, hitFilterCallback, hitTestCallback, hitParams);
            return result;
        }

        private IPersistEntity? GetClickedEntity(RayMeshGeometry3DHitTestResult? hit)
        {
            if (hit == null) return null;
            object? tag = null;
            try
            {
                tag = hit.ModelHit.GetValue(FrameworkElement.TagProperty);
            }
            catch
            {
                // 某些 3D 物件非 FrameworkElement，讀取 Tag 會失敗；直接返回 null 避免崩潰
                try { Trace.WriteLine("[StrongViewer] Hit without Tag; ModelHit has no TagProperty."); } catch { }
                return null;
            }

            // Case 1: Tag is layer
            if (tag is XbimMeshLayer<WpfMeshGeometry3D, WpfMaterial> layer)
            {
                var frag = layer.Visible.Meshes.Find(hit.VertexIndex1);
                var modelId = frag.ModelId;
                IModel? modelHit = null;
                if (modelId == 0) modelHit = _model ?? _viewer.Model;
                else if (_viewer.Model != null)
                {
                    foreach (var refModel in _viewer.Model.ReferencedModels)
                    {
                        if (refModel.Model.UserDefinedId != modelId) continue;
                        modelHit = refModel.Model;
                        break;
                    }
                }
                if (modelHit == null) return null;
                if (frag.IsEmpty) frag = layer.Visible.Meshes.Find(hit.VertexIndex2);
                if (frag.IsEmpty) frag = layer.Visible.Meshes.Find(hit.VertexIndex3);
                return frag.IsEmpty ? null : modelHit.Instances[frag.EntityLabel];
            }

            // Case 2: Tag is mesh
            if (tag is WpfMeshGeometry3D mesh)
            {
                var frag = mesh.Meshes.Find(hit.VertexIndex1);
                var modelId = frag.ModelId;
                IModel? modelHit = null;
                if (modelId == 0) modelHit = _model ?? _viewer.Model;
                else if (_viewer.Model != null)
                {
                    foreach (var refModel in _viewer.Model.ReferencedModels)
                    {
                        if (refModel.Model.UserDefinedId != modelId) continue;
                        modelHit = refModel.Model;
                        break;
                    }
                }
                if (modelHit == null) return null;
                if (frag.IsEmpty) frag = mesh.Meshes.Find(hit.VertexIndex2);
                if (frag.IsEmpty) frag = mesh.Meshes.Find(hit.VertexIndex3);
                return frag.IsEmpty ? null : modelHit.Instances[frag.EntityLabel];
            }

            try
            {
                if (tag is XbimInstanceHandle handle)
                    return handle.GetEntity();
            }
            catch { }

            return null;
        }

        private static int? TryGetEntityLabel(IIfcObject entity)
        {
            try
            {
                var pi = entity.GetType().GetProperty("EntityLabel", BindingFlags.Public | BindingFlags.Instance);
                if (pi != null)
                {
                    var v = pi.GetValue(entity);
                    if (v is int i) return i;
                }
            }
            catch { }
            return null;
        }

        // MapHitToIIfcObject not needed with Helix-based hit implementation

        private static bool InvokeIfExists(object target, string name, params object?[] args)
        {
            try
            {
                var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == name && m.GetParameters().Length == args.Length);
                foreach (var mi in methods)
                {
                    var ps = mi.GetParameters();
                    if (!AreArgsAssignable(ps, args)) continue;
                    mi.Invoke(target, args);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool AreArgsAssignable(ParameterInfo[] parameters, object?[] args)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i].ParameterType;
                var a = args[i];
                if (a == null)
                {
                    if (p.IsValueType && Nullable.GetUnderlyingType(p) == null) return false;
                    continue;
                }
                if (!p.IsInstanceOfType(a))
                {
                    if (p == typeof(int) && a is IIfcObject obj)
                    {
                        var lbl = TryGetEntityLabel(obj);
                        if (lbl.HasValue)
                        {
                            args[i] = lbl.Value;
                            continue;
                        }
                    }
                    if (p.IsArray && p.GetElementType() == typeof(int) && a is IIfcObject obj2)
                    {
                        var lbl = TryGetEntityLabel(obj2);
                        if (lbl.HasValue)
                        {
                            args[i] = new[] { lbl.Value };
                            continue;
                        }
                    }
                    return false;
                }
            }
            return true;
        }

        // 嘗試從控制項或其子成員找到 HelixViewport3D 或類似 Viewport，並呼叫 ZoomExtents/ZoomToFit
        private static bool TryHelixViewportZoomExtents(object control)
        {
            try
            {
                var t = control.GetType();
                // 1) 直接找名為 Viewport/Viewport3D 的屬性
                string[] propNames = new[] { "Viewport", "Viewport3D", "ViewPort", "HelixViewport", "HelixViewport3D" };
                foreach (var pn in propNames)
                {
                    var pi = t.GetProperty(pn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pi != null)
                    {
                        var vp = pi.GetValue(control);
                        if (vp != null && TryInvokeViewportFit(vp)) return true;
                    }
                }

                // 2) 掃描所有屬性/欄位，看型別名包含 HelixViewport3D / Viewport3D
                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var val = pi.GetValue(control);
                        if (val == null) continue;
                        var name = val.GetType().Name;
                        if (name.IndexOf("Viewport", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("HelixViewport", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (TryInvokeViewportFit(val)) return true;
                        }
                    }
                    catch { }
                }
                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var val = fi.GetValue(control);
                        if (val == null) continue;
                        var name = val.GetType().Name;
                        if (name.IndexOf("Viewport", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("HelixViewport", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (TryInvokeViewportFit(val)) return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static bool TryInvokeViewportFit(object viewport)
        {
            try
            {
                // 常見 Helix 方法名稱
                string[] names = new[] { "ZoomExtents", "ZoomToFit", "FitToView", "ZoomAll" };
                foreach (var n in names)
                {
                    var mi = viewport.GetType().GetMethod(n, BindingFlags.Public | BindingFlags.Instance, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (mi != null)
                    {
                        mi.Invoke(viewport, null);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 從 IfcStore 內部探查與期望型別相容的底層模型物件（例如 XbimModel 或 IModel 實作）。
        /// </summary>
        private static object? FindAssignableFromModel(IfcStore model, Type expectedType)
        {
            try
            {
                if (expectedType.IsInstanceOfType(model)) return model;
                string[] names = new[] { "Model", "UnderlyingModel", "InternalModel", "XbimModel" };
                var t = model.GetType();
                foreach (var n in names)
                {
                    var pi = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pi != null)
                    {
                        var val = pi.GetValue(model);
                        if (val != null && expectedType.IsInstanceOfType(val)) return val;
                    }
                    var fi = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fi != null)
                    {
                        var val = fi.GetValue(model);
                        if (val != null && expectedType.IsInstanceOfType(val)) return val;
                    }
                }
                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var val = pi.GetValue(model);
                        if (val != null && expectedType.IsInstanceOfType(val)) return val;
                    }
                    catch { }
                }
                foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var val = fi.GetValue(model);
                        if (val != null && expectedType.IsInstanceOfType(val)) return val;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private void DumpControlSurfaceOnce()
        {
            if (_dumped) return;
            _dumped = true;
            try
            {
                var t = _viewer.GetType();
                Trace.WriteLine($"[StrongViewer] Control type: {t.FullName}");
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                    .Select(p => $"P {p.Name}:{p.PropertyType.Name} (CanWrite={p.CanWrite})");
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Select(m => $"M {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                Trace.WriteLine("[StrongViewer] Control properties: " + string.Join(" | ", props));
                Trace.WriteLine("[StrongViewer] Control methods: " + string.Join(" | ", methods));
            }
            catch { }
        }

        private static void TrySetEnumPropertyByName(object target, string propName, string[] preferredNames)
        {
            try
            {
                var pi = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi == null || !pi.CanWrite) return;
                var t = pi.PropertyType;
                if (!t.IsEnum) return;
                var names = Enum.GetNames(t);
                var values = Enum.GetValues(t);
                object? chosen = null;
                foreach (var pref in preferredNames)
                {
                    var idx = Array.IndexOf(names, pref);
                    if (idx >= 0) { chosen = values.GetValue(idx); break; }
                }
                chosen ??= values.Length > 0 ? values.GetValue(0) : null;
                if (chosen != null) pi.SetValue(target, chosen);
                Trace.WriteLine($"[StrongViewer] Set {propName} = {chosen}.");
            }
            catch { }
        }

        private static void TrySetBoolPropertyIfExists(object target, string propName, bool value)
        {
            try
            {
                var pi = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null && pi.CanWrite && pi.PropertyType == typeof(bool))
                {
                    pi.SetValue(target, value);
                    Trace.WriteLine($"[StrongViewer] Set {propName} = {value}.");
                }
            }
            catch { }
        }

        // 嘗試以 enum 參數呼叫 ReloadModel(ModelRefreshOptions)
        private static bool TryInvokeReloadModelWithOptions(object target, string[] preferredOptionNames)
        {
            try
            {
                // 嘗試取得 Xbim.Presentation.ModelRefreshOptions enum
                var enumType = Type.GetType("Xbim.Presentation.ModelRefreshOptions, Xbim.Presentation", throwOnError: false)
                               ?? Type.GetType("Xbim.Presentation.ModelRefreshOptions", throwOnError: false);
                if (enumType == null || !enumType.IsEnum)
                {
                    return false;
                }

                object? chosen = null;
                var names = Enum.GetNames(enumType);
                var values = Enum.GetValues(enumType);
                foreach (var pref in preferredOptionNames)
                {
                    var idx = Array.IndexOf(names, pref);
                    if (idx >= 0) { chosen = values.GetValue(idx); break; }
                }
                chosen ??= values.Length > 0 ? values.GetValue(0) : null;
                if (chosen == null) return false;

                var mi = target.GetType().GetMethod("ReloadModel", BindingFlags.Public | BindingFlags.Instance, binder: null, types: new[] { enumType }, modifiers: null);
                if (mi == null) return false;
                mi.Invoke(target, new object?[] { chosen });
                Trace.WriteLine($"[StrongViewer] ReloadModel({chosen}) invoked.");
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[StrongViewer] TryInvokeReloadModelWithOptions failed: {ex.Message}");
                return false;
            }
        }

        // 某些組建將 enum 宣告為控制項內的巢狀型別（DrawingControl3D.ModelRefreshOptions），優先從目標控制項型別解析
        private static bool TryInvokeReloadModelWithOptionsOnControl(object control, string[] preferredOptionNames)
        {
            try
            {
                var t = control.GetType();
                var enumType = t.GetNestedType("ModelRefreshOptions", BindingFlags.Public | BindingFlags.NonPublic)
                                ?? Type.GetType("Xbim.Presentation.ModelRefreshOptions, Xbim.Presentation", throwOnError: false);
                if (enumType == null || !enumType.IsEnum)
                    return false;

                object? chosen = null;
                var names = Enum.GetNames(enumType);
                var values = Enum.GetValues(enumType);
                foreach (var pref in preferredOptionNames)
                {
                    var idx = Array.IndexOf(names, pref);
                    if (idx >= 0) { chosen = values.GetValue(idx); break; }
                }
                // 備援：若偏好名稱沒命中，優先挑選含 View 或 Filter 的項目，再退回第一個
                if (chosen == null)
                {
                    int pick = -1;
                    for (int i = 0; i < names.Length; i++)
                    {
                        var n = names[i];
                        if (n.IndexOf("View", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Filter", StringComparison.OrdinalIgnoreCase) >= 0)
                        { pick = i; break; }
                    }
                    if (pick >= 0) chosen = values.GetValue(pick);
                }
                chosen ??= values.Length > 0 ? values.GetValue(0) : null;
                if (chosen == null) return false;

                var mi = t.GetMethod("ReloadModel", BindingFlags.Public | BindingFlags.Instance, binder: null, types: new[] { enumType }, modifiers: null);
                if (mi == null) return false;
                mi.Invoke(control, new object?[] { chosen });
                Trace.WriteLine($"[StrongViewer] ReloadModel({chosen}) invoked (control-nested enum).");
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[StrongViewer] TryInvokeReloadModelWithOptionsOnControl failed: {ex.Message}");
                return false;
            }
        }

        // ==== helpers: UI/Reflection/Collections/Refresh ====
        private void RunOnUi(Action action)
        {
            try
            {
                var disp = System.Windows.Application.Current?.Dispatcher ?? _viewer.Dispatcher;
                if (disp.CheckAccess()) action();
                else disp.Invoke(action);
            }
            catch
            {
                // 退而求其次：直接執行，避免因 Dispatcher 不可用而完全失效
                try { action(); } catch { }
            }
        }

        private T RunOnUi<T>(Func<T> func)
        {
            try
            {
                var disp = System.Windows.Application.Current?.Dispatcher ?? _viewer.Dispatcher;
                if (disp.CheckAccess()) return func();
                else return disp.Invoke(func);
            }
            catch
            {
                try { return func(); } catch { return default!; }
            }
        }

        private void TrySetSelectedEntity(IIfcObject entity)
        {
            try
            {
                var selPi = _viewer.GetType().GetProperty("SelectedEntity", BindingFlags.Public | BindingFlags.Instance);
                selPi?.SetValue(_viewer, entity);
            }
            catch { }
        }

        private static bool TryGetMember(object target, string[] candidateNames, out (MemberInfo mi, bool isProperty) member)
        {
            var t = target.GetType();
            foreach (var n in candidateNames)
            {
                var pi = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi != null) { member = (pi, true); return true; }
                var fi = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) { member = (fi, false); return true; }
            }
            member = default;
            return false;
        }

        private static object? GetMemberValue(object target, (MemberInfo mi, bool isProperty) member)
        {
            try
            {
                if (member.isProperty) return ((PropertyInfo)member.mi).GetValue(target);
                return ((FieldInfo)member.mi).GetValue(target);
            }
            catch { return null; }
        }

        private static void SetMemberValue(object target, (MemberInfo mi, bool isProperty) member, object? value)
        {
            try
            {
                if (member.isProperty)
                {
                    var pi = (PropertyInfo)member.mi;
                    if (pi.CanWrite) pi.SetValue(target, value);
                }
                else
                {
                    ((FieldInfo)member.mi).SetValue(target, value);
                }
            }
            catch { }
        }

        private static Type? GetMemberType((MemberInfo mi, bool isProperty) member)
        {
            return member.isProperty ? ((PropertyInfo)member.mi).PropertyType : ((FieldInfo)member.mi).FieldType;
        }

        private int? GetCollectionCount((MemberInfo mi, bool isProperty) member)
        {
            try
            {
                var current = GetMemberValue(_viewer, member);
                return GetCollectionCount(current);
            }
            catch { return null; }
        }

        private static int? GetCollectionCount(object? coll)
        {
            try
            {
                if (coll == null) return null;
                if (coll is ICollection ic) return ic.Count;
                var t = coll.GetType();
                var piCount = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                if (piCount != null && piCount.PropertyType == typeof(int))
                {
                    var v = piCount.GetValue(coll);
                    if (v is int i) return i;
                }
                var piLen = t.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                if (piLen != null && piLen.PropertyType == typeof(int))
                {
                    var v = piLen.GetValue(coll);
                    if (v is int i) return i;
                }
                if (coll is IEnumerable en)
                {
                    int c = 0;
                    var e = en.GetEnumerator();
                    while (e.MoveNext()) c++;
                    return c;
                }
            }
            catch { }
            return null;
        }

        private void ClearCollection((MemberInfo mi, bool isProperty) member)
        {
            try
            {
                var current = GetMemberValue(_viewer, member);
                if (current == null)
                {
                    // 若為 null，就嘗試建立同型別空集合再設定，避免控制項內邏輯忽略 null
                    var t = GetMemberType(member);
                    if (t != null && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                    {
                        var inst = Activator.CreateInstance(t);
                        SetMemberValue(_viewer, member, inst);
                        current = inst;
                    }
                }
                if (current != null)
                {
                    // 優先使用 Clear()
                    var mClear = current.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (mClear != null) { mClear.Invoke(current, null); return; }
                    // 若無 Clear，嘗試以新空集合替換
                    var t = GetMemberType(member);
                    if (t != null && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                    {
                        var inst = Activator.CreateInstance(t);
                        SetMemberValue(_viewer, member, inst);
                    }
                    else
                    {
                        SetMemberValue(_viewer, member, null);
                    }
                }
            }
            catch { }
        }

        private void ReplaceCollectionWithSingle((MemberInfo mi, bool isProperty) member, IIfcObject entity)
        {
            try
            {
                var lbl = TryGetEntityLabel(entity);
                var t = GetMemberType(member);
                if (t == null) return;

                object? coll = GetMemberValue(_viewer, member);
                if (coll == null)
                {
                    if (!t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                    {
                        coll = Activator.CreateInstance(t);
                        SetMemberValue(_viewer, member, coll);
                    }
                }
                // 先清空
                if (coll != null)
                {
                    var mClear = coll.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                    mClear?.Invoke(coll, null);
                }

                // 嘗試 Add(IPersistEntity) 或 Add(int)
                if (coll != null)
                {
                    var addPe = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IPersistEntity) }, null);
                    var addInt = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                    if (addPe != null && entity is IPersistEntity pe)
                    {
                        addPe.Invoke(coll, new object?[] { pe });
                    }
                    else if (addInt != null && lbl.HasValue)
                    {
                        addInt.Invoke(coll, new object?[] { lbl.Value });
                    }
                    else
                    {
                        // 後援：若非標準泛型集合，試 IList
                        var list = coll as System.Collections.IList;
                        if (list != null)
                        {
                            if (entity is IPersistEntity pe2) list.Add(pe2);
                            else if (lbl.HasValue) list.Add(lbl.Value);
                        }
                    }
                    // 將集合放回（若屬性為 settable）
                    SetMemberValue(_viewer, member, coll);
                }
            }
            catch { }
        }

        private void AddToCollection((MemberInfo mi, bool isProperty) member, IIfcObject entity)
        {
            try
            {
                var lbl = TryGetEntityLabel(entity);
                var t = GetMemberType(member);
                if (t == null) return;
                object? coll = GetMemberValue(_viewer, member);
                if (coll == null)
                {
                    if (!t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                    {
                        coll = Activator.CreateInstance(t);
                        SetMemberValue(_viewer, member, coll);
                    }
                }
                if (coll != null)
                {
                    var addPe = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IPersistEntity) }, null);
                    var addInt = coll.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                    var containsPe = coll.GetType().GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IPersistEntity) }, null);
                    var containsInt = coll.GetType().GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                    bool added = false;
                    if (addPe != null && entity is IPersistEntity pe)
                    {
                        if (containsPe == null || !(bool)containsPe.Invoke(coll, new object?[] { pe })!)
                        {
                            addPe.Invoke(coll, new object?[] { pe });
                            added = true;
                        }
                    }
                    else if (addInt != null && lbl.HasValue)
                    {
                        if (containsInt == null || !(bool)containsInt.Invoke(coll, new object?[] { lbl.Value })!)
                        {
                            addInt.Invoke(coll, new object?[] { lbl.Value });
                            added = true;
                        }
                    }
                    else
                    {
                        var list = coll as System.Collections.IList;
                        if (list != null)
                        {
                            var val = (entity is IPersistEntity pe2) ? (object)pe2 : (lbl.HasValue ? (object)lbl.Value : null);
                            if (val != null && !list.Contains(val))
                            {
                                list.Add(val);
                                added = true;
                            }
                        }
                    }
                    if (added)
                    {
                        SetMemberValue(_viewer, member, coll);
                    }
                }
            }
            catch { }
        }

        private void RefreshAfterFilterChange(bool preserveCamera)
        {
            // 先嘗試 ReloadModel(option)
            var optionNames = preserveCamera ? new[] { "ViewPreserveCameraPosition", "View", "Filter", "None" } : new[] { "View", "Filter", "None" };
            if (!TryInvokeReloadModelWithOptionsOnControl(_viewer, optionNames))
            {
                if (!InvokeIfExists(_viewer, "ReloadModel"))
                {
                    // 可能的濾鏡/可視性套用方法（若存在）
                    InvokeIfExists(_viewer, "ApplyFilters");
                    InvokeIfExists(_viewer, "ApplyVisibility");
                    InvokeIfExists(_viewer, "UpdateVisibility");
                    InvokeIfExists(_viewer, "FilterScene");
                    InvokeIfExists(_viewer, "FilterScenes");
                    InvokeIfExists(_viewer, "RefreshScene");
                    InvokeIfExists(_viewer, "RebuildModel");
                    InvokeIfExists(_viewer, "Refresh");
                    InvokeIfExists(_viewer, "RefreshView");
                    InvokeIfExists(_viewer, "RefreshViewport");
                    InvokeIfExists(_viewer, "Redraw");
                }
            }
            // 僅標記需要重新繪製，避免同步 UpdateLayout 造成 UI 卡頓
            TryInvalidateOnly();
        }

        // ===== helpers for Selection object handling =====
        private static bool IsCollectionLike(object obj)
        {
            if (obj is IEnumerable) return true;
            var t = obj.GetType();
            // 具有 Add/Remove/Count 也視為集合樣式
            var hasAdd = t.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance) != null;
            var hasCount = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance) != null
                           || t.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance) != null;
            return hasAdd && hasCount;
        }

        // 嘗試在 selection 物件（例如 EntitySelection）上設定多選資料（labels）
        private bool TryPopulateSelectionObject(object selectionObj, int[] labels)
        {
            try
            {
                var t = selectionObj.GetType();
                // 1) 嘗試找屬性：SelectedEntities/HighlightedEntities/Entities/Items/SelectedItems/EntityLabels/SelectedEntityLabels
                string[] propNames = new[] { "SelectedEntities", "HighlightedEntities", "Entities", "Items", "SelectedItems", "EntityLabels", "SelectedEntityLabels" };
                foreach (var pn in propNames)
                {
                    var pi = t.GetProperty(pn, BindingFlags.Public | BindingFlags.Instance);
                    if (pi == null) continue;
                    var val = pi.GetValue(selectionObj);
                    if (val == null)
                    {
                        var pt = pi.PropertyType;
                        if (!pt.IsAbstract && pt.GetConstructor(Type.EmptyTypes) != null)
                        {
                            val = Activator.CreateInstance(pt);
                            if (pi.CanWrite) pi.SetValue(selectionObj, val);
                        }
                    }
                    if (val != null)
                    {
                        // 清空與加值（支援 Add(int)/Add(IPersistEntity) 或直接替換 List<int>）
                        var mClear = val.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                        mClear?.Invoke(val, null);
                        var addInt = val.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                        if (addInt != null)
                        {
                            foreach (var id in labels.Distinct()) addInt.Invoke(val, new object?[] { id });
                            return true;
                        }
                        var addPe = val.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(IPersistEntity) }, null);
                        if (addPe != null && _model != null)
                        {
                            foreach (var id in labels.Distinct())
                            {
                                var ent = _model.Instances[id] as IPersistEntity;
                                if (ent != null) addPe.Invoke(val, new object?[] { ent });
                            }
                            return true;
                        }
                        // 嘗試直接指派 List<int>
                        var list = labels.Distinct().ToList();
                        var pt2 = pi.PropertyType;
                        if (pt2.IsAssignableFrom(list.GetType()))
                        {
                            if (pi.CanWrite) pi.SetValue(selectionObj, list);
                            return true;
                        }
                    }
                }

                // 2) 嘗試找方法：Set/SetRange/Add/AddRange/SetLabels/SetEntities 等
                string[] methodNames = new[] { "Set", "SetRange", "Add", "AddRange", "SetLabels", "SetEntities" };
                foreach (var mn in methodNames)
                {
                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == mn).ToList();
                    foreach (var mi in methods)
                    {
                        var ps = mi.GetParameters();
                        if (ps.Length == 1)
                        {
                            var p0 = ps[0].ParameterType;
                            // 支援 int[] 或 IEnumerable<int>
                            if (p0.IsArray && p0.GetElementType() == typeof(int))
                            {
                                mi.Invoke(selectionObj, new object?[] { labels });
                                return true;
                            }
                            if (typeof(IEnumerable).IsAssignableFrom(p0))
                            {
                                // 最簡單：傳 List<int>
                                mi.Invoke(selectionObj, new object?[] { labels.ToList() });
                                return true;
                            }
                            if (p0 == typeof(int))
                            {
                                // 單值 Add：逐一加入
                                foreach (var id in labels) mi.Invoke(selectionObj, new object?[] { id });
                                return true;
                            }
                        }
                        else if (ps.Length == 2 && _model != null)
                        {
                            // 常見形式：Add(model, id)
                            if (typeof(IModel).IsAssignableFrom(ps[0].ParameterType) && ps[1].ParameterType == typeof(int))
                            {
                                foreach (var id in labels) mi.Invoke(selectionObj, new object?[] { _model, id });
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        // ==== 快取/Invalidation 輔助 ====
        private bool TryGetSelectionMember(out (MemberInfo mi, bool isProperty) member)
        {
            if (_selMemberCached.HasValue)
            {
                member = _selMemberCached.Value;
                return true;
            }
            if (TryGetMember(_viewer, SelNames, out member))
            {
                _selMemberCached = member;
                return true;
            }
            return false;
        }

        private bool TryGetHiddenMember(out (MemberInfo mi, bool isProperty) member)
        {
            if (_hidMemberCached.HasValue)
            {
                member = _hidMemberCached.Value;
                return true;
            }
            if (TryGetMember(_viewer, HidNames, out member))
            {
                _hidMemberCached = member;
                return true;
            }
            return false;
        }

        private bool TryGetIsolateMember(out (MemberInfo mi, bool isProperty) member)
        {
            if (_isoMemberCached.HasValue)
            {
                member = _isoMemberCached.Value;
                return true;
            }
            if (TryGetMember(_viewer, IsoNames, out member))
            {
                _isoMemberCached = member;
                return true;
            }
            return false;
        }

        private void TryInvalidateOnly()
        {
            try { RunOnUi(() => _viewer.InvalidateVisual()); } catch { }
        }
    }
}
