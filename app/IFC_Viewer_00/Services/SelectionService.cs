using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace IFC_Viewer_00.Services
{
    public enum SelectionOrigin
    {
        Viewer3D,
        TreeView,
        Schematic,
        Programmatic
    }

    public class SelectionSetChangedEventArgs : EventArgs
    {
        public IReadOnlyCollection<int> Added { get; }
        public IReadOnlyCollection<int> Removed { get; }
        public SelectionOrigin Origin { get; }
        public SelectionSetChangedEventArgs(IEnumerable<int> added, IEnumerable<int> removed, SelectionOrigin origin)
        {
            Added = added?.ToArray() ?? Array.Empty<int>();
            Removed = removed?.ToArray() ?? Array.Empty<int>();
            Origin = origin;
        }
    }

    public interface ISelectionService
    {
        IReadOnlyCollection<int> Selected { get; }
    event EventHandler<SelectionSetChangedEventArgs>? SelectionChanged;
        void SetSelection(IEnumerable<int> ids, SelectionOrigin origin);
        void Add(int id, SelectionOrigin origin);
        void AddRange(IEnumerable<int> ids, SelectionOrigin origin);
        void Remove(int id, SelectionOrigin origin);
        void Clear(SelectionOrigin origin);
        bool IsChanging { get; }
    }

    public class SelectionService : ISelectionService
    {
        private readonly ObservableCollection<int> _selected = new();
        private bool _suppress;

        public IReadOnlyCollection<int> Selected => _selected;
        public bool IsChanging => _suppress;
    public event EventHandler<SelectionSetChangedEventArgs>? SelectionChanged;

        public SelectionService()
        {
            _selected.CollectionChanged += OnCollectionChanged;
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_suppress) return;
            // CollectionChanged 不包含 origin；對外只在 API 呼叫時觸發 SelectionChanged
        }

        public void SetSelection(IEnumerable<int> ids, SelectionOrigin origin)
        {
            ids ??= Array.Empty<int>();
            var set = new HashSet<int>(ids);
            _suppress = true;
            try
            {
                var removed = _selected.Where(x => !set.Contains(x)).ToArray();
                var added = set.Where(x => !_selected.Contains(x)).ToArray();
                foreach (var r in removed) _selected.Remove(r);
                foreach (var a in added) _selected.Add(a);
                SelectionChanged?.Invoke(this, new SelectionSetChangedEventArgs(added, removed, origin));
            }
            finally { _suppress = false; }
        }

        public void Add(int id, SelectionOrigin origin)
        {
            AddRange(new[] { id }, origin);
        }

        public void AddRange(IEnumerable<int> ids, SelectionOrigin origin)
        {
            if (ids == null) return;
            var added = new List<int>();
            _suppress = true;
            try
            {
                foreach (var id in ids)
                {
                    if (!_selected.Contains(id)) { _selected.Add(id); added.Add(id); }
                }
                if (added.Count > 0)
                    SelectionChanged?.Invoke(this, new SelectionSetChangedEventArgs(added, Array.Empty<int>(), origin));
            }
            finally { _suppress = false; }
        }

        public void Remove(int id, SelectionOrigin origin)
        {
            var removed = Array.Empty<int>();
            _suppress = true;
            try
            {
                if (_selected.Remove(id)) removed = new[] { id };
                if (removed.Length > 0)
                    SelectionChanged?.Invoke(this, new SelectionSetChangedEventArgs(Array.Empty<int>(), removed, origin));
            }
            finally { _suppress = false; }
        }

        public void Clear(SelectionOrigin origin)
        {
            if (_selected.Count == 0) return;
            _suppress = true;
            try
            {
                var removed = _selected.ToArray();
                _selected.Clear();
                SelectionChanged?.Invoke(this, new SelectionSetChangedEventArgs(Array.Empty<int>(), removed, origin));
            }
            finally { _suppress = false; }
        }
    }
}
