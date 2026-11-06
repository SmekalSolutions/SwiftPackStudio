using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.Management;

namespace SwiftPack.Views
{
    public partial class ExecutePackagePage : Page
    {
        private Process?           _runningProcess;
        private Action?            _onAfterDeploy = null; // fired after any deployment (Install/Uninstall/Repair) completes
        private CancellationTokenSource? _cts;
        private string?            _lastScriptPath;   // path set from CreatePackagePage
        public  string?            LastScriptPath => _lastScriptPath; // exposed for BuildDocumentSession
        private string?            _logFilePath;
        private DateTime           _deployStartTime;
        private string?            _activePs1Path;    // ps1 currently executing (for cancel kill)
        private string?            _activePidFile;    // temp file containing inner process PID
        private string             _activeDeployMode = ""; // "Install", "Uninstall", "Repair"
        private ExecutePackageConfig _execCfg = new();   // loaded from ExecutePackage.json
        private string? _execCfgPath;                    // resolved ExecutePackage.json actually loaded, null when none

        // ── Event-log buffers: one per source, so switching tabs shows each run's full output ──
        // A run used to clear the single TextBox, so starting an Uninstall wiped the Install log.
        // Every write now goes to the buffer for the current deploy mode (Repair folds into
        // Install), and TxtEventLog shows whichever tab is selected. Install / Uninstall tabs by
        // the EVENT LOG title let the user read either afterwards.
        private readonly System.Text.StringBuilder _installLogBuf   = new();
        private readonly System.Text.StringBuilder _uninstallLogBuf = new();
        private const string ReadyLogText =
            "[Ready] Select a script file and click a Install / Uninstall / Repair button\u2026";
        private string _shownLogMode = "Install";   // which buffer TxtEventLog displays
        private string _logWriteMode = "Install";   // which buffer new writes go to (set per run)
        // No tab is highlighted until a deploy runs (Install/Uninstall/Repair) or the user clicks a
        // tab. Until then both tabs sit un-selected, matching a fresh, idle Event Log.
        private bool _logTabActivated = false;

        private static string LogBucketFor(string deployMode) =>
            string.Equals(deployMode, "Uninstall", StringComparison.OrdinalIgnoreCase) ? "Uninstall" : "Install";
        private System.Text.StringBuilder BufFor(string mode) =>
            string.Equals(mode, "Uninstall", StringComparison.OrdinalIgnoreCase) ? _uninstallLogBuf : _installLogBuf;

        // Tracks the last successful log paths - exposed for CreateDocument auto-fill
        public string? LastInstallLogPath   { get; internal set; }
        public string? LastUninstallLogPath { get; internal set; }

        /// <summary>
        /// Fired after each successful deployment. Parameters: (deployMode, psadtLogPath, scriptPath).
        /// MainWindow subscribes to trigger CreateDocument auto-fill.
        /// </summary>
        public Action<string, string?, string?>? OnDeployCompleted { get; set; }

        // ── Called by MainWindow when navigating here from Create Package ──
        public void SetScriptPath(string ps1Path)
        {
            if (_lastScriptPath != ps1Path)
            {
                OnScriptPathChanged?.Invoke(ps1Path);
                // Clear stale logs - they belong to the previous package
                LastInstallLogPath   = null;
                LastUninstallLogPath = null;
            }
            _lastScriptPath = ps1Path;
            // Show the expanded PSADT commands (from Settings) in the Install / Uninstall boxes.
            PopulatePsadtCommandBoxes();
        }

        /// <summary>Fired when the script path changes to a new package - used to clear stale intunewin data.</summary>
        public Action<string>? OnScriptPathChanged { get; set; }

        // True only while an auto-package chain is programmatically triggering a deploy
        // (SetScriptAndInstall/Uninstall). A real user button click leaves it false, which
        // tells LaunchDeployment to clear any stale per-run RuntimeCopyLogsOverride so the
        // manual deploy honors ExecutePackage.json instead of a value left by a prior chain.
        private bool _autoTriggeredDeploy = false;

        /// <summary>
        /// Sets the script path and programmatically clicks the Admin Install button.
        /// After Install completes, fires onAfterInstall (if provided).
        /// </summary>
        public void SetScriptAndInstall(string ps1Path, Action? onAfterInstall = null)
        {
            if (string.IsNullOrWhiteSpace(ps1Path) || !System.IO.File.Exists(ps1Path)) return;
            Dispatcher.Invoke(() =>
            {
                SetScriptPath(ps1Path);
                _onAfterDeploy = onAfterInstall; // stored before click so it's ready when deploy finishes
                var t = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(300) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    if (BtnAdminInstall != null && BtnAdminInstall.IsEnabled)
                    {
                        _autoTriggeredDeploy = true; // chain-triggered: keep the chain's override
                        BtnAdminInstall.RaiseEvent(
                            new System.Windows.RoutedEventArgs(Button.ClickEvent));
                    }
                };
                t.Start();
            });
        }

        /// <summary>
        /// Sets the script path, clicks Admin Install, then automatically clicks
        /// Admin Uninstall after Install completes.
        /// </summary>
        public void SetScriptAndInstallThenUninstall(string ps1Path, Action? onAfterUninstall = null)
        {
            SetScriptAndInstall(ps1Path, onAfterInstall: () =>
            {
                // Install done - now trigger Uninstall with same script
                SetScriptAndUninstall(ps1Path, onAfterUninstall: onAfterUninstall);
            });
        }

        /// <summary>
        /// Sets the script path and programmatically clicks the Admin Uninstall button.
        /// onAfterUninstall fires after uninstall completes (optional).
        /// </summary>
        public void SetScriptAndUninstall(string ps1Path, Action? onAfterUninstall = null)
        {
            if (string.IsNullOrWhiteSpace(ps1Path) || !System.IO.File.Exists(ps1Path)) return;
            Dispatcher.Invoke(() =>
            {
                SetScriptPath(ps1Path);
                _onAfterDeploy = onAfterUninstall;
                var t = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(300) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    if (BtnAdminUninstall != null && BtnAdminUninstall.IsEnabled)
                    {
                        _autoTriggeredDeploy = true; // chain-triggered: keep the chain's override
                        BtnAdminUninstall.RaiseEvent(
                            new System.Windows.RoutedEventArgs(Button.ClickEvent));
                    }
                };
                t.Start();
            });
        }

        public ExecutePackagePage()
        {
            InitializeComponent();
            // Default selections are set in XAML via IsChecked="True"
            LoadExecConfig();

            Loaded += (_, _) =>
            {
                AttachCaretHandlerToAllTextBoxes(this);
                this.PreviewMouseLeftButtonDown += Page_PreviewMouseLeftButtonDown;
                ApplyLogTabStyles();   // start with neither tab highlighted (until a deploy runs)

                // If SetScriptPath was called before the page was loaded into the frame
                // (TxtScriptPath was null at the time), apply the stored path now.
                if (!string.IsNullOrEmpty(_lastScriptPath) &&
                    TxtScriptPath != null &&
                    string.IsNullOrWhiteSpace(TxtScriptPath.Text))
                {
                    PopulatePsadtCommandBoxes();
                }
            };
        }

        /// <summary>
        /// Page-level handler: when the user clicks anywhere on the page, find any
        /// TextBox that currently has a selection and clear it - unless the click
        /// landed inside that same TextBox.
        /// </summary>
        private void Page_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.FocusedElement is not TextBox focused) return;
            if (focused.SelectionLength == 0) return;

            var clickPos = e.GetPosition(focused);
            bool clickedInsideFocused =
                clickPos.X >= 0 && clickPos.X <= focused.ActualWidth &&
                clickPos.Y >= 0 && clickPos.Y <= focused.ActualHeight;

            if (!clickedInsideFocused)
                focused.SelectionLength = 0;
        }

        /// <summary>
        /// Walks the visual tree and attaches the smart-caret handler
        /// and a custom context menu to every TextBox on the page.
        /// </summary>
        private void AttachCaretHandlerToAllTextBoxes(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox tb)
                {
                    tb.PreviewMouseLeftButtonDown += PathBox_PreviewMouseLeftButtonDown;
                    tb.ContextMenu = BuildTextBoxContextMenu(tb);
                }
                else
                    AttachCaretHandlerToAllTextBoxes(child);
            }
        }

        /// <summary>
        /// Smart caret: click in empty left space → caret at start;
        /// click in empty right space → caret at end; drag → select.
        /// </summary>
        private void PathBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBox tb || string.IsNullOrEmpty(tb.Text)) return;
            var clickPoint = e.GetPosition(tb);
            int exactIndex = tb.GetCharacterIndexFromPoint(clickPoint, snapToText: false);
            if (exactIndex != -1) return; // clicked on actual text - WPF handles normally

            int nearestIndex = tb.GetCharacterIndexFromPoint(clickPoint, snapToText: true);
            bool goToStart   = nearestIndex < tb.Text.Length / 2;

            e.Handled = true;
            tb.CaptureMouse();
            if (!tb.IsFocused) tb.Focus();
            int anchor = goToStart ? 0 : tb.Text.Length;
            tb.Select(anchor, 0);
            if (anchor == 0) tb.ScrollToHome(); else tb.ScrollToEnd();

            MouseEventHandler?       moveHandler = null;
            MouseButtonEventHandler? upHandler   = null;

            moveHandler = (s, me) =>
            {
                if (s is not TextBox t) return;
                var pos       = me.GetPosition(t);
                int dragIndex = t.GetCharacterIndexFromPoint(pos, snapToText: true);
                var lastR     = t.GetRectFromCharacterIndex(t.Text.Length);
                if (pos.X >= lastR.Left) dragIndex = t.Text.Length;
                var firstR    = t.GetRectFromCharacterIndex(0);
                if (pos.X <= firstR.Left) dragIndex = 0;
                t.Select(Math.Min(anchor, dragIndex), Math.Abs(dragIndex - anchor));
                me.Handled = true;
            };

            upHandler = (s, me) =>
            {
                if (s is not TextBox t) return;
                t.ReleaseMouseCapture();
                t.PreviewMouseMove         -= moveHandler;
                t.PreviewMouseLeftButtonUp -= upHandler;
                me.Handled = true;
            };

            tb.PreviewMouseMove         += moveHandler;
            tb.PreviewMouseLeftButtonUp += upHandler;
        }

        /// <summary>
        /// Builds a themed context menu with Cut/Copy/Paste/Select All/Delete/Undo/Redo.
        /// </summary>
        private ContextMenu BuildTextBoxContextMenu(TextBox tb)
        {
            var menu = new ContextMenu();
            if (TryFindResource("TxtBoxContextMenuStyle") is Style ctxStyle)
                menu.Style = ctxStyle;

            Style? itemStyle = TryFindResource("TxtBoxMenuItemStyle") as Style;

            MenuItem MakeItem(string header, string icon, string gesture, Action action, Func<bool> canExecute)
            {
                var item = new MenuItem { Header = $"  {icon}  {header}", InputGestureText = gesture };
                if (itemStyle != null) item.Style = itemStyle;
                item.Click  += (_, _) => { if (canExecute()) action(); };
                menu.Opened += (_, _) => item.IsEnabled = canExecute();
                return item;
            }

            Separator MakeSep() => new Separator { Margin = new Thickness(0, 2, 0, 2) };

            menu.Items.Add(MakeItem("Cut",        "✂",  "Ctrl+X",
                () => tb.Cut(),
                () => !tb.IsReadOnly && tb.SelectionLength > 0));
            menu.Items.Add(MakeItem("Copy",       "📋", "Ctrl+C",
                () => tb.Copy(),
                () => tb.SelectionLength > 0));
            menu.Items.Add(MakeItem("Paste",      "📄", "Ctrl+V",
                () => tb.Paste(),
                () => !tb.IsReadOnly && Clipboard.ContainsText()));
            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("Select All", "⬛", "Ctrl+A",
                () => tb.SelectAll(),
                () => !string.IsNullOrEmpty(tb.Text)));
            menu.Items.Add(MakeItem("Delete",     "🗑",  "Del",
                () =>
                {
                    if (tb.SelectionLength > 0) tb.SelectedText = "";
                    else if (tb.CaretIndex < tb.Text.Length)
                        tb.Text = tb.Text.Remove(tb.CaretIndex, 1);
                },
                () => !tb.IsReadOnly && (tb.SelectionLength > 0 || tb.CaretIndex < tb.Text.Length)));
            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("Undo",       "↩",  "Ctrl+Z",
                () => tb.Undo(),
                () => !tb.IsReadOnly && tb.CanUndo));
            menu.Items.Add(MakeItem("Redo",       "↪",  "Ctrl+Y",
                () => tb.Redo(),
                () => !tb.IsReadOnly && tb.CanRedo));

            return menu;
        }

        // ══════════════════════════════════════════════════════════════
        // SIX DIRECT DEPLOYMENT BUTTON HANDLERS
        // ══════════════════════════════════════════════════════════════
        // Manual clicks must always show the result window. The auto flow suppresses
        // it via GlobalShowMessageBox = false, and an auto chain that aborted mid-way
        // (e.g. publish failed) can leave that suppression behind - which made manual
        // installs close their result window instantly. Clear it before launching.
        private void ManualDeploy(string action, bool systemContext)
        {
            SwiftPack.Views.SourceDownloadPage.GlobalShowMessageBox = true;
            LaunchDeployment(action, systemContext: systemContext);
        }
        private void BtnAdminInstall_Click(object sender, RoutedEventArgs e)
            => ManualDeploy("Install", systemContext: false);
        private void BtnAdminUninstall_Click(object sender, RoutedEventArgs e)
            => ManualDeploy("Uninstall", systemContext: false);
        private void BtnAdminRepair_Click(object sender, RoutedEventArgs e)
            => ManualDeploy("Repair", systemContext: false);
        private void BtnSystemInstall_Click(object sender, RoutedEventArgs e)
            => ManualDeploy("Install", systemContext: true);
        private void BtnSystemUninstall_Click(object sender, RoutedEventArgs e)
            => ManualDeploy("Uninstall", systemContext: true);
        private void BtnSystemRepair_Click(object sender, RoutedEventArgs e)
            => ManualDeploy("Repair", systemContext: true);

        private void SetAllDeployButtonsEnabled(bool enabled)
        {
            // This toggle IS the page's busy state (called false when a deployment starts,
            // true when it ends), so it is also where Auto Package learns to stand down.
            AppBusy.SetManual("Execute Package", !enabled);
            BtnAdminInstall.IsEnabled    = enabled;
            BtnAdminUninstall.IsEnabled  = enabled;
            BtnAdminRepair.IsEnabled     = enabled;
            BtnSystemInstall.IsEnabled   = enabled;
            BtnSystemUninstall.IsEnabled = enabled;
            BtnSystemRepair.IsEnabled    = enabled;
            BtnBrowse.IsEnabled          = enabled;
            BtnClearPath.IsEnabled       = enabled;
        }

        /// <summary>Button that triggers the given deployment mode in the given context.</summary>
        private System.Windows.Controls.Button? DeployButtonFor(string mode, bool systemContext) => mode switch
        {
            "Install"   => systemContext ? BtnSystemInstall   : BtnAdminInstall,
            "Uninstall" => systemContext ? BtnSystemUninstall : BtnAdminUninstall,
            "Repair"    => systemContext ? BtnSystemRepair    : BtnAdminRepair,
            _           => null
        };

        /// <summary>Removes the running-action highlight from all six deployment buttons.</summary>
        private void ClearDeployButtonHighlights()
        {
            foreach (var b in new[] { BtnAdminInstall, BtnAdminUninstall, BtnAdminRepair,
                                      BtnSystemInstall, BtnSystemUninstall, BtnSystemRepair })
                if (b != null) b.Tag = null;
        }

        private static SolidColorBrush TC(string key)
        {
            var res = Application.Current.TryFindResource(key);
            return res is SolidColorBrush b ? b : new SolidColorBrush(Colors.Gray);
        }

        // ══════════════════════════════════════════════════════════════
        // SCRIPT PATH
        // ══════════════════════════════════════════════════════════════
        private void BrowseScript_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog {
                Title  = "Select Invoke-AppDeployToolkit.ps1",
                Filter = "PowerShell scripts (*.ps1)|*.ps1|All Files|*.*",
                InitialDirectory = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\"
            };
            if (dlg.ShowDialog() == true) SetScriptPath(dlg.FileName);
        }

        private void ScriptPath_DragOver(object sender, DragEventArgs e)
        { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }

        private void ScriptPath_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0 && File.Exists(files[0])) SetScriptPath(files[0]);
        }

        private void TxtScriptPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtScriptPath.Text.Trim().Length > 4) TxtScriptPath.ScrollToEnd();
        }

        private void BtnClearPath_Click(object sender, RoutedEventArgs e)
        {
            _lastScriptPath = null;
            TxtScriptPath.Text = string.Empty;
            if (TxtUninstallPath != null) TxtUninstallPath.Text = string.Empty;
            TxtScriptPath.Focus();
            _installLogBuf.Clear();
            _uninstallLogBuf.Clear();
            _logWriteMode = "Install";
            // New/reset returns the Event Log to its fresh, idle look: show the Install buffer
            // (empty → Ready text) but with neither tab highlighted, as on first load.
            _shownLogMode = "Install";
            _logTabActivated = false;
            if (TxtEventLog != null) TxtEventLog.Text = ReadyLogText;
            ApplyLogTabStyles();
            ClearDeployButtonHighlights();   // no action selected after New, same as the Event Log tabs
        }

        // ══════════════════════════════════════════════════════════════
        // LOG HELPERS
        // ══════════════════════════════════════════════════════════════
        private void Log(string msg, bool timestamp = true)
        {
            string line = timestamp ? $"[{DateTime.Now:HH:mm:ss}] {msg}" : msg;
            AppendLog("\n" + line);
        }

        // Single funnel for every log write: append to the CURRENT write buffer, and to the on-screen
        // TextBox only when that buffer's tab is the one showing. Keeps tab switching correct.
        private void AppendLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string mode = _logWriteMode;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BufFor(mode).Append(text);
                if (TxtEventLog != null && string.Equals(mode, _shownLogMode, StringComparison.OrdinalIgnoreCase))
                {
                    TxtEventLog.AppendText(text);
                    TxtEventLog.ScrollToEnd();
                    LogScrollViewer?.ScrollToEnd();
                }
            }));
        }

        // Show a buffer in the TextBox and mark its tab active. Empty buffer shows the Ready hint.
        private void SelectLogTab(string mode)
        {
            mode = string.Equals(mode, "Uninstall", StringComparison.OrdinalIgnoreCase) ? "Uninstall" : "Install";
            _shownLogMode = mode;
            _logTabActivated = true;   // a deploy ran or the user picked a tab - now show the highlight
            if (TxtEventLog != null)
            {
                string body = BufFor(mode).ToString();
                TxtEventLog.Text = body.Length == 0 ? ReadyLogText : body.TrimStart('\n');
                TxtEventLog.ScrollToEnd();
                LogScrollViewer?.ScrollToEnd();
            }
            ApplyLogTabStyles();
        }

        private void ApplyLogTabStyles()
        {
            // Until the first deploy (or a manual tab click), neither tab is highlighted.
            if (!_logTabActivated)
            {
                if (BtnLogTabInstall   != null) BtnLogTabInstall.Tag   = null;
                if (BtnLogTabUninstall != null) BtnLogTabUninstall.Tag = null;
                return;
            }
            bool showInstall = string.Equals(_shownLogMode, "Install", StringComparison.OrdinalIgnoreCase);
            if (BtnLogTabInstall   != null) BtnLogTabInstall.Tag   = showInstall ? "Active" : null;
            if (BtnLogTabUninstall != null) BtnLogTabUninstall.Tag = showInstall ? null : "Active";
        }

        private void BtnLogTabInstall_Click(object sender, RoutedEventArgs e)   => SelectLogTab("Install");
        private void BtnLogTabUninstall_Click(object sender, RoutedEventArgs e) => SelectLogTab("Uninstall");

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            BufFor(_shownLogMode).Clear();
            if (TxtEventLog != null) TxtEventLog.Text = ReadyLogText;
        }

        // ══════════════════════════════════════════════════════════════
        // LAUNCH DEPLOYMENT - called by all 6 buttons
        // ══════════════════════════════════════════════════════════════
        // ── PSADT command auto-population (from ExecutePackage.json) ──
        // Replaces the bare Invoke-AppDeployToolkit.exe / .ps1 name in a Settings command template
        // with the full quoted path under the resolved package's Package folder, leaving any prefix
        // (e.g. Powershell.exe -File) and trailing args untouched.
        private static string ExpandPsadtCommand(string? template, string scriptDir)
        {
            string t = (template ?? "").Trim();
            if (t.Length == 0) return "";
            return Regex.Replace(
                t,
                "\"?(Invoke-AppDeployToolkit\\.(?:exe|ps1))\"?",
                m => "\"" + Path.Combine(scriptDir, m.Groups[1].Value) + "\"",
                RegexOptions.IgnoreCase);
        }

        // Fills the PSADT Install / PSADT Uninstall boxes from the Settings command templates,
        // expanded against the current script's folder. Called whenever the script path is resolved
        // or the Settings commands change.
        private void PopulatePsadtCommandBoxes()
        {
            if (TxtScriptPath == null) return;
            string path = (_lastScriptPath ?? "").Trim();
            if (path.Length == 0) return;   // nothing resolved yet - leave the boxes untouched
            string scriptDir = Path.GetDirectoryName(path) ?? "";

            string inst = ExpandPsadtCommand(_execCfg?.PSADTInstallCommand,   scriptDir);
            string unin = ExpandPsadtCommand(_execCfg?.PSADTUninstallCommand, scriptDir);

            // Install box shows the expanded install command; empty when no install template is
            // configured, so LaunchDeployment falls back to the classic script + mode path.
            TxtScriptPath.Text = inst;
            if (TxtUninstallPath != null)
                TxtUninstallPath.Text = unin;
        }

        private async void LaunchDeployment(string deployMode, bool systemContext)
        {
            // The Install box now shows the expanded PSADT command, so the actual script file is
            // taken from _lastScriptPath (set when the package/script was resolved). Fall back to a
            // path typed directly into the box when nothing was resolved.
            string ps1 = (_lastScriptPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ps1) || !File.Exists(ps1))
            {
                string boxed = TxtScriptPath.Text.Trim();
                if (File.Exists(boxed)) ps1 = boxed;
            }
            if (string.IsNullOrWhiteSpace(ps1) || !File.Exists(ps1))
            {
                ExecStyledMessage(Window.GetWindow(this), "Script Required", "Please select a valid Invoke-AppDeployToolkit.ps1 file.", "⚠");
                return;
            }

            string scriptDir = Path.GetDirectoryName(ps1) ?? ".";
            _logFilePath     = ReadLogPathFromConfig(scriptDir);

            // Re-read ExecutePackage.json for THIS run. The cached copy is loaded in the ctor, so
            // any edit made to the file after the app started (Notepad, an upgrade that rewrites
            // Templates\ExecutePackage.json) would otherwise be ignored while the Settings dialog,
            // which always reads the file fresh, showed the new value.
            LoadExecConfig();

            // A manual deploy (real button click) must honor ExecutePackage.json, not a stale
            // per-run RuntimeCopyLogsOverride left behind by a prior auto-package chain. Chain-
            // triggered deploys (via SetScriptAndInstall/Uninstall) set _autoTriggeredDeploy and
            // keep the override the chain intentionally applied.
            bool autoTriggered = _autoTriggeredDeploy;
            _autoTriggeredDeploy = false;
            if (!autoTriggered)
                ExecutePackageConfig.RuntimeCopyLogsOverride = null;

            _deployStartTime = DateTime.Now;
            // Route this run's output to its own buffer and show that tab. Clear only this buffer so
            // the other mode's log (e.g. a prior Install) stays intact and viewable via its tab.
            _logWriteMode = LogBucketFor(deployMode);
            BufFor(_logWriteMode).Clear();
            SelectLogTab(_logWriteMode);
            Log($"Script  : {ps1}");
            Log($"Mode    : {deployMode}");
            Log($"Context : {(systemContext ? "System (PsExec)" : "Administrator")}");
            Log($"Log dir : {_logFilePath}");
            Log("─────────────────────────────────────────");

            TxtExecRunning.Text          = deployMode + " in progress. Please wait.";
            BorderExecRunning.Visibility = Visibility.Visible;

            SetAllDeployButtonsEnabled(false);
            // Highlight the button whose action is running (Admin/System × Install/Uninstall/Repair).
            ClearDeployButtonHighlights();
            var activeBtn = DeployButtonFor(deployMode, systemContext);
            if (activeBtn != null) activeBtn.Tag = "Active";
            _activeDeployMode = deployMode;
            BtnCancelHeader.IsEnabled    = true;
                BtnCancelHeader.IsEnabled    = true;
            TxtExecStatus.Visibility     = Visibility.Collapsed;
            PanelExecProgress.Visibility = Visibility.Visible;
            TxtExecProgressLabel.Text    = $"Running {deployMode}…";
            // Show progress bar in the relevant context box header
            if (systemContext)
                PrgSystemProgress.Visibility = Visibility.Visible;
            else
                PrgAdminProgress.Visibility  = Visibility.Visible;

            _cts = new CancellationTokenSource();

            // Requirement 5: run the command shown in the PSADT Install / PSADT Uninstall box (both
            // Administrator and System context). Only when a template is configured; with no template
            // the classic script + mode launch inside RunScript is used unchanged.
            // Run whatever command the relevant box shows (Install box for Install, Uninstall box
            // for Uninstall). Repair has no box, so its command comes straight from Settings,
            // expanded against the package folder. An empty command falls back to the classic path.
            string? psadtCommand =
                deployMode == "Install"   ? TxtScriptPath?.Text?.Trim() :
                deployMode == "Uninstall" ? TxtUninstallPath?.Text?.Trim() :
                deployMode == "Repair"    ? ExpandPsadtCommand(_execCfg?.PSADTRepairCommand, scriptDir) : null;
            if (string.IsNullOrWhiteSpace(psadtCommand)) psadtCommand = null;

            try
            {
                await Task.Run(() => RunScript(ps1, deployMode, systemContext, psadtCommand, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                Log("⚠  Execution cancelled by user.");
            }
            catch (Exception ex)
            {
                Log($"❌ Error: {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    BorderExecRunning.Visibility = Visibility.Collapsed;
                    SetAllDeployButtonsEnabled(true);
                    // Do NOT clear the highlight here: the button that just ran stays highlighted so
                    // it matches the Event Log tab (both show the last action). It is cleared only
                    // when the NEXT run starts (ClearDeployButtonHighlights above) or on New/Clear.
                    BtnCancelHeader.IsEnabled    = false;
                    PanelExecProgress.Visibility = Visibility.Collapsed;
                    TxtExecStatus.Visibility     = Visibility.Visible;
                    TxtExecStatus.Text           = "* Script path required";
                    PrgAdminProgress.Visibility  = Visibility.Collapsed;
                    PrgSystemProgress.Visibility = Visibility.Collapsed;
                });
                _runningProcess = null;
                _cts?.Dispose(); _cts = null;
            }
        }
        /// <summary>
        /// Loads ExecutePackage.json from the Templates folder (installed or dev location).
        /// Falls back silently - all fields have safe defaults.
        /// Called on page load AND at the start of every deploy, so an edit made to the file
        /// outside the Settings dialog (Notepad, a reinstall overwriting Templates\) is honoured
        /// without restarting the app. Previously this ran once in the constructor, so the
        /// Settings dialog (which re-reads the file every time it opens) could show "Yes" while
        /// the deploy still used a stale cached "No".
        /// </summary>
        private void LoadExecConfig()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ExecutePackage.json",
                System.IO.Path.Combine(exeDir, "Templates", "ExecutePackage.json"),
                System.IO.Path.Combine(exeDir, "ExecutePackage.json"),
            };
            foreach (var path in candidates)
            {
                // Per-candidate try/catch: one unreadable or malformed file must not abort the
                // whole search and leave _execCfg silently at its all-null default (= No).
                try
                {
                    if (!File.Exists(path)) continue;
                    var opts = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        // Match the Settings dialog's JsonDocumentOptions exactly, so the file the
                        // dialog can read is always a file the deploy can read.
                        AllowTrailingCommas = true,
                        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    };
                    var cfg = System.Text.Json.JsonSerializer.Deserialize<ExecutePackageConfig>(
                        File.ReadAllText(path), opts);
                    if (cfg != null) { _execCfg = cfg; _execCfgPath = path; return; }
                }
                catch { }
            }
        }

        // ── Settings: edit ExecutePackage.json ─────────────────────────────
        // Modal editor over ExecutePackage.json. Shows each Yes/No option as a radio pair,
        // lets the user toggle them, and writes the changes back via a JsonNode round-trip.
        // Mirrors the Auto Package / Create Package Settings dialogs.
        private void BtnExecSettings_Click(object sender, RoutedEventArgs e) => OpenExecutePackageSettings();

        private void OpenExecutePackageSettings()
        {
            // JSON keys for the two free-text PSADT command templates. Kept out of the Yes/No
            // "Flags" loop below so they render as text boxes, not radio pairs.
            const string PsadtInstallKey   = "PSADTInstallCommand";
            const string PsadtUninstallKey = "PSADTUninstallCommand";
            const string PsadtRepairKey    = "PSADTRepairCommand";

            // Same resolution as LoadExecConfig: Templates folder first, then dev locations.
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ExecutePackage.json",
                Path.Combine(exeDir, "Templates", "ExecutePackage.json"),
                Path.Combine(exeDir, "ExecutePackage.json"),
            };
            string cfgPath = candidates.FirstOrDefault(File.Exists) ?? candidates[0];

            System.Text.Json.Nodes.JsonObject root;
            try
            {
                var docOpts = new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip };
                root = (File.Exists(cfgPath)
                    ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath), null, docOpts)?.AsObject()
                    : null) ?? new System.Text.Json.Nodes.JsonObject();
            }
            catch
            {
                ExecStyledMessage(null, "Execute Package - Settings",
                    "ExecutePackage.json is not valid JSON. Fix the file, then try again.\n\n" + cfgPath, "⚠");
                return;
            }

            var winBg      = Application.Current.TryFindResource("WindowBgBrush")      as Brush ?? Brushes.White;
            var borderClr  = Application.Current.TryFindResource("BorderMainBrush")    as Brush ?? Brushes.LightGray;
            var boxBg      = Application.Current.TryFindResource("InputBgBrush")       as Brush ?? new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr     = Application.Current.TryFindResource("BorderMainBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg      = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var valFg      = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? Brushes.Black;
            var sec        = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));

            var cwin = new Window
            {
                Width = 520, SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 48),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false
            };
            var outer = new Border { Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black } };
            cwin.Content = outer;
            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            var tb = new Border { Background = titleBarBg, CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(16, 6, 12, 6) };
            tb.MouseLeftButtonDown += (_, _) => cwin.DragMove();
            Grid.SetRow(tb, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti = new TextBlock { Text = "⚙", FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt = new TextBlock { Text = "Execute Package - Settings", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button { Content = "✕", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => cwin.Close();
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg; og.Children.Add(tb);

            var form = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form };
            Grid.SetRow(scroll, 1); og.Children.Add(scroll);

            string ReadStr(System.Text.Json.Nodes.JsonNode? n)
            {
                if (n == null) return "";
                try { return n.GetValue<string>(); } catch { }
                try { return n.ToJsonString().Trim('"'); } catch { }
                return "";
            }

            form.Children.Add(new TextBlock { Text = "Flags", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = valFg, Margin = new Thickness(0, 0, 0, 2) });
            form.Children.Add(new TextBlock { Text = "Yes / No options read from ExecutePackage.json.", FontSize = 10.5, Foreground = lblFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

            var editors = new System.Collections.Generic.Dictionary<string, RadioButton>();
            foreach (var kv in root)
            {
                string key = kv.Key;
                if (key == PsadtInstallKey || key == PsadtUninstallKey || key == PsadtRepairKey) continue;   // rendered as text below
                string cur = ReadStr(kv.Value);
                bool isYes = string.Equals(cur.Trim(), "Yes",  StringComparison.OrdinalIgnoreCase)
                          || string.Equals(cur.Trim(), "true", StringComparison.OrdinalIgnoreCase);

                var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 6), MinHeight = 30 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = new TextBlock { Text = Regex.Replace(key, "(?<=[a-z0-9])(?=[A-Z])", " "), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lbl, 0); rowGrid.Children.Add(lbl);
                var rbY = new RadioButton { Content = "Yes", FontSize = 11, IsChecked = isYes,  Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Cursor = Cursors.Hand };
                var rbN = new RadioButton { Content = "No",  FontSize = 11, IsChecked = !isYes, Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
                var rbPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rbPanel.Children.Add(rbY); rbPanel.Children.Add(rbN);   // grouped by their parent panel
                Grid.SetColumn(rbPanel, 1); rowGrid.Children.Add(rbPanel);
                form.Children.Add(rowGrid);
                editors[key] = rbY;   // on save: rbY.IsChecked -> "Yes"/"No"
            }

            if (editors.Count == 0)
                form.Children.Add(new TextBlock { Text = "No settings were found in ExecutePackage.json.", FontSize = 11, Foreground = lblFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });

            // ── PSADT command templates (free text, not Yes/No) ──
            form.Children.Add(new TextBlock { Text = "PSADT Commands", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = valFg, Margin = new Thickness(0, 10, 0, 2) });
            form.Children.Add(new TextBlock { Text = "Command templates written to ExecutePackage.json.", FontSize = 10.5, Foreground = lblFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

            var cmdEditors = new System.Collections.Generic.Dictionary<string, TextBox>();
            void AddPsadtCmdRow(string key, string caption)
            {
                var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 6), MinHeight = 30 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(lbl, 0); rowGrid.Children.Add(lbl);
                var box = new TextBox { Text = ReadStr(root[key]), FontSize = 11, Background = Brushes.Transparent, Foreground = valFg, BorderThickness = new Thickness(0), Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var boxBorder = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4, 6, 4), Child = box };
                Grid.SetColumn(boxBorder, 1); rowGrid.Children.Add(boxBorder);
                form.Children.Add(rowGrid);
                cmdEditors[key] = box;
            }
            AddPsadtCmdRow(PsadtInstallKey,   "PSADT Install Command");
            AddPsadtCmdRow(PsadtUninstallKey, "PSADT Uninstall Command");
            AddPsadtCmdRow(PsadtRepairKey,    "PSADT Repair Command");

            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 4, 16, 14) };
            Grid.SetRow(btnBar, 2);
            var bCancel = new Button { Content = "✕  Cancel", Style = sec, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            bCancel.Click += (_, _) => cwin.Close();
            var bSave = new Button { Content = "💾  Save", Style = sec, Height = 30, Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            ExecApplyPrimaryLook(bSave);
            bSave.Click += (_, _) =>
            {
                try
                {
                    foreach (var kv in editors)
                        root[kv.Key] = kv.Value.IsChecked == true ? "Yes" : "No";
                    foreach (var kv in cmdEditors)
                        root[kv.Key] = kv.Value.Text ?? "";

                    // Serialise with a Utf8JsonWriter (JsonNode.ToJsonString with a bare
                    // JsonSerializerOptions throws in .NET 8). Build the bytes in memory first
                    // so a failure cannot corrupt the file.
                    byte[] jsonBytes;
                    using (var ms = new MemoryStream())
                    {
                        using (var jw = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
                            root.WriteTo(jw);
                        jsonBytes = ms.ToArray();
                    }
                    File.WriteAllBytes(cfgPath, jsonBytes);

                    // Refresh the cached config so the change applies without a restart.
                    LoadExecConfig();
                    // Reflect any changed PSADT command templates in the page boxes immediately.
                    PopulatePsadtCommandBoxes();

                    cwin.Close();
                    ExecStyledMessage(null, "Execute Package - Settings",
                        "Settings written to ExecutePackage.json.", "✅");
                }
                catch (Exception ex)
                {
                    ExecStyledMessage(cwin, "Execute Package - Settings",
                        "Save failed:\n" + ex.Message + "\n\n" + cfgPath, "⚠");
                }
            };
            btnBar.Children.Add(bCancel); btnBar.Children.Add(bSave);
            og.Children.Add(btnBar);

            cwin.ShowDialog();
        }

        // Small branded message dialog matching the app's window style (replaces the plain
        // MessageBox in the Settings flow). Single OK button.
        private void ExecStyledMessage(Window? owner, string title, string message, string icon)
        {
            var winBg      = Application.Current.TryFindResource("WindowBgBrush")      as Brush ?? Brushes.White;
            var borderClr  = Application.Current.TryFindResource("BorderMainBrush")    as Brush ?? Brushes.LightGray;
            var boxBg      = Application.Current.TryFindResource("InputBgBrush")       as Brush ?? new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr     = Application.Current.TryFindResource("BorderMainBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var valFg      = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? Brushes.Black;
            var sec        = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));

            var win = new Window
            {
                Width = 420, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner ?? Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent,
                FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false
            };
            var outer = new Border { Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black } };
            win.Content = outer;
            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            var tb = new Border { Background = titleBarBg, CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(16, 6, 12, 6) };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti = new TextBlock { Text = icon, FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button { Content = "✕", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => win.Close();
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg; og.Children.Add(tb);

            var msgBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(16, 14, 16, 6) };
            msgBox.Child = new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = valFg };
            Grid.SetRow(msgBox, 1); og.Children.Add(msgBox);

            var actionRow = new Grid { Margin = new Thickness(16, 2, 16, 14) };
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(actionRow, 2);
            var dIcon = ExecDecisionIcon();
            Grid.SetColumn(dIcon, 0);
            actionRow.Children.Add(dIcon);
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(btnBar, 1);
            var bOk = new Button { Content = "✔  OK", Style = sec, Height = 30, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsDefault = true };
            ExecApplyPrimaryLook(bOk);
            bOk.Click += (_, _) => win.Close();
            btnBar.Children.Add(bOk);
            actionRow.Children.Add(btnBar);
            og.Children.Add(actionRow);

            win.ShowDialog();
        }

        // Give a dialog button the highlighted "primary action" look: a filled
        // accent background, rounded corners and a subtle hover. Theme resources
        // keep it readable in both light and dark modes, mirroring the Publish
        // Package dialog. Applied on top of any existing Style (local values win
        // in WPF), so callers keep their own sizing.
        private static void ExecApplyPrimaryLook(Button b)
        {
            Brush bg     = Application.Current.TryFindResource("DlgBtnBlueBgBrush")     as Brush ?? new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5));
            Brush fg     = Application.Current.TryFindResource("DlgBtnBlueFgBrush")     as Brush ?? Brushes.White;
            Brush border = Application.Current.TryFindResource("DlgBtnBlueBorderBrush") as Brush ?? bg;

            b.Background      = bg;
            b.Foreground      = fg;
            b.BorderBrush     = border;
            b.BorderThickness = new Thickness(1);
            b.Cursor          = Cursors.Hand;

            var tpl = new ControlTemplate(typeof(Button));
            var bd  = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty,      bg);
            bd.SetValue(Border.BorderBrushProperty,     border);
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty,    new CornerRadius(6));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 12, 0));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
            tpl.Triggers.Add(hov);
            b.Template = tpl;
        }

        // Uniform "make a choice" icon for a dialog's action row: a blue question
        // glyph matching the Publish Package dialog exactly (same glyph, colour
        // and size) so every popup looks the same. The title bar still carries
        // the page context icon. A monochrome glyph with an explicit Foreground
        // is used so the blue tint always applies in both themes.
        private static TextBlock ExecDecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };

        private static string ReadLogPathFromConfig(string scriptDir)
        {
            // Default fallback
            string defaultLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "Software");

            // Search order for config.psd1:
            //   1. <scriptDir>\Config\config.psd1   (PSADT v4 standard location)
            //   2. <scriptDir>\config.psd1           (flat layout)
            //   3. <parentDir>\Config\config.psd1    (if script is in a subfolder)
            //   4. <parentDir>\config.psd1
            string? parentDir = Path.GetDirectoryName(scriptDir);
            var candidates = new[]
            {
                Path.Combine(scriptDir,         "Config",           "config.psd1"),
                Path.Combine(scriptDir,                             "config.psd1"),
                Path.Combine(parentDir ?? "",   "Config",           "config.psd1"),
                Path.Combine(parentDir ?? "",                       "config.psd1"),
                Path.Combine(scriptDir,         "AppDeployToolkit", "AppDeployToolkitConfig.xml"),
            };

            foreach (var cfg in candidates)
            {
                if (!File.Exists(cfg)) continue;
                try
                {
                    string txt = File.ReadAllText(cfg);

                    // For config.psd1: find the Toolkit block's LogPath specifically.
                    // The file has: Toolkit = @{ ... LogPath = '$envSystemDrive\Logs\' ... }
                    // We must NOT match MSI.LogPath (which may be $null).
                    // Strategy: find the Toolkit = @{ ... } block first, then extract LogPath from it.
                    string logPath = "";

                    // Try to find Toolkit block
                    var toolkitBlock = Regex.Match(txt,
                        @"Toolkit\s*=\s*@\{([^}]+(?:\{[^}]*\}[^}]*)*)\}",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);

                    string searchIn = toolkitBlock.Success ? toolkitBlock.Groups[1].Value : txt;

                    // Match LogPath = '...' or LogPath = "..."  (not $null)
                    var m = Regex.Match(searchIn,
                        @"(?m)^\s*LogPath\s*=\s*'([^']+)'",
                        RegexOptions.IgnoreCase);
                    if (!m.Success)
                        m = Regex.Match(searchIn,
                            "(?m)^\\s*LogPath\\s*=\\s*\"([^\"]+)\"",
                            RegexOptions.IgnoreCase);

                    if (m.Success)
                    {
                        string raw = m.Groups[1].Value.Trim();
                        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("$null", StringComparison.OrdinalIgnoreCase))
                            continue; // skip null/empty entries, try next candidate

                        // Expand known PowerShell environment variable aliases
                        string sysDrive = Path.GetPathRoot(
                            Environment.GetFolderPath(Environment.SpecialFolder.System))
                            ?.TrimEnd('\\') ?? "C:";
                        raw = Regex.Replace(raw, @"\$envSystemDrive|\$env:SystemDrive",
                            sysDrive, RegexOptions.IgnoreCase);
                        raw = Regex.Replace(raw, @"\$envWinDir|\$env:windir|\$env:SystemRoot",
                            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            RegexOptions.IgnoreCase);
                        raw = Regex.Replace(raw, @"\$envProgramData|\$env:ProgramData",
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                            RegexOptions.IgnoreCase);
                        raw = Regex.Replace(raw, @"\$envTemp|\$env:Temp|\$env:TMP",
                            Path.GetTempPath().TrimEnd('\\'),
                            RegexOptions.IgnoreCase);

                        logPath = raw.TrimEnd('\\');
                        if (!string.IsNullOrWhiteSpace(logPath))
                            return logPath;
                    }

                    // Legacy XML format (AppDeployToolkitConfig.xml)
                    if (cfg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        var xm = Regex.Match(txt,
                            @"<Toolkit_LogPath>\s*([^<]+?)\s*</Toolkit_LogPath>",
                            RegexOptions.IgnoreCase);
                        if (xm.Success && !string.IsNullOrWhiteSpace(xm.Groups[1].Value))
                            return xm.Groups[1].Value.Trim().TrimEnd('\\');
                    }
                }
                catch { }
            }
            return defaultLog;
        }

        private void RunScript(string ps1, string deployMode, bool systemContext, string? psadtCommand, CancellationToken ct)
        {
            string scriptDir = Path.GetDirectoryName(ps1) ?? ".";
            _activePs1Path = ps1;  // track for cancel

            // Requirement 5: when a PSADT command was supplied (from the Install / Uninstall box),
            // run THAT command verbatim through a one-line .cmd (cmd /c), so any launcher works -
            // the Invoke-AppDeployToolkit.exe or a full "Powershell.exe -File ...ps1" line - and cmd
            // returns its exit code. Repair, or an empty box, falls back to the classic wrapper.
            bool   useCmd  = !string.IsNullOrWhiteSpace(psadtCommand);
            string cmdFile = "";   // temp .cmd holding the box command (when used)

            // Which uninstall entries existed BEFORE this install, so the one the package creates
            // can be picked out afterwards by difference. Taken here rather than after the fact
            // because there is no other reliable way to tell which of a machine's few hundred
            // installed programs this package just added.
            var arpBefore = deployMode.Equals("Install", StringComparison.OrdinalIgnoreCase)
                ? SnapshotArpKeys() : null;

            // Package folder name segments used to filter PSADT log files so that
            // FindDeployLog only matches logs belonging to THIS package.
            // e.g. "Smekal_FileCompare_1.0_01" -> ["Smekal","FileCompare","1.0","01"]
            string[] pkgParts = BuildPkgParts(scriptDir);

            // Positional args matching working command format:
            //   Install:    (no extra arg)
            //   Uninstall:  Uninstall
            //   Repair:     Repair
            string scriptArg = deployMode == "Install" ? "" : deployMode;

            // Temp files
            string exitFile  = Path.Combine(Path.GetTempPath(),
                "SwiftPack_exit_" + Guid.NewGuid().ToString("N") + ".txt");
            string pidFile   = Path.Combine(Path.GetTempPath(),
                "SwiftPack_pid_"  + Guid.NewGuid().ToString("N") + ".txt");
            string wrapperPs1 = Path.Combine(Path.GetTempPath(),
                "SwiftPack_wrap_" + Guid.NewGuid().ToString("N") + ".ps1");

            // Declare afterDeploy here so it is in scope for both the try body
            // (where ShowResultDialog is dispatched) and the finally block (where it is captured).
            Action? afterDeploy = null;

            try
            {
                int exitCode = -1;

                // When the box carries a command, materialise it as a one-line .cmd that cmd /c runs.
                if (useCmd)
                {
                    cmdFile = Path.Combine(Path.GetTempPath(),
                        "SwiftPack_cmd_" + Guid.NewGuid().ToString("N") + ".cmd");
                    File.WriteAllText(cmdFile,
                        "@echo off\r\n" + psadtCommand + "\r\nexit /b %ERRORLEVEL%\r\n",
                        new System.Text.UTF8Encoding(false));
                    Log("Command : " + psadtCommand);
                }

                if (systemContext)
                {
                    // System context via PsExec.
                    // PsExec MUST run elevated (admin rights) to install PSEXESVC service.
                    // We use the same elevated wrapper PS1 approach as Admin context.
                    string? psExec = FindPsExec(scriptDir);
                    if (psExec == null)
                    {
                        Log("❌ PsExec.exe not found in PATH or script folder.");
                        Log("   Place PsExec.exe in the app folder or PATH.");

                        // The return below exits before the ShowResultDialog dispatch at the end of
                        // the try block, so without this the System Context run just ends silently:
                        // the buttons re-enable and nothing tells the user why nothing happened.
                        string missingPath = PsExecPrimaryPath;
                        Dispatcher.Invoke(() => ExecStyledMessage(Window.GetWindow(this),
                            "Execute Package - File not found",
                            "PsExec.exe not found at:\n" + missingPath, "⚠"));
                        return;
                    }
                    Log($"PsExec  : {psExec}");

                    string escapedExit   = exitFile.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    string escapedPid    = pidFile.Replace("\\",  "\\\\").Replace("\"", "\\\"");
                    string escapedPsExec = psExec.Replace("'", "''");
                    string escapedPs1    = ps1.Replace("'", "''");

                    // Build wrapper PS1 that runs PsExec -s, capturing exit code.
                    // PsExec syntax: PsExec.exe [psexec-switches] program [program-args]
                    // -w sets working directory (must be scriptDir, NOT the ps1 file path)
                    string escapedScriptDir = scriptDir.Replace("'", "''");
                    string escapedPs1ForArgs = ps1.Replace("'", "''");
                    // Build PowerShell arg list for PSADT
                    // DeploymentType is passed as named param only when not Install
                    //
                    // The ps1 path is wrapped in embedded double quotes: Start-Process joins
                    // -ArgumentList elements with plain spaces and does NOT quote them, so a
                    // path containing a space ("...\Manual Package\...") arrives at the target
                    // as two separate arguments. The quotes inside the element survive the join
                    // and keep the path whole on the final command line. Same treatment for -w
                    // below - that one truncated to a nonexistent directory and PsExec answered
                    // with exit code 267, ERROR_DIRECTORY, before the script ever ran.
                    string deployTypeArgs = scriptArg.Length > 0
                        ? "'-DeploymentType','" + scriptArg.Replace("'", "''") + "'"
                        : "";
                    string psArgsTail = deployTypeArgs.Length > 0
                        ? "'-File','\"" + escapedPs1ForArgs + "\"','-DeploymentType','" + scriptArg.Replace("'","''") + "'"
                        : "'-File','\"" + escapedPs1ForArgs + "\"'";

                    // Program + args PsExec runs. With a box command, that is its exe (double-quoted
                    // so a spaced path survives Start-Process joining) plus its tokenised args;
                    // otherwise the classic powershell.exe -File <ps1> [-DeploymentType <mode>].
                    string sysProgram = useCmd
                        ? "'cmd.exe','/c','\"" + cmdFile.Replace("'", "''") + "\"'"
                        : "'powershell.exe','-ExecutionPolicy','Bypass','-NonInteractive','-WindowStyle','Hidden'," + psArgsTail;

                    // Wrapper: runs PsExec -s (SYSTEM) with exit code capture
                    // PsExec syntax: psexec [switches] [program] [program-args]
                    // -s = run as SYSTEM, -w = set working dir, -accepteula = no EULA prompt
                    string wrapperContent =
                        "$exitPath = \"" + escapedExit + "\"\r\n" +
                        "$pidPath  = \"" + escapedPid  + "\"\r\n" +
                        "try {\r\n" +
                        "    $psArgs = @(" +
                        "'-accepteula','-s','-nobanner'," +
                        "'-w','\"" + escapedScriptDir + "\"'," +
                        sysProgram + ")\r\n" +
                        "    $p = Start-Process -FilePath '" + escapedPsExec + "'" +
                        " -ArgumentList $psArgs" +
                        " -Wait -PassThru -WindowStyle Hidden\r\n" +
                        "    $p.Id | Out-File $pidPath -Encoding ASCII -Force\r\n" +
                        "    $p.ExitCode | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                        "} catch {\r\n" +
                        "    -1 | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                        "}\r\n";

                    File.WriteAllText(wrapperPs1, wrapperContent, new System.Text.UTF8Encoding(false));
                    _activePidFile = pidFile;
                    Log("Starting process (SYSTEM context via PsExec, elevated wrapper)…");
                    Log("ℹ  Live output is read from the PSADT log file as it is written.");

                    using var sysProc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName         = "powershell.exe",
                            Arguments        = "-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden" +
                                              " -File \"" + wrapperPs1 + "\"",
                            UseShellExecute  = true,
                            Verb             = "runas",
                            WindowStyle      = ProcessWindowStyle.Hidden,
                            WorkingDirectory = scriptDir
                        }
                    };

                    _runningProcess = sysProc;

                    // Snap log position before start so we only show lines from THIS run
                    string? tailTargetSys = FindDeployLog(_logFilePath, deployMode, pkgParts);
                    long    tailPosSys    = 0;
                    int     tailLinesSys  = 0;
                    if (tailTargetSys != null)
                    {
                        try
                        {
                            using var fs = new FileStream(tailTargetSys, FileMode.Open,
                                FileAccess.Read, FileShare.ReadWrite);
                            tailPosSys = fs.Length;
                        }
                        catch { tailPosSys = 0; }
                    }

                    try { sysProc.Start(); }
                    catch (System.ComponentModel.Win32Exception ex)
                        when (ex.NativeErrorCode == 1223)
                    {
                        Log("⚠  UAC prompt was declined. Execution cancelled.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log("❌ Failed to start process: " + ex.Message);
                        return;
                    }

                    while (!sysProc.WaitForExit(800))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            try { if (!sysProc.HasExited) KillProcessTree(sysProc.Id); } catch { }
                            Log("⚠  Execution cancelled by user.");
                            return;
                        }
                        // Re-resolve every pass: the pre-start lookup may have latched onto
                        // another mode's log, or PSADT created/rotated a new file mid-run.
                        tailTargetSys = RefreshTailTarget(_logFilePath, deployMode, pkgParts,
                                                          tailTargetSys, ref tailPosSys);
                        if (tailTargetSys != null)
                            tailPosSys = TailLogFile(tailTargetSys, tailPosSys, ref tailLinesSys);
                    }

                    // Read exit code written by wrapper
                    if (File.Exists(exitFile))
                    {
                        try
                        {
                            string raw = File.ReadAllText(exitFile).Trim();
                            if (int.TryParse(raw, out int parsed)) exitCode = parsed;
                        }
                        catch { }
                    }

                    // Drain remaining log lines after process exits.
                    // Poll until the log file stops growing (2 stable reads in a row)
                    // or 2 seconds maximum - exits immediately when PSADT is done writing.
                    {
                        long prevSize = -1; int stable = 0;
                        var deadline = DateTime.UtcNow.AddSeconds(2);
                        while (DateTime.UtcNow < deadline)
                        {
                            tailTargetSys = RefreshTailTarget(_logFilePath, deployMode, pkgParts,
                                                              tailTargetSys, ref tailPosSys);
                            if (tailTargetSys != null)
                            {
                                tailPosSys = TailLogFile(tailTargetSys, tailPosSys, ref tailLinesSys);
                                long sz = new FileInfo(tailTargetSys).Length;
                                if (sz == prevSize) { if (++stable >= 2) break; }
                                else { stable = 0; prevSize = sz; }
                            }
                            System.Threading.Thread.Sleep(150);
                        }
                    }
                    Dispatcher.Invoke(new Action(() => {}), System.Windows.Threading.DispatcherPriority.Background);
                }
                else
                {
                    // Administrator context - elevated via UAC (UseShellExecute=true + Verb=runas).
                    //
                    // IMPORTANT: Do NOT redirect stdout/stderr from PSADT.
                    // PSADT shows interactive UI dialogs (e.g. "Click Install to continue").
                    // Redirecting stdout/stderr keeps the pipe open until the user responds,
                    // causing ReadToEnd() / WaitForExit() to deadlock → exit code -1.
                    //
                    // Solution:
                    //   1. Wrapper PS1 just runs the script (no redirect) and writes exit code only.
                    //   2. While waiting, we tail-read PSADT's own log file in C:\Windows\Logs\Software
                    //      to stream live output into the Event Log.

                    string exePath = Path.Combine(scriptDir,
                        Path.GetFileNameWithoutExtension(ps1) + ".exe");
                    bool useExe = File.Exists(exePath);

                    string escapedExit = exitFile.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    string escapedPid  = pidFile.Replace("\\", "\\\\").Replace("\"", "\\\"");

                    string wrapperContent;
                    if (useCmd)
                    {
                        wrapperContent =
                            "$exitPath = \"" + escapedExit + "\"\r\n" +
                            "$pidPath  = \"" + escapedPid  + "\"\r\n" +
                            "try {\r\n" +
                            "    $p = Start-Process -FilePath 'cmd.exe'" +
                            " -ArgumentList @('/c','\"" + cmdFile.Replace("'", "''") + "\"')" +
                            " -WindowStyle Hidden -PassThru\r\n" +
                            "    $p.Id | Out-File $pidPath -Encoding ASCII -Force\r\n" +
                            "    $p.WaitForExit()\r\n" +
                            "    $p.ExitCode | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                            "} catch {\r\n" +
                            "    -1 | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                            "}\r\n";
                    }
                    else if (useExe)
                    {
                        string escapedExe = exePath.Replace("'", "''");
                        wrapperContent =
                            "$exitPath = \"" + escapedExit + "\"\r\n" +
                            "$pidPath  = \"" + escapedPid  + "\"\r\n" +
                            "try {\r\n" +
                            "    $p = Start-Process -FilePath '" + escapedExe + "'" +
                            (scriptArg.Length > 0 ? " -ArgumentList @('" + scriptArg.Replace("'","''") + "')" : "") +
                            " -PassThru\r\n" +
                            "    $p.Id | Out-File $pidPath -Encoding ASCII -Force\r\n" +
                            "    $p.WaitForExit()\r\n" +
                            "    $p.ExitCode | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                            "} catch {\r\n" +
                            "    -1 | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                            "}\r\n";
                    }
                    else
                    {
                        string escapedPs1 = ps1.Replace("'", "''");
                        wrapperContent =
                            "$exitPath = \"" + escapedExit + "\"\r\n" +
                            "$pidPath  = \"" + escapedPid  + "\"\r\n" +
                            "try {\r\n" +
                            "    $p = Start-Process -FilePath 'powershell.exe'" +
                            " -ArgumentList @('-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File','" +
                            escapedPs1 + "'" +
                            (scriptArg.Length > 0 ? ",'" + scriptArg.Replace("'","''") + "'" : "") +
                            ") -Wait -PassThru -WindowStyle Hidden\r\n" +
                            "    $p.Id | Out-File $pidPath -Encoding ASCII -Force\r\n" +
                            "    $p.ExitCode | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                            "} catch {\r\n" +
                            "    -1 | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                            "}\r\n";
                    }

                    File.WriteAllText(wrapperPs1, wrapperContent, new System.Text.UTF8Encoding(false));
                    _activePidFile = pidFile;
                    Log("Starting process (Administrator context, UAC elevation)…");
                    Log("ℹ  Live output is read from the PSADT log file as it is written.");

                    using var elevProc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName         = "powershell.exe",
                            Arguments        = "-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden" +
                                              " -File \"" + wrapperPs1 + "\"",
                            UseShellExecute  = true,
                            Verb             = "runas",
                            WindowStyle      = ProcessWindowStyle.Hidden,
                            WorkingDirectory = scriptDir
                        }
                    };

                    _runningProcess = elevProc;

                    // Record current end-of-file position BEFORE starting the process.
                    // PSADT appends to the same log file across runs. By snapping the
                    // file size now, we skip all pre-existing content and only display
                    // lines written during THIS run.
                    string? tailTarget = FindDeployLog(_logFilePath, deployMode, pkgParts);
                    long    tailPos    = 0;
                    int     tailLines  = 0;
                    if (tailTarget != null)
                    {
                        try
                        {
                            using var fs = new FileStream(tailTarget, FileMode.Open,
                                FileAccess.Read, FileShare.ReadWrite);
                            tailPos = fs.Length;
                        }
                        catch { tailPos = 0; }
                    }

                    try
                    {
                        elevProc.Start();
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                        when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED - user clicked No on UAC
                    {
                        Log("⚠  UAC prompt was declined. Execution cancelled.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log("❌ Failed to start process: " + ex.Message);
                        return;
                    }

                    // Tail-read new lines every 800ms while process is running
                    while (!elevProc.WaitForExit(800))
                    {
                        // Check for cancellation - exit loop cleanly without throwing
                        if (ct.IsCancellationRequested)
                        {
                            try { if (!elevProc.HasExited) KillProcessTree(elevProc.Id); } catch { }
                            Log("⚠  Execution cancelled by user.");
                            return;
                        }
                        // Log file may not exist yet, or PSADT may write a different file
                        // than the one snapped before start - keep re-resolving every pass.
                        tailTarget = RefreshTailTarget(_logFilePath, deployMode, pkgParts,
                                                       tailTarget, ref tailPos);
                        if (tailTarget != null)
                            tailPos = TailLogFile(tailTarget, tailPos, ref tailLines);
                    }

                    // PSADT may still be writing final log entries after WaitForExit returns.
                    // Poll until log stops growing (2 stable reads) or 2s max - exits immediately when done.
                    tailTarget = RefreshTailTarget(_logFilePath, deployMode, pkgParts, tailTarget, ref tailPos);
                    {
                        long prevSize = -1; int stable = 0;
                        var deadline = DateTime.UtcNow.AddSeconds(2);
                        while (DateTime.UtcNow < deadline)
                        {
                            tailTarget = RefreshTailTarget(_logFilePath, deployMode, pkgParts, tailTarget, ref tailPos);
                            if (tailTarget != null)
                            {
                                tailPos = TailLogFile(tailTarget, tailPos, ref tailLines);
                                long sz = new FileInfo(tailTarget).Length;
                                if (sz == prevSize) { if (++stable >= 2) break; }
                                else { stable = 0; prevSize = sz; }
                            }
                            System.Threading.Thread.Sleep(150);
                        }
                    }

                    // PSADT's Show-ADTInstallationPrompt spawns a detached ServiceUI/powershell
                    // child that survives after Close-ADTSession. Wait for the inner powershell
                    // process (whose PID we captured) and ALL its descendants to fully exit.
                    if (File.Exists(pidFile))
                    {
                        try
                        {
                            string rawPid = File.ReadAllText(pidFile).Trim();
                            if (int.TryParse(rawPid, out int innerPid) && innerPid > 0)
                            {
                                // Wait for the inner powershell and all child processes to exit
                                WaitForProcessTreeExit(innerPid, ct);
                                // Final drain after process tree exits - same stable detection
                                {
                                    long prevSize2 = -1; int stable2 = 0;
                                    var deadline2 = DateTime.UtcNow.AddSeconds(1);
                                    while (DateTime.UtcNow < deadline2)
                                    {
                                        tailTarget = RefreshTailTarget(_logFilePath, deployMode, pkgParts,
                                                                       tailTarget, ref tailPos);
                                        if (tailTarget != null)
                                        {
                                            tailPos = TailLogFile(tailTarget, tailPos, ref tailLines);
                                            long sz2 = new FileInfo(tailTarget).Length;
                                            if (sz2 == prevSize2) { if (++stable2 >= 2) break; }
                                            else { stable2 = 0; prevSize2 = sz2; }
                                        }
                                        System.Threading.Thread.Sleep(150);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    Dispatcher.Invoke(new Action(() => {}), System.Windows.Threading.DispatcherPriority.Background);

                    // Read exit code written by wrapper
                    if (File.Exists(exitFile))
                    {
                        try
                        {
                            string raw = File.ReadAllText(exitFile).Trim();
                            if (int.TryParse(raw, out int parsed)) exitCode = parsed;
                        }
                        catch { }
                    }
                }

                Log("─────────────────────────────────────────");
                bool success = exitCode == 0 || exitCode == 3010;
                Log((success ? "✅" : "❌") + " Process exited with code " + exitCode);
                if (exitCode == 3010) Log("ℹ  Exit code 3010 = success, reboot required.");

                // Find the PSADT log file for this deployment type
                string? psadtLog = FindDeployLog(_logFilePath, deployMode, pkgParts);
                if (psadtLog != null) Log("📄 Log: " + psadtLog);
                else Log("ℹ  Log folder: " + _logFilePath + " (no .log files found yet)");

                // Auto-populate Package Validation log path when exit code is 0 or 3010
                if (success && psadtLog != null)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var mainWin = Application.Current.MainWindow as MainWindow;
                            mainWin?._qaPage.SetLogPath(psadtLog);
                        }
                        catch { }
                    }));
                }

                // Icon.png for Intune, written next to the package. Install only, and only when it
                // succeeded: there is nothing installed to take an icon from otherwise.
                if (success && deployMode.Equals("Install", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string? pkgRootLogo = System.IO.Directory.GetParent(scriptDir)?.FullName;
                        if (!string.IsNullOrEmpty(pkgRootLogo) && System.IO.Directory.Exists(pkgRootLogo))
                            WriteIntuneLogo(pkgRootLogo, arpBefore);
                        else
                            Log("ℹ  Icon.png not created - package folder could not be resolved from " + scriptDir + ".");
                    }
                    catch (Exception ex) { Log("⚠  Icon.png skipped: " + ex.Message); }
                }

                // Copy the log file for THIS deployment mode only into
                // <PackageRootFolder>\Documents\ - only when CopyLogsToDocuments = "Yes"
                // in ExecutePackage.json.
                if (_execCfg.ShouldCopyLogs)
                {
                    try
                    {
                        string? pkgRoot = System.IO.Directory.GetParent(scriptDir)?.FullName;
                        if (!string.IsNullOrEmpty(pkgRoot)
                            && System.IO.Directory.Exists(pkgRoot)
                            && psadtLog != null
                            && System.IO.File.Exists(psadtLog))
                        {
                            string docsDir = System.IO.Path.Combine(pkgRoot, "Documents");
                            System.IO.Directory.CreateDirectory(docsDir);
                            string dest = System.IO.Path.Combine(docsDir,
                                System.IO.Path.GetFileName(psadtLog));

                            // PSADT (elevated) may still hold the log open for a moment after the
                            // run finishes - File.Copy would fail because it opens the source with
                            // no write-sharing. Read with FileShare.ReadWrite and retry briefly.
                            Exception? lastErr = null;
                            bool copied = false;
                            for (int attempt = 0; attempt < 5 && !copied; attempt++)
                            {
                                try
                                {
                                    using (var srcStream = new System.IO.FileStream(psadtLog,
                                               System.IO.FileMode.Open, System.IO.FileAccess.Read,
                                               System.IO.FileShare.ReadWrite))
                                    using (var dstStream = new System.IO.FileStream(dest,
                                               System.IO.FileMode.Create, System.IO.FileAccess.Write,
                                               System.IO.FileShare.None))
                                        srcStream.CopyTo(dstStream);
                                    copied = true;
                                }
                                catch (System.IO.IOException ex) { lastErr = ex; System.Threading.Thread.Sleep(250); }
                            }

                            if (copied) Log("📂 Log copied → " + dest);
                            else Log("⚠  Log copy failed after retries: " + (lastErr?.Message ?? "unknown error"));
                        }
                        else
                        {
                            Log("ℹ  Log not copied - " +
                                (psadtLog == null ? "no matching .log found in " + _logFilePath
                                                  : "package Documents folder could not be resolved from " + scriptDir) + ".");
                        }
                    }
                    catch (Exception ex) { Log("⚠  Log copy skipped: " + ex.Message); }
                }
                else
                {
                    // State the value that actually decided this and where it came from. The old
                    // text always blamed ExecutePackage.json, even when the deciding value was the
                    // per-app runtime override or a config file that never loaded.
                    string why = ExecutePackageConfig.RuntimeCopyLogsOverride != null
                        ? "per-app override CopyLogs = " + ExecutePackageConfig.RuntimeCopyLogsOverride
                        : "CopyLogsToDocuments = " + (string.IsNullOrWhiteSpace(_execCfg.CopyLogsToDocuments)
                                                        ? "(not set)" : _execCfg.CopyLogsToDocuments)
                          + " in " + (_execCfgPath ?? "ExecutePackage.json (file not found)");
                    Log("ℹ  Log not copied to Documents (" + why + ").");
                }

                // Show result dialog - BeginInvoke so it runs on UI thread.
                // Pass afterDeploy so ShowResultDialog fires it only AFTER win.ShowDialog() returns
                // (i.e. after the user clicks Close), not before.
                string   _dm  = deployMode;
                bool     _suc = success;
                int      _ec  = exitCode;
                string?  _pl  = psadtLog;
                string?  _lp  = _logFilePath;
                DateTime _st  = _deployStartTime;
                string   _sd  = scriptDir;
                bool     _hasNext = _onAfterDeploy != null;
                afterDeploy      = _onAfterDeploy;
                _onAfterDeploy   = null;

                // Track last log paths and notify MainWindow for document auto-fill
                if (success && psadtLog != null)
                {
                    if (deployMode.Equals("Install", StringComparison.OrdinalIgnoreCase))
                        LastInstallLogPath   = psadtLog;
                    else if (deployMode.Equals("Uninstall", StringComparison.OrdinalIgnoreCase))
                        LastUninstallLogPath = psadtLog;
                    // Use the ps1 parameter (captured on UI thread before Task.Run) - NOT TxtScriptPath.Text
                    OnDeployCompleted?.Invoke(deployMode, psadtLog, ps1);
                }

                Dispatcher.BeginInvoke(new Action(() => ShowResultDialog(_dm, _suc, _ec, _pl, _lp, _st, _sd, _hasNext, afterDeploy)));
            }
            finally
            {
                _activePs1Path = null;
                _activePidFile = null;
                string wp = wrapperPs1, ef = exitFile, pf = pidFile, cf = cmdFile;
                Task.Run(() => {
                    System.Threading.Thread.Sleep(2000);
                    try { if (File.Exists(wp)) File.Delete(wp); } catch { }
                    try { if (File.Exists(ef)) File.Delete(ef); } catch { }
                    try { if (File.Exists(pf)) File.Delete(pf); } catch { }
                    try { if (!string.IsNullOrEmpty(cf) && File.Exists(cf)) File.Delete(cf); } catch { }
                });
            }
        }

        // ══ Intune logo ═════════════════════════════════════════════════════
        // Intune wants a PNG for the app tile in Company Portal. Everything below reads the icon
        // out of the binary that was just installed on this machine, so it works with no network:
        // nothing is downloaded and no vendor site is consulted.

        private static readonly (RegistryKey root, string sub)[] ArpHives =
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        /// <summary>Every uninstall subkey name currently present, as "hiveIndex|subkey".</summary>
        private static HashSet<string> SnapshotArpKeys()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ArpHives.Length; i++)
            {
                try
                {
                    using var hive = ArpHives[i].root.OpenSubKey(ArpHives[i].sub);
                    if (hive == null) continue;
                    foreach (string sub in hive.GetSubKeyNames()) set.Add(i + "|" + sub);
                }
                catch { }
            }
            return set;
        }

        /// <summary>
        /// The icon source for the app this install added, from the uninstall entry that was not
        /// there before: a resolvable DisplayIcon file, else the MSI ProductIcon for that entry's
        /// ProductCode, else the largest EXE in a folder derived from InstallLocation / DisplayIcon /
        /// InstallSource. The MSI paths are what make this work for .msi installs, which usually
        /// leave DisplayIcon unresolvable and InstallLocation blank. Null when no key yields any of
        /// the three (a file-copy-only package legitimately does).
        /// </summary>
        private static string? FindNewlyInstalledIconSource(HashSet<string>? before)
        {
            string exeFallback = "";
            for (int i = 0; i < ArpHives.Length; i++)
            {
                try
                {
                    using var hive = ArpHives[i].root.OpenSubKey(ArpHives[i].sub);
                    if (hive == null) continue;
                    foreach (string sub in hive.GetSubKeyNames())
                    {
                        if (before != null && before.Contains(i + "|" + sub)) continue;   // was already there
                        using var key = hive.OpenSubKey(sub);
                        if (key == null) continue;
                        // A patch or update entry carries no icon worth using and would otherwise
                        // win simply by being new.
                        if ((key.GetValue("SystemComponent") as int? ?? 0) != 0) continue;
                        if (string.IsNullOrWhiteSpace(key.GetValue("DisplayName") as string)) continue;

                        string direct = IconSourceFromKey(key, sub, ref exeFallback);
                        if (direct.Length > 0) return direct;
                    }
                }
                catch { }
            }
            return exeFallback.Length > 0 ? exeFallback : null;
        }

        // Best icon source from one uninstall subkey, MSI shapes included, or "" when the key yields
        // nothing on disk. Order: a DisplayIcon that resolves to a real file (EXE, and MSIs that
        // cache a usable path), then the MSI ProductIcon for the key's ProductCode (the subkey name
        // IS the ProductCode for a per-product MSI entry) - the reliable source when DisplayIcon and
        // InstallLocation both give nothing, then the largest .exe under a folder derived from
        // InstallLocation, DisplayIcon's directory, or InstallSource (collected into exeFallback so a
        // direct icon on a later key still wins).
        private static string IconSourceFromKey(RegistryKey key, string? subKeyName, ref string exeFallback)
        {
            string di = ResolveIconFilePath(key.GetValue("DisplayIcon") as string ?? "");
            if (di.Length > 0) return di;

            string pcode = (!string.IsNullOrEmpty(subKeyName)
                            && subKeyName.StartsWith("{") && subKeyName.EndsWith("}"))
                         ? subKeyName : "";
            string msiIcon = ResolveMsiProductIcon(pcode);
            if (msiIcon.Length > 0) return msiIcon;

            if (exeFallback.Length == 0)
            {
                foreach (string cand in EnumerateFolderCandidates(key))
                {
                    try
                    {
                        var exes = Directory.GetFiles(cand, "*.exe", SearchOption.TopDirectoryOnly);
                        if (exes.Length > 0) { exeFallback = exes.OrderByDescending(f => new FileInfo(f).Length).First(); break; }
                    }
                    catch { }
                }
            }
            return "";
        }

        // Folders worth scanning for an app's main .exe, in priority order, skipping blanks and the
        // Windows Installer cache (its exes are icon stubs, not the real application).
        private static IEnumerable<string> EnumerateFolderCandidates(RegistryKey key)
        {
            string winInstaller;
            try { winInstaller = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer"); }
            catch { winInstaller = @"C:\Windows\Installer"; }

            static string Expand(string? s)
            {
                string v = (s ?? "").Trim().Trim('"');
                try { v = Environment.ExpandEnvironmentVariables(v); } catch { }
                return v.TrimEnd(Path.DirectorySeparatorChar);
            }
            bool Usable(string v) =>
                v.Length > 0 && Directory.Exists(v)
                && !v.StartsWith(winInstaller, StringComparison.OrdinalIgnoreCase);

            string loc = Expand(key.GetValue("InstallLocation") as string);
            if (Usable(loc)) yield return loc;

            string diPath = ResolveIconFilePath(key.GetValue("DisplayIcon") as string ?? "");
            if (diPath.Length > 0)
            {
                string dir = Expand(Path.GetDirectoryName(diPath));
                if (Usable(dir)) yield return dir;
            }

            string src = Expand(key.GetValue("InstallSource") as string);
            if (Usable(src)) yield return src;
        }

        // Resolve a "path" or "path,index" DisplayIcon to a real file, or "" when nothing on disk
        // matches. Strips a trailing numeric ,index (only when the tail is numeric, so a real comma
        // in a path is kept) and expands environment variables some installers store raw.
        private static string ResolveIconFilePath(string displayIcon)
        {
            string raw = (displayIcon ?? "").Trim().Trim('"');
            if (raw.Length == 0) return "";
            int comma = raw.LastIndexOf(',');
            if (comma > 1 && int.TryParse(raw.Substring(comma + 1).Trim(), out _))
                raw = raw.Substring(0, comma).Trim().Trim('"');
            if (raw.Length == 0) return "";
            try { raw = Environment.ExpandEnvironmentVariables(raw); } catch { }
            return File.Exists(raw) ? raw : "";
        }

        // The icon path msi.dll reports for a ProductCode, or "" when the code is blank, not an MSI
        // GUID, or unavailable. "ProductIcon" is a real path on disk - usually the cached
        // ARPPRODUCTICON.exe or an .ico - the same icon Windows shows in Programs and Features.
        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern uint MsiGetProductInfo(string szProduct, string szProperty,
            System.Text.StringBuilder? lpValueBuf, ref uint pcchValueBuf);

        private static string ResolveMsiProductIcon(string productCode)
        {
            string code = (productCode ?? "").Trim();
            if (code.Length == 0 || code[0] != '{') return "";
            try
            {
                uint len = 0;
                MsiGetProductInfo(code, "ProductIcon", null, ref len);   // error 234 (MORE_DATA) expected
                if (len == 0) return "";
                var sb = new System.Text.StringBuilder((int)len + 1);
                len = (uint)sb.Capacity;
                if (MsiGetProductInfo(code, "ProductIcon", sb, ref len) != 0) return "";
                string p = sb.ToString().Trim().Trim('"');
                if (p.Length == 0) return "";
                try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
                return ResolveIconFilePath(p);   // ProductIcon can itself carry a ,index tail
            }
            catch { return ""; }
        }

        // Icons live in a PE resource, not an image container, so no image decoder can read an
        // .exe. PrivateExtractIcons is the Win32 call that asks for a given size and returns the
        // closest the file actually carries, which is how a modern binary's 256px icon is reached
        // rather than the 32px one the shell hands out by default.
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[]? phicon, int[]? piconid, int nIcons, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// The largest image an .ico, .exe or .dll carries, re-encoded as PNG. Null when the file
        /// holds no icon at all.
        /// </summary>
        private static byte[]? ExtractLargestIconPng(string path)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapSource? best = null;
                string ext = Path.GetExtension(path).ToLowerInvariant();

                if (ext == ".exe" || ext == ".dll")
                {
                    // Largest first: the first size the file actually has wins, so a binary with
                    // only a 32px icon still yields something rather than a blank upscale.
                    foreach (int size in new[] { 256, 128, 64, 48, 32 })
                    {
                        var h = new IntPtr[1];
                        var ids = new int[1];
                        int got = PrivateExtractIcons(path, 0, size, size, h, ids, 1, 0);
                        if (got <= 0 || h[0] == IntPtr.Zero) continue;
                        try
                        {
                            best = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                h[0], System.Windows.Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        }
                        finally { DestroyIcon(h[0]); }
                        if (best != null) break;
                    }
                }
                else
                {
                    // An .ico holds several sizes in one file and the biggest is the one to keep.
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(fs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    foreach (var f in dec.Frames)
                        if (best == null || f.PixelWidth * f.PixelHeight > best.PixelWidth * best.PixelHeight) best = f;
                }
                if (best == null) return null;

                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(best));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        /// <summary>
        /// Writes &lt;packageRoot&gt;\Documents\Icon.png from the icon of the application this
        /// install added. An existing Icon.png in that folder is left alone: a logo put there by
        /// hand is a deliberate choice and is usually better than an extracted one.
        /// </summary>
        // Fallback icon source when the install added no NEW uninstall entry (app already installed).
        // Finds the existing ARP entry whose DisplayName matches this package - its normalized name
        // is a substring of the package folder name - and returns its icon source. Prefers the most
        // specific (longest) name match so a broader product does not win.
        private static string? FindInstalledIconSourceByName(string pkgFolderName)
        {
            string folderNorm = NormalizeName(pkgFolderName);
            if (folderNorm.Length == 0) return null;
            string? bestSrc = null; int bestLen = 0;
            string exeFallback = "";
            for (int i = 0; i < ArpHives.Length; i++)
            {
                try
                {
                    using var hive = ArpHives[i].root.OpenSubKey(ArpHives[i].sub);
                    if (hive == null) continue;
                    foreach (string sub in hive.GetSubKeyNames())
                    {
                        using var key = hive.OpenSubKey(sub);
                        if (key == null) continue;
                        if ((key.GetValue("SystemComponent") as int? ?? 0) != 0) continue;
                        string dispNorm = NormalizeName(key.GetValue("DisplayName") as string ?? "");
                        if (dispNorm.Length < 5 || dispNorm.Length <= bestLen) continue;
                        if (!folderNorm.Contains(dispNorm)) continue;

                        // MSI-aware resolution for this matched entry (DisplayIcon file, else MSI
                        // ProductIcon, else largest .exe from a derived folder).
                        string src = IconSourceFromKey(key, sub, ref exeFallback);
                        if (src.Length == 0) src = exeFallback;
                        if (src.Length > 0) { bestSrc = src; bestLen = dispNorm.Length; }
                    }
                }
                catch { }
            }
            return bestSrc;
        }

        // Lowercased alphanumeric-only form, so "Smekal File Compare 1.0" and the folder segment
        // "SmekalFileCompare_1.0.0_017" compare on their letters and digits alone.
        private static string NormalizeName(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "")
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private void WriteIntuneLogo(string pkgRoot, HashSet<string>? arpBefore)
        {
            // Icon.png lives under the package's Documents subfolder (the same place the PSADT log
            // is copied), not the package root.
            string docsDir = Path.Combine(pkgRoot, "Documents");
            string dest = Path.Combine(docsDir, "Icon.png");
            if (File.Exists(dest))
            { Log("ℹ  Icon.png already exists - left as it is: " + dest); return; }

            // Prefer an uninstall entry this install just added; if the app was already installed
            // (a re-install, or a new build of the same version), fall back to the existing entry
            // whose name matches this package, so Icon.png is still produced.
            string? src = FindNewlyInstalledIconSource(arpBefore)
                       ?? FindInstalledIconSourceByName(Path.GetFileName(pkgRoot.TrimEnd('\\', '/')));
            if (src == null)
            { Log("ℹ  Icon.png not created - no matching installed application was found to take an icon from."); return; }

            byte[]? png = ExtractLargestIconPng(src);
            if (png == null)
            { Log("ℹ  Icon.png not created - no icon could be read from " + src + "."); return; }

            Directory.CreateDirectory(docsDir);
            File.WriteAllBytes(dest, png);
            Log("🖼  Icon.png created → " + dest + "  (from " + Path.GetFileName(src) + ")");
        }

        /// <summary>
        /// Splits the package root folder name into the segments that must ALL appear in a PSADT
        /// log file name for that log to belong to THIS package. The folder name is what PSADT
        /// uses as its InstallName, so it is also the log file name prefix:
        ///   "VideoLAN_VLCmediaplayer_3.0.23.0_055"
        ///   -> ["Smekal","FileCompare","1.0.0","01"]
        ///   -> matches "VideoLAN_VLCmediaplayer_3.0.23.0_055_PSAppDeployToolkit_Install.log"
        ///
        /// Invoke-AppDeployToolkit.ps1 does NOT sit at a fixed depth. Create Package copies the
        /// PSADT template as-is, so the folder holding the script can be any of:
        ///   &lt;pkg&gt;\Package\   &lt;pkg&gt;\AppPackages\   &lt;pkg&gt;\Output\AppPackages\Test\
        /// Taking the script folder's parent therefore yielded "AppPackages" for the deepest
        /// layout, which matches no log file name at all - every log then looked missing and
        /// nothing was tailed or copied. Walk UP instead and take the first ancestor that looks
        /// like a package name (two or more underscore / hyphen separated segments), falling back
        /// to the old parent-of-script-folder behaviour when no ancestor qualifies.
        /// </summary>
        private static string[] BuildPkgParts(string scriptDir)
        {
            var sep = new[] { '_', '-' };
            try
            {
                for (var dir = Directory.GetParent(scriptDir); dir != null; dir = dir.Parent)
                {
                    string[] parts = dir.Name.Split(sep, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) return parts;
                }
            }
            catch { }

            return (Path.GetDirectoryName(scriptDir) is string pr ? Path.GetFileName(pr) : "")
                   .Split(sep, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Finds the PSADT log file for the given deployment type.
        /// PSADT names logs: PackageName_PSAppDeployToolkit_Install.log
        ///                   PackageName_PSAppDeployToolkit_Uninstall.log
        ///                   PackageName_PSAppDeployToolkit_Repair.log
        /// Falls back to the most-recently-modified .log if no match found.
        /// </summary>
        private static string? FindDeployLog(string? logDir, string deployMode,
                                             string[]? pkgParts = null)
        {
            if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir)) return null;
            try
            {
                var all = Directory.GetFiles(logDir, "*.log", SearchOption.TopDirectoryOnly);

                // Filter to logs whose filename contains ALL package name segments.
                // This excludes logs from other packages while tolerating the EN_ language
                // tag that PSADT inserts between version and index in the log filename.
                // e.g. pkgParts = ["Smekal","FileCompare","1.0","01"]
                IEnumerable<string> candidates = all;
                if (pkgParts != null && pkgParts.Length > 0)
                    candidates = all.Where(f =>
                        pkgParts.All(part =>
                            Path.GetFileName(f).IndexOf(part,
                                StringComparison.OrdinalIgnoreCase) >= 0));

                // Prefer exact match on deployment type suffix
                string suffix = "_PSAppDeployToolkit_" + deployMode + ".log";
                var match = candidates
                    .Where(f => Path.GetFileName(f).EndsWith(suffix,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
                if (match != null) return match;

                // Fall back to most-recently-modified candidate for this package
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch { return null; }
        }

        private static string? FindLatestLog(string? logDir, string[]? pkgParts = null)
            => FindDeployLog(logDir, "Install", pkgParts);

        /// <summary>
        /// Re-resolves the freshest PSADT log for this run on every poll pass. PSADT may
        /// write a different file than the one snapped before start: the pre-start lookup
        /// can latch onto another deployment type's log (its fallback picks the most recent
        /// package log when no *_Mode.log exists yet), or PSADT creates/rotates a new file
        /// mid-run. Switching targets resets the read position so the correct file is
        /// shown from its beginning.
        /// </summary>
        private static string? RefreshTailTarget(string? logDir, string deployMode,
                                                 string[]? pkgParts, string? current, ref long pos)
        {
            string? freshest = FindDeployLog(logDir, deployMode, pkgParts);
            if (freshest != null &&
                !string.Equals(freshest, current, StringComparison.OrdinalIgnoreCase))
            {
                pos = 0;            // different (newer / better-matching) log - read from start
                return freshest;
            }
            if (current != null && !File.Exists(current))
            {
                pos = 0;            // current log vanished - fall back (possibly null)
                return freshest;
            }
            return current ?? freshest;
        }

        private void ShowResultDialog(string deployMode, bool success, int exitCode,
                                      string? logFile, string? logDir, DateTime startTime,
                                      string? packageFolder = null, bool hasNextCallback = false,
                                      Action? afterDeploy = null)
        {
            var endTime   = DateTime.Now;
            var elapsed   = endTime - startTime;
            string elapsedStr = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                : elapsed.TotalSeconds >= 1
                    ? $"{(int)elapsed.TotalSeconds}s"
                    : $"{(int)elapsed.TotalMilliseconds}ms";

            string modeLabel = deployMode switch
            {
                "Install"   => "Install",
                "Uninstall" => "Uninstall",
                "Repair"    => "Repair",
                _           => deployMode
            };
            string winTitle = success
                ? $"Execute Package - {modeLabel} Complete"
                : $"Execute Package - {modeLabel} Failed";

            // ── Window shell ─────────────────────────────────────────────
            var winBg     = Application.Current.TryFindResource("WindowBgBrush")   as Brush ?? Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? Brushes.LightGray;
            var win = new Window
            {
                Title = winTitle, Width = 460, MaxWidth = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Segoe UI"),
                ShowInTaskbar = false,
            };
            var outerBorder = new Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black }
            };
            win.Content = outerBorder;
            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerBorder.Child = outerGrid;

            // ── Title bar ───────────────────────────────────────────────
            // Dark title bar - same as CreatePackage dialogs (0x53,0x56,0x63 with white text)
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));
            Brush titleTxtBrush = Brushes.White;
            Brush titleSecBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            var txtPri = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? Brushes.Black;
            var txtSec = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var titleBar = new Border { Background = titleBarBg, BorderBrush = borderClr, BorderThickness = new Thickness(0,0,0,1), CornerRadius = new CornerRadius(10,10,0,0), Padding = new Thickness(16,6,12,6) };
            titleBar.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(titleBar, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tIcon  = new TextBlock { Text = "▶", FontSize = 14, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            var tText  = new TextBlock { Text = winTitle, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center };
            var tClose = new Button { Content = "✕", FontSize = 11, Foreground = titleSecBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6,2,6,2) };
            tClose.Click += (_, _) => win.Close();
            Grid.SetColumn(tIcon, 0); Grid.SetColumn(tText, 1); Grid.SetColumn(tClose, 2);
            tg.Children.Add(tIcon); tg.Children.Add(tText); tg.Children.Add(tClose);
            titleBar.Child = tg;
            outerGrid.Children.Add(titleBar);

            // ── Content grid ────────────────────────────────────────────
            var root = new Grid { Margin = new Thickness(8) };
            Grid.SetRow(root, 1);
            outerGrid.Children.Add(root);

            var boxBg  = Application.Current.TryFindResource("InputBgBrush")       as Brush ?? new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x55, 0x5C, 0x7A));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));

            // Exit code colour
            Brush codeBrush = success
                ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
                : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

            // Build detail rows: Time, Mode, Exit Code, Log, [Note]
            var detailRows = new List<(string label, string value, bool highlight)>
            {
                ("Time",      $"Started: {startTime:HH:mm:ss}  Finished: {endTime:HH:mm:ss}  Duration: {elapsedStr}", false),
                ("Path",      packageFolder ?? deployMode, false),
                ("Exit Code", exitCode.ToString(), true),
            };
            if (!string.IsNullOrEmpty(logFile))
                detailRows.Add(("Log", logFile, false));
            if (exitCode == 3010)
                detailRows.Add(("Note", "Reboot required to complete the operation.", false));

            var details = new Grid();
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < detailRows.Count; i++)
            {
                details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (i < detailRows.Count - 1)
                    details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
                int r = i * 2;
                var lbl = new TextBlock
                {
                    Text = detailRows[i].label,
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center
                };
                var box = new Border
                {
                    Background = boxBg, BorderBrush = boxBdr,
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6)
                };
                box.Child = new TextBlock
                {
                    Text = detailRows[i].value,
                    FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Foreground = detailRows[i].highlight ? codeBrush : valFg,
                    FontWeight = detailRows[i].highlight ? FontWeights.Bold : FontWeights.Normal,
                    FontFamily = new FontFamily("Consolas")
                };
                Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0);
                Grid.SetRow(box, r); Grid.SetColumn(box, 1);
                details.Children.Add(lbl);
                details.Children.Add(box);
            }

            // Spacer + Buttons row
            int btnSpacerRow = detailRows.Count * 2 - 1;
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int btnRow = btnSpacerRow + 1;

            Style? sec = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            int btnCount = 1 + (logDir != null && Directory.Exists(logDir) ? 1 : 0) + (logFile != null && File.Exists(logFile) ? 1 : 0);
            var btnGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = btnCount };

            if (logDir != null && Directory.Exists(logDir))
            {
                var b = new Button { Content = "📁 Open Path", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(0,0,4,0) };
                b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", packageFolder ?? logDir ?? "") { UseShellExecute = true }); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ExecutePackage] open path failed: " + ex.Message); } };
                btnGrid.Children.Add(b);
            }
            if (logFile != null && File.Exists(logFile))
            {
                var b = new Button { Content = "📄 Open Log", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(4,0,4,0) };
                b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(logFile) { UseShellExecute = true }); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ExecutePackage] open log failed: " + ex.Message); } };
                btnGrid.Children.Add(b);
            }
            var ok = new Button { Content = "✕ Close", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(4,0,0,0), IsDefault = true };
            ok.Click += (_, _) => win.Close();
            btnGrid.Children.Add(ok);
            // Highlight the first button as the default action (uniform with other pages).
            if (btnGrid.Children[0] is Button firstBtn) ExecApplyPrimaryLook(firstBtn);

            Grid.SetRow(btnGrid, btnRow); Grid.SetColumn(btnGrid, 1);
            details.Children.Add(btnGrid);

            // Icon in col 0 of the button row: uniform blue decision icon.
            var rowIcon = ExecDecisionIcon();
            Grid.SetRow(rowIcon, btnRow); Grid.SetColumn(rowIcon, 0);
            details.Children.Add(rowIcon);

            root.Children.Add(details);

            // Auto-close on success if ShowMessageBox = No (set by Source Download per-app JSON).
            // The flag must stay false for the entire chain (e.g. Install then Uninstall).
            // Reset it to true only AFTER the modal closes and no more callbacks follow.
            if (success && !SwiftPack.Views.SourceDownloadPage.GlobalShowMessageBox)
            {
                win.Loaded += (_, _) => win.Dispatcher.BeginInvoke(
                    new Action(() => win.Close()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            win.ShowDialog();
            // Reset only when this is the last step in the chain (no further Install/Uninstall follows)
            if (!success || !hasNextCallback)
                SwiftPack.Views.SourceDownloadPage.GlobalShowMessageBox = true;

            // Fire the next step (e.g. Uninstall) AFTER the user clicks Close on this dialog.
            // This is guaranteed because win.ShowDialog() is modal - it only returns on Close.
            if (afterDeploy != null)
                Dispatcher.BeginInvoke(afterDeploy, System.Windows.Threading.DispatcherPriority.Normal);
            else
            {
                // Full chain complete - reset PendingPackageFolder so manual QA runs aren't affected
                SwiftPack.Views.ValidatePackagePage.PendingPackageFolder = "";
            }
        }

        private static ControlTemplate CreateBtnTemplate()
        {
            var tpl = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.PaddingProperty, new Thickness(10, 0, 10, 0));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            tpl.VisualTree = border;
            return tpl;
        }

        /// <summary>
        /// Canonical production location for PsExec.exe - the first path FindPsExec probes.
        /// Shown in the "not found" dialog so both stay in step.
        /// </summary>
        private static string PsExecPrimaryPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Smekal", "SwiftPack Studio", "Templates", "PsExec.exe");

        private static string? FindPsExec(string scriptDir)
        {
            // Resolve the folder where this executable lives
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;

            // Standard install paths
            string installDir   = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Smekal", "SwiftPack Studio");
            // Templates folder - primary location as configured
            string templatesDir = Path.Combine(installDir, "Templates");

            var candidates = new[]
            {
                // Templates folder - canonical production location
                Path.Combine(templatesDir, "psexec.exe"),
                Path.Combine(templatesDir, "PsExec.exe"),
                // Same folder as the running .exe
                Path.Combine(exeDir, "psexec.exe"),
                Path.Combine(exeDir, "PsExec.exe"),
                // Install root
                Path.Combine(installDir, "psexec.exe"),
                Path.Combine(installDir, "PsExec.exe"),
                // Script folder (legacy / portable)
                Path.Combine(scriptDir, "psexec.exe"),
                Path.Combine(scriptDir, "PsExec.exe"),
                Path.Combine(scriptDir, "SupportFiles", "PsExec.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;

            // Search PATH environment variable
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null)
                foreach (var dir in pathEnv.Split(';'))
                {
                    string full = Path.Combine(dir.Trim(), "psexec.exe");
                    if (File.Exists(full)) return full;
                    full = Path.Combine(dir.Trim(), "PsExec.exe");
                    if (File.Exists(full)) return full;
                }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        // TAIL LOG FILE - CMTrace format parser
        // PSADT writes CMTrace XML lines (may span multiple lines):
        //   <![LOG[message]LOG]!><time="HH:mm:ss.fff+zzz" ... component="..." type="1|2|3" ...>
        // Reads all new content at once so multi-line entries are matched correctly.
        // Uses the actual PSADT timestamp from the log rather than DateTime.Now.
        // ══════════════════════════════════════════════════════════════
        private static readonly System.Text.RegularExpressions.Regex _cmtraceRx =
            new System.Text.RegularExpressions.Regex(
                @"<!\[LOG\[(?<msg>.*?)\]LOG\]!><time=""(?<time>[^""]+)""\s+date=""[^""]+""\s+component=""(?<comp>[^""]*)""\s+context=""[^""]+""\s+type=""(?<type>[^""]*)""\s+thread=""[^""]+""\s+file=""[^""]*"">",
                System.Text.RegularExpressions.RegexOptions.Singleline |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Extracts HH:mm:ss from CMTrace time field "08:00:12.579+330"
        private static string ParseCmTraceTime(string raw)
            => raw.Length >= 8 && raw[2] == ':' && raw[5] == ':' ? raw.Substring(0, 8) : raw;

        private long TailLogFile(string path, long pos, ref int lineCount)
        {
            if (!File.Exists(path)) return pos;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // File was truncated or recreated since the position was snapped -
                // a seek beyond EOF would silently read nothing forever. Restart.
                if (pos > fs.Length) pos = 0;
                fs.Seek(pos, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string chunk = sr.ReadToEnd();
                if (string.IsNullOrEmpty(chunk)) return fs.Position;
                long newPos = fs.Position;

                var matches = _cmtraceRx.Matches(chunk);
                int lastEnd = 0;

                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    // Plain text before this CMTrace entry
                    if (m.Index > lastEnd)
                    {
                        foreach (var pl in chunk.Substring(lastEnd, m.Index - lastEnd).Split('\n'))
                        {
                            string t = pl.Trim().Trim('\r');
                            if (!string.IsNullOrWhiteSpace(t))
                            {
                                AppendLog("\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + t);
                                lineCount++;
                            }
                        }
                    }
                    lastEnd = m.Index + m.Length;

                    string msg   = m.Groups["msg"].Value.Trim();
                    string ttype = m.Groups["type"].Value.Trim();
                    string time  = ParseCmTraceTime(m.Groups["time"].Value.Trim());
                    if (string.IsNullOrWhiteSpace(msg)) { lineCount++; continue; }

                    string prefix  = ttype == "2" ? "⚠  " : ttype == "3" ? "❌ " : "";
                    string display = prefix + msg;

                    // Multi-line messages: first line on timestamp row, rest indented
                    var msgLines  = display.Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries);
                    string first  = msgLines.Length > 0 ? msgLines[0] : display;
                    string rest   = msgLines.Length > 1
                        ? "\n" + string.Join("\n", msgLines.Skip(1).Select(l => "             " + l.TrimStart()))
                        : "";

                    string logLine = "[" + time + "] " + first + rest;
                    AppendLog("\n" + logLine);
                    lineCount++;
                }

                // Trailing plain text after last CMTrace entry
                if (lastEnd < chunk.Length)
                {
                    foreach (var pl in chunk.Substring(lastEnd).Split('\n'))
                    {
                        string t = pl.Trim().Trim('\r');
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            AppendLog("\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + t);
                            lineCount++;
                        }
                    }
                }

                return newPos;
            }
            catch { return pos; }
        }

        // ══════════════════════════════════════════════════════════════
        // CANCEL
        // ══════════════════════════════════════════════════════════════
        private bool ShowExecCancelDialog(string deployMode = "")
        {
            bool result = false;
            string modeLabel = string.IsNullOrEmpty(deployMode) ? "" : $" {deployMode}";
            string dialogTitle = $"Execute Package - {modeLabel} Cancel".Trim();
            var winBg     = Application.Current.TryFindResource("WindowBgBrush")   as Brush ?? Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? Brushes.LightGray;
            var win = new Window
            {
                Title = dialogTitle, Width = 460, MaxWidth = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false
            };
            var outer = new Border { Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black } };
            win.Content = outer;
            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            // Dark title bar - same as CreatePackage dialogs
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));
            Brush titleTxtBrush = Brushes.White;
            Brush titleSecBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            var txtPri = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? Brushes.Black;
            var txtSec = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var tb = new Border { Background = titleBarBg, BorderBrush = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63)), BorderThickness = new Thickness(0,0,0,1), CornerRadius = new CornerRadius(10,10,0,0), Padding = new Thickness(16,6,12,6) };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti = new TextBlock { Text = "▶", FontSize = 14, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            var tt = new TextBlock { Text = dialogTitle, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center };
            var tc = new Button { Content = "✕", FontSize = 11, Foreground = titleSecBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6,2,6,2) };
            tc.Click += (_, _) => win.Close();
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tc, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tc);
            tb.Child = tg; og.Children.Add(tb);

            var root = new Grid { Margin = new Thickness(8) };
            Grid.SetRow(root, 1); og.Children.Add(root);

            var boxBg  = Application.Current.TryFindResource("InputBgBrush")       as Brush ?? new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x55, 0x5C, 0x7A));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));

            var dg = new Grid();
            dg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            dg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            dg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            dg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border MkBox(string text, bool mono = false) { var b = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10,6,10,6) }; b.Child = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg, FontFamily = new FontFamily(mono ? "Consolas" : "Segoe UI") }; return b; }
            TextBlock MkLbl(string text) => new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };

            var l0 = MkLbl("Status"); var b0 = MkBox("Package execution is in progress.");
            var l2 = MkLbl("Action"); var b2 = MkBox("Do you want to cancel it?");
            Grid.SetRow(l0,0); Grid.SetColumn(l0,0); Grid.SetRow(b0,0); Grid.SetColumn(b0,1);
            Grid.SetRow(l2,2); Grid.SetColumn(l2,0); Grid.SetRow(b2,2); Grid.SetColumn(b2,1);
            dg.Children.Add(l0); dg.Children.Add(b0);
            dg.Children.Add(l2); dg.Children.Add(b2);

            Style? sec2 = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            var bg2 = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };
            var by = new Button { Content = "✔ Yes", Style = sec2, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(0,0,4,0) };
            ExecApplyPrimaryLook(by);
            by.Click += (_, _) => { result = true; win.Close(); };
            var bn = new Button { Content = "✕ No",  Style = sec2, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(4,0,0,0), IsDefault = true };
            bn.Click += (_, _) => { result = false; win.Close(); };
            bg2.Children.Add(by); bg2.Children.Add(bn);
            Grid.SetRow(bg2,4); Grid.SetColumn(bg2,1);
            dg.Children.Add(bg2);
            // ❓ icon in col 0 of the button row - matches CreatePackage cancel dialog pattern
            var cancelIcon = ExecDecisionIcon();
            Grid.SetRow(cancelIcon, 4); Grid.SetColumn(cancelIcon, 0);
            dg.Children.Add(cancelIcon);
            root.Children.Add(dg);
            win.ShowDialog();
            return result;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowExecCancelDialog(_activeDeployMode)) return;
            _cts?.Cancel();
            BtnCancelHeader.IsEnabled    = false;

            string? pidFilePath = _activePidFile;

            Task.Run(() =>
            {
                // 1. Kill the wrapper process (non-elevated)
                try
                {
                    if (_runningProcess != null && !_runningProcess.HasExited)
                        KillProcessTree(_runningProcess.Id);
                }
                catch { }

                // 2. Kill the elevated inner process using its PID from the pid file.
                //    The wrapper writes the inner PID immediately after Start-Process,
                //    so we wait briefly then read it.
                if (!string.IsNullOrEmpty(pidFilePath))
                {
                    int innerPid = -1;
                    // Wait up to 3s for the pid file to appear
                    for (int i = 0; i < 30 && innerPid < 0; i++)
                    {
                        System.Threading.Thread.Sleep(100);
                        try
                        {
                            if (File.Exists(pidFilePath))
                            {
                                string raw = File.ReadAllText(pidFilePath).Trim();
                                int.TryParse(raw, out innerPid);
                            }
                        }
                        catch { }
                    }

                    if (innerPid > 0)
                    {
                        // Kill the elevated inner process tree via runas elevated taskkill
                        try
                        {
                            var psi = new ProcessStartInfo("taskkill", $"/PID {innerPid} /T /F")
                            {
                                UseShellExecute = true,
                                Verb            = "runas",
                                WindowStyle     = ProcessWindowStyle.Hidden
                            };
                            Process.Start(psi)?.WaitForExit(5000);
                        }
                        catch { }
                    }
                }

                Dispatcher.BeginInvoke(new Action(() =>
                    Log("⚠  Execution cancelled. All processes terminated.")));
            });
        }

        private static void KillProcessTree(int pid)
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                    { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(3000);
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════
        // WAIT FOR PROCESS TREE EXIT
        // PSADT's Show-ADTInstallationPrompt spawns detached children.
        // We collect ALL descendant PIDs via WMI, then wait for every
        // one of them to exit before showing the result dialog.
        // Max wait: 10 minutes.
        // ══════════════════════════════════════════════════════════════
        private static void WaitForProcessTreeExit(int rootPid, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMinutes(10);

            // Collect all descendant PIDs via WMI parent chain
            static HashSet<int> GetDescendants(int root)
            {
                var all   = new HashSet<int>();
                var queue = new Queue<int>();
                queue.Enqueue(root);
                try
                {
                    // Build parent→children map from Win32_Process
                    var parentMap = new Dictionary<int, List<int>>();
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT ProcessId, ParentProcessId FROM Win32_Process");
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        int pid    = Convert.ToInt32(obj["ProcessId"]);
                        int parent = Convert.ToInt32(obj["ParentProcessId"]);
                        if (!parentMap.ContainsKey(parent)) parentMap[parent] = new List<int>();
                        parentMap[parent].Add(pid);
                    }
                    while (queue.Count > 0)
                    {
                        int cur = queue.Dequeue();
                        all.Add(cur);
                        if (parentMap.TryGetValue(cur, out var children))
                            foreach (int c in children) queue.Enqueue(c);
                    }
                }
                catch { all.Add(root); }
                return all;
            }

            // Snapshot descendants at start (includes prompt process spawned by PSADT)
            var pids = GetDescendants(rootPid);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                bool anyAlive = false;
                foreach (int pid in pids)
                {
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        if (!p.HasExited) { anyAlive = true; break; }
                    }
                    catch { /* process already gone - fine */ }
                }
                if (!anyAlive) return;
                System.Threading.Thread.Sleep(500);
            }
        }
    }

    /// <summary>Settings persisted in ExecutePackage.json.</summary>
    public class ExecutePackageConfig
    {
        /// <summary>
        /// "Yes" = copy the PSADT .log file into &lt;PackageFolder&gt;\Documents after each deployment.
        /// Any other value (or absent) = do not copy.
        /// </summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(LenientStringJsonConverter))]
        public string? CopyLogsToDocuments { get; set; }

        /// <summary>
        /// PSADT install command template, edited in the Settings dialog. The Execute Package page
        /// expands the bare script name in this into a full path for its PSADT Install box.
        /// </summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(LenientStringJsonConverter))]
        public string? PSADTInstallCommand { get; set; }

        /// <summary>
        /// PSADT uninstall command template, edited in the Settings dialog. Expanded the same way
        /// for the Execute Package page's PSADT Uninstall box.
        /// </summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(LenientStringJsonConverter))]
        public string? PSADTUninstallCommand { get; set; }

        /// <summary>
        /// PSADT repair command template, edited in the Settings dialog. Repair has no box on the
        /// page, so this is expanded and run directly when Repair is triggered.
        /// </summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(LenientStringJsonConverter))]
        public string? PSADTRepairCommand { get; set; }

        /// <summary>
        /// Runtime override - set by the auto-package flow from the app JSON so per-app
        /// flags take precedence over the global ExecutePackage.json value.
        /// </summary>
        public static string? RuntimeCopyLogsOverride { get; set; } = null;

        public bool ShouldCopyLogs =>
            string.Equals(RuntimeCopyLogsOverride ?? CopyLogsToDocuments, "Yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a JSON value into a string even when the file stores it as a bare boolean or number,
    /// e.g. "CopyLogsToDocuments": true. The Settings dialog already treats a JSON true as "Yes";
    /// without this the deserializer threw on that same file, the whole config load was abandoned,
    /// and the deploy silently fell back to the all-null default (= No) while the dialog still
    /// displayed Yes.
    /// </summary>
    public class LenientStringJsonConverter : System.Text.Json.Serialization.JsonConverter<string?>
    {
        public override string? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
                                     System.Text.Json.JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case System.Text.Json.JsonTokenType.String: return reader.GetString();
                case System.Text.Json.JsonTokenType.True:   return "Yes";
                case System.Text.Json.JsonTokenType.False:  return "No";
                case System.Text.Json.JsonTokenType.Number:
                    return reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case System.Text.Json.JsonTokenType.Null:   return null;
                default: reader.Skip(); return null;
            }
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value,
                                   System.Text.Json.JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else               writer.WriteStringValue(value);
        }
    }
}
