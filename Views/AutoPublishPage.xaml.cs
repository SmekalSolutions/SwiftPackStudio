// =====================================================================
//  AutoPublishPage.xaml.cs
//  SwiftPack Studio — Auto Package › Publish tab (self-contained)
//
//  UI matches PublishDeployPage exactly. Zero dependency on
//  PublishDeployPage or any other non-Auto feature.
// =====================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace SwiftPack.Views
{
    // ── Data carriers ─────────────────────────────────────────────────

    public sealed class AutoDetectionRule
    {
        public string RuleType       { get; set; } = "";
        public bool   IsDefault      { get; set; }
        public string MsiProductCode { get; set; } = "";
        public string MsiCheck       { get; set; } = "";
        public string MsiOperator    { get; set; } = "";
        public string MsiVersion     { get; set; } = "";
        public string FilePath       { get; set; } = "";
        public string FileName       { get; set; } = "";
        public string FileCheck      { get; set; } = "";
        public string FileOperator   { get; set; } = "";
        public string FileValue      { get; set; } = "";
        public bool   File32Bit      { get; set; }
        public string RegKeyPath     { get; set; } = "";
        public string RegValue       { get; set; } = "";
        public string RegCheck       { get; set; } = "";
        public string RegOperator    { get; set; } = "";
        public string RegValueData   { get; set; } = "";
        public bool   Reg32Bit       { get; set; }
    }

    public sealed class AutoAssignmentEntry
    {
        public string AssignType { get; set; } = "";
        public string GroupName  { get; set; } = "";
    }

    public sealed class AutoDependencyEntry
    {
        public string AppName     { get; set; } = "";
        public bool   AutoInstall { get; set; }
    }

    public sealed class AutoSupersedenceEntry
    {
        public string AppName           { get; set; } = "";
        public bool   UninstallPrevious { get; set; }
    }

    // ── Page ──────────────────────────────────────────────────────────

    public partial class AutoPublishPage : Page
    {
        private IAutoPackageContext? _context;
        private bool _wantMsi  = true;
        private bool _wantFile = true;
        private bool _wantReg  = true;

        // Maps tab index → (Button, StackPanel)
        private readonly List<(Button Btn, StackPanel Panel)> _tabs = new();

        // ── Exposed data properties ───────────────────────────────────
        public string ClientId           { get; private set; } = "";
        public string TenantId           { get; private set; } = "";
        public string IntunewinFilePath   { get; private set; } = "";

        /// <summary>Set by AutoPackagePage. Invoked when Save is clicked (manually or auto-triggered by PublishSettings).</summary>
        public Action? OnSaveRequested { get; set; } = null;

        // Fired (once per publish) after the publish-to-Intune PowerShell script
        // finishes — success, failure, or cancel. AutoPackagePage uses this as the
        // true terminus of the auto-flow when PublishToIntune=Yes.
        public Action? OnPublishComplete { get; set; } = null;

        // Exposed so the host can show start/finish/duration + log path in the
        // "Publish — Complete" status dialog.
        public string   PublishLogPath  { get; private set; } = "";
        public DateTime PublishStartTime { get; private set; }
        public DateTime PublishEndTime   { get; private set; }

        /// <summary>Programmatically clicks the Save button — triggered by PublishSettings checkbox.</summary>
        public void TriggerSave()
        {
            if (Dispatcher.CheckAccess())
                BtnSave_Click(this, new System.Windows.RoutedEventArgs());
            else
                Dispatcher.Invoke(() => BtnSave_Click(this, new System.Windows.RoutedEventArgs()));
        }
        public string AppName             { get; private set; } = "";
        public string AppVendor           { get; private set; } = "";
        public string AppVersion          { get; private set; } = "";
        public string Description         { get; private set; } = "";
        public string InformationURL      { get; private set; } = "";
        public string PrivacyURL          { get; private set; } = "";
        public string Developer           { get; private set; } = "";
        public string Owner               { get; private set; } = "";
        public string Notes               { get; private set; } = "";
        public string IconPath            { get; private set; } = "";
        public string Category            { get; private set; } = "";
        public string FeaturedApp         { get; private set; } = "";
        // These reflect the LIVE Program-tab textboxes when available, so that whatever
        // is shown (and possibly user-edited) is exactly what gets saved to the per-app
        // JSON. They fall back to the last value set during AutoFillFromPackage when the
        // textbox isn't realized yet. (Req 1–3: the saved JSON must match the UI.)
        private string _installCmdBacking   = "";
        private string _uninstallCmdBacking = "";
        public string InstallCmd
        {
            get => (TxtInstallCmd != null && !string.IsNullOrWhiteSpace(TxtInstallCmd.Text))
                       ? TxtInstallCmd.Text : _installCmdBacking;
            private set => _installCmdBacking = value;
        }
        public string UninstallCmd
        {
            get => (TxtUninstallCmd != null && !string.IsNullOrWhiteSpace(TxtUninstallCmd.Text))
                       ? TxtUninstallCmd.Text : _uninstallCmdBacking;
            private set => _uninstallCmdBacking = value;
        }
        public string InstallationTime          { get; private set; } = "";
        public string AllowAvailableUninstall   { get; private set; } = "";
        public string InstallBehavior           { get; private set; } = "";
        public string DeviceRestartBehavior     { get; private set; } = "";
        public string MinimumOperatingSystem    { get; private set; } = "";
        public string OperatingSystemArchitecture { get; private set; } = "";
        public string RulesFormat                       { get; private set; } = "";
        public string DetectionScriptFile               { get; private set; } = "";
        public string DetectionFileDateValue            { get; private set; } = "";   // resolved File date (Date created/modified)
        public string DetectionFileTimeValue            { get; private set; } = "";   // resolved File time (Date created/modified)
        public string DetectionFileSizeValue            { get; private set; } = "";   // resolved File size in MB (Size in MB)
        public bool   DetectionScriptEnforceSignature   { get; private set; }
        public bool   DetectionScriptRunAs32Bit         { get; private set; }
        public string DeploymentType                    { get; private set; } = "";
        public List<AutoDetectionRule>    DetectionRules { get; } = new();
        public List<AutoDependencyEntry>   Dependencies  { get; } = new();
        public List<AutoSupersedenceEntry> Supersedences { get; } = new();
        public List<AutoAssignmentEntry>   Assignments   { get; } = new();

        // ── Construction ──────────────────────────────────────────────

        public AutoPublishPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // ── View-lock: read-only page; step tabs + Cancel stay interactive ──
        private bool _viewLockApplied;
        private void ApplyViewLock()
        {
            if (_viewLockApplied) return;
            _viewLockApplied = true;

            // Lock all inputs (automation sets values by property). The 1–8 step
            // tab buttons remain clickable (LockInputs skips Buttons), so the user
            // can still navigate and VIEW each section.
            AutoViewLock.LockInputs(this);

            // Grey the kept action buttons (still enabled for automation).
            // Publish uses an accent "primary" style that always looks lit; make it
            // match the Save button (same style) so its idle background equals Save's
            // (Req 5). It only appears highlighted while actually publishing (see
            // BtnPublish_Click → AutoViewLock.Highlight).
            if (BtnPublish != null && BtnSavePD != null)
                BtnPublish.Style = BtnSavePD.Style;
            // Hidden from the user, but kept enabled so the automation can still fire them
            // (BtnPublish via RaiseEvent, BtnSavePD via BtnSave_Click).
            if (BtnPublish != null) { BtnPublish.IsEnabled = true; BtnPublish.Visibility = Visibility.Collapsed; }
            if (BtnSavePD  != null) { BtnSavePD.IsEnabled  = true; BtnSavePD.Visibility  = Visibility.Collapsed; }

            // PdBtnCancel stays fully active (enabled only while publishing).
            if (PdBtnCancel != null) PdBtnCancel.IsEnabled = false;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyViewLock();
            _tabs.Clear();
            _tabs.Add((BtnTab1, Tab1));
            _tabs.Add((BtnTab2, Tab2));
            _tabs.Add((BtnTab3, Tab3));
            _tabs.Add((BtnTab4, Tab4));
            _tabs.Add((BtnTab5, Tab5));
            _tabs.Add((BtnTab6, Tab6));
            _tabs.Add((BtnTab7, Tab7));
            _tabs.Add((BtnTab8, Tab8));

            ActivateTab(0); // default: App Info
        }

        public void SetContext(IAutoPackageContext context) => _context = context;
        public void EnsureReady() { }
        public void SetDetectionWants(bool wantMsi, bool wantFile, bool wantReg)
        {
            _wantMsi = wantMsi; _wantFile = wantFile; _wantReg = wantReg;
        }

        // ── Tab switching — same pattern as PublishDeployPage ─────────

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out int idx))
                ActivateTab(idx - 1);
        }

        private void ActivateTab(int idx)
        {
            var active   = TryFindResource("TabBtnActiveStyle") as Style;
            var inactive = TryFindResource("TabBtnStyle")       as Style;

            for (int i = 0; i < _tabs.Count; i++)
            {
                bool on = i == idx;
                if (active   != null && inactive != null)
                    _tabs[i].Btn.Style = on ? active : inactive;
                _tabs[i].Panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ── Log tab — public API for streaming script output ─────────

        /// <summary>
        /// Appends a line to the Log tab with optional colour.
        /// Thread-safe — may be called from any thread.
        /// colour: null = default grey, "green" = success, "red" = error, "yellow" = warning.
        /// </summary>
        // Start offset (in LogTextBox.Text) of the last "  Uploading: N%" line, or -1. Progress
        // lines arrive once per percent from AutoPackage_PublishToIntune.ps1; appended naively a
        // large upload writes a hundred of them. Instead each one REPLACES the previous, so the Log
        // shows a single live percentage - and the first non-progress line ends the run, so the
        // final "Uploading: 100%" stays in the log as the record of the upload.
        private int _uploadProgressStart = -1;
        private static bool IsUploadProgressLine(string t) =>
            t.TrimStart().StartsWith("Uploading: ", StringComparison.Ordinal) && t.Contains('%');

        public void AppendLog(string text, string? colour = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (LogTextBox == null) return;
                bool progress = IsUploadProgressLine(text);
                if (progress && _uploadProgressStart >= 0 && _uploadProgressStart <= LogTextBox.Text.Length)
                {
                    // Overwrite the tail from where the previous progress line began. Nothing can
                    // sit after it: every append lands here, and any non-progress append resets
                    // the marker below - so the tail IS the previous progress line.
                    LogTextBox.Text = LogTextBox.Text.Substring(0, _uploadProgressStart)
                                    + text + Environment.NewLine;
                }
                else
                {
                    if (progress) _uploadProgressStart = LogTextBox.Text.Length;
                    // TextBox: simply append line — colour not supported but text is correct
                    LogTextBox.AppendText(text + Environment.NewLine);
                }
                if (!progress) _uploadProgressStart = -1;
                LogTextBox.ScrollToEnd();
                // The TextBox sits in a StackPanel inside the page-level ScrollViewer,
                // so it grows to its full content height and its internal scrollbar
                // never engages - ScrollToEnd above is a no-op and the visible
                // scrollbar belongs to the OUTER ScrollViewer. Scroll that one to the
                // bottom too, but only while the Log tab is showing so appends never
                // yank the view on other tabs.
                if (Tab8 != null && Tab8.Visibility == Visibility.Visible)
                {
                    DependencyObject d = LogTextBox;
                    while (d != null && d is not ScrollViewer)
                        d = System.Windows.Media.VisualTreeHelper.GetParent(d);
                    (d as ScrollViewer)?.ScrollToBottom();
                }
            });
        }

        /// <summary>Clears all content from the Log tab.</summary>
        public void ClearLog()
        {
            Dispatcher.Invoke(() =>
            {
                if (LogTextBox != null) LogTextBox.Clear();
                _uploadProgressStart = -1;   // a stale offset must not survive into the next run
            });
        }

        /// <summary>Switches the Publish tab to the Log sub-tab (tab index 7 = "8 Log").</summary>
        public void ActivateLogTab()
        {
            Dispatcher.Invoke(() => ActivateTab(7)); // 0-based: Tab8 is index 7
        }

        /// <summary>
        /// Programmatically triggers the Publish button — called by AutoPackagePage
        /// when PublishToIntune=Yes in per-app JSON after uninstall completes.
        /// </summary>
        public void TriggerPublishButton()
        {
            if (Dispatcher.CheckAccess())
                BtnPublish?.RaiseEvent(new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.Button.ClickEvent));
            else
                Dispatcher.Invoke(() => BtnPublish?.RaiseEvent(new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.Button.ClickEvent)));
        }

        /// <summary>
        /// Explicitly sets the Intunewin file path, bypassing the directory scan in
        /// AutoFillFromPackage. Called by AutoPackagePage when the exact path is
        /// already known from the Intunewin creation event so the Publish button
        /// never fires with an empty path.
        /// </summary>
        public void SetIntunewinFilePath(string path)
        {
            void DoSet()
            {
                IntunewinFilePath = path;
                if (TxtIntunewinFile != null) TxtIntunewinFile.Text = path;
            }
            if (Dispatcher.CheckAccess()) DoSet();
            else Dispatcher.Invoke(DoSet);
        }

        // ── Detection method toggle ───────────────────────────────────

        private void DetectMethod_Changed(object sender, RoutedEventArgs e)
        {
            bool script = RbDetectScript?.IsChecked == true;
            if (PanelManualRules  != null) PanelManualRules.Visibility  = script ? Visibility.Collapsed : Visibility.Visible;
            if (PanelDetectScript != null) PanelDetectScript.Visibility = script ? Visibility.Visible  : Visibility.Collapsed;
        }

        // ── Toolbar handlers ──────────────────────────────────────────

        // Log file path for current publish run — written to %TEMP%
        private string _publishLogPath = "";
        private System.Diagnostics.Process? _publishProcess;   // live publish process (for Cancel)
        private volatile bool _publishCancelled;               // set when user clicks Cancel

        private void BtnPublish_Click(object sender, RoutedEventArgs e)
        {
            // Disable button to prevent double-click during publish
            BtnPublish.IsEnabled = false;
            _publishCancelled = false;
            // Highlight Publish only while it is actively running (Req 1).
            AutoViewLock.Highlight(BtnPublish);
            // Cancel becomes active while publishing
            if (PdBtnCancel != null) PdBtnCancel.IsEnabled = true;
            // Show the thin progress bar below the header during publish-to-Intune.
            if (PublishProgressBar != null) { PublishProgressBar.IsIndeterminate = true; PublishProgressBar.Visibility = Visibility.Visible; }
            PublishStartTime = DateTime.Now;
            // Switch to Log tab so user sees output immediately
            ActivateLogTab();
            ClearLog();
            // Create log file in %TEMP%
            // Write into the shared per-package log (revision-suffixed) so Package, Execute,
            // Intunewin and Publish all append to ONE file. Derive the package name from the
            // .intunewin filename (e.g. Smekal_FileCompare_1.0_01.intunewin).
            string _pkgBase = "";
            try
            {
                string _iwf = TxtIntunewinFile?.Text?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(_iwf))
                    _pkgBase = System.IO.Path.GetFileNameWithoutExtension(_iwf);
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            _publishLogPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                !string.IsNullOrWhiteSpace(_pkgBase)
                    ? $"AutoPackage_{_pkgBase}.log"
                    : $"PublishToIntune_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            PublishLogPath = _publishLogPath;
            AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Starting publish to Intune...", "yellow");
            AppendLog("  Log file : " + _publishLogPath);
            // Run async so UI stays responsive
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try   { await RunPublishScriptAsync(); }
                catch (Exception ex) { AppendLog("ERROR: " + ex.Message, "red"); }
                finally
                {
                    SavePublishLog();
                    Dispatcher.Invoke(() =>
                    {
                        BtnPublish.IsEnabled = true;
                        AutoViewLock.Unhighlight(BtnPublish);   // reverts to Save-matching style
                        if (PdBtnCancel != null) PdBtnCancel.IsEnabled = false;
                        if (PublishProgressBar != null) { PublishProgressBar.Visibility = Visibility.Collapsed; PublishProgressBar.IsIndeterminate = false; }
                    });
                    // Publish-to-Intune finished (success/fail/cancel) — this is the
                    // terminal step of the auto-flow when PublishToIntune=Yes.
                    PublishEndTime = DateTime.Now;
                    try { OnPublishComplete?.Invoke(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
            });
        }

        // ── Cancel publish-to-Intune (Req 1 + Req 2) ──────────────────────
        // Stops the running publish: kills the PowerShell process so no further
        // Intune action happens, and flags the run as cancelled so the chain
        // does not continue to a next action.
        private void PdBtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _publishCancelled = true;
            try
            {
                var p = _publishProcess;
                if (p != null && !p.HasExited)
                {
                    try { KillProcessTree(p.Id); } catch { try { p.Kill(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Cancel requested — stopping publish to Intune…", "yellow");
            if (PdBtnCancel != null) PdBtnCancel.IsEnabled = false;
        }

        // Kill a process and its children (best-effort) via taskkill /T.
        private static void KillProcessTree(int pid)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // ── Sign-in window watcher ────────────────────────────────────────────
        // Interactive sign-in opens a browser window titled "Sign in to your account".
        // The system browser gives MSAL no signal when the user closes that window, so the
        // script would otherwise sit on its fallback timeout. Instead we find that window and
        // cancel the publish the instant it is destroyed. Best-effort: if the window can't be
        // identified (localised title, unusual browser, or opened as a background tab) it simply
        // falls back to the script's timeout. It only acts while _signInPhase is true (i.e. before
        // a token is obtained), so a successful sign-in is never cancelled.
        private volatile bool _signInPhase;
        private System.Threading.Thread? _signInWatcher;

        private delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, System.IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(System.IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowTextLength(System.IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(System.IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(System.IntPtr hWnd);

        private static System.IntPtr FindSignInWindow()
        {
            System.IntPtr found = System.IntPtr.Zero;
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    int len = GetWindowTextLength(h);
                    if (len <= 0) return true;
                    var sb = new System.Text.StringBuilder(len + 1);
                    GetWindowText(h, sb, sb.Capacity);
                    if (sb.ToString().IndexOf("Sign in to your account", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = h;
                        return false; // stop enumerating
                    }
                    return true;
                }, System.IntPtr.Zero);
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return found;
        }

        private static string GetWindowTitleText(System.IntPtr hWnd)
        {
            try
            {
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return "";
                var sb = new System.Text.StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        private void StartSignInWatcher()
        {
            if (_signInWatcher != null && _signInWatcher.IsAlive) return;
            _signInWatcher = new System.Threading.Thread(() =>
            {
                // Wait for the sign-in window to appear (up to ~25s).
                System.IntPtr hwnd = System.IntPtr.Zero;
                var appear = System.Diagnostics.Stopwatch.StartNew();
                while (_signInPhase && hwnd == System.IntPtr.Zero && appear.Elapsed.TotalSeconds < 25)
                {
                    hwnd = FindSignInWindow();
                    if (hwnd == System.IntPtr.Zero) System.Threading.Thread.Sleep(400);
                }
                if (hwnd == System.IntPtr.Zero) return; // not found — fall back to the script timeout

                // Watch that window. While it's open, poll its title: a SUCCESSFUL sign-in navigates
                // the browser to the "Authentication Successful" redirect page. Once that appears the
                // auth code has already been delivered to MSAL and Connect will finish - so we must
                // NOT cancel, even if the user then closes that page immediately.
                bool loginSucceeded = false;
                while (_signInPhase && IsWindow(hwnd))
                {
                    if (GetWindowTitleText(hwnd).IndexOf("Authentication Successful", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        loginSucceeded = true;
                        break; // success page reached — stop watching, never cancel
                    }
                    System.Threading.Thread.Sleep(150);
                }
                if (!_signInPhase || loginSucceeded) return; // connected or succeeded — no cancel

                // Window gone while still on the sign-in page and not yet connected. A redirect
                // delivered just before the close still lets Connect finish, so wait a grace and keep
                // checking _signInPhase before cancelling: a real cancel keeps _signInPhase true
                // (Connect hangs on a redirect that never arrives); a completed sign-in flips it off.
                var grace = System.Diagnostics.Stopwatch.StartNew();
                while (_signInPhase && grace.Elapsed.TotalSeconds < 6) System.Threading.Thread.Sleep(200);
                if (!_signInPhase) return; // Connect completed during grace — do not cancel

                var p = _publishProcess;
                if (p != null && !p.HasExited)
                {
                    AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Sign-in window closed - publish cancelled.", "red");
                    _publishCancelled = true;
                    try { KillProcessTree(p.Id); } catch { try { p.Kill(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }
                }
            });
            _signInWatcher.IsBackground = true;
            _signInWatcher.Start();
        }

        private void SavePublishLog()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_publishLogPath)) return;
                string logContent = "";
                Dispatcher.Invoke(() => logContent = LogTextBox?.Text ?? "");
                // Append (not overwrite) so Publish output joins the shared per-package log
                // alongside Package, Execute and Intunewin sections.
                string header =
                    Environment.NewLine +
                    "════════════════════════════════════════════════════════════" + Environment.NewLine +
                    $"  PUBLISH TO INTUNE - {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                    "════════════════════════════════════════════════════════════" + Environment.NewLine;
                System.IO.File.AppendAllText(_publishLogPath, header + logContent + Environment.NewLine, System.Text.Encoding.UTF8);
                AppendLog("  Log saved: " + _publishLogPath);
            }
            catch (Exception ex)
            {
                AppendLog("WARNING: Could not save log file: " + ex.Message, "yellow");
            }
        }

        /// <summary>
        /// Resolves credentials, builds args, launches AutoPackage_PublishToIntune.ps1
        /// and streams its output live to the Log tab.
        /// </summary>
        private async System.Threading.Tasks.Task RunPublishScriptAsync()
        {
            // ── 1. Validate script exists ────────────────────────────────────
            string scriptPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Smekal", "SwiftPack Studio", "Templates", "AutoPackage_PublishToIntune.ps1");

            if (!System.IO.File.Exists(scriptPath))
            {
                AppendLog("ERROR: Script not found: " + scriptPath, "red");
                AppendLog("       Place AutoPackage_PublishToIntune.ps1 in:", "red");
                AppendLog("       " + System.IO.Path.GetDirectoryName(scriptPath), "red");
                return;
            }

            // ── 2. Resolve JsonPath from IntunewinFilePath (.intunewin → .json) ──
            string intunewinPath = "";
            Dispatcher.Invoke(() => intunewinPath = TxtIntunewinFile?.Text?.Trim() ?? "");

            if (string.IsNullOrWhiteSpace(intunewinPath) || !System.IO.File.Exists(intunewinPath))
            {
                AppendLog("ERROR: Intunewin file not set or not found: " + intunewinPath, "red");
                AppendLog("       Set the Intunewin File path in the Publish tab.", "red");
                return;
            }

            string jsonPath = System.IO.Path.ChangeExtension(intunewinPath, ".json");
            if (!System.IO.File.Exists(jsonPath))
            {
                AppendLog("ERROR: Intune JSON settings file not found: " + jsonPath, "red");
                AppendLog("       Click Save first to generate the JSON file.", "red");
                return;
            }

            AppendLog("  Script   : " + scriptPath);
            AppendLog("  JsonPath : " + jsonPath);

            // ── 3. Read TenantId and ClientId from AutoPackage.json ──────────
            string autoPackagePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Smekal", "SwiftPack Studio", "Templates", "AutoPackage.json");

            string tenantId = "";
            string clientId = "";

            // ── 3. Read AutoPackage.json → TenantId (always) + ClientId + IntuneAuthPath ──
            string intuneAuthFilePath = "";
            AutoPackageCfgForPublish? apCfg = null;
            if (System.IO.File.Exists(autoPackagePath))
            {
                try
                {
                    var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    apCfg = System.Text.Json.JsonSerializer.Deserialize<AutoPackageCfgForPublish>(
                        System.IO.File.ReadAllText(autoPackagePath), opts);
                    // TenantId — always from AutoPackage.json (single source)
                    tenantId           = apCfg?.IntuneSettings?.TenantId ?? "";
                    // ClientId — from AutoPackage.json (may be overridden by Credential Manager below)
                    clientId           = apCfg?.IntuneSettings?.ClientId ?? "";
                    intuneAuthFilePath = apCfg?.IntuneAuthPath            ?? "";
                    AppendLog("  TenantId : " + (string.IsNullOrEmpty(tenantId) ? "[not set]" : tenantId));
                    AppendLog("  ClientId : " + (string.IsNullOrEmpty(clientId) ? "[not set — will check Credential Manager]" : "[set from AutoPackage.json]"));
                }
                catch (Exception ex)
                {
                    AppendLog("WARNING: Could not read AutoPackage.json: " + ex.Message, "yellow");
                }
            }
            else
            {
                AppendLog("WARNING: AutoPackage.json not found at: " + autoPackagePath, "yellow");
            }

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                AppendLog("ERROR: TenantId is empty in AutoPackage.json IntuneSettings.", "red");
                AppendLog("       Set IntuneSettings.TenantId in AutoPackage.json.", "red");
                return;
            }

            // ── 4. Resolve ClientId and ClientSecret ─────────────────────────
            string clientSecret = "";

            // 4a. Credential Manager — ClientId (Username) and ClientSecret (Password)
            try
            {
                var cred = CredentialManager.ReadCredential("SwiftPack_Intune");
                if (cred != null)
                {
                    if (!string.IsNullOrWhiteSpace(cred.UserName))
                    {
                        clientId = cred.UserName;
                        AppendLog("  ClientId : overridden from Credential Manager (SwiftPack_Intune)");
                    }
                    if (!string.IsNullOrWhiteSpace(cred.Password))
                    {
                        clientSecret = cred.Password;
                        AppendLog("  Secret   : ClientSecret from Credential Manager");
                    }
                }
            }
            catch { /* Credential Manager not available — continue */ }

            // Validate ClientId (after Credential Manager check)
            if (string.IsNullOrWhiteSpace(clientId))
            {
                AppendLog("ERROR: ClientId is empty. Set it in AutoPackage.json IntuneSettings.ClientId", "red");
                AppendLog("       or store in Credential Manager: cmdkey /generic:SwiftPack_Intune /user:<ClientId> /pass:<ClientSecret>", "red");
                return;
            }

            // 4b. ClientSecret from IntuneAuth.json (if IntuneAuthPath set and Credential Manager had no secret)
            if (string.IsNullOrWhiteSpace(clientSecret) && !string.IsNullOrWhiteSpace(intuneAuthFilePath))
            {
                if (System.IO.File.Exists(intuneAuthFilePath))
                {
                    try
                    {
                        var opts2 = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var authCfg = System.Text.Json.JsonSerializer.Deserialize<IntuneAuthConfig>(
                            System.IO.File.ReadAllText(intuneAuthFilePath), opts2);
                        if (!string.IsNullOrWhiteSpace(authCfg?.ClientSecret))
                        {
                            clientSecret = authCfg!.ClientSecret!;
                            AppendLog("  Secret   : ClientSecret from IntuneAuth.json (" + intuneAuthFilePath + ")");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog("WARNING: Could not read IntuneAuth.json: " + ex.Message, "yellow");
                    }
                }
                else
                {
                    AppendLog("WARNING: IntuneAuthPath set but file not found: " + intuneAuthFilePath, "yellow");
                }
            }

            // Reuse the shared top-bar Intune session when signed in, so publishing does not
            // prompt for a second browser sign-in. Falls back to the script's own
            // Connect-MSIntuneGraph (below) when the shared session is not signed in.
            if (IntuneSession.IsConnected)
            {
                await RunPublishInSharedSessionAsync(scriptPath, jsonPath, tenantId, clientId);
                return;
            }

            // 4c. Fall back to interactive login
            if (string.IsNullOrWhiteSpace(clientSecret))
                AppendLog("  Auth     : Interactive browser login (no ClientSecret found)");

            // ── 5. Build PowerShell arguments ────────────────────────────────
            var sb = new System.Text.StringBuilder();
            sb.Append($"-ExecutionPolicy Bypass -NonInteractive -File \"{scriptPath}\"");
            sb.Append($" -JsonPath \"{jsonPath}\"");
            sb.Append($" -TenantId \"{tenantId}\"");
            sb.Append($" -ClientId \"{clientId}\"");
            if (!string.IsNullOrWhiteSpace(clientSecret))
                sb.Append($" -ClientSecret \"{clientSecret}\"");

            AppendLog("");
            AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Launching script...", "yellow");

            // ── 6. Launch and stream output ──────────────────────────────────
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = sb.ToString(),
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = System.Text.Encoding.UTF8,
            };

            using var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            _publishProcess = proc;
            bool relationshipsFailed = false;

            proc.OutputDataReceived += (_, de) =>
            {
                if (de.Data == null) return;
                string line = de.Data;
                // The app can upload OK while Intune rejects the dependency/supersedence
                // relationships (the script only warns and continues), so catch it here.
                if (line.IndexOf("Failed to post relationships", StringComparison.OrdinalIgnoreCase) >= 0)
                    relationshipsFailed = true;
                // Sign-in window watcher: active only between "Connecting to Intune" and the point a
                // token is (or isn't) obtained, so we can cancel instantly when the user closes the
                // sign-in window (and never cancel a successful sign-in).
                if (line.Contains("Connecting to Intune"))
                {
                    _signInPhase = true;
                    StartSignInWatcher();
                }
                else if (line.Contains("Connected to Intune") || line.Contains("Building detection rules")
                         || line.Contains("access_denied") || line.Contains("Authentication token was not found")
                         || line.Contains("Publish aborted"))
                {
                    _signInPhase = false;
                }
                string colour = line.StartsWith("WARNING") ? "yellow"
                              : line.StartsWith("ERROR")   ? "red"
                              : line.Contains("successfully") || line.StartsWith("DONE") ? "green"
                              : null;
                AppendLog(line, colour);
            };

            proc.ErrorDataReceived += (_, de) =>
            {
                if (de.Data == null) return;
                AppendLog(de.Data, "red");
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await System.Threading.Tasks.Task.Run(() => proc.WaitForExit());
            _publishProcess = null;
            _signInPhase = false;   // stop the sign-in watcher

            if (_publishCancelled)
            {
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publish cancelled by user.", "yellow");
            }
            else if (relationshipsFailed)
            {
                AutoFlowStatus.Set(AutoFlowStatus.PublishToIntune, AutoFlowStatus.State.Failed);
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] The app uploaded, but Intune rejected the dependencies/supersedences - they were NOT added. See the reason in the Log above.", "red");
            }
            else if (proc.ExitCode == 0)
            {
                AutoFlowStatus.Set(AutoFlowStatus.PublishToIntune, AutoFlowStatus.State.Ok);
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publish completed successfully.", "green");
            }
            else
            {
                AutoFlowStatus.Set(AutoFlowStatus.PublishToIntune, AutoFlowStatus.State.Failed);
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publish failed with exit code: " + proc.ExitCode, "red");
                AppendLog("       Check the Log tab for details.", "red");
            }
        }

        // Runs the publish script inside the shared IntuneSession (already connected via the
        // top-bar "Sign in to Intune"), passing -UseExistingConnection so the script skips its
        // own Connect-MSIntuneGraph. No browser prompt, no separate process.
        private async System.Threading.Tasks.Task RunPublishInSharedSessionAsync(
            string scriptPath, string jsonPath, string tenantId, string clientId)
        {
            AppendLog("  Auth     : Using the shared Intune sign-in (no browser prompt)");
            AppendLog("");
            AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publishing (existing connection)...", "yellow");

            string sp  = scriptPath.Replace("'", "''");
            string jp  = jsonPath.Replace("'", "''");
            string tid = tenantId.Replace("'", "''");
            string cid = clientId.Replace("'", "''");
            string cmd =
                "$global:LASTEXITCODE = 0; try { " +
                $"& '{sp}' -JsonPath '{jp}' -TenantId '{tid}' -ClientId '{cid}' -UseExistingConnection " +
                "} catch { Write-Host ('ERROR: ' + $_.Exception.Message); $global:LASTEXITCODE = 1 }; " +
                "if ($null -eq $LASTEXITCODE) { $global:LASTEXITCODE = 0 }";

            int exit = -1;
            bool published = false;
            bool relFailed = false;
            try
            {
                exit = await IntuneSession.Session!.RunAsync(cmd, line =>
                {
                    // The script prints "DONE: ... uploaded successfully." on its success path.
                    // A script run via '&' inside a live session does not return a reliable
                    // $LASTEXITCODE, so success is detected from this marker (as the main
                    // Publish page does) rather than from the exit code alone.
                    if (line.StartsWith("DONE") || line.IndexOf("uploaded successfully", StringComparison.OrdinalIgnoreCase) >= 0)
                        published = true;
                    // The app can upload OK while Intune rejects the dependency/supersedence
                    // relationships. The script only warns and continues, so catch it here.
                    if (line.IndexOf("Failed to post relationships", StringComparison.OrdinalIgnoreCase) >= 0)
                        relFailed = true;
                    string? colour = line.StartsWith("WARNING") ? "yellow"
                                   : line.StartsWith("ERROR")   ? "red"
                                   : (line.Contains("successfully") || line.StartsWith("DONE")) ? "green"
                                   : null;
                    AppendLog(line, colour);
                }, 1800000);
            }
            catch (Exception ex)
            {
                AppendLog("EXCEPTION: " + ex.Message, "red");
                if (IntuneSession.Session == null || !IntuneSession.Session.IsAlive)
                {
                    IntuneSession.SetConnected(false);
                    AppendLog("The shared Intune session ended. Sign in again (top right), then publish.", "red");
                }
            }

            if (_publishCancelled)
            {
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publish cancelled by user.", "yellow");
            }
            else if (relFailed)
            {
                AutoFlowStatus.Set(AutoFlowStatus.PublishToIntune, AutoFlowStatus.State.Failed);
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] The app uploaded, but Intune rejected the dependencies/supersedences - they were NOT added. See the reason in the Log above.", "red");
            }
            else if (published || exit == 0)
            {
                AutoFlowStatus.Set(AutoFlowStatus.PublishToIntune, AutoFlowStatus.State.Ok);
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publish completed successfully.", "green");
            }
            else
            {
                AutoFlowStatus.Set(AutoFlowStatus.PublishToIntune, AutoFlowStatus.State.Failed);
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publish failed with exit code: " + exit, "red");
                AppendLog("       Check the Log tab for details.", "red");
            }
        }

        // ── Minimal DTOs for reading AutoPackage.json and ClientSecret.json ──
        private class AutoPackageCfgForPublish
        {
            [System.Text.Json.Serialization.JsonPropertyName("IntuneAuthPath")]
            public string? IntuneAuthPath { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("IntuneSettings")]
            public IntuneSettingsForPublish? IntuneSettings { get; set; }
        }

        private class IntuneSettingsForPublish
        {
            [System.Text.Json.Serialization.JsonPropertyName("TenantId")]
            public string? TenantId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("ClientId")]
            public string? ClientId { get; set; }
        }

        // IntuneAuth.json — ClientSecret only, stored in secured/network location
        private class IntuneAuthConfig
        {
            [System.Text.Json.Serialization.JsonPropertyName("ClientSecret")]
            public string? ClientSecret { get; set; }
        }

        // ── Windows Credential Manager helper ─────────────────────────────
        private static class CredentialManager
        {
            [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredReadW",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            private static extern bool CredRead(string target, uint type, uint flags,
                out IntPtr credential);

            [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredFree")]
            private static extern void CredFree(IntPtr buffer);

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential, CharSet =
                System.Runtime.InteropServices.CharSet.Unicode)]
            private struct CREDENTIAL
            {
                public uint     Flags;
                public uint     Type;
                public string   TargetName;
                public string   Comment;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
                public uint     CredentialBlobSize;
                public IntPtr   CredentialBlob;
                public uint     Persist;
                public uint     AttributeCount;
                public IntPtr   Attributes;
                public string   TargetAlias;
                public string   UserName;
            }

            public record Credential(string UserName, string Password);

            public static Credential? ReadCredential(string target)
            {
                try
                {
                    if (!CredRead(target, 1 /*CRED_TYPE_GENERIC*/, 0, out IntPtr ptr)) return null;
                    try
                    {
                        var cred = System.Runtime.InteropServices.Marshal.PtrToStructure<CREDENTIAL>(ptr);
                        string pwd = cred.CredentialBlobSize > 0
                            ? System.Runtime.InteropServices.Marshal.PtrToStringUni(
                                cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2)) ?? ""
                            : "";
                        return new Credential(cred.UserName ?? "", pwd);
                    }
                    finally { CredFree(ptr); }
                }
                catch { return null; }
            }
        }


        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            OnSaveRequested?.Invoke();
        }
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) { /* future */ }
        private void BtnClear_Click(object sender, RoutedEventArgs e)   => ClearAll();

        private void BtnBrowseIntunewin_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Intunewin files (*.intunewin)|*.intunewin|All files (*.*)|*.*",
                Title  = "Select Intunewin file"
            };
            if (dlg.ShowDialog() == true)
            {
                IntunewinFilePath = dlg.FileName;
                if (TxtIntunewinFile != null) TxtIntunewinFile.Text = IntunewinFilePath;
            }
        }

        private void BtnClearIntunewin_Click(object sender, RoutedEventArgs e)
        {
            IntunewinFilePath = "";
            if (TxtIntunewinFile != null) TxtIntunewinFile.Text = "";
        }

        private void BtnBrowseIcon_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files (*.png;*.ico)|*.png;*.ico|All files (*.*)|*.*",
                Title  = "Select Logo"
            };
            if (dlg.ShowDialog() == true && TxtIconPath != null)
                TxtIconPath.Text = dlg.FileName;
        }

        private void BtnBrowseDetectScript_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PowerShell scripts (*.ps1)|*.ps1|All files (*.*)|*.*",
                Title  = "Select detection script"
            };
            if (dlg.ShowDialog() == true && TxtDetectScriptPath != null)
                TxtDetectScriptPath.Text = dlg.FileName;
        }

        private void FieldClear_PD_Click(object sender, RoutedEventArgs e)
        {
            // Each clear button's Tag = the x:Name of the field it clears
            // Since fields are inside templates, use visual tree walk
            if (sender is Button btn)
            {
                var tb = FindTextBoxSibling(btn);
                if (tb != null) tb.Text = "";
            }
        }

        private static TextBox? FindTextBoxSibling(Button btn)
        {
            if (btn.Parent is Panel p)
                return p.Children.OfType<TextBox>().FirstOrDefault();
            if (btn.Parent is Grid g)
                return g.Children.OfType<TextBox>().FirstOrDefault();
            return null;
        }

        // ── Assignment group add stubs ─────────────────────────────────

        private void PromptAddGroup(string assignType, StackPanel? list)
        {
            // In Auto flow groups are pre-populated from JSON.
            // This stub satisfies the click handler requirement.
        }

        // ══════════════════════════════════════════════════════════════
        //  AutoFillFromPackage
        // ══════════════════════════════════════════════════════════════

        public void AutoFillFromPackage(
            string                      packageFolder,
            string                      appName          = "",
            string                      appVendor        = "",
            string                      appVersion       = "",
            string                      description      = "",
            AutoPackageAppEntry?         perApp          = null,
            AutoIntuneSettingsOverride?  ov2             = null,
            AutoIntuneSettings?          settings        = null,
            string[]?                    depNames        = null,
            string[]?                    depAutoInstall  = null,
            string[]?                    supNames        = null,
            string[]?                    supUninstallPrev = null,
            string                       deploymentType  = "",
            string                       sourceAppName   = "")   // ← actual installer display name for registry lookup
        {
            void DoFill()
            {
                var p = perApp;
                var s = settings ?? new AutoIntuneSettings();

                // Intunewin path
                string? intunewin = null;
                if (!string.IsNullOrWhiteSpace(packageFolder) && Directory.Exists(packageFolder))
                {
                    try
                    {
                        string intunePath = IOPath.Combine(packageFolder, "Intune");
                        intunewin = Directory.Exists(intunePath)
                            ? Directory.GetFiles(intunePath, "*.intunewin", SearchOption.TopDirectoryOnly).FirstOrDefault()
                            : Directory.GetFiles(packageFolder, "*.intunewin", SearchOption.AllDirectories).FirstOrDefault();
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
                IntunewinFilePath = intunewin ?? "";
                SetTxt(TxtIntunewinFile, IntunewinFilePath);

                // Auth
                ClientId = s.ClientId;
                TenantId = s.TenantId;

                // ── 1 App Info ────────────────────────────────────────
                AppName        = R(ov2?.IntuneAppName,    s.IntuneAppName,    appName);
                AppVendor      = R(ov2?.IntuneAppVendor,  s.IntuneAppVendor,  appVendor);
                AppVersion     = R(ov2?.IntuneAppVersion, s.IntuneAppVersion, appVersion);
                Description    = R(p?.Description,    ov2?.Description,    description);
                InformationURL = R(p?.InformationURL, ov2?.InformationURL, s.InformationURL);
                PrivacyURL     = R(p?.PrivacyURL,     ov2?.PrivacyURL,     s.PrivacyURL);
                Developer      = R(p?.Developer,      ov2?.Developer,      s.Developer);
                Owner          = R(p?.Owner,          ov2?.Owner,          s.Owner);
                Notes          = R(p?.Notes,          ov2?.Notes,          s.Notes);
                IconPath       = R(p?.Logo,           ov2?.Logo,           s.Logo ?? "");
                Category       = R(p?.IntuneCategory, ov2?.Category,       s.Category);
                FeaturedApp    = R(p?.FeaturedApp,    ov2?.FeaturedApp,    s.FeaturedApp);

                SetTxt(TxtAppName,     AppName);
                SetTxt(TxtAppVendor,   AppVendor);
                SetTxt(TxtAppVersion,  AppVersion);
                SetTxt(TxtDescription, Description);
                SetTxt(TxtInfoUrl,     InformationURL);
                SetTxt(TxtPrivacyUrl,  PrivacyURL);
                SetTxt(TxtDeveloper,   Developer);
                SetTxt(TxtOwner,       Owner);
                SetTxt(TxtNotes,       Notes);
                SetTxt(TxtIconPath,    IconPath);
                SetCmb(CmbCategory,    Category);
                SetCmb(CmbFeatured,    FeaturedApp);

                // ── 2 Program ─────────────────────────────────────────
                // Resolve the .msi in the package (needed for Req 3 derivation).
                string? msiFileForCmd = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(packageFolder) && Directory.Exists(packageFolder))
                        msiFileForCmd = Directory.GetFiles(packageFolder, "*.msi", SearchOption.AllDirectories).FirstOrDefault();
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                // Req 1–4: choose Install/Uninstall commands based on PSADT template
                // presence, explicit JSON commands, or derive from installer + silent args.
                var (derivedInstall, derivedUninstall) = BuildProgramCommands(p, msiFileForCmd, packageFolder);
                // Fall back to the legacy resolution (override/settings) only when the
                // requirement-driven derivation produced nothing.
                InstallCmd              = !string.IsNullOrWhiteSpace(derivedInstall)
                                            ? derivedInstall
                                            : R(p?.InstallCommand,   ov2?.InstallCommand,   s.InstallCommand);
                UninstallCmd            = !string.IsNullOrWhiteSpace(derivedUninstall)
                                            ? derivedUninstall
                                            : R(p?.UninstallCommand, ov2?.UninstallCommand, s.UninstallCommand);
                InstallationTime        = R(p?.InstallationTime,        ov2?.InstallationTime,        s.InstallationTime);
                AllowAvailableUninstall = R(p?.AllowAvailableUninstall, ov2?.AllowAvailableUninstall, s.AllowAvailableUninstall);
                InstallBehavior         = R(p?.InstallBehavior,         ov2?.InstallBehavior,         s.InstallBehavior);
                DeviceRestartBehavior   = R(p?.DeviceRestartBehaviour,  ov2?.DeviceRestartBehaviour,  s.ResolvedRestartBehavior);

                SetTxt(TxtInstallCmd,   InstallCmd);
                SetTxt(TxtUninstallCmd, UninstallCmd);
                if (!string.IsNullOrWhiteSpace(InstallationTime))
                    SetTxt(TxtInstallTime, InstallationTime);

                if (!string.IsNullOrWhiteSpace(AllowAvailableUninstall))
                {
                    bool yes = AllowAvailableUninstall.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    if (RbUninstallYes != null) RbUninstallYes.IsChecked = yes;
                    if (RbUninstallNo  != null) RbUninstallNo.IsChecked  = !yes;
                }
                if (!string.IsNullOrWhiteSpace(InstallBehavior))
                {
                    bool user = InstallBehavior.Equals("User", StringComparison.OrdinalIgnoreCase);
                    if (RbInstallUser   != null) RbInstallUser.IsChecked   = user;
                    if (RbInstallSystem != null) RbInstallSystem.IsChecked = !user;
                }
                SetCmb(CmbRestartBehavior, DeviceRestartBehavior);

                // ── 3 Requirements ────────────────────────────────────
                MinimumOperatingSystem      = R(p?.MinimumOperatingSystem,      ov2?.MinimumOperatingSystem,      s.ResolvedMinimumOs);
                OperatingSystemArchitecture = R(p?.OperatingSystemArchitecture, ov2?.OperatingSystemArchitecture, s.OperatingSystemArchitecture);
                SetCmb(CmbMinOs, MinimumOperatingSystem);
                if (!string.IsNullOrWhiteSpace(OperatingSystemArchitecture))
                {
                    if (ChkX86   != null) ChkX86.IsChecked   = OperatingSystemArchitecture.Contains("32",  StringComparison.OrdinalIgnoreCase);
                    if (ChkX64   != null) ChkX64.IsChecked   = OperatingSystemArchitecture.Contains("64",  StringComparison.OrdinalIgnoreCase);
                    if (ChkArm64 != null) ChkArm64.IsChecked = OperatingSystemArchitecture.Contains("ARM", StringComparison.OrdinalIgnoreCase);
                }

                // ── 4 Detection ───────────────────────────────────────
                BuildAndRenderDetectionRules(p, ov2, s, packageFolder, sourceAppName);

                // ── 5 Dependencies ────────────────────────────────────
                Dependencies.Clear();
                if (DependenciesList != null)
                {
                    DependenciesList.Children.Clear();
                    bool hasDeps = depNames != null && depNames.Any(d => !string.IsNullOrWhiteSpace(d));
                    if (hasDeps)
                    {
                        for (int i = 0; i < depNames!.Length; i++)
                        {
                            if (string.IsNullOrWhiteSpace(depNames[i])) continue;
                            bool auto = depAutoInstall != null && i < depAutoInstall.Length &&
                                        depAutoInstall[i].Equals("Yes", StringComparison.OrdinalIgnoreCase);
                            Dependencies.Add(new AutoDependencyEntry { AppName = depNames[i], AutoInstall = auto });
                            DependenciesList.Children.Add(MakeTableRow(depNames[i], auto ? "Yes" : "No", 160));
                        }
                    }
                    else SeedPlaceholder(DependenciesList, "No dependencies added.");
                }

                // ── 6 Supersedence ────────────────────────────────────
                Supersedences.Clear();
                if (SupersedenceList != null)
                {
                    SupersedenceList.Children.Clear();
                    bool hasSup = supNames != null && supNames.Any(n => !string.IsNullOrWhiteSpace(n));
                    if (hasSup)
                    {
                        for (int i = 0; i < supNames!.Length; i++)
                        {
                            if (string.IsNullOrWhiteSpace(supNames[i])) continue;
                            bool uninstPrev = supUninstallPrev != null && i < supUninstallPrev.Length &&
                                              supUninstallPrev[i].Equals("Yes", StringComparison.OrdinalIgnoreCase);
                            Supersedences.Add(new AutoSupersedenceEntry { AppName = supNames[i], UninstallPrevious = uninstPrev });
                            SupersedenceList.Children.Add(MakeTableRow(supNames[i], uninstPrev ? "Yes" : "No", 200));
                        }
                    }
                    else SeedPlaceholder(SupersedenceList, "No superseded apps added.");
                }

                // ── 7 Assignments ─────────────────────────────────────
                DeploymentType = deploymentType ?? "";

                Assignments.Clear();
                FillAssignList(AvailableList, "Available", R(p?.AssignmentsAvailable, null, s.AssignmentsAvailable));
                // NOTE: ActivateTopTab is intentionally NOT called here.
                // NavigateToPublishTab (AutoPackagePage) handles tab activation after AutoFillFromPackage
                // returns — and only when prefillOnly=false. Calling it here would switch tabs during
                // the prefillOnly pass (between Install and Uninstall), which is incorrect.
            }

            if (Dispatcher.CheckAccess()) DoFill();
            else Dispatcher.Invoke(DoFill);
        }

        // ── Detection builder + renderer ─────────────────────────────

        private void BuildAndRenderDetectionRules(
            AutoPackageAppEntry?        p,
            AutoIntuneSettingsOverride? ov2,
            AutoIntuneSettings          s,
            string                      packageFolder,
            string                      sourceAppName = "")
        {
            DetectionRules.Clear();
            RulesFormat = DetectionScriptFile = "";
            DetectionFileDateValue = DetectionFileTimeValue = "";
            DetectionScriptEnforceSignature = DetectionScriptRunAs32Bit = false;

            bool isScript = R(p?.RulesFormat, ov2?.RulesFormat, "")
                                .Equals("Custom script", StringComparison.OrdinalIgnoreCase) ||
                            (p?.DetectionTypeScript ?? "").Equals("Yes", StringComparison.OrdinalIgnoreCase);

            RulesFormat = isScript ? "Custom script" : "Manual detection rules";
            if (RbDetectScript != null) RbDetectScript.IsChecked = isScript;
            if (RbDetectManual != null) RbDetectManual.IsChecked = !isScript;
            if (PanelManualRules  != null) PanelManualRules.Visibility  = isScript ? Visibility.Collapsed : Visibility.Visible;
            if (PanelDetectScript != null) PanelDetectScript.Visibility = isScript ? Visibility.Visible  : Visibility.Collapsed;

            if (isScript)
            {
                DetectionScriptFile             = p?.DetectionScriptFile ?? "";
                DetectionScriptEnforceSignature = (p?.DetectionScriptEnforceSignatureCheck ?? "").Equals("Yes", StringComparison.OrdinalIgnoreCase);
                DetectionScriptRunAs32Bit       = (p?.DetectionScriptRunAs32Bit            ?? "").Equals("Yes", StringComparison.OrdinalIgnoreCase);
                SetTxt(TxtDetectScriptPath, DetectionScriptFile);
                if (RbSigYes    != null) RbSigYes.IsChecked    = DetectionScriptEnforceSignature;
                if (RbSigNo     != null) RbSigNo.IsChecked     = !DetectionScriptEnforceSignature;
                if (RbScript32Yes != null) RbScript32Yes.IsChecked = DetectionScriptRunAs32Bit;
                if (RbScript32No  != null) RbScript32No.IsChecked  = !DetectionScriptRunAs32Bit;
                return;
            }

            // Find MSI
            string? msiFile = null;
            if (!string.IsNullOrWhiteSpace(packageFolder) && Directory.Exists(packageFolder))
                try { msiFile = Directory.GetFiles(packageFolder, "*.msi", SearchOption.AllDirectories).FirstOrDefault(); }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            // ── Auto-populate empty detection fields from registry uninstall hive ──────────
            // Only runs when RulesFormat == "Manual detection rules" and p is not null.
            // For each field: if the per-app JSON value is empty, look it up from the registry.
            // Priority: 1) product code in JSON → LookupInstalledByProductCode
            //           2) product code from .msi file → LookupInstalledByProductCode
            //           3) app name scan across all three uninstall hives
            if (p != null && !isScript)
            {
                // Resolve product code to use for lookup (JSON first, then MSI file)
                string resolvedPc = p.DetectionMsiProductCode?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(resolvedPc) && !string.IsNullOrWhiteSpace(msiFile) && File.Exists(msiFile))
                    resolvedPc = ReadMsiProductCode(msiFile);

                // Attempt registry lookup for File/Registry auto-population.
                // IMPORTANT: resolve by APP NAME first. The MSI product code may be a
                // user-typed value intended ONLY for the MSI detection rule — it must not
                // drive (and corrupt) the File and Registry values. Name-based lookup finds
                // the app's real installed entry, giving correct InstallLocation, main exe,
                // registry key path and DisplayVersion. Product-code lookup is only a
                // last-resort fallback when the name cannot be matched.
                UninstallRegEntry? reg = null;
                if (!string.IsNullOrWhiteSpace(sourceAppName))
                    reg = LookupByDisplayName(sourceAppName);
                if (reg == null && !string.IsNullOrWhiteSpace(AppName) &&
                    !AppName.Equals(sourceAppName, StringComparison.OrdinalIgnoreCase))
                    reg = LookupByDisplayName(AppName);
                if (reg == null && !string.IsNullOrWhiteSpace(resolvedPc))
                    reg = LookupByProductCode(resolvedPc);

                if (reg != null)
                {
                    // DetectionMsiProductCode — from the product code we resolved. For a
                    // genuine MSI package with no JSON code, fall back to the registry
                    // uninstall key leaf when it is a {GUID}. We ONLY do this when an .msi
                    // actually exists in the package — an EXE source has no MSI product
                    // code, so we must not synthesize one from the registry (even though
                    // some EXE installers register a {GUID} uninstall key).
                    if (string.IsNullOrWhiteSpace(p.DetectionMsiProductCode))
                    {
                        if (!string.IsNullOrWhiteSpace(resolvedPc))
                            p.DetectionMsiProductCode = resolvedPc;
                        else if (!string.IsNullOrWhiteSpace(msiFile) && File.Exists(msiFile))
                        {
                            string leaf = ExtractGuidFromKeyPath(reg.RegKeyPath);
                            if (!string.IsNullOrWhiteSpace(leaf))
                                p.DetectionMsiProductCode = leaf;
                        }
                    }

                    // DetectionMsiValue — DisplayVersion from registry
                    if (string.IsNullOrWhiteSpace(p.DetectionMsiValue) && !string.IsNullOrWhiteSpace(reg.DisplayVersion))
                        p.DetectionMsiValue = reg.DisplayVersion;

                    // DetectionFileFolderPath — InstallLocation from registry
                    if (string.IsNullOrWhiteSpace(p.DetectionFileFolderPath) && !string.IsNullOrWhiteSpace(reg.InstallLocation))
                        p.DetectionFileFolderPath = reg.InstallLocation;

                    // DetectionFileFileName — main exe from InstallLocation (via shortcuts then largest exe)
                    if (string.IsNullOrWhiteSpace(p.DetectionFileFileName))
                    {
                        string exePath = ResolveMainExe(reg);
                        if (!string.IsNullOrWhiteSpace(exePath))
                        {
                            p.DetectionFileFileName = IOPath.GetFileName(exePath);

                            // DetectionFileFolderPath fallback if still empty
                            if (string.IsNullOrWhiteSpace(p.DetectionFileFolderPath))
                                p.DetectionFileFolderPath = IOPath.GetDirectoryName(exePath) ?? "";

                            // DetectionFileValue — file version of main exe
                            if (string.IsNullOrWhiteSpace(p.DetectionFileValue) && File.Exists(exePath))
                            {
                                try
                                {
                                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                                    string fv = (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                                        ? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}"
                                        : fvi.FileVersion?.Trim() ?? "";
                                    if (!string.IsNullOrWhiteSpace(fv))
                                        p.DetectionFileValue = fv;
                                }
                                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }
                        }
                    }

                    // ── Date created / Date modified: auto-fetch the main exe's date+time ──
                    // When the File detection method is a date type and the date/time value
                    // is not provided in the JSON, read it from the resolved main exe so the
                    // Publish → Detection tab shows a concrete value. The DATE goes into
                    // DetectionFileDateValue (MM/dd/yyyy) and the TIME into DetectionFileTimeValue
                    // (h:mm:ss tt), mirroring the Download tab's date+time File Value fields.
                    {
                        string fileMethod = (p.DetectionFileDetectionMethod ?? "").Trim();
                        bool isDateCreated  = fileMethod.Equals("Date created",  StringComparison.OrdinalIgnoreCase);
                        bool isDateModified = fileMethod.Equals("Date modified", StringComparison.OrdinalIgnoreCase);
                        if ((isDateCreated || isDateModified) &&
                            (string.IsNullOrWhiteSpace(p.DetectionFileDateValue) || string.IsNullOrWhiteSpace(p.DetectionFileTimeValue)))
                        {
                            try
                            {
                                // Resolve the exe to inspect: explicit folder+file if present,
                                // otherwise the registry-resolved main exe.
                                string exeForDate = "";
                                if (!string.IsNullOrWhiteSpace(p.DetectionFileFolderPath) && !string.IsNullOrWhiteSpace(p.DetectionFileFileName))
                                    exeForDate = IOPath.Combine(p.DetectionFileFolderPath, p.DetectionFileFileName);
                                if (string.IsNullOrWhiteSpace(exeForDate) || !File.Exists(exeForDate))
                                    exeForDate = ResolveMainExe(reg);

                                if (!string.IsNullOrWhiteSpace(exeForDate) && File.Exists(exeForDate))
                                {
                                    DateTime dt = isDateCreated
                                        ? File.GetCreationTime(exeForDate)
                                        : File.GetLastWriteTime(exeForDate);
                                    if (string.IsNullOrWhiteSpace(p.DetectionFileDateValue))
                                        p.DetectionFileDateValue = dt.ToString("MM/dd/yyyy");
                                    if (string.IsNullOrWhiteSpace(p.DetectionFileTimeValue))
                                        // HH:MM:SS AM/PM — two-digit 12-hour clock with AM/PM.
                                        p.DetectionFileTimeValue = dt.ToString("hh:mm:ss tt");
                                }
                            }
                            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        }
                    }

                    // DetectionRegKeyPath — full HKEY_... path from registry.
                    // Also REPLACE a stale key path that embeds the MSI product code GUID:
                    // such a value came from a product-code-based lookup (e.g. a user-typed
                    // MSI product code) and is NOT the app's real uninstall key. The Registry
                    // rule must reflect the app's actual registry entry (resolved by name),
                    // independent of the MSI product code.
                    bool regKeyIsProductCodeDerived =
                        !string.IsNullOrWhiteSpace(p.DetectionMsiProductCode) &&
                        !string.IsNullOrWhiteSpace(p.DetectionRegKeyPath) &&
                        p.DetectionRegKeyPath.IndexOf(p.DetectionMsiProductCode, StringComparison.OrdinalIgnoreCase) >= 0;
                    if ((string.IsNullOrWhiteSpace(p.DetectionRegKeyPath) || regKeyIsProductCodeDerived)
                        && !string.IsNullOrWhiteSpace(reg.RegKeyPath))
                    {
                        p.DetectionRegKeyPath = reg.RegKeyPath;
                        // The registry value (DisplayVersion) tied to that stale key is also
                        // wrong — refresh it from the real entry so they stay consistent.
                        if (regKeyIsProductCodeDerived && !string.IsNullOrWhiteSpace(reg.DisplayVersion))
                            p.DetectionRegistryValue = reg.DisplayVersion;
                    }

                    // DetectionRegistryValue — DisplayVersion from registry
                    if (string.IsNullOrWhiteSpace(p.DetectionRegistryValue) && !string.IsNullOrWhiteSpace(reg.DisplayVersion))
                        p.DetectionRegistryValue = reg.DisplayVersion;

                    // DetectionRegValueName — the registry VALUE we read above is DisplayVersion,
                    // so the corresponding "Registry Value Name" is "DisplayVersion". Capture it
                    // explicitly (when empty) so the value is persisted into _staticSelectedApp and
                    // recorded in the AutoPackage log — instead of only being supplied as the
                    // AutoIntuneSettings render-time default. A key-level existence check ignores
                    // the value name, so setting it here is harmless in that case.
                    if (string.IsNullOrWhiteSpace(p.DetectionRegValueName) && !string.IsNullOrWhiteSpace(reg.DisplayVersion))
                        p.DetectionRegValueName = "DisplayVersion";
                }
                else
                {
                    // No registry entry found — still ensure DetectionMsiProductCode is set from MSI file
                    if (string.IsNullOrWhiteSpace(p.DetectionMsiProductCode) && !string.IsNullOrWhiteSpace(resolvedPc))
                        p.DetectionMsiProductCode = resolvedPc;
                }

                // ── Size in MB: resolve the target exe's size in MB ───────────────────────────
                // Runs whether or not the app was installed, so it covers BOTH:
                //   • InstallPackage = Yes  → size taken after install success (registry-resolved
                //                             or explicit folder+file, e.g. installed filecompare.exe).
                //   • InstallPackage = No   → size taken after createpackage success from the exe
                //                             matching DetectionFileFileName inside the package
                //                             source Files folder (the app is not installed).
                // Only fills DetectionFileSizeValue when the method is "Size in MB" and the JSON
                // did not already supply a value. Size is binary MB (bytes / 1048576), matching
                // Windows Explorer's "Size" figure, formatted with up to two decimals.
                {
                    string fileMethod = (p.DetectionFileDetectionMethod ?? "").Trim();
                    bool isSizeMb = fileMethod.Equals("Size in MB", StringComparison.OrdinalIgnoreCase);
                    if (isSizeMb && string.IsNullOrWhiteSpace(p.DetectionFileSizeValue))
                    {
                        try
                        {
                            string exeForSize = "";

                            // 1) Explicit folder + file (installed path, e.g. C:\Program Files\Smekal\FileCompare\filecompare.exe)
                            if (!string.IsNullOrWhiteSpace(p.DetectionFileFolderPath) && !string.IsNullOrWhiteSpace(p.DetectionFileFileName))
                                exeForSize = IOPath.Combine(p.DetectionFileFolderPath, p.DetectionFileFileName);

                            // 2) Registry-resolved main exe (install case)
                            if ((string.IsNullOrWhiteSpace(exeForSize) || !File.Exists(exeForSize)) && reg != null)
                                exeForSize = ResolveMainExe(reg);

                            // 3) No-install case: find the exe matching DetectionFileFileName inside the
                            //    package source Files folder; fall back to the first/largest *.exe there.
                            if ((string.IsNullOrWhiteSpace(exeForSize) || !File.Exists(exeForSize)) &&
                                !string.IsNullOrWhiteSpace(packageFolder) && Directory.Exists(packageFolder))
                            {
                                try
                                {
                                    var pkgExes = Directory.GetFiles(packageFolder, "*.exe", SearchOption.AllDirectories);
                                    string wantName = (p.DetectionFileFileName ?? "").Trim();
                                    string? match = null;
                                    if (!string.IsNullOrWhiteSpace(wantName))
                                        match = pkgExes.FirstOrDefault(e =>
                                            IOPath.GetFileName(e).Equals(wantName, StringComparison.OrdinalIgnoreCase));
                                    if (string.IsNullOrWhiteSpace(match) && pkgExes.Length > 0)
                                        match = pkgExes.OrderByDescending(e => new FileInfo(e).Length).First();
                                    if (!string.IsNullOrWhiteSpace(match)) exeForSize = match;
                                }
                                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                            }

                            if (!string.IsNullOrWhiteSpace(exeForSize) && File.Exists(exeForSize))
                            {
                                long bytes = new FileInfo(exeForSize).Length;
                                double mb   = bytes / 1048576.0;   // 1024*1024, matches Explorer "Size"
                                p.DetectionFileSizeValue =
                                    mb.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                }
            }
            // ── End auto-populate ──────────────────────────────────────────────────────────

            // Req 1: the MSI product code is only resolved during the auto-populate block
            // above (from registry / .msi). The first BuildProgramCommands call (in the
            // Program section) ran BEFORE that, so an MSI uninstall could come out as
            // `msiexec /x ""`. Recompute now that p.DetectionMsiProductCode is populated,
            // and refresh the textboxes so both the UI and the saved JSON are correct.
            {
                var (reInstall, reUninstall) = BuildProgramCommands(p, msiFile, packageFolder);
                if (!string.IsNullOrWhiteSpace(reInstall))   { InstallCmd   = reInstall;   SetTxt(TxtInstallCmd,   reInstall); }
                if (!string.IsNullOrWhiteSpace(reUninstall)) { UninstallCmd = reUninstall; SetTxt(TxtUninstallCmd, reUninstall); }
            }

            if (_wantMsi)
            {
                string msiPc = !string.IsNullOrWhiteSpace(p?.DetectionMsiProductCode)
                                   ? p!.DetectionMsiProductCode
                                   : (!string.IsNullOrWhiteSpace(msiFile) && File.Exists(msiFile)) ? ReadMsiProductCode(msiFile) : "";
                // An MSI detection rule is meaningful only with a product code. EXE sources
                // have none, so skip the MSI rule entirely instead of showing an empty/stale one.
                if (!string.IsNullOrWhiteSpace(msiPc))
                    DetectionRules.Add(new AutoDetectionRule
                    {
                        RuleType       = "MSI", IsDefault = true,
                        MsiProductCode = msiPc,
                        MsiCheck = R(p?.DetectionMsiVersionCheck, ov2?.DetectionMsiVersionCheck, "").Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No",
                        MsiOperator    = R(p?.DetectionMsiOperator, ov2?.DetectionMsiOperator, ""),
                        MsiVersion     = p?.DetectionMsiValue ?? "",
                    });
            }

            bool wantFile = _wantFile && ((p?.DetectionTypeFile ?? "").Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                        || s.DetectionTypes.Any(t => t.Equals("File", StringComparison.OrdinalIgnoreCase)));
            if (wantFile)
            {
                string fm = R(p?.DetectionFileDetectionMethod, ov2?.DetectionFileDetectionMethod, s.DetectionFileDetectionMethod);
                bool fileIsDate = fm.StartsWith("Date", StringComparison.OrdinalIgnoreCase);
                bool fileIsSize = fm.Equals("Size in MB", StringComparison.OrdinalIgnoreCase);
                string fv = p?.DetectionFileValue ?? "";       // version/size value
                string fsz = p?.DetectionFileSizeValue ?? "";  // size-in-MB value (auto-resolved above)
                string fd = p?.DetectionFileDateValue ?? "";   // date part (Date created/modified)
                string ft = p?.DetectionFileTimeValue ?? "";   // time part (Date created/modified)
                if (fileIsDate)
                {
                    // Expose the resolved (possibly auto-fetched) date/time so the host can
                    // persist them to the per-app JSON as separate fields.
                    DetectionFileDateValue = fd;
                    DetectionFileTimeValue = ft;
                }
                if (fileIsSize)
                {
                    // Expose the resolved (possibly auto-fetched) size so the host can
                    // persist it to the per-app JSON as DetectionFileSizeValue.
                    DetectionFileSizeValue = fsz;
                }
                DetectionRules.Add(new AutoDetectionRule
                {
                    RuleType = "File",
                    FilePath = R(p?.DetectionFileFolderPath, ov2?.DetectionFileFolderPath, s.DetectionFileFolderPath),
                    FileName = R(p?.DetectionFileFileName,   ov2?.DetectionFileFileName,   s.DetectionFileFileName),
                    FileCheck    = fm,
                    FileOperator = R(p?.DetectionFileOperator, ov2?.DetectionFileOperator, s.DetectionFileOperator),
                    // Date → combine date + time; Size in MB → the resolved size; otherwise the version.
                    FileValue    = fileIsDate
                                   ? (string.IsNullOrWhiteSpace(fd) ? "" : fd.Trim() + (!string.IsNullOrWhiteSpace(ft) ? " " + ft.Trim() : ""))
                                   : fileIsSize
                                       ? fsz
                                       : fv,
                    File32Bit    = (p?.DetectionFile32Bit ?? "").Equals("Yes", StringComparison.OrdinalIgnoreCase),
                });
            }

            bool wantReg = _wantReg && ((p?.DetectionTypeRegistry ?? "").Equals("Yes", StringComparison.OrdinalIgnoreCase)
                                      || s.DetectionTypes.Any(t => t.Equals("Registry", StringComparison.OrdinalIgnoreCase)));
            if (wantReg)
                DetectionRules.Add(new AutoDetectionRule
                {
                    RuleType = "Registry", IsDefault = true,
                    RegKeyPath  = R(p?.DetectionRegKeyPath,   ov2?.DetectionRegKeyPath,   s.DetectionRegKeyPath),
                    RegValue    = R(p?.DetectionRegValueName, ov2?.DetectionRegValueName, s.DetectionRegValueName),
                    RegCheck    = R(p?.DetectionRegDetectionMethod, ov2?.DetectionRegDetectionMethod, s.DetectionRegDetectionMethod),
                    RegOperator = R(p?.DetectionRegOperator,  ov2?.DetectionRegOperator,  s.DetectionRegOperator),
                    RegValueData = p?.DetectionRegistryValue ?? "",
                    Reg32Bit    = (p?.DetectionReg32Bit ?? "").Equals("Yes", StringComparison.OrdinalIgnoreCase),
                });

            RenderDetectionRules();
        }

        // ── Intune-portal display rules for Operator/Value visibility ──────
        // File: "exists" / "does not exist" carry no operator or value.
        public static bool FileMethodIsExistence(string? method)
        {
            string m = (method ?? "").Trim();
            return m.Equals("File or folder exists", StringComparison.OrdinalIgnoreCase)
                || m.Equals("File or folder does not exist", StringComparison.OrdinalIgnoreCase)
                || m.IndexOf("exist", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Registry KEY-level existence checks: no value name / operator / value.
        public static bool RegMethodIsKeyLevel(string? method)
        {
            string m = (method ?? "").Trim();
            return m.Equals("Key exists",        StringComparison.OrdinalIgnoreCase)
                || m.Equals("Key does not exist", StringComparison.OrdinalIgnoreCase);
        }

        // Registry: show Operator+Value ONLY for a value comparison with a value
        // name present. Existence checks ("Key exists"/"Key does not exist", and the
        // value-exists variants) and an empty value name show nothing.
        public static bool RegShowsOperatorAndValue(string? method, string? valueName)
        {
            if (string.IsNullOrWhiteSpace(valueName)) return false;          // Req 6
            string m = (method ?? "").Trim();
            if (m.IndexOf("exist", StringComparison.OrdinalIgnoreCase) >= 0) // Req 4 & 5 (key/value exists / does not exist)
                return false;
            // Remaining methods are comparisons: String / Version / Integer comparison.
            return true;                                                     // Req 7
        }

        private void RenderDetectionRules()
        {
            if (DetectionRulesList == null) return;
            DetectionRulesList.Children.Clear();
            if (!DetectionRules.Any())
            { SeedPlaceholder(DetectionRulesList, "No detection rules added."); return; }

            foreach (var rule in DetectionRules)
            {
                // Rule card with type badge + field grid (matches PublishDeployPage card style)
                var card = new Border
                {
                    BorderThickness = new Thickness(1),
                    Margin          = new Thickness(0, 0, 0, 6),
                    Padding         = new Thickness(10, 8, 10, 10),
                    CornerRadius    = new CornerRadius(4),
                };
                card.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");
                card.SetResourceReference(Border.BackgroundProperty,   "CardBgBrush");

                var outer = new Grid();
                outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Type badge
                var badge = new TextBlock
                {
                    Text = rule.RuleType, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
                };
                badge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBlueBrush");
                Grid.SetColumn(badge, 0);
                outer.Children.Add(badge);

                // Details grid
                var details = new Grid();
                details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(details, 1);

                void AddRow(string lbl, string val)
                {
                    if (string.IsNullOrWhiteSpace(val)) return;
                    int r = details.RowDefinitions.Count;
                    details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var l = new TextBlock { Text = lbl, FontSize = 11, Margin = new Thickness(0, 1, 8, 1) };
                    l.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    var v = new TextBlock { Text = val, FontSize = 11, Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
                    v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    Grid.SetRow(l, r); Grid.SetColumn(l, 0);
                    Grid.SetRow(v, r); Grid.SetColumn(v, 1);
                    details.Children.Add(l); details.Children.Add(v);
                }

                if (rule.RuleType == "MSI")
                {
                    AddRow("MSI product code", rule.MsiProductCode);
                    AddRow("Version check",    rule.MsiCheck);
                    // Operator + Value only when a version check is requested (Req 2).
                    if (rule.MsiCheck.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                    {
                        AddRow("Operator", rule.MsiOperator);
                        AddRow("Value",    rule.MsiVersion);
                    }
                }
                else if (rule.RuleType == "File")
                {
                    AddRow("Path",             rule.FilePath);
                    AddRow("File or folder",   rule.FileName);
                    AddRow("Detection method", rule.FileCheck);
                    // "File or folder exists" / "...does not exist" carry no operator or
                    // value in the Intune portal (Req 3). Only comparison methods
                    // (Date modified / Version / Size) show Operator + Value.
                    if (!FileMethodIsExistence(rule.FileCheck))
                    {
                        AddRow("Operator", rule.FileOperator);
                        AddRow("Value",    rule.FileValue);
                    }
                    AddRow("32-bit on 64-bit", rule.File32Bit ? "Yes" : "No");
                }
                else if (rule.RuleType == "Registry")
                {
                    AddRow("Key path",         rule.RegKeyPath);
                    // "Value name" applies only to value-level checks. For a KEY-level
                    // existence check ("Key exists"/"Key does not exist") there is no
                    // value name in the Intune portal, so hide the row (Req 1, Req 2)
                    // even if a value (e.g. DisplayVersion) was auto-filled.
                    if (!RegMethodIsKeyLevel(rule.RegCheck))
                        AddRow("Value name",   rule.RegValue);
                    AddRow("Detection method", rule.RegCheck);
                    // Operator + Value are shown ONLY for a value-based comparison
                    // (String / Version / Integer comparison) WITH a value name set.
                    // Hidden for any existence check, and hidden when no value name.
                    if (RegShowsOperatorAndValue(rule.RegCheck, rule.RegValue))
                    {
                        AddRow("Operator", rule.RegOperator);
                        AddRow("Value",    rule.RegValueData);
                    }
                    AddRow("32-bit on 64-bit", rule.Reg32Bit ? "Yes" : "No");
                }

                outer.Children.Add(details);
                card.Child = outer;
                DetectionRulesList.Children.Add(card);
            }
        }

        // ── ClearAll ──────────────────────────────────────────────────

        public void ClearAll()
        {
            void DoClear()
            {
                // ── Backing properties ─────────────────────────────────────
                ClientId = TenantId = IntunewinFilePath = "";
                AppName = AppVendor = AppVersion = Description = InformationURL = "";
                PrivacyURL = Developer = Owner = Notes = IconPath = Category = FeaturedApp = "";
                InstallCmd = UninstallCmd = InstallationTime = AllowAvailableUninstall = "";
                InstallBehavior = DeviceRestartBehavior = MinimumOperatingSystem = OperatingSystemArchitecture = "";
                RulesFormat = DetectionScriptFile = DeploymentType = "";
                DetectionScriptEnforceSignature = DetectionScriptRunAs32Bit = false;
                DetectionRules.Clear(); Dependencies.Clear(); Supersedences.Clear(); Assignments.Clear();

                // ── TextBoxes (Tabs 1-4) ───────────────────────────────────
                void C(TextBox? tb) { if (tb != null) tb.Text = ""; }
                C(TxtIntunewinFile); C(TxtAppName); C(TxtAppVendor); C(TxtAppVersion);
                C(TxtDescription); C(TxtInfoUrl); C(TxtPrivacyUrl); C(TxtDeveloper);
                C(TxtOwner); C(TxtNotes); C(TxtIconPath); C(TxtInstallCmd);
                C(TxtUninstallCmd); C(TxtInstallTime); C(TxtDetectScriptPath);

                // ── Tab 1 — App Info: Combo boxes ─────────────────────────
                if (CmbCategory != null) CmbCategory.SelectedIndex = 0;
                if (CmbFeatured != null) CmbFeatured.SelectedIndex = 0;

                // ── Tab 2 — Program: Radio buttons & Combo box ─────────────
                if (RbInstallSystem    != null) RbInstallSystem.IsChecked    = true;
                if (RbUninstallNo      != null) RbUninstallNo.IsChecked      = true;
                if (CmbRestartBehavior != null) CmbRestartBehavior.SelectedIndex = 0;

                // ── Tab 3 — Requirements: Checkboxes & Combo box ───────────
                if (ChkX64   != null) ChkX64.IsChecked   = true;
                if (ChkX86   != null) ChkX86.IsChecked   = false;
                if (ChkArm64 != null) ChkArm64.IsChecked = false;
                if (CmbMinOs != null) CmbMinOs.SelectedIndex = 0;

                // ── Tab 4 — Detection: Radio buttons ───────────────────────
                if (RbDetectManual != null) RbDetectManual.IsChecked = true;
                if (RbScript32No   != null) RbScript32No.IsChecked   = true;
                if (RbSigNo        != null) RbSigNo.IsChecked        = true;

                // ── Tab 7 — Assignments: Radio button ──────────────────────

                // ── Dynamic lists (Tabs 4-7) ───────────────────────────────
                if (DetectionRulesList != null) { DetectionRulesList.Children.Clear(); SeedPlaceholder(DetectionRulesList, "No detection rules added."); }
                if (DependenciesList   != null) { DependenciesList.Children.Clear();   SeedPlaceholder(DependenciesList,   "No dependencies added."); }
                if (SupersedenceList   != null) { SupersedenceList.Children.Clear();   SeedPlaceholder(SupersedenceList,   "No superseded apps added."); }
                ClearAssignList(AvailableList);

                // ── Tab 8 — Log ────────────────────────────────────────────
                if (LogTextBox != null) LogTextBox.Clear();
            }
            if (Dispatcher.CheckAccess()) DoClear(); else Dispatcher.Invoke(DoClear);
        }

        // ── UI helpers ────────────────────────────────────────────────

        private static void SetTxt(TextBox? tb, string? val)
        { if (tb != null && !string.IsNullOrWhiteSpace(val)) tb.Text = val; }

        private static void SetCmb(ComboBox? cb, string? val)
        {
            if (cb == null || string.IsNullOrWhiteSpace(val)) return;
            foreach (ComboBoxItem ci in cb.Items)
                if (string.Equals(ci.Content?.ToString(), val, StringComparison.OrdinalIgnoreCase))
                { cb.SelectedItem = ci; return; }
            cb.Text = val;
        }

        private void FillAssignList(StackPanel? list, string assignType, string csv)
        {
            if (list == null) return;
            list.Children.Clear();
            var groups = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (groups.Length == 0) { ClearAssignList(list); return; }
            foreach (var g in groups)
            {
                Assignments.Add(new AutoAssignmentEntry { AssignType = assignType, GroupName = g });
                list.Children.Add(MakeTableRow(g, assignType, 80));
            }
        }

        private void ClearAssignList(StackPanel? list)
        {
            if (list == null) return;
            list.Children.Clear();
            SeedPlaceholder(list, "No groups added.");
        }

        private void SeedPlaceholder(StackPanel? sp, string text)
        {
            if (sp == null) return;
            var tb = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(10, 7, 10, 7) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            sp.Children.Add(tb);
        }

        /// Makes a two-column table row (name | value) matching the headers above
        private UIElement MakeTableRow(string name, string value, double valueColWidth)
        {
            var g = new Grid { Margin = new Thickness(10, 4, 10, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueColWidth) });
            var n = new TextBlock { Text = name,  FontSize = 11, TextWrapping = TextWrapping.Wrap };
            n.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var v = new TextBlock { Text = value, FontSize = 11 };
            v.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(v, 1);
            g.Children.Add(n); g.Children.Add(v);
            return g;
        }

        // ── MSI product code reader ───────────────────────────────────

        // If a registry uninstall key path ends in a {GUID} (the MSI product code),
        // return that GUID; otherwise empty.
        private static string ExtractGuidFromKeyPath(string? keyPath)
        {
            if (string.IsNullOrWhiteSpace(keyPath)) return "";
            string leaf = keyPath.TrimEnd('\\');
            int slash = leaf.LastIndexOf('\\');
            if (slash >= 0 && slash < leaf.Length - 1) leaf = leaf.Substring(slash + 1);
            leaf = leaf.Trim();
            // A product code looks like {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
            if (leaf.StartsWith("{") && leaf.EndsWith("}") && leaf.Length >= 38)
                return leaf;
            return "";
        }

        private static string ReadMsiProductCode(string msiPath)
        {
            try
            {
                var it = Type.GetTypeFromProgID("WindowsInstaller.Installer");
                if (it == null) return "";
                var inst = Activator.CreateInstance(it); if (inst == null) return "";
                var db   = it.InvokeMember("OpenDatabase", System.Reflection.BindingFlags.InvokeMethod, null, inst, new object[] { msiPath, 0 });
                if (db == null) return "";
                var dbt  = db.GetType();
                var view = dbt.InvokeMember("OpenView", System.Reflection.BindingFlags.InvokeMethod, null, db,
                               new object[] { "SELECT Value FROM Property WHERE Property='ProductCode'" });
                if (view == null) return "";
                var vt = view.GetType();
                vt.InvokeMember("Execute", System.Reflection.BindingFlags.InvokeMethod, null, view, null);
                var rec = vt.InvokeMember("Fetch",   System.Reflection.BindingFlags.InvokeMethod, null, view, null);
                if (rec == null) return "";
                var val = rec.GetType().InvokeMember("get_StringData", System.Reflection.BindingFlags.InvokeMethod, null, rec, new object[] { 1 })?.ToString() ?? "";
                vt.InvokeMember("Close", System.Reflection.BindingFlags.InvokeMethod, null, view, null);
                return val;
            }
            catch { return ""; }
        }

        private static string R(string? a, string? b, string fallback = "")
            => !string.IsNullOrWhiteSpace(a) ? a!
             : !string.IsNullOrWhiteSpace(b) ? b!
             : fallback;

        // ── Read UninstallString / QuietUninstallString from the uninstall hive ──
        // Used by Requirement 4 when the per-app JSON has no UninstallPath: derive the
        // EXE uninstall command from the registry. Tries product code first, then name.
        private static string ReadUninstallStringFromRegistry(string productCode, string appName)
        {
            string Try(RegistryKey key)
            {
                try
                {
                    var quiet = key.GetValue("QuietUninstallString") as string;
                    if (!string.IsNullOrWhiteSpace(quiet)) return quiet!;
                    var us = key.GetValue("UninstallString") as string;
                    return us ?? "";
                }
                catch { return ""; }
            }

            // 1) By product code
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                string pc = productCode.Trim();
                if (!pc.StartsWith("{")) pc = "{" + pc;
                if (!pc.EndsWith("}"))   pc += "}";
                foreach (var (sub, root, _) in _uninstallHives)
                {
                    try
                    {
                        using var hive = root.OpenSubKey(sub);
                        using var key  = hive?.OpenSubKey(pc);
                        if (key != null)
                        {
                            string v = Try(key);
                            if (!string.IsNullOrWhiteSpace(v)) return v;
                        }
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
            }

            // 2) By display name (best/longest match)
            if (!string.IsNullOrWhiteSpace(appName))
            {
                string best = ""; int bestLen = 0;
                foreach (var (sub, root, _) in _uninstallHives)
                {
                    try
                    {
                        using var hive = root.OpenSubKey(sub);
                        if (hive == null) continue;
                        foreach (string subName in hive.GetSubKeyNames())
                        {
                            try
                            {
                                using var key = hive.OpenSubKey(subName);
                                if (key == null) continue;
                                string dn = key.GetValue("DisplayName") as string ?? "";
                                if (string.IsNullOrWhiteSpace(dn)) continue;
                                bool match = dn.Contains(appName, StringComparison.OrdinalIgnoreCase) ||
                                             appName.Contains(dn,  StringComparison.OrdinalIgnoreCase);
                                if (!match) continue;
                                string v = Try(key);
                                if (!string.IsNullOrWhiteSpace(v) && dn.Length > bestLen)
                                { best = v; bestLen = dn.Length; }
                            }
                            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        }
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
                if (!string.IsNullOrWhiteSpace(best)) return best;
            }
            return "";
        }

        // ── Build the Intune "Program" tab Install / Uninstall commands ──────────
        // Implements Requirements 1–4:
        //   • PSADT template present  → use PSADTInstallCommand / PSADTUninstallCommand.
        //   • No PSADT template:
        //       - use InstallCommand / UninstallCommand from JSON when present (Req 2);
        //       - otherwise DERIVE them (Req 3 = MSI, Req 4 = EXE) from the installer
        //         file + silent parameters, product code, UninstallPath / registry.
        // Returns the (install, uninstall) command pair. Empty strings when undeterminable.
        // The Intune settings tab's Install/Uninstall command wins per field when configured, so
        // the Publish Program tab and the Intune portal show exactly what was set there (for
        // example Invoke-AppDeployToolkit.exe). A blank field falls back to the derivation below.
        private static (string install, string uninstall) BuildProgramCommands(
            AutoPackageAppEntry? p, string? msiFile, string packageFolder)
        {
            if (p == null) return ("", "");
            var (di, du) = BuildDerivedProgramCommands(p, msiFile, packageFolder);
            // Template app: the Intune settings command wins. No-template (direct-install) app:
            // the derived SOURCE command wins - there is no Invoke-AppDeployToolkit.exe, so the .exe
            // default must not leak into the Program tab or the portal.
            bool hasTpl = !string.IsNullOrWhiteSpace(p.PSADTTemplateFolder);
            string i = (hasTpl && !string.IsNullOrWhiteSpace(p.InstallCommand))   ? p.InstallCommand!   : di;
            string u = (hasTpl && !string.IsNullOrWhiteSpace(p.UninstallCommand)) ? p.UninstallCommand! : du;
            return (i, u);
        }

        private static (string install, string uninstall) BuildDerivedProgramCommands(
            AutoPackageAppEntry? p, string? msiFile, string packageFolder)
        {
            if (p == null) return ("", "");

            // ── Req 1: PSADT template present → use the PSADT commands verbatim ──
            bool hasPsadt = !string.IsNullOrWhiteSpace(p.PSADTTemplateFolder);
            if (hasPsadt)
                return (p.PSADTInstallCommand ?? "", p.PSADTUninstallCommand ?? "");

            // ── No PSADT template ──
            // Always derive from the CURRENT Download Preference + package files so the Program
            // tab reflects THIS run, not a previous one. SourceInstallCommand / SourceUninstallCommand
            // are intentionally NOT consulted: nothing in the auto-flow sets them for the current
            // run, so an older persisted value would make an MSI run publish the prior EXE command
            // (and the reverse). Start empty and let the MSI / EXE derivation below fill both from
            // the installer name, product code and silent args.
            string install   = "";
            string uninstall = "";

            // Decide MSI vs EXE from DownloadPreference (fallback: presence of an .msi file).
            string pref = (p.DownloadPreference ?? "").Trim();
            bool isMsi = pref.StartsWith("MSI", StringComparison.OrdinalIgnoreCase)
                         || (string.IsNullOrWhiteSpace(pref) && !string.IsNullOrWhiteSpace(msiFile));

            if (isMsi)
            {
                // ── Req 3: MSI ──
                string msiName = !string.IsNullOrWhiteSpace(msiFile)
                    ? IOPath.GetFileName(msiFile) : "";
                // Product code — JSON first, then read from the .msi.
                string productCode = p.DetectionMsiProductCode?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(productCode) &&
                    !string.IsNullOrWhiteSpace(msiFile) && File.Exists(msiFile))
                    productCode = ReadMsiProductCode(msiFile);

                if (string.IsNullOrWhiteSpace(install))
                {
                    string args = (p.SilentInstallParameterMSI ?? "").Trim();
                    install = $"msiexec /i \"{msiName}\"" + (args.Length > 0 ? " " + args : "");
                }
                if (string.IsNullOrWhiteSpace(uninstall))
                {
                    string args = (p.SilentUninstallParameterMSI ?? "").Trim();
                    uninstall = $"msiexec /x \"{productCode}\"" + (args.Length > 0 ? " " + args : "");
                }
                return (install, uninstall);
            }
            else
            {
                // ── Req 4: EXE ──
                // <exe name> — installer EXE in the package folder.
                string exeName = "";
                try
                {
                    if (!string.IsNullOrWhiteSpace(packageFolder) && Directory.Exists(packageFolder))
                    {
                        string? exe = Directory.GetFiles(packageFolder, "*.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(exe)) exeName = IOPath.GetFileName(exe);
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                if (string.IsNullOrWhiteSpace(install))
                {
                    string args = (p.SilentInstallParameterEXE ?? "").Trim();
                    install = $"\"{exeName}\"" + (args.Length > 0 ? " " + args : "");
                }
                if (string.IsNullOrWhiteSpace(uninstall))
                {
                    // Uninstall path — JSON UninstallPath first, else registry UninstallString.
                    string uninstallPath = (p.UninstallPath ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(uninstallPath))
                    {
                        string productCode = p.DetectionMsiProductCode?.Trim() ?? "";
                        uninstallPath = ReadUninstallStringFromRegistry(productCode, p.SourceAppName ?? "");
                    }
                    string args = (p.SilentUninstallParameterEXE ?? "").Trim();
                    // If the registry UninstallString already carries its own args/quoting,
                    // append the silent args only when we have a bare path.
                    if (!string.IsNullOrWhiteSpace(uninstallPath))
                        uninstall = uninstallPath.TrimStart().StartsWith("\"")
                            ? uninstallPath + (args.Length > 0 ? " " + args : "")
                            : $"\"{uninstallPath}\"" + (args.Length > 0 ? " " + args : "");
                    else
                        uninstall = "";
                }
                return (install, uninstall);
            }
        }

        // ── Registry uninstall hive helpers ──────────────────────────

        private sealed record UninstallRegEntry(
            string DisplayName,
            string DisplayVersion,
            string InstallLocation,
            string DisplayIcon,
            string RegKeyPath,
            bool   Is32Bit);

        private static readonly (string Sub, RegistryKey Root, bool Is32)[] _uninstallHives =
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",             Registry.LocalMachine, false),
            (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", Registry.LocalMachine, true),
            (@"Software\Microsoft\Windows\CurrentVersion\Uninstall",             Registry.CurrentUser,  false),
        };

        /// <summary>Look up a registry uninstall entry by MSI product code (e.g. {GUID}).</summary>
        private static UninstallRegEntry? LookupByProductCode(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode)) return null;
            string pc = productCode.Trim();
            if (!pc.StartsWith("{")) pc = "{" + pc;
            if (!pc.EndsWith("}"))   pc += "}";

            foreach (var (sub, root, is32) in _uninstallHives)
            {
                try
                {
                    using var hive = root.OpenSubKey(sub);
                    if (hive == null) continue;
                    using var key  = hive.OpenSubKey(pc);
                    if (key  == null) continue;
                    string hroot = root == Registry.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                    return ReadEntry(key, $"{hroot}\\{sub}\\{pc}", is32);
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            return null;
        }

        /// <summary>
        /// Fallback: scan all hives for an entry whose DisplayName contains <paramref name="appName"/>
        /// (or vice-versa). Returns the best match (longest DisplayName match).
        /// </summary>
        private static UninstallRegEntry? LookupByDisplayName(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return null;
            UninstallRegEntry? best = null;
            int bestLen = 0;

            foreach (var (sub, root, is32) in _uninstallHives)
            {
                try
                {
                    using var hive = root.OpenSubKey(sub);
                    if (hive == null) continue;
                    foreach (string subName in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = hive.OpenSubKey(subName);
                            if (key == null) continue;
                            string dn = key.GetValue("DisplayName") as string ?? "";
                            if (string.IsNullOrWhiteSpace(dn)) continue;
                            bool match = dn.Contains(appName, StringComparison.OrdinalIgnoreCase) ||
                                         appName.Contains(dn,  StringComparison.OrdinalIgnoreCase);
                            if (!match) continue;
                            if (dn.Length > bestLen)
                            {
                                bestLen = dn.Length;
                                string hroot = root == Registry.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                                best = ReadEntry(key, $"{hroot}\\{sub}\\{subName}", is32);
                            }
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            return best;
        }

        private static UninstallRegEntry ReadEntry(RegistryKey key, string regKeyPath, bool is32)
        {
            string dn      = key.GetValue("DisplayName")    as string ?? "";
            string ver     = key.GetValue("DisplayVersion") as string ?? "";
            string loc     = (key.GetValue("InstallLocation") as string ?? "").TrimEnd(IOPath.DirectorySeparatorChar);
            string icon    = (key.GetValue("DisplayIcon")     as string ?? "").Split(',')[0].Trim().Trim('"');
            // Derive InstallLocation from DisplayIcon if not set
            if (string.IsNullOrWhiteSpace(loc) && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                loc = (IOPath.GetDirectoryName(icon) ?? "").TrimEnd(IOPath.DirectorySeparatorChar);
            return new UninstallRegEntry(dn, ver, loc, icon, regKeyPath, is32);
        }

        /// <summary>
        /// Resolve the main executable path for an app using:
        ///   1. DisplayIcon (if it's an .exe that exists)
        ///   2. Largest .exe in InstallLocation
        ///   3. Start Menu .lnk shortcuts whose target is under InstallLocation
        /// </summary>
        private static string ResolveMainExe(UninstallRegEntry reg)
        {
            // 1. DisplayIcon
            if (!string.IsNullOrWhiteSpace(reg.DisplayIcon) &&
                reg.DisplayIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(reg.DisplayIcon))
                return reg.DisplayIcon;

            // 2. Largest exe in InstallLocation
            if (!string.IsNullOrWhiteSpace(reg.InstallLocation) && Directory.Exists(reg.InstallLocation))
            {
                try
                {
                    var exes = Directory.GetFiles(reg.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly);
                    if (exes.Length > 0)
                        return exes.OrderByDescending(e => new FileInfo(e).Length).First();
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            // 3. Start Menu shortcuts (.lnk) whose name or target matches the app name
            if (!string.IsNullOrWhiteSpace(reg.DisplayName))
            {
                var startMenuDirs = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                };
                foreach (string dir in startMenuDirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)))
                {
                    try
                    {
                        foreach (string lnk in Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories))
                        {
                            try
                            {
                                // Use IWshRuntimeLibrary / Shell32 COM to resolve shortcut
                                var shell  = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                                if (shell == null) continue;
                                var sc     = shell.GetType().InvokeMember("CreateShortcut",
                                                 System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                                if (sc == null) continue;
                                string target = sc.GetType().InvokeMember("TargetPath",
                                                    System.Reflection.BindingFlags.GetProperty, null, sc, null)?.ToString() ?? "";
                                if (string.IsNullOrWhiteSpace(target)) continue;
                                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                                if (!File.Exists(target)) continue;

                                string lnkName = IOPath.GetFileNameWithoutExtension(lnk);
                                bool nameMatch = lnkName.Contains(reg.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                                                 reg.DisplayName.Contains(lnkName, StringComparison.OrdinalIgnoreCase);
                                bool locMatch  = !string.IsNullOrWhiteSpace(reg.InstallLocation) &&
                                                 target.StartsWith(reg.InstallLocation, StringComparison.OrdinalIgnoreCase);

                                if (nameMatch || locMatch)
                                    return target;
                            }
                            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        }
                    }
                    catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPublishPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
            }

            return "";
        }
    }
}
