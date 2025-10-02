using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IFC_Viewer_00.Models;
using IFC_Viewer_00.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xbim.Ifc4.Interfaces;

namespace IFC_Viewer_00.ViewModels
{
	public partial class SchemaTreeViewModel : ObservableObject
	{
		private readonly SchemaService _schemaService;
		private readonly ISelectionService _selection;
		private readonly Func<IIfcObject?> _getCurrentSingleSelection;

		[ObservableProperty]
		private SchemaNode root = new SchemaNode { PropertyName = "Root", PropertyType = "", PropertyValue = "No data" };

		[ObservableProperty]
		private int maxDepth = 5;

		[ObservableProperty]
		private bool autoRefresh = true;

		[ObservableProperty]
		private string selectedInfo = "(未選取)";

		[ObservableProperty]
		private string status = "就緒";

		public IRelayCommand RefreshFromSelectionCommand { get; }

		public SchemaTreeViewModel(SchemaService schemaService, ISelectionService selection, Func<IIfcObject?> getCurrentSingleSelection)
		{
			_schemaService = schemaService;
			_selection = selection;
			_getCurrentSingleSelection = getCurrentSingleSelection;
			RefreshFromSelectionCommand = new AsyncRelayCommand(RefreshFromSelectionAsync);
			_selection.SelectionChanged += OnSelectionChanged;
		}

		private async void OnSelectionChanged(object? sender, SelectionSetChangedEventArgs e)
		{
			if (!AutoRefresh) return;
			await RefreshFromSelectionAsync();
		}

		private Task RefreshFromSelectionAsync()
		{
			try
			{
				var ent = _getCurrentSingleSelection();
				if (ent == null)
				{
					SelectedInfo = "(未選取)";
					Root = new SchemaNode { PropertyName = "Root", PropertyType = "", PropertyValue = "No selection" };
					Status = "請在左側樹或 3D 檢視選取一個物件";
					return Task.CompletedTask;
				}
				var label = (ent as Xbim.Common.IPersistEntity)?.EntityLabel;
				SelectedInfo = label.HasValue ? $"#{label.Value} {ent.GetType().Name}" : ent.GetType().Name;
				Root = _schemaService.GenerateSchemaTree((Xbim.Common.IPersistEntity)ent, "Entity", MaxDepth);
				Status = $"已載入：{SelectedInfo} (MaxDepth={MaxDepth})";
			}
			catch (Exception ex)
			{
				Status = $"載入失敗：{ex.Message}";
			}
			return Task.CompletedTask;
		}
	}
}

