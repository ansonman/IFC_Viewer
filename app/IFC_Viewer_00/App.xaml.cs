using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace IFC_Viewer_00;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		try
		{
			var baseDir = AppDomain.CurrentDomain.BaseDirectory;
			var logPath = Path.Combine(baseDir, "viewer3d.log");
			Trace.AutoFlush = true;
			// 將 Trace 導向到檔案（服務中改用 Trace.WriteLine）
			if (!Trace.Listeners.OfType<TextWriterTraceListener>().Any())
			{
				var fileListener = new TextWriterTraceListener(logPath);
				Trace.Listeners.Add(fileListener);
			}
			Trace.WriteLine($"[App] Startup at {DateTime.Now:O}. Logs -> {logPath}");
		}
		catch { /* 忽略記錄器初始化失敗 */ }
	}
}

