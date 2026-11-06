// =====================================================================
//  AutoPackageShared.cs
//  SwiftPack Studio - consolidated shared types for the Auto Package feature.
//
//  Consolidated (no logic changed) from three former files:
//    - IAutoPackageHost.cs  -> interface IAutoPackageHost
//    - AutoViewLock.cs      -> static class AutoViewLock
//    - AutoPackageShared.cs -> IAutoPackageContext, AutoShowMessageBox,
//                              AutoStatusDialog, AutoFlowStatus, SecretProtector
//
//  After adopting this file, delete IAutoPackageHost.cs and AutoViewLock.cs.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SwiftPack.Views
{
    public interface IAutoPackageHost
    {
        /// <summary>
        /// Silently installs/uninstalls the downloaded source file, then
        /// optionally triggers Auto Package creation in sequence.
        /// Mirrors MainWindow.RunPsadtCommandAndThenAutoPackage — without
        /// the CreateDocument / ValidatePackage / ExecutePackageConfig refs.
        /// </summary>
        void RunPsadtCommandAndThenAutoPackage(
            string installCmd,
            string uninstallCmd,
            string appName,
            string sourceAppName         = "",   // catalog/installer display name — registry lookups only
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
            string copyDocxToDocuments   = "");

        /// <summary>
        /// Navigates the host window to AutoPackagePage and triggers
        /// auto-creation immediately with the given file / metadata.
        /// </summary>
        void NavigateAndTriggerAutoPackage(
            string filePath,
            string appVendor        = "",
            string appName          = "",
            string closeApps        = "",
            bool   doInstallPkg     = false,
            bool   doUninstallPkg   = false,
            string pkgTemplateFolder= "",
            string appVersion       = "",
            string uninstallPath    = "");

        /// <summary>The AutoPackagePage instance hosted by this window.</summary>
        AutoPackagePage AutoPackagePage { get; }

        /// <summary>
        /// Cancels an in-progress silent Install/Uninstall source run started by
        /// RunPsadtCommandAndThenAutoPackage. Kills the active child process (if any)
        /// and prevents any remaining silent steps from starting.
        /// </summary>
        void CancelSilentRun();

        /// <summary>
        /// Shows the Download-tab thin progress bar and highlights the matching
        /// "Install/Uninstall source silently" label. phase = "install" | "uninstall".
        /// For "uninstall" the host first switches back to the Download tab.
        /// Used by the deferred uninstall-after-create path in AutoPackagePage.
        /// </summary>
        void BeginSilentPhase(string phase);

        /// <summary>Hides the Download-tab progress bar and clears the highlight.</summary>
        void EndSilentPhase();
    }

    /// <summary>
    /// Implemented by AutoPackagePage; passed to every Auto sub-page
    /// so they can call back into the host without a direct type dependency.
    /// </summary>
    public interface IAutoPackageContext
    {
        /// <summary>Switch the top-level tab strip to the named tab.</summary>
        void ActivateTopTab(string tabName);   // "Download" | "Package" | "Execute" | "Intunewin" | "Publish"

        /// <summary>
        /// Auto-flow control: when false, completion dialogs in Execute and
        /// Intunewin should close themselves automatically (no user click needed).
        /// </summary>
        bool ShowMessageBox { get; set; }

        /// <summary>
        /// Fired by AutoIntunewinPage after a successful .intunewin creation.
        /// AutoPackagePage wires this to forward the event to MainWindow.
        /// </summary>
        void NotifyIntunewinCreated(string intunewinPath);

        /// <summary>
        /// The full path of the pending-after-execute chain action stored by the
        /// auto-flow; AutoIntunewinPage picks it up after creation completes.
        /// Returns null when nothing is queued.
        /// </summary>
        System.Action? ConsumePendingAfterExecuteChain();

        /// <summary>
        /// Called when a chained auto-flow step (e.g. Install/Uninstall package)
        /// FAILS and the chain is aborted. Releases the auto-flow lock so the
        /// Download/Package columns and the top tabs become interactive again
        /// without the user having to click through the tabs manually.
        /// </summary>
        void NotifyAutoFlowAborted();
    }

    /// <summary>
    /// Builds the msiexec verbose-log switch for the "Install source silently" /
    /// "Uninstall source silently" steps, producing %TEMP%\{msi file name}_{phase}.log.
    /// </summary>
    public static class MsiLog
    {
        /// <summary>
        /// Returns <paramref name="args"/> with a /l*v switch appended.
        ///
        /// Only msiexec understands /l*v, so callers apply this to MSI commands only; an EXE
        /// installer would treat it as an unknown argument.
        ///
        /// %TEMP% is expanded here rather than passed through as a literal. These commands are
        /// launched via ProcessStartInfo, which does not expand environment variables inside the
        /// argument string, so a literal "%TEMP%" would leave msiexec writing to a folder of that
        /// name. Expanding also pins the log to this user's temp folder even though the process is
        /// started elevated (Verb = "runas"). The result is quoted because the expanded path
        /// contains spaces on nearly every machine.
        ///
        /// The caller's own arguments are preserved and returned first, so whatever is configured
        /// under Silent Install MSI / Silent Uninstall MSI still applies.
        /// </summary>
        public static string With(string args, string msiFile, string phase)
        {
            try
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(msiFile ?? "");
                if (string.IsNullOrWhiteSpace(name)) name = "package";
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{name}_{phase}.log");
                string logArg  = $"/l*v \"{logPath}\"";
                return string.IsNullOrWhiteSpace(args) ? logArg : args.Trim() + " " + logArg;
            }
            catch (System.Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[MsiLog] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
                return args ?? "";
            }
        }
    }

    /// <summary>
    /// Everything the silent runner needs from the window that hosts it. Deliberately small:
    /// only the parts that are genuinely window-shaped stayed behind, so the runner itself has
    /// no window type of its own.
    /// </summary>
    public interface IAutoSilentHost
    {
        /// <summary>Owner for the modal status dialogs. May be null.</summary>
        System.Windows.Window? OwnerWindow { get; }

        /// <summary>The AutoPackagePage instance hosted by this window.</summary>
        AutoPackagePage AutoPackagePage { get; }

        /// <summary>Shows the Download-tab progress bar and highlights the matching label.</summary>
        void BeginSilentPhase(string phase);

        /// <summary>Hides the Download-tab progress bar and clears the highlight.</summary>
        void EndSilentPhase();

        /// <summary>
        /// Step 3: navigate to Auto Package, feed it the source and metadata, start creation, and
        /// arrange the Execute-package follow-on. Navigation and nav-button state are the only
        /// genuinely window-shaped part of the flow, so they stay with the host.
        /// </summary>
        void StartAutoPackageChain(
            string downloadedFile, string pkgInstallArgs, string pkgUninstallArgs,
            string appVendor, string appName, string closeApps, string appVersion,
            string uninstallPath, string templateFolder,
            bool doInstallPkg, bool doUninstallPkg);
    }

    /// <summary>
    /// The Auto Package silent Install source / Uninstall source runner.
    ///
    /// This belongs to the Auto Package feature rather than to any window, so it lives here
    /// with the rest of the shared Auto Package types.
    ///
    /// The window keeps only IAutoSilentHost: an owner for dialogs, the AutoPackagePage instance,
    /// the two progress-bar calls and the Step 3 navigation handoff.
    /// </summary>
    public static class AutoSilentRunner
    {
        // Cancellation state for the run in progress. Static because only one silent run can be
        // active at a time - the same assumption the window-owned version made.
        private static System.Threading.CancellationTokenSource? _silentCts;
        private static System.Diagnostics.Process?               _silentProc;
        private static readonly object                           _silentLock = new object();
        // Per-app folder (App Settings\<Vendor>\<AppName>) for the post-install Icon.png. Set by
        // SourceDownloadPage just before it starts the source install, since Run() only receives the
        // PSADT template folder and the versioned download folder - neither is the per-app one.
        public static string PendingPerAppFolder = "";

        // Fired on the UI thread with the Icon.png path once it exists (freshly written OR already
        // present), so the caller can point the app's Logo at it - the same wiring the PSADT/Execute
        // path does via AutoExecutePage._onAppLogoWritten. Without this, the direct-install (empty
        // template) path writes Icon.png but never fills the Publish > App Info Logo box. Set by
        // SourceDownloadPage next to PendingPerAppFolder; cleared to null when not in use.
        public static Action<string>? OnSourceIconWritten = null;

        /// <summary>
        /// Cancels an in-progress silent run: kills the active child process (if any) and stops
        /// any remaining step from starting.
        /// </summary>
        public static void Cancel()
        {
            lock (_silentLock)
            {
                try { _silentCts?.Cancel(); } catch { }
                try
                {
                    if (_silentProc != null && !_silentProc.HasExited)
                        _silentProc.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        public static void Run(
            IAutoSilentHost host,
            string installCmd,
            string uninstallCmd,
            string appName,
            string sourceAppName    = "",   // catalog/installer display name — registry lookups only
            string appVendor        = "",
            string appVersion       = "",
            string closeApps        = "",
            string uninstallPath    = "",
            string templateFolder   = "",
            string downloadedFile   = "",
            bool   doCreatePackage  = false,
            bool   doInstallPkg     = false,
            bool   doUninstallPkg   = false,
            string pkgInstallArgs   = "",
            string pkgUninstallArgs = "",
            string unifiedLogPath   = "",   // shared log path written by ShowDownloadCompleteDialog
            string copyLogsToDocuments  = "",
            string copyCsvToDocuments   = "",
            string copyHtmlToDocuments  = "",
            string copyTxtToDocuments   = "",
            string copyDocxToDocuments  = "")
        {
            // Apply per-app Documents copy flags
            ExecutePackageConfig.RuntimeCopyLogsOverride = string.IsNullOrWhiteSpace(copyLogsToDocuments) ? null : copyLogsToDocuments;
            ValidatePackagePage.RuntimeCopyCSVOverride   = string.IsNullOrWhiteSpace(copyCsvToDocuments)  ? null : copyCsvToDocuments;
            ValidatePackagePage.RuntimeCopyHtmlOverride  = string.IsNullOrWhiteSpace(copyHtmlToDocuments) ? null : copyHtmlToDocuments;
            CreateDocumentPage.RuntimeCopyTxtOverride    = string.IsNullOrWhiteSpace(copyTxtToDocuments)  ? null : copyTxtToDocuments;
            CreateDocumentPage.RuntimeCopyDocxOverride   = string.IsNullOrWhiteSpace(copyDocxToDocuments) ? null : copyDocxToDocuments;
            CreateDocumentPage.RuntimeDocumentsFolder    = null;

            // ── Use the shared log file passed from ShowDownloadCompleteDialog ───
            // This ensures download info + install/uninstall steps share one file:
            //   AutoPackage_<Vendor>_<AppName>_<Version>.log  in %TEMP%
            // Normalise version for log name — strip revision suffix e.g. "3.0.23_08" → "3.0.23"
            var _mwVerMatch = System.Text.RegularExpressions.Regex.Match(appVersion.Trim(), @"^[\d]+(?:\.[\d]+)*");
            string _mwLogVer = _mwVerMatch.Success ? _mwVerMatch.Value : appVersion.Trim();
            string logPath = !string.IsNullOrWhiteSpace(unifiedLogPath)
                ? unifiedLogPath
                : System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"AutoPackage_{appVendor.Trim().Replace(" ", "")}_{appName.Replace(" ", "")}_{_mwLogVer}.log");

            void WriteLog(string msg)
            {
                try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}"); }
                catch { }
            }

            // Run install + optional uninstall + optional AutoPackage on a background thread
            // so the UI stays responsive. Each step waits for the previous to complete.
            // ── Set up a fresh cancellation token for this silent run ──
            System.Threading.CancellationToken token;
            lock (_silentLock)
            {
                try { _silentCts?.Cancel(); _silentCts?.Dispose(); } catch { }
                _silentCts  = new System.Threading.CancellationTokenSource();
                _silentProc = null;
                token       = _silentCts.Token;
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                WriteLog("=== SwiftPack Silent Install/Uninstall ===");
                WriteLog($"App Name        : {appName}");
                WriteLog($"App Vendor      : {appVendor}");
                WriteLog($"App Version     : {appVersion}");
                WriteLog($"Downloaded File : {downloadedFile}");
                WriteLog($"Source path is resolved as: <SourceDownloadPath>\\<Vendor>\\<AppName Version>\\<filename>");
                WriteLog($"Install Command : {installCmd}");
                WriteLog($"Uninstall Command: {uninstallCmd}");
                WriteLog($"Pkg Install Args : {pkgInstallArgs}");
                WriteLog($"Pkg Uninstall Args: {pkgUninstallArgs}");
                WriteLog("");

                // ── Step 1: Silent Install ──────────────────────────────────────
                // The install command uses the DOWNLOADED FILE directly with silent args.
                // installCmd is built from the downloaded file path + SilentInstallParameter(EXE/MSI).
                // PSADTInstallCommand (powershell.exe ... -DeploymentType Install) is only used
                // AFTER a package is created — it is NOT used here.
                if (!string.IsNullOrWhiteSpace(installCmd) && !string.IsNullOrWhiteSpace(downloadedFile))
                {
                    string ext   = System.IO.Path.GetExtension(downloadedFile).ToLowerInvariant();
                    bool   isMsi = ext == ".msi" || ext == ".msp";

                    // Build the actual OS-level install command from the downloaded file
                    string resolvedExe;
                    string resolvedArgs;

                    if (isMsi)
                    {
                        // msiexec /i "<file>" <SilentInstallParameterMSI>
                        resolvedExe  = "msiexec.exe";
                        resolvedArgs = $"/i \"{downloadedFile}\" {MsiLog.With(pkgInstallArgs, downloadedFile, "Install")}".TrimEnd();
                    }
                    else
                    {
                        // "<file>" <SilentInstallParameterEXE>
                        resolvedExe  = downloadedFile;
                        resolvedArgs = pkgInstallArgs.Trim();
                    }

                    WriteLog("--- Step 1: Silent Install ---");
                    WriteLog($"Executable : {resolvedExe}");
                    WriteLog($"Arguments  : {resolvedArgs}");
                    WriteLog($"Working Dir: {System.IO.Path.GetDirectoryName(downloadedFile) ?? ""}");

                    if (token.IsCancellationRequested) { WriteLog("Install cancelled before start."); System.Windows.Application.Current.Dispatcher.Invoke(() => host.EndSilentPhase()); return; }

                    // Show the thin top progress bar + highlight "Install source silently".
                    System.Windows.Application.Current.Dispatcher.Invoke(() => host.BeginSilentPhase("install"));
                    var _instStart = DateTime.Now;
                    int _instExit = -1; bool _instThrew = false;
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(resolvedExe, resolvedArgs)
                        {
                            UseShellExecute  = true,
                            Verb             = "runas",
                            WorkingDirectory = System.IO.Path.GetDirectoryName(downloadedFile) ?? ""
                        };
                        WriteLog("Starting install process...");
                        var proc = System.Diagnostics.Process.Start(psi);
                        lock (_silentLock) { _silentProc = proc; }
                        // Poll for exit so cancellation is prompt and portable across frameworks.
                        if (proc != null)
                        {
                            while (!proc.WaitForExit(200))
                            {
                                if (token.IsCancellationRequested)
                                {
                                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                                    break;
                                }
                            }
                        }
                        lock (_silentLock) { _silentProc = null; }
                        int exitCode = proc?.ExitCode ?? -1;
                        _instExit = exitCode;
                        WriteLog(token.IsCancellationRequested
                            ? "Install cancelled by user."
                            : $"Install completed. Exit code: {exitCode}");
                    }
                    catch (Exception ex)
                    {
                        _instThrew = true;
                        WriteLog($"Install FAILED: {ex.Message}");
                    }
                    finally { System.Windows.Application.Current.Dispatcher.Invoke(() => host.EndSilentPhase()); }

                    // Status dialog (only when ShowMessageBox = Yes). Not shown if cancelled.
                    if (!token.IsCancellationRequested)
                    {
                        bool ok = !_instThrew && (_instExit == 0 || _instExit == 3010);
                        AutoFlowStatus.Set(AutoFlowStatus.InstallSource, ok ? AutoFlowStatus.State.Ok : AutoFlowStatus.State.Failed);
                        System.Windows.Application.Current.Dispatcher.Invoke(() => ShowSilentSourceDialog(host.OwnerWindow, 
                            "Install Source Silently", ok, _instExit, _instStart,
                            System.IO.Path.GetDirectoryName(downloadedFile) ?? "", logPath));

                        // FAILURE RULE: if the silent source install failed, abort the whole flow -
                        // do NOT run Uninstall source / Create package / any subsequent step, and do
                        // NOT run the success-only document/email generation.
                        if (!ok)
                        {
                            WriteLog("Install source silently FAILED - aborting: skipping Uninstall source, Create package and all subsequent steps.");
                            System.Windows.Application.Current.Dispatcher.Invoke(() => { try { host.AutoPackagePage.AbortAutoFlow(); } catch { } });
                            return;
                        }

                        // The source app is installed now (the source uninstall in Step 2 may remove
                        // it again), so capture its 256px Icon.png into the per-app folder if one is
                        // not already there - the same icon written after Install package.
                        WriteLog(EnsureSourceIcon(PendingPerAppFolder,
                            string.IsNullOrWhiteSpace(sourceAppName) ? appName : sourceAppName,
                            downloadedFile, out string writtenIconPath));
                        // Wire the app's Logo to the Icon.png so it auto-populates the Publish >
                        // App Info Logo box (mirrors the PSADT/Execute path's _onAppLogoWritten).
                        if (!string.IsNullOrWhiteSpace(writtenIconPath))
                        {
                            var cb = OnSourceIconWritten;
                            if (cb != null)
                            {
                                string iconForCb = writtenIconPath;
                                try
                                {
                                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                                    { try { cb(iconForCb); } catch { } });
                                }
                                catch { }
                            }
                        }
                    }

                    if (token.IsCancellationRequested) { WriteLog("Aborting silent run after install cancellation."); return; }
                }
                else
                {
                    WriteLog("Step 1 skipped: no install command or downloaded file path.");
                }

                // ── Step 2: Silent Uninstall (optional) ────────────────────────
                if (!string.IsNullOrWhiteSpace(uninstallCmd))
                {
                    if (token.IsCancellationRequested) { WriteLog("Uninstall cancelled before start."); return; }
                    // Small delay so installed app fully settles before uninstall
                    await System.Threading.Tasks.Task.Delay(2000);

                    string resolvedExe;
                    string resolvedArgs;

                    // Always look up the registry UninstallString first — it is the most
                    // reliable source and works whether the app was installed from MSI or EXE.
                    string? regUninstall = FindRegistryUninstallString(
                        string.IsNullOrWhiteSpace(sourceAppName) ? appName : sourceAppName);
                    WriteLog($"Registry UninstallString: {regUninstall ?? "(not found)"}");

                    if (!string.IsNullOrWhiteSpace(regUninstall))
                    {
                        string rCmd = regUninstall.Trim();

                        // ── MSI product: registry gives "MsiExec.exe /I{GUID}" or "MsiExec.exe /X{GUID}"
                        // We must change /I to /X (uninstall) and append silent args
                        if (rCmd.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Extract the product code GUID e.g. {AC9F2F90-E877-...}
                            var guidMatch = System.Text.RegularExpressions.Regex.Match(
                                rCmd, @"\{[0-9A-Fa-f\-]{36}\}");
                            if (guidMatch.Success)
                            {
                                resolvedExe  = "msiexec.exe";
                                resolvedArgs = $"/x {guidMatch.Value} {MsiLog.With(pkgUninstallArgs, downloadedFile, "Uninstall")}".Trim();
                            }
                            else
                            {
                                // Fallback: replace /I with /X in whatever msiexec call is there
                                resolvedExe  = "msiexec.exe";
                                resolvedArgs = System.Text.RegularExpressions.Regex.Replace(
                                    rCmd, @"(?i)msiexec\.exe\s*", "").Trim()
                                    .Replace("/I", "/X").Replace("/i", "/X");
                                resolvedArgs = (resolvedArgs + " " + MsiLog.With(pkgUninstallArgs, downloadedFile, "Uninstall")).Trim();
                            }
                        }
                        else
                        {
                            // EXE uninstaller — use the full registry command as-is + append silent args
                            string rExe, rArgs;
                            if (rCmd.StartsWith("\""))
                            {
                                int close = rCmd.IndexOf('"', 1);
                                rExe  = close > 0 ? rCmd.Substring(1, close - 1) : rCmd;
                                rArgs = close > 0 ? rCmd.Substring(close + 1).TrimStart() : "";
                            }
                            else
                            {
                                int sp = rCmd.IndexOf(' ');
                                rExe  = sp > 0 ? rCmd.Substring(0, sp) : rCmd;
                                rArgs = sp > 0 ? rCmd.Substring(sp + 1).TrimStart() : "";
                            }
                            resolvedExe  = rExe;
                            resolvedArgs = (rArgs + " " + pkgUninstallArgs).Trim();
                        }
                    }
                    else
                    {
                        // No registry entry — fall back to downloaded file + uninstall args
                        string ext   = System.IO.Path.GetExtension(downloadedFile).ToLowerInvariant();
                        bool   isMsi = ext == ".msi" || ext == ".msp";
                        if (isMsi)
                        {
                            resolvedExe  = "msiexec.exe";
                            resolvedArgs = $"/x \"{downloadedFile}\" {MsiLog.With(pkgUninstallArgs, downloadedFile, "Uninstall")}".TrimEnd();
                        }
                        else
                        {
                            resolvedExe  = downloadedFile;
                            resolvedArgs = pkgUninstallArgs.Trim();
                        }
                        WriteLog("Registry not found — falling back to downloaded file.");
                    }

                    WriteLog("--- Step 2: Silent Uninstall ---");
                    WriteLog($"Executable : {resolvedExe}");
                    WriteLog($"Arguments  : {resolvedArgs}");

                    if (token.IsCancellationRequested) { WriteLog("Uninstall cancelled before launch."); return; }

                    // Navigate back to Download tab, show progress bar + highlight
                    // "Uninstall source silently".
                    System.Windows.Application.Current.Dispatcher.Invoke(() => host.BeginSilentPhase("uninstall"));
                    var _uninStart = DateTime.Now;
                    int _uninExit = -1; bool _uninThrew = false;
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(resolvedExe, resolvedArgs)
                        {
                            UseShellExecute = true,
                            Verb            = "runas"
                        };
                        WriteLog("Starting uninstall process...");
                        var proc = System.Diagnostics.Process.Start(psi);
                        lock (_silentLock) { _silentProc = proc; }
                        if (proc != null)
                        {
                            while (!proc.WaitForExit(200))
                            {
                                if (token.IsCancellationRequested)
                                {
                                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                                    break;
                                }
                            }
                        }
                        lock (_silentLock) { _silentProc = null; }
                        int exitCode = proc?.ExitCode ?? -1;
                        _uninExit = exitCode;
                        WriteLog(token.IsCancellationRequested
                            ? "Uninstall cancelled by user."
                            : $"Uninstall completed. Exit code: {exitCode}");
                    }
                    catch (Exception ex)
                    {
                        _uninThrew = true;
                        WriteLog($"Uninstall FAILED: {ex.Message}");
                    }
                    finally { System.Windows.Application.Current.Dispatcher.Invoke(() => host.EndSilentPhase()); }

                    // Status dialog (only when ShowMessageBox = Yes). Not shown if cancelled.
                    if (!token.IsCancellationRequested)
                    {
                        bool ok = !_uninThrew && (_uninExit == 0 || _uninExit == 3010 || _uninExit == 1605);
                        AutoFlowStatus.Set(AutoFlowStatus.UninstallSource, ok ? AutoFlowStatus.State.Ok : AutoFlowStatus.State.Failed);
                        System.Windows.Application.Current.Dispatcher.Invoke(() => ShowSilentSourceDialog(host.OwnerWindow, 
                            "Uninstall Source Silently", ok, _uninExit, _uninStart, "", logPath));
                    }

                    if (token.IsCancellationRequested) { WriteLog("Aborting silent run after uninstall cancellation."); return; }
                }
                else
                {
                    WriteLog("Step 2 skipped: uninstall not requested.");
                }

                WriteLog("");
                WriteLog($"Log file: {logPath}");

                // ── Step 3: Trigger AutoPackage (optional) ──────────────────────
                if (doCreatePackage && !string.IsNullOrWhiteSpace(downloadedFile))
                {
                    WriteLog("--- Step 3: Triggering AutoPackage ---");
                    // Navigation, nav-button state and the Execute-package follow-on are the only
                    // genuinely window-shaped part of this method, so they stay with the host.
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        host.StartAutoPackageChain(downloadedFile, pkgInstallArgs, pkgUninstallArgs,
                            appVendor, appName, closeApps, appVersion, uninstallPath, templateFolder,
                            doInstallPkg, doUninstallPkg));
                }
                else
                {
                    // No package chain follows (e.g. only Install/Uninstall source selected) —
                    // finalise the per-action status so the Download tab paints the result.
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        try { host.AutoPackagePage.NotifyAutoFlowFinishedNoChain(); } catch { }
                    });
                }
            });
        }

        private static void ShowSilentSourceDialog(System.Windows.Window? owner, string phaseLabel, bool success, int exitCode, DateTime startTime, string path, string logPath)
        {
            if (!SwiftPack.Views.AutoShowMessageBox.Value) return;   // ShowMessageBox = No → no dialog
            try
            {
                var end = DateTime.Now; var el = end - startTime;
                string es = el.TotalMinutes >= 1 ? $"{(int)el.TotalMinutes}m {el.Seconds}s"
                          : el.TotalSeconds >= 1 ? $"{(int)el.TotalSeconds}s" : $"{(int)el.TotalMilliseconds}ms";
                string title = success ? $"{phaseLabel} - Complete" : $"{phaseLabel} - Failed";
                var rows = new System.Collections.Generic.List<(string,string,bool)>
                {
                    ("Time", $"Started: {startTime:HH:mm:ss}  Finished: {end:HH:mm:ss}  Duration: {es}", false),
                };
                // The Uninstall Source Silently dialog omits the Path row.
                if (!phaseLabel.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase))
                    rows.Add(("Path", path ?? "", false));
                rows.Add(("Exit Code", exitCode.ToString(), true));
                if (!string.IsNullOrWhiteSpace(logPath)) rows.Add(("Log", logPath, false));
                SwiftPack.Views.AutoStatusDialog.Show(owner, title, "↧", success, rows,
                    pathToOpen: path, logToOpen: logPath);
            }
            catch { }
        }

        private static string? FindRegistryUninstallString(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return null;

            // Normalise: lower-case, collapse whitespace
            string searchNorm = System.Text.RegularExpressions.Regex.Replace(
                appName.ToLowerInvariant().Trim(), @"\s+", " ");

            // Tokenise search name into significant words (3+ chars, not version noise)
            static string[] Tokens(string s) =>
                s.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                 .Where(w => w.Length >= 3 &&
                             !System.Text.RegularExpressions.Regex.IsMatch(w, @"^\d[\d\.]*$"))
                 .ToArray();

            string[] searchTokens = Tokens(searchNorm);
            if (searchTokens.Length == 0) return null;

            var hives = new[]
            {
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            string? bestUninstall  = null;
            int     bestScore      = 0;

            foreach (var (root, sub) in hives)
            {
                try
                {
                    using var hive = root.OpenSubKey(sub);
                    if (hive == null) continue;
                    foreach (var name in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = hive.OpenSubKey(name);
                            if (key == null) continue;

                            var dn = (key.GetValue("DisplayName") as string ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(dn)) continue;

                            string dnNorm = System.Text.RegularExpressions.Regex.Replace(
                                dn.ToLowerInvariant(), @"\s+", " ");
                            string[] dnTokens = Tokens(dnNorm);
                            if (dnTokens.Length == 0) continue;

                            // Score = number of search tokens that appear in the display name tokens
                            int matched = searchTokens.Count(st =>
                                dnTokens.Any(dt => dt.Equals(st, StringComparison.OrdinalIgnoreCase)));

                            // Require ALL search tokens to match (strict: every word of the app
                            // name must appear in the registry display name).
                            // Also require the display name has no more than 3 extra tokens to
                            // prevent short registry names matching everything.
                            int extra = dnTokens.Length - matched;
                            if (matched < searchTokens.Length || extra > 3) continue;

                            var us = key.GetValue("UninstallString") as string;
                            if (string.IsNullOrWhiteSpace(us)) continue;

                            // Prefer the entry with the highest match score (fewest extra tokens)
                            int score = matched * 10 - extra;
                            if (score > bestScore)
                            {
                                bestScore     = score;
                                bestUninstall = us;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return bestUninstall;
        }

        // Same name matching as FindRegistryUninstallString, but returns the app's icon SOURCE.
        // Per uninstall entry, in order: a DisplayIcon that resolves to a real file (covers EXE
        // installers and MSIs that cache a usable icon path), then the MSI ProductIcon for the
        // entry's ProductCode (covers MSIs whose DisplayIcon is empty/unresolvable AND whose
        // InstallLocation is blank - PuTTY and Notepad++ are exactly this), then the largest EXE in
        // a folder derived from InstallLocation, DisplayIcon's directory, or InstallSource. When a
        // downloaded MSI path is known, its own ProductCode is tried first so the icon is found even
        // if the ARP DisplayName does not tokenise to the app name. Used to capture Icon.png after a
        // successful source install.
        private static string? FindInstalledIconSourceByName(string appName, string downloadedFile = "")
        {
            // 0. Most reliable for MSI: read the ProductCode straight from the file just installed
            //    and ask msi.dll for that product's icon. Independent of DisplayName matching.
            string ext = System.IO.Path.GetExtension(downloadedFile ?? "").ToLowerInvariant();
            if ((ext == ".msi" || ext == ".msp") && System.IO.File.Exists(downloadedFile))
            {
                string pcFromFile = ReadMsiProductCodeFromFile(downloadedFile);
                string iconFromFile = ResolveMsiProductIcon(pcFromFile);
                if (iconFromFile.Length > 0) return iconFromFile;
            }

            if (string.IsNullOrWhiteSpace(appName)) return null;
            static string[] Tokens(string s) =>
                s.Split(new[] { ' ', '-', '_', '.', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                 .Where(w => w.Length >= 3 && !w.All(c => char.IsDigit(c) || c == '.'))
                 .ToArray();
            string[] searchTokens = Tokens(appName.ToLowerInvariant().Trim());
            if (searchTokens.Length == 0) return null;

            var hives = new[]
            {
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            string? bestIcon = null;
            int     bestScore = 0;
            foreach (var (root, sub) in hives)
            {
                try
                {
                    using var hive = root.OpenSubKey(sub);
                    if (hive == null) continue;
                    foreach (var name in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = hive.OpenSubKey(name);
                            if (key == null) continue;
                            var dn = (key.GetValue("DisplayName") as string ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(dn)) continue;
                            string[] dnTokens = Tokens(dn.ToLowerInvariant());
                            if (dnTokens.Length == 0) continue;
                            int matched = searchTokens.Count(st =>
                                dnTokens.Any(dt => dt.Equals(st, StringComparison.OrdinalIgnoreCase)));
                            int extra = dnTokens.Length - matched;
                            if (matched < searchTokens.Length || extra > 3) continue;

                            // MSI-aware resolution for this matched entry (see IconSourceFromKey).
                            string? cand = IconSourceFromKey(key, name);
                            if (cand == null) continue;
                            int score = matched * 10 - extra;
                            if (score > bestScore) { bestScore = score; bestIcon = cand; }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return bestIcon;
        }

        // Best icon source from one uninstall subkey, MSI shapes included, or null. Order:
        //   1. DisplayIcon that resolves to a real file (EXE, and MSIs that cache a usable path).
        //   2. MSI ProductIcon for the key's ProductCode (the subkey name is the ProductCode for a
        //      per-product MSI entry) - the reliable source when DisplayIcon and InstallLocation
        //      both give nothing.
        //   3. Largest .exe under a resolvable folder: InstallLocation, then DisplayIcon's own
        //      directory (MSIs often point DisplayIcon at an exe in the app folder while leaving
        //      InstallLocation blank), then InstallSource.
        private static string? IconSourceFromKey(Microsoft.Win32.RegistryKey key, string? subKeyName)
        {
            string di = ResolveIconFilePath(key.GetValue("DisplayIcon") as string ?? "");
            if (di.Length > 0) return di;

            string pcode = (!string.IsNullOrEmpty(subKeyName)
                            && subKeyName.StartsWith("{") && subKeyName.EndsWith("}"))
                         ? subKeyName : "";
            string msiIcon = ResolveMsiProductIcon(pcode);
            if (msiIcon.Length > 0) return msiIcon;

            foreach (string cand in EnumerateFolderCandidates(key))
            {
                try
                {
                    var exes = System.IO.Directory.GetFiles(cand, "*.exe", System.IO.SearchOption.TopDirectoryOnly);
                    if (exes.Length > 0)
                        return exes.OrderByDescending(f => new System.IO.FileInfo(f).Length).First();
                }
                catch { }
            }
            return null;
        }

        // Folders worth scanning for an app's main .exe, in priority order, skipping blanks and the
        // Windows Installer cache (its exes are icon stubs, not the real application).
        private static IEnumerable<string> EnumerateFolderCandidates(Microsoft.Win32.RegistryKey key)
        {
            string winInstaller;
            try { winInstaller = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer"); }
            catch { winInstaller = @"C:\Windows\Installer"; }

            static string Expand(string? s)
            {
                string v = (s ?? "").Trim().Trim('"');
                try { v = Environment.ExpandEnvironmentVariables(v); } catch { }
                return v.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            }
            bool Usable(string v) =>
                v.Length > 0 && System.IO.Directory.Exists(v)
                && !v.StartsWith(winInstaller, StringComparison.OrdinalIgnoreCase);

            string loc = Expand(key.GetValue("InstallLocation") as string);
            if (Usable(loc)) yield return loc;

            string diPath = ResolveIconFilePath(key.GetValue("DisplayIcon") as string ?? "");
            if (diPath.Length > 0)
            {
                string dir = Expand(System.IO.Path.GetDirectoryName(diPath));
                if (Usable(dir)) yield return dir;
            }

            string src = Expand(key.GetValue("InstallSource") as string);
            if (Usable(src)) yield return src;
        }

        // Resolve a "path" or "path,index" DisplayIcon to a real file, or "" when nothing on disk
        // matches. Strips a trailing numeric ,index (kept only when the tail is numeric, so a real
        // comma in a path is not cut) and expands environment variables some installers store raw.
        private static string ResolveIconFilePath(string displayIcon)
        {
            string raw = (displayIcon ?? "").Trim().Trim('"');
            if (raw.Length == 0) return "";
            int comma = raw.LastIndexOf(',');
            if (comma > 1 && int.TryParse(raw.Substring(comma + 1).Trim(), out _))
                raw = raw.Substring(0, comma).Trim().Trim('"');
            if (raw.Length == 0) return "";
            try { raw = Environment.ExpandEnvironmentVariables(raw); } catch { }
            return System.IO.File.Exists(raw) ? raw : "";
        }

        // The icon path msi.dll reports for a ProductCode, or "" when the code is blank, not an MSI
        // GUID, or the property is unavailable. Two-call length-then-buffer pattern, as the MSI API
        // requires. "ProductIcon" is a real path on disk - usually the cached ARPPRODUCTICON.exe or
        // an .ico - the same icon Windows shows in Programs and Features.
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

        // Read the ProductCode from an .msi on disk via the WindowsInstaller COM automation object.
        // Returns "" on any error. Runs on its own STA thread because the COM object requires it.
        private static string ReadMsiProductCodeFromFile(string msiPath)
        {
            string productCode = "";
            var t = new System.Threading.Thread(() =>
            {
                object? inst = null, db = null, vw = null, rec = null;
                try
                {
                    Type? ti = Type.GetTypeFromProgID("WindowsInstaller.Installer");
                    if (ti == null) return;
                    inst = Activator.CreateInstance(ti);
                    db = ti.InvokeMember("OpenDatabase", System.Reflection.BindingFlags.InvokeMethod,
                            null, inst, new object[] { msiPath, 0 });
                    var dt = db!.GetType();
                    vw = dt.InvokeMember("OpenView", System.Reflection.BindingFlags.InvokeMethod,
                            null, db, new object[] { "SELECT Value FROM Property WHERE Property='ProductCode'" });
                    var vt = vw!.GetType();
                    vt.InvokeMember("Execute", System.Reflection.BindingFlags.InvokeMethod, null, vw, null);
                    rec = vt.InvokeMember("Fetch", System.Reflection.BindingFlags.InvokeMethod, null, vw, null);
                    if (rec != null)
                        productCode = rec.GetType().InvokeMember("StringData",
                            System.Reflection.BindingFlags.GetProperty, null, rec, new object[] { 1 }) as string ?? "";
                }
                catch { }
                finally
                {
                    static void Rel(object? o) { try { if (o != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(o); } catch { } }
                    Rel(rec); Rel(vw); Rel(db); Rel(inst);
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();
            return productCode.Trim();
        }

        [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern uint MsiGetProductInfo(string szProduct, string szProperty,
            System.Text.StringBuilder? lpValueBuf, ref uint pcchValueBuf);

        // Icons live in a PE resource, so PrivateExtractIcons is asked for a given size and returns
        // the closest the file carries - that is how a modern binary's 256px icon is reached.
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[]? phicon, int[]? piconid, int nIcons, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // The largest image an .ico/.exe/.dll carries, re-encoded as PNG. Null when none.
        private static byte[]? ExtractLargestIconPng(string path)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapSource? best = null;
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".exe" || ext == ".dll")
                {
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
                    using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(fs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    foreach (var f in dec.Frames)
                        if (best == null || f.PixelWidth * f.PixelHeight > best.PixelWidth * best.PixelHeight) best = f;
                }
                if (best == null) return null;
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(best));
                using var ms = new System.IO.MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        // Called after a successful "Install source silently" (app installed, source uninstall not
        // run yet): write the app's 256px Icon.png into the per-app folder if one is not already
        // there - the same icon written after Install package. Returns a log line, and sets
        // iconPath to the Icon.png whenever one exists (freshly written OR already present) so the
        // caller can wire the app's Logo to it.
        private static string EnsureSourceIcon(string perAppFolder, string appName, string downloadedFile, out string iconPath)
        {
            iconPath = "";
            try
            {
                string folder = (perAppFolder ?? "").Trim();
                if (folder.Length == 0 || !System.IO.Directory.Exists(folder))
                    return "Icon.png not created - per-app folder not set or missing.";
                string dest = System.IO.Path.Combine(folder, "Icon.png");
                if (System.IO.File.Exists(dest)) { iconPath = dest; return "Icon.png already exists - left as it is: " + dest; }
                string? src = FindInstalledIconSourceByName(appName, downloadedFile);
                if (src == null) return "Icon.png not created - no uninstall entry for this app to take an icon from.";
                byte[]? png = ExtractLargestIconPng(src);
                if (png == null) return "Icon.png not created - no icon could be read from " + src + ".";
                System.IO.File.WriteAllBytes(dest, png);
                iconPath = dest;
                return "Icon.png created -> " + dest + "  (from " + System.IO.Path.GetFileName(src) + ")";
            }
            catch (System.Exception ex) { return "Icon.png not created - " + ex.Message; }
        }
    }

    /// <summary>
    /// Process-wide flag used inside the Auto Package flow to suppress
    /// "completion" dialogs across ExecutePackagePage and AutoIntunewinPage
    /// when the flow is running unattended (chain mode).
    /// </summary>
    public static class AutoShowMessageBox
    {
        /// <summary>When false, dialogs auto-close without waiting for user input.</summary>
        public static bool Value { get; set; } = true;
    }

    /// <summary>
    /// Reusable, theme-styled status dialog ("&lt;title&gt; — Complete/Failed") used by the
    /// Auto Package flow for phases that don't have their own dialog (silent Install/Uninstall
    /// source, etc.). Matches the look of the existing per-page completion dialogs: borderless
    /// rounded window, dark title bar, labelled value rows, and Open Path / Open Log / Close
    /// buttons. Callers gate visibility on AutoShowMessageBox.Value themselves.
    /// </summary>
    public static class AutoStatusDialog
    {
        public static void Show(
            System.Windows.DependencyObject owner,
            string title,
            string icon,
            bool success,
            System.Collections.Generic.IEnumerable<(string label, string value, bool highlight)> rows,
            string? pathToOpen = null,
            string? logToOpen  = null)
        {
            System.Windows.Media.Brush Res(string key, System.Windows.Media.Brush fallback) =>
                System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;

            var winBg     = Res("WindowBgBrush",   System.Windows.Media.Brushes.White);
            var borderClr = Res("BorderMainBrush", System.Windows.Media.Brushes.LightGray);
            var boxBg     = Res("InputBgBrush",     new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0,0xF1,0xF8)));
            var lblFg     = Res("TextSecondaryBrush", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55,0x5C,0x7A)));
            var valFg     = Res("TextPrimaryBrush",   new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A,0x1D,0x2E)));
            var codeBrush = success ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E,0x7D,0x32))
                                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6,0x28,0x28));

            var win = new System.Windows.Window
            {
                Title = title, Width = 460, MaxWidth = 460,
                SizeToContent = System.Windows.SizeToContent.Height,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner = System.Windows.Window.GetWindow(owner),
                ResizeMode = System.Windows.ResizeMode.NoResize,
                WindowStyle = System.Windows.WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false,
            };

            var outerBorder = new System.Windows.Controls.Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = System.Windows.Media.Colors.Black }
            };
            win.Content = outerBorder;

            var outerGrid = new System.Windows.Controls.Grid();
            outerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            outerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            outerBorder.Child = outerGrid;

            // Title bar
            var titleBar = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53,0x56,0x63)),
                BorderBrush = borderClr, BorderThickness = new System.Windows.Thickness(0,0,0,1),
                CornerRadius = new System.Windows.CornerRadius(10,10,0,0),
                Padding = new System.Windows.Thickness(16,6,12,6)
            };
            titleBar.MouseLeftButtonDown += (_, _) => { try { win.DragMove(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackageShared] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } };
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            var tg = new System.Windows.Controls.Grid();
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            var tIcon = new System.Windows.Controls.TextBlock { Text = icon, FontSize = 14, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(0,0,8,0) };
            var tText = new System.Windows.Controls.TextBlock { Text = title, FontSize = 13, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            var tClose = new System.Windows.Controls.Button { Content = "✕", FontSize = 11, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC,0xFF,0xFF,0xFF)), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new System.Windows.Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = System.Windows.VerticalAlignment.Center, Padding = new System.Windows.Thickness(6,2,6,2) };
            tClose.Click += (_, _) => win.Close();
            System.Windows.Controls.Grid.SetColumn(tIcon, 0); System.Windows.Controls.Grid.SetColumn(tText, 1); System.Windows.Controls.Grid.SetColumn(tClose, 2);
            tg.Children.Add(tIcon); tg.Children.Add(tText); tg.Children.Add(tClose);
            titleBar.Child = tg; outerGrid.Children.Add(titleBar);

            // Content
            var root = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(8) };
            System.Windows.Controls.Grid.SetRow(root, 1); outerGrid.Children.Add(root);

            var rowList = new System.Collections.Generic.List<(string label, string value, bool highlight)>(rows);
            // Drop rows whose value is empty so blank fields (e.g. an unknown Path) don't render.
            rowList.RemoveAll(r => string.IsNullOrWhiteSpace(r.value));
            var details = new System.Windows.Controls.Grid();
            details.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(72) });
            details.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            for (int i = 0; i < rowList.Count; i++)
            {
                details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
                if (i < rowList.Count - 1) details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(8) });
                int r = i * 2;
                var lbl = new System.Windows.Controls.TextBlock { Text = rowList[i].label, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                var box = new System.Windows.Controls.Border { Background = boxBg, BorderBrush = borderClr, BorderThickness = new System.Windows.Thickness(1), CornerRadius = new System.Windows.CornerRadius(6), Padding = new System.Windows.Thickness(10,6,10,6) };
                box.Child = new System.Windows.Controls.TextBlock { Text = rowList[i].value, FontSize = 11, TextWrapping = System.Windows.TextWrapping.Wrap, Foreground = rowList[i].highlight ? codeBrush : valFg, FontWeight = rowList[i].highlight ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
                System.Windows.Controls.Grid.SetRow(lbl, r); System.Windows.Controls.Grid.SetColumn(lbl, 0);
                System.Windows.Controls.Grid.SetRow(box, r); System.Windows.Controls.Grid.SetColumn(box, 1);
                details.Children.Add(lbl); details.Children.Add(box);
            }
            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(8) });
            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            int btnRow = rowList.Count * 2;

            var sec = System.Windows.Application.Current.TryFindResource("SecondaryButtonStyle") as System.Windows.Style;
            bool hasPath = !string.IsNullOrWhiteSpace(pathToOpen);
            bool hasLog  = !string.IsNullOrWhiteSpace(logToOpen) && System.IO.File.Exists(logToOpen);
            int btnCount = 1 + (hasPath ? 1 : 0) + (hasLog ? 1 : 0);
            var btnGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = btnCount };
            if (hasPath)
            {
                var b = new System.Windows.Controls.Button { Content = "📁 Open Path", Style = sec, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Height = 28, Padding = new System.Windows.Thickness(6,0,6,0), Margin = new System.Windows.Thickness(0,0,4,0) };
                b.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", pathToOpen!) { UseShellExecute = true }); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackageShared] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } };
                btnGrid.Children.Add(b);
            }
            if (hasLog)
            {
                var b = new System.Windows.Controls.Button { Content = "📄 Open Log", Style = sec, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Height = 28, Padding = new System.Windows.Thickness(6,0,6,0), Margin = new System.Windows.Thickness(4,0,4,0) };
                b.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logToOpen!) { UseShellExecute = true }); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoPackageShared] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } };
                btnGrid.Children.Add(b);
            }
            var ok = new System.Windows.Controls.Button { Content = "✕ Close", Style = sec, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Height = 28, Padding = new System.Windows.Thickness(6,0,6,0), Margin = new System.Windows.Thickness(4,0,0,0), IsDefault = true };
            ok.Click += (_, _) => win.Close();
            btnGrid.Children.Add(ok);
            // Highlight the first button as the default action (uniform with the feature pages).
            if (btnGrid.Children[0] is System.Windows.Controls.Button firstBtn) ApplyPrimaryLook(firstBtn);
            System.Windows.Controls.Grid.SetRow(btnGrid, btnRow); System.Windows.Controls.Grid.SetColumn(btnGrid, 1);
            details.Children.Add(btnGrid);
            var statusIcon = DecisionIcon();
            System.Windows.Controls.Grid.SetRow(statusIcon, btnRow); System.Windows.Controls.Grid.SetColumn(statusIcon, 0);
            details.Children.Add(statusIcon);

            root.Children.Add(details);
            win.ShowDialog();
        }

        // Give a dialog button the highlighted "primary action" look: a filled
        // accent background, rounded corners and a subtle hover. Theme resources
        // keep it readable in both light and dark modes, mirroring the Publish
        // Package dialog. Applied on top of any existing Style (local values win
        // in WPF), so callers keep their own sizing.
        private static void ApplyPrimaryLook(Button b)
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
        private static TextBlock DecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };
    }

    /// <summary>
    /// Shared per-action status for the Auto Package flow. The flow (AutoPackagePage)
    /// records each step's outcome here as it runs; the Download tab reads it when the
    /// flow completes and colours the action checkboxes accordingly.
    /// </summary>
    public static class AutoFlowStatus
    {
        // Canonical action keys (match the Download tab checkbox order).
        public const string InstallSource   = "InstallSource";
        public const string UninstallSource = "UninstallSource";
        public const string CreatePackage   = "CreatePackage";
        public const string InstallPackage  = "InstallPackage";
        public const string UninstallPackage= "UninstallPackage";
        public const string CreateIntunewin = "CreateIntunewin";
        public const string PublishSettings = "PublishSettings";
        public const string PublishToIntune = "PublishToIntune";
        public const string SendMail        = "SendMail";
        /// <summary>Automatic supersedence of previous versions. Carried out by the publish
        /// script, so its outcome is the publish outcome.</summary>
        public const string AddSupersedence = "AddSupersedence";

        public enum State { None, Ok, Failed }

        // ── Whole-flow running flag ──────────────────────────────────────────
        // True from the moment a download button is clicked until the whole
        // auto-package flow finishes. Used to lock the sub-tab bar against manual
        // switching (the automation still switches programmatically). The change
        // event lets the tab host react without a direct page reference.
        private static bool _flowRunning;
        public static event System.Action? FlowRunningChanged;
        public static bool FlowRunning
        {
            get => _flowRunning;
            set
            {
                if (_flowRunning == value) return;
                _flowRunning = value;
                try { FlowRunningChanged?.Invoke(); } catch { }
            }
        }

        private static readonly object _lock = new();
        private static readonly System.Collections.Generic.Dictionary<string, State> _status = new();
        private static readonly System.Collections.Generic.HashSet<string> _wanted = new();

        /// <summary>Record which actions were selected for this run (set by the Download tab).</summary>
        public static void SetWanted(System.Collections.Generic.IEnumerable<string> actions)
        {
            lock (_lock)
            {
                _wanted.Clear();
                if (actions != null) foreach (var a in actions) _wanted.Add(a);
            }
        }

        public static bool IsWanted(string action)
        {
            lock (_lock) { return _wanted.Contains(action); }
        }

        // ── Per-application persisted results ─────────────────────────────
        // Keyed by a stable application identity (e.g. "Vendor|AppName") so that switching
        // between apps in the Download tab preserves each app's last result, and only a fresh
        // download of that same app clears it.
        public class AppResult
        {
            public System.Collections.Generic.Dictionary<string, State> Status = new();
            public System.Collections.Generic.HashSet<string> Wanted = new();
            public bool AnyFailed;
        }
        private static readonly System.Collections.Generic.Dictionary<string, AppResult> _appResults = new();

        /// <summary>Persist the current run's result under an app key.</summary>
        public static void SaveResultFor(string appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey)) return;
            lock (_lock)
            {
                bool anyFailed = false;
                foreach (var kv in _status) if (kv.Value == State.Failed) anyFailed = true;
                _appResults[appKey] = new AppResult
                {
                    Status   = new System.Collections.Generic.Dictionary<string, State>(_status),
                    Wanted   = new System.Collections.Generic.HashSet<string>(_wanted),
                    AnyFailed = anyFailed
                };
            }
        }

        /// <summary>Retrieve a previously saved result for an app key (null if none).</summary>
        public static AppResult? GetResultFor(string appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey)) return null;
            lock (_lock) { return _appResults.TryGetValue(appKey, out var r) ? r : null; }
        }

        /// <summary>Clear a saved result (e.g. when that app is downloaded again).</summary>
        public static void ClearResultFor(string appKey)
        {
            if (string.IsNullOrWhiteSpace(appKey)) return;
            lock (_lock) { _appResults.Remove(appKey); }
        }

        /// <summary>The package name this status set belongs to (so the Download tab only
        /// colours the matching app).</summary>
        public static string PackageName { get; private set; } = "";

        /// <summary>True once the flow has finished (success or failure) — Download tab paints then.</summary>
        public static bool Completed { get; private set; }

        /// <summary>Set the package name this status set belongs to.</summary>
        public static void SetPackageName(string packageName)
        {
            lock (_lock) { PackageName = packageName ?? ""; }
        }

        /// <summary>Clear all status and begin tracking a fresh run.</summary>
        public static void Begin(string packageName)
        {
            lock (_lock)
            {
                _status.Clear();
                PackageName = packageName ?? "";
                Completed = false;
            }
        }

        /// <summary>Record an action outcome.</summary>
        public static void Set(string action, State state)
        {
            lock (_lock) { _status[action] = state; }
        }

        public static void MarkOk(string action)     => Set(action, State.Ok);
        public static void MarkFailed(string action)  => Set(action, State.Failed);

        /// <summary>Mark the run finished so the Download tab can paint the result.</summary>
        public static void Finish() { lock (_lock) { Completed = true; } FlowRunning = false; }

        public static State Get(string action)
        {
            lock (_lock) { return _status.TryGetValue(action, out var s) ? s : State.None; }
        }

        /// <summary>Snapshot of all recorded statuses.</summary>
        public static System.Collections.Generic.Dictionary<string, State> Snapshot()
        {
            lock (_lock) { return new System.Collections.Generic.Dictionary<string, State>(_status); }
        }

        /// <summary>Clear everything (e.g. when the user re-opens the Download tab).</summary>
        public static void Reset()
        {
            lock (_lock) { _status.Clear(); PackageName = ""; Completed = false; }
        }
    }

    /// <summary>
    /// DPAPI-based string protection for storing secrets (e.g. the SMTP app password)
    /// encrypted at rest. Scope is LocalMachine so the SwiftPack service/app can read it
    /// regardless of which user runs it on that machine.
    /// </summary>
    public static class SecretProtector
    {
        public static string Encrypt(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes(plain);
                byte[] enc  = System.Security.Cryptography.ProtectedData.Protect(
                    data, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
                return System.Convert.ToBase64String(enc);
            }
            catch { return ""; }
        }

        public static string Decrypt(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return "";
            try
            {
                byte[] enc  = System.Convert.FromBase64String(protectedBase64);
                byte[] data = System.Security.Cryptography.ProtectedData.Unprotect(
                    enc, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
                return System.Text.Encoding.UTF8.GetString(data);
            }
            catch { return ""; }
        }
    }

    internal static class AutoViewLock
    {
        // Greyed look for "kept" buttons that the user must not click but the
        // automation still triggers programmatically.
        public const double GreyedOpacity = 0.55;

        // Accent used when an action is actively running (Req 3).
        private static readonly Brush AccentFill =
            new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xF6)); // blue accent
        private static readonly Brush AccentText = Brushes.White;

        // Remembers each highlighted button's original look so we can restore it.
        private sealed class SavedLook
        {
            public Brush? Background;
            public Brush? Foreground;
            public FontWeight Weight;
            public bool HasBackground;
            public bool HasForeground;
        }
        private static readonly Dictionary<Button, SavedLook> _saved = new();

        // ─────────────────────────────────────────────────────────────────
        //  Make a button LOOK disabled but stay technically enabled so the
        //  automation can still RaiseEvent its Click. Blocks user clicks.
        // ─────────────────────────────────────────────────────────────────
        public static void GreyKeepEnabled(Button? b)
        {
            if (b == null) return;
            b.IsEnabled         = true;   // MUST stay enabled for automation
            b.Opacity           = GreyedOpacity;
            b.IsHitTestVisible  = false;  // user cannot click
            b.Focusable         = false;  // user cannot tab to it
            b.Cursor            = System.Windows.Input.Cursors.Arrow;
        }

        // Visual-only greying: blocks user clicks but does NOT force IsEnabled,
        // for buttons whose IsEnabled is data-driven by existing page logic
        // (e.g. Intunewin Create enables only when all paths are set). The
        // automation's RaiseEvent still works whenever the page logic has it
        // enabled. While disabled by page logic, WPF greys it anyway.
        public static void GreyLookOnly(Button? b)
        {
            if (b == null) return;
            b.Opacity           = GreyedOpacity;
            b.IsHitTestVisible  = false;
            b.Focusable         = false;
            b.Cursor            = System.Windows.Input.Cursors.Arrow;
        }

        // Override an accent/"primary" button's fill so it reads as a neutral
        // greyed control while idle (it only lights up via Highlight() when running).
        private static readonly Brush NeutralFill =
            new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)); // muted grey
        public static void NeutralizeFill(Button? b)
        {
            if (b == null) return;
            b.Background = NeutralFill;
            b.Foreground = Brushes.White;
        }

        // Copy the visual fill (Background + Foreground) of one button onto another,
        // so e.g. the Publish button matches the Save button exactly while idle.
        public static void MatchFill(Button? target, Button? source)
        {
            if (target == null || source == null) return;
            target.Background = source.Background;
            target.Foreground = source.Foreground;
            target.BorderBrush = source.BorderBrush;
        }

        public static void Hide(UIElement? e)
        {
            if (e != null) e.Visibility = Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Highlight a running action button (accent fill + bold).
        // ─────────────────────────────────────────────────────────────────
        public static void Highlight(Button? b)
        {
            if (b == null) return;
            if (!_saved.ContainsKey(b))
            {
                _saved[b] = new SavedLook
                {
                    Background     = b.Background,
                    Foreground     = b.Foreground,
                    Weight         = b.FontWeight,
                    HasBackground  = b.ReadLocalValue(Control.BackgroundProperty) != DependencyProperty.UnsetValue,
                    HasForeground  = b.ReadLocalValue(Control.ForegroundProperty) != DependencyProperty.UnsetValue,
                };
            }
            b.Background = AccentFill;
            b.Foreground = AccentText;
            b.FontWeight = FontWeights.Bold;
            b.Opacity    = 1.0;           // fully lit while running
        }

        public static void Unhighlight(Button? b)
        {
            if (b == null) return;
            if (_saved.TryGetValue(b, out var s))
            {
                if (s.HasBackground) b.Background = s.Background; else b.ClearValue(Control.BackgroundProperty);
                if (s.HasForeground) b.Foreground = s.Foreground; else b.ClearValue(Control.ForegroundProperty);
                b.FontWeight = s.Weight;
                _saved.Remove(b);
            }
            // Return to the greyed-idle look.
            b.Opacity = GreyedOpacity;
        }

        public static void UnhighlightAll(params Button?[] buttons)
        {
            foreach (var b in buttons) Unhighlight(b);
        }

        // ─────────────────────────────────────────────────────────────────
        //  Walk the visual/logical tree and lock every INPUT control so the
        //  user cannot edit it. Buttons are intentionally skipped here — they
        //  are handled explicitly by each page (hide / grey / active).
        // ─────────────────────────────────────────────────────────────────
        public static void LockInputs(DependencyObject? root)
        {
            if (root == null) return;

            foreach (var child in Walk(root))
            {
                switch (child)
                {
                    // Buttons handled per-page; never touched here.
                    case Button:
                        break;

                    case TextBox tb:
                        tb.IsReadOnly       = true;
                        tb.IsReadOnlyCaretVisible = false;
                        tb.Focusable        = false;
                        // Keep hit-test ON so the user can still scroll/select-to-read,
                        // but read-only blocks edits. If you want zero interaction,
                        // set IsHitTestVisible=false too.
                        break;

                    case PasswordBox pb:
                        pb.IsHitTestVisible = false;
                        pb.Focusable        = false;
                        break;

                    case ComboBox cb:
                        cb.IsHitTestVisible = false;
                        cb.Focusable        = false;
                        break;

                    case CheckBox chk:
                        chk.IsHitTestVisible = false;
                        chk.Focusable        = false;
                        break;

                    case RadioButton rb:
                        rb.IsHitTestVisible = false;
                        rb.Focusable        = false;
                        break;

                    case ToggleButton tgl:        // (non-RadioButton/CheckBox toggles)
                        tgl.IsHitTestVisible = false;
                        tgl.Focusable        = false;
                        break;

                    case Slider sl:
                        sl.IsHitTestVisible = false;
                        sl.Focusable        = false;
                        break;

                    case ListBox lb:
                        lb.IsHitTestVisible = false;
                        lb.Focusable        = false;
                        break;
                }
            }
        }

        // Depth-first enumeration over the logical tree (works before render),
        // falling back to the visual tree where logical children are absent.
        private static IEnumerable<DependencyObject> Walk(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;

                // Logical children first (covers items added in XAML reliably)
                foreach (var lc in LogicalTreeHelper.GetChildren(node))
                    if (lc is DependencyObject d) stack.Push(d);

                // Visual children (covers template-generated content once rendered).
                // VisualTreeHelper only accepts Visual / Visual3D — logical children
                // may include non-visual nodes (ColumnDefinition, RowDefinition, etc.).
                if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                {
                    int vc = 0;
                    try { vc = VisualTreeHelper.GetChildrenCount(node); } catch { vc = 0; }
                    for (int i = 0; i < vc; i++)
                        stack.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
        }
    }
}
