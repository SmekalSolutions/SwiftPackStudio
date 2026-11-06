using System.Windows;
using SwiftPack.Views;

namespace SwiftPack
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Enable WPF's pointer-based input stack (WM_POINTER) so precision-
            // touchpad two-finger scrolling is delivered to scroll controls. The
            // legacy stack swallows that input, so without this the touchpad cannot
            // scroll grids/lists. Must be set before any window or input is created.
            System.AppContext.SetSwitch(
                "Switch.System.Windows.Input.Stylus.EnablePointerSupport", true);

            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Open the main window directly. There is no sign-in gate: the app
            // launches straight into the UI.
            var mw = new MainWindow();
            MainWindow = mw;
            mw.Closed += (_, _) => Shutdown();
            mw.Show();
            InstallHWheelHook(mw);
        }

        // ── Horizontal wheel (WM_MOUSEHWHEEL) ─────────────────────────────
        // WPF never raises the horizontal mouse-wheel message a touchpad two-finger
        // horizontal swipe sends, so horizontal scrolling reaches no ScrollViewer.
        // Hook it once on the main window and scroll whichever ScrollViewer is under
        // the pointer; this covers every page in the app.
        private const int WM_MOUSEHWHEEL = 0x020E;

        private void InstallHWheelHook(Window w)
        {
            if (PresentationSource.FromVisual(w) is System.Windows.Interop.HwndSource src)
                src.AddHook(HWheelHook);
        }

        private System.IntPtr HWheelHook(System.IntPtr hwnd, int msg,
            System.IntPtr wParam, System.IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEHWHEEL)
            {
                short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                var sv = ScrollViewerUnderMouse();
                if (sv != null && sv.ScrollableWidth > 0 && delta != 0)
                {
                    int lines = System.Math.Max(1, System.Math.Abs((int)delta) / 40);
                    for (int i = 0; i < lines; i++)
                    {
                        if (delta > 0) sv.LineRight(); else sv.LineLeft();
                    }
                    handled = true;
                }
            }
            return System.IntPtr.Zero;
        }

        private static System.Windows.Controls.ScrollViewer? ScrollViewerUnderMouse()
        {
            System.Windows.DependencyObject? d =
                System.Windows.Input.Mouse.DirectlyOver as System.Windows.DependencyObject;
            while (d != null)
            {
                // Skip inner scrollviewers that can't scroll horizontally (e.g. a
                // TextBox's content host) and keep walking up to the one that can.
                if (d is System.Windows.Controls.ScrollViewer sv && sv.ScrollableWidth > 0)
                    return sv;
                d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : null;
            }
            return null;
        }
    }
}
