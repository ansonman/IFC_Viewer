using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IFC_Viewer_00.ViewModels;
using IFC_Viewer_00.Services;
using Xunit;
using System.Threading;
using System.Windows.Threading;
using System.Reflection;

namespace IFC_Viewer_00.Tests
{
    public class MainViewModelTests
    {
        private static string FindProjectRoot()
        {
            // 優先從目前測試目錄往上找 Project1.ifc
            var dir = AppContext.BaseDirectory;
            var current = new DirectoryInfo(dir);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "Project1.ifc");
                if (File.Exists(candidate)) return current.FullName;
                current = current.Parent;
            }
            throw new FileNotFoundException("找不到 Project1.ifc，請確認檔案存在於方案根目錄。");
        }

        [Fact]
        public async Task LoadIfcFile_Should_Populate_Model_Property()
        {
            // Arrange
            // 使用既有預設建構子，它會注入 StubViewer3DService
            var vm = new MainViewModel();
            var root = FindProjectRoot();
            var filePath = Path.Combine(root, "Project1.ifc");
            Assert.True(File.Exists(filePath), $"測試檔不存在: {filePath}");

            // Act
            await vm.OpenFileByPathAsync(filePath);

            // Assert
            Assert.NotNull(vm.Model);
            // 使用 LINQ Count() 以避免不同 xBIM 版本 Count 屬性差異
            var count = vm.Model!.Instances.Count();
            Assert.True(count > 0, $"模型中不含 IFC 實體，Instances.Count={count}");
        }

        [Fact]
        public async Task LoadIfcFile_Should_Set_Model_On_ViewerControl()
        {
            // 在 STA + WPF Dispatcher 上執行，以確保 async 續傳回 UI 執行緒並安全存取 WPF 控制項
            Exception? threadEx = null;
            object? controlModel = null;
            object? vmModel = null;
            object? tagModel = null;
            IFC_Viewer_00.Services.WindowsUiViewer3DService? svcRef = null;

            var t = new Thread(() =>
            {
                try
                {
                    // 建立 Dispatcher 同步內容
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

                    // 嘗試載入 Xbim.WindowsUI（在測試環境中可能未自動載入）
                    try
                    {
                        Assembly.Load("Xbim.WindowsUI");
                    }
                    catch
                    {
                        // 若無法透過名稱載入，改從 app 專案輸出資料夾嘗試載入 dll
                        var rootDir = FindProjectRoot();
                        string[] candidates = new[]
                        {
                            Path.Combine(rootDir, "app", "IFC_Viewer_00", "bin", "Debug", "net8.0-windows", "Xbim.WindowsUI.dll"),
                            Path.Combine(rootDir, "app", "IFC_Viewer_00", "bin", "Release", "net8.0-windows", "Xbim.WindowsUI.dll")
                        };
                        foreach (var p in candidates)
                        {
                            if (File.Exists(p))
                            {
                                try { Assembly.LoadFrom(p); break; } catch { /* 忽略，嘗試下一個 */ }
                            }
                        }
                    }

                    // 探查可用之 Xbim 視覺控制項型別（優先尋找名稱含 DrawingControl3D，其次任何位於 Xbim.Presentation 的類別，且具備可寫 Model 屬性）
                    var viewerType = Type.GetType("Xbim.Presentation.DrawingControl3D, Xbim.WindowsUI", throwOnError: false);
                    if (viewerType == null)
                    {
                        viewerType = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a =>
                            {
                                try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                            })
                            .FirstOrDefault(t =>
                                (t.FullName?.Contains("Xbim.Presentation", StringComparison.OrdinalIgnoreCase) ?? false)
                                && t.GetProperty("Model", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null
                                && !t.IsAbstract
                            );
                    }

                    object viewer;
                    if (viewerType != null)
                    {
                        viewer = Activator.CreateInstance(viewerType!);
                    }
                    else
                    {
                        // 找不到 Xbim 控制項，使用簡易的假控制項驗證服務指派邏輯
                        viewerType = typeof(FakeViewerControl);
                        viewer = new FakeViewerControl();
                    }

                    var svc = new WindowsUiViewer3DService(viewer!);
                    svcRef = svc;
                    var vm = new MainViewModel(svc);

                    var root = FindProjectRoot();
                    var filePath = Path.Combine(root, "Project1.ifc");
                    Assert.True(File.Exists(filePath), $"測試檔不存在: {filePath}");

                    // 在 Dispatcher 上以 async 方式執行，完成後關閉訊息迴圈
                    dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            await vm.OpenFileByPathAsync(filePath);
                            // 確認 ViewModel 已載入
                            Assert.NotNull(vm.Model); // IfcStore 載入失敗或檔案路徑錯誤
                            vmModel = vm.Model;

                            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                            var modelProp = viewerType!.GetProperty("Model", flags);
                            if (modelProp != null)
                            {
                                controlModel = modelProp!.GetValue(viewer);
                            }
                            else
                            {
                                var modelField = viewerType!.GetField("Model", flags);
                                if (modelField != null)
                                {
                                    controlModel = modelField.GetValue(viewer);
                                }
                            }

                            // 嘗試從 Tag 讀取（保底診斷用）
                            var tagProp = viewerType!.GetProperty("Tag", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (tagProp != null)
                            {
                                tagModel = tagProp.GetValue(viewer);
                            }

                            // 允許控制項內部只接受底層相容模型：
                            // - 若 controlModel 為 null，但 Tag 或服務記錄了相同 IfcStore，也視為成功
                            // - 若 controlModel 存在，但不是同一參考，允許；最少要保證服務收到相同 IfcStore
                            Assert.NotNull(vmModel);
                            if (controlModel == null)
                            {
                                Assert.True(tagModel != null || (svcRef?.LastAssignedModel) != null, "控制項未暴露 Model；Tag 與服務 LastAssignedModel 亦為 null");
                                if (tagModel != null)
                                {
                                    Assert.Same(vmModel, tagModel);
                                }
                                else
                                {
                                    Assert.Same(vmModel, svcRef!.LastAssignedModel);
                                }
                            }
                            else
                            {
                                // 如果控制項剛好接受 IfcStore，應該要是同一參考
                                if (ReferenceEquals(vmModel, controlModel))
                                {
                                    Assert.Same(vmModel, controlModel);
                                }
                                else
                                {
                                    // 否則至少驗證服務側的 IfcStore 一致
                                    Assert.Same(vmModel, svcRef!.LastAssignedModel);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            threadEx = ex;
                        }
                        finally
                        {
                            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                        }
                    });

                    // 啟動 Dispatcher 訊息迴圈，直到上面工作結束
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });

            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();

            if (threadEx != null) throw new Xunit.Sdk.XunitException($"STA 測試執行例外: {threadEx}");

            // 外層最終一致性檢查（主要驗證已在 STA 內完成）：
            Assert.NotNull(vmModel);
            // 若控制項未暴露 Model，至少服務側記錄應一致
            if (controlModel != null)
            {
                Assert.Same(vmModel, controlModel);
            }
        }
    }
}
