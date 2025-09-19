using System;
using System.Windows;
using IFC_Viewer_00.Services;
using Xunit;

namespace IFC_Viewer_00.Tests
{
    public class WindowsUiViewer3DServiceTests
    {
        private class FakeControl
        {
            // Properties probed by service
            public object? Model { get; set; }
            public object? Context { get; set; }
            public object? ModelContext { get; set; }

            // Flags to assert
            public bool ResetCalled { get; private set; }
            public bool ZoomExtentsCalled { get; private set; }
            public bool FitToViewCalled { get; private set; }
            public bool RefreshCalled { get; private set; }
            public bool ReloadModelCalled { get; private set; }
            public bool ShowAllCalled { get; private set; }
            public int HitTestPointCalls { get; private set; }
            public int HitTestXYCalls { get; private set; }

            // Methods invoked via reflection
            public void ResetCamera() => ResetCalled = true;
            public void ZoomExtents() => ZoomExtentsCalled = true;
            public void FitToView() => FitToViewCalled = true;
            public void Refresh() => RefreshCalled = true;
            public void ReloadModel() => ReloadModelCalled = true;
            public void ShowAll() => ShowAllCalled = true;

            public object? HitTest(Point p)
            {
                HitTestPointCalls++;
                return null;
            }

            public object? HitTest(double x, double y)
            {
                HitTestXYCalls++;
                return null;
            }
        }

        [Fact]
        public void SetModel_ShouldAssignModel_AndAttemptCameraRefreshAndShowAll()
        {
            var fake = new FakeControl();
            var svc = new WindowsUiViewer3DService(fake);

            svc.SetModel(null);

            // Model property may be set to null, that's fine; verify call effects
            Assert.True(fake.ResetCalled, "ResetCamera should be attempted");
            // Refresh or ReloadModel can be called; at least one should be true
            Assert.True(fake.RefreshCalled || fake.ReloadModelCalled, "Refresh/ReloadModel should be attempted");
            Assert.True(fake.ShowAllCalled, "ShowAll should be attempted");
        }

        [Fact]
        public void HitTest_ShouldPreferPointOverload()
        {
            var fake = new FakeControl();
            var svc = new WindowsUiViewer3DService(fake);

            var r = svc.HitTest(10, 20);
            Assert.Null(r);
            Assert.Equal(1, fake.HitTestPointCalls);
            // double,double may be tried if Point not found; ensure Point was called at least once
        }
    }
}
