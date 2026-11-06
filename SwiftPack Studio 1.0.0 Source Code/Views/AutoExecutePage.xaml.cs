// =====================================================================
//  AutoExecutePage.xaml.cs  —  SwiftPack Studio  —  Auto Package > Execute tab
//  FULLY SELF-CONTAINED: no dependency on ExecutePackagePage,
//  ValidatePackagePage, CreateDocumentPage, ExecutePackage.json, or
//  SourceDownloadPage (compile-time). GlobalShowMessageBox is read via
//  reflection at runtime only.
// =====================================================================

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
using System.Windows.Media;
using Microsoft.Win32;

namespace SwiftPack.Views
{
    public partial class AutoExecutePage : Page
    {
        private IAutoPackageContext?          _context;
        private Process?                      _runningProcess;
        private Action?                       _onAfterDeploy;
        private CancellationTokenSource?      _cts;
        private string?                       _logFilePath;
        private DateTime                      _deployStartTime;
        private string?                       _activePs1Path;
        private string?                       _activePidFile;
        private string                        _activeDeployMode = "";
        // Configured PSADT commands (Package settings) plus the package's ps1 path. The ps1 path
        // used to live in the visible box; it now backs the PSADT Install / PSADT Uninstall boxes.
        private string                        _scriptPs1Path    = "";
        private string                        _psadtInstallCmd  = "";
        private string                        _psadtUninstallCmd = "";

        // ── Completion signal for the rest of the Auto Package strip ───────
        // The MANUAL pipeline wires ExecutePackagePage.OnDeployCompleted into
        // MainWindow, which resolves the PSADT log paths and feeds
        // CreateDocumentPage. This page had no equivalent, so the strip's
        // Document tab never learned the log paths and nothing anywhere called
        // AutoDocumentPage.GenerateDocuments - the .txt/.docx simply had no
        // author. Raised on the worker thread after the verdict and the log
        // copy; subscribers dispatch themselves (AutoDocumentPage does).
        public static event Action<string /*deployMode*/, bool /*success*/,
            string? /*logInPackageDocs*/, string? /*psadtLog*/, string /*scriptDir*/>? DeployCompleted;

        // Last resolved log per mode - the copied in-package path when the copy
        // succeeded, else the C:\Windows\Logs\Software original. For anything
        // that needs the paths after the event has come and gone.
        public static string? LastAutoInstallLogPath   { get; private set; }
        public static string? LastAutoUninstallLogPath { get; private set; }

        private static readonly string DefaultLogDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "Software");

        // ── Event-log buffers: one per source, so switching tabs shows each run's full output ──
        // A run used to clear the single TextBox, so starting an Uninstall wiped the Install log.
        // Every log write now goes to the buffer for the current deploy mode (Repair folds into
        // Install), and TxtEventLog shows whichever tab is selected. Install/Uninstall tabs by the
        // EVENT LOG title let the user read either afterwards.
        private readonly System.Text.StringBuilder _installLogBuf   = new();
        private readonly System.Text.StringBuilder _uninstallLogBuf = new();
        private const string ReadyLogText =
            "[Ready] Select a script file and click an Install / Uninstall / Repair button\u2026";
        // Which buffer TxtEventLog currently displays: "Install" or "Uninstall".
        private string _shownLogMode = "Install";
        // Which buffer new writes go to. Set at the start of each run from the deploy mode.
        private string _logWriteMode = "Install";

        // Uninstall (and only Uninstall) writes to the Uninstall buffer; Install and Repair share
        // the Install buffer, matching the two tabs shown.
        private static string LogBucketFor(string deployMode) =>
            string.Equals(deployMode, "Uninstall", StringComparison.OrdinalIgnoreCase) ? "Uninstall" : "Install";

        private System.Text.StringBuilder BufFor(string mode) =>
            string.Equals(mode, "Uninstall", StringComparison.OrdinalIgnoreCase) ? _uninstallLogBuf : _installLogBuf;

        private static readonly Regex _cmtraceRx = new Regex(
            @"<!\[LOG\[(?<msg>.*?)\]LOG\]!><time=""(?<time>[^""]+)""\s+date=""[^""]+""\s+component=""(?<comp>[^""]*)""\s+context=""[^""]+""\s+type=""(?<type>[^""]*)""\s+thread=""[^""]+""\s+file=""[^""]*"">",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public AutoExecutePage() { InitializeComponent(); Loaded += (_, _) => { ApplyViewLock(); ApplyLogTabStyles(); }; }

        // ── View-lock: page is read-only; only Cancel is user-interactive ──
        // Install/Uninstall stay enabled (automation RaiseEvents them) but are
        // greyed and non-clickable for the user. Browse/Clear/Repair are hidden.
        private bool _viewLockApplied;
        private void ApplyViewLock()
        {
            if (_viewLockApplied) return;
            _viewLockApplied = true;

            // 1. Make all inputs read-only for the user (automation sets by property).
            AutoViewLock.LockInputs(this);

            // 2. Hide buttons not used by the automation.
            AutoViewLock.Hide(BtnBrowse);
            AutoViewLock.Hide(BtnClear);
            AutoViewLock.Hide(BtnClearLog);
            AutoViewLock.Hide(BtnAdminRepair);
            AutoViewLock.Hide(BtnSystemRepair);

            // 3. Grey the kept action buttons (still enabled for automation).
            AutoViewLock.GreyKeepEnabled(BtnAdminInstall);
            AutoViewLock.GreyKeepEnabled(BtnAdminUninstall);
            AutoViewLock.GreyKeepEnabled(BtnSystemInstall);
            AutoViewLock.GreyKeepEnabled(BtnSystemUninstall);

            // 4. Cancel stays fully interactive (enabled only while running).
            if (BtnCancelHeader != null) { BtnCancelHeader.IsEnabled = false; }
        }

        public void SetContext(IAutoPackageContext context) => _context = context;

        // ── Open Application (Package settings) ───────────────────────────
        // When "Yes", RunScript opens the installed app's main exe after a successful
        // Install, holds it 2-3s, then closes it before the next chained step runs.
        // A failure to open aborts the chain. _openAppName is the app display name used
        // to resolve the main exe from a Desktop / Start Menu shortcut.
        private string _openApplication = "No";
        private string _openAppName     = "";

        public void SetOpenApplication(string openApplication, string appName)
        {
            _openApplication = string.IsNullOrWhiteSpace(openApplication) ? "No" : openApplication.Trim();
            _openAppName     = appName ?? "";
        }

        // ── Public API ────────────────────────────────────────────────────

        public void SetScriptPath(string ps1Path)
            => Dispatcher.Invoke(() => { _scriptPs1Path = ps1Path ?? ""; RefreshPsadtCommandBoxes(); });

        // Called by the orchestrator with the app's configured PSADT Install/Uninstall commands
        // (Package settings). Stored raw; the boxes show them with the relative script path
        // resolved to the package folder.
        public void SetPsadtCommands(string installCmd, string uninstallCmd)
            => Dispatcher.Invoke(() =>
            {
                _psadtInstallCmd   = installCmd   ?? "";
                _psadtUninstallCmd = uninstallCmd ?? "";
                RefreshPsadtCommandBoxes();
            });

        // Fills the PSADT Install / PSADT Uninstall boxes with the configured command, its relative
        // Invoke-AppDeployToolkit.exe/.ps1 replaced by the full quoted path in the package folder.
        // Everything else in the command is kept verbatim. Falls back to the .exe launcher when no
        // command was configured.
        private void RefreshPsadtCommandBoxes()
        {
            string inst = string.IsNullOrWhiteSpace(_psadtInstallCmd)   ? "Invoke-AppDeployToolkit.exe"           : _psadtInstallCmd;
            string unin = string.IsNullOrWhiteSpace(_psadtUninstallCmd) ? "Invoke-AppDeployToolkit.exe Uninstall" : _psadtUninstallCmd;
            if (TxtScriptPath     != null) TxtScriptPath.Text     = ResolvePsadtCommand(inst);
            if (TxtPsadtUninstall != null) TxtPsadtUninstall.Text = ResolvePsadtCommand(unin);
        }

        private string ResolvePsadtCommand(string cmd)
        {
            string c = (cmd ?? "").Trim();
            if (c.Length == 0) return "";
            string dir;
            try { dir = Path.GetDirectoryName((_scriptPs1Path ?? "").Trim()) ?? ""; }
            catch { dir = ""; }
            if (dir.Length == 0) return c;   // no package path known yet - show the raw command
            foreach (string name in new[] { "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.ps1" })
            {
                int idx = c.IndexOf(name, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                char before = idx > 0 ? c[idx - 1] : ' ';
                if (before == '\\' || before == '/' || before == '"') break;   // already carries a path
                c = c.Substring(0, idx) + "\"" + Path.Combine(dir, name) + "\"" + c.Substring(idx + name.Length);
                break;
            }
            return c;
        }

        public void SetScriptAndInstall(string ps1Path, Action? onAfterInstall = null, bool useSystemContext = false)
        {
            if (string.IsNullOrWhiteSpace(ps1Path) || !File.Exists(ps1Path)) return;
            Dispatcher.Invoke(() =>
            {
                SetScriptPath(ps1Path);
                _onAfterDeploy = onAfterInstall;
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                t.Tick += (_, _) => { t.Stop(); Button? b = useSystemContext ? BtnSystemInstall : BtnAdminInstall; if (b?.IsEnabled == true) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                t.Start();
            });
        }

        public void SetScriptAndUninstall(string ps1Path, Action? onAfterUninstall = null, bool useSystemContext = false)
        {
            if (string.IsNullOrWhiteSpace(ps1Path) || !File.Exists(ps1Path)) return;
            Dispatcher.Invoke(() =>
            {
                SetScriptPath(ps1Path);
                _onAfterDeploy = onAfterUninstall;
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                t.Tick += (_, _) => { t.Stop(); Button? b = useSystemContext ? BtnSystemUninstall : BtnAdminUninstall; if (b?.IsEnabled == true) b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                t.Start();
            });
        }

        // ── CancelAndClear — called by AutoPackagePage.ResetAllTabs() ────

        /// <summary>
        /// Cancels any in-progress Install / Uninstall / Repair execution,
        /// kills the child process, clears the script path, and resets the
        /// log to its default "[Ready]" message.
        /// Safe to call at any time — no-op when nothing is running.
        /// </summary>
        public void CancelAndClear()
        {
            // 1. Signal cancellation so LaunchDeployment's async loop exits
            try { _cts?.Cancel(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            _cts = null;

            // 2. Kill the child process tree (if still alive)
            try
            {
                if (_runningProcess != null && !_runningProcess.HasExited)
                    KillProcessTree(_runningProcess.Id);
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            _runningProcess = null;
            _activePs1Path  = null;
            _activePidFile  = null;

            // 3. Drop the pending callback so it does not fire for the old run
            _onAfterDeploy = null;

            // 4. Reset the UI on the dispatcher thread
            void DoReset()
            {
                _scriptPs1Path = "";
                if (TxtScriptPath     != null) TxtScriptPath.Text     = "";
                if (TxtPsadtUninstall != null) TxtPsadtUninstall.Text = "";

                if (TxtEventLog != null)
                {
                    _installLogBuf.Clear();
                    _uninstallLogBuf.Clear();
                    _logWriteMode = "Install";
                    SelectLogTab("Install");
                }

                if (BorderAdminRunning  != null) BorderAdminRunning.Visibility  = Visibility.Collapsed;
                if (BorderSystemRunning != null) BorderSystemRunning.Visibility = Visibility.Collapsed;
                if (PrgAdminProgress    != null) PrgAdminProgress.Visibility    = Visibility.Collapsed;
                if (PrgSystemProgress   != null) PrgSystemProgress.Visibility   = Visibility.Collapsed;
                if (ExecTopProgressBar  != null) ExecTopProgressBar.Visibility  = Visibility.Collapsed;
                if (TxtAdminRunning     != null) TxtAdminRunning.Text           = "";
                if (TxtSystemRunning    != null) TxtSystemRunning.Text          = "";

                SetAllButtonsEnabled(true);
                if (BtnCancelHeader != null) BtnCancelHeader.IsEnabled = false;
            }

            if (Dispatcher.CheckAccess()) DoReset();
            else Dispatcher.Invoke(DoReset);
        }

        // ── XAML handlers ─────────────────────────────────────────────────

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Select Invoke-AppDeployToolkit.ps1", Filter = "PowerShell Scripts (*.ps1)|*.ps1|All Files (*.*)|*.*" };
            if (dlg.ShowDialog() == true) { _scriptPs1Path = dlg.FileName; RefreshPsadtCommandBoxes(); }
        }
        private void BtnClear_Click(object sender, RoutedEventArgs e)    { _scriptPs1Path = ""; if (TxtScriptPath != null) TxtScriptPath.Text = ""; if (TxtPsadtUninstall != null) TxtPsadtUninstall.Text = ""; }
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            BufFor(_shownLogMode).Clear();
            if (TxtEventLog != null) TxtEventLog.Text = ReadyLogText;
        }
        private void BtnCancelHeader_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            if (BtnCancelHeader != null) BtnCancelHeader.IsEnabled = false;
            Log("⚠  Cancel requested — waiting for process to stop…");
            Task.Run(() =>
            {
                try { if (_runningProcess != null && !_runningProcess.HasExited) KillProcessTree(_runningProcess.Id); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                string? pf = _activePidFile;
                if (!string.IsNullOrEmpty(pf) && File.Exists(pf))
                {
                    Thread.Sleep(500);
                    try { if (int.TryParse(File.ReadAllText(pf).Trim(), out int ip) && ip > 0) { Process.Start(new ProcessStartInfo("taskkill", $"/PID {ip} /T /F") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })?.WaitForExit(5000); } } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
                Dispatcher.BeginInvoke(new Action(() => Log("⚠  All processes terminated.")));
            });
        }

        private void BtnAdminInstall_Click(object sender, RoutedEventArgs e)    => LaunchDeployment("Install",   false);
        private void BtnAdminUninstall_Click(object sender, RoutedEventArgs e)  => LaunchDeployment("Uninstall", false);
        private void BtnAdminRepair_Click(object sender, RoutedEventArgs e)     => LaunchDeployment("Repair",    false);
        private void BtnSystemInstall_Click(object sender, RoutedEventArgs e)   => LaunchDeployment("Install",   true);
        private void BtnSystemUninstall_Click(object sender, RoutedEventArgs e) => LaunchDeployment("Uninstall", true);
        private void BtnSystemRepair_Click(object sender, RoutedEventArgs e)    => LaunchDeployment("Repair",    true);

        private void SetAllButtonsEnabled(bool on)
        {
            // View-lock: Install/Uninstall must stay ENABLED at all times so the
            // automation can RaiseEvent them; they are greyed + non-clickable for the
            // user via ApplyViewLock(). So we deliberately do NOT toggle IsEnabled on
            // them here (doing so would let the user click them, or — if disabled —
            // would block the automation's programmatic click).
            // This method is kept for call-site compatibility but is now a no-op for
            // the action buttons. Highlighting of the running button is handled in
            // LaunchDeployment via AutoViewLock.Highlight/Unhighlight.

            // The EVENT LOG source tabs ARE user-only (never RaiseEvent'd), so block them
            // while a deploy runs and re-enable when it completes. The auto-switch to the
            // active run's tab still happens: SelectLogTab() runs programmatically and does
            // not depend on the buttons being enabled.
            void SetTabs()
            {
                if (BtnLogTabInstall   != null) BtnLogTabInstall.IsEnabled   = on;
                if (BtnLogTabUninstall != null) BtnLogTabUninstall.IsEnabled = on;
            }
            if (Dispatcher.CheckAccess()) SetTabs();
            else Dispatcher.Invoke(SetTabs);

            _ = on;
        }

        // ── LaunchDeployment ──────────────────────────────────────────────

        private async void LaunchDeployment(string deployMode, bool systemContext)
        {
            string ps1 = (_scriptPs1Path ?? "").Trim();
            if (string.IsNullOrWhiteSpace(ps1) || !File.Exists(ps1))
            { AutoStatusDialog.Show(this, "Execute Package - Script Required", "▶", false, new[] { ("Status", "Please select a valid Invoke-AppDeployToolkit.ps1 file.", false) }); return; }

            string scriptDir = Path.GetDirectoryName(ps1) ?? ".";
            _logFilePath = DefaultLogDir;
            _deployStartTime = DateTime.Now;
            // Route this run's output to its own buffer and show that tab. Clear only this buffer,
            // so the other mode's log (e.g. a prior Install) stays intact and viewable via its tab.
            _logWriteMode = LogBucketFor(deployMode);
            BufFor(_logWriteMode).Clear();
            SelectLogTab(_logWriteMode);
            Log($"Script  : {ps1}"); Log($"Mode    : {deployMode}");
            Log($"Context : {(systemContext ? "System (PsExec)" : "Administrator")}");
            Log($"Log dir : {_logFilePath}"); Log("─────────────────────────────────────────");

            _activeDeployMode = deployMode;
            SetAllButtonsEnabled(false);
            if (BtnCancelHeader != null) BtnCancelHeader.IsEnabled = true;

            // Req 3: highlight the button that is actually running (accent + bold),
            // for the correct context (Administrator vs System).
            Button? runningBtn =
                systemContext
                    ? (deployMode == "Uninstall" ? BtnSystemUninstall : BtnSystemInstall)
                    : (deployMode == "Uninstall" ? BtnAdminUninstall  : BtnAdminInstall);
            AutoViewLock.Highlight(runningBtn);
            // Show the single top thin progress bar (Download-tab style) instead of the
            // per-context header bars.
            if (ExecTopProgressBar != null) { ExecTopProgressBar.IsIndeterminate = true; ExecTopProgressBar.Visibility = Visibility.Visible; }
            if (systemContext) { if (BorderSystemRunning != null) BorderSystemRunning.Visibility = Visibility.Visible; if (TxtSystemRunning != null) TxtSystemRunning.Text = $"{deployMode} in progress…"; }
            else               { if (BorderAdminRunning  != null) BorderAdminRunning.Visibility  = Visibility.Visible; if (TxtAdminRunning  != null) TxtAdminRunning.Text  = $"{deployMode} in progress…"; }

            _cts = new CancellationTokenSource();
            string psadtCmd = deployMode == "Install"   ? (TxtScriptPath?.Text?.Trim()     ?? "")
                            : deployMode == "Uninstall" ? (TxtPsadtUninstall?.Text?.Trim() ?? "")
                            : "";   // Repair keeps the original wrapper (no configured command)
            try   { await Task.Run(() => RunScript(ps1, deployMode, systemContext, _cts.Token, psadtCmd)); }
            catch (OperationCanceledException) { Log("⚠  Execution cancelled."); }
            catch (Exception ex)               { Log($"❌ Error: {ex.Message}"); }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    SetAllButtonsEnabled(true);
                    AutoViewLock.UnhighlightAll(BtnAdminInstall, BtnAdminUninstall, BtnSystemInstall, BtnSystemUninstall);
                    if (BtnCancelHeader     != null) BtnCancelHeader.IsEnabled     = false;
                    if (BorderAdminRunning  != null) BorderAdminRunning.Visibility  = Visibility.Collapsed;
                    if (BorderSystemRunning != null) BorderSystemRunning.Visibility = Visibility.Collapsed;
                    if (PrgAdminProgress    != null) PrgAdminProgress.Visibility    = Visibility.Collapsed;
                    if (PrgSystemProgress   != null) PrgSystemProgress.Visibility   = Visibility.Collapsed;
                    if (ExecTopProgressBar  != null) ExecTopProgressBar.Visibility  = Visibility.Collapsed;
                });
                _runningProcess = null; _cts?.Dispose(); _cts = null;
            }
        }

        // ── RunScript ─────────────────────────────────────────────────────

        private void RunScript(string ps1, string deployMode, bool systemContext, CancellationToken ct, string psadtCmd = "")
        {
            string scriptDir = Path.GetDirectoryName(ps1) ?? ".";
            // When the PSADT Install / PSADT Uninstall box carries a command, run THAT (via cmd /c
            // so any launcher works) instead of the built-in "powershell -File <ps1>" wrapper.
            // Repair, or an empty box, falls back to the original wrapper below.
            bool useConfiguredCmd = !string.IsNullOrWhiteSpace(psadtCmd);
            _activePs1Path = ps1;
            string[] pkgParts = (Path.GetDirectoryName(scriptDir) is string pr ? Path.GetFileName(pr) : "")
                .Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            string scriptArg   = deployMode == "Install" ? "" : deployMode;
            string exitFile    = Path.Combine(Path.GetTempPath(), "AP_exit_"  + Guid.NewGuid().ToString("N") + ".txt");
            string pidFile     = Path.Combine(Path.GetTempPath(), "AP_pid_"   + Guid.NewGuid().ToString("N") + ".txt");
            string wrapperPs1  = Path.Combine(Path.GetTempPath(), "AP_wrap_"  + Guid.NewGuid().ToString("N") + ".ps1");
            string cmdFile     = "";   // temp .cmd holding the configured PSADT command (when used)
            Action? afterDeploy = null;
            // ARP snapshot taken just before the install runs, so the success block can tell which
            // uninstall entry this install added and pull its icon for the per-app Icon.png.
            HashSet<string>? arpBefore = null;

            // Shared per-package log, revision-suffixed: %TEMP%\AutoPackage_{PackageFolder}.log -
            // the same file the Package, Intunewin and Publish steps append to. A start failure
            // (see PsExec below) is otherwise only written to the on-screen EVENT LOG, which the
            // next run clears, so nothing about it survives.
            string pkgFolderName = Path.GetDirectoryName(scriptDir) is string pkgRootDir ? Path.GetFileName(pkgRootDir) : "";
            string pkgLogPath    = pkgFolderName.Length > 0
                ? Path.Combine(Path.GetTempPath(), $"AutoPackage_{pkgFolderName}.log")
                : "";
            void LogToPackageFile(string msg)
            {
                if (pkgLogPath.Length == 0) return;
                try { File.AppendAllText(pkgLogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}"); }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            // A failure that stops the deployment process from ever starting (PsExec missing,
            // UAC declined, launch failed) still has to release the auto-flow. On the normal path
            // the terminus runs onAfterDeploy or signals an abort from ShowResultDialog; the early
            // returns below skip both, so without this the auto-flow lock is never released and the
            // sub-tabs stay frozen on the Execute tab. Mark the package step failed and signal the
            // abort - which unlocks the tabs and returns to the Download tab - matching the failure
            // branch in ShowResultDialog.
            void AbortAutoFlowOnStartFailure()
            {
                try
                {
                    AutoFlowStatus.Set(
                        deployMode == "Uninstall" ? AutoFlowStatus.UninstallPackage : AutoFlowStatus.InstallPackage,
                        AutoFlowStatus.State.Failed);
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                _onAfterDeploy = null;
                try { _context?.NotifyAutoFlowAborted(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            try
            {
                int exitCode = -1;

                // Build wrapper content
                string wrapperContent;
                if (useConfiguredCmd)
                {
                    // Run the box command verbatim through a one-line .cmd so any launcher (the
                    // Invoke-AppDeployToolkit.exe or a full powershell line) works and cmd returns
                    // its exit code. %SystemRoot% and friends expand at run time.
                    cmdFile = Path.Combine(Path.GetTempPath(), "AP_cmd_" + Guid.NewGuid().ToString("N") + ".cmd");
                    File.WriteAllText(cmdFile, "@echo off\r\n" + psadtCmd + "\r\nexit /b %ERRORLEVEL%\r\n", new System.Text.UTF8Encoding(false));
                    Log("Command : " + psadtCmd);
                }
                if (systemContext)
                {
                    string? psExec = FindPsExec(scriptDir);
                    if (psExec == null)
                    {
                        Log("❌ PsExec.exe not found. Place it in the Templates folder or PATH.");
                        // Same failure, two more places: the shared per-package log (so it is not
                        // lost when the EVENT LOG is cleared) and the Download Information column
                        // on the Download tab, which is where the flow returns after the abort.
                        string psExecMsg  = $"PsExec.exe not found on this machine: \"{PsExecTemplatesDir}\"";
                        string psExecHint = "System Context needs PsExec.exe. Place it in the Templates folder or on PATH, then run again.";
                        LogToPackageFile($"{deployMode} package ABORTED: {psExecMsg}");
                        LogToPackageFile(psExecHint);
                        ReportPackageErrorToDownloadTab($"{deployMode} package failed", psExecMsg, psExecHint);
                        AbortAutoFlowOnStartFailure();
                        return;
                    }
                    Log($"PsExec  : {psExec}");
                    string ee = exitFile.Replace("\\","\\\\").Replace("\"","\\\""), ep = pidFile.Replace("\\","\\\\").Replace("\"","\\\"");
                    // Q() wraps a value in LITERAL double quotes inside the PowerShell array element.
                    //
                    // Start-Process -ArgumentList takes an array, joins the elements with single
                    // spaces and adds NO quoting of its own. So an element holding a path with a
                    // space is split by the receiving process: 'C:\SwiftPack\Auto Package\...' is
                    // read as "-File C:\SwiftPack\Auto" plus stray arguments, the file is not found,
                    // and powershell.exe exits 0xFFFD0000 (-196608) before PSADT ever starts - which
                    // is why no .log appears in C:\Windows\Logs\Software when this happens.
                    //
                    // Escaping the single quotes is still needed for the array LITERAL; the double
                    // quotes are what survives the join. Both are required, they solve different
                    // problems.
                    static string Q(string v) => "\"" + v.Replace("'", "''") + "\"";
                    string ePsExec = psExec.Replace("'","''"), eScriptDir = Q(scriptDir), ePs1 = Q(ps1);
                    string psArgsTail = scriptArg.Length > 0
                        ? "'-File','" + ePs1 + "','-DeploymentType','" + scriptArg.Replace("'","''") + "'"
                        : "'-File','" + ePs1 + "'";
                    // Configured command runs as SYSTEM via cmd /c <cmdFile>; else the built-in line.
                    string sysApp = useConfiguredCmd
                        ? "'cmd.exe','/c','" + Q(cmdFile) + "'"
                        : "'powershell.exe','-ExecutionPolicy','Bypass','-NonInteractive','-WindowStyle','Hidden'," + psArgsTail;
                    wrapperContent = "$exitPath=\"" + ee + "\"\r\n$pidPath=\"" + ep + "\"\r\ntry{\r\n$psArgs=@('-accepteula','-s','-nobanner','-w','" + eScriptDir + "'," + sysApp + ")\r\n$p=Start-Process -FilePath '" + ePsExec + "' -ArgumentList $psArgs -Wait -PassThru -WindowStyle Hidden\r\n$p.Id|Out-File $pidPath -Encoding ASCII -Force\r\n$p.ExitCode|Out-File $exitPath -Encoding ASCII -Force\r\n}catch{-1|Out-File $exitPath -Encoding ASCII -Force}\r\n";
                }
                else
                {
                    string ee = exitFile.Replace("\\","\\\\").Replace("\"","\\\""), ep = pidFile.Replace("\\","\\\\").Replace("\"","\\\"");
                    // Same Start-Process join problem as the SYSTEM branch above: the array elements
                    // are concatenated with spaces and nothing quotes them, so a package path such as
                    // C:\SwiftPack\Auto Package\... is cut at "Auto". This is the branch that failed
                    // with -196608 on an Administrator-context Install.
                    string ePs1 = "\"" + ps1.Replace("'", "''") + "\"";
                    string args = scriptArg.Length > 0 ? ",'" + scriptArg.Replace("'","''") + "'" : "";
                    // Configured command runs elevated via cmd /c <cmdFile>; else the built-in line.
                    string eCmd = "\"" + cmdFile.Replace("'", "''") + "\"";
                    string adminInner = useConfiguredCmd
                        ? "Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c','" + eCmd + "')"
                        : "Start-Process -FilePath 'powershell.exe' -ArgumentList @('-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File','" + ePs1 + "'" + args + ")";
                    wrapperContent = "$exitPath=\"" + ee + "\"\r\n$pidPath=\"" + ep + "\"\r\ntry{\r\n$p=" + adminInner + " -Wait -PassThru -WindowStyle Hidden\r\n$p.Id|Out-File $pidPath -Encoding ASCII -Force\r\n$p.ExitCode|Out-File $exitPath -Encoding ASCII -Force\r\n}catch{-1|Out-File $exitPath -Encoding ASCII -Force}\r\n";
                }

                File.WriteAllText(wrapperPs1, wrapperContent, new System.Text.UTF8Encoding(false));
                _activePidFile = pidFile;
                Log($"Starting process ({(systemContext ? "SYSTEM via PsExec" : "Administrator, UAC elevation")})…");
                Log("ℹ  Live output is read from the PSADT log file.");

                using var proc = new Process { StartInfo = new ProcessStartInfo { FileName = "powershell.exe", Arguments = "-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"" + wrapperPs1 + "\"", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = scriptDir } };
                _runningProcess = proc;

                string? tailTarget = FindDeployLog(_logFilePath, deployMode, pkgParts, _deployStartTime.AddSeconds(-5));
                long tailPos = 0; int tailLines = 0;
                if (tailTarget != null) try { using var fs = new FileStream(tailTarget, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); tailPos = fs.Length; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                // Snapshot the uninstall registry immediately before the install runs (Install
                // only). The success block diffs against this to find the newly added app's icon.
                if (deployMode == "Install") arpBefore = SnapshotArpKeys();

                try { proc.Start(); }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log("⚠  UAC declined."); AbortAutoFlowOnStartFailure(); return; }
                catch (Exception ex) { Log("❌ Failed to start: " + ex.Message); AbortAutoFlowOnStartFailure(); return; }

                while (!proc.WaitForExit(800))
                {
                    if (ct.IsCancellationRequested) { try { if (!proc.HasExited) KillProcessTree(proc.Id); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } Log("⚠  Cancelled."); AbortAutoFlowOnStartFailure(); return; }
                    if (tailTarget == null || !File.Exists(tailTarget)) { tailTarget = FindDeployLog(_logFilePath, deployMode, pkgParts, _deployStartTime.AddSeconds(-5)); tailPos = 0; }
                    if (tailTarget != null) tailPos = TailLogFile(tailTarget, tailPos, ref tailLines);
                }

                // Drain remaining log output
                tailTarget ??= FindDeployLog(_logFilePath, deployMode, pkgParts, _deployStartTime.AddSeconds(-5));
                { long p = -1; int s = 0; var dl = DateTime.UtcNow.AddSeconds(2);
                  while (DateTime.UtcNow < dl) { if (tailTarget != null) { tailPos = TailLogFile(tailTarget, tailPos, ref tailLines); long sz = new FileInfo(tailTarget).Length; if (sz == p) { if (++s >= 2) break; } else { s = 0; p = sz; } } Thread.Sleep(150); } }

                // Wait for inner process tree
                if (File.Exists(pidFile)) try { if (int.TryParse(File.ReadAllText(pidFile).Trim(), out int ip) && ip > 0) WaitForProcessTreeExit(ip, ct); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                Dispatcher.Invoke(new Action(() => {}), System.Windows.Threading.DispatcherPriority.Background);

                // Read exit code
                if (File.Exists(exitFile)) try { if (int.TryParse(File.ReadAllText(exitFile).Trim(), out int parsed)) exitCode = parsed; } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                // Result
                Log("─────────────────────────────────────────");
                bool success = exitCode == 0 || exitCode == 3010;
                AutoFlowStatus.Set(
                    deployMode == "Uninstall" ? AutoFlowStatus.UninstallPackage : AutoFlowStatus.InstallPackage,
                    success ? AutoFlowStatus.State.Ok : AutoFlowStatus.State.Failed);
                Log((success ? "✅" : "❌") + " Process exited with code " + exitCode);
                if (exitCode == 3010) Log("ℹ  Exit code 3010 = success, reboot required.");

                // Write the 256px per-app Icon.png from the app this install added, so Publish has
                // a real icon to send. On the UI thread to match the WPF imaging path, and gated on
                // a successful Install. An existing Icon.png is left untouched.
                if (success && deployMode == "Install")
                {
                    var arpSnap = arpBefore;
                    Dispatcher.Invoke(() =>
                    {
                        try { WriteAppLogo(arpSnap); }
                        catch (Exception ex) { Log("⚠  Icon.png skipped: " + ex.Message); }
                    });
                }

                string? psadtLog = FindDeployLog(_logFilePath, deployMode, pkgParts, _deployStartTime.AddSeconds(-5));
                string? copiedLog = null;
                if (psadtLog != null)
                {
                    Log("📄 Log: " + psadtLog);
                    // Copy log to the package's Documents folder (Auto Package only - no
                    // external config). Failures used to vanish into Debug output, which made
                    // "the log is not in Documents" undiagnosable from the event log alone;
                    // every way this can not-happen now says so in the log.
                    try
                    {
                        string? pkgRoot = Directory.GetParent(scriptDir)?.FullName;
                        if (string.IsNullOrEmpty(pkgRoot) || !Directory.Exists(pkgRoot))
                            Log("⚠  Log NOT copied - no package root above " + scriptDir);
                        else if (!File.Exists(psadtLog))
                            Log("⚠  Log NOT copied - source vanished: " + psadtLog);
                        else
                        {
                            string docsDir = Path.Combine(pkgRoot, "Documents");
                            Directory.CreateDirectory(docsDir);
                            string dest = Path.Combine(docsDir, Path.GetFileName(psadtLog));
                            File.Copy(psadtLog, dest, overwrite: true);
                            copiedLog = dest;
                            Log("📂 Log copied → " + dest);
                        }
                    }
                    catch (Exception exCopy) { Log("⚠  Log NOT copied - " + exCopy.GetType().Name + ": " + exCopy.Message); }
                }
                else Log("ℹ  Log folder: " + _logFilePath + " (no .log files found yet)");

                // Hand the finished deploy to the strip: remember the best path per mode
                // (in-package copy preferred) and announce it. AutoDocumentPage listens and
                // owns everything document-shaped from here.
                string? bestLog = copiedLog ?? psadtLog;
                if (deployMode == "Uninstall") LastAutoUninstallLogPath = bestLog;
                else                           LastAutoInstallLogPath   = bestLog;
                if (bestLog != null)
                    Log("🗎 " + deployMode + " log path recorded for the Document tab");
                try { DeployCompleted?.Invoke(deployMode, success, copiedLog, psadtLog, scriptDir); }
                catch (Exception exEvt) { Log("⚠  Document hand-off failed - " + exEvt.GetType().Name + ": " + exEvt.Message); }

                // ── Open Application (Package settings: Open Application = Yes) ──
                // After a successful Install, open the app's main exe (resolved from a
                // Desktop / Start Menu shortcut), hold it 2-3 s, then close it. Whether
                // it opened or not is logged. A failure to open flips this step to Failed
                // so the chain aborts and the next step (Uninstall / Intunewin / Publish)
                // does NOT run. Runs on this background thread, so the hold does not
                // freeze the UI.
                if (success && deployMode == "Install"
                    && _openApplication.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                {
                    Log("─────────────────────────────────────────");
                    Log("Open Application : verifying the installed app launches…");
                    bool opened = OpenAndCloseInstalledApp(_openAppName);
                    if (opened)
                        Log("✅ Open Application succeeded - app launched and was closed.");
                    else
                    {
                        success = false;
                        AutoFlowStatus.Set(AutoFlowStatus.InstallPackage, AutoFlowStatus.State.Failed);
                        Log("❌ Open Application failed - the app did not launch; stopping before the next step.");
                    }
                }

                bool hasNext = _onAfterDeploy != null;
                afterDeploy  = _onAfterDeploy;
                _onAfterDeploy = null;

                // Read GlobalShowMessageBox via reflection — no compile-time dep on SourceDownloadPage
                bool showDlg = true;
                try { var sdType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } }).FirstOrDefault(t => t.FullName == "SwiftPack.Views.SourceDownloadPage"); var prop = sdType?.GetProperty("GlobalShowMessageBox", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); if (prop != null) showDlg = (bool)(prop.GetValue(null) ?? true); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                string dm = deployMode; bool suc = success; int ec = exitCode; string? pl = psadtLog, lp = _logFilePath; DateTime st = _deployStartTime; string sd = scriptDir;
                Dispatcher.BeginInvoke(new Action(() => ShowResultDialog(dm, suc, ec, pl, lp, st, sd, hasNext, afterDeploy, showDlg)));
            }
            finally
            {
                _activePs1Path = null; _activePidFile = null;
                string wp = wrapperPs1, ef = exitFile, pf = pidFile, cf = cmdFile;
                Task.Run(() => { Thread.Sleep(3000); try { if (File.Exists(wp)) File.Delete(wp); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } try { if (File.Exists(ef)) File.Delete(ef); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } try { if (File.Exists(pf)) File.Delete(pf); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } try { if (cf.Length > 0 && File.Exists(cf)) File.Delete(cf); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } });
            }
        }

        // ── ShowResultDialog ─────────────────────────────────────────────

        // Give a dialog button the highlighted "primary action" look: a filled
        // accent background, rounded corners and a subtle hover. Theme resources
        // keep it readable in both light and dark modes, mirroring the Publish
        // Package dialog. Applied on top of any existing Style (local values win
        // in WPF), so callers keep their own sizing.
        private static void AxApplyPrimaryLook(Button b)
        {
            Brush bg     = Application.Current.TryFindResource("DlgBtnBlueBgBrush")     as Brush ?? new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5));
            Brush fg     = Application.Current.TryFindResource("DlgBtnBlueFgBrush")     as Brush ?? Brushes.White;
            Brush border = Application.Current.TryFindResource("DlgBtnBlueBorderBrush") as Brush ?? bg;

            b.Background      = bg;
            b.Foreground      = fg;
            b.BorderBrush     = border;
            b.BorderThickness = new Thickness(1);
            b.Cursor          = System.Windows.Input.Cursors.Hand;

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
        private static TextBlock AxDecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };

        private void ShowResultDialog(string deployMode, bool success, int exitCode, string? logFile, string? logDir, DateTime startTime, string? packageFolder, bool hasNextCallback, Action? afterDeploy, bool showDialog)
        {
            var endTime = DateTime.Now; var elapsed = endTime - startTime;
            string es = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : elapsed.TotalSeconds >= 1 ? $"{(int)elapsed.TotalSeconds}s" : $"{(int)elapsed.TotalMilliseconds}ms";
            string modeLabel = deployMode switch { "Install" => "Install", "Uninstall" => "Uninstall", "Repair" => "Repair", _ => deployMode };
            string winTitle  = success ? $"Execute Package - {modeLabel} Complete" : $"Execute Package - {modeLabel} Failed";

            var winBg = Application.Current.TryFindResource("WindowBgBrush") as Brush ?? Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? Brushes.LightGray;
            var win = new Window { Title = winTitle, Width = 460, MaxWidth = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false };
            var outerBorder = new Border { Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black } };
            win.Content = outerBorder;
            var outerGrid = new Grid(); outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); outerBorder.Child = outerGrid;

            // Title bar
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63)); Brush ttb = Brushes.White; Brush tsb = new SolidColorBrush(Color.FromArgb(0xCC,0xFF,0xFF,0xFF));
            var titleBar = new Border { Background = titleBarBg, BorderBrush = borderClr, BorderThickness = new Thickness(0,0,0,1), CornerRadius = new CornerRadius(10,10,0,0), Padding = new Thickness(16,6,12,6) };
            titleBar.MouseLeftButtonDown += (_, _) => win.DragMove(); Grid.SetRow(titleBar, 0);
            var tg = new Grid(); tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tIcon = new TextBlock { Text = "▶", FontSize = 14, Foreground = ttb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            var tText = new TextBlock { Text = winTitle, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ttb, VerticalAlignment = VerticalAlignment.Center };
            var tClose = new Button { Content = "✕", FontSize = 11, Foreground = tsb, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6,2,6,2) };
            tClose.Click += (_, _) => win.Close(); Grid.SetColumn(tIcon, 0); Grid.SetColumn(tText, 1); Grid.SetColumn(tClose, 2); tg.Children.Add(tIcon); tg.Children.Add(tText); tg.Children.Add(tClose); titleBar.Child = tg; outerGrid.Children.Add(titleBar);

            // Content
            var root = new Grid { Margin = new Thickness(8) }; Grid.SetRow(root, 1); outerGrid.Children.Add(root);
            var boxBg = Application.Current.TryFindResource("InputBgBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xF0,0xF1,0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD,0xDF,0xEA));
            var lblFg = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x55,0x5C,0x7A));
            var valFg = Application.Current.TryFindResource("TextPrimaryBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A,0x1D,0x2E));
            Brush codeBrush = success ? new SolidColorBrush(Color.FromRgb(0x2E,0x7D,0x32)) : new SolidColorBrush(Color.FromRgb(0xC6,0x28,0x28));

            var rows = new List<(string l, string v, bool h)> { ("Time", $"Started: {startTime:HH:mm:ss}  Finished: {endTime:HH:mm:ss}  Duration: {es}", false), ("Path", packageFolder ?? deployMode, false), ("Exit Code", exitCode.ToString(), true) };
            if (!string.IsNullOrEmpty(logFile)) rows.Add(("Log", logFile, false));
            if (exitCode == 3010) rows.Add(("Note", "Reboot required to complete the operation.", false));

            var details = new Grid(); details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) }); details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < rows.Count; i++)
            {
                details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (i < rows.Count - 1) details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
                int r = i * 2;
                var lbl = new TextBlock { Text = rows[i].l, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
                var box = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10,6,10,6) };
                box.Child = new TextBlock { Text = rows[i].v, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = rows[i].h ? codeBrush : valFg, FontWeight = rows[i].h ? FontWeights.Bold : FontWeights.Normal, FontFamily = new FontFamily("Consolas") };
                Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0); Grid.SetRow(box, r); Grid.SetColumn(box, 1); details.Children.Add(lbl); details.Children.Add(box);
            }
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int btnRow = rows.Count * 2;
            Style? sec = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            int btnCount = 1 + (logDir != null && Directory.Exists(logDir) ? 1 : 0) + (logFile != null && File.Exists(logFile) ? 1 : 0);
            var btnGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = btnCount };
            if (logDir  != null && Directory.Exists(logDir))  { var b = new Button { Content = "📁 Open Path", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(0,0,4,0) }; b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", packageFolder ?? logDir) { UseShellExecute = true }); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }; btnGrid.Children.Add(b); }
            if (logFile != null && File.Exists(logFile))       { var b = new Button { Content = "📄 Open Log",  Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(4,0,4,0) }; b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(logFile) { UseShellExecute = true }); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }; btnGrid.Children.Add(b); }
            var ok = new Button { Content = "✕ Close", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(4,0,0,0), IsDefault = true };
            ok.Click += (_, _) => win.Close(); btnGrid.Children.Add(ok);
            // Highlight the first button as the default action (uniform with other pages).
            if (btnGrid.Children[0] is Button firstBtn) AxApplyPrimaryLook(firstBtn);
            Grid.SetRow(btnGrid, btnRow); Grid.SetColumn(btnGrid, 1); details.Children.Add(btnGrid);
            var rowIcon = AxDecisionIcon();
            Grid.SetRow(rowIcon, btnRow); Grid.SetColumn(rowIcon, 0); details.Children.Add(rowIcon);
            root.Children.Add(details);

            if (success && !showDialog)
                win.Loaded += (_, _) => win.Dispatcher.BeginInvoke(new Action(() => win.Close()), System.Windows.Threading.DispatcherPriority.Background);

            win.ShowDialog();

            // Fire next step — NO ValidatePackagePage / SourceDownloadPage reset.
            // FAILURE RULE: only proceed to the next chained action when THIS step
            // succeeded (exit code 0 or 3010). If Install/Uninstall package failed,
            // do NOT run the next action (e.g. Uninstall package after Install package,
            // or Intunewin/Publish) — abort the chain.
            if (afterDeploy != null)
            {
                if (success)
                    Dispatcher.BeginInvoke(afterDeploy, System.Windows.Threading.DispatcherPriority.Normal);
                else
                {
                    Log($"❌ {deployMode} failed (exit {exitCode}) — skipping next action; chain aborted.");
                    // The chain is aborted on failure, so the normal terminus (Intunewin/Publish)
                    // that would release the auto-flow lock never runs. Release it now — right
                    // after the user closes this failure dialog — so the Download/Package columns
                    // and the top tabs become interactive again immediately.
                    try { _context?.NotifyAutoFlowAborted(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
            }
            else if (!success)
            {
                // No further chained action queued AND this step failed: this dialog IS the
                // terminus. Release the auto-flow lock on close so the tabs reactivate.
                try { _context?.NotifyAutoFlowAborted(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
        }

        // ── Log helpers ───────────────────────────────────────────────────

        private void Log(string msg)
        {
            AppendLog("\n" + $"[{DateTime.Now:HH:mm:ss}] {msg}");
        }

        // Append raw text (already including any leading newline) to the CURRENT write buffer, and
        // to the on-screen TextBox only when the tab for that buffer is the one showing. This is the
        // single funnel every log write goes through, so tab switching and buffering stay correct.
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
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Show a buffer in the TextBox and mark its tab active. Empty buffer shows the Ready hint.
        private void SelectLogTab(string mode)
        {
            mode = string.Equals(mode, "Uninstall", StringComparison.OrdinalIgnoreCase) ? "Uninstall" : "Install";
            _shownLogMode = mode;
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
            var active   = TryFindResource("LogTabBtnActive") as Style;
            var inactive = TryFindResource("LogTabBtn")       as Style;
            bool showInstall = string.Equals(_shownLogMode, "Install", StringComparison.OrdinalIgnoreCase);
            if (BtnLogTabInstall   != null) BtnLogTabInstall.Style   = showInstall ? active : inactive;
            if (BtnLogTabUninstall != null) BtnLogTabUninstall.Style = showInstall ? inactive : active;
        }

        private void BtnLogTabInstall_Click(object sender, RoutedEventArgs e)   => SelectLogTab("Install");
        private void BtnLogTabUninstall_Click(object sender, RoutedEventArgs e) => SelectLogTab("Uninstall");

        private static string ParseCmTraceTime(string raw) => raw.Length >= 8 && raw[2] == ':' && raw[5] == ':' ? raw.Substring(0, 8) : raw;

        private long TailLogFile(string path, long pos, ref int lineCount)
        {
            if (!File.Exists(path)) return pos;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // If the log was truncated/recreated since the last read (a repeat run overwrites the
                // same file), pos would be past the end - restart from the beginning so this run's
                // output actually shows.
                if (fs.Length < pos) pos = 0;
                fs.Seek(pos, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8, true);
                string chunk = sr.ReadToEnd(); if (string.IsNullOrEmpty(chunk)) return fs.Position;
                long newPos = fs.Position; var matches = _cmtraceRx.Matches(chunk); int lastEnd = 0;
                foreach (Match m in matches)
                {
                    if (m.Index > lastEnd) foreach (var pl in chunk.Substring(lastEnd, m.Index - lastEnd).Split('\n')) { string t = pl.Trim().Trim('\r'); if (!string.IsNullOrWhiteSpace(t)) { AppendLog("\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + t); lineCount++; } }
                    lastEnd = m.Index + m.Length;
                    string msg = m.Groups["msg"].Value.Trim(), ttype = m.Groups["type"].Value.Trim(), time = ParseCmTraceTime(m.Groups["time"].Value.Trim());
                    if (string.IsNullOrWhiteSpace(msg)) { lineCount++; continue; }
                    string prefix = ttype == "2" ? "⚠  " : ttype == "3" ? "❌ " : "";
                    var ml = (prefix + msg).Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries);
                    string logLine = "[" + time + "] " + (ml.Length > 0 ? ml[0] : prefix + msg) + (ml.Length > 1 ? "\n" + string.Join("\n", ml.Skip(1).Select(l => "             " + l.TrimStart())) : "");
                    AppendLog("\n" + logLine); lineCount++;
                }
                if (lastEnd < chunk.Length) foreach (var pl in chunk.Substring(lastEnd).Split('\n')) { string t = pl.Trim().Trim('\r'); if (!string.IsNullOrWhiteSpace(t)) { AppendLog("\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + t); lineCount++; } }
                return newPos;
            }
            catch { return pos; }
        }

        // ── FindDeployLog ─────────────────────────────────────────────────

        private static string? FindDeployLog(string? logDir, string deployMode, string[]? pkgParts = null, DateTime? minWrite = null)
        {
            if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir)) return null;
            try
            {
                var all = Directory.GetFiles(logDir, "*.log", SearchOption.TopDirectoryOnly);
                IEnumerable<string> cands = pkgParts?.Length > 0 ? all.Where(f => pkgParts.All(p => Path.GetFileName(f).IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)) : all;
                // Only consider logs written during THIS run. The PSADT log dir (e.g.
                // C:\WINDOWS\Logs\Software) is shared and accumulates a log per package/run/mode, so
                // without this a stale log from an earlier run (or the Install log during the Uninstall
                // phase) could be tailed instead of the one this step is writing right now.
                if (minWrite.HasValue) cands = cands.Where(f => File.GetLastWriteTime(f) >= minWrite.Value);
                string suffix = "_PSAppDeployToolkit_" + deployMode + ".log";
                // Only ever return a log matching THIS deploy mode's suffix
                // (..._PSAppDeployToolkit_{Install|Uninstall}.log). The old fallback to "most recent
                // .log" returned the Install log during the Uninstall phase (before the Uninstall log
                // existed) and the live-tail loop then stuck to it, so the Uninstall step showed no
                // live output. Returning null until the correct log appears lets the loop keep looking
                // and switch to it the moment it is created.
                return cands.Where(f => Path.GetFileName(f).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch { return null; }
        }

        // ── FindPsExec ────────────────────────────────────────────────────

        // Where PsExec.exe is expected to live: the installed Templates folder. Kept as one
        // definition so the search below and the failure message quote the same path.
        private static string PsExecTemplatesDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Smekal", "SwiftPack Studio", "Templates");

        // Surface a failed step in the Download tab's Download Information column. Reflection
        // keeps this page free of a compile-time dependency on SourceDownloadPage, the same way
        // GlobalShowMessageBox is read above. No-op when that page is not loaded.
        private static void ReportPackageErrorToDownloadTab(string title, string message, string hint)
        {
            try
            {
                var sdType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "SwiftPack.Views.SourceDownloadPage");
                var mi = sdType?.GetMethod("ReportPackageError",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                mi?.Invoke(null, new object?[] { title, message, hint });
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private static string? FindPsExec(string scriptDir)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Smekal", "SwiftPack Studio");
            string templatesDir = PsExecTemplatesDir;
            var cands = new[] { Path.Combine(templatesDir,"psexec.exe"), Path.Combine(templatesDir,"PsExec.exe"), Path.Combine(exeDir,"psexec.exe"), Path.Combine(exeDir,"PsExec.exe"), Path.Combine(installDir,"psexec.exe"), Path.Combine(installDir,"PsExec.exe"), Path.Combine(scriptDir,"psexec.exe"), Path.Combine(scriptDir,"PsExec.exe") };
            foreach (var c in cands) if (File.Exists(c)) return c;
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null) foreach (var dir in pathEnv.Split(';')) { string f = Path.Combine(dir.Trim(),"psexec.exe"); if (File.Exists(f)) return f; f = Path.Combine(dir.Trim(),"PsExec.exe"); if (File.Exists(f)) return f; }
            return null;
        }

        // ── Process tree helpers ──────────────────────────────────────────

        private static void KillProcessTree(int pid)
        {
            try { Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(3000); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // ── Open Application: open the installed app's main exe, hold, then close ──
        // Resolves the main exe from a Desktop / Start Menu shortcut whose name matches the
        // app display name (e.g. "VLC media player.lnk" -> vlc.exe), launches it, waits ~2.5 s
        // so it is visibly up, verifies it is running, then closes it. Returns true only when
        // the app was launched and confirmed running.
        private bool OpenAndCloseInstalledApp(string appName)
        {
            string exe = ResolveMainExeFromShortcuts(appName);
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Log("⚠  Open Application: no Desktop / Start Menu shortcut resolved to a main exe for \"" + appName + "\".");
                return false;
            }

            Log("Open Application : launching " + exe);
            Process? started = null;
            try
            {
                started = Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute  = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
                });
            }
            catch (Exception ex)
            {
                Log("❌ Open Application: launch failed - " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            // Hold 2-3 s so the app is visibly running before it is closed.
            Thread.Sleep(2500);

            // A GUI launcher may hand off to a differently named main process, so confirm by the
            // exe base name (how Close Apps names processes) and fall back to the started handle.
            string procName = Path.GetFileNameWithoutExtension(exe);
            bool running = false;
            try { running = Process.GetProcessesByName(procName).Length > 0; }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            if (!running)
                try { running = started != null && !started.HasExited; }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            // Close regardless (so nothing is left running), then report on what was seen.
            CloseOpenedApp(procName, started);

            if (!running)
                Log("⚠  Open Application: \"" + procName + "\" was not detected running after launch.");
            return running;
        }

        // Close the app opened by OpenAndCloseInstalledApp: a graceful main-window close of every
        // matching process first, then a forced kill of any survivor and of the process we started
        // (with its tree). procName is the exe base name, mirroring how Close Apps names processes.
        private void CloseOpenedApp(string procName, Process? started)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            Thread.Sleep(1000);

            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                    try { if (!p.HasExited) KillProcessTree(p.Id); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            try { if (started != null && !started.HasExited) KillProcessTree(started.Id); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            Log("Open Application : closed \"" + procName + "\".");
        }

        // Resolve the app's main exe from a Desktop / Start Menu shortcut whose name matches the
        // app display name. Mirrors AutoPackagePage.ResolveMainExeFromShortcuts (kept self-contained
        // so this page carries no compile-time dependency on it): scans the four common shortcut
        // roots for *.lnk whose file name matches significant words of the display name, resolves
        // each TargetPath via WScript.Shell, skips browsers / launchers, and returns the best match.
        private static string ResolveMainExeFromShortcuts(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return "";

            var browserExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "msedge", "chrome", "firefox", "iexplore", "opera", "brave",
                "safari", "msedgewebview2", "launcher", "setup", "unins000", "uninstall"
            };

            try
            {
                var sigWords = displayName
                    .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= 3 &&
                        !new[] { "the", "and", "for", "com", "inc", "ltd", "llc", "s.l.", "s.a.", "microsoft" }.Contains(w.ToLowerInvariant()))
                    .Select(w => w.ToLowerInvariant()).ToArray();

                string dispNorm = Regex.Replace(displayName, @"[\s\-_.]", "").ToLowerInvariant();
                if (sigWords.Length == 0 && dispNorm.Length < 3) return "";

                var folders = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                };

                var wshType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshType == null) return "";
                object? shell = Activator.CreateInstance(wshType);
                if (shell == null) return "";

                var candidates = new List<(int score, string path)>();

                foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)))
                {
                    foreach (var lnk in Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories))
                    {
                        string lnkName     = Path.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
                        string lnkNameNorm = Regex.Replace(lnkName, @"[\s\-_.]", "");

                        int matchScore = 0;
                        if (sigWords.Any(w => lnkName.Contains(w))) matchScore += 2;
                        if (dispNorm.Length >= 4 && lnkNameNorm.Contains(dispNorm)) matchScore += 3;
                        if (matchScore == 0) continue;

                        try
                        {
                            object? sc = wshType.InvokeMember("CreateShortcut",
                                System.Reflection.BindingFlags.InvokeMethod, null, shell,
                                new object[] { lnk });
                            if (sc == null) continue;
                            string target = sc.GetType().InvokeMember("TargetPath",
                                System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(sc);

                            if (string.IsNullOrWhiteSpace(target)) continue;
                            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!File.Exists(target)) continue;

                            string exeBaseName = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
                            if (browserExes.Contains(exeBaseName)) continue;
                            if (sigWords.Any(w => exeBaseName.Contains(w))) matchScore += 5;

                            candidates.Add((matchScore, target));
                        }
                        catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                    }
                }

                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

                if (candidates.Count > 0)
                    return candidates.OrderByDescending(c => c.score).First().path;
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            return "";
        }

        private static void WaitForProcessTreeExit(int rootPid, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMinutes(10);
            static HashSet<int> GetDesc(int root)
            {
                var all = new HashSet<int>(); var q = new Queue<int>(); q.Enqueue(root);
                try { var pm = new Dictionary<int,List<int>>(); using var s = new System.Management.ManagementObjectSearcher("SELECT ProcessId,ParentProcessId FROM Win32_Process"); foreach (System.Management.ManagementObject o in s.Get()) { int pid = Convert.ToInt32(o["ProcessId"]), par = Convert.ToInt32(o["ParentProcessId"]); if (!pm.ContainsKey(par)) pm[par]=new List<int>(); pm[par].Add(pid); } while (q.Count>0) { int cur=q.Dequeue(); all.Add(cur); if (pm.TryGetValue(cur,out var ch)) foreach (int c in ch) q.Enqueue(c); } } catch { all.Add(root); }
                return all;
            }
            var pids = GetDesc(rootPid);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                bool any = false;
                foreach (int pid in pids) { try { var p = Process.GetProcessById(pid); if (!p.HasExited) { any = true; break; } } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }
                if (!any) return; Thread.Sleep(500);
            }
        }

        // Per-app Icon.png (256px). The auto flow lands a 256px Icon.png in the per-app folder
        // (App Settings\<Vendor>\<AppName>) that Publish reads from entry.Logo. The orchestrator
        // (AutoPackagePage) sets the target folder and a callback that points entry.Logo at the
        // file once it exists. Icon extraction mirrors the standalone Execute page: an ARP diff to
        // the newly installed app, then PrivateExtractIcons at 256. An existing Icon.png is always
        // left as it is.
        private string          _appLogoFolder = "";
        private string          _appLogoName   = "";
        private Action<string>? _onAppLogoWritten;

        public void SetAppLogoTarget(string perAppFolder, string appName, Action<string>? onWritten)
        {
            _appLogoFolder    = perAppFolder ?? "";
            _appLogoName      = appName ?? "";
            _onAppLogoWritten = onWritten;
        }

        private static readonly (RegistryKey root, string sub)[] ArpHives =
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        // Every uninstall subkey name currently present, as "hiveIndex|subkey".
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

        // The icon source for the app this install added, found on the uninstall entry that was not
        // there before: a resolvable DisplayIcon file, else the MSI ProductIcon for that entry's
        // ProductCode, else the largest EXE in a folder derived from InstallLocation / DisplayIcon /
        // InstallSource. The MSI paths are what make this work for .msi installs, which usually
        // leave DisplayIcon unresolvable and InstallLocation blank. Null only when no key yields any
        // of the three.
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
                        // A patch or update entry carries no icon worth using and would win simply
                        // by being new.
                        if ((key.GetValue("SystemComponent") as int? ?? 0) != 0) continue;
                        if (string.IsNullOrWhiteSpace(key.GetValue("DisplayName") as string)) continue;

                        // A direct icon (DisplayIcon file or MSI ProductIcon) wins immediately; the
                        // .exe scan is collected into exeFallback and only used if no key yields one.
                        string direct = IconSourceFromKey(key, sub, ref exeFallback);
                        if (direct.Length > 0) return direct;
                    }
                }
                catch { }
            }
            return exeFallback.Length > 0 ? exeFallback : null;
        }

        // Fallback for an app that was already installed, so this install added no NEW uninstall
        // entry (a reinstall, or an in-place upgrade that reuses the same key). Matches the app's
        // configured name against the uninstall DisplayName - both ways, so "WinSCP" finds
        // "WinSCP 6.5.6" and "VLC media player" matches exactly - and takes that entry's icon.
        private static string? FindInstalledIconSourceByName(string appName)
        {
            string want = (appName ?? "").Trim();
            if (want.Length == 0) return null;
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
                        string dn = (key.GetValue("DisplayName") as string ?? "").Trim();
                        if (dn.Length == 0) continue;
                        bool match = dn.Equals(want, StringComparison.OrdinalIgnoreCase)
                                  || dn.StartsWith(want, StringComparison.OrdinalIgnoreCase)
                                  || want.StartsWith(dn, StringComparison.OrdinalIgnoreCase);
                        if (!match) continue;

                        string direct = IconSourceFromKey(key, sub, ref exeFallback);
                        if (direct.Length > 0) return direct;
                    }
                }
                catch { }
            }
            return exeFallback.Length > 0 ? exeFallback : null;
        }

        // MSI installs rarely put a usable file path in DisplayIcon and often leave
        // InstallLocation blank, so the EXE-shaped discovery below misses them. Given the app's
        // ProductCode, msi.dll resolves the same icon Windows itself shows in Programs and Features
        // ("ProductIcon" is a real path on disk, usually the cached ARPPRODUCTICON.exe or an .ico).
        // This is the reliable MSI source and is tried before giving up.
        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern uint MsiGetProductInfo(string szProduct, string szProperty,
            System.Text.StringBuilder? lpValueBuf, ref uint pcchValueBuf);

        // Resolve the icon a "path,index" DisplayIcon points at. Handles the two MSI shapes the
        // plain File.Exists check below trips over: a leftover index after a comma, and a
        // Windows Installer cache path (…\Installer\{GUID}\name.exe) whose file really is there but
        // was rejected because Split kept an index or a stray quote. Returns "" when nothing on
        // disk matches.
        private static string ResolveIconFilePath(string displayIcon)
        {
            string raw = (displayIcon ?? "").Trim().Trim('"');
            if (raw.Length == 0) return "";
            // Drop a trailing ,<index> the way the shell does, but only when the tail is numeric so
            // a real comma in a path is not cut.
            int comma = raw.LastIndexOf(',');
            if (comma > 1 && int.TryParse(raw.Substring(comma + 1).Trim(), out _))
                raw = raw.Substring(0, comma).Trim().Trim('"');
            if (raw.Length == 0) return "";
            // Expand %ProgramFiles% etc. that some installers store literally.
            try { raw = Environment.ExpandEnvironmentVariables(raw); } catch { }
            return File.Exists(raw) ? raw : "";
        }

        // The icon path msi.dll reports for a ProductCode, or "" when the code is blank, not an MSI
        // GUID, or the property is unavailable. The two-call length-then-buffer pattern is how the
        // MSI API returns strings.
        private static string ResolveMsiProductIcon(string productCode)
        {
            string code = (productCode ?? "").Trim();
            if (code.Length == 0 || code[0] != '{') return "";
            try
            {
                uint len = 0;
                // First call: ask for the required length (error 234 = ERROR_MORE_DATA is expected).
                uint rc = MsiGetProductInfo(code, "ProductIcon", null, ref len);
                if (len == 0) return "";
                var sb = new System.Text.StringBuilder((int)len + 1);
                len = (uint)sb.Capacity;
                rc = MsiGetProductInfo(code, "ProductIcon", sb, ref len);
                if (rc != 0) return "";
                string p = sb.ToString().Trim().Trim('"');
                if (p.Length == 0) return "";
                try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
                // ProductIcon can itself carry a ,index tail.
                return ResolveIconFilePath(p);
            }
            catch { return "" ; }
        }

        // Best icon source from one uninstall subkey, MSI shapes included, or "" when the key
        // yields nothing on disk. Order: a DisplayIcon that resolves to a real file (covers EXE and
        // most MSIs), then the MSI ProductIcon for the key's ProductCode (covers MSIs with an empty
        // or unresolvable DisplayIcon and no InstallLocation), then the largest .exe under a folder
        // derived from InstallLocation, DisplayIcon's directory, or InstallSource. The .exe scan is
        // returned via `exeFallback` so a direct icon on a later key can still win over it.
        private static string IconSourceFromKey(RegistryKey key, string? subKeyName, ref string exeFallback)
        {
            // 1. DisplayIcon → real file (EXE installers and MSIs that cache a usable icon path).
            string di = ResolveIconFilePath(key.GetValue("DisplayIcon") as string ?? "");
            if (di.Length > 0) return di;

            // 2. MSI ProductIcon. The subkey name for a per-product MSI entry IS the ProductCode;
            //    fall back to any explicit ProductCode-like value the entry stored.
            string pcode = "";
            if (!string.IsNullOrEmpty(subKeyName) && subKeyName.StartsWith("{") && subKeyName.EndsWith("}"))
                pcode = subKeyName;
            string msiIcon = ResolveMsiProductIcon(pcode);
            if (msiIcon.Length > 0) return msiIcon;

            // 3. Largest .exe under a resolvable folder. Try InstallLocation, then the directory of
            //    DisplayIcon (an MSI often points DisplayIcon at an exe in the app folder even when
            //    InstallLocation is blank), then InstallSource as a last resort.
            if (exeFallback.Length == 0)
            {
                foreach (string cand in EnumerateFolderCandidates(key))
                {
                    try
                    {
                        var exes = Directory.GetFiles(cand, "*.exe", SearchOption.TopDirectoryOnly);
                        if (exes.Length > 0)
                        {
                            exeFallback = exes.OrderByDescending(f => new FileInfo(f).Length).First();
                            break;
                        }
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

            string Norm(string? s)
            {
                string v = (s ?? "").Trim().Trim('"');
                try { v = Environment.ExpandEnvironmentVariables(v); } catch { }
                return v.TrimEnd(Path.DirectorySeparatorChar);
            }
            bool Usable(string v) =>
                v.Length > 0 && Directory.Exists(v)
                && !v.StartsWith(winInstaller, StringComparison.OrdinalIgnoreCase);

            string loc = Norm(key.GetValue("InstallLocation") as string);
            if (Usable(loc)) yield return loc;

            string diPath = ResolveIconFilePath(key.GetValue("DisplayIcon") as string ?? "");
            if (diPath.Length > 0)
            {
                string dir = Norm(Path.GetDirectoryName(diPath));
                if (Usable(dir)) yield return dir;
            }

            string src = Norm(key.GetValue("InstallSource") as string);
            if (Usable(src)) yield return src;
        }

        // Icons live in a PE resource, not an image container, so no image decoder can read an
        // .exe. PrivateExtractIcons asks for a given size and returns the closest the file carries,
        // which is how a modern binary's 256px icon is reached rather than the 32px shell default.
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[]? phicon, int[]? piconid, int nIcons, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // The largest image an .ico, .exe or .dll carries, re-encoded as PNG. Null when the file
        // holds no icon at all.
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

        // Writes <perAppFolder>\Icon.png from the icon of the application this install added, then
        // reports the path so the orchestrator can wire entry.Logo. An existing Icon.png is left as
        // it is but still reported, so Publish picks it up on re-runs too.
        private void WriteAppLogo(HashSet<string>? arpBefore)
        {
            string folder = (_appLogoFolder ?? "").Trim();
            if (folder.Length == 0 || !Directory.Exists(folder))
            { Log("ℹ  Icon.png not created - per-app folder not set or missing."); return; }

            string dest = Path.Combine(folder, "Icon.png");
            if (File.Exists(dest))
            {
                Log("ℹ  Icon.png already exists - left as it is: " + dest);
                try { _onAppLogoWritten?.Invoke(dest); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                return;
            }

            // Prefer the entry THIS install added; fall back to the app's existing entry by name so
            // an already-installed app (a reinstall or in-place upgrade adds no new uninstall entry)
            // still yields an icon.
            string? src = FindNewlyInstalledIconSource(arpBefore) ?? FindInstalledIconSourceByName(_appLogoName);
            if (src == null)
            { Log("ℹ  Icon.png not created - no uninstall entry for this app to take an icon from."); return; }

            byte[]? png = ExtractLargestIconPng(src);
            if (png == null)
            { Log("ℹ  Icon.png not created - no icon could be read from " + src + "."); return; }

            File.WriteAllBytes(dest, png);
            Log("🖼  Icon.png created → " + dest + "  (from " + Path.GetFileName(src) + ")");
            try { _onAppLogoWritten?.Invoke(dest); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoExecutePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }
    }
}
