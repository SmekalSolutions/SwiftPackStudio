using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SwiftPack.Views
{
    public partial class MainWindow : Window, IAutoPackageHost, IAutoSilentHost
    {
        private Button? _activeNavButton;
        private string? _lastIntunewinPath;        // set when intunewin is created — used for Create Document
        private string? _lastIntunewinPackageRoot;  // package root folder matching _lastIntunewinPath
        private string? _lastIntunewinFileSize;     // pre-computed file size string, set at creation time

        private EditMSIPage            _mstEditorPage      = new EditMSIPage();
        private CreatePackagePage      _pkgPage            = new CreatePackagePage();
        private ExecutePackagePage     _execPage           = new ExecutePackagePage();
        private CreateIntunewinPage    _intunePage         = new CreateIntunewinPage();
        private PublishPackagePage     _publishPage        = new PublishPackagePage();
        private readonly AutoPackagePage        _autoPackagePage    = new AutoPackagePage();
        public AutoPackagePage AutoPackagePage => _autoPackagePage; // IAutoPackageHost

        // True after Execute Package completes an Install (cleared on Uninstall). When set,
        // opening Publish & Deploy refreshes detection data from the now-installed app with
        // priority over Create Package values.
        private bool _execInstallCompleted = false;

        // ── Silent Install/Uninstall source — cancellation + progress (IAutoPackageHost) ──

        /// <summary>
        /// Cancels an in-progress silent Install/Uninstall source run. Signals the token
        /// (so no further silent steps start) and kills the active child process tree.
        /// </summary>
        public void CancelSilentRun() => AutoSilentRunner.Cancel();

        // Forward silent-phase progress to the SourceDownloadPage actually displayed inside
        // AutoPackagePage's FrameDownload (the visible instance), via AutoPackagePage so we
        // hit the page the user is looking at — not this window's own field instance.
        public void BeginSilentPhase(string phase) => _autoPackagePage.ForwardSilentPhaseToDownload("BeginSilentPhase", phase);
        public void EndSilentPhase()              => _autoPackagePage.ForwardSilentPhaseToDownload("EndSilentPhase");

        // Status dialog for silent Install/Uninstall source — shown only when the per-app
        // ShowMessageBox flag is Yes (mirrored into AutoShowMessageBox.Value). Mirrors the
        // look of the other "— Complete" dialogs. Must be called on the UI thread.
        internal ValidatePackagePage   _qaPage             = new ValidatePackagePage();
        private ValidateSourcePage      _sourceValPage      = new ValidateSourcePage();
        private CreateDocumentPage     _docPage            = new CreateDocumentPage();
        private SourceDownloadPage     _sourceDownloadPage;
        private System.Windows.Threading.DispatcherTimer? _globalScheduleTimer;

        // ── Win32 imports for .lnk shell drag-drop and taskbar restore ────────────

        // Shell drag-drop
        [DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr hwnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile,
            System.Text.StringBuilder? lpszFile, uint cch);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);

        [DllImport("shell32.dll")]
        private static extern bool DragQueryPoint(IntPtr hDrop, out POINT lppt);

        // Low-level global mouse hook — receives mouse events system-wide,
        // even when this window is minimized (unlike WPF Mouse.LeftButton).
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook,
            LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT   pt;
            public uint    mouseData;
            public uint    flags;
            public uint    time;
            public IntPtr  dwExtraInfo;
        }

        private const int  WH_MOUSE_LL  = 14;
        private const int  WM_LBUTTONUP = 0x0202;
        private const int  WM_MOUSEMOVE = 0x0200;
        private const int  WM_DROPFILES = 0x0233;
        private const uint GA_ROOT      = 2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        // Keep a strong reference so the delegate is not GC'd while the hook is active
        private LowLevelMouseProc? _mouseHookProc;
        private IntPtr             _mouseHookHandle    = IntPtr.Zero;
        private bool               _lButtonDown        = false;
        private DateTime           _taskbarHoverStart  = DateTime.MinValue;
        private bool               _hoveringTaskbar    = false;
        // How long cursor must hover over taskbar while dragging before we restore
        private static readonly TimeSpan TaskbarHoverDelay = TimeSpan.FromMilliseconds(600);

        // Taskbar window class names (consistent across Windows 10/11)
        private static readonly string[] TaskbarClassNames =
        {
            "Shell_TrayWnd",        // main taskbar
            "Shell_SecondaryTrayWnd", // secondary monitor taskbar
            "MSTaskSwWClass",       // taskbar button area
            "MSTaskListWClass",     // task list
            "ThumbnailDeviceHelperWnd", // thumbnail preview
            "TaskListThumbnailWnd", // Win11 thumbnail
        };

        private static bool IsTaskbarWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            foreach (var name in TaskbarClassNames)
                if (cls.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            // Also walk up to parent in case we hit a child of the taskbar
            IntPtr parent = GetAncestor(hwnd, GA_ROOT);
            if (parent != hwnd && parent != IntPtr.Zero)
            {
                GetClassName(parent, sb, sb.Capacity);
                cls = sb.ToString();
                foreach (var name in TaskbarClassNames)
                    if (cls.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ── SourceInitialized: hook WndProc and install global mouse hook ──────────
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd   = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);

            // Register to receive WM_DROPFILES from the Windows Shell (.lnk files)
            DragAcceptFiles(hwnd, true);

            // Install a low-level global mouse hook so we get mouse events even
            // when this window is minimized. Standard WPF events and
            // Mouse.LeftButton are frozen for minimized windows, but WH_MOUSE_LL
            // receives every mouse message system-wide.
            _mouseHookProc = LowLevelMouseHook;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var mod  = proc.MainModule!;
            _mouseHookHandle = SetWindowsHookEx(
                WH_MOUSE_LL, _mouseHookProc,
                GetModuleHandle(mod.ModuleName), 0);

            // Clean up hook when window closes
            Closed += (_, _) =>
            {
                if (_mouseHookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_mouseHookHandle);
                    _mouseHookHandle = IntPtr.Zero;
                }
            };
        }

        /// <summary>
        /// Global low-level mouse hook callback. Receives every mouse event
        /// on the system, including during a drag while this window is minimized.
        ///
        /// Strategy: track left-button state. On every MOUSEMOVE while the button
        /// is down (= a drag is in progress) and the window is minimized, check
        /// whether the cursor is over our window handle. If yes, restore.
        /// This is exactly how Explorer and other apps handle "drag to taskbar".
        /// </summary>
        /// <summary>
        /// Global low-level mouse hook. Receives every mouse event system-wide,
        /// including during a drag while this window is minimized.
        ///
        /// The taskbar button belongs to Shell_TrayWnd (a different process),
        /// so we cannot match it by HWND. Instead we detect that the cursor is
        /// over any taskbar window class while a drag is in progress, and restore
        /// after the cursor has hovered there for TaskbarHoverDelay (600 ms) —
        /// matching Windows' own "drag to taskbar to restore" behaviour.
        /// </summary>
        private IntPtr LowLevelMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;

                if (msg == 0x0201) // WM_LBUTTONDOWN
                {
                    _lButtonDown       = true;
                    _hoveringTaskbar   = false;
                    _taskbarHoverStart = DateTime.MinValue;
                }
                else if (msg == WM_LBUTTONUP)
                {
                    _lButtonDown       = false;
                    _hoveringTaskbar   = false;
                    _taskbarHoverStart = DateTime.MinValue;
                }
                else if (msg == WM_MOUSEMOVE && _lButtonDown &&
                         WindowState == WindowState.Minimized)
                {
                    var    info      = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    IntPtr hwndUnder = WindowFromPoint(info.pt);
                    bool   onTaskbar = IsTaskbarWindow(hwndUnder);

                    if (onTaskbar)
                    {
                        if (!_hoveringTaskbar)
                        {
                            // Cursor just entered the taskbar — start the hover timer
                            _hoveringTaskbar   = true;
                            _taskbarHoverStart = DateTime.UtcNow;
                        }
                        else if (DateTime.UtcNow - _taskbarHoverStart >= TaskbarHoverDelay)
                        {
                            // Hovered long enough — restore window
                            _hoveringTaskbar   = false;
                            _taskbarHoverStart = DateTime.MinValue;
                            Dispatcher.BeginInvoke(() =>
                            {
                                if (WindowState == WindowState.Minimized)
                                {
                                    WindowState = WindowState.Normal;
                                    Activate();
                                }
                            });
                        }
                    }
                    else
                    {
                        // Cursor left the taskbar area — reset hover timer
                        _hoveringTaskbar   = false;
                        _taskbarHoverStart = DateTime.MinValue;
                    }
                }
            }
            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        /// <summary>
        /// WndProc hook — intercepts WM_DROPFILES so .lnk files dropped directly
        /// on the window are forwarded to CreatePackagePage.
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                               ref bool handled)
        {
            if (msg == WM_DROPFILES)
            {
                HandleShellDrop(wParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// WPF-level window drop handler — catches all file types dropped on the
        /// window after it is restored from minimized (via the taskbar hover restore).
        /// Forwards to CreatePackagePage.HandleLnkShellDrop which routes through
        /// ProcessDroppedFiles for correct handling of all extensions.
        /// </summary>
        private void MainWindow_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            // Only forward to CreatePackagePage if it is the currently active page
            if (_activeNavButton != BtnCreatePackage) return;
            var dropped = files.Where(f => f != null).ToList();
            if (dropped.Count > 0)
                _pkgPage.HandleLnkShellDrop(
                    dropped,
                    e.GetPosition(this));

            e.Handled = true;
        }

        private void HandleShellDrop(IntPtr hDrop)
        {
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0) { DragFinish(hDrop); return; }

            var dropped = new System.Collections.Generic.List<string>();
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, null, 0);
                var sb = new System.Text.StringBuilder((int)len + 2);
                DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                dropped.Add(sb.ToString());
            }

            DragQueryPoint(hDrop, out POINT dropPt);
            DragFinish(hDrop);

            // Forward ALL dropped files to CreatePackagePage ONLY if it is the active page
            if (dropped.Count > 0 && _activeNavButton == BtnCreatePackage)
                _pkgPage.HandleLnkShellDrop(
                    dropped,
                    new System.Windows.Point(dropPt.X, dropPt.Y));
        }

        // ── Nav lock: one owner at a time ──────────────────────────────────
        // AppBusy raises Changed from whichever thread finished the work, so this hops to
        // the UI thread before touching buttons.
        private void OnAppBusyChanged()
        {
            if (Dispatcher.CheckAccess()) ApplyNavLock();
            else Dispatcher.BeginInvoke((Action)ApplyNavLock);
        }

        /// <summary>
        /// Greys the nav buttons that must not be used right now. NAV BUTTONS ONLY - the
        /// pages stay live because the auto chain navigates to ExecutePackagePage and
        /// PublishPackagePage programmatically and would otherwise break itself.
        /// Auto Package stays enabled during an auto run so the run can be watched.
        /// </summary>
        // Tracks the previous auto state so ApplyNavLock can spot the moment a run starts.
        private bool _autoWasRunning;

        /// <summary>
        /// Brings the Auto Package page to the front. Page instances are long-lived fields,
        /// so whatever the user had typed on the page being left is still there when they
        /// come back - navigating away costs them nothing.
        /// </summary>
        private void ShowAutoPackageNow()
        {
            try
            {
                if (_activeNavButton != BtnAutoPackage)
                {
                    if (_activeNavButton != null) _activeNavButton.Tag = null;
                    _activeNavButton = BtnAutoPackage;
                    _activeNavButton.Tag = "Active";
                }
                if (MainFrame.Content != _autoPackagePage)
                    MainFrame.Navigate(_autoPackagePage);
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[MainWindow] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private void ApplyNavLock()
        {
            try
            {
                bool auto   = AppBusy.AutoRunning;
                bool manual = AppBusy.ManualRunning;

                // Rising edge only: an auto run has JUST started, so bring it on screen.
                // Sitting on Create MSI while the toolbar quietly greys out reads as the app
                // freezing; showing the run explains itself. Rising edge matters because the
                // chain navigates to Execute Package and Publish Package mid-run - reacting
                // to every change would yank the user back here and hide those steps.
                if (auto && !_autoWasRunning) ShowAutoPackageNow();
                _autoWasRunning = auto;

                string autoWhy   = $"Auto Package is running ({AppBusy.AutoReason}). Manual features are unavailable until it finishes.";
                string manualWhy = $"A manual feature is running ({AppBusy.ManualReason}). Auto Package is unavailable until it finishes.";

                void Lock(Button? b, bool locked, string why)
                {
                    if (b == null) return;
                    b.IsEnabled = !locked;
                    b.ToolTip   = locked ? why : null;
                    b.Opacity   = locked ? 0.45 : 1.0;
                }

                foreach (var b in new[] { BtnMstEditor, BtnSourceValidation, BtnCreatePackage,
                                          BtnExecutePackage, BtnPerformQA, BtnCreateIntunewin,
                                          BtnPublishDeploy, BtnTabDocument })
                    Lock(b, auto, autoWhy);

                Lock(BtnAutoPackage, manual, manualWhy);
                // "New" resets the whole workspace; that is destructive mid-run either way.
                Lock(BtnNew, auto || manual, auto ? autoWhy : manualWhy);
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[MainWindow] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        public MainWindow()
        {
            _sourceDownloadPage = new SourceDownloadPage();
            InitializeComponent();
            // Inject MainWindow reference so scheduled downloads work before page is visited
            _sourceDownloadPage.SetMainWindow(this);
            Loaded += (_, _) => StartGlobalScheduleTimer();
            _activeNavButton = BtnAutoPackage;
            MainFrame.Navigate(_autoPackagePage);

            // One owner at a time: while an auto run is live the manual features are
            // locked out, and while a manual feature is working Auto Package is. See
            // AppBusy for why (the auto chain drives the manual pages).
            AppBusy.Changed += OnAppBusyChanged;
            Unloaded        += (_, _) => AppBusy.Changed -= OnAppBusyChanged;
            ApplyNavLock();

            // Wire SourceInitialized here — Window events cannot be set via XAML attributes
            SourceInitialized += MainWindow_SourceInitialized;

            // Pre-load CreatePackage.json defaults immediately after construction.
            // Doing this here avoids all Page.Loaded / NavigationService timing issues
            // since controls exist as soon as InitializeComponent() returns.
            WireUpPages();

            // Apply the user's saved theme preference (Light on first run)
            ThemeManager.LoadSaved();
            UpdateThemeToggleButton();

            // Hook window close — confirm close, then prompt to save unsaved script.
            Closing += (_, e) =>
            {
                if (!ShowStyledConfirm(
                        "Close SwiftPack Studio?",
                        "Any unsaved work will be lost and cannot be restored the next time you open the app. Are you sure you want to close?"))
                {
                    e.Cancel = true;
                    return;
                }

                if (!_pkgPage.PromptSaveAndClose())
                    e.Cancel = true;
            };
        }

        // All cross-page wiring, extracted so both the constructor and the New button
        // (which rebuilds the pages) apply the exact same hookups.
        private void WireUpPages()
        {
            _pkgPage.ApplyCreatePackageConfig();

            // When a package is successfully created, immediately pre-fill the Execute Package
            // script path and Create Intunewin paths — so they are ready even before the user
            // navigates to those pages.
            _pkgPage.OnScriptPathCreated = ps1 =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _lastIntunewinPath = null; _lastIntunewinFileSize = null; // new package — clear stale intunewin from previous run
                    _execPage.SetScriptPath(ps1);
                    _intunePage.AutoFillFromScript(ps1);
                }));
            };

            // Clear stale intunewin when a different package's script is loaded into Execute Package.
            _execPage.OnScriptPathChanged = newPs1 =>
            {
                if (string.IsNullOrEmpty(newPs1)) { _lastIntunewinPath = null; _lastIntunewinPackageRoot = null; _lastIntunewinFileSize = null; return; }
                if (string.IsNullOrEmpty(_lastIntunewinPackageRoot)) return; // nothing to clear
                // Go up two levels from ps1 to get package root
                string? ps1Root = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(newPs1) ?? "") ?? "";
                if (!string.IsNullOrEmpty(ps1Root) &&
                    !ps1Root.Equals(_lastIntunewinPackageRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _lastIntunewinPath        = null;
                    _lastIntunewinPackageRoot = null;
                    _lastIntunewinFileSize    = null;
                }
            };

            // After each successful Install/Uninstall, update CreateDocument and auto-export .txt/.docx.
            _execPage.OnDeployCompleted = (deployMode, logPath, scriptPath) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Clear BOTH stored log paths first — then set only the one we just ran
                    _execPage.LastInstallLogPath   = null;
                    _execPage.LastUninstallLogPath = null;

                    // Build + auto-export the Create Document content from the CURRENT session.
                    // Defined as a local action so the Install branch can defer it until AFTER
                    // the Publish & Deploy detection rules have been populated.
                    Action buildDocNow = () =>
                    {
                        _docPage.ClearAllFields();
                        _docPage.LoadSession(BuildDocumentSession(scriptPath, deployMode, logPath));
                        _docPage.AutoExportToDocuments();
                    };

                    if (deployMode.Equals("Install", StringComparison.OrdinalIgnoreCase))
                    {
                        _execPage.LastInstallLogPath = logPath;
                        // Execute Package just installed the app on this machine. Give this the
                        // highest priority for Publish & Deploy auto-fill: the next time the user
                        // opens Publish & Deploy we refresh detection data from the now-installed
                        // app, even if the App Name field is already populated.
                        _execInstallCompleted = true;

                        // After a successful install (exit code 0), wait 2 seconds for the
                        // installer's registry/file writes to settle, then:
                        //   1) run the SAME full Publish & Deploy auto-fill that navigating to
                        //      the page triggers — this populates the MSI / File / Registry
                        //      detection rules from the now-installed app WITHOUT requiring the
                        //      user to open the Publish Package page first;
                        //   2) run the File-rule prefill (Path / File-or-folder / value / time
                        //      per the selected detection method);
                        //   3) THEN build the Create Document session + auto-export — so the
                        //      Detection Rules box gets the full live MSI / File / Registry
                        //      block via BuildDetectionSummary(), not just the registry rule.
                        // UI/memory only — no intunewin and no per-app json is written here.
                        System.Threading.Tasks.Task.Delay(2000).ContinueWith(__ =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    _publishPage.AutoFillFromPackage(
                                        scriptPath:    _execPage.LastScriptPath ?? _pkgPage.LastCreatedScriptPath,
                                        mainFilePath:  _pkgPage.LastMainFilePath,
                                        appName:       _pkgPage.LastAppName,
                                        appVendor:     _pkgPage.LastAppVendor,
                                        appVersion:    _pkgPage.LastAppVersion,
                                        intunewinPath: _intunePage.LastIntunewinPath,
                                        forceInstalledRefresh: true);
                                }
                                catch { }
                                try
                                {
                                    _publishPage.PrefillFileRuleFromInstalledApp(
                                        _pkgPage.LastAppName, _pkgPage.LastAppVendor);
                                }
                                catch { }
                                // NOTE: _execInstallCompleted stays true on purpose — opening the
                                // Publish Package page afterwards re-runs the refresh harmlessly.
                                try { buildDocNow(); } catch { }
                            }));
                        });
                    }
                    else if (deployMode.Equals("Uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        _execPage.LastUninstallLogPath = logPath;
                        _execInstallCompleted = false;
                    }

                    // Resolve the package root for the Documents auto-export. Auto Package
                    // sets PendingPackageFolder; the manual Create -> Execute flow does not,
                    // so derive it from the executed script path when it is empty:
                    //   <pkgRoot>\<PSADT subfolder>\Invoke-AppDeployToolkit.ps1
                    // (the PSADT subfolder is named "Package", "Package22", etc.).
                    string pkgRootForDocs = ValidatePackagePage.PendingPackageFolder?.Trim() ?? "";
                    if (string.IsNullOrEmpty(pkgRootForDocs) && !string.IsNullOrEmpty(scriptPath))
                    {
                        // Documents / QA exports are SIBLINGS of the app-package folder — i.e. the
                        // PARENT of the script's own folder — matching where Execute copies its logs
                        // (GetParent(scriptDir)) and how Create Package lays out the structure:
                        //   ps1 at <pkg>\Package               → base <pkg>
                        //   ps1 at <pkg>\AppPackages           → base <pkg>
                        //   ps1 at <pkg>\Output\AppPackages\Test → base <pkg>\Output\AppPackages
                        string ps1Folder = System.IO.Path.GetDirectoryName(scriptPath) ?? "";
                        if (!string.IsNullOrEmpty(ps1Folder))
                            pkgRootForDocs = System.IO.Path.GetDirectoryName(ps1Folder) ?? ps1Folder;
                        // Publish the derived root so the QA CSV/HTML auto-export resolves it too.
                        if (!string.IsNullOrEmpty(pkgRootForDocs))
                            ValidatePackagePage.PendingPackageFolder = pkgRootForDocs;
                    }
                    if (string.IsNullOrEmpty(CreateDocumentPage.RuntimeDocumentsFolder)
                        && !string.IsNullOrEmpty(pkgRootForDocs))
                    {
                        CreateDocumentPage.RuntimeDocumentsFolder =
                            System.IO.Path.Combine(pkgRootForDocs, "Documents");
                    }

                    // Install builds the document inside the delayed continuation above —
                    // every other deploy mode (Uninstall / Repair) builds it right away.
                    if (!deployMode.Equals("Install", StringComparison.OrdinalIgnoreCase))
                        buildDocNow();
                }));
            };

            // After a .intunewin file is created via the MANUAL Create Intunewin page, update
            // CreateDocument. The auto flow used to be wired here too, and that was the leak.
            Action<string> handleIntunewinCreated = intunewinPath =>
            {
                // Set the path and pre-compute file size synchronously (fast, no directory scan).
                _lastIntunewinPath        = intunewinPath;
                _lastIntunewinPackageRoot = System.IO.Path.GetDirectoryName(
                    System.IO.Path.GetDirectoryName(intunewinPath) ?? "") ?? "";
                try
                {
                    long bytes = new System.IO.FileInfo(intunewinPath).Length;
                    _lastIntunewinFileSize = bytes >= 1_048_576
                        ? $"{bytes / 1_048_576.0:F1} MB"
                        : $"{bytes / 1024.0:F1} KB";
                }
                catch { _lastIntunewinFileSize = ""; }

                // Capture for use in background thread
                string capturedRoot     = _lastIntunewinPackageRoot;
                string capturedPath     = intunewinPath;
                string capturedFileSize = _lastIntunewinFileSize ?? "";

                // Run SafeGetFiles (recursive directory scan) off the UI thread to avoid freezing,
                // then call BuildDocumentSession and UI updates back on the UI thread.
                System.Threading.Tasks.Task.Run(() =>
                {
                    string? intunePs1 = SafeGetFiles(capturedRoot, "Invoke-AppDeployToolkit.ps1")
                        .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _docPage.ClearAllFields();
                        _docPage.LoadSession(BuildDocumentSession(intunePs1));
                        _docPage.AutoExportToDocuments();
                    }));
                });
            };

            _intunePage.OnIntunewinCreated     = path => Dispatcher.BeginInvoke(new Action(() => handleIntunewinCreated(path)));
            // The AUTO flow is deliberately NOT wired into handleIntunewinCreated any more.
            // It used to be, and every auto intunewin then CLEARED the manual Create
            // Document page, loaded the auto run's session into it, and ran its export -
            // the intunewin name and size appearing on that page after an auto run was
            // this line's fingerprint. The Auto Package strip owns its own Document tab
            // and .txt/.docx now (AutoDocumentPage), so the manual page stays exactly as
            // the user left it. The no-op keeps any unconditional invoker safe.
            _autoPackagePage.OnIntunewinCreated = _ => { };

            // Live mirror: ANY manual change on the Publish Package page is pushed to the
            // Create Document page immediately — overlapping fields (app name, publisher,
            // version, category, description, install/uninstall commands, install
            // behaviour) and the full Detection Rules block.
            _publishPage.LiveDataChanged += () =>
            {
                try
                {
                    var f = _publishPage.GetLiveDocumentFields();
                    _docPage.ApplyLivePublishData(f.AppName, f.Publisher, f.Version, f.Category,
                        f.Description, f.InstallCmd, f.UninstallCmd, f.InstallBehaviour,
                        f.DetectionSummary);
                }
                catch { }
            };

            // Mirror the Publish page's Intune sign-in state onto the top-bar button (2 lines).
            // Both states keep line 1 short so the button width does not grow when signed in.
            _publishPage.IntuneSignedInChanged += signedIn => Dispatcher.Invoke(() =>
            {
                TxtIntuneSignIcon.Text  = signedIn ? "🔓" : "🔑";
                TxtIntuneSignLine1.Text = signedIn ? "Sign out"  : "Sign in to";
                TxtIntuneSignLine2.Text = signedIn ? "of Intune" : "Intune";
                BtnSignInIntune.ToolTip = signedIn
                    ? "Sign out of Intune"
                    : "Sign in to Intune now so the next publish does not prompt for login";
            });
            _publishPage.IntuneSignInEnabledChanged += enabled =>
                Dispatcher.Invoke(() => BtnSignInIntune.IsEnabled = enabled);

            // Toggle the overlay progress bar (top of content strip) during an Intune sign-in,
            // without navigating to the Publish page's Log tab or shifting the layout.
            _publishPage.IntuneSignInBusyChanged += busy => Dispatcher.Invoke(() =>
            {
                if (IntuneProgressBar != null)
                    IntuneProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            });

            // A freshly-wired Publish page has no Intune session yet, so show the signed-out state.
            TxtIntuneSignIcon.Text    = "🔑";
            TxtIntuneSignLine1.Text   = "Sign in to";
            TxtIntuneSignLine2.Text   = "Intune";
            BtnSignInIntune.ToolTip   = "Sign in to Intune now so the next publish does not prompt for login";
            BtnSignInIntune.IsEnabled = true;

        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (_activeNavButton != null) _activeNavButton.Tag = null;
            btn.Tag = "Active";
            _activeNavButton = btn;

            if      (btn == BtnMstEditor)       { MainFrame.Navigate(_mstEditorPage); }
            else if (btn == BtnSourceValidation){ MainFrame.Navigate(_sourceValPage); }
            else if (btn == BtnCreatePackage)   { MainFrame.Navigate(_pkgPage); }
            else if (btn == BtnExecutePackage)
            {
                // Do NOT auto-fill on navigation. Browsing a PSADT template in Create Package
                // sets its script path (LastCreatedScriptPath), but that is not a "created"
                // package - filling here made the Script File appear just from browsing. The
                // Execute Package script path is filled by the OnScriptPathCreated callback
                // (WireUpPages) only when a package is actually created, so on a fresh open it
                // stays empty.
                MainFrame.Navigate(_execPage);
            }
            else if (btn == BtnCreateIntunewin)
            {
                // Do NOT auto-fill on navigation. Browsing a PSADT template in Create Package
                // sets its script path (LastCreatedScriptPath), but that is not a "created"
                // package - filling here made the fields appear just from browsing. The Create
                // Intunewin fields are filled by the OnScriptPathCreated callback (WireUpPages)
                // only when a package is actually created, so on a fresh open they stay empty.
                MainFrame.Navigate(_intunePage);
            }
            else if (btn == BtnPublishDeploy)
            {
                // Priority 1: if Execute Package just installed the app on this machine, refresh
                // Publish & Deploy from the now-installed app — even if App Name is already set.
                // Priority 2 (fallback): auto-fill from CreatePackage values, but only when the
                // App Name field is still empty (don't clobber a completed intunewin flow).
                bool publishAppNameEmpty = string.IsNullOrWhiteSpace(
                    (_publishPage.FindName("TxtAppName") as System.Windows.Controls.TextBox)?.Text);
                if (_execInstallCompleted || publishAppNameEmpty)
                {
                    _publishPage.AutoFillFromPackage(
                        scriptPath:    _execPage.LastScriptPath ?? _pkgPage.LastCreatedScriptPath,
                        mainFilePath:  _pkgPage.LastMainFilePath,
                        appName:       _pkgPage.LastAppName,
                        appVendor:     _pkgPage.LastAppVendor,
                        appVersion:    _pkgPage.LastAppVersion,
                        intunewinPath: _intunePage.LastIntunewinPath,
                        forceInstalledRefresh: _execInstallCompleted);  // execute-package install wins
                    _execInstallCompleted = false;   // consume the one-shot priority refresh
                }
                MainFrame.Navigate(_publishPage);
            }
            else if (btn == BtnAutoPackage)     { MainFrame.Navigate(_autoPackagePage); }
            else if (btn == BtnPerformQA)       { MainFrame.Navigate(_qaPage); }
            else if (btn == BtnHelp)
            {
                // Opens the bundled help document in the default browser.
                var help = System.IO.Path.Combine(AppContext.BaseDirectory, "Docs", "SwiftPack_Help.html");
                if (System.IO.File.Exists(help))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = help, UseShellExecute = true });
                    }
                    catch { ShowStyledMessage("SwiftPack Studio", "Could not open the help document."); }
                }
                else
                {
                    ShowStyledMessage("SwiftPack Studio", "Help file not found - please reinstall the application.");
                }
            }
        }

        // ── Called by SourceDownloadPage after download ────────────────────
        public void NavigateFromSourceDownload(string page)
        {
            if (_activeNavButton != null) _activeNavButton.Tag = null;
            _activeNavButton = page == "AutoPackage" ? BtnAutoPackage : BtnSourceValidation;
            _activeNavButton.Tag = "Active";
            MainFrame.Navigate(page == "AutoPackage"
                ? (System.Windows.Controls.Page)_autoPackagePage
                : _sourceValPage);
        }

        // ── Set source path in Source Validation or Auto Package ──────────
        public void SetSourceDownloadPath(string target, string filePath)
        {
            if (target == "SourceValidation")
                _sourceValPage.SetSourceFilePath(filePath);
            else if (target == "AutoPackage")
                _autoPackagePage.SetSourcePath(filePath);
        }

        // ── Navigate to Auto Package and trigger Create ───────────────────
        public void NavigateAndTriggerAutoPackage(string filePath, string appVendor = "", string appName = "", string closeApps = "",
            bool doInstallPkg = false, bool doUninstallPkg = false, string pkgTemplateFolder = "",
            string appVersion = "", string uninstallPath = "")
        {
            if (_activeNavButton != null) _activeNavButton.Tag = null;
            _activeNavButton = BtnAutoPackage;
            _activeNavButton.Tag = "Active";

            _autoPackagePage.SetSourcePath(filePath);
            _autoPackagePage.SetPackageMetadata(appVendor, appName, closeApps, appVersion, uninstallPath);
            _autoPackagePage.SetPackageTemplateFolder(pkgTemplateFolder);

            if (doInstallPkg || doUninstallPkg)
            {
                _autoPackagePage.SetAfterCreateCallback(() =>
                {
                    string pkgFolder = _autoPackagePage.LastCreatedPackageFolder;
                    if (doInstallPkg)
                    {
                        if (doUninstallPkg)
                            NavigateToExecutePackageAndInstallThenUninstall(pkgFolder);
                        else
                            NavigateToExecutePackageAndInstall(pkgFolder);
                    }
                    else if (doUninstallPkg)
                        NavigateToExecutePackageAndUninstall(pkgFolder);
                });
            }

            MainFrame.Navigate(_autoPackagePage);
            _ = _autoPackagePage.TriggerAutoCreate();
        }

        // ── Navigate to Execute Package and trigger Admin Install ──────────
        /// <summary>
        /// Finds Invoke-AppDeployToolkit.ps1 in the package folder, navigates to
        /// Execute Package, sets the script path and clicks Admin Install.
        /// </summary>
        public void NavigateToExecutePackageAndInstall(string packageFolder)
        {
            string? ps1 = FindInvokeScript(packageFolder);
            if (ps1 == null) return;

            if (_activeNavButton != null) _activeNavButton.Tag = null;
            _activeNavButton = BtnExecutePackage;
            _activeNavButton.Tag = "Active";
            MainFrame.Navigate(_execPage);
            _execPage.SetScriptAndInstall(ps1);
        }

        // ── Install then automatically Uninstall after Install completes ───
        public void NavigateToExecutePackageAndInstallThenUninstall(string packageFolder)
        {
            string? ps1 = FindInvokeScript(packageFolder);
            if (ps1 == null) return;

            if (_activeNavButton != null) _activeNavButton.Tag = null;
            _activeNavButton = BtnExecutePackage;
            _activeNavButton.Tag = "Active";
            MainFrame.Navigate(_execPage);
            // Fires Install; on Install completion automatically fires Uninstall
            _execPage.SetScriptAndInstallThenUninstall(ps1);
        }

        // ── Navigate to Execute Package and trigger Admin Uninstall ────────
        public void NavigateToExecutePackageAndUninstall(string packageFolder)
        {
            string? ps1 = FindInvokeScript(packageFolder);
            if (ps1 == null) return;

            if (_activeNavButton != null) _activeNavButton.Tag = null;
            _activeNavButton = BtnExecutePackage;
            _activeNavButton.Tag = "Active";
            MainFrame.Navigate(_execPage);
            _execPage.SetScriptAndUninstall(ps1);
        }

        /// <summary>
        /// Searches common sub-paths for Invoke-AppDeployToolkit.ps1 in the given folder.
        /// </summary>
        private static string? FindInvokeScript(string packageFolder)
        {
            if (string.IsNullOrWhiteSpace(packageFolder)) return null;

            // Common locations inside a PSAppDeployToolkit package
            string[] candidates =
            {
                System.IO.Path.Combine(packageFolder, "Native", "Invoke-AppDeployToolkit.ps1"),
                System.IO.Path.Combine(packageFolder, "Invoke-AppDeployToolkit.ps1"),
                System.IO.Path.Combine(packageFolder, "Deploy-Application.ps1"),
            };

            foreach (var candidate in candidates)
                if (System.IO.File.Exists(candidate))
                    return candidate;

            // Recursive search one level deep
            try
            {
                foreach (var dir in System.IO.Directory.GetDirectories(packageFolder))
                {
                    var found = System.IO.Directory.GetFiles(dir, "Invoke-AppDeployToolkit.ps1");
                    if (found.Length > 0) return found[0];
                }
            }
            catch { }

            return null;
        }

        // ── Navigate to Source Validation, pre-fill path+args, and Execute ──
        /// <summary>
        /// Navigates to Source Validation, sets the source path (file + silent install switches),
        /// and triggers Execute. After the install capture completes:
        ///   - if doCreatePackage is true  → triggers Create Package with the downloaded file
        ///   - if doUninstall is true      → runs Silent Uninstall after Create Package completes
        /// Pass empty string for uninstallCmd to skip uninstall.
        /// </summary>
        public void NavigateToSourceValidationAndExecute(
            string sourcePath,
            string uninstallCmd,
            string downloadedFilePath = "",
            bool   doCreatePackage    = false,
            bool   doUninstall        = false,
            string pkgInstallArgs     = "",
            string pkgUninstallArgs   = "",
            string pkgAppVendor       = "",
            string pkgAppName         = "",
            string pkgCloseApps       = "",
            string pkgSourceAppName   = "",
            bool   doInstallPkg       = false,
            bool   doUninstallPkg     = false,
            string pkgTemplateFolder  = "",
            string pkgAppVersion      = "",
            string pkgUninstallPath   = "",
            string copyLogsToDocuments  = "",
            string copyCsvToDocuments   = "",
            string copyHtmlToDocuments  = "",
            string copyTxtToDocuments   = "",
            string copyDocxToDocuments  = "")
        {
            // ── Apply per-app Documents copy flags ────────────────────────────
            // These override the global config file values for the duration of this run.
            // CreateDocumentPage's Txt/Docx overrides are deliberately NOT set here any
            // more: the auto flow no longer drives the manual Create Document page at all
            // (its own AutoDocumentPage writes the .txt/.docx), and setting them from here
            // only leaked auto flags into the user's next manual export.
            ExecutePackageConfig.RuntimeCopyLogsOverride          = string.IsNullOrWhiteSpace(copyLogsToDocuments) ? null : copyLogsToDocuments;
            ValidatePackagePage.RuntimeCopyCSVOverride             = string.IsNullOrWhiteSpace(copyCsvToDocuments)  ? null : copyCsvToDocuments;
            ValidatePackagePage.RuntimeCopyHtmlOverride            = string.IsNullOrWhiteSpace(copyHtmlToDocuments) ? null : copyHtmlToDocuments;
            // Defensive clear: no auto run may leave a stale export target on the manual page.
            CreateDocumentPage.RuntimeDocumentsFolder = null;

            if (_activeNavButton != null) _activeNavButton.Tag = null;
            _activeNavButton = BtnSourceValidation;
            _activeNavButton.Tag = "Active";

            MainFrame.Navigate(_sourceValPage);

            string registryLookupName = string.IsNullOrWhiteSpace(pkgSourceAppName)
                ? pkgAppName : pkgSourceAppName;

            _sourceValPage.SetSourcePathAndExecute(
                sourcePath, uninstallCmd, downloadedFilePath, doCreatePackage, doUninstall,
                (filePath) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_activeNavButton != null) _activeNavButton.Tag = null;
                        _activeNavButton = BtnAutoPackage;
                        _activeNavButton.Tag = "Active";
                        _autoPackagePage.SetSourcePath(filePath);
                        _autoPackagePage.SetInstallUninstallArgs(pkgInstallArgs, pkgUninstallArgs);
                        _autoPackagePage.SetPackageMetadata(pkgAppVendor, pkgAppName, pkgCloseApps, pkgAppVersion, pkgUninstallPath);
                        _autoPackagePage.SetPackageTemplateFolder(pkgTemplateFolder);

                        // Register Silent Uninstall + Install/Uninstall package callbacks
                        _autoPackagePage.SetAfterCreateCallback(() =>
                        {
                            string pkgFolder = _autoPackagePage.LastCreatedPackageFolder;

                            // 1. Silent Uninstall (if checked)
                            if (doUninstall)
                            {
                                var parts    = (uninstallCmd + "|" + registryLookupName).Split('|', 2);
                                var uCmd     = parts.Length > 0 ? parts[0] : "";
                                var uAppName = parts.Length > 1 ? parts[1] : registryLookupName;
                                _sourceValPage.RunSilentUninstall(uCmd, uAppName);
                            }

                            // 2. Install package (if checked)
                            if (doInstallPkg)
                            {
                                if (doUninstallPkg)
                                    NavigateToExecutePackageAndInstallThenUninstall(pkgFolder);
                                else
                                    NavigateToExecutePackageAndInstall(pkgFolder);
                            }
                            else if (doUninstallPkg)
                                NavigateToExecutePackageAndUninstall(pkgFolder);
                        });

                        MainFrame.Navigate(_autoPackagePage);
                        _ = _autoPackagePage.TriggerAutoCreate();
                    });
                },
                (uninstCmdAndName) =>
                {
                    // Silent Install checked but Create Package NOT checked — uninstall fires directly
                    Dispatcher.Invoke(() =>
                    {
                        if (doCreatePackage) return;
                        var parts    = uninstCmdAndName.Split('|', 2);
                        var uCmd     = parts.Length > 0 ? parts[0] : "";
                        var uAppName = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                            ? parts[1] : registryLookupName;
                        _sourceValPage.RunSilentUninstall(uCmd, uAppName);
                    });
                },
                appName:    registryLookupName,
                appVendor:  pkgAppVendor,
                appVersion: pkgAppVersion);
        }

        // ── Called by SourceDownloadPage after "Install source silently" download ──
        // Runs the PSADT install command directly (no ValidateSourcePage / EtwQaCapture)
        // then optionally runs the PSADT uninstall command and triggers AutoPackage.
        // The silent Install source / Uninstall source runner now lives in AutoPackageShared.cs
        // (AutoSilentRunner). It belongs to the Auto Package feature, not to a window.
        //
        // This method stays because IAutoPackageHost declares it and SourceDownloadPage calls it.
        public void RunPsadtCommandAndThenAutoPackage(
            string installCmd,
            string uninstallCmd,
            string appName,
            string sourceAppName         = "",
            string appVendor             = "",
            string appVersion            = "",
            string closeApps             = "",
            string uninstallPath         = "",
            string templateFolder        = "",
            string downloadedFile        = "",
            bool   doCreatePackage       = false,
            bool   doInstallPkg          = false,
            bool   doUninstallPkg        = false,
            string pkgInstallArgs        = "",
            string pkgUninstallArgs      = "",
            string unifiedLogPath        = "",
            string copyLogsToDocuments   = "",
            string copyCsvToDocuments    = "",
            string copyHtmlToDocuments   = "",
            string copyTxtToDocuments    = "",
            string copyDocxToDocuments   = "")
            => AutoSilentRunner.Run(this, installCmd, uninstallCmd, appName, sourceAppName,
                   appVendor, appVersion, closeApps, uninstallPath, templateFolder, downloadedFile,
                   doCreatePackage, doInstallPkg, doUninstallPkg, pkgInstallArgs, pkgUninstallArgs,
                   unifiedLogPath, copyLogsToDocuments, copyCsvToDocuments, copyHtmlToDocuments,
                   copyTxtToDocuments, copyDocxToDocuments);

        // ── IAutoSilentHost ───────────────────────────────────────────────
        // The only parts of the silent flow that are genuinely window-shaped.
        public System.Windows.Window? OwnerWindow => this;

        /// <summary>Step 3: navigate to Auto Package and start creation, then chain to Execute package.</summary>
        public void StartAutoPackageChain(
            string downloadedFile, string pkgInstallArgs, string pkgUninstallArgs,
            string appVendor, string appName, string closeApps, string appVersion,
            string uninstallPath, string templateFolder,
            bool doInstallPkg, bool doUninstallPkg)
        {
            if (_activeNavButton != null) _activeNavButton.Tag = null;
            _activeNavButton = BtnAutoPackage;
            _activeNavButton.Tag = "Active";

            _autoPackagePage.SetSourcePath(downloadedFile);
            _autoPackagePage.SetInstallUninstallArgs(pkgInstallArgs, pkgUninstallArgs);
            _autoPackagePage.SetPackageMetadata(appVendor, appName, closeApps, appVersion, uninstallPath);
            _autoPackagePage.SetPackageTemplateFolder(templateFolder);

            _autoPackagePage.SetAfterCreateCallback(() =>
            {
                string pkgFolder = _autoPackagePage.LastCreatedPackageFolder;
                if (doInstallPkg)
                {
                    if (doUninstallPkg)
                        NavigateToExecutePackageAndInstallThenUninstall(pkgFolder);
                    else
                        NavigateToExecutePackageAndInstall(pkgFolder);
                }
                else if (doUninstallPkg)
                    NavigateToExecutePackageAndUninstall(pkgFolder);
            });

            MainFrame.Navigate(_autoPackagePage);
            _ = _autoPackagePage.TriggerAutoCreate();
        }

        /// <summary>
        /// Looks up the UninstallString for an app from the Windows registry.
        /// Uses strict matching: the registry DisplayName must be a close match to appName
        /// (same words, not just a substring) to avoid uninstalling the wrong app.
        /// Searches HKLM 64-bit, HKLM WOW6432Node, and HKCU hives.
        /// </summary>

        // ── Called by CreateIntunewinPage after successful .intunewin creation ──
        // Navigates to Publish & Deploy and auto-fills all fields from package data.
        public void NavigateToPublishDeploy(string intunewinPath)
        {
            // Mark Publish & Deploy nav button as active
            if (_activeNavButton != null) _activeNavButton.Tag = null;
            BtnPublishDeploy.Tag = "Active";
            _activeNavButton = BtnPublishDeploy;

            _publishPage.AutoFillFromPackage(
                scriptPath:    _pkgPage.LastCreatedScriptPath,
                mainFilePath:  _pkgPage.LastMainFilePath,
                appName:       _pkgPage.LastAppName,
                appVendor:     _pkgPage.LastAppVendor,
                appVersion:    _pkgPage.LastAppVersion,
                intunewinPath: intunewinPath);

            MainFrame.Navigate(_publishPage);
        }

        // ── Export all Publish & Deploy tab data to IntuneAppSettings.json ──
        public void ExportIntuneAppSettings(string outputFolder, string? intunewinPath = null, string? sourcePs1 = null)
        {
            try
            {
                string? overrideName = null, overrideVendor = null, overrideVersion = null;

                // Read the app identity from the package's Invoke-AppDeployToolkit.ps1, passed as
                // overrides so the Publish & Deploy UI fields are NOT touched. Prefer the known
                // source ps1 (the Source File used to build the package). Only fall back to
                // searching the intunewin's package folder if that is missing - and never search a
                // drive root: a custom Output Folder (e.g. C:\AppData) makes the intunewin's
                // grandparent the drive root, and recursively scanning C:\ would freeze the app.
                string? ps1 = (!string.IsNullOrEmpty(sourcePs1) && File.Exists(sourcePs1)) ? sourcePs1 : null;
                if (ps1 == null && !string.IsNullOrEmpty(intunewinPath))
                {
                    string pkgRoot = Path.GetDirectoryName(Path.GetDirectoryName(intunewinPath) ?? "") ?? "";
                    string trimmedRoot = pkgRoot.TrimEnd('\\', '/');
                    bool isDriveRoot = trimmedRoot.Length <= 2;   // "C:" after trimming a drive root
                    if (pkgRoot.Length > 0 && !isDriveRoot)
                        ps1 = SafeGetFiles(pkgRoot, "Invoke-AppDeployToolkit.ps1")
                            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                }
                if (!string.IsNullOrEmpty(ps1) && File.Exists(ps1))
                {
                    string ps1Text = File.ReadAllText(ps1);
                    overrideName    = Ps1Val(ps1Text, "AppName")    ?? "";
                    overrideVendor  = Ps1Val(ps1Text, "AppVendor")  ?? "";
                    overrideVersion = Ps1Val(ps1Text, "AppVersion") ?? "";
                }

                _publishPage.WriteIntuneAppSettingsJson(outputFolder,
                    overrideAppName:       string.IsNullOrEmpty(overrideName)    ? null : overrideName,
                    overridePublisher:     string.IsNullOrEmpty(overrideVendor)  ? null : overrideVendor,
                    overrideAppVersion:    string.IsNullOrEmpty(overrideVersion) ? null : overrideVersion,
                    overrideIntunewinPath: intunewinPath);
            }
            catch { /* silently ignore — non-critical */ }
        }

        private void StartGlobalScheduleTimer()
        {
            // Run scheduled downloads regardless of which page is active
            _globalScheduleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _globalScheduleTimer.Tick += async (_, _) =>
            {
                try { await _sourceDownloadPage.RunScheduledDownloadsAsync(); }
                catch { /* silently ignore scheduler errors */ }
            };
            _globalScheduleTimer.Start();
            // Run immediately on startup to catch any missed schedules
            _ = _sourceDownloadPage.RunScheduledDownloadsAsync();
        }

        // Single theme toggle: switches to the opposite theme, refreshes the code-built
        // Auto Package download tab so brushes update immediately, then updates the button
        // so it always shows the theme the user can switch TO.
        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle();
            try { _sourceDownloadPage?.RefreshAfterThemeChange(); } catch { }
            UpdateThemeToggleButton();
        }

        // Light mode shows "Dark" (switch to dark); Dark mode shows "Light" (switch to light).
        private void UpdateThemeToggleButton()
        {
            bool isDark = ThemeManager.Current == AppTheme.Dark;
            if (TxtThemeIcon   != null) TxtThemeIcon.Text   = isDark ? "☀" : "🌙";
            if (TxtThemeLabel  != null) TxtThemeLabel.Text  = isDark ? "Light" : "Dark";
            if (BtnThemeToggle != null) BtnThemeToggle.ToolTip = isDark ? "Switch to Light theme" : "Switch to Dark theme";
        }

        // ── Session ────────────────────────────────────────────────────────────

        // Starts a fresh session: rebuilds every page so all forms and in-page state reset
        // to their just-opened state, clears cross-page runtime state, and returns to the
        // Auto Package page (which is intentionally kept). The app stays open.
        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowStyledConfirm(
                    "Start new",
                    "This resets every section to its newly-opened state."))
                return;

            // Rebuild every page except Auto Package so each one starts fresh.
            _mstEditorPage      = new EditMSIPage();
            _pkgPage            = new CreatePackagePage();
            _execPage           = new ExecutePackagePage();
            _intunePage         = new CreateIntunewinPage();
            _publishPage        = new PublishPackagePage();
            _qaPage             = new ValidatePackagePage();
            _sourceValPage      = new ValidateSourcePage();
            _docPage            = new CreateDocumentPage();
            _sourceDownloadPage = new SourceDownloadPage();
            _sourceDownloadPage.SetMainWindow(this);

            // Re-apply all cross-page wiring to the fresh instances.
            WireUpPages();

            // Clear cross-page runtime state so nothing carries over from the old session.
            _lastIntunewinPath        = null;
            _lastIntunewinPackageRoot = null;
            _lastIntunewinFileSize    = null;
            _execInstallCompleted     = false;
            ValidatePackagePage.PendingPackageFolder  = null;
            CreateDocumentPage.RuntimeDocumentsFolder = null;

            // Return to the Auto Package page (kept as-is) and mark its nav button active.
            if (_activeNavButton != null) _activeNavButton.Tag = null;
            _activeNavButton = BtnAutoPackage;
            BtnAutoPackage.Tag = "Active";
            MainFrame.Navigate(_autoPackagePage);
        }

        // Drives the Publish page's Intune session from the global top bar, so both manual
        // and auto package deployments can sign in without first opening the Publish page.
        // The sign-in runs in place - no navigation to the Publish page or Log tab. The result
        // is reported by a dialog, and every attempt is recorded in %temp%\Sign in to intune.log.
        private void BtnSignInIntune_Click(object sender, RoutedEventArgs e)
        {
            _publishPage.ToggleIntuneSignIn();
        }

        // Close from the top bar. Just requests the window close; the Closing handler
        // shows the "Close SwiftPack Studio?" confirmation and the unsaved-work prompt,
        // so the button behaves exactly like the title-bar X.
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // App-styled Yes/No confirmation dialog (replaces the stock MessageBox so
        // it matches the rest of the product). Returns true when the user confirms.
        private bool ShowStyledConfirm(string heading, string body)
        {
            var dlg = new Window
            {
                Owner = this,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
            };

            var card = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                CornerRadius = new CornerRadius(14),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE4, 0xE4, 0xF0)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(14),
            };
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.22, Color = System.Windows.Media.Color.FromRgb(0x3B, 0x3B, 0x4F) };

            var root = new StackPanel { Margin = new Thickness(28, 26, 28, 26) };

            // Brand row: logo + app name, centered.
            var brand = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16),
            };
            var brandImg = LoadAppIcon();
            if (brandImg != null)
            {
                brand.Children.Add(new System.Windows.Controls.Image
                {
                    Source = brandImg, Width = 22, Height = 22,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
                });
            }
            brand.Children.Add(new TextBlock
            {
                Text = "SwiftPack Studio", FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x3B, 0x4F)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            root.Children.Add(brand);

            // Question icon in a soft circle.
            var ring = new Border
            {
                Width = 52, Height = 52, CornerRadius = new CornerRadius(26),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xEA, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
                Child = new TextBlock
                {
                    Text = "?", FontSize = 26, FontWeight = FontWeights.Bold,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0x63, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            root.Children.Add(ring);

            root.Children.Add(new TextBlock
            {
                Text = heading, FontSize = 19, FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x3A)),
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
            });
            root.Children.Add(new TextBlock
            {
                Text = body, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x6B)),
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 22),
            });

            bool result = false;
            var btnRow = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };

            var no = MakeDialogButton("No", false);
            no.Click += (_, _) => { result = false; dlg.Close(); };
            btnRow.Children.Add(no);

            var yes = MakeDialogButton("Yes", true);
            yes.Click += (_, _) => { result = true; dlg.Close(); };
            btnRow.Children.Add(yes);

            root.Children.Add(btnRow);
            card.Child = root;
            dlg.Content = card;
            dlg.ShowDialog();
            return result;
        }

        // Themed single-OK message dialog matching ShowStyledConfirm's look, replacing raw
        // MessageBox.Show for a consistent appearance.
        private void ShowStyledMessage(string heading, string body)
        {
            var dlg = new Window
            {
                Owner = this, Width = 380, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
            };
            var card = new Border
            {
                Background = System.Windows.Media.Brushes.White, CornerRadius = new CornerRadius(14),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE4, 0xE4, 0xF0)),
                BorderThickness = new Thickness(1), Margin = new Thickness(14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.22, Color = System.Windows.Media.Color.FromRgb(0x3B, 0x3B, 0x4F) },
            };
            var root = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
            root.Children.Add(new TextBlock
            {
                Text = heading, FontSize = 16, FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x3A)),
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
            });
            root.Children.Add(new TextBlock
            {
                Text = body, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x6B)),
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20),
            });
            var ok = MakeDialogButton("OK", true);
            ok.Click += (_, _) => dlg.Close();
            ok.HorizontalAlignment = HorizontalAlignment.Center;
            ok.Width = 130;
            root.Children.Add(ok);
            card.Child = root;
            dlg.Content = card;
            dlg.ShowDialog();
        }

        // Loads the app logo for in-dialog branding. Tries the embedded resource
        // first (pack URI), then falls back to a file next to the exe. Returns null
        // if nothing is found, in which case the dialog simply shows the name only.
        private System.Windows.Media.ImageSource? LoadAppIcon()
        {
            foreach (var uri in new[]
            {
                "pack://application:,,,/swiftpack-icon.png",
                "pack://application:,,,/Resources/swiftpack-icon.png",
                "pack://application:,,,/swiftpack.ico",
            })
            {
                try { return new System.Windows.Media.Imaging.BitmapImage(new Uri(uri, UriKind.Absolute)); }
                catch { }
            }
            try
            {
                foreach (var file in new[] { "swiftpack-icon.png", "swiftpack.ico" })
                {
                    string path = System.IO.Path.Combine(AppContext.BaseDirectory, file);
                    if (System.IO.File.Exists(path))
                        return new System.Windows.Media.Imaging.BitmapImage(new Uri(path, UriKind.Absolute));
                }
            }
            catch { }
            return null;
        }

        // Builds a rounded dialog button (filled accent for the primary action,
        // light outline for the secondary).
        private Button MakeDialogButton(string text, bool primary)
        {
            var b = new Button
            {
                Content = text, Height = 40, Margin = new Thickness(primary ? 6 : 0, 0, primary ? 0 : 6, 0),
                Foreground = primary ? System.Windows.Media.Brushes.White
                                     : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x6B)),
                Background = primary ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0x63, 0xFF))
                                     : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF5)),
                BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold, FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var t = new ControlTemplate(typeof(Button));
            var bd = new System.Windows.FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            b.Template = t;
            return b;
        }


        // ── Create Document ───────────────────────────────────────────────────

        private void BtnTabDocument_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (_activeNavButton != null) _activeNavButton.Tag = null;
            btn.Tag          = "Active";
            _activeNavButton = btn;

            // Do NOT pre-fill here — all fields start empty when first opened.
            // The user clicks "Auto-Fill" to populate from the current session.
            MainFrame.Navigate(_docPage);
        }

        /// <summary>Recursively finds files matching a pattern, silently skipping inaccessible directories.</summary>
        private static IEnumerable<string> SafeGetFiles(string root, string pattern)
        {
            var results = new List<string>();
            try
            {
                results.AddRange(Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly));
                foreach (string dir in Directory.GetDirectories(root))
                    results.AddRange(SafeGetFiles(dir, pattern));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return results;
        }

        /// <summary>Builds the document session using explicit ps1/deployMode/logPath from OnDeployCompleted — bypasses all heuristics.</summary>
        internal CreateDocumentSession BuildDocumentSession(string? explicitPs1Path, string? deployMode = null, string? deployLogPath = null)
        {
            _explicitDocPs1      = explicitPs1Path;
            _explicitDeployMode  = deployMode;
            _explicitDeployLog   = deployLogPath;
            try { return BuildDocumentSession(); }
            finally { _explicitDocPs1 = null; _explicitDeployMode = null; _explicitDeployLog = null; }
        }

        private string? _explicitDocPs1;     // set by BuildDocumentSession(string) overload
        private string? _explicitDeployMode; // "Install" or "Uninstall"
        private string? _explicitDeployLog;  // exact log path from this specific run

        internal CreateDocumentSession BuildDocumentSession()
        {
            var s = new CreateDocumentSession();

            string Ctrl(string name)
            {
                try
                {
                    var ctrl = _autoPackagePage.FindName(name);
                    return ctrl switch
                    {
                        TextBox     tb  => tb.Text?.Trim()                                ?? string.Empty,
                        TextBlock   tbl => tbl.Text?.Trim()                               ?? string.Empty,
                        ComboBox    cb  => cb.Text?.Trim()                                ?? string.Empty,
                        RadioButton rb  => rb.IsChecked == true
                                           ? rb.Content?.ToString()?.Trim() ?? string.Empty
                                           : string.Empty,
                        _               => string.Empty,
                    };
                }
                catch { return string.Empty; }
            }

            bool IsChecked(string name)
            {
                try { return (_autoPackagePage.FindName(name) as RadioButton)?.IsChecked == true; }
                catch { return false; }
            }

            // 1. Parse latest AutoPackage_*.log from %TEMP%
            // Skip when an explicit scriptPath was supplied (Execute Package flow) — the AutoPackage
            // log belongs to a prior Auto Package run and would contain stale data for a different package.
            bool hasExplicitPs1 = !string.IsNullOrEmpty(_explicitDocPs1)
                && File.Exists(_explicitDocPs1)
                && !_explicitDocPs1.Contains("Template", StringComparison.OrdinalIgnoreCase);
            if (!hasExplicitPs1)
            {
                string tempDir  = Path.GetTempPath();
                string? autoLog = Directory
                    .GetFiles(tempDir, "AutoPackage_*.log")
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(autoLog) && File.Exists(autoLog))
                    ParseAutoPackageLog(autoLog, File.ReadAllLines(autoLog), s);
            }

            // ── Derive ps1 — absolute priority order ──────────────────────
            // 0. Explicit ps1 passed by caller (OnDeployCompleted scriptPath) — always correct
            // 1. _execPage.LastScriptPath — if it's a real package (not template)
            // 2. _lastIntunewinPackageRoot — set when intunewin was created
            // 3. Time-based fallback (last resort)
            s.PackageName         = "";
            s.CloseApps           = "";
            s.SourceMediaLocation = "";
            s.AppLang             = "";
            s.AppRevision         = "";

            string? logDerivedRoot = null;
            string? logDerivedPs1  = null;

            // Priority 0: explicit ps1 from caller — no guessing needed
            if (!string.IsNullOrEmpty(_explicitDocPs1) && File.Exists(_explicitDocPs1)
                && !_explicitDocPs1.Contains("Template", StringComparison.OrdinalIgnoreCase))
            {
                logDerivedPs1  = _explicitDocPs1;
                logDerivedRoot = Directory.GetParent(Path.GetDirectoryName(_explicitDocPs1) ?? "")?.FullName;
            }

            // Priority 1: _execPage.LastScriptPath (non-template)
            if (string.IsNullOrEmpty(logDerivedPs1))
            {
                string? execPs1 = _execPage.LastScriptPath;
                if (!string.IsNullOrEmpty(execPs1) && File.Exists(execPs1)
                    && !execPs1.Contains("Template", StringComparison.OrdinalIgnoreCase))
                {
                    logDerivedPs1  = execPs1;
                    logDerivedRoot = Directory.GetParent(Path.GetDirectoryName(execPs1) ?? "")?.FullName;
                }
            }

            // Priority 2: _lastIntunewinPackageRoot
            if (string.IsNullOrEmpty(logDerivedPs1) && !string.IsNullOrEmpty(_lastIntunewinPackageRoot)
                && Directory.Exists(_lastIntunewinPackageRoot))
            {
                logDerivedPs1 = SafeGetFiles(_lastIntunewinPackageRoot, "Invoke-AppDeployToolkit.ps1")
                    .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (!string.IsNullOrEmpty(logDerivedPs1))
                    logDerivedRoot = _lastIntunewinPackageRoot;
            }

            if (string.IsNullOrEmpty(logDerivedPs1))
            {
                // Fallback: find ps1 closest in time to last execute log
                string? lastLog = _execPage.LastInstallLogPath ?? _execPage.LastUninstallLogPath;
                string pkgsBase = !string.IsNullOrEmpty(_pkgPage.LastCreatedScriptPath)
                    ? (Directory.GetParent(
                        Directory.GetParent(Path.GetDirectoryName(_pkgPage.LastCreatedScriptPath) ?? "")?.FullName ?? "")
                        ?.FullName ?? @"C:\Packages")
                    : @"C:\Packages";
                if (!string.IsNullOrEmpty(lastLog) && Directory.Exists(pkgsBase))
                {
                    DateTime logTime = File.Exists(lastLog) ? File.GetLastWriteTime(lastLog) : DateTime.MinValue;
                    // Enumerate safely — skip folders we can't access
                    var ps1Files = SafeGetFiles(pkgsBase, "Invoke-AppDeployToolkit.ps1");
                    logDerivedPs1 = ps1Files
                        .OrderBy(f => Math.Abs((File.GetLastWriteTime(f) - logTime).TotalSeconds))
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(logDerivedPs1))
                        logDerivedRoot = Directory.GetParent(Path.GetDirectoryName(logDerivedPs1) ?? "")?.FullName;
                }
            }

            // 2. App metadata — only from ps1-derived source to avoid cross-package contamination
            bool usePkgPage = string.IsNullOrEmpty(logDerivedRoot);
            // When an explicit ps1 was supplied (Execute Package flow), skip the live AutoPackagePage
            // control read entirely — those controls may still show data from a prior Auto Package run.
            // The ps1 file itself (Step 3) is the correct source and will fill these in.
            if (!hasExplicitPs1)
            {
                if (string.IsNullOrEmpty(s.AppName))
                    s.AppName   = NzCtrl(Ctrl("ExeTxtAppName"),    Ctrl("TxtAppName"),    usePkgPage ? (_pkgPage.LastAppName    ?? "") : "");
                if (string.IsNullOrEmpty(s.AppVendor))
                    s.AppVendor = NzCtrl(Ctrl("ExeTxtPublisher"),  Ctrl("TxtAppVendor"),  usePkgPage ? (_pkgPage.LastAppVendor  ?? "") : "");
                if (string.IsNullOrEmpty(s.AppVersion))
                    s.AppVersion= NzCtrl(Ctrl("ExeTxtAppVersion"), Ctrl("TxtAppVersion"), usePkgPage ? (_pkgPage.LastAppVersion ?? "") : "");
            }
            s.AppRevision = "";
            s.Description       = NzCtrl(Ctrl("ExeTxtDescription"),  Ctrl("TxtDescription"),  "");
            s.GroupName         = NzCtrl(Ctrl("ExeTxtGroupName"),     Ctrl("TxtGroupName"),     "");
            s.InstallCommandLine   = "";  // always PSADT standard — set in step 5
            s.UninstallCommandLine = "";
            if (IsChecked("ExeRbDeplRequired"))       s.DeploymentType = "Required";
            else if (IsChecked("ExeRbDeplAvailable")) s.DeploymentType = "Available";
            bool isFile = IsChecked("ExeRbDetectFile");
            bool isReg  = IsChecked("ExeRbDetectReg");
            s.DetectionType        = isFile ? "File" : (isReg ? "Registry" : "");
            s.DetectionFilePath    = NzCtrl(Ctrl("ExeTxtDetectFilePath"),  "", "");
            s.DetectionFileName    = NzCtrl(Ctrl("ExeTxtDetectFileName"),  "", "");
            s.DetectionFileVersion = NzCtrl(Ctrl("ExeTxtDetectFileValue"), "", "");
            s.DetectionRegKey      = NzCtrl(Ctrl("ExeTxtDetectRegKey"),    "", "");
            s.DetectionRegValue    = NzCtrl(Ctrl("ExeTxtDetectRegValue"),  "", "");
            s.ProductCode          = NzCtrl(Ctrl("TxtProductCode"), Ctrl("MsiTxtProductCode"), "");

            // 3. Read ps1 for AppLang / ScriptDate
            string ps1Path = logDerivedPs1 ?? string.Empty;

            if (string.IsNullOrEmpty(ps1Path) && !string.IsNullOrEmpty(s.PackageRootFolder) && Directory.Exists(s.PackageRootFolder))
                ps1Path = Directory
                    .GetFiles(s.PackageRootFolder, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrEmpty(ps1Path))
            {
                string pkgPs1 = _pkgPage.LastCreatedScriptPath ?? string.Empty;
                bool samePackage = string.IsNullOrEmpty(s.AppVendor) ||
                    string.IsNullOrEmpty(_pkgPage.LastAppVendor) ||
                    (_pkgPage.LastAppVendor ?? "").Equals(s.AppVendor, StringComparison.OrdinalIgnoreCase);
                if (samePackage)
                    ps1Path = pkgPs1;
            }
            if (!string.IsNullOrEmpty(ps1Path) && File.Exists(ps1Path))
            {
                string ps1 = File.ReadAllText(ps1Path);
                // When an explicit ps1 was provided, always trust it for app identity —
                // don't let stale values from a prior run block the correct data.
                if (hasExplicitPs1 || string.IsNullOrEmpty(s.AppVendor))  s.AppVendor  = Ps1Val(ps1, "AppVendor")  ?? "";
                if (hasExplicitPs1 || string.IsNullOrEmpty(s.AppName))    s.AppName    = Ps1Val(ps1, "AppName")    ?? "";
                if (hasExplicitPs1 || string.IsNullOrEmpty(s.AppVersion)) s.AppVersion = Ps1Val(ps1, "AppVersion") ?? "";
                s.AppLang     = NzCtrl(Ps1Val(ps1, "AppLang") ?? "", Ctrl("ExeTxtAppLang"), "EN");
                s.AppRevision = NzCtrl(s.AppRevision, Ps1Val(ps1, "AppRevision") ?? "", "01");
                s.ScriptDate  = Ps1Val(ps1, "AppScriptDate") ?? string.Empty;
                // Always read CloseApps and SourceMediaLocation from the current ps1
                s.CloseApps = Ps1Val(ps1, "CloseProcesses") ?? Ps1Val(ps1, "CloseApps") ?? s.CloseApps;
                s.SourceMediaLocation = Path.GetDirectoryName(ps1Path) ?? s.SourceMediaLocation;
            }

            // 4. Package name — always rebuild when logDerivedRoot is active to avoid stale names
            if ((!string.IsNullOrEmpty(logDerivedRoot) || string.IsNullOrEmpty(s.PackageName))
                && !string.IsNullOrEmpty(s.AppVendor))
            {
                string rev  = string.IsNullOrEmpty(s.AppRevision) ? "01" : s.AppRevision.PadLeft(2, '0');
                s.PackageName = $"{s.AppVendor}_{s.AppName.Replace(" ", "")}_{s.AppVersion}_{rev}";
            }

            // 5. Install/Uninstall commands — always use the standard PSADT command when a ps1 was found
            // (reading from the ps1's Start-ADTProcess args gives app-specific installer args, not the PSADT wrapper)
            const string PsadtInstall   = "powershell.exe -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"Invoke-AppDeployToolkit.ps1\" -DeploymentType Install";
            const string PsadtUninstall = "powershell.exe -ExecutionPolicy Bypass -NonInteractive -WindowStyle Hidden -File \"Invoke-AppDeployToolkit.ps1\" -DeploymentType Uninstall";
            if (!string.IsNullOrEmpty(ps1Path))
            {
                s.InstallCommandLine   = PsadtInstall;
                s.UninstallCommandLine = PsadtUninstall;
            }
            else
            {
                if (!string.IsNullOrEmpty(s.InstallExeName) && string.IsNullOrEmpty(s.InstallCommandLine))
                    s.InstallCommandLine = $"\"{s.InstallExeName}\" {(string.IsNullOrEmpty(s.InstallArgs) ? "/S" : s.InstallArgs)}";
                if (!string.IsNullOrEmpty(s.UninstallPath) && string.IsNullOrEmpty(s.UninstallCommandLine))
                    s.UninstallCommandLine = $"\"{s.UninstallPath.Replace("$env:SystemDrive", "%SystemDrive%")}\" {(string.IsNullOrEmpty(s.UninstallArgs) ? "/S" : s.UninstallArgs)}";
            }

            bool LogMatchesPkg(string? logPath)
            {
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return false;
                if (string.IsNullOrEmpty(s.AppVendor) && string.IsNullOrEmpty(s.AppName)) return true;
                string logClean = System.Text.RegularExpressions.Regex.Replace(
                    Path.GetFileNameWithoutExtension(logPath), @"[\s,.]", "");
                string vendor = System.Text.RegularExpressions.Regex.Replace(s.AppVendor ?? "", @"[\s,.]", "");
                string app    = System.Text.RegularExpressions.Regex.Replace(s.AppName   ?? "", @"[\s,.]", "");
                return (!string.IsNullOrEmpty(vendor) && logClean.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(app)    && logClean.Contains(app,    StringComparison.OrdinalIgnoreCase));
            }

            // 6. Logs — use explicit values from OnDeployCompleted when available (always correct).
            // Only fall back to _execPage.Last* when no explicit values were provided (e.g. intunewin trigger).
            if (!string.IsNullOrEmpty(_explicitDeployMode) && !string.IsNullOrEmpty(_explicitDeployLog)
                && File.Exists(_explicitDeployLog))
            {
                // Only set the log for the mode that was actually run — leave the other empty
                if (_explicitDeployMode.Equals("Install", StringComparison.OrdinalIgnoreCase))
                    s.InstallLogPath = _explicitDeployLog;
                else if (_explicitDeployMode.Equals("Uninstall", StringComparison.OrdinalIgnoreCase))
                    s.UninstallLogPath = _explicitDeployLog;
            }
            else
            {
                // Fallback: use stored log paths but only if they match the current package
                if (LogMatchesPkg(_execPage.LastInstallLogPath))   s.InstallLogPath   = _execPage.LastInstallLogPath!;
                if (LogMatchesPkg(_execPage.LastUninstallLogPath)) s.UninstallLogPath = _execPage.LastUninstallLogPath!;
            }

            // When an explicit deploy mode was provided (Execute Package flow), only search for
            // the OTHER mode's log via fallback — never search for the mode already explicitly set,
            // and never search ANY fallback if both modes were already determined.
            bool explicitInstall   = !string.IsNullOrEmpty(_explicitDeployMode)
                && _explicitDeployMode.Equals("Install",   StringComparison.OrdinalIgnoreCase);
            bool explicitUninstall = !string.IsNullOrEmpty(_explicitDeployMode)
                && _explicitDeployMode.Equals("Uninstall", StringComparison.OrdinalIgnoreCase);

            // When an explicit deploy was run, we only have ONE log (the one we just ran).
            // Never search logDir/docsDir for the OTHER mode's log — it either doesn't exist
            // yet or belongs to a completely different package.
            bool skipFallbackSearch = explicitInstall || explicitUninstall;

            string logDir = string.Empty;
            if (!skipFallbackSearch)
            {
                try
                {
                    string qaVal = (_qaPage.FindName("TxtLogPath") as TextBox)?.Text?.Trim() ?? string.Empty;
                    if (!string.IsNullOrEmpty(qaVal))
                    {
                        logDir = File.Exists(qaVal) ? (Path.GetDirectoryName(qaVal) ?? string.Empty)
                                                    : (Directory.Exists(qaVal) ? qaVal : string.Empty);
                        if (File.Exists(qaVal))
                        {
                            string qaName = Path.GetFileName(qaVal);
                            if (qaName.Contains("_Uninstall", StringComparison.OrdinalIgnoreCase))
                            { if (string.IsNullOrEmpty(s.UninstallLogPath)) s.UninstallLogPath = qaVal; }
                            else
                            { if (string.IsNullOrEmpty(s.InstallLogPath))   s.InstallLogPath   = qaVal; }
                        }
                    }
                }
                catch { }
            }

            string docsDir = !string.IsNullOrEmpty(s.PackageRootFolder)
                ? Path.Combine(s.PackageRootFolder, "Documents") : string.Empty;

            if (!skipFallbackSearch)
            {
                foreach (string dir in new[] { logDir, docsDir }
                    .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
                {
                    if (string.IsNullOrEmpty(s.InstallLogPath))
                        s.InstallLogPath = Directory.GetFiles(dir, "*.log")
                            .Where(f => Path.GetFileName(f).Contains("_Install") && !Path.GetFileName(f).Contains("_Uninstall"))
                            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault() ?? string.Empty;
                    if (string.IsNullOrEmpty(s.UninstallLogPath))
                        s.UninstallLogPath = Directory.GetFiles(dir, "*.log")
                            .Where(f => Path.GetFileName(f).Contains("_Uninstall"))
                            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault() ?? string.Empty;
                }
            }

            // Sanity check: if InstallLogPath ended up with an Uninstall log, swap them
            if (!string.IsNullOrEmpty(s.InstallLogPath) &&
                Path.GetFileName(s.InstallLogPath).IndexOf("_Uninstall", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Wrong log in install slot — move to uninstall slot if empty
                if (string.IsNullOrEmpty(s.UninstallLogPath))
                    s.UninstallLogPath = s.InstallLogPath;
                s.InstallLogPath = string.Empty;
            }
            // If InstallLogPath is still empty, search again specifically for Install-only logs
            if (string.IsNullOrEmpty(s.InstallLogPath) && !string.IsNullOrEmpty(s.UninstallLogPath))
            {
                string uninstallDir = Path.GetDirectoryName(s.UninstallLogPath) ?? string.Empty;
                if (Directory.Exists(uninstallDir))
                    s.InstallLogPath = Directory.GetFiles(uninstallDir, "*.log")
                        .Where(f => { var n = Path.GetFileName(f); return n.IndexOf("Install", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) < 0; })
                        .OrderByDescending(File.GetLastWriteTime).FirstOrDefault() ?? string.Empty;
            }

            // 7. Intunewin — ONLY from _lastIntunewinPath when it matches the current package
            try
            {
                string? iPath = null;
                if (!string.IsNullOrEmpty(_lastIntunewinPath) && File.Exists(_lastIntunewinPath))
                {
                    // Only use the intunewin if its package root matches the current session's ps1 package root
                    bool samePackage = true;
                    if (!string.IsNullOrEmpty(_lastIntunewinPackageRoot) && !string.IsNullOrEmpty(ps1Path))
                    {
                        // ps1 is e.g. C:\Packages\VideoLAN_VLC_...\Package22\Invoke-AppDeployToolkit.ps1
                        // go up two levels to get package root: C:\Packages\VideoLAN_VLC_...
                        string? ps1PackageRoot = System.IO.Path.GetDirectoryName(
                            System.IO.Path.GetDirectoryName(ps1Path) ?? "") ?? "";
                        samePackage = string.IsNullOrEmpty(ps1PackageRoot) ||
                            ps1PackageRoot.Equals(_lastIntunewinPackageRoot, StringComparison.OrdinalIgnoreCase);
                    }
                    if (samePackage)
                        iPath = _lastIntunewinPath;
                }

                if (!string.IsNullOrEmpty(iPath))
                {
                    s.IntunewinPath     = iPath;
                    // Use the pre-computed size from handleIntunewinCreated — avoids file-lock/exception
                    // risks that occur when computing the size later via FileInfo.
                    s.IntunewinFileSize = _lastIntunewinFileSize ?? "";
                }
                else
                {
                    s.IntunewinPath     = "";
                    s.IntunewinFileSize = "";
                }
            }
            catch { }

            // ── Live Publish Package detection rules ───────────────────────────
            // Snapshot the Detection tab AS IT IS NOW (MSI / File / Registry
            // cards, or the detection script). This is what the Create Document
            // page shows in its Detection Rules box whenever it is non-empty —
            // it covers the gap where the post-install auto-prefill has filled
            // the tab but the per-app json has not been saved yet.
            try { s.DetectionSummary = _publishPage.BuildDetectionSummary(); } catch { }

            // ── DEBUG DUMP ──────────────────────────────────────────────────────
            try
            {
                string dbg = $"""
=== BuildDocumentSession DEBUG [{DateTime.Now:HH:mm:ss}] ===
_explicitDocPs1    : {_explicitDocPs1 ?? "(null)"}
_explicitDeployMode: {_explicitDeployMode ?? "(null)"}
_explicitDeployLog : {_explicitDeployLog ?? "(null)"}
hasExplicitPs1     : {(!string.IsNullOrEmpty(_explicitDocPs1) && File.Exists(_explicitDocPs1) && !_explicitDocPs1.Contains("Template", StringComparison.OrdinalIgnoreCase))}
s.AppName          : {s.AppName}
s.AppVendor        : {s.AppVendor}
s.AppVersion       : {s.AppVersion}
s.PackageName      : {s.PackageName}
s.InstallLogPath   : {s.InstallLogPath}
s.UninstallLogPath : {s.UninstallLogPath}
s.IntunewinFileSize: {s.IntunewinFileSize}
s.IntunewinPath    : {s.IntunewinPath}
s.DetectionSummary : {(string.IsNullOrWhiteSpace(s.DetectionSummary) ? "(empty)" : s.DetectionSummary.Replace("\r", "").Replace("\n", " | "))}
_lastIntunewinPath : {_lastIntunewinPath ?? "(null)"}
_lastIntunewinFileSize: {_lastIntunewinFileSize ?? "(null)"}
_lastIntunewinPackageRoot: {_lastIntunewinPackageRoot ?? "(null)"}
intunewin_file_exists: {(_lastIntunewinPath != null && File.Exists(_lastIntunewinPath))}
explicitInstall    : {explicitInstall}
explicitUninstall  : {explicitUninstall}
skipFallbackSearch : {skipFallbackSearch}
""";
                // Append the debug block to the package's own AutoPackage_<PackageRoot>.log
                // in %TEMP% instead of a separate SwiftPack_DocSession.log file.
                string pkgRootName = "";
                try
                {
                    if (!string.IsNullOrEmpty(ps1Path))
                        pkgRootName = Path.GetFileName(
                            Path.GetDirectoryName(Path.GetDirectoryName(ps1Path) ?? "") ?? "") ?? "";
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(pkgRootName))
                {
                    string pkgLog = Path.Combine(Path.GetTempPath(), $"AutoPackage_{pkgRootName}.log");
                    File.AppendAllText(pkgLog, Environment.NewLine + dbg + Environment.NewLine);
                }
                // Remove the legacy separate debug file if present from earlier versions.
                try { File.Delete(Path.Combine(Path.GetTempPath(), "SwiftPack_DocSession.log")); } catch { }
            }
            catch { }
            // ── END DEBUG ────────────────────────────────────────────────────────

            return s;
        }

        private static string NzCtrl(params string[] candidates)
        {
            foreach (var v in candidates)
                if (!string.IsNullOrWhiteSpace(v)) return v;
            return string.Empty;
        }

        private static string? Ps1Val(string ps1, string key)
        {
            var m = Regex.Match(ps1,
                $@"(?<![#\w]){Regex.Escape(key)}\s*=\s*['""]([^'""]*)['""]",
                RegexOptions.IgnoreCase);
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.Trim() : null;
        }

        private static void ParseAutoPackageLog(string logFilePath, string[] lines, CreateDocumentSession s)
        {
            string fn = Path.GetFileNameWithoutExtension(logFilePath);
            s.PackageName = fn.StartsWith("AutoPackage_", StringComparison.OrdinalIgnoreCase)
                ? fn.Substring("AutoPackage_".Length) : string.Empty;

            static string Val(string line)
            {
                int idx = line.IndexOf(" : ");
                return idx >= 0 ? line.Substring(idx + 3).Trim() : string.Empty;
            }

            bool inFields = false;
            foreach (string raw in lines)
            {
                // Strip the leading timestamp "[HH:mm:ss] " that Log() prepends to every line.
                string line = raw.Trim();
                if (line.Length > 11 && line[0] == '[' && line[9] == ']' && line[10] == ' ')
                    line = line.Substring(11).Trim();

                if (line == "--- Package Fields ---")     { inFields = true;  continue; }
                if (line == "--- End Package Fields ---") { inFields = false; continue; }

                if (inFields)
                {
                    if      (line.StartsWith("AppName "))              s.AppName              = Val(line);
                    else if (line.StartsWith("AppVendor "))            s.AppVendor            = Val(line);
                    else if (line.StartsWith("AppVersion "))           s.AppVersion           = Val(line);
                    else if (line.StartsWith("AppRevision "))          s.AppRevision          = Val(line);
                    else if (line.StartsWith("AppLang "))              s.AppLang              = Val(line);
                    else if (line.StartsWith("PackageName "))          s.PackageName          = Val(line);
                    else if (line.StartsWith("SourcePath "))           s.SourcePath           = Val(line);
                    else if (line.StartsWith("UninstallPath "))        s.UninstallPath        = Val(line);
                    else if (line.StartsWith("InstallArgs "))          s.InstallArgs          = Val(line);
                    else if (line.StartsWith("UninstallArgs "))        s.UninstallArgs        = Val(line);
                    else if (line.StartsWith("InstallCommand "))       s.InstallCommandLine   = Val(line);
                    else if (line.StartsWith("UninstallCommand "))     s.UninstallCommandLine = Val(line);
                    else if (line.StartsWith("CloseApplications "))    s.CloseApps            = Val(line);
                    else if (line.StartsWith("DeploymentType "))       s.DeploymentType       = Val(line);
                    else if (line.StartsWith("Description "))          s.Description          = Val(line);
                    else if (line.StartsWith("GroupName "))            s.GroupName            = Val(line);
                    else if (line.StartsWith("CreateIntunewin "))      s.CreateIntunewin      = Val(line);
                    else if (line.StartsWith("CreateDocument "))       s.CreateDocument       = Val(line);
                    else if (line.StartsWith("InstallBehaviour "))     s.InstallBehaviour     = Val(line);
                    else if (line.StartsWith("ProductCode "))          s.ProductCode          = Val(line);
                    else if (line.StartsWith("DetectionType "))
                    {
                        string dt = Val(line);
                        s.DetectionType = string.IsNullOrEmpty(s.DetectionType) || s.DetectionType == dt
                            ? dt : s.DetectionType + ", " + dt;
                    }
                    else if (line.StartsWith("Detection.MSI.ProductCode ")) s.ProductCode          = Val(line);
                    else if (line.StartsWith("Detection.MSI.Operator "))    s.DetectionMsiOperator = Val(line);
                    else if (line.StartsWith("Detection.MSI.Version "))     s.DetectionMsiVersion  = Val(line);
                    else if (line.StartsWith("Detection.File.Path "))       s.DetectionFilePath    = Val(line);
                    else if (line.StartsWith("Detection.File.Name "))       s.DetectionFileName    = Val(line);
                    else if (line.StartsWith("Detection.File.Version "))    s.DetectionFileVersion = Val(line);
                    else if (line.StartsWith("Detection.File.Method "))     s.DetectionFileMethod  = Val(line);
                    else if (line.StartsWith("Detection.File.Operator "))   s.DetectionFileOperator= Val(line);
                    else if (line.StartsWith("Detection.File.Operator:"))   s.DetectionFileOperator= Val(line.Replace("Operator:", "Operator :"));
                    else if (line.StartsWith("Detection.Reg.KeyPath "))     s.DetectionRegKey      = Val(line);
                    else if (line.StartsWith("Detection.Reg.ValueName "))   s.DetectionRegValue    = Val(line);
                    else if (line.StartsWith("Detection.Reg.ValueName:"))   s.DetectionRegValue    = Val(line.Replace("ValueName:", "ValueName :"));
                    else if (line.StartsWith("Detection.Reg.Method "))      s.DetectionRegMethod   = Val(line);
                    else if (line.StartsWith("Detection.Reg.Operator "))    s.DetectionRegOperator = Val(line);
                    else if (line.StartsWith("Detection.Reg.Version "))     s.DetectionRegVersion  = Val(line);
                }
                else
                {
                    if (Regex.IsMatch(line, @"Path1\s*:") && string.IsNullOrEmpty(s.PackageRootFolder))
                    { var m = Regex.Match(line, @"([A-Za-z]:\\.+)$"); if (m.Success) s.PackageRootFolder = m.Groups[1].Value.Trim(); }

                    if (Regex.IsMatch(line, @"EXE src\b", RegexOptions.IgnoreCase) && string.IsNullOrEmpty(s.SourcePath))
                    { var m = Regex.Match(line, @"([A-Za-z]:\\.+)$"); if (m.Success) s.SourcePath = m.Groups[1].Value.Trim(); }

                    if (Regex.IsMatch(line, @"^\s*EXE Install:\s+", RegexOptions.IgnoreCase) && string.IsNullOrEmpty(s.InstallExeName))
                    { var m = Regex.Match(line, @"EXE Install:\s+(.+)$", RegexOptions.IgnoreCase); if (m.Success) s.InstallExeName = m.Groups[1].Value.Trim(); }

                    if (Regex.IsMatch(line, @"EXE Uninstall", RegexOptions.IgnoreCase) && string.IsNullOrEmpty(s.UninstallPath))
                    { var m = Regex.Match(line, @"EXE Uninstall[^:]*:\s+(.+)$", RegexOptions.IgnoreCase); if (m.Success) s.UninstallPath = m.Groups[1].Value.Trim(); }

                    if (Regex.IsMatch(line, @"EXE CloseApps:\s+", RegexOptions.IgnoreCase) && string.IsNullOrEmpty(s.CloseApps))
                    { var m = Regex.Match(line, @"EXE CloseApps:\s+(.+)$", RegexOptions.IgnoreCase); if (m.Success) s.CloseApps = m.Groups[1].Value.Trim(); }

                    // Note: IntunewinPath is intentionally NOT parsed from the log.
                    // It is set exclusively by OnIntunewinCreated → _lastIntunewinPath to avoid stale data.

                    if (line.Contains("Copying source") && string.IsNullOrEmpty(s.SourceMediaLocation))
                    { var m = Regex.Match(line, @"([A-Za-z]:\\.+\\Files)", RegexOptions.IgnoreCase);
                      if (m.Success) s.SourceMediaLocation = Path.GetDirectoryName(m.Groups[1].Value.Trim()) ?? string.Empty; }
                }
            }
        }
    }
}

// ── CreateDocumentSession DTO ─────────────────────────────────────────────────
namespace SwiftPack.Views
{
    public class CreateDocumentSession
    {
        // ── Package Information ───────────────────────────────────────────────
        public string PackageName            { get; set; } = string.Empty;
        public string AppName                { get; set; } = string.Empty;
        public string AppVendor              { get; set; } = string.Empty;
        public string AppVersion             { get; set; } = string.Empty;
        public string PackageRootFolder      { get; set; } = string.Empty;
        public string SourceMediaLocation    { get; set; } = string.Empty; // Package22 folder
        public string PackageCreatedBy       { get; set; } = string.Empty;
        public string PackageValidatedBy     { get; set; } = string.Empty;
        public string CloseApps              { get; set; } = string.Empty;
        public string Shortcuts              { get; set; } = string.Empty;
        public string InstallLogPath         { get; set; } = string.Empty;
        public string UninstallLogPath       { get; set; } = string.Empty;

        // ── Request Details ───────────────────────────────────────────────────
        public string RequestNumber          { get; set; } = string.Empty;
        public string SourcePath             { get; set; } = string.Empty;
        public string RequestorName          { get; set; } = string.Empty;
        public string RequestorEmail         { get; set; } = string.Empty;
        public string AppOwnerName           { get; set; } = string.Empty;
        public string AppOwnerEmail          { get; set; } = string.Empty;
        public string DepartmentName         { get; set; } = string.Empty;
        public string Country                { get; set; } = string.Empty;

        // ── Deployment Configuration ──────────────────────────────────────────
        public string IntunewinFileName      { get; set; } = string.Empty;
        public string IntunewinFileSize      { get; set; } = string.Empty;
        public string IntunewinPath          { get; set; } = string.Empty;
        public string Publisher              { get; set; } = string.Empty;
        public string CategoryName           { get; set; } = string.Empty;
        public string Description            { get; set; } = string.Empty;
        public string InstallCommandLine     { get; set; } = string.Empty;
        public string UninstallCommandLine   { get; set; } = string.Empty;
        public string InstallBehaviour       { get; set; } = string.Empty;
        public string OperatingSystem        { get; set; } = string.Empty;
        public string ScriptDate             { get; set; } = string.Empty;
        // Detection
        public string DetectionType          { get; set; } = string.Empty;
        public string ProductCode            { get; set; } = string.Empty;
	public string DetectionSummary       	     { get; set; } = string.Empty; // live MSI/File/Registry block from the Publish Package page
        public string DetectionMsiOperator   { get; set; } = string.Empty;
        public string DetectionMsiVersion    { get; set; } = string.Empty;
        public string DetectionFilePath      { get; set; } = string.Empty;
        public string DetectionFileName      { get; set; } = string.Empty;
        public string DetectionFileVersion   { get; set; } = string.Empty;
        public string DetectionFileMethod    { get; set; } = string.Empty;
        public string DetectionFileOperator  { get; set; } = string.Empty;
        public string DetectionRegKey        { get; set; } = string.Empty;
        public string DetectionRegValue      { get; set; } = string.Empty;
        public string DetectionRegMethod     { get; set; } = string.Empty;
        public string DetectionRegOperator   { get; set; } = string.Empty;
        public string DetectionRegVersion    { get; set; } = string.Empty;
        public string Dependencies           { get; set; } = string.Empty;
        public string Supersedence           { get; set; } = string.Empty;
        public string DeploymentType         { get; set; } = string.Empty;
        public string AssignmentGroup        { get; set; } = string.Empty;
        public string UatUsers               { get; set; } = string.Empty;
        public string RollbackPlan           { get; set; } = string.Empty;

        // ── Other Details ─────────────────────────────────────────────────────
        public string KnownIssues            { get; set; } = string.Empty;
        public string AdditionalInformation  { get; set; } = string.Empty;

        // ── Internal (not shown in doc) ───────────────────────────────────────
        public string AppLang                { get; set; } = string.Empty;
        public string AppRevision            { get; set; } = string.Empty;
        public string InstallExeName         { get; set; } = string.Empty;
        public string UninstallPath          { get; set; } = string.Empty;
        public string InstallArgs            { get; set; } = string.Empty;
        public string UninstallArgs          { get; set; } = string.Empty;
        public string CreateIntunewin        { get; set; } = string.Empty;
        public string CreateDocument         { get; set; } = string.Empty;
        public string GroupName              { get; set; } = string.Empty;
    }
}
