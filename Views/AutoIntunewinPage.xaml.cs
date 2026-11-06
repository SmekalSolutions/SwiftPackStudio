// =====================================================================
//  AutoIntunewinPage.xaml.cs
//  SwiftPack Studio — Auto Package › Intunewin tab
//
//  Drop-in path:  SwiftPack_v8/Views/AutoIntunewinPage.xaml.cs
//
//  Responsibilities:
//    • Self-contained .intunewin creation (wraps IntuneWinAppUtil.exe).
//    • Does NOT depend on CreateIntunewinPage, IntuneSettings.json,
//      PsExec.exe, or any other feature listed in the isolation spec.
//    • Communicates with the host (AutoPackagePage) only through
//      IAutoPackageContext (no compile-time type dependency).
//
//  Config / tools used (Auto feature only):
//    • AutoPackage.json  — for IntuneWinAppUtilPath resolution
//    • IntuneWinAppUtil.exe — at the same well-known path as AutoPackagePage
//
//  Public entry points (called by AutoPackagePage):
//    • IwAutoFillFromScript(string scriptPath)
//    • NavigateToIntunewinTabAndCreate(string packageFolder)
//    • SetPendingAfterChain(Action? chain)
//    • SetSuppressCompletionDialog(bool value)
// =====================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IOPath = System.IO.Path;
using Microsoft.Win32;

namespace SwiftPack.Views
{
    public partial class AutoIntunewinPage : Page
    {
        // ── Tool path (mirrors AutoPackagePage — Auto feature only) ──────
        private static readonly string IntuneWinAppUtilPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Smekal", "SwiftPack Studio", "Templates", "IntuneWinAppUtil.exe");

        // ── Host context (set by AutoPackagePage after Navigate) ─────────
        private IAutoPackageContext? _context;

        // ── Internal state ────────────────────────────────────────────────
        private bool _iwUserEditedName          = false;
        private bool _iwSuppressNameChange       = false;
        private bool _iwSuppressCompletionDialog = false;
        private CancellationTokenSource? _iwCts;
        private Process? _iwActiveProc;
        private volatile bool _iwCreationPaused  = false;

        // ── Action chain: fires after successful creation (→ Publish tab) ─
        private Action? _pendingAfterChain = null;

        // ══════════════════════════════════════════════════════════════════
        //  Construction
        // ══════════════════════════════════════════════════════════════════

        public AutoIntunewinPage()
        {
            InitializeComponent();
            Loaded += (_, _) => ApplyViewLock();
        }

        // ── View-lock: read-only page; only Cancel is user-interactive ──
        private bool _viewLockApplied;
        private void ApplyViewLock()
        {
            if (_viewLockApplied) return;
            _viewLockApplied = true;

            AutoViewLock.LockInputs(this);

            // Hide buttons not used by the automation.
            AutoViewLock.Hide(IwBtnBrowseFolder);
            AutoViewLock.Hide(IwBtnBrowseSetupFile);
            AutoViewLock.Hide(IwBtnBrowseOutputFolder);
            AutoViewLock.Hide(IwBtnClearAll);
            AutoViewLock.Hide(IwBtnClearLog);

            // Keep Create — greyed (look only; its IsEnabled is data-driven and
            // required for the automation's RaiseEvent).
            if (IwBtnCreate != null) IwBtnCreate.Visibility = Visibility.Collapsed;   // hidden; automation still fires it via RaiseEvent (IsEnabled stays data-driven)

            // Cancel stays active.
        }

        /// <summary>
        /// Called by AutoPackagePage immediately after Navigate() so this page
        /// can call back into the host without knowing its concrete type.
        /// </summary>
        public void SetContext(IAutoPackageContext context) => _context = context;

        // ══════════════════════════════════════════════════════════════════
        //  Public API — called by AutoPackagePage
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pre-fill all fields from a known script path.
        /// Called when the user manually switches to the Intunewin tab
        /// after a package has already been created.
        /// </summary>
        public void IwAutoFillFromScript(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath)) return;
            Dispatcher.Invoke(() =>
            {
                if (IwTxtPackageFolderPath == null) return;
                string ps1Dir   = IOPath.GetDirectoryName(scriptPath) ?? "";
                string pkgRoot  = IOPath.GetDirectoryName(ps1Dir)     ?? "";
                string baseName = IOPath.GetFileNameWithoutExtension(scriptPath);

                IwTxtPackageFolderPath.Text = ps1Dir;
                IwTxtSetupFile.Text         = scriptPath;
                IwTxtOutputFolder.Text      = !string.IsNullOrEmpty(pkgRoot)
                    ? IOPath.Combine(pkgRoot, "Intune") : ps1Dir;
                _iwUserEditedName = true;
                IwSetNameSilently(baseName);
                IwUpdateStatus();
            });
        }

        /// <summary>
        /// Navigate to this page's content, fill fields from packageFolder,
        /// pre-delete any stale .intunewin, then programmatically click Create.
        /// Called by AutoPackagePage as part of the auto-flow chain.
        /// </summary>
        public void NavigateToIntunewinTabAndCreate(string packageFolder)
        {
            // Tell host to switch tab strip to Intunewin
            _context?.ActivateTopTab("Intunewin");

            string sourceFolder = "";
            string sourceFile   = "";
            string outputFolder = "";
            string baseName     = "";

            if (!string.IsNullOrWhiteSpace(packageFolder) && Directory.Exists(packageFolder))
            {
                try
                {
                    string? ps1 = Directory.GetFiles(packageFolder,
                        "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(ps1) && File.Exists(ps1))
                    {
                        string ps1Dir  = IOPath.GetDirectoryName(ps1) ?? packageFolder;
                        string pkgRoot = IOPath.GetDirectoryName(ps1Dir) ?? packageFolder;
                        sourceFolder = ps1Dir;
                        sourceFile   = ps1;
                        // Intune folder is a sibling of the ps1's folder (its parent), so nested
                        // layouts like <pkg>\Output\AppPackage produce <pkg>\Output\Intune.
                        outputFolder = IOPath.Combine(pkgRoot, "Intune");
                        // Name the .intunewin after the PACKAGE folder (e.g. Smekal_FileCompare_1.0_01),
                        // not after the ps1's parent — which for nested layouts would be "Output".
                        baseName = IOPath.GetFileName(packageFolder.TrimEnd('\\', '/'));
                    }
                    else
                    {
                        string? installer =
                            Directory.GetFiles(packageFolder, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
                            ?? Directory.GetFiles(packageFolder, "*.msi", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(installer))
                        {
                            string instDir = IOPath.GetDirectoryName(installer) ?? packageFolder;
                            sourceFolder = instDir;
                            sourceFile   = installer;
                            outputFolder = IOPath.Combine(packageFolder, "Intune");
                            // Req 2: when there is NO PSADT template the .intunewin must be
                            // named after the PACKAGE folder (e.g. Smekal_FileCompare_1.0_01),
                            // NOT after the raw installer file (e.g. filecompare-1.0-x64).
                            baseName     = IOPath.GetFileName(packageFolder.TrimEnd('\\', '/'));
                            if (string.IsNullOrWhiteSpace(baseName))
                                baseName = IOPath.GetFileNameWithoutExtension(installer);
                        }
                    }

                    // Pre-delete any .intunewin built silently in Step 7
                    // so IwBtnCreate_Click does not show the overwrite dialog.
                    if (!string.IsNullOrWhiteSpace(outputFolder) && Directory.Exists(outputFolder))
                    {
                        foreach (string existing in Directory.GetFiles(outputFolder, "*.intunewin", SearchOption.TopDirectoryOnly))
                        {
                            try { File.Delete(existing); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                        }
                    }
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            if (IwTxtPackageFolderPath != null) IwTxtPackageFolderPath.Text = sourceFolder;
            if (IwTxtSetupFile         != null) IwTxtSetupFile.Text         = sourceFile;
            if (IwTxtOutputFolder      != null) IwTxtOutputFolder.Text      = outputFolder;
            _iwUserEditedName = !string.IsNullOrWhiteSpace(baseName);
            IwSetNameSilently(baseName);
            IwUpdateStatus();

            // Auto-trigger Create with dialog suppressed
            if (IwBtnCreate != null && IwBtnCreate.IsEnabled)
            {
                _iwSuppressCompletionDialog = true;
                IwBtnCreate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }

        /// <summary>Store the action that fires after intunewin creation (→ Publish tab).</summary>
        public void SetPendingAfterChain(Action? chain) => _pendingAfterChain = chain;

        /// <summary>Suppress the success dialog for unattended (auto-flow) runs.</summary>
        public void SetSuppressCompletionDialog(bool value) => _iwSuppressCompletionDialog = value;

        // ══════════════════════════════════════════════════════════════════
        //  Status / button state
        // ══════════════════════════════════════════════════════════════════

        private void IwUpdateStatus()
        {
            bool hasFolder = Directory.Exists(IwTxtPackageFolderPath?.Text?.Trim() ?? "");
            bool hasSetup  = File.Exists(IwTxtSetupFile?.Text?.Trim() ?? "");
            bool hasOutput = !string.IsNullOrWhiteSpace(IwTxtOutputFolder?.Text?.Trim());
            if (IwBtnCreate != null) IwBtnCreate.IsEnabled = hasFolder && hasSetup && hasOutput;
        }

        private void IwSetInputControlsEnabled(bool enabled)
        {
            if (IwBtnBrowseFolder       != null) IwBtnBrowseFolder.IsEnabled       = enabled;
            if (IwBtnBrowseSetupFile    != null) IwBtnBrowseSetupFile.IsEnabled    = enabled;
            if (IwBtnBrowseOutputFolder != null) IwBtnBrowseOutputFolder.IsEnabled = enabled;
            if (IwBtnClearAll           != null) IwBtnClearAll.IsEnabled           = enabled;
            if (IwBtnClearLog           != null) IwBtnClearLog.IsEnabled           = enabled;
            if (IwTxtPackageFolderPath  != null) IwTxtPackageFolderPath.IsReadOnly = !enabled;
            if (IwTxtSetupFile          != null) IwTxtSetupFile.IsReadOnly         = !enabled;
            if (IwTxtOutputFolder       != null) IwTxtOutputFolder.IsReadOnly      = !enabled;
            if (IwTxtIntunewinName      != null) IwTxtIntunewinName.IsReadOnly     = !enabled;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Name helpers
        // ══════════════════════════════════════════════════════════════════

        private void IwSetNameSilently(string value)
        {
            _iwSuppressNameChange  = true;
            if (IwTxtIntunewinName != null) IwTxtIntunewinName.Text = value;
            _iwSuppressNameChange  = false;
        }

        private void IwResetName()
        {
            _iwUserEditedName = false;
            IwSetNameSilently(string.Empty);
        }

        private static bool IwFolderHasFiles(string folderPath)
        {
            try { return Directory.Exists(folderPath) && Directory.EnumerateFiles(folderPath).Any(); }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════════
        //  TextChanged handlers
        // ══════════════════════════════════════════════════════════════════

        private void IwTxtPackageFolderPath_TextChanged(object sender, TextChangedEventArgs e) => IwUpdateStatus();
        private void IwTxtOutputFolder_TextChanged(object sender, TextChangedEventArgs e)       => IwUpdateStatus();

        private void IwTxtSetupFile_TextChanged(object sender, TextChangedEventArgs e)
        {
            string file = IwTxtSetupFile?.Text?.Trim() ?? "";
            if (!_iwUserEditedName && !string.IsNullOrWhiteSpace(file))
                IwSetNameSilently(IOPath.GetFileNameWithoutExtension(file) + ".intunewin");
            IwUpdateStatus();
        }

        private void IwTxtIntunewinName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_iwSuppressNameChange) return;
            if (!string.IsNullOrWhiteSpace(IwTxtSetupFile?.Text?.Trim()))
                _iwUserEditedName = true;
        }

        private void IwPathBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb) { tb.CaretIndex = tb.Text.Length; tb.ScrollToEnd(); }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Browse handlers
        // ══════════════════════════════════════════════════════════════════

        private IntPtr IwOwnerHwnd =>
            new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;

        private void IwBtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            string? chosen = ShellFolderPicker.Browse(
                IwOwnerHwnd, "Select the Source Folder",
                IwTxtPackageFolderPath?.Text?.Trim() ?? "", allowNewFolder: false);
            if (chosen == null) return;

            if (!IwFolderHasFiles(chosen))
            {
                new NoFilesInFolderDialog(chosen) { Owner = Window.GetWindow(this) }.ShowDialog();
                if (IwTxtPackageFolderPath != null) IwTxtPackageFolderPath.Text = string.Empty;
                if (IwTxtSetupFile         != null) IwTxtSetupFile.Text         = string.Empty;
                IwResetName(); IwUpdateStatus(); return;
            }

            if (IwTxtPackageFolderPath != null) IwTxtPackageFolderPath.Text = chosen;

            string setupFile = IwTxtSetupFile?.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(setupFile) &&
                !setupFile.StartsWith(chosen, StringComparison.OrdinalIgnoreCase))
            {
                if (IwTxtSetupFile != null) IwTxtSetupFile.Text = string.Empty;
                IwResetName();
            }
            IwUpdateStatus();
            IwLog($"Source folder selected: {chosen}");
        }

        private void IwBtnBrowseSetupFile_Click(object sender, RoutedEventArgs e)
        {
            string systemDrive = IOPath.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            string initialDir  = IwTxtPackageFolderPath?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
                initialDir = systemDrive;

            var dlg = new OpenFileDialog
            {
                Title            = "Select Source File",
                Filter           = "All Supported Files|*.exe;*.msi;*.ps1;*.cmd;*.bat|" +
                                   "Executables (*.exe)|*.exe|MSI Packages (*.msi)|*.msi|" +
                                   "PowerShell Scripts (*.ps1)|*.ps1|Scripts (*.cmd;*.bat)|*.cmd;*.bat|" +
                                   "All Files (*.*)|*.*",
                InitialDirectory = initialDir
            };
            if (dlg.ShowDialog() != true) return;

            string chosenFile   = dlg.FileName;
            string sourceFolder = IwTxtPackageFolderPath?.Text?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder) &&
                !chosenFile.StartsWith(sourceFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                IwShowStyledMessage("File Outside Source Folder",
                    $"The selected file is not inside the Source Folder.\n\n" +
                    $"Source Folder : {sourceFolder}\nSelected File : {chosenFile}\n\n" +
                    "Please select a file that resides inside the Source Folder.");
                return;
            }

            if (IwTxtSetupFile != null) IwTxtSetupFile.Text = chosenFile;
            if (!_iwUserEditedName)
                IwSetNameSilently(IOPath.GetFileNameWithoutExtension(chosenFile) + ".intunewin");
            IwUpdateStatus();
            IwLog($"Source file selected: {chosenFile}");
        }

        private void IwBtnBrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            string? chosen = ShellFolderPicker.Browse(
                IwOwnerHwnd, "Select the Output Folder for the .intunewin file",
                IwTxtOutputFolder?.Text?.Trim() ?? "", allowNewFolder: true);
            if (chosen == null) return;
            if (IwTxtOutputFolder != null) IwTxtOutputFolder.Text = chosen;
            IwLog($"Output folder set: {chosen}");
        }

        // ══════════════════════════════════════════════════════════════════
        //  Drag & Drop handlers
        // ══════════════════════════════════════════════════════════════════

        private void IwTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                        ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void IwTxtPackageFolderPath_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            string folder  = Directory.Exists(paths[0]) ? paths[0] : IOPath.GetDirectoryName(paths[0]) ?? paths[0];
            if (!IwFolderHasFiles(folder))
            {
                new NoFilesInFolderDialog(folder) { Owner = Window.GetWindow(this) }.ShowDialog();
                if (IwTxtPackageFolderPath != null) IwTxtPackageFolderPath.Text = string.Empty;
                if (IwTxtSetupFile         != null) IwTxtSetupFile.Text         = string.Empty;
                IwResetName(); IwUpdateStatus(); return;
            }
            if (IwTxtPackageFolderPath != null) IwTxtPackageFolderPath.Text = folder;
            string setupFile = IwTxtSetupFile?.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(setupFile) &&
                !setupFile.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                if (IwTxtSetupFile != null) IwTxtSetupFile.Text = string.Empty;
                IwResetName();
            }
            IwUpdateStatus();
            IwLog($"Source folder set via drag & drop: {folder}");
        }

        private void IwTxtSetupFile_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            string file = paths[0];
            if (!File.Exists(file)) return;
            string sourceFolder = IwTxtPackageFolderPath?.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder) &&
                !file.StartsWith(sourceFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                IwShowStyledMessage("File Outside Source Folder",
                    $"The dropped file is not inside the Source Folder.\n\n" +
                    $"Source Folder : {sourceFolder}\nDropped File  : {file}\n\n" +
                    "Please drop a file that resides inside the Source Folder.");
                return;
            }
            if (IwTxtSetupFile != null) IwTxtSetupFile.Text = file;
            if (!_iwUserEditedName)
                IwSetNameSilently(IOPath.GetFileNameWithoutExtension(file) + ".intunewin");
            IwUpdateStatus();
            IwLog($"Source file set via drag & drop: {file}");
        }

        private void IwTxtOutputFolder_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            string folder  = Directory.Exists(paths[0]) ? paths[0] : IOPath.GetDirectoryName(paths[0]) ?? paths[0];
            if (IwTxtOutputFolder != null) IwTxtOutputFolder.Text = folder;
            IwLog($"Output folder set via drag & drop: {folder}");
        }

        private void IwTxtIntunewinName_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            string name = IOPath.GetFileNameWithoutExtension(paths[0]) + ".intunewin";
            if (IwTxtIntunewinName != null) IwTxtIntunewinName.Text = name;
            IwLog($"Intunewin name set via drag & drop: {name}");
        }

        // ══════════════════════════════════════════════════════════════════
        //  Clear All
        // ══════════════════════════════════════════════════════════════════

        private void IwBtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (IwTxtPackageFolderPath != null) IwTxtPackageFolderPath.Text = string.Empty;
            if (IwTxtSetupFile         != null) IwTxtSetupFile.Text         = string.Empty;
            if (IwTxtOutputFolder      != null) IwTxtOutputFolder.Text      = string.Empty;
            IwResetName();
            if (IwTxtEventLog != null)
                IwTxtEventLog.Text = "[Ready] Select a Source Folder, Source File and Output Folder, then click Create…";
            IwUpdateStatus();
        }

        // ══════════════════════════════════════════════════════════════════
        //  Log helpers
        // ══════════════════════════════════════════════════════════════════

        private void IwLog(string msg, bool timestamp = true)
        {
            string line = timestamp ? $"[{DateTime.Now:HH:mm:ss}] {msg}" : msg;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IwTxtEventLog == null) return;
                IwTxtEventLog.AppendText("\n" + line);
                IwTxtEventLog.ScrollToEnd();
                IwLogScrollViewer?.ScrollToEnd();
            }));
        }

        private void IwBtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            if (IwTxtEventLog != null)
                IwTxtEventLog.Text = "[Ready] Select a Source Folder, Source File and Output Folder, then click Create…";
        }

        // ══════════════════════════════════════════════════════════════════
        //  Cancel creation
        // ══════════════════════════════════════════════════════════════════

        private void IwBtnCancelCreate_Click(object sender, RoutedEventArgs e) => IwCancelCreation();

        private void IwCancelCreation()
        {
            if (_iwCreationPaused) return;
            var proc = _iwActiveProc;
            if (proc == null || proc.HasExited) return;

            _iwCreationPaused = true;
            IwSuspendProcess(proc);
            IwLog("⏸  Creation paused — waiting for user confirmation…");

            bool cancelConfirmed = IwShowCancelDialog();

            if (cancelConfirmed)
            {
                _iwCreationPaused = false;
                try { if (!proc.HasExited) proc.Kill(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                try { _iwCts?.Cancel(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                if (IwBtnCancelCreate != null) IwBtnCancelCreate.IsEnabled = false;
                IwLog("🛑  Cancellation confirmed by user.");
            }
            else
            {
                _iwCreationPaused = false;
                IwResumeProcess(proc);
                IwLog("▶  Creation resumed.");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Suspend / Resume process threads
        // ══════════════════════════════════════════════════════════════════

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr IwOpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint IwSuspendThread(IntPtr hThread);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern int IwResumeThread(IntPtr hThread);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool IwCloseHandle(IntPtr hObject);
        private const uint IW_THREAD_SUSPEND_RESUME = 0x0002;

        private void IwSuspendProcess(Process proc)
        {
            try
            {
                foreach (ProcessThread thread in proc.Threads)
                {
                    IntPtr h = IwOpenThread(IW_THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                    if (h != IntPtr.Zero) { IwSuspendThread(h); IwCloseHandle(h); }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private void IwResumeProcess(Process proc)
        {
            try
            {
                foreach (ProcessThread thread in proc.Threads)
                {
                    IntPtr h = IwOpenThread(IW_THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                    if (h != IntPtr.Zero) { IwResumeThread(h); IwCloseHandle(h); }
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Create .intunewin (async — mirrors original IwBtnCreate_Click)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Path of the shared per-package log, revision-suffixed:
        /// %TEMP%\AutoPackage_{Vendor}_{App}_{Version}_{Revision}.log - the one file the Package,
        /// Execute, Intunewin and Publish steps all append to. The name is read from the PSADT
        /// script inside the source folder; the folder name is the fallback.
        /// </summary>
        private static string IwPackageLogPath(string sourceFolder)
        {
            string iwLogBaseName = "";
            try
            {
                string? ps1File = Directory.GetFiles(sourceFolder, "Invoke-AppDeployToolkit.ps1",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (ps1File != null)
                {
                    string ps1Text = File.ReadAllText(ps1File);
                    string GetField(string key)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(ps1Text,
                            @"(?m)^\s+" + System.Text.RegularExpressions.Regex.Escape(key) +
                            @"\s*=\s*'([^']*)'",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        return m.Success ? m.Groups[1].Value.Trim() : "";
                    }
                    string San(string s)
                    {
                        foreach (char c in IOPath.GetInvalidFileNameChars()) s = s.Replace(c, '_');
                        return s.Replace(" ", "").Trim('_');
                    }
                    var parts = new[] { San(GetField("AppVendor")), San(GetField("AppName")), San(GetField("AppVersion")), San(GetField("AppRevision")) }
                        .Where(p => !string.IsNullOrEmpty(p)).ToArray();
                    if (parts.Length > 0) iwLogBaseName = string.Join("_", parts);
                }
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            if (string.IsNullOrWhiteSpace(iwLogBaseName))
                iwLogBaseName = IOPath.GetFileName(sourceFolder.TrimEnd('\\', '/'));
            return IOPath.Combine(IOPath.GetTempPath(), $"AutoPackage_{iwLogBaseName}.log");
        }

        /// <summary>
        /// End a create that cannot start: write the reason to the per-package log, mark the
        /// Create intunewin step failed, show the reason in the Download Information column, drop
        /// the pending chain so nothing downstream runs, and release the auto-flow, which clears
        /// FlowRunning (top tabs interactive again) and returns to the Download tab.
        /// </summary>
        private void IwAbortCreate(string title, string message, string hint, string logPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(logPath))
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss}] Create intunewin ABORTED: {message}{Environment.NewLine}" +
                        $"[{DateTime.Now:HH:mm:ss}] {hint}{Environment.NewLine}");
            }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            try { AutoFlowStatus.Set(AutoFlowStatus.CreateIntunewin, AutoFlowStatus.State.Failed); }
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            ReportPackageErrorToDownloadTab(title, message, hint);

            _pendingAfterChain          = null;   // nothing downstream may run
            _iwSuppressCompletionDialog = false;
            try { _context?.NotifyAutoFlowAborted(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        // Surface a failed step in the Download tab's Download Information column. Reflection
        // keeps this page free of a compile-time dependency on SourceDownloadPage. No-op when
        // that page is not loaded.
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
            catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
        }

        private async void IwBtnCreate_Click(object sender, RoutedEventArgs e)
        {
            string sourceFolder  = IwTxtPackageFolderPath?.Text?.Trim() ?? "";
            string setupFile     = IwTxtSetupFile?.Text?.Trim()         ?? "";
            string outputFolder  = IwTxtOutputFolder?.Text?.Trim()      ?? "";
            string intunewinName = IwTxtIntunewinName?.Text?.Trim()     ?? "";

            // ── Validation ────────────────────────────────────────────────
            if (!Directory.Exists(sourceFolder))
            {
                IwShowStyledMessage("Invalid Folder", "The selected source folder does not exist."); return;
            }
            if (!IwFolderHasFiles(sourceFolder))
            {
                new NoFilesInFolderDialog(sourceFolder) { Owner = Window.GetWindow(this) }.ShowDialog(); return;
            }
            if (!File.Exists(setupFile))
            {
                IwShowStyledMessage("Invalid File", "The selected source file does not exist."); return;
            }
            if (!setupFile.StartsWith(sourceFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                IwShowStyledMessage("File Outside Source Folder",
                    $"The source file is not inside the Source Folder.\n\n" +
                    $"Source Folder : {sourceFolder}\nSource File   : {setupFile}\n\n" +
                    "Please select a file that resides inside the Source Folder."); return;
            }
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                IwShowStyledMessage("Output Folder Required", "Output Folder is required. Please browse or type a folder path."); return;
            }
            if (!File.Exists(IntuneWinAppUtilPath))
            {
                // No modal here. The auto-flow drives this page unattended, so a message box just
                // parks the whole flow on this tab: every top tab stays locked behind
                // AutoFlowStatus.FlowRunning and nothing returns to the Download tab. Log it,
                // surface it in the Download Information column, and abort the flow instead.
                string iwToolMsg  = $"IntuneWinAppUtil.exe not found on this machine: \"{IntuneWinAppUtilPath}\"";
                string iwToolHint = "The .intunewin was not created. Ensure the tool is installed with SwiftPack Studio, then run again.";
                IwLog("❌ " + iwToolMsg);
                IwAbortCreate("Create intunewin failed", iwToolMsg, iwToolHint, IwPackageLogPath(sourceFolder));
                return;
            }

            // ── Ensure output folder exists ───────────────────────────────
            try
            {
                if (!Directory.Exists(outputFolder))
                { Directory.CreateDirectory(outputFolder); IwLog($"Created output folder: {outputFolder}"); }
                else
                { IwLog($"Output folder already exists: {outputFolder}"); }
            }
            catch (Exception ex)
            {
                IwShowStyledMessage("Folder Error", $"Failed to create output folder:\n{ex.Message}"); return;
            }

            string setupFileName = IOPath.GetFileName(setupFile);

            if (string.IsNullOrWhiteSpace(intunewinName))
                intunewinName = IOPath.GetFileNameWithoutExtension(setupFileName) + ".intunewin";
            if (!intunewinName.EndsWith(".intunewin", StringComparison.OrdinalIgnoreCase))
                intunewinName += ".intunewin";

            // ── Overwrite check ───────────────────────────────────────────
            string defaultIntunewinName = IOPath.GetFileNameWithoutExtension(setupFileName) + ".intunewin";
            string existingFile = IOPath.Combine(outputFolder, defaultIntunewinName);
            if (File.Exists(existingFile))
            {
                bool overwrite = IwShowFileExistsDialog(existingFile);
                if (!overwrite)
                {
                    IwLog("⚠  Aborted — output file already exists and overwrite was declined.");
                    if (IwBtnCreate       != null) IwBtnCreate.IsEnabled       = true;
                    if (IwBtnCancelCreate != null) IwBtnCancelCreate.IsEnabled = false;
                    IwSetInputControlsEnabled(true);
                    IwUpdateStatus(); return;
                }
                try   { File.Delete(existingFile); IwLog($"Deleted existing: {existingFile}"); }
                catch (Exception ex) { IwLog($"⚠  Could not delete existing file: {ex.Message}"); }
            }

            // ── Build log file path ───────────────────────────────────────
            string iwLogPath = IwPackageLogPath(sourceFolder);

            // ── Prepare UI for creation ───────────────────────────────────
            string args = $"-c \"{sourceFolder}\" -s \"{setupFileName}\" -o \"{outputFolder}\"";

            if (IwBtnCreate         != null) IwBtnCreate.IsEnabled         = false;
            if (IwBtnCancelCreate   != null) IwBtnCancelCreate.IsEnabled   = true;
            IwSetInputControlsEnabled(false);
            if (IwPrgCreateProgress != null) IwPrgCreateProgress.Visibility = Visibility.Visible;

            _iwCreationPaused = false;
            _iwCts = new CancellationTokenSource();
            var token = _iwCts.Token;

            IwLog("─────────────────────────────────────────────────────");
            IwLog($"Tool    : {IntuneWinAppUtilPath}");
            IwLog($"Source  : {sourceFolder}");
            IwLog($"File    : {setupFileName}");
            IwLog($"Output  : {outputFolder}");
            IwLog($"Command : IntuneWinAppUtil.exe {args}");
            IwLog("─────────────────────────────────────────────────────");
            IwLog("Starting IntuneWinAppUtil…");

            // ── Run IntuneWinAppUtil in background ────────────────────────
            int exitCode = -1;
            // Append to the shared per-package log (revision-suffixed) so Package, Execute,
            // Intunewin and Publish all write to ONE file.
            var iwLogWriter = new StreamWriter(iwLogPath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
            iwLogWriter.WriteLine();
            iwLogWriter.WriteLine("════════════════════════════════════════════════════════════");
            iwLogWriter.WriteLine($"  CREATE INTUNEWIN - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            iwLogWriter.WriteLine("════════════════════════════════════════════════════════════");

            void ProcessLine(string line, bool isProgress)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    IwLog(isProgress ? $"  {line.Trim()}" : line);
                    try { iwLogWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line.Trim()}"); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
                }
            }

            var iwStartTime = DateTime.Now;
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = IntuneWinAppUtilPath,
                    Arguments              = args,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    WorkingDirectory       = sourceFolder,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding  = System.Text.Encoding.UTF8,
                };
                using var proc = new Process { StartInfo = psi };
                _iwActiveProc = proc;
                proc.Start();

                using var reg = token.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } });

                var stderrThread = new System.Threading.Thread(() =>
                {
                    var sb = new System.Text.StringBuilder(); string lastPct = "";
                    int ch;
                    while ((ch = proc.StandardError.Read()) != -1)
                    {
                        char c = (char)ch;
                        if (c == '\r') { string raw = sb.ToString().Trim(); sb.Clear(); if (!string.IsNullOrWhiteSpace(raw) && raw != lastPct) { lastPct = raw; ProcessLine(raw, true); } }
                        else if (c == '\n') { string raw = sb.ToString().TrimEnd('\r').Trim(); sb.Clear(); if (!string.IsNullOrWhiteSpace(raw)) ProcessLine(raw, false); }
                        else sb.Append(c);
                    }
                    string tail = sb.ToString().Trim(); if (!string.IsNullOrWhiteSpace(tail)) ProcessLine(tail, false);
                });
                stderrThread.IsBackground = true;
                stderrThread.Start();

                var sbOut = new System.Text.StringBuilder(); string lastOutPct = "";
                int och;
                while ((och = proc.StandardOutput.Read()) != -1)
                {
                    char c = (char)och;
                    if (c == '\r') { string raw = sbOut.ToString().Trim(); sbOut.Clear(); if (!string.IsNullOrWhiteSpace(raw) && raw != lastOutPct) { lastOutPct = raw; ProcessLine(raw, true); } }
                    else if (c == '\n') { string raw = sbOut.ToString().TrimEnd('\r').Trim(); sbOut.Clear(); if (!string.IsNullOrWhiteSpace(raw)) ProcessLine(raw, false); }
                    else sbOut.Append(c);
                }
                string outTail = sbOut.ToString().Trim(); if (!string.IsNullOrWhiteSpace(outTail)) ProcessLine(outTail, false);

                stderrThread.Join();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            });

            // ── Rename output if user specified a custom name ─────────────
            string defaultName   = IOPath.GetFileNameWithoutExtension(setupFileName) + ".intunewin";
            string defaultOutput = IOPath.Combine(outputFolder, defaultName);
            string desiredOutput = IOPath.Combine(outputFolder, intunewinName);

            if (!intunewinName.Equals(defaultName, StringComparison.OrdinalIgnoreCase) && File.Exists(defaultOutput))
            {
                try
                {
                    if (File.Exists(desiredOutput)) File.Delete(desiredOutput);
                    File.Move(defaultOutput, desiredOutput);
                    IwLog($"Renamed output to: {intunewinName}");
                }
                catch (Exception ex) { IwLog($"⚠  Could not rename file: {ex.Message}"); desiredOutput = defaultOutput; }
            }

            try { iwLogWriter.Close(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }

            // ── Restore UI ────────────────────────────────────────────────
            bool cancelled = _iwCts?.IsCancellationRequested == true;
            _iwCts?.Dispose(); _iwCts = null;
            _iwActiveProc = null;
            _iwCreationPaused = false;

            if (IwBtnCreate         != null) IwBtnCreate.IsEnabled         = true;
            if (IwPrgCreateProgress != null) IwPrgCreateProgress.Visibility = Visibility.Collapsed;
            if (IwBtnCancelCreate   != null) IwBtnCancelCreate.IsEnabled   = false;
            IwSetInputControlsEnabled(true);
            IwUpdateStatus();

            if (cancelled) { IwLog("⚠  Creation cancelled by user."); return; }

            // ── Success path ──────────────────────────────────────────────
            if (exitCode == 0)
            {
                AutoFlowStatus.Set(AutoFlowStatus.CreateIntunewin, AutoFlowStatus.State.Ok);
                string finalFile = File.Exists(desiredOutput) ? desiredOutput : defaultOutput;
                string finalName = IOPath.GetFileName(finalFile);
                IwLog($"✅ Success! .intunewin created: {finalName}");
                IwLog($"   Location: {outputFolder}");
                IwLog("─────────────────────────────────────────────────────");
                IwLog($"📋 Log: {iwLogPath}");

                // Notify host so MainWindow can auto-fill CreateDocument fields
                _context?.NotifyIntunewinCreated(finalFile);

                // Show success dialog for manual runs, OR during the auto-flow when the
                // per-app ShowMessageBox flag is Yes (mirrored in AutoShowMessageBox.Value).
                bool suppressDlg = _iwSuppressCompletionDialog && !AutoShowMessageBox.Value;
                _iwSuppressCompletionDialog = false;
                if (!suppressDlg)
                {
                    var successDlg = new IntunewinSuccessDialog(finalFile, outputFolder, iwLogPath, iwStartTime)
                        { Owner = Window.GetWindow(this) };
                    successDlg.ShowDialog();
                }

                // Fire the pending chain (→ Publish tab) and reset flow state
                Action? afterIw = _pendingAfterChain;
                _pendingAfterChain = null;
                if (afterIw == null)
                    if (_context != null) _context.ShowMessageBox = true;
                if (afterIw != null)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, afterIw);
            }
            else
            {
                AutoFlowStatus.Set(AutoFlowStatus.CreateIntunewin, AutoFlowStatus.State.Failed);
                IwLog($"❌ IntuneWinAppUtil exited with code {exitCode}.");
                IwLog("─────────────────────────────────────────────────────");
                IwShowStyledMessage("Creation Failed",
                    $"Failed to create .intunewin package.\n\nIntuneWinAppUtil exited with code {exitCode}.\n\nCheck the Event Log for details.");
                // Abort the chain: Publish and all later steps are already skipped (the pending chain
                // was not fired). Release the auto-flow lock so the Download/Package columns and top
                // tabs reactivate, and paint the failed status.
                try { _context?.NotifyAutoFlowAborted(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Styled dialogs — self-contained, no dependency on other dialogs
        // ══════════════════════════════════════════════════════════════════

        private bool IwShowFileExistsDialog(string filePath)
        {
            bool result = false;
            var win = IwBuildStyledDialog("Create Intunewin - File Exists");
            var (root, boxBg, boxBdr, lblFg, valFg, sec) = IwBuildDialogContent(win);
            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(62) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var l1 = IwMakeLabel("File", lblFg);   var b1 = IwMakeBox(filePath, boxBg, boxBdr, valFg);
            System.Windows.Controls.Grid.SetColumn(l1, 0); System.Windows.Controls.Grid.SetColumn(b1, 1);
            grid.Children.Add(l1); grid.Children.Add(b1);
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(8) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var l2 = IwMakeLabel("Action", lblFg); var b2 = IwMakeBox("File already exists. Do you want to overwrite it?", boxBg, boxBdr, valFg, false);
            System.Windows.Controls.Grid.SetRow(l2, 2); System.Windows.Controls.Grid.SetColumn(l2, 0);
            System.Windows.Controls.Grid.SetRow(b2, 2); System.Windows.Controls.Grid.SetColumn(b2, 1);
            grid.Children.Add(l2); grid.Children.Add(b2);
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(8) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var btns = IwMakeYesNoButtons(sec, () => { result = true; win.Close(); }, () => { result = false; win.Close(); });
            var icon = IwDecisionIcon();
            System.Windows.Controls.Grid.SetRow(btns, 4); System.Windows.Controls.Grid.SetColumn(btns, 1);
            System.Windows.Controls.Grid.SetRow(icon, 4); System.Windows.Controls.Grid.SetColumn(icon, 0);
            grid.Children.Add(btns); grid.Children.Add(icon);
            root.Children.Add(grid);
            win.ShowDialog();
            return result;
        }

        private bool IwShowCancelDialog()
        {
            bool result = false;
            var win = IwBuildStyledDialog("Create Intunewin - Cancel Creation");
            var (root, boxBg, boxBdr, lblFg, valFg, sec) = IwBuildDialogContent(win);
            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(62) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var l1 = IwMakeLabel("Status", lblFg); var b1 = IwMakeBox("Intunewin creation is in progress.", boxBg, boxBdr, valFg, false);
            System.Windows.Controls.Grid.SetColumn(l1, 0); System.Windows.Controls.Grid.SetColumn(b1, 1);
            grid.Children.Add(l1); grid.Children.Add(b1);
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(8) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var l2 = IwMakeLabel("Action", lblFg); var b2 = IwMakeBox("Do you want to cancel it?", boxBg, boxBdr, valFg, false);
            System.Windows.Controls.Grid.SetRow(l2, 2); System.Windows.Controls.Grid.SetColumn(l2, 0);
            System.Windows.Controls.Grid.SetRow(b2, 2); System.Windows.Controls.Grid.SetColumn(b2, 1);
            grid.Children.Add(l2); grid.Children.Add(b2);
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(8) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var btns = IwMakeYesNoButtons(sec, () => { result = true; win.Close(); }, () => { result = false; win.Close(); });
            var icon = IwDecisionIcon();
            System.Windows.Controls.Grid.SetRow(btns, 4); System.Windows.Controls.Grid.SetColumn(btns, 1);
            System.Windows.Controls.Grid.SetRow(icon, 4); System.Windows.Controls.Grid.SetColumn(icon, 0);
            grid.Children.Add(btns); grid.Children.Add(icon);
            root.Children.Add(grid);
            win.ShowDialog();
            return result;
        }

        private Window IwBuildStyledDialog(string title)
        {
            var winBg     = Application.Current.TryFindResource("WindowBgBrush")   as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
            var win = new Window
            {
                Title = title, Width = 460, MaxWidth = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false
            };
            var outer = new System.Windows.Controls.Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = System.Windows.Media.Colors.Black }
            };
            win.Content = outer;
            return win;
        }

        private (System.Windows.Controls.Grid root,
                 System.Windows.Media.Brush boxBg, System.Windows.Media.Brush boxBdr,
                 System.Windows.Media.Brush lblFg, System.Windows.Media.Brush valFg,
                 Style? sec)
            IwBuildDialogContent(Window win)
        {
            var borderClr  = Application.Current.TryFindResource("BorderMainBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            System.Windows.Media.Brush titleTxtBrush = System.Windows.Media.Brushes.White;
            System.Windows.Media.Brush titleSecBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

            var outerGrid = new System.Windows.Controls.Grid();
            outerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            ((System.Windows.Controls.Border)win.Content).Child = outerGrid;

            var titleBar = new System.Windows.Controls.Border { Background = titleBarBg, BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(16, 6, 12, 6) };
            titleBar.MouseLeftButtonDown += (_, _) => win.DragMove();
            System.Windows.Controls.Grid.SetRow(titleBar, 0);

            var tg = new System.Windows.Controls.Grid();
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            var tIcon  = new System.Windows.Controls.TextBlock { Text = "☁", FontSize = 14, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tText  = new System.Windows.Controls.TextBlock { Text = win.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center };
            var tClose = new System.Windows.Controls.Button { Content = "✕", FontSize = 11, Foreground = titleSecBrush, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tClose.Click += (_, _) => win.Close();
            System.Windows.Controls.Grid.SetColumn(tIcon, 0); System.Windows.Controls.Grid.SetColumn(tText, 1); System.Windows.Controls.Grid.SetColumn(tClose, 2);
            tg.Children.Add(tIcon); tg.Children.Add(tText); tg.Children.Add(tClose);
            titleBar.Child = tg;
            outerGrid.Children.Add(titleBar);

            var root = new System.Windows.Controls.Grid { Margin = new Thickness(8) };
            System.Windows.Controls.Grid.SetRow(root, 1);
            outerGrid.Children.Add(root);

            var boxBg  = Application.Current.TryFindResource("InputBgBrush")       as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x5C, 0x7A));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x2E));
            var sec    = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;

            return (root, boxBg, boxBdr, lblFg, valFg, sec);
        }

        private static System.Windows.Controls.TextBlock IwMakeLabel(string text, System.Windows.Media.Brush fg) =>
            new System.Windows.Controls.TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center };

        private static System.Windows.Controls.Border IwMakeBox(string text, System.Windows.Media.Brush bg, System.Windows.Media.Brush bdr, System.Windows.Media.Brush fg, bool mono = true)
        {
            var box = new System.Windows.Controls.Border { Background = bg, BorderBrush = bdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };
            box.Child = new System.Windows.Controls.TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = fg, FontFamily = mono ? new System.Windows.Media.FontFamily("Consolas") : new System.Windows.Media.FontFamily("Segoe UI") };
            return box;
        }

        private static System.Windows.Controls.Primitives.UniformGrid IwMakeYesNoButtons(Style? sec, Action onYes, Action onNo)
        {
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };
            var yes = new System.Windows.Controls.Button { Content = "✔ Yes", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0) };
            IwApplyPrimaryLook(yes);
            yes.Click += (_, _) => onYes();
            var no  = new System.Windows.Controls.Button { Content = "✕ No",  Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), IsDefault = true };
            no.Click  += (_, _) => onNo();
            grid.Children.Add(yes); grid.Children.Add(no);
            return grid;
        }

        // Themed single-OK message dialog, replacing raw MessageBox.Show so warnings/errors
        // match the app's look. Reuses the page's dialog builders.
        private void IwShowStyledMessage(string title, string message)
        {
            var win = IwBuildStyledDialog(title);
            var (root, _, _, _, valFg, sec) = IwBuildDialogContent(win);

            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var msg = new System.Windows.Controls.TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = valFg, Margin = new Thickness(8, 8, 8, 12) };
            System.Windows.Controls.Grid.SetRow(msg, 0); root.Children.Add(msg);

            var actionRow = new System.Windows.Controls.Grid { Margin = new Thickness(8, 0, 8, 8) };
            actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dIcon = IwDecisionIcon();
            System.Windows.Controls.Grid.SetColumn(dIcon, 0);
            actionRow.Children.Add(dIcon);
            var okBtn = new System.Windows.Controls.Button { Content = "✔ OK", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(16, 0, 16, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            IwApplyPrimaryLook(okBtn);
            okBtn.Click += (_, _) => win.Close();
            System.Windows.Controls.Grid.SetColumn(okBtn, 1);
            actionRow.Children.Add(okBtn);
            System.Windows.Controls.Grid.SetRow(actionRow, 1); root.Children.Add(actionRow);

            win.ShowDialog();
        }

        // Give a dialog button the highlighted "primary action" look: a filled
        // accent background, rounded corners and a subtle hover. Theme resources
        // keep it readable in both light and dark modes, mirroring the Publish
        // Package dialog. Applied on top of any existing Style (local values win
        // in WPF), so callers keep their own sizing.
        private static void IwApplyPrimaryLook(System.Windows.Controls.Button b)
        {
            var bg     = Application.Current.TryFindResource("DlgBtnBlueBgBrush")     as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x9B, 0xF5));
            var fg     = Application.Current.TryFindResource("DlgBtnBlueFgBrush")     as System.Windows.Media.Brush ?? (System.Windows.Media.Brush)System.Windows.Media.Brushes.White;
            var border = Application.Current.TryFindResource("DlgBtnBlueBorderBrush") as System.Windows.Media.Brush ?? bg;

            b.Background      = bg;
            b.Foreground      = fg;
            b.BorderBrush     = border;
            b.BorderThickness = new Thickness(1);
            b.Cursor          = System.Windows.Input.Cursors.Hand;

            var tpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button));
            var bd  = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            bd.SetValue(System.Windows.Controls.Border.BackgroundProperty,      bg);
            bd.SetValue(System.Windows.Controls.Border.BorderBrushProperty,     border);
            bd.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(System.Windows.Controls.Border.CornerRadiusProperty,    new CornerRadius(6));
            var cp = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 12, 0));
            cp.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
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
        private static System.Windows.Controls.TextBlock IwDecisionIcon()
            => new System.Windows.Controls.TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };
        // ── CancelAndClear — called by AutoPackagePage.ResetAllTabs() ────
        /// <summary>Cancel any in-progress creation and clear all fields.</summary>
        public void CancelAndClear()
        {
            try { _iwCts?.Cancel(); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoIntunewinPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            _iwCts = null;
            _iwSuppressCompletionDialog = false;
            Dispatcher.Invoke(() =>
            {
                if (IwTxtPackageFolderPath != null) IwTxtPackageFolderPath.Text = string.Empty;
                if (IwTxtSetupFile         != null) IwTxtSetupFile.Text         = string.Empty;
                if (IwTxtOutputFolder      != null) IwTxtOutputFolder.Text      = string.Empty;
                IwResetName();
                if (IwTxtEventLog != null)
                    IwTxtEventLog.Text = "[Ready] Select a Source Folder, Source File and Output Folder, then click Create…";
                IwUpdateStatus();
            });
        }

    }
}
