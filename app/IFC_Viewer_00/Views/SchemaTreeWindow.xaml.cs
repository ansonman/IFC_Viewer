using System.Windows;
using Xbim.Ifc4.Interfaces;

namespace IFC_Viewer_00.Views
{
	public partial class SchemaTreeWindow : Window
	{
		public SchemaTreeWindow()
		{
			// 以反射呼叫 InitializeComponent（避免設計時期無 g.cs 時造成編譯錯誤）
			try
			{
				var init = GetType().GetMethod("InitializeComponent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
				init?.Invoke(this, null);
			}
			catch { }
		}

		// 為了相容舊呼叫點：提供 SetEntity 多載，實際轉交給 ViewModel（若支援）
		public void SetEntity(IIfcObject? entity)
		{
			try
			{
				if (this.DataContext is IFC_Viewer_00.ViewModels.SchemaTreeViewModel vm)
				{
					// 直接設定 Highlight 來源，並觸發刷新
					// VM 目前提供 RefreshFromSelectionCommand 依 getter 取值；
					// 這裡若需要即時套用，可以暫時置換 getter（簡化起見，僅嘗試呼叫現有命令）。
					vm.RefreshFromSelectionCommand?.Execute(null);
				}
			}
			catch { }
		}

		// 更寬鬆的多載以防呼叫簽章不同
		public void SetEntity(object? entity)
		{
			try { SetEntity(entity as IIfcObject); } catch { }
		}
	}
}

