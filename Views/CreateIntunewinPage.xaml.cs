using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SwiftPack.Views
{
    // ══════════════════════════════════════════════════════════════════════
    // Modern Vista-style folder picker via IFileOpenDialog COM interface.
    // Always opens at the specified folder (or system drive as fallback),
    // never remembers a stale previously-visited location.
    // ══════════════════════════════════════════════════════════════════════
    internal static class ShellFolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"),
         ClassInterface(ClassInterfaceType.None)]
        private class FileOpenDialogRCW { }

        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr hwnd);
            void SetFileTypes(uint c, IntPtr p);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr p, out uint dw);
            void Unadvise(uint dw);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string n);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string n);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, uint fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid g);
            void ClearClientData();
            void SetFilter(IntPtr f);
            void GetResults(out IntPtr e);
            void GetSelectedItems(out IntPtr s);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem psi, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

        private const uint FOS_PICKFOLDERS     = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint SIGDN_FILESYSPATH   = 0x80058000;
        private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        public static string? Browse(IntPtr ownerHwnd, string title,
                                     string initialFolder, bool allowNewFolder = true)
        {
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            string openAt = Directory.Exists(initialFolder) ? initialFolder : systemDrive;

            var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                dlg.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                dlg.SetTitle(title);

                var iid = IID_IShellItem;
                if (SHCreateItemFromParsingName(openAt, IntPtr.Zero, ref iid, out IShellItem si) == 0)
                    dlg.SetFolder(si);

                int hr = dlg.Show(ownerHwnd);
                if (hr != 0) return null;

                dlg.GetResult(out IShellItem result);
                result.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dlg);
            }
        }
    }

    public partial class CreateIntunewinPage : Page
    {
        private const string IntuneWinAppUtilPath =
            @"C:\Program Files\Smekal\SwiftPack Studio\Templates\IntuneWinAppUtil.exe";

        private bool _userEditedName     = false;
        private bool _suppressNameChange = false;
        private CancellationTokenSource? _cts;
        private System.Diagnostics.Process? _activeProc;
        // Script path set by AutoFillFromScript before page was loaded - applied in Loaded event
        private string? _pendingScriptPath;

        /// <summary>
        /// Fired after a .intunewin file is successfully created. Parameter: full path to .intunewin file.
        /// MainWindow subscribes to trigger CreateDocument auto-fill.
        /// </summary>
        public Action<string>? OnIntunewinCreated { get; set; }

        // ── Pause/Resume support ──────────────────────────────────────
        // Set to true while the cancel-confirmation dialog is open.
        // The background Task polls this and suspends/resumes the process.
        private volatile bool _creationPaused = false;

        // Win32: suspend / resume every thread in a process.
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint THREAD_SUSPEND_RESUME = 0x0002;

        private void SuspendProcess(System.Diagnostics.Process proc)
        {
            try
            {
                foreach (ProcessThread thread in proc.Threads)
                {
                    IntPtr hThread = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                    if (hThread != IntPtr.Zero)
                    {
                        SuspendThread(hThread);
                        CloseHandle(hThread);
                    }
                }
            }
            catch { /* ignore - process may have already exited */ }
        }

        private void ResumeProcess(System.Diagnostics.Process proc)
        {
            try
            {
                foreach (ProcessThread thread in proc.Threads)
                {
                    IntPtr hThread = OpenThread(THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                    if (hThread != IntPtr.Zero)
                    {
                        ResumeThread(hThread);
                        CloseHandle(hThread);
                    }
                }
            }
            catch { /* ignore - process may have already exited */ }
        }

        public string? LastIntunewinPath
        {
            get
            {
                string dir  = TxtOutputFolder?.Text.Trim() ?? "";
                string name = TxtIntunewinName?.Text.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name))
                    return null;
                if (!name.EndsWith(".intunewin", StringComparison.OrdinalIgnoreCase))
                    name += ".intunewin";
                return System.IO.Path.Combine(dir, name);
            }
        }

        private IntPtr OwnerHwnd =>
            new System.Windows.Interop.WindowInteropHelper(
                Window.GetWindow(this)).Handle;

        public CreateIntunewinPage()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                // If AutoFillFromScript was called before the page was loaded (controls were null),
                // apply the stored path now.
                if (!string.IsNullOrEmpty(_pendingScriptPath))
                {
                    string ps1 = _pendingScriptPath;
                    _pendingScriptPath = null;
                    ApplyScriptFill(ps1);
                }
                AttachCaretHandlerToAllTextBoxes(this);
                this.PreviewMouseLeftButtonDown += Page_PreviewMouseLeftButtonDown;
            };
        }

        private void Page_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (System.Windows.Input.Keyboard.FocusedElement is not TextBox focused) return;
            if (focused.SelectionLength == 0) return;
            var clickPos = e.GetPosition(focused);
            bool inside = clickPos.X >= 0 && clickPos.X <= focused.ActualWidth &&
                          clickPos.Y >= 0 && clickPos.Y <= focused.ActualHeight;
            if (!inside) focused.SelectionLength = 0;
        }

        private void AttachCaretHandlerToAllTextBoxes(System.Windows.DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox tb)
                {
                    tb.PreviewMouseLeftButtonDown += PathBox_PreviewMouseLeftButtonDown;
                    tb.ContextMenu = BuildTextBoxContextMenu(tb);
                }
                else
                    AttachCaretHandlerToAllTextBoxes(child);
            }
        }

        private void PathBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not TextBox tb || string.IsNullOrEmpty(tb.Text)) return;
            var clickPoint = e.GetPosition(tb);
            int exactIndex = tb.GetCharacterIndexFromPoint(clickPoint, snapToText: false);
            if (exactIndex != -1) return;

            int nearestIndex = tb.GetCharacterIndexFromPoint(clickPoint, snapToText: true);
            bool goToStart   = nearestIndex < tb.Text.Length / 2;

            e.Handled = true;
            tb.CaptureMouse();
            if (!tb.IsFocused) tb.Focus();
            int anchor = goToStart ? 0 : tb.Text.Length;
            tb.Select(anchor, 0);
            if (anchor == 0) tb.ScrollToHome(); else tb.ScrollToEnd();

            System.Windows.Input.MouseEventHandler?       moveHandler = null;
            System.Windows.Input.MouseButtonEventHandler? upHandler   = null;

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

            menu.Items.Add(MakeItem("Cut",        "✂",  "Ctrl+X", () => tb.Cut(),      () => !tb.IsReadOnly && tb.SelectionLength > 0));
            menu.Items.Add(MakeItem("Copy",       "📋", "Ctrl+C", () => tb.Copy(),     () => tb.SelectionLength > 0));
            menu.Items.Add(MakeItem("Paste",      "📄", "Ctrl+V", () => tb.Paste(),    () => !tb.IsReadOnly && System.Windows.Clipboard.ContainsText()));
            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("Select All", "⬛", "Ctrl+A", () => tb.SelectAll(), () => !string.IsNullOrEmpty(tb.Text)));
            menu.Items.Add(MakeItem("Delete",     "🗑",  "Del",
                () => { if (tb.SelectionLength > 0) tb.SelectedText = ""; else if (tb.CaretIndex < tb.Text.Length) tb.Text = tb.Text.Remove(tb.CaretIndex, 1); },
                () => !tb.IsReadOnly && (tb.SelectionLength > 0 || tb.CaretIndex < tb.Text.Length)));
            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("Undo", "↩", "Ctrl+Z", () => tb.Undo(), () => !tb.IsReadOnly && tb.CanUndo));
            menu.Items.Add(MakeItem("Redo", "↪", "Ctrl+Y", () => tb.Redo(), () => !tb.IsReadOnly && tb.CanRedo));
            return menu;
        }

        /// <summary>Enables or disables all Browse buttons, text boxes, and the Clear Log button.</summary>
        private void SetInputControlsEnabled(bool enabled)
        {
            BtnBrowseFolder.IsEnabled      = enabled;
            BtnBrowseSetupFile.IsEnabled   = enabled;
            BtnBrowseOutputFolder.IsEnabled = enabled;
            BtnClearLog.IsEnabled          = enabled;
            BtnRefresh.IsEnabled           = enabled;
            TxtPackageFolderPath.IsReadOnly = !enabled;
            TxtSetupFile.IsReadOnly         = !enabled;
            TxtOutputFolder.IsReadOnly      = !enabled;
            TxtIntunewinName.IsReadOnly     = !enabled;
        }

        // ══════════════════════════════════════════════════════════════
        // NEW - reset the page for a new intunewin
        // ══════════════════════════════════════════════════════════════
        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            // Confirm first: New clears every field, so give the user a way out.
            if (!ShowIntunewinNewDialog()) return;

            // Clear all fields so the user can start a fresh intunewin.
            TxtPackageFolderPath.Text = string.Empty;
            TxtSetupFile.Text         = string.Empty;
            TxtOutputFolder.Text      = string.Empty;
            _userEditedName = false;
            SetIntunewinNameSilently(string.Empty);
            UpdateStatus();
            Log("🆕  Ready for a new intunewin.");
        }

        // ── Called by MainWindow when navigating here from Create Package ──
        // scriptPath = full path to Invoke-AppDeployToolkit.ps1, e.g.:
        //   C:\Temp\WinZip_...\sdasd\Invoke-AppDeployToolkit.ps1
        // Derives:
        //   Source Folder  = directory containing the ps1
        //   Source File    = the ps1 itself
        //   Output Folder  = parent of the ps1 directory
        //   Intunewin Name = filename without extension
        public void AutoFillFromScript(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath)) return;

            // If the page hasn't been loaded into the frame yet, controls are null.
            // Store the path so the Loaded event can apply it once the page is loaded.
            if (TxtPackageFolderPath == null || TxtOutputFolder == null)
            {
                _pendingScriptPath = scriptPath;
                return;
            }

            ApplyScriptFill(scriptPath);
        }

        /// <summary>
        /// Core fill logic - always derives Output Folder as &lt;PackageRoot&gt;\Intune.
        /// </summary>
        private void ApplyScriptFill(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath)) return;

            string ps1Dir  = Path.GetDirectoryName(scriptPath) ?? "";
            string pkgRoot = Path.GetDirectoryName(ps1Dir)     ?? "";
            string baseName = Path.GetFileNameWithoutExtension(scriptPath);

            TxtPackageFolderPath.Text = ps1Dir;
            TxtSetupFile.Text         = scriptPath;

            // Output folder: give each package its own Intune subfolder under the package root.
            // If the package root cannot be determined, fall back to the script's own folder.
            TxtOutputFolder.Text = !string.IsNullOrEmpty(pkgRoot)
                ? Path.Combine(pkgRoot, "Intune")
                : ps1Dir;

            // Intunewin file name default: the PACKAGE NAME (the package-root folder name,
            // e.g. "VideoLAN_VLCmediaplayer_3.0.23_01"), NOT the source-file base name (which
            // would be "Invoke-AppDeployToolkit"). Resolved by walking UP to the package root,
            // because the ps1 does not sit at a fixed depth - see ResolvePackageName. Falls back
            // to the old parent-of-script-folder name, then the source folder name, then the ps1
            // base name, if the package root cannot be determined.
            {
                string pkgName = ResolvePackageName(ps1Dir);
                if (string.IsNullOrWhiteSpace(pkgName) && !string.IsNullOrWhiteSpace(pkgRoot))
                    pkgName = Path.GetFileName(pkgRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(pkgName) && !string.IsNullOrWhiteSpace(ps1Dir))
                    pkgName = Path.GetFileName(ps1Dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(pkgName))
                    pkgName = baseName;

                _userEditedName = true;
                SetIntunewinNameSilently(pkgName);
            }

            UpdateStatus();
        }

        /// <summary>
        /// Returns the package root folder NAME for a script that lives somewhere beneath it:
        ///   C:\Packages\VideoLAN_VLCmediaplayer_3.0.23.0_01443\Output\AppPackages\Test
        ///   returns "VideoLAN_VLCmediaplayer_3.0.23.0_01443"
        ///
        /// Invoke-AppDeployToolkit.ps1 does NOT sit at a fixed depth. Create Package copies the
        /// PSADT template as-is, so the folder holding the script can be any of
        ///   &lt;pkg&gt;\Package\   &lt;pkg&gt;\AppPackages\   &lt;pkg&gt;\Output\AppPackages\Test\
        /// Taking the script folder's parent therefore yielded "AppPackages" for the deepest
        /// layout. Walk UP instead and take the first ancestor that looks like a package name
        /// (two or more underscore / hyphen separated segments). Returns "" when no ancestor
        /// qualifies, so the caller keeps its existing fallbacks.
        ///
        /// Only the NAME is resolved this way. The Output Folder deliberately stays
        /// &lt;parent of the script folder&gt;\Intune - that is where Create Package lays the
        /// Intune folder down, beside the folder holding the script.
        /// </summary>
        private static string ResolvePackageName(string scriptDir)
        {
            if (string.IsNullOrWhiteSpace(scriptDir)) return "";
            var sep = new[] { '_', '-' };
            try
            {
                for (var dir = Directory.GetParent(scriptDir); dir != null; dir = dir.Parent)
                    if (dir.Name.Split(sep, StringSplitOptions.RemoveEmptyEntries).Length >= 2)
                        return dir.Name;
            }
            catch { }
            return "";
        }

        // ══════════════════════════════════════════════════════════════
        // BROWSE - Source Folder
        // Opens at system drive by default (req 2).
        // ══════════════════════════════════════════════════════════════
        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            string? chosen = ShellFolderPicker.Browse(
                OwnerHwnd,
                "Select the Source Folder",
                TxtPackageFolderPath.Text.Trim(),
                allowNewFolder: false);

            if (chosen == null) return;

            TxtPackageFolderPath.Text = chosen;

            // Req 1: warn immediately if the selected folder contains no files at all
            if (!FolderHasFiles(chosen))
            {
                new NoFilesInFolderDialog(chosen) { Owner = Window.GetWindow(this) }.ShowDialog();
                TxtPackageFolderPath.Text = string.Empty;
                TxtSetupFile.Text         = string.Empty;
                ResetIntunewinName();
                UpdateStatus();
                return;
            }

            if (!string.IsNullOrWhiteSpace(TxtSetupFile.Text) &&
                !TxtSetupFile.Text.StartsWith(chosen, StringComparison.OrdinalIgnoreCase))
            {
                TxtSetupFile.Text = string.Empty;
                ResetIntunewinName();
            }

            UpdateStatus();
            Log($"Source folder selected: {chosen}");
        }

        // ══════════════════════════════════════════════════════════════
        // BROWSE - Source File
        // Opens at source folder if set, otherwise system drive.
        // ══════════════════════════════════════════════════════════════
        private void BtnBrowseSetupFile_Click(object sender, RoutedEventArgs e)
        {
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            string initialDir  = TxtPackageFolderPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
                initialDir = systemDrive;

            var dlg = new OpenFileDialog
            {
                Title            = "Select Source File",
                Filter           = "All Supported Files|*.exe;*.msi;*.ps1;*.cmd;*.bat|" +
                                   "Executables (*.exe)|*.exe|" +
                                   "MSI Packages (*.msi)|*.msi|" +
                                   "PowerShell Scripts (*.ps1)|*.ps1|" +
                                   "Scripts (*.cmd;*.bat)|*.cmd;*.bat|" +
                                   "All Files (*.*)|*.*",
                InitialDirectory = initialDir
            };

            if (dlg.ShowDialog() != true) return;

            string chosenFile   = dlg.FileName;
            string sourceFolder = TxtPackageFolderPath.Text.Trim();

            // ── Req 1: Validate that the chosen file lives inside the source folder ──
            if (!string.IsNullOrWhiteSpace(sourceFolder) &&
                Directory.Exists(sourceFolder) &&
                !chosenFile.StartsWith(sourceFolder.TrimEnd('\\') + "\\",
                                       StringComparison.OrdinalIgnoreCase))
            {
                ShowStyledMessage("File Outside Source Folder",
                    $"The selected file is not inside the Source Folder.\n\n" +
                    $"Source Folder : {sourceFolder}\n" +
                    $"Selected File : {chosenFile}\n\n" +
                    "Please select a file that resides inside the Source Folder, " +
                    "or update the Source Folder to match.");
                return;
            }

            TxtSetupFile.Text = chosenFile;

            if (!_userEditedName)
                SetIntunewinNameSilently(
                    Path.GetFileNameWithoutExtension(chosenFile) + ".intunewin");

            UpdateStatus();
            Log($"Source file selected: {chosenFile}");
        }

        // ══════════════════════════════════════════════════════════════
        // BROWSE - Output Folder
        // Opens at system drive by default (req 3).
        // ══════════════════════════════════════════════════════════════
        private void BtnBrowseOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            string? chosen = ShellFolderPicker.Browse(
                OwnerHwnd,
                "Select the Output Folder for the .intunewin file",
                TxtOutputFolder.Text.Trim(),
                allowNewFolder: true);

            if (chosen == null) return;

            TxtOutputFolder.Text = chosen;
            Log($"Output folder set: {chosen}");
        }

        // ══════════════════════════════════════════════════════════════
        // INTUNEWIN FILE NAME - user edit tracking
        // ══════════════════════════════════════════════════════════════
        private void TxtIntunewinName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressNameChange) return;
            if (!string.IsNullOrWhiteSpace(TxtSetupFile.Text))
                _userEditedName = true;
        }

        private void SetIntunewinNameSilently(string value)
        {
            _suppressNameChange   = true;
            TxtIntunewinName.Text = (value ?? string.Empty).Replace("__", "-");
            _suppressNameChange   = false;
        }

        private void ResetIntunewinName()
        {
            _userEditedName = false;
            SetIntunewinNameSilently(string.Empty);
        }

        // Returns true if the folder contains at least one file (non-recursive top-level check)
        private static bool FolderHasFiles(string folderPath)
        {
            try
            {
                return Directory.Exists(folderPath) &&
                       Directory.EnumerateFiles(folderPath).Any();
            }
            catch { return false; }
        }



        // ══════════════════════════════════════════════════════════════
        // STATUS / BUTTON STATE
        // Create is enabled only when all three mandatory fields are set.
        // Req 4: Output Folder is now mandatory.
        // ══════════════════════════════════════════════════════════════
        private void UpdateStatus()
        {
            bool hasFolder = Directory.Exists(TxtPackageFolderPath.Text.Trim());
            bool hasSetup  = File.Exists(TxtSetupFile.Text.Trim());
            bool hasOutput = !string.IsNullOrWhiteSpace(TxtOutputFolder.Text.Trim());

            BtnCreate.IsEnabled = hasFolder && hasSetup && hasOutput;
        }

        // ══════════════════════════════════════════════════════════════
        // CREATE INTUNEWIN
        // ══════════════════════════════════════════════════════════════
        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            string sourceFolder  = TxtPackageFolderPath.Text.Trim();
            string setupFile     = TxtSetupFile.Text.Trim();
            string outputFolder  = TxtOutputFolder.Text.Trim();
            string intunewinName = TxtIntunewinName.Text.Trim();

            // ── Validation ───────────────────────────────────────────
            if (!Directory.Exists(sourceFolder))
            {
                ShowStyledMessage("Invalid Folder", "The selected source folder does not exist.");
                return;
            }

            // Req 1: Source folder must contain at least one file
            if (!FolderHasFiles(sourceFolder))
            {
                new NoFilesInFolderDialog(sourceFolder) { Owner = Window.GetWindow(this) }.ShowDialog();
                return;
            }
            if (!File.Exists(setupFile))
            {
                ShowStyledMessage("Invalid File", "The selected source file does not exist.");
                return;
            }

            // Req 1: Source file must be inside source folder
            if (!setupFile.StartsWith(sourceFolder.TrimEnd('\\') + "\\",
                                      StringComparison.OrdinalIgnoreCase))
            {
                ShowStyledMessage("File Outside Source Folder",
                    $"The source file is not inside the Source Folder.\n\n" +
                    $"Source Folder : {sourceFolder}\n" +
                    $"Source File   : {setupFile}\n\n" +
                    "Please select a file that resides inside the Source Folder, " +
                    "or update the Source Folder to match.");
                return;
            }

            // Req 4: Output folder is mandatory
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                ShowStyledMessage("Output Folder Required", "Output Folder is required. Please browse or type a folder path.");
                return;
            }

            if (!File.Exists(IntuneWinAppUtilPath))
            {
                ShowStyledMessage("Create Intunewin - File not found",
                    $"IntuneWinAppUtil.exe not found at:\n{IntuneWinAppUtilPath}");
                return;
            }

            try
            {
                if (!Directory.Exists(outputFolder))
                {
                    Directory.CreateDirectory(outputFolder);
                    Log($"Created output folder: {outputFolder}");
                }
                else
                {
                    Log($"Output folder already exists: {outputFolder}");
                }
            }
            catch (Exception ex)
            {
                ShowStyledMessage("Folder Error", $"Failed to create output folder:\n{ex.Message}");
                return;
            }

            string setupFileName = Path.GetFileName(setupFile);

            if (string.IsNullOrWhiteSpace(intunewinName))
                intunewinName = Path.GetFileNameWithoutExtension(setupFileName) + ".intunewin";
            if (!intunewinName.EndsWith(".intunewin", StringComparison.OrdinalIgnoreCase))
                intunewinName += ".intunewin";

            // ── Pre-delete existing .intunewin so IntuneWinAppUtil never prompts Y/N ──
            // IntuneWinAppUtil writes <setupFileName-without-ext>.intunewin by default.
            string defaultIntunewinName = Path.GetFileNameWithoutExtension(setupFileName) + ".intunewin";
            string existingFile = Path.Combine(outputFolder, defaultIntunewinName);
            if (File.Exists(existingFile))
            {
                bool overwriteFile = ShowIntunewinFileExistsDialog(existingFile);
                if (!overwriteFile)
                {
                    Log("⚠  Aborted - output file already exists and overwrite was declined.");
                    BtnCreate.IsEnabled       = true;
                    BtnCancelCreate.IsEnabled = false;
                    SetInputControlsEnabled(true);
                    BorderRunning.Visibility  = Visibility.Collapsed;
                    UpdateStatus();
                    return;
                }
                try   { File.Delete(existingFile); Log($"Deleted existing: {existingFile}"); }
                catch (Exception ex) { Log($"⚠  Could not delete existing file: {ex.Message}"); }
            }

            // ── Set up log file in %TEMP% ──────────────────────────────────────────
            // Log name: CreateIntunewin_<AppVendor>_<AppName>_<AppVersion>_<AppRevision>.log
            // Derived from Invoke-AppDeployToolkit.ps1 in the sourceFolder
            string _intuneLogBaseName = "";
            try
            {
                string? ps1File = Directory.GetFiles(
                    sourceFolder, "Invoke-AppDeployToolkit.ps1",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (ps1File != null)
                {
                    string ps1Text = File.ReadAllText(ps1File);
                    string GetPs1Field(string key)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            ps1Text,
                            @"(?m)^\s+" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=\s*'([^']*)'",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        return m.Success ? m.Groups[1].Value.Trim() : "";
                    }
                    string San(string s) {
                        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
                        return s.Replace(" ", "").Trim('_');
                    }
                    var parts = new[]
                    {
                        San(GetPs1Field("AppVendor")),
                        San(GetPs1Field("AppName")),
                        San(GetPs1Field("AppVersion")),
                        San(GetPs1Field("AppRevision"))
                    }.Where(p => !string.IsNullOrEmpty(p)).ToArray();
                    if (parts.Length > 0) _intuneLogBaseName = string.Join("_", parts);
                }
            }
            catch (Exception ex) { Debug.WriteLine("[CreateIntunewin] log base-name parse failed: " + ex.Message); }
            if (string.IsNullOrWhiteSpace(_intuneLogBaseName))
                _intuneLogBaseName = Path.GetFileName(sourceFolder.TrimEnd('\\', '/'));
            string intunewinLogPath = Path.Combine(
                Path.GetTempPath(),
                $"CreateIntunewin_{_intuneLogBaseName}.log");

            // No -q flag: we want the live % progress lines IntuneWinAppUtil writes to stderr
            string args = $"-c \"{sourceFolder}\" -s \"{setupFileName}\" -o \"{outputFolder}\"";

            AppBusy.SetManual("Create Intunewin", true);   // Auto Package waits until this run ends
            BtnCreate.IsEnabled       = false;
            BtnCancelCreate.IsEnabled = true;
            SetInputControlsEnabled(false);
            BorderRunning.Visibility  = Visibility.Visible;
            TxtRunningLabel.Text      = "Creating .intunewin - please wait…";
            PrgCreateProgress.Visibility = Visibility.Visible;

            _creationPaused = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Log("─────────────────────────────────────────────────────");
            Log($"Tool    : {IntuneWinAppUtilPath}");
            Log($"Source  : {sourceFolder}");
            Log($"File    : {setupFileName}");
            Log($"Output  : {outputFolder}");
            Log($"Command : IntuneWinAppUtil.exe {args}");
            Log("─────────────────────────────────────────────────────");
            Log("Starting IntuneWinAppUtil…");

            int exitCode = -1;

            // Line processor: writes to event log AND to the log file in %TEMP%.
            var logWriter = new System.IO.StreamWriter(intunewinLogPath, append: false, System.Text.Encoding.UTF8)
                { AutoFlush = true };
            void ProcessOutputLine(string line, bool isProgressLine)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Log(isProgressLine ? $"  {line.Trim()}" : line);
                    try { logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line.Trim()}"); } catch { }
                }
            }

            var _intuneStartTime = DateTime.Now;
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
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding  = Encoding.UTF8,
                };

                using var proc = new Process { StartInfo = psi };
                _activeProc = proc;
                proc.Start();

                // Register cancellation - kill the process if Cancel is clicked
                using var reg = token.Register(() =>
                {
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                });

                // Read stderr on a dedicated thread using char-by-char so \r-terminated
                // progress lines (e.g. "[==========] 73%") are not stripped by ReadLine.
                var stderrThread = new System.Threading.Thread(() =>
                {
                    var sbErr = new StringBuilder();
                    string lastErrPct = string.Empty;
                    int ech;
                    while ((ech = proc.StandardError.Read()) != -1)
                    {
                        char ec = (char)ech;
                        if (ec == '\r')
                        {
                            string raw = sbErr.ToString().Trim();
                            sbErr.Clear();
                            if (!string.IsNullOrWhiteSpace(raw) && raw != lastErrPct)
                            {
                                lastErrPct = raw;
                                ProcessOutputLine(raw, isProgressLine: true);
                            }
                        }
                        else if (ec == '\n')
                        {
                            string raw = sbErr.ToString().TrimEnd('\r').Trim();
                            sbErr.Clear();
                            if (!string.IsNullOrWhiteSpace(raw))
                                ProcessOutputLine(raw, isProgressLine: false);
                        }
                        else sbErr.Append(ec);
                    }
                    string errTail = sbErr.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(errTail))
                        ProcessOutputLine(errTail, isProgressLine: false);
                });
                stderrThread.IsBackground = true;
                stderrThread.Start();

                var sb = new StringBuilder();
                string lastPct = string.Empty;
                int ch;

                while ((ch = proc.StandardOutput.Read()) != -1)
                {
                    char c = (char)ch;
                    if (c == '\r')
                    {
                        string raw = sb.ToString().Trim();
                        sb.Clear();
                        if (!string.IsNullOrWhiteSpace(raw) && raw != lastPct)
                        {
                            lastPct = raw;
                            ProcessOutputLine(raw, isProgressLine: true);
                        }
                    }
                    else if (c == '\n')
                    {
                        string raw = sb.ToString().TrimEnd('\r').Trim();
                        sb.Clear();
                        if (!string.IsNullOrWhiteSpace(raw))
                            ProcessOutputLine(raw, isProgressLine: false);
                    }
                    else sb.Append(c);
                }

                string tail = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(tail)) ProcessOutputLine(tail, isProgressLine: false);

                stderrThread.Join();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            });

            string defaultName   = Path.GetFileNameWithoutExtension(setupFileName) + ".intunewin";
            string defaultOutput = Path.Combine(outputFolder, defaultName);
            string desiredOutput = Path.Combine(outputFolder, intunewinName);

            if (!intunewinName.Equals(defaultName, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(defaultOutput))
            {
                try
                {
                    if (File.Exists(desiredOutput)) File.Delete(desiredOutput);
                    File.Move(defaultOutput, desiredOutput);
                    Log($"Renamed output to: {intunewinName}");
                }
                catch (Exception ex)
                {
                    Log($"⚠  Could not rename file: {ex.Message}");
                    desiredOutput = defaultOutput;
                }
            }

            try { logWriter.Close(); } catch { }

            bool cancelled = _cts?.IsCancellationRequested == true;
            _cts?.Dispose(); _cts = null;
            _activeProc = null;
            _creationPaused = false;

            AppBusy.SetManual("Create Intunewin", false);
            BorderRunning.Visibility  = Visibility.Collapsed;
            BtnCreate.IsEnabled       = true;
            PrgCreateProgress.Visibility = Visibility.Collapsed;
            BtnCancelCreate.IsEnabled = false;
            SetInputControlsEnabled(true);
            UpdateStatus();

            if (cancelled)
            {
                Log("⚠  Creation cancelled by user.");
                return;
            }

            if (exitCode == 0)
            {
                string finalFile = File.Exists(desiredOutput) ? desiredOutput : defaultOutput;
                string finalName = Path.GetFileName(finalFile);

                Log($"✅ Success! .intunewin created: {finalName}");
                Log($"   Location: {outputFolder}");
                Log("─────────────────────────────────────────────────────");
                Log($"📋 Log: {intunewinLogPath}");

                // Notify MainWindow so CreateDocument can be auto-filled
                OnIntunewinCreated?.Invoke(finalFile);
                var successDlg = new IntunewinSuccessDialog(finalFile, outputFolder, intunewinLogPath, _intuneStartTime)
                {
                    Owner = Window.GetWindow(this)
                };
                successDlg.ShowDialog();
            }
            else
            {
                Log($"❌ IntuneWinAppUtil exited with code {exitCode}.");
                Log("─────────────────────────────────────────────────────");

                ShowStyledMessage("Creation Failed",
                    $"❌  Failed to create .intunewin package.\n\n" +
                    $"IntuneWinAppUtil exited with code {exitCode}.\n\n" +
                    "Check the Event Log for details.");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // TEXTBOX DIRECT EDIT - live validation
        // IsReadOnly=False lets click place the caret exactly where
        // the user clicks (req 1 from previous session).
        // ══════════════════════════════════════════════════════════════
        private void TxtPackageFolderPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateStatus();
        }

        // Shared by all four path textboxes - pushes caret to end on focus or click.
        private void PathBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                tb.CaretIndex = tb.Text.Length;
                tb.ScrollToEnd();
            }
        }


        private void TxtSetupFile_TextChanged(object sender, TextChangedEventArgs e)
        {
            string file = TxtSetupFile.Text.Trim();
            if (!_userEditedName && !string.IsNullOrWhiteSpace(file))
                SetIntunewinNameSilently(
                    Path.GetFileNameWithoutExtension(file) + ".intunewin");
            UpdateStatus();
        }

        private void TxtOutputFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateStatus();
        }

        // ══════════════════════════════════════════════════════════════
        // DRAG & DROP
        // ══════════════════════════════════════════════════════════════
        private void TextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                        ? DragDropEffects.Copy
                        : DragDropEffects.None;
        }

        private void TxtPackageFolderPath_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            string folder  = Directory.Exists(paths[0]) ? paths[0]
                             : Path.GetDirectoryName(paths[0]) ?? paths[0];
            TxtPackageFolderPath.Text = folder;

            // Req 1: warn if dropped folder has no files
            if (!FolderHasFiles(folder))
            {
                new NoFilesInFolderDialog(folder) { Owner = Window.GetWindow(this) }.ShowDialog();
                TxtPackageFolderPath.Text = string.Empty;
                TxtSetupFile.Text         = string.Empty;
                ResetIntunewinName();
                UpdateStatus();
                return;
            }

            if (!string.IsNullOrWhiteSpace(TxtSetupFile.Text) &&
                !TxtSetupFile.Text.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                TxtSetupFile.Text = string.Empty;
                ResetIntunewinName();
            }

            UpdateStatus();
            Log($"Source folder set via drag & drop: {folder}");
        }

        private void TxtSetupFile_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            string file = paths[0];
            if (!File.Exists(file)) return;

            // Req 1 for drag & drop: warn if file is outside source folder
            string sourceFolder = TxtPackageFolderPath.Text.Trim();
            if (!string.IsNullOrWhiteSpace(sourceFolder) &&
                Directory.Exists(sourceFolder) &&
                !file.StartsWith(sourceFolder.TrimEnd('\\') + "\\",
                                 StringComparison.OrdinalIgnoreCase))
            {
                ShowStyledMessage("File Outside Source Folder",
                    $"The dropped file is not inside the Source Folder.\n\n" +
                    $"Source Folder : {sourceFolder}\n" +
                    $"Dropped File  : {file}\n\n" +
                    "Please drop a file that resides inside the Source Folder, " +
                    "or update the Source Folder to match.");
                return;
            }

            TxtSetupFile.Text = file;
            if (!_userEditedName)
                SetIntunewinNameSilently(
                    Path.GetFileNameWithoutExtension(file) + ".intunewin");

            UpdateStatus();
            Log($"Source file set via drag & drop: {file}");
        }

        private void TxtOutputFolder_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            string folder  = Directory.Exists(paths[0]) ? paths[0]
                             : Path.GetDirectoryName(paths[0]) ?? paths[0];
            TxtOutputFolder.Text = folder;
            Log($"Output folder set via drag & drop: {folder}");
        }

        private void TxtIntunewinName_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            string name = (Path.GetFileNameWithoutExtension(paths[0]) + ".intunewin").Replace("__", "-");
            TxtIntunewinName.Text = name;
            Log($"Intunewin name set via drag & drop: {name}");
        }

        // ══════════════════════════════════════════════════════════════
        // CLEAR ALL
        // ══════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════
        // LOG HELPERS
        // ══════════════════════════════════════════════════════════════
        private void Log(string msg, bool timestamp = true)
        {
            string line = timestamp ? $"[{DateTime.Now:HH:mm:ss}] {msg}" : msg;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtEventLog.AppendText("\n" + line);
                TxtEventLog.ScrollToEnd();
                LogScrollViewer.ScrollToEnd();
            }));
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
            => TxtEventLog.Text =
                "[Ready] Select a Source Folder, Source File and Output Folder, then click Create…";

        // ══════════════════════════════════════════════════════════════
        // CANCEL HANDLERS
        // Clicking Cancel:
        //   1. Immediately suspends the IntuneWinAppUtil process.
        //   2. Shows a Yes/No confirmation dialog.
        //      • Yes  → cancels (kills) the process.
        //      • No   → resumes the process and continues creation.
        //      • (no action / dialog stays open) → process stays paused.
        // ══════════════════════════════════════════════════════════════
        private void BtnCancelCreate_Click(object sender, RoutedEventArgs e) => CancelCreation();

        private void CancelCreation()
        {
            // Guard: don't open a second dialog if one is already open.
            if (_creationPaused) return;

            var proc = _activeProc;
            if (proc == null || proc.HasExited) return;

            // 1. Suspend the process immediately so no work is lost/wasted.
            _creationPaused = true;
            SuspendProcess(proc);

            Log("⏸  Creation paused - waiting for user confirmation…");
            TxtRunningLabel.Text = "Paused - Cancel confirmation required…";

            // 2. Show the confirmation dialog (blocks the UI thread modally,
            //    but the background Task is paused at the OS level so it won't
            //    advance while we wait).
            bool cancelConfirmed = ShowIntunewinCancelDialog();

            if (cancelConfirmed)
            {
                // 3a. User confirmed cancellation - kill the process.
                _creationPaused = false;
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                try { _cts?.Cancel(); } catch { }
                BtnCancelCreate.IsEnabled = false;
                Log("🛑  Cancellation confirmed by user.");
            }
            else
            {
                // 3b. User chose to continue - resume the suspended process.
                _creationPaused = false;
                ResumeProcess(proc);
                Log("▶  Creation resumed.");
                TxtRunningLabel.Text = "Creating .intunewin - please wait…";
            }
        }
        // ── Styled File Exists dialog (Auto Package format) ──────────────────────
        private bool ShowIntunewinFileExistsDialog(string filePath)
        {
            bool result = false;
            var win = BuildStyledDialog("Create Intunewin - File Exists");
            var (root, boxBg, boxBdr, lblFg, valFg, sec) = BuildDialogContent(win);

            var details = new System.Windows.Controls.Grid();
            details.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(62) });
            details.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            var lbl1 = MakeLabel("File", lblFg); var box1 = MakeBox(filePath, boxBg, boxBdr, valFg);
            System.Windows.Controls.Grid.SetRow(lbl1, 0); System.Windows.Controls.Grid.SetColumn(lbl1, 0);
            System.Windows.Controls.Grid.SetRow(box1, 0); System.Windows.Controls.Grid.SetColumn(box1, 1);
            details.Children.Add(lbl1); details.Children.Add(box1);

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(8) });

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            var lbl2 = MakeLabel("Action", lblFg); var box2 = MakeBox("File already exists. Do you want to overwrite it?", boxBg, boxBdr, valFg, false);
            System.Windows.Controls.Grid.SetRow(lbl2, 2); System.Windows.Controls.Grid.SetColumn(lbl2, 0);
            System.Windows.Controls.Grid.SetRow(box2, 2); System.Windows.Controls.Grid.SetColumn(box2, 1);
            details.Children.Add(lbl2); details.Children.Add(box2);

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(8) });
            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            var btns = MakeYesNoButtons(sec, () => { result = true; win.Close(); }, () => { result = false; win.Close(); });
            System.Windows.Controls.Grid.SetRow(btns, 4); System.Windows.Controls.Grid.SetColumn(btns, 1);
            details.Children.Add(btns);
            var iconFE = CiDecisionIcon();
            System.Windows.Controls.Grid.SetRow(iconFE, 4); System.Windows.Controls.Grid.SetColumn(iconFE, 0);
            details.Children.Add(iconFE);

            root.Children.Add(details);
            win.ShowDialog();
            return result;
        }

        // ── Styled Cancel Creation dialog (Auto Package format) ───────────────────
        private bool ShowIntunewinCancelDialog()
        {
            bool result = false;
            var win = BuildStyledDialog("Create Intunewin - Cancel Creation");
            var (root, boxBg, boxBdr, lblFg, valFg, sec) = BuildDialogContent(win);

            var details = new System.Windows.Controls.Grid();
            details.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(62) });
            details.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            var lbl1 = MakeLabel("Status", lblFg); var box1 = MakeBox("Intunewin creation is in progress.", boxBg, boxBdr, valFg, false);
            System.Windows.Controls.Grid.SetRow(lbl1, 0); System.Windows.Controls.Grid.SetColumn(lbl1, 0);
            System.Windows.Controls.Grid.SetRow(box1, 0); System.Windows.Controls.Grid.SetColumn(box1, 1);
            details.Children.Add(lbl1); details.Children.Add(box1);

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(8) });

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            var lbl2 = MakeLabel("Action", lblFg); var box2 = MakeBox("Do you want to cancel it?", boxBg, boxBdr, valFg, false);
            System.Windows.Controls.Grid.SetRow(lbl2, 2); System.Windows.Controls.Grid.SetColumn(lbl2, 0);
            System.Windows.Controls.Grid.SetRow(box2, 2); System.Windows.Controls.Grid.SetColumn(box2, 1);
            details.Children.Add(lbl2); details.Children.Add(box2);

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(8) });
            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            var btns = MakeYesNoButtons(sec, () => { result = true; win.Close(); }, () => { result = false; win.Close(); });
            System.Windows.Controls.Grid.SetRow(btns, 4); System.Windows.Controls.Grid.SetColumn(btns, 1);
            details.Children.Add(btns);
            var iconCD = CiDecisionIcon();
            System.Windows.Controls.Grid.SetRow(iconCD, 4); System.Windows.Controls.Grid.SetColumn(iconCD, 0);
            details.Children.Add(iconCD);

            root.Children.Add(details);
            win.ShowDialog();
            return result;
        }

        // ── Styled New confirmation dialog (matches the Publish Package "New" dialog) ──
        private bool ShowIntunewinNewDialog()
        {
            bool result = false;
            var win = BuildStyledDialog("Create Intunewin - New");
            var (root, boxBg, boxBdr, lblFg, valFg, sec) = BuildDialogContent(win);

            var details = new System.Windows.Controls.Grid();
            details.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(62) });
            details.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            var lbl1 = MakeLabel("Status", lblFg); var box1 = MakeBox("This will clear all fields and start a new intunewin. Continue?", boxBg, boxBdr, valFg, false);
            System.Windows.Controls.Grid.SetRow(lbl1, 0); System.Windows.Controls.Grid.SetColumn(lbl1, 0);
            System.Windows.Controls.Grid.SetRow(box1, 0); System.Windows.Controls.Grid.SetColumn(box1, 1);
            details.Children.Add(lbl1); details.Children.Add(box1);

            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(8) });
            details.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            var btns = MakeYesNoButtons(sec, () => { result = true; win.Close(); }, () => { result = false; win.Close(); });
            System.Windows.Controls.Grid.SetRow(btns, 2); System.Windows.Controls.Grid.SetColumn(btns, 1);
            details.Children.Add(btns);
            var iconNW = CiDecisionIcon();
            System.Windows.Controls.Grid.SetRow(iconNW, 2); System.Windows.Controls.Grid.SetColumn(iconNW, 0);
            details.Children.Add(iconNW);

            root.Children.Add(details);
            win.ShowDialog();
            return result;
        }

        // ── Shared dialog builder helpers ────────────────────────────────────────
        // Themed single-OK message dialog, replacing raw MessageBox.Show so warnings/errors
        // match the app's look. Reuses the page's dialog builders.
        private void ShowStyledMessage(string title, string message)
        {
            var win = BuildStyledDialog(title);
            var (root, _, _, _, valFg, sec) = BuildDialogContent(win);

            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            var msg = new System.Windows.Controls.TextBlock { Text = message, FontSize = 12, TextWrapping = System.Windows.TextWrapping.Wrap, Foreground = valFg, Margin = new System.Windows.Thickness(8, 8, 8, 12) };
            System.Windows.Controls.Grid.SetRow(msg, 0); root.Children.Add(msg);

            var actionRow = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(8, 0, 8, 8) };
            actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            actionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            var dIcon = CiDecisionIcon();
            System.Windows.Controls.Grid.SetColumn(dIcon, 0);
            actionRow.Children.Add(dIcon);
            var okBtn = new System.Windows.Controls.Button { Content = "✔ OK", Style = sec, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Height = 28, Padding = new System.Windows.Thickness(16, 0, 16, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, IsDefault = true };
            CiApplyPrimaryLook(okBtn);
            okBtn.Click += (_, _) => win.Close();
            System.Windows.Controls.Grid.SetColumn(okBtn, 1);
            actionRow.Children.Add(okBtn);
            System.Windows.Controls.Grid.SetRow(actionRow, 1); root.Children.Add(actionRow);

            win.ShowDialog();
        }

        private System.Windows.Window BuildStyledDialog(string title)
        {
            var winBg     = Application.Current.TryFindResource("WindowBgBrush")   as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
            var borderClr = Application.Current.TryFindResource("BorderMainBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
            var win = new System.Windows.Window
            {
                Title = title, Width = 460, MaxWidth = 460,
                SizeToContent = System.Windows.SizeToContent.Height,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                Owner = System.Windows.Window.GetWindow(this),
                ResizeMode = System.Windows.ResizeMode.NoResize,
                WindowStyle = System.Windows.WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false
            };
            var outer = new System.Windows.Controls.Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = System.Windows.Media.Colors.Black }
            };
            win.Content = outer;
            return win;
        }

        private (System.Windows.Controls.Grid root,
                 System.Windows.Media.Brush boxBg, System.Windows.Media.Brush boxBdr,
                 System.Windows.Media.Brush lblFg, System.Windows.Media.Brush valFg,
                 System.Windows.Style? sec)
            BuildDialogContent(System.Windows.Window win)
        {
            var borderClr  = Application.Current.TryFindResource("BorderMainBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
            // Dark title bar - same as CreatePackage dialogs (#535663 with white text)
            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            System.Windows.Media.Brush titleTxtBrush = System.Windows.Media.Brushes.White;
            System.Windows.Media.Brush titleSecBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            var txtPri     = Application.Current.TryFindResource("TextPrimaryBrush")   as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black;
            var txtSec     = Application.Current.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

            var outerGrid = new System.Windows.Controls.Grid();
            outerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            outerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            ((System.Windows.Controls.Border)win.Content).Child = outerGrid;

            var titleBar = new System.Windows.Controls.Border { Background = titleBarBg, BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63)), BorderThickness = new System.Windows.Thickness(0,0,0,1), CornerRadius = new System.Windows.CornerRadius(10,10,0,0), Padding = new System.Windows.Thickness(16,6,12,6) };
            titleBar.MouseLeftButtonDown += (_, _) => win.DragMove();
            System.Windows.Controls.Grid.SetRow(titleBar, 0);

            var tg = new System.Windows.Controls.Grid();
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            var tIcon = new System.Windows.Controls.TextBlock { Text = "☁", FontSize = 14, Foreground = titleTxtBrush, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(0,0,8,0) };
            var tText = new System.Windows.Controls.TextBlock { Text = win.Title, FontSize = 13, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = titleTxtBrush, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            var tClose = new System.Windows.Controls.Button { Content = "✕", FontSize = 11, Foreground = titleSecBrush, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new System.Windows.Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = System.Windows.VerticalAlignment.Center, Padding = new System.Windows.Thickness(6,2,6,2) };
            tClose.Click += (_, _) => win.Close();
            System.Windows.Controls.Grid.SetColumn(tIcon, 0); System.Windows.Controls.Grid.SetColumn(tText, 1); System.Windows.Controls.Grid.SetColumn(tClose, 2);
            tg.Children.Add(tIcon); tg.Children.Add(tText); tg.Children.Add(tClose);
            titleBar.Child = tg;
            outerGrid.Children.Add(titleBar);

            var root = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(8) };
            System.Windows.Controls.Grid.SetRow(root, 1);
            outerGrid.Children.Add(root);

            var boxBg  = Application.Current.TryFindResource("InputBgBrush")       as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x5C, 0x7A));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x2E));
            var sec    = Application.Current.TryFindResource("SecondaryButtonStyle") as System.Windows.Style;

            return (root, boxBg, boxBdr, lblFg, valFg, sec);
        }

        private static System.Windows.Controls.TextBlock MakeLabel(string text, System.Windows.Media.Brush fg) =>
            new System.Windows.Controls.TextBlock { Text = text, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Foreground = fg, VerticalAlignment = System.Windows.VerticalAlignment.Center };

        private static System.Windows.Controls.Border MakeBox(string text, System.Windows.Media.Brush bg, System.Windows.Media.Brush bdr, System.Windows.Media.Brush fg, bool mono = true)
        {
            var box = new System.Windows.Controls.Border { Background = bg, BorderBrush = bdr, BorderThickness = new System.Windows.Thickness(1), CornerRadius = new System.Windows.CornerRadius(6), Padding = new System.Windows.Thickness(10, 6, 10, 6) };
            box.Child = new System.Windows.Controls.TextBlock { Text = text, FontSize = 11, TextWrapping = System.Windows.TextWrapping.Wrap, Foreground = fg, FontFamily = mono ? new System.Windows.Media.FontFamily("Consolas") : new System.Windows.Media.FontFamily("Segoe UI") };
            return box;
        }

        private static System.Windows.Controls.Primitives.UniformGrid MakeYesNoButtons(System.Windows.Style? sec, System.Action onYes, System.Action onNo)
        {
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };
            var yes = new System.Windows.Controls.Button { Content = "✔ Yes", Style = sec, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Height = 28, Padding = new System.Windows.Thickness(6,0,6,0), Margin = new System.Windows.Thickness(0,0,4,0) };
            CiApplyPrimaryLook(yes);
            yes.Click += (_, _) => onYes();
            var no  = new System.Windows.Controls.Button { Content = "✕ No",  Style = sec, FontSize = 11, FontWeight = System.Windows.FontWeights.SemiBold, Height = 28, Padding = new System.Windows.Thickness(6,0,6,0), Margin = new System.Windows.Thickness(4,0,0,0), IsDefault = true };
            no.Click  += (_, _) => onNo();
            grid.Children.Add(yes); grid.Children.Add(no);
            return grid;
        }

        // Give a dialog button the highlighted "primary action" look: a filled
        // accent background, rounded corners and a subtle hover. Theme resources
        // keep it readable in both light and dark modes, mirroring the Publish
        // Package dialog. Applied on top of any existing Style (local values win
        // in WPF), so callers keep their own sizing. Internal so the
        // IntunewinSuccessDialog window below can reuse it.
        internal static void CiApplyPrimaryLook(Button b)
        {
            var bg     = Application.Current.TryFindResource("DlgBtnBlueBgBrush")     as System.Windows.Media.Brush ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x9B, 0xF5));
            var fg     = Application.Current.TryFindResource("DlgBtnBlueFgBrush")     as System.Windows.Media.Brush ?? (System.Windows.Media.Brush)System.Windows.Media.Brushes.White;
            var border = Application.Current.TryFindResource("DlgBtnBlueBorderBrush") as System.Windows.Media.Brush ?? bg;

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
        // is used so the blue tint always applies in both themes. Internal so the
        // IntunewinSuccessDialog window below can reuse it.
        internal static TextBlock CiDecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };


    }

    /// <summary>
    /// Success dialog shown after a .intunewin file is created. Built entirely in code (no separate
    /// XAML file) so it lives alongside CreateIntunewinPage. Shows Time / Path / (optional) Log rows
    /// and Open Path / Open Log / Close buttons. Merged from the former IntunewinSuccessDialog.xaml(.cs).
    /// </summary>
    public class IntunewinSuccessDialog : Window
    {
        private readonly string _fullFilePath;
        private readonly string _folderPath;
        private readonly string _logPath;
        public bool OpenFolderRequested { get; private set; }

        public IntunewinSuccessDialog(string fullFilePath, string folderPath, string logPath = "", DateTime startTime = default)
        {
            _fullFilePath = fullFilePath;
            _folderPath   = folderPath;
            _logPath      = logPath;

            // Window chrome
            Title                 = "Create Intunewin - Complete";
            Width                 = 460;
            MaxWidth              = 460;
            SizeToContent         = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode            = ResizeMode.NoResize;
            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            Background            = System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar         = false;
            FontFamily            = new System.Windows.Media.FontFamily("Segoe UI");

            var sec = Application.Current?.TryFindResource("SecondaryButtonStyle") as Style;

            // Root border: themed background + border + drop shadow
            var root = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            root.SetResourceReference(Border.BackgroundProperty, "WindowBgBrush");
            root.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");

            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title bar
            var titleBar = new Border
            {
                Background      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius    = new CornerRadius(10, 10, 0, 0),
                Padding         = new Thickness(16, 6, 12, 6)
            };
            titleBar.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");
            titleBar.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = "☁", FontSize = 14, Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(icon, 0);

            var titleTxt = new TextBlock
            {
                Text = "Create Intunewin - Complete",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleTxt, 1);

            var closeX = new Button
            {
                Content = "✕", FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2)
            };
            closeX.Click += BtnClose_Click;
            Grid.SetColumn(closeX, 2);

            titleGrid.Children.Add(icon);
            titleGrid.Children.Add(titleTxt);
            titleGrid.Children.Add(closeX);
            titleBar.Child = titleGrid;
            Grid.SetRow(titleBar, 0);

            // Content grid: 2 columns (label / value), 7 rows (Auto + 8px spacers)
            var content = new Grid { Margin = new Thickness(8) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 7; i++)
                content.RowDefinitions.Add(new RowDefinition { Height = (i % 2 == 1) ? new GridLength(8) : GridLength.Auto });

            // Local builders mirroring the former XAML styles
            TextBlock MakeLabel(string text)
            {
                var t = new TextBlock
                {
                    Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                return t;
            }
            TextBlock MakeValueText()
            {
                var t = new TextBlock
                {
                    FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas")
                };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                return t;
            }
            Border MakeValueBox(TextBlock inner)
            {
                var b = new Border
                {
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6), Child = inner
                };
                b.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");
                b.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");
                return b;
            }

            // Time row (row 0)
            var timeLbl = MakeLabel("Time");
            Grid.SetRow(timeLbl, 0); Grid.SetColumn(timeLbl, 0);
            var txtTime = MakeValueText();
            var timeBox = MakeValueBox(txtTime);
            Grid.SetRow(timeBox, 0); Grid.SetColumn(timeBox, 1);

            // Path row (row 2) - full intunewin file path
            var pathLbl = MakeLabel("Path");
            Grid.SetRow(pathLbl, 2); Grid.SetColumn(pathLbl, 0);
            var txtPath = MakeValueText();
            var pathBox = MakeValueBox(txtPath);
            Grid.SetRow(pathBox, 2); Grid.SetColumn(pathBox, 1);

            // Log row (row 4) - collapsed unless a log file exists
            var logLbl = MakeLabel("Log");
            logLbl.Visibility = Visibility.Collapsed;
            Grid.SetRow(logLbl, 4); Grid.SetColumn(logLbl, 0);
            var txtLog = MakeValueText();
            var logBox = MakeValueBox(txtLog);
            logBox.Visibility = Visibility.Collapsed;
            Grid.SetRow(logBox, 4); Grid.SetColumn(logBox, 1);

            // Buttons row (row 6)
            var check = CreateIntunewinPage.CiDecisionIcon();
            Grid.SetRow(check, 6); Grid.SetColumn(check, 0);

            var btnGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 3 };
            var btnOpenPath = new Button
            {
                Content = "📁 Open Path", FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28,
                Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 0), Style = sec
            };
            CreateIntunewinPage.CiApplyPrimaryLook(btnOpenPath);
            btnOpenPath.Click += BtnOpenFolder_Click;
            var btnOpenLog = new Button
            {
                Content = "📄 Open Log", FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28,
                Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 4, 0), Style = sec,
                Visibility = Visibility.Collapsed
            };
            btnOpenLog.Click += BtnOpenLog_Click;
            var btnClose = new Button
            {
                Content = "✕ Close", FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28,
                Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), Style = sec,
                IsDefault = true
            };
            btnClose.Click += BtnClose_Click;
            btnGrid.Children.Add(btnOpenPath);
            btnGrid.Children.Add(btnOpenLog);
            btnGrid.Children.Add(btnClose);
            Grid.SetRow(btnGrid, 6); Grid.SetColumn(btnGrid, 1);

            content.Children.Add(timeLbl); content.Children.Add(timeBox);
            content.Children.Add(pathLbl); content.Children.Add(pathBox);
            content.Children.Add(logLbl);  content.Children.Add(logBox);
            content.Children.Add(check);   content.Children.Add(btnGrid);
            Grid.SetRow(content, 1);

            outerGrid.Children.Add(titleBar);
            outerGrid.Children.Add(content);
            root.Child = outerGrid;
            Content = root;

            // Populate values
            var endTime = DateTime.Now;
            var elapsed = endTime - (startTime == default ? endTime : startTime);
            string elapsedStr = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                : elapsed.TotalSeconds >= 1
                    ? $"{(int)elapsed.TotalSeconds}s"
                    : $"{(int)elapsed.TotalMilliseconds}ms";
            txtTime.Text = $"Started: {(startTime == default ? endTime : startTime):HH:mm:ss}  Finished: {endTime:HH:mm:ss}  Duration: {elapsedStr}";

            txtPath.Text = fullFilePath;

            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                txtLog.Text           = logPath;
                logBox.Visibility     = Visibility.Visible;
                logLbl.Visibility     = Visibility.Visible;
                btnOpenLog.Visibility = Visibility.Visible;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderRequested = false;
            DialogResult = false;
        }

        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine("[CreateIntunewin] open log failed: " + ex.Message); }
            // Window stays open - only Close button dismisses the dialog
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folderPath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine("[CreateIntunewin] open folder failed: " + ex.Message); }
            // Window stays open - only Close button dismisses the dialog
        }
    }
}
