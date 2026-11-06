using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace SwiftPack.Views
{
    // ── QaSnapshot model (mirrors EtwQaCapture.cs schema) ──────────────
    // Used to deserialize .svr JSON files for registry verification
    public class QaSnapshot
    {
        public string SnapVersion  { get; set; } = "";
        public string FileName      { get; set; } = "";
        public string FilePath      { get; set; } = "";
        public string Arguments    { get; set; } = "";
        public string CaptureTime  { get; set; } = "";
        public List<string>     Files        { get; set; } = new();
        public List<string>     Folders      { get; set; } = new();
        public List<string>     Shortcuts    { get; set; } = new();
        public List<QaRegEntry> Registry     { get; set; } = new();
        public List<QaEnvVar>   EnvVars      { get; set; } = new();
        public List<QaService>  Services     { get; set; } = new();
        public List<string>     Certificates { get; set; } = new();
        public List<string>     Firewall     { get; set; } = new();
        // Items the user UNCHECKED in Validate Source. Format: "Category|DisplayPath|Value".
        // These must be skipped during validation - the user deliberately excluded them, so they
        // are not part of the package and must not be reported as failures.
        public List<string>     ExcludedPaths { get; set; } = new();
        // Desktop shortcuts captured from the source BEFORE exclusion pruning. Kept separately so
        // the "Desktop shortcuts removed" check can still flag a desktop shortcut the user excluded
        // in Validate Source: exclusion suppresses must-exist checks, but a shortcut that is meant
        // to be REMOVED must be caught even when excluded. Not part of the .svr on disk.
        [JsonIgnore]
        public List<string>     AllDesktopShortcuts { get; set; } = new();
    }
    public class QaEnvVar
    {
        public string Name      { get; set; } = "";
        public string Value     { get; set; } = "";
        public string Delimiter { get; set; } = "";
    }
    public class QaRegEntry
    {
        public string Key   { get; set; } = "";
        public string Value { get; set; } = "";
        public string Data  { get; set; } = "";
        public string Type  { get; set; } = "REG_SZ";
    }
    public class QaService
    {
        public string Name        { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Binary      { get; set; } = "";
        public string StartType   { get; set; } = "";
    }

    public partial class ValidatePackagePage : Page
    {
        // ── Result model ─────────────────────────────────────────────────
        private enum QAStatus { Pass, Fail, Skip, Ignore }
        private enum RowHighlight { None, Amber, Red }
        private record QAResult(int No, string CheckType, string Detail, QAStatus Status, string? Note = null, RowHighlight Highlight = RowHighlight.None, string? OpenPath = null);

        // ── State ─────────────────────────────────────────────────────────
        private string _packageFolder = "";
        // The folder that actually CONTAINS Invoke-AppDeployToolkit.ps1, captured during the
        // last Verify run. Used to resolve the sibling Documents folder (GetParent(scriptDir)).
        private string _resolvedScriptDir = "";
        // True only while an auto-package chain is programmatically triggering Verify (AutoRunQA).
        // A real user click leaves it false, telling BtnRunQA_Click to clear any stale per-run
        // xlsx/html overrides so the manual Verify honors ValidatePackage.json.
        private bool _autoTriggeredQA = false;

        /// <summary>Set from the auto-flow (Source Download → Auto Package → Execute Package)
        /// so AutoExportIfConfigured knows where to copy QA reports.</summary>
        public static string PendingPackageFolder { get; set; } = "";
        private readonly List<QAResult> _results = new();
        // Cache of built row Borders - built once, filtered by Visibility (fast)
        private readonly List<(UIElement Row, QAResult Result)> _rowCache = new();

        // Rows currently ON SCREEN, i.e. the ones the active filter admits. This is the ListBox's
        // ItemsSource.
        //
        // The filter used to work by setting row.Visibility = Collapsed and leaving every row in the
        // panel. That cannot work with a virtualizing panel: a collapsed row still occupies an item
        // slot and measures 0 high, which wrecks the extent estimate VirtualizingStackPanel uses to
        // size the scrollbar. Membership is the right lever - a row the filter excludes simply is
        // not an item. It is also much faster: no layout pass over the excluded rows at all.
        //
        // ObservableCollection so a single row can be swapped in place (the Ignore toggle rebuilds
        // one row) without rebuilding the whole list.
        private System.Collections.ObjectModel.ObservableCollection<UIElement> _visibleRows = new();
        // Maps result index → current effective status (for Ignore overrides)
        private readonly Dictionary<int, QAStatus> _statusOverrides = new();
        // Currently selected (left-clicked) result row
        private Border? _selectedRowBorder = null;
        // Path of the last auto-written HTML report (updated when Ignore toggled)
        private string? _liveHtmlPath = null;
        // Final user-visible report locations from the last run (the <PackageFolder>\Documents
        // copies when copying is enabled, otherwise the export-folder files). Save overwrites these.
        private string? _savedHtmlPath = null;
        private string? _savedCsvPath  = null;
        // File the user imported from - Save overwrites this file (and its sibling) in place
        private string? _importSourcePath = null;
        private enum FilterMode { All, PassOnly, FailOnly, SkipOnly, IgnoreOnly }
        private FilterMode _filterMode = FilterMode.All;
        // Search state
        private string _qaSearchText = "";
        private System.Windows.Threading.DispatcherTimer? _searchDebounce = null;
        private List<int> _qaSearchMatches = new(); // indices into _results (== _rowCache order)
        private int _qaSearchIdx = -1;
        // Search control references (resolved after InitializeComponent via FindName)
        private TextBox?    _txtQASearch      => FindName("TxtQASearch")      as TextBox;
        private TextBlock?  _txtQASearchCount => FindName("TxtQASearchCount") as TextBlock;
        // Cached QA config (for QAsnapPath access in check helpers)
        private QAConfig? _qaConfig = null;
        // Set true while PrefillSvrFile programmatically fills the SVR path, so its TextChanged
        // handler does nothing (the auto-populate must never trigger any QA action).
        private bool _suppressQAsnapChange = false;
        // App identity for the current results - embedded in exported reports and restored on import
        private string _appName    = "-";
        private string _appVendor  = "-";
        private string _appVersion = "-";
        // AppRevision from the deploy script (e.g. "011"). Used only in the report file name so it
        // matches the PSADT log / Create Document naming: <Vendor>_<App>_<Version>_<Revision>_...
        private string _appRevision = "-";
        // Deployment type of last QA run (true = uninstall, false = install)
        private bool _isUninstallRun = false;

        // Runtime copy flag overrides - set by auto-package flow from app JSON (public so MainWindow can access)
        public static string? RuntimeCopyCSVOverride  { get; set; } = null;
        public static string? RuntimeCopyHtmlOverride { get; set; } = null;

        // ── QA Snapshot - loaded from .svr file for registry checks ────
        // Populated in RunAllChecks; used by CheckStartProcess and registry helpers
        private QaSnapshot? _qaSnap = null;

        // Full path of the .svr that _qaSnap was loaded from (needed to dedupe - see _snapsValidated).
        private string? _qaSnapPath = null;

        // .svr files whose contents have already been validated in THIS QA run, by full path.
        //
        // FindQAsnapFile returns the explicitly chosen SVR for EVERY baseName it is asked about
        // (SelectedSvrFile short-circuits the name matching). So in a package that installs two apps
        // - two install commands, e.g. Start-ADTMsiProcess for one and Start-ADTProcess for the
        // other - BOTH commands resolve to the same .svr and each one validated its entire contents.
        // Every File / Folder / Registry / Shortcut row was therefore emitted twice.
        //
        // A merged .svr has no per-source attribution anyway (it keeps only the last capture's
        // FileName), so there is no way to say which item belongs to which command. Validate each
        // distinct snapshot exactly once. The per-command checks - exit code, log, ARP flags, MSI
        // properties - still run for every command, because those come from the MSI/EXE itself and
        // are genuinely different per app.
        private readonly HashSet<string> _snapsValidated = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True the first time a given .svr path is seen in this run; false on every later command
        /// that resolves to the same file. A null/unknown path always returns true (nothing to
        /// dedupe against, so behave as before).
        /// </summary>
        private bool ClaimSnapshot(string? snapPath) =>
            string.IsNullOrWhiteSpace(snapPath) || _snapsValidated.Add(snapPath!);

        // ── Tracks which .svr paths have already been iterated in the current uninstall run ──
        // Prevents the same snapshot from being processed multiple times when several
        // Start-ADTProcess uninstall commands resolve to the same .svr file, which
        // would otherwise double (or triple) the total check count.
        private readonly HashSet<string> _usedUninstallSnapPaths =
            new(StringComparer.OrdinalIgnoreCase);

        // ── QA.json path (alongside the exe or in Templates folder) ──────
        private static string QAJsonPath =>
            @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidatePackage.json";

        public ValidatePackagePage()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                AttachCaretHandlerToAllTextBoxes(this);
                this.PreviewMouseLeftButtonDown -= Page_PreviewMouseLeftButtonDown;
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
                    tb.PreviewMouseLeftButtonDown -= PathBox_PreviewMouseLeftButtonDown;
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

            menu.Items.Add(MakeItem("Cut",        "✂",  "Ctrl+X", () => tb.Cut(),       () => !tb.IsReadOnly && tb.SelectionLength > 0));
            menu.Items.Add(MakeItem("Copy",       "📋", "Ctrl+C", () => tb.Copy(),      () => tb.SelectionLength > 0));
            menu.Items.Add(MakeItem("Paste",      "📄", "Ctrl+V", () => tb.Paste(),     () => !tb.IsReadOnly && System.Windows.Clipboard.ContainsText()));
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

        /// <summary>
        /// Called externally (e.g. from ExecutePackagePage after a successful install/uninstall)
        /// to pre-populate the log path and automatically run QA.
        /// </summary>
        public void SetLogPath(string logPath)
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return;
            Dispatcher.Invoke(() =>
            {
                TxtLogPath.Text = logPath;
                // Auto-fill the matched .svr FILE too, but do NOT run Verify. Only the auto-trigger
                // is removed; the auto-fill stays. The user reviews both paths and clicks Verify
                // (and can Browse a different .svr if the match is wrong).
                try { PrefillSvrFile(logPath); } catch { }
            });
        }

        // Resolves the best-matching .svr FILE for the current package and shows it in the SVR
        // Path field (as a file), without running any checks. Best effort - leaves the field
        // as-is when nothing matches, so the user can Browse the correct .svr.
        private void PrefillSvrFile(string logPath)
        {
            string pkg = !string.IsNullOrWhiteSpace(_packageFolder)
                ? _packageFolder
                : (PendingPackageFolder ?? "").Trim();
            // Manual Create -> Execute doesn't set the package folder, so fall back to the folder
            // recorded inside the log - exactly how the verify run resolves it.
            if (string.IsNullOrWhiteSpace(pkg) && !string.IsNullOrEmpty(logPath) && File.Exists(logPath))
            {
                try
                {
                    var adtLog = ParseAdtLog(logPath);
                    if (!string.IsNullOrWhiteSpace(adtLog?.PackageFolder))
                        pkg = adtLog!.PackageFolder!.Trim();
                }
                catch { }
            }
            string field = TxtQAsnapPath?.Text.Trim() ?? "";

            // Point FindQAsnapFile at the right folder to scan: an existing folder in the field,
            // else QA.json's QAsnapPath, else the default. Set as a folder so SelectedSvrFile()
            // stays null while we scan (otherwise a stale .svr in the field would short-circuit).
            var qa = LoadQAConfig(pkg);
            string searchDir =
                (!string.IsNullOrWhiteSpace(field) && Directory.Exists(field)) ? field
              : (!string.IsNullOrWhiteSpace(qa?.QAsnapPath))                    ? qa!.QAsnapPath!.Trim()
              :                                                                    DefaultQAsnapDir;
            if (_qaConfig == null) _qaConfig = new QAConfig();
            _qaConfig.QAsnapPath = searchDir;

            // Candidate base names to match a .svr by (FindQAsnapFile is fuzzy). Covers EXE
            // installs, MSI packages, and the AppName from the deploy script.
            var candidates = new List<string>();
            try
            {
                string? script = FindInvokeScript(pkg);
                if (script != null && File.Exists(script))
                {
                    var exe = EtwQaCapture.DetectExeInstall(script);
                    if (exe.HasValue && !string.IsNullOrWhiteSpace(exe.Value.FileName))
                        candidates.Add(IOPath.GetFileNameWithoutExtension(exe.Value.FileName));
                    string content = File.ReadAllText(script);
                    string an = ExtractSessionParam(content, "AppName");
                    if (!string.IsNullOrWhiteSpace(an)) candidates.Add(an);
                }
                if (!string.IsNullOrWhiteSpace(pkg) && Directory.Exists(pkg))
                    foreach (var msi in Directory.EnumerateFiles(pkg, "*.msi", SearchOption.AllDirectories))
                    { candidates.Add(IOPath.GetFileNameWithoutExtension(msi)); break; }

                // Fallback from the log filename, e.g. "VideoLAN_VLCmediaplayer_3.0.23_0144_PSApp..."
                // → a revision-stripped base "VideoLAN_VLCmediaplayer_3.0.23" (which matches the .svr
                // name exactly), then the full base as a last resort.
                if (!string.IsNullOrEmpty(logPath))
                {
                    string logBase = IOPath.GetFileNameWithoutExtension(logPath);
                    if (!string.IsNullOrWhiteSpace(logBase))
                    {
                        string stripped = Regex.Replace(logBase, @"_\d+_.*$", "");
                        if (stripped.Length >= 3 && !string.Equals(stripped, logBase, StringComparison.Ordinal))
                            candidates.Add(stripped);
                        candidates.Add(logBase);
                    }
                }
            }
            catch { }

            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                string? svr = FindQAsnapFile(c);
                if (svr != null && TxtQAsnapPath != null)
                {
                    // Fill the field silently so this auto-populate can never trigger a
                    // TextChanged-driven action; sync _qaConfig ourselves.
                    _suppressQAsnapChange = true;
                    TxtQAsnapPath.Text = svr;
                    _suppressQAsnapChange = false;
                    if (_qaConfig == null) _qaConfig = new QAConfig();
                    _qaConfig.QAsnapPath = svr;
                    return;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  BROWSE / DRAG-DROP
        // ══════════════════════════════════════════════════════════════════
        private void BtnBrowsePackage_Click(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title    = "Select Invoke-AppDeployToolkit.ps1",
                Filter   = "Invoke-AppDeployToolkit|Invoke-AppDeployToolkit.ps1|PowerShell scripts|*.ps1",
                FileName = "Invoke-AppDeployToolkit.ps1",
            };
            if (dlg.ShowDialog() == true)
                SetPackageFolder(dlg.FileName);
        }

        private void BtnClearLogPath_Click(object s, RoutedEventArgs e)
        {
            if (TxtLogPath != null) TxtLogPath.Text = "";
        }

        private void BtnClearSearch_Click(object s, RoutedEventArgs e)
        {
            if (_txtQASearch != null) _txtQASearch.Text = "";
        }

        private void BtnBrowseLog_Click(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Install/Uninstall Log File",
                Filter = "Log files|*.log",
            };

            // Pre-navigate to the LogPath from config.psd1 if available
            string initialDir = GetLogPathFromConfig();
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                dlg.InitialDirectory = initialDir;

            if (dlg.ShowDialog() == true)
            {
                // Set the log path only. Do NOT auto-verify and do NOT auto-fill the SVR path -
                // the user picks the .svr file and clicks Verify.
                TxtLogPath.Text = dlg.FileName;
            }
        }

        // ── SVR file controls ─────────────────────────────────────────────
        private void BtnBrowseQAsnapPath_Click(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select SVR file",
                Filter = "SVR snapshot|*.svr|All files|*.*",
            };
            string current = TxtQAsnapPath?.Text.Trim() ?? "";
            string? initDir = File.Exists(current)      ? IOPath.GetDirectoryName(current)
                            : Directory.Exists(current) ? current
                            : null;
            if (!string.IsNullOrEmpty(initDir)) dlg.InitialDirectory = initDir;

            if (dlg.ShowDialog() == true)
                TxtQAsnapPath.Text = dlg.FileName;
        }

        private void BtnClearQAsnapPath_Click(object s, RoutedEventArgs e)
        {
            if (TxtQAsnapPath != null) TxtQAsnapPath.Clear();
        }

        private void TxtQAsnapPath_TextChanged(object s, TextChangedEventArgs e)
        {
            // Programmatic prefill syncs _qaConfig itself; skip all side effects here.
            if (_suppressQAsnapChange) return;
            // Keep _qaConfig in sync so verification picks up the runtime-entered path
            if (_qaConfig == null) _qaConfig = new QAConfig();
            _qaConfig.QAsnapPath = TxtQAsnapPath?.Text.Trim() ?? "";
        }

        /// <summary>
        /// Searches for config.psd1 relative to any known package folder or working dir,
        /// extracts LogPath = '...' and returns that directory.
        /// </summary>
        private string GetLogPathFromConfig()
        {
            // Try current _packageFolder first, then look alongside any existing log path
            var searchRoots = new List<string>();
            if (!string.IsNullOrEmpty(_packageFolder) && Directory.Exists(_packageFolder))
                searchRoots.Add(_packageFolder);
            string existingLog = TxtLogPath?.Text.Trim() ?? "";
            if (!string.IsNullOrEmpty(existingLog))
            {
                string logDir = IOPath.GetDirectoryName(existingLog) ?? "";
                if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
                    searchRoots.Add(logDir);
            }

            foreach (var root in searchRoots)
            {
                try
                {
                    var configs = Directory.GetFiles(root, "config.psd1", SearchOption.AllDirectories);
                    foreach (var cfg in configs)
                    {
                        string text = File.ReadAllText(cfg);
                        var m = Regex.Match(text, @"LogPath\s*=\s*'([^']*)'", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            string logPath = Environment.ExpandEnvironmentVariables(m.Groups[1].Value.Trim());
                            if (Directory.Exists(logPath)) return logPath;
                            // If path doesn't exist yet, return its parent if that exists
                            string parent = IOPath.GetDirectoryName(logPath) ?? "";
                            if (Directory.Exists(parent)) return parent;
                        }
                    }
                }
                catch { }
            }
            return "";
        }

        /// <summary>Triggers QA run on the UI thread if a valid log path is set.</summary>
        private void AutoRunQA()
        {
            string logPath = TxtLogPath?.Text.Trim() ?? "";
            if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
            {
                _autoTriggeredQA = true; // chain-triggered: keep the chain's xlsx/html overrides
                // Dispatch asynchronously so the UI thread is free to render before QA starts
                Dispatcher.BeginInvoke(new Action(() => BtnRunQA_Click(this, new RoutedEventArgs())));
            }
        }

        private void BtnClearAll_Click(object s, RoutedEventArgs e)
        {
            TxtPackageFolder.Clear();
            TxtLogPath.Clear();
            _packageFolder = "";
            TxtQAStatus.Text = "";
            _filterMode = FilterMode.All;
            UpdateFilterButtonStyles();
            ClearResults();
            InfoAppName.Text   = "-";
            InfoVersion.Text   = "-";
            InfoPublisher.Text = "-";
            InfoScript.Text    = "-";
            InfoLog.Text       = "-";
            _appName = _appVendor = _appVersion = _appRevision = "-";
        }

        private void BtnShowAll_Click(object s, RoutedEventArgs e)
        {
            _filterMode = FilterMode.All;
            UpdateFilterButtonStyles();
            RenderResults();
        }

        private void BtnShowPass_Click(object s, RoutedEventArgs e)
        {
            _filterMode = FilterMode.PassOnly;
            UpdateFilterButtonStyles();
            RenderResults();
        }

        private void BtnShowFail_Click(object s, RoutedEventArgs e)
        {
            _filterMode = FilterMode.FailOnly;
            UpdateFilterButtonStyles();
            RenderResults();
        }

        private void BtnShowSkip_Click(object s, RoutedEventArgs e)
        {
            _filterMode = FilterMode.SkipOnly;
            UpdateFilterButtonStyles();
            RenderResults();
        }

        // Summary box clicks - toggle: clicking the active filter again resets to All
        private void BdrAllBox_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            _filterMode = FilterMode.All;
            UpdateFilterButtonStyles();
            RenderResults();
        }

        private void BdrPassBox_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            _filterMode = _filterMode == FilterMode.PassOnly ? FilterMode.All : FilterMode.PassOnly;
            UpdateFilterButtonStyles();
            RenderResults();
        }
        private void BdrFailBox_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            _filterMode = _filterMode == FilterMode.FailOnly ? FilterMode.All : FilterMode.FailOnly;
            UpdateFilterButtonStyles();
            RenderResults();
        }
        private void BdrSkipBox_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            _filterMode = _filterMode == FilterMode.SkipOnly ? FilterMode.All : FilterMode.SkipOnly;
            UpdateFilterButtonStyles();
            RenderResults();
        }
        private void BdrIgnoreBox_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            _filterMode = _filterMode == FilterMode.IgnoreOnly ? FilterMode.All : FilterMode.IgnoreOnly;
            UpdateFilterButtonStyles();
            RenderResults();
        }

        private void BtnShowIgnoreSummary_Click(object s, RoutedEventArgs e)
        {
            _filterMode = FilterMode.IgnoreOnly;
            UpdateFilterButtonStyles();
            RenderResults();
        }

        private void BtnClearQA_Click(object s, RoutedEventArgs e)
        {
            // Confirm first: New clears all fields and results, so give the user a way out.
            if (!QaShowConfirmDialog("Package Validation - New",
                    "This will clear all fields and results and start a new validation. Continue?"))
                return;

            _results.Clear();
            _rowCache.Clear();
            _statusOverrides.Clear();
            _selectedRowBorder = null;
            _liveHtmlPath      = null;
            _savedHtmlPath = _savedCsvPath = null;
            _importSourcePath = null;
            _filterMode        = FilterMode.All;
            _qaSearchText      = "";
            _qaSearchMatches.Clear();
            _visibleRows.Clear();   // ItemsSource is bound - clear the source, not Items
            CommandNavPanel.Children.Clear();
            if (FindName("TxtAllCount") is System.Windows.Controls.TextBlock allReset0) allReset0.Text = "-";
            TxtPassCount.Text   = "-";
            TxtFailCount.Text   = "-";
            TxtSkipCount.Text   = "-";
            if (FindName("TxtIgnoreCount") is System.Windows.Controls.TextBlock ign)
                ign.Text = "-";
            TxtQAStatus.Text    = "";
            TxtQASearch.Clear();
            TxtLogPath.Clear();
            TxtPackageFolder.Clear();
            if (TxtQAsnapPath != null) TxtQAsnapPath.Clear();   // SVR field stays empty on New
            InfoAppName.Text    = "-";
            InfoPublisher.Text  = "-";
            InfoVersion.Text    = "-";
            _appName = _appVendor = _appVersion = _appRevision = "-";
            UpdateFilterButtonStyles();
            UpdateSummaryButtonStyles();
        }

        private void BtnCopyRow_Click(object s, RoutedEventArgs e)
        {
            if (_selectedRowBorder == null) return;
            try
            {
                var (r, _) = (ValueTuple<QAResult, bool>)_selectedRowBorder.Tag;
                var es     = EffectiveStatus(r);
                string status = es == QAStatus.Pass ? "Pass" : es == QAStatus.Fail ? "Fail"
                              : es == QAStatus.Ignore ? "Ignore" : "Skip";
                string note   = string.IsNullOrEmpty(r.Note) ? "" : $"  ({r.Note})";
                System.Windows.Clipboard.SetText(
                    $"{r.No}\t{r.CheckType}\t{r.Detail}{note}\t{status}");
            }
            catch { }
        }

        private void UpdateSummaryButtonStyles()
        {
            void ApplyBox(string name, FilterMode mode)
            {
                if (FindName(name) is Border b)
                {
                    b.BorderThickness = _filterMode == mode ? new Thickness(2.5) : new Thickness(1);
                    b.Opacity = _filterMode == mode ? 1.0 : 0.7;
                }
            }
            ApplyBox("BdrAllBox",    FilterMode.All);
            ApplyBox("BdrPassBox",   FilterMode.PassOnly);
            ApplyBox("BdrFailBox",   FilterMode.FailOnly);
            ApplyBox("BdrSkipBox",   FilterMode.SkipOnly);
            ApplyBox("BdrIgnoreBox", FilterMode.IgnoreOnly);
        }

        private void UpdateFilterButtonStyles()
        {
            // Highlight the active filter button with a pressed/bold border
            var activeStyle   = (Style)FindResource("PrimaryButtonStyle");
            var inactiveStyle = (Style)FindResource("SecondaryButtonStyle");
            BtnShowPass.Style = _filterMode == FilterMode.PassOnly   ? activeStyle : inactiveStyle;
            BtnShowFail.Style = _filterMode == FilterMode.FailOnly   ? activeStyle : inactiveStyle;


            // Summary panel buttons (may be null before first render)
            UpdateSummaryButtonStyles();
        }

        private void TxtLogPath_DragOver(object s, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        private void TxtLogPath_Drop(object s, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                // Set the log path only; the user picks the .svr and clicks Verify.
                TxtLogPath.Text = files[0];
            }
        }

        /// <summary>Page-wide drag-over: accept .log files dropped anywhere on the page.</summary>
        private void Page_DragOver(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                e.Effects = (files != null && files.Length > 0)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        /// <summary>Page-wide drop: load the dropped .log file just like Browse/textbox drop.</summary>
        private void Page_Drop(object s, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                // Set the log path only; the user picks the .svr and clicks Verify.
                TxtLogPath.Text = files[0];
            }
        }
        private void TxtPackageFolder_DragOver(object s, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        private void TxtPackageFolder_Drop(object s, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                SetPackageFolder(Directory.Exists(files[0]) ? files[0] : IOPath.GetDirectoryName(files[0])!);
        }

        private void SetPackageFolder(string path)
        {
            // Accept either the .ps1 file itself or a folder path
            string folder = File.Exists(path)
                ? IOPath.GetDirectoryName(path)!
                : path;
            _packageFolder = folder;
            TxtPackageFolder.Text = path; // show what the user picked
            TxtQAStatus.Text = FindInvokeScript(folder) != null
                ? "✔ Invoke-AppDeployToolkit.ps1 found"
                : "⚠ Invoke-AppDeployToolkit.ps1 not found";
        }

        // ══════════════════════════════════════════════════════════════════
        //  RUN QA
        // ══════════════════════════════════════════════════════════════════
        private async void BtnRunQA_Click(object s, RoutedEventArgs e)
        {
            // A manual Verify (real button click) must honor ValidatePackage.json, not stale
            // per-run overrides left by a prior auto-package chain. Chain-triggered Verify
            // (AutoRunQA) sets _autoTriggeredQA and keeps the override the chain applied.
            bool autoTriggeredQA = _autoTriggeredQA;
            _autoTriggeredQA = false;
            if (!autoTriggeredQA)
            {
                RuntimeCopyCSVOverride  = null;
                RuntimeCopyHtmlOverride = null;
            }

            string manualLogPath = TxtLogPath?.Text.Trim() ?? "";

            // ps1 field is optional - if provided, use it; otherwise derive folder from log
            string rawInput = TxtPackageFolder.Text.Trim();
            if (!string.IsNullOrEmpty(rawInput))
            {
                if (File.Exists(rawInput) && rawInput.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                    _packageFolder = IOPath.GetDirectoryName(rawInput)!;
                else if (Directory.Exists(rawInput))
                    _packageFolder = rawInput;
            }

            if (string.IsNullOrEmpty(manualLogPath) || !File.Exists(manualLogPath))
            {
                ShowQaValidationDialog(
                    "Package Validation - Validation",
                    string.IsNullOrWhiteSpace(manualLogPath)
                        ? "Log file is required. Please browse or drag-and-drop a valid PSADT .log file."
                        : $"Log file not found:\n{manualLogPath}\n\nPlease browse to a valid .log file.",
                    "⚠️");
                return;
            }

            BtnRunQA.IsEnabled = false;
            SetQAControlsEnabled(false);
            TxtQAStatus.Text = "";
            _filterMode = FilterMode.All;
            _packageFolder = ""; // reset so it's re-derived from new log
            _resolvedScriptDir = ""; // reset; re-captured in RunAllChecks once the script is located
            UpdateFilterButtonStyles();
            ClearResults();
            InfoAppName.Text   = "-";
            InfoVersion.Text   = "-";
            InfoPublisher.Text = "-";
            InfoScript.Text    = "-";
            InfoLog.Text       = "-";
            _appName = _appVendor = _appVersion = _appRevision = "-";

            // Capture UI-thread values before background task starts
            string snapPathFromUi = TxtQAsnapPath?.Text.Trim() ?? "";

            // Disable all buttons except Cancel; enable Cancel
            SetVerifyingState(true);

            // Clear search box when starting a new verification
            if (_txtQASearch != null) _txtQASearch.Clear();
            _qaSearchText = "";
            _qaSearchMatches.Clear();
            _qaSearchIdx = -1;

            // Show spinner - created and shown on UI thread
            Window? progressWin = ShowQAProgressDialog();
            try
            {
                // All heavy work (log parsing + checks) runs on background thread
                await Task.Run(() => RunAllChecks(manualLogPath, snapPathFromUi));
                RenderResults(progressWin);
                progressWin = null;
                AutoExportIfConfigured();
            }
            catch (Exception ex)
            {
                VpStyledMessage(Window.GetWindow(this), "Package Validation", $"QA Error:\n{ex.Message}", "⛔");
            }
            finally
            {
                SetVerifyingState(false);
                try { if (progressWin != null) { progressWin.Close(); progressWin = null; } } catch { }
            }
        }

        /// <summary>
        /// Shows a spinning progress dialog while QA is running.
        /// Must be called on the UI thread. Returns the window so caller can Close() it.
        /// </summary>
        private Window ShowQAProgressDialog()
        {
            var win = new Window
            {
                Title = "Package Validation",
                Width = 360, Height = 160,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current.MainWindow,
                Topmost = true,
                ShowInTaskbar = false,
                Background = (Brush)(FindResource("WindowBgBrush") ?? Brushes.White),
            };

            var outer = new Border
            {
                BorderBrush = (Brush)(FindResource("BorderMainBrush") ?? Brushes.LightGray),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24)
            };

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            // Spinner - rotating dashed ellipse
            var accentBrush = (Brush?)null;
            try { accentBrush = (Brush)FindResource("AccentBrush"); } catch { }
            accentBrush ??= (Brush?)null;
            try { accentBrush ??= (Brush)FindResource("PrimaryBrush"); } catch { }
            accentBrush ??= new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));

            var spinEllipse = new System.Windows.Shapes.Ellipse
            {
                Width = 40, Height = 40,
                Stroke = accentBrush,
                StrokeThickness = 4,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 6, 3 },
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };
            var spinAnim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0, To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(0.9)),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            spinEllipse.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, spinAnim);

            var msgTb = new TextBlock
            {
                Text = "Package Validation in progress…",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)(FindResource("TextPrimaryBrush") ?? Brushes.Black),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var subTb = new TextBlock
            {
                Text = "Please wait - this window will close automatically once complete.",
                FontSize = 11,
                Foreground = (Brush)(FindResource("TextMutedBrush") ?? Brushes.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };

            stack.Children.Add(spinEllipse);
            stack.Children.Add(msgTb);
            stack.Children.Add(subTb);
            outer.Child = stack;
            win.Content = outer;

            win.Show();
            return win;
        }

        // ══════════════════════════════════════════════════════════════════
        //  MAIN QA ENGINE
        // ══════════════════════════════════════════════════════════════════
        private void RunAllChecks(string manualLogPath = "", string snapPathFromUi = "")
        {
            _results.Clear();
            _rowCache.Clear();
            _statusOverrides.Clear();
            _usedUninstallSnapPaths.Clear();
            _selectedRowBorder = null;
            _liveHtmlPath = null;
            _savedHtmlPath = _savedCsvPath = null;
            _importSourcePath = null;

            // ── Detect Install vs Uninstall from log file content ─────
            // Log format: "Deployment type is [Install]" or "Deployment type is [Uninstall]"
            bool isUninstallRun = false;
            if (!string.IsNullOrEmpty(manualLogPath) && File.Exists(manualLogPath))
            {
                try {
                    string logHead = "";
                    using (var sr = new System.IO.StreamReader(manualLogPath))
                    {
                        char[] buf = new char[51200];
                        int read = sr.Read(buf, 0, buf.Length);
                        logHead = new string(buf, 0, read);
                    }
                    // Matches: "Deployment type is [Uninstall]" or "DeploymentType:'Uninstall'"
                    var dtMatch = Regex.Match(logHead,
                        @"Deployment\s+type\s+is\s+\[(Install|Uninstall)\]|DeploymentType[=:]['""]?(Install|Uninstall)['""]?",
                        RegexOptions.IgnoreCase);
                    if (dtMatch.Success)
                    {
                        string val = dtMatch.Groups[1].Success
                            ? dtMatch.Groups[1].Value
                            : dtMatch.Groups[2].Value;
                        isUninstallRun = val.Equals("Uninstall", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                        // Fallback: log filename
                        isUninstallRun = IOPath.GetFileName(manualLogPath)
                            .IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
                } catch {
                    isUninstallRun = IOPath.GetFileName(manualLogPath)
                        .IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            else
            {
                isUninstallRun = !string.IsNullOrEmpty(manualLogPath) &&
                    IOPath.GetFileName(manualLogPath)
                        .IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // ── Parse ADT log first - derive package folder if not set ─
            AdtLogInfo adtLog = !string.IsNullOrEmpty(manualLogPath) ? ParseAdtLog(manualLogPath) : new AdtLogInfo();

            // If log parser detected deployment type, override the filename-based guess
            if (!string.IsNullOrEmpty(adtLog.DeploymentType))
                isUninstallRun = adtLog.DeploymentType.Equals("Uninstall", StringComparison.OrdinalIgnoreCase);

            _isUninstallRun = isUninstallRun; // cache for export filename

            // Derive package folder from log if not already set
            if (string.IsNullOrEmpty(_packageFolder) && !string.IsNullOrEmpty(adtLog.PackageFolder))
                _packageFolder = adtLog.PackageFolder;

            // Find Invoke-AppDeployToolkit.ps1 - from package folder or log-derived path
            string? script = FindInvokeScript(_packageFolder);
            if (script == null && !string.IsNullOrEmpty(adtLog.PackageFolder))
                script = FindInvokeScript(adtLog.PackageFolder);

            // ── Row 1: Package Path ────────────────────────────────────
            // Always show the resolved package folder as the very first QA row
            int idx = 1;
            {
                string pkgPath   = script != null ? IOPath.GetDirectoryName(script)! : _packageFolder;
                bool   pkgExists = !string.IsNullOrEmpty(pkgPath) && Directory.Exists(pkgPath);
                _results.Add(new(idx++, "Package Path · Folder",
                    pkgExists ? pkgPath : (!string.IsNullOrEmpty(pkgPath) ? pkgPath : "Package folder not found"),
                    pkgExists ? QAStatus.Pass : QAStatus.Fail,
                    pkgExists ? null : "Invoke-AppDeployToolkit.ps1 folder not found",
                    RowHighlight.None,
                    pkgExists ? pkgPath : null));
            }

            // ── Row 2: Package Validation Reports Path ────────────────
            // ExportPath in ValidatePackage.json is mandatory - always report that configured path.
            // There is no C:\Temp fallback: a missing ExportPath is a real configuration failure.
            {
                string rptPath = "";
                try
                {
                    var quickCfg = LoadQAConfig(_packageFolder);
                    if (!string.IsNullOrEmpty(quickCfg?.ExportPath))
                        rptPath = Environment.ExpandEnvironmentVariables(quickCfg.ExportPath.Trim());
                }
                catch { }

                if (string.IsNullOrEmpty(rptPath))
                {
                    // ExportPath is mandatory - surface its absence instead of inventing a path.
                    _results.Add(new(idx++, "Package Validation Reports Path · Folder",
                        "Export path not configured",
                        QAStatus.Fail,
                        "ExportPath is missing from ValidatePackage.json (mandatory)"));
                }
                else
                {
                    // The folder is created only when the report is written, so a configured-but-
                    // not-yet-created path is expected and is not a failure.
                    bool rptExists = Directory.Exists(rptPath);
                    _results.Add(new(idx++, "Package Validation Reports Path · Folder",
                        rptPath,
                        rptExists ? QAStatus.Pass : QAStatus.Skip,
                        rptExists ? null : "Will be created when the report is written",
                        RowHighlight.None,
                        rptExists ? rptPath : null));
                }
            }

            if (script == null)
            {
                _results.Add(new(idx++, "Package Path · Folder",
                    $"Invoke-AppDeployToolkit.ps1 not found - checked: {_packageFolder}",
                    QAStatus.Fail));
                return;
            }

            // ── Read session info from script ──────────────────────────
            string scriptContent = File.ReadAllText(script);
            string appName    = ExtractSessionParam(scriptContent, "AppName");
            string appVendor  = ExtractSessionParam(scriptContent, "AppVendor");
            string appVersion = ExtractSessionParam(scriptContent, "AppVersion");
            string appRev     = ExtractSessionParam(scriptContent, "AppRevision");
            string scriptDir  = IOPath.GetDirectoryName(script)!;
            _resolvedScriptDir = scriptDir; // remember for the sibling Documents resolution in AutoExportIfConfigured
            // Find the Files subfolder - could be alongside the script or one level up
            string filesDir   = IOPath.Combine(scriptDir, "Files");
            if (!Directory.Exists(filesDir))
            {
                // Try parent directory's Files folder (script might be in a subfolder)
                string parentFiles = IOPath.Combine(IOPath.GetDirectoryName(scriptDir)!, "Files");
                if (Directory.Exists(parentFiles)) filesDir = parentFiles;
            }

            _appName    = string.IsNullOrWhiteSpace(appName)    ? "-" : appName;
            _appVendor  = string.IsNullOrWhiteSpace(appVendor)  ? "-" : appVendor;
            _appVersion = string.IsNullOrWhiteSpace(appVersion) ? "-" : appVersion;
            _appRevision = string.IsNullOrWhiteSpace(appRev)    ? "-" : appRev;
            Dispatcher.Invoke(() =>
            {
                InfoAppName.Text  = appName;
                InfoVersion.Text  = appVersion;
                InfoPublisher.Text = appVendor;
                InfoScript.Text   = script;
            });

            // ── Log path: manual input wins; auto-detect as fallback ───
            string logPath = !string.IsNullOrWhiteSpace(manualLogPath)
                ? manualLogPath
                : FindLogPath(_packageFolder, appName, appVendor, appVersion);

            Dispatcher.Invoke(() =>
            {
                InfoLog.Text = !string.IsNullOrWhiteSpace(logPath) ? logPath : "Not specified";
                // Only update textbox if user hasn't typed something themselves
                if (TxtLogPath != null && string.IsNullOrWhiteSpace(TxtLogPath.Text))
                    TxtLogPath.Text = logPath ?? "";
            });

            // ── Extract Install-ADTDeployment commands ─────────────────
            // ── Load .svr - registry source of truth for EXE installs ─────────────────
            // Looks for C:\Temp\<FileName> - For QA.svr written by EtwQaCapture.
            // If found, registry checks use snapshot data instead of live machine registry.
            // Only loaded for install runs - uninstall checks load their own per-EXE snap
            // inside CheckUninstallProcess to avoid double-counting qasnap items.
            _qaSnap     = null;
            _qaSnapPath = null;
            _snapsValidated.Clear();   // fresh run: no snapshot claimed yet
            if (!isUninstallRun)
            {
                try
                {
                    var exeDetect = EtwQaCapture.DetectExeInstall(script);
                    if (exeDetect.HasValue)
                    {
                        // Use FindQAsnapFile so QAsnapPath from QA.json/UI takes priority
                        string baseName5 = IOPath.GetFileNameWithoutExtension(exeDetect.Value.FileName);
                        string? snapPath = FindQAsnapFile(baseName5);
                        if (snapPath != null)
                        {
                            string snapJson = File.ReadAllText(snapPath);
                            _qaSnap     = DeserializeSnapshot(snapJson);
                            _qaSnapPath = _qaSnap != null ? snapPath : null;
                        }
                    }
                }
                catch { /* qasnap is optional - fall back to live registry if missing */ }
            }

            var commands = ExtractInstallCommands(scriptContent);

            // ── Load QA.json ───────────────────────────────────────────
            QAConfig? qa = LoadQAConfig(_packageFolder);
            _qaConfig = qa; // cache for use in export auto-path and QAsnapPath

            // ── Resolve QAsnapPath and populate the UI textbox ────────────────────────
            // Priority:
            //   1. User manually typed/browsed a path in the UI textbox → keep it, use it
            //   2. QA.json has a non-empty QAsnapPath → use it, populate textbox
            //   3. QA.json QAsnapPath is empty/missing → fall back to C:\Temp\Validate Source
            //   In all cases, update the textbox so the user can see what is being used.
            {
                const string DefaultSnapDir = @"C:\Temp\Validate Source";
                string resolvedSnapPath;

                if (!string.IsNullOrWhiteSpace(snapPathFromUi))
                {
                    // Case 1: user provided path - use as-is, no textbox update needed
                    resolvedSnapPath = snapPathFromUi;
                }
                else if (!string.IsNullOrWhiteSpace(qa?.QAsnapPath))
                {
                    // Case 2: QA.json has a path → populate textbox with it
                    resolvedSnapPath = qa!.QAsnapPath!.Trim();
                    // Search folder stays internal (_qaConfig below); the field shows a .svr FILE
                    // or stays empty, never a folder.
                }
                else
                {
                    // Case 3: QA.json QAsnapPath empty or QA.json missing → default to C:\Temp\Validate Source
                    resolvedSnapPath = DefaultSnapDir;
                    // Search folder stays internal (_qaConfig below); never shown in the field.
                }

                if (_qaConfig == null) _qaConfig = new QAConfig();
                _qaConfig.QAsnapPath = resolvedSnapPath;
            }
            if (qa == null)
            {
                // Distinguish "file not found" from "file found but failed to parse"
                bool jsonFileExists = System.IO.File.Exists(
                    @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidatePackage.json");
                string skipDetail = jsonFileExists
                    ? "ValidatePackage.json found but could not be parsed - check JSON syntax"
                    : "ValidatePackage.json not found - NoModify/NoRepair/NoRemove and MSI Properties checks skipped";
                string skipHint = jsonFileExists
                    ? @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidatePackage.json"
                    : $"Place ValidatePackage.json in: C:\\Program Files\\Smekal\\SwiftPack Studio\\Templates\\ValidatePackage.json";
                _results.Add(new(999, "ValidatePackage.json",
                    skipDetail, QAStatus.Skip, skipHint));
            }

            // If package folder can be derived from log (when ps1 not found elsewhere)
            if (string.IsNullOrEmpty(filesDir) || !Directory.Exists(filesDir))
            {
                string logDerivedFilesDir = IOPath.Combine(adtLog.PackageFolder, "Files");
                if (Directory.Exists(logDerivedFilesDir)) filesDir = logDerivedFilesDir;
            }

            // ── Hardcode Path check - runs for both install and uninstall ──
            CheckHardcodedPaths(ref idx, script, scriptContent);

            // ── Check each command ─────────────────────────────────────
            if (!isUninstallRun)
            {
                foreach (var (cmd, lineNo) in commands)
                {
                    string resolved = ResolveCommand(cmd, filesDir);
                    // Build check type label with script line number
                    string cmdLabel = GetCommandLabel(cmd, lineNo);

                    if (IsCopyFile(cmd))
                        CheckCopyFile(ref idx, resolved, cmd, adtLog, cmdLabel);
                    else if (IsStartProcess(cmd))
                        CheckStartProcess(ref idx, resolved, cmd, filesDir, qa, cmdLabel);
                    else if (IsStartMsi(cmd))
                        CheckStartMsi(ref idx, resolved, cmd, filesDir, qa, adtLog, cmdLabel);
                    else if (IsStartMsp(cmd))
                        CheckStartMsp(ref idx, resolved, filesDir, cmdLabel);
                    else if (IsSetRegistry(cmd))
                        CheckSetRegistry(ref idx, resolved, cmd, adtLog, cmdLabel);
                    else if (IsNewFolder(cmd))
                        CheckNewFolder(ref idx, resolved, cmdLabel);
                    else if (IsNewShortcut(cmd) || IsSetShortcut(cmd))
                        CheckNewShortcut(ref idx, resolved, cmdLabel);
                    else if (IsSetEnvVar(cmd))
                        CheckSetEnvVar(ref idx, resolved, cmdLabel);
                    else if (IsStartService(cmd))
                        CheckStartService(ref idx, resolved, cmdLabel);
                    else if (IsStopService(cmd))
                        CheckStopService(ref idx, resolved, cmdLabel);
                    else if (IsRegisterDll(cmd) || IsInvokeRegSvr32(cmd))
                        CheckRegisterDll(ref idx, resolved, cmdLabel);
                    else if (IsSetIniValue(cmd))
                        CheckSetIniValue(ref idx, resolved, cmdLabel);
                    else if (IsCopyFileToUserProfiles(cmd))
                        CheckCopyFileToUserProfiles(ref idx, resolved, cmdLabel);
                    else if (IsSetActiveSetup(cmd))
                        CheckSetActiveSetup(ref idx, resolved, cmdLabel);
                    else
                        _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Command type not checked"));
                }

                if (_results.Count == 0)
                    _results.Add(new(1, "Info", "No install commands found in Install-ADTDeployment", QAStatus.Skip));
            }

            // ── Extract Uninstall-ADTDeployment commands ───────────────
            if (isUninstallRun)
            {
                // Also parse install commands to cross-reference Copy-ADTFile destinations
                var installCmds = ExtractInstallCommands(scriptContent);
                var installCopies = installCmds
                    .Where(c => IsCopyFile(c.Cmd))
                    .Select(c => ResolveCommand(c.Cmd, filesDir))
                    .ToList();

                var uninstallCommands = ExtractUninstallCommands(scriptContent);
                if (uninstallCommands.Count > 0)
                {
                    foreach (var (cmd, lineNo) in uninstallCommands)
                    {
                            string resolved = ResolveCommand(cmd, filesDir);
                        string cmdLabel = GetCommandLabel(cmd, lineNo);
                        if (IsStartMsi(cmd))
                            CheckUninstallMsi(ref idx, resolved, cmd, filesDir, adtLog, cmdLabel);
                        else if (IsStartProcess(cmd))
                            CheckUninstallProcess(ref idx, resolved, cmd, filesDir, cmdLabel);
                        else if (IsRemoveFile(cmd))
                            CheckRemoveFile(ref idx, resolved, cmd, adtLog, cmdLabel);
                        else if (IsRemoveFolder(cmd))
                            CheckRemoveFolder(ref idx, resolved, cmdLabel);
                        else if (IsRemoveRegistry(cmd))
                            CheckRemoveRegistry(ref idx, resolved, cmd, adtLog, cmdLabel);
                        else if (IsSetRegistry(cmd))
                            CheckSetRegistry(ref idx, resolved, cmd, adtLog, cmdLabel);
                        else if (IsRemoveEnvVar(cmd))
                            CheckRemoveEnvVar(ref idx, resolved, cmdLabel);
                        else if (IsStopService(cmd))
                            CheckStopService(ref idx, resolved, cmdLabel);
                        else if (IsStartService(cmd))
                            CheckStartService(ref idx, resolved, cmdLabel);
                        else if (IsUnregisterDll(cmd))
                            CheckUnregisterDll(ref idx, resolved, cmdLabel);
                        else if (IsInvokeRegSvr32(cmd))
                            CheckInvokeRegSvr32(ref idx, resolved, isUninstall: true, cmdLabel);
                        else if (IsRemoveIniValue(cmd))
                            CheckRemoveIniValue(ref idx, resolved, cmdLabel);
                        else if (IsRemoveFileFromUserProfiles(cmd))
                            CheckRemoveFileFromUserProfiles(ref idx, resolved, cmdLabel);
                        else if (IsSetActiveSetup(cmd))
                            CheckSetActiveSetup(ref idx, resolved, cmdLabel);
                        else if (IsUninstallApp(cmd))
                            CheckUninstallApplication(ref idx, resolved, cmdLabel);
                        else
                            _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Command type not checked"));
                    }
                }

                // Cross-check: every Copy-ADTFile from Install should have a Remove-ADTFile in Uninstall
                CheckOrphanedCopies(ref idx, installCopies, uninstallCommands.Select(c => c.Cmd).ToList(), filesDir);

                // Check env vars removed (from MSI install verbose log)
                var msiRun = adtLog.MsiRuns.FirstOrDefault();
                if (!string.IsNullOrEmpty(msiRun?.VerboseLogPath))
                {
                    var installVerbose = ParseMsiVerboseLog(msiRun.VerboseLogPath);
                    CheckEnvVarsRemoved(ref idx, installVerbose);
                }

                if (_results.Count == 0)
                    _results.Add(new(1, "Info", "No uninstall commands found in Uninstall-ADTDeployment", QAStatus.Skip));
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  COPY-ADTFILE
        // ══════════════════════════════════════════════════════════════════
        private void CheckCopyFile(ref int idx, string resolved, string rawCmd, AdtLogInfo? adtLog = null, string cmdLabel = "Copy-ADTFile")
        {
            string src  = ExtractParam(resolved, "-Path").Trim('\'', '"');
            string dest = ExtractParam(resolved, "-Destination").Trim('\'', '"');

            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dest))
            {
                _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -Path / -Destination"));
                return;
            }

            string srcAbs  = ExpandEnv(src);
            string destAbs = ExpandEnv(dest);
            string fileName = IOPath.GetFileName(srcAbs);
            string destFile = IOPath.Combine(destAbs.TrimEnd('\\','/'), fileName);
            string detail   = $"\"{srcAbs}\"  →  \"{destFile}\"";

            // ── Check 1: ADT log confirms the copy ran and succeeded ──
            if (adtLog?.FileCopies.Count > 0)
            {
                var match = adtLog.FileCopies.FirstOrDefault(c =>
                    string.Equals(c.Src, srcAbs, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Src, src,    StringComparison.OrdinalIgnoreCase) ||
                    // Fallback: match by filename only (handles $env: expansion differences)
                    string.Equals(IOPath.GetFileName(c.Src), fileName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    if (match.Success)
                    {
                        // Log says copy succeeded - check disk too (may differ on QA machine)
                        bool onDisk = File.Exists(destFile);
                        _results.Add(new(idx++, cmdLabel,
                            detail,
                            QAStatus.Pass, // Log confirmation is sufficient
                            onDisk ? null : $"Copied per log (file not found on this machine: {destFile})",
                            RowHighlight.None, destAbs));
                        return;
                    }
                    else
                    {
                        _results.Add(new(idx++, cmdLabel, detail, QAStatus.Fail, "Log shows file copy FAILED",
                            RowHighlight.None, destAbs));
                        return;
                    }
                }
                // Command found in script but not in log - did not execute
                _results.Add(new(idx++, cmdLabel, detail, QAStatus.Fail,
                    "Copy command not found in log - command did not execute",
                    RowHighlight.None, destAbs));
                return;
            }

            // ── Fallback: disk check only (no log) ───────────────────
            bool exists = File.Exists(destFile);
            _results.Add(new(idx++, cmdLabel,
                detail,
                exists ? QAStatus.Pass : QAStatus.Fail,
                exists ? null : $"File not found on disk: {destFile}",
                RowHighlight.None, destAbs));
        }

        // ══════════════════════════════════════════════════════════════════
        //  SET-ADTREGISTRYKEY CHECK
        // ══════════════════════════════════════════════════════════════════
        private void CheckSetRegistry(ref int idx, string resolved, string rawCmd, AdtLogInfo? adtLog, string cmdLabel = "Set-ADTRegistryKey")
        {
            // Parse -LiteralPath/-Path and -Name/-Value from the command
            string keyPath  = ExtractParam(resolved, "-LiteralPath").Trim('"', '\'');
            if (string.IsNullOrEmpty(keyPath))
                keyPath = ExtractParam(resolved, "-Path").Trim('"', '\'');
            string name     = ExtractParam(resolved, "-Name").Trim('"', '\'');
            string value    = ExtractParam(resolved, "-Value").Trim('"', '\'');

            // Normalise: strip "Microsoft.PowerShell.Core\Registry::" prefix if present
            keyPath = Regex.Replace(keyPath, @"^Microsoft\.PowerShell\.Core\\Registry::", "",
                RegexOptions.IgnoreCase);

            // Check against ADT log entries first (confirm it actually ran)
            AdtLogInfo.RegKeyEntry? logEntry = null;
            if (adtLog?.RegEntries.Count > 0)
            {
                logEntry = adtLog.RegEntries.FirstOrDefault(r =>
                    string.Equals(r.KeyPath,     keyPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.ValueName,   name,    StringComparison.OrdinalIgnoreCase));
            }

            bool ranInLog = logEntry != null;

            // Verify value exists on system registry
            bool regExists = false;
            try
            {
                string hiveStr = keyPath.Split('\\')[0];
                string subKey  = string.Join("\\", keyPath.Split('\\').Skip(1));
                var hive = hiveStr.Contains("LOCAL_MACHINE") ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = hive.OpenSubKey(subKey);
                if (key != null)
                {
                    var val = key.GetValue(name);
                    regExists = val != null &&
                        string.Equals(val.ToString(), value, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            // Item 1: format as [HKEY_LOCAL_MACHINE\SOFTWARE\test] "abc" "REG_SZ" "1"
            // Detect value type
            string regType = "REG_SZ";
            try {
                string hiveStr2 = keyPath.Split('\\')[0];
                string subKey2  = string.Join("\\", keyPath.Split('\\').Skip(1));
                var hive2 = hiveStr2.Contains("LOCAL_MACHINE") ? Registry.LocalMachine : Registry.CurrentUser;
                using var key2 = hive2.OpenSubKey(subKey2);
                if (key2 != null)
                {
                    var kind = key2.GetValueKind(name);
                    regType = kind switch {
                        Microsoft.Win32.RegistryValueKind.DWord   => "REG_DWORD",
                        Microsoft.Win32.RegistryValueKind.QWord   => "REG_QWORD",
                        Microsoft.Win32.RegistryValueKind.Binary  => "REG_BINARY",
                        Microsoft.Win32.RegistryValueKind.MultiString  => "REG_MULTI_SZ",
                        Microsoft.Win32.RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
                        _ => "REG_SZ"
                    };
                }
            } catch { }

            string detail = $"[{keyPath}]  \"{name}\"  \"{regType}\"  \"{value}\"";
            if (!ranInLog)
                detail += "  (Not found in install log - command may not have executed)";
            else if (!regExists)
                detail += "  (Registry key/value not found on system)";

            _results.Add(new(idx++, cmdLabel + " · Registry",
                detail,
                regExists ? QAStatus.Pass : QAStatus.Fail,
                null, RowHighlight.None,
                regExists ? $"reg://{keyPath}" : null));
        }

        // ══════════════════════════════════════════════════════════════════
        //  START-ADTPROCESS (EXE install)
        // ══════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════
        //  START-ADTPROCESS (EXE install)
        //  All checks driven from .svr when available:
        //    1. Registry (Uninstall entry + all other keys)
        //    2. InstallLocation / Folders
        //    3. Shortcuts
        //    4. Services
        //    5. Key files (exe/dll sample)
        //    6. ARP Flags (NoModify/NoRepair/NoRemove from QA.json)
        // ══════════════════════════════════════════════════════════════════
        private void CheckStartProcess(ref int idx, string resolved, string rawCmd,
            string filesDir, QAConfig? qa, string cmdLabel = "Start-ADTProcess")
        {
            string exeRaw  = ExtractParam(resolved, "-FilePath").Trim('"', '\'');
            string exePath = IOPath.IsPathRooted(exeRaw)
                ? exeRaw
                : IOPath.Combine(filesDir.TrimEnd('\\', '/'), exeRaw);
            exePath = ExpandEnv(exePath);
            string exeFileName = IOPath.GetFileName(exePath);
            string exeName     = IOPath.GetFileNameWithoutExtension(exePath);

            // ── Load the .svr specific to THIS EXE ─────────────────────────────────
            // Each EXE may have its own .svr file (e.g. when multiple EXEs are in one
            // package). FindQAsnapFile searches by EXE baseName with fuzzy matching so
            // "DuplicateCleaner" matches "DigitalVolcano_DuplicateCleaner1.2.svr".
            QaSnapshot? exeSnap = null;
            string?     exeSnapPath = null;
            try
            {
                string? snapPath = FindQAsnapFile(exeName);
                if (snapPath != null)
                {
                    exeSnap = DeserializeSnapshot(File.ReadAllText(snapPath));
                    if (exeSnap != null) exeSnapPath = snapPath;
                }
            }
            catch { }

            // Use per-EXE snap when available; fall back to the page-level _qaSnap
            // (which covers the first/primary EXE detected at startup).
            QaSnapshot? activeSnap     = exeSnap ?? _qaSnap;
            string?     activeSnapPath = exeSnap != null ? exeSnapPath : _qaSnapPath;

            // ── No qasnap for this EXE: basic live-registry fallback ──────────────────
            if (activeSnap == null)
            {
                var regFallback = FindRegistryEntryForExe(exeFileName, exeName, null);
                if (regFallback == null)
                    _results.Add(new(idx++, cmdLabel + " · Registry",
                        $"EXE Install: \"{exePath}\"", QAStatus.Fail,
                        $"Application is not installed on the machine."));
                else
                {
                    _results.Add(new(idx++, cmdLabel + " · Registry",
                        $"{regFallback.DisplayName}  |  {regFallback.DisplayVersion}  |  {regFallback.Publisher}  [live registry]",
                        QAStatus.Pass, "No .svr found - registry check used live registry only",
                        RowHighlight.None, "reg://" + regFallback.RegistryKeyPath));
                    if (qa != null) CheckRegistryFlags(ref idx, cmdLabel + " · ARPFlags", regFallback, qa);
                }
                return;
            }

            // ════════════════════════════════════════════════════════════
            //  QASNAP-DRIVEN CHECKS
            // ════════════════════════════════════════════════════════════

            // Only once per .svr per run. An explicitly chosen SVR is returned by FindQAsnapFile for
            // every command, so in a two-app package both install commands land on the same file and
            // every File / Folder / Registry / Shortcut row would be emitted twice.
            if (!ClaimSnapshot(activeSnapPath))
            {
                _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip,
                    $"Snapshot already validated for an earlier command in this script: "
                    + IOPath.GetFileName(activeSnapPath!)));
                return;
            }

            // ── 1. Uninstall registry entry ───────────────────────────
            // Try qasnap first; if not found (capture may not include Uninstall key),
            // fall back to live registry - NSIS installers commonly write the
            // Uninstall entry after capture, so it appears in live registry only.
            var regEntry = FindRegistryEntryForExe(exeFileName, exeName, activeSnap);
            if (regEntry == null)
                regEntry = FindRegistryEntryForExe(exeFileName, exeName, null);
            if (regEntry == null)
            {
                _results.Add(new(idx++, cmdLabel + " · Registry",
                    $"EXE Install: \"{exePath}\"", QAStatus.Fail,
                    "No uninstall registry entry found in .svr or live registry"));
            }
            else
            {
                // 1a) Product identity row - verify the key actually exists on the live machine.
                // The qasnap captured it during install; if the app was uninstalled since then
                // the key will be gone and we must report Fail, not Pass.
                bool regKeyExistsLive = false;
                try
                {
                    string[] kParts  = regEntry.RegistryKeyPath.Split('\\');
                    string   kSubKey = string.Join("\\", kParts.Skip(1));
                    var      kHive   = kParts[0].Contains("LOCAL_MACHINE")
                                       ? Registry.LocalMachine : Registry.CurrentUser;
                    using var liveKey = kHive.OpenSubKey(kSubKey);
                    regKeyExistsLive = liveKey != null;
                }
                catch { }

                _results.Add(new(idx++, cmdLabel + " · Registry",
                    regKeyExistsLive
                        ? $"{regEntry.DisplayName}  |  {regEntry.DisplayVersion}  |  {regEntry.Publisher}"
                        : $"EXE Install: \"{exePath}\"",
                    regKeyExistsLive ? QAStatus.Pass : QAStatus.Fail,
                    regKeyExistsLive ? null : "Application is not installed on the machine.",
                    RowHighlight.None,
                    regKeyExistsLive ? "reg://" + regEntry.RegistryKeyPath : null));

                // 1b) InstallLocation - only meaningful when key exists live
                if (regKeyExistsLive && !string.IsNullOrEmpty(regEntry.InstallLocation))
                {
                    bool locExists = Directory.Exists(regEntry.InstallLocation)
                                  || File.Exists(regEntry.InstallLocation);
                    string locDetail = locExists
                        ? GetFolderStats(regEntry.InstallLocation)
                        : regEntry.InstallLocation;
                    _results.Add(new(idx++, cmdLabel + " · InstallLocation",
                        locDetail,
                        locExists ? QAStatus.Pass : QAStatus.Fail,
                        locExists ? null : "Folder not found on this machine",
                        RowHighlight.None, regEntry.InstallLocation));
                }

                // 1c) All other values in the uninstall key - always check (Fail if key gone)
                var skipValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "DisplayName","DisplayVersion","Publisher","InstallLocation",
                      "NoModify","NoRepair","NoRemove" };
                var uninstallEntries = (activeSnap?.Registry ?? new())
                    .Where(r => r.Key.Equals(regEntry.RegistryKeyPath, StringComparison.OrdinalIgnoreCase)
                             && !skipValues.Contains(r.Value)
                             && !string.IsNullOrEmpty(r.Value))
                    .ToList();
                foreach (var sr in uninstallEntries)
                {
                    bool valExists = false;
                    try
                    {
                        string[] pts = regEntry.RegistryKeyPath.Split('\\');
                        string subKey = string.Join("\\", pts.Skip(1));
                        var hive = pts[0].Contains("LOCAL_MACHINE")
                            ? Registry.LocalMachine : Registry.CurrentUser;
                        using var lk = hive.OpenSubKey(subKey);
                        valExists = lk?.GetValue(sr.Value) != null;
                    }
                    catch { }
                    _results.Add(new(idx++, cmdLabel + " · Registry",
                        $"[{sr.Key}]  {sr.Value} = {sr.Data}  ({sr.Type})",
                        valExists ? QAStatus.Pass : QAStatus.Fail,
                        valExists ? null : "Registry value not found on this machine",
                        RowHighlight.None, string.IsNullOrEmpty(sr.Value)
                            ? "reg://" + regEntry.RegistryKeyPath
                            : "regval://" + regEntry.RegistryKeyPath + "?" + sr.Value));
                }

                // 1d) ARP Flags - only meaningful when key exists live
                if (regKeyExistsLive && qa != null)
                    CheckRegistryFlags(ref idx, cmdLabel + " · ARPFlags", regEntry, qa);
            }

            // ── 2. All other (non-Uninstall) registry keys from qasnap ─
            var otherRegEntries = (activeSnap?.Registry ?? new())
                .Where(r => r.Key.IndexOf(@"CurrentVersion\Uninstall",
                    StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();
            foreach (var sr in otherRegEntries)
            {
                bool valExists = false;
                try
                {
                    string[] pts   = sr.Key.Split('\\');
                    string subKey  = string.Join("\\", pts.Skip(1));
                    var hive = pts[0].Contains("LOCAL_MACHINE")
                        ? Registry.LocalMachine : Registry.CurrentUser;
                    using var lk = hive.OpenSubKey(subKey);
                    if (lk == null) valExists = false;
                    else if (string.IsNullOrEmpty(sr.Value)) valExists = true;
                    else valExists = lk.GetValue(sr.Value) != null;
                }
                catch { }
                _results.Add(new(idx++, cmdLabel + " · Registry",
                    $"[{sr.Key}]  {sr.Value} = {sr.Data}  ({sr.Type})",
                    valExists ? QAStatus.Pass : QAStatus.Fail,
                    valExists ? null : "Registry key/value not found on this machine",
                    RowHighlight.None, string.IsNullOrEmpty(sr.Value) ? "reg://" + sr.Key : "regval://" + sr.Key + "?" + sr.Value));
            }

            // ── 3. Folders ────────────────────────────────────────────
            foreach (var folder in activeSnap.Folders)
            {
                if (IsSharedSystemFolder(folder)) continue;
                bool exists = Directory.Exists(folder);
                _results.Add(new(idx++, cmdLabel + " · Folder",
                    folder,
                    exists ? QAStatus.Pass : QAStatus.Fail,
                    exists ? null : "Folder not found on this machine",
                    RowHighlight.None, folder));
            }

            // ── 4. Shortcuts ──────────────────────────────────────────
            var kwRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (qa?.ShortcutKeywords != null)
                foreach (var kv in qa.ShortcutKeywords) kwRules[kv.Key] = kv.Value ?? "";
            else
                foreach (var kw in new[]{ "Uninstall","Remove","Readme","Log","Help","Text",
                    "Website","License","Update","Upgrade","Online","Notes","Release","Document" })
                    kwRules[kw] = "";
            string desktopRule = qa?.DesktopShortcuts ?? "";

            foreach (var scPath in activeSnap.Shortcuts)
            {
                bool scExists  = File.Exists(scPath);
                bool isDesktop = scPath.IndexOf("\\Desktop\\", StringComparison.OrdinalIgnoreCase) >= 0;
                // When the rule requires desktop shortcuts be removed, validate them separately over
                // the pre-exclusion list (below) so ones excluded in Validate Source are still caught.
                if (isDesktop && desktopRule.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                    continue;
                QAStatus   status = scExists ? QAStatus.Pass : QAStatus.Fail;
                RowHighlight high = RowHighlight.None;
                bool keywordFail  = false;
                foreach (var kv in kwRules)
                {
                    if (scPath.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (kv.Value.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                    { status = QAStatus.Fail; high = RowHighlight.Red; keywordFail = true; }
                    else if (kv.Value.Trim() == "" && high != RowHighlight.Red)
                        high = RowHighlight.Amber;
                }
                if (isDesktop && !keywordFail)
                {
                    if (desktopRule.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                    { status = QAStatus.Fail; high = RowHighlight.Red; }
                    else if (desktopRule.Trim() == "" && scExists)
                        high = RowHighlight.Amber;
                }
                if (!scExists) { status = QAStatus.Fail; high = RowHighlight.None; }
                string scDetail = scPath;
                if (!scExists) scDetail += "  (Shortcut not found on disk)";
                else if (status == QAStatus.Fail && high == RowHighlight.Red) scDetail += "  (Remove shortcut)";
                _results.Add(new(idx++, cmdLabel + " · Shortcut",
                    scDetail, status, null, high, IOPath.GetDirectoryName(scPath)));
            }

            // Desktop-shortcut removal - checked over the pre-exclusion list so shortcuts the user
            // excluded in Validate Source are still flagged when they are supposed to be removed.
            if (desktopRule.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                CheckDesktopShortcutsRemoved(ref idx, activeSnap.AllDesktopShortcuts, cmdLabel, _appName, exeName);

            // ── 5. Services ───────────────────────────────────────────
            foreach (var svc in activeSnap.Services)
            {
                bool svcExists = false;
                try
                {
                    using var sc = new System.ServiceProcess.ServiceController(svc.Name);
                    _ = sc.DisplayName;
                    svcExists = true;
                }
                catch { }
                _results.Add(new(idx++, cmdLabel + " · Service",
                    $"{svc.DisplayName}  |  StartType: {svc.StartType}  |  {svc.Binary}",
                    svcExists ? QAStatus.Pass : QAStatus.Fail,
                    svcExists ? null : $"Service '{svc.Name}' not found on this machine",
                    RowHighlight.None, "service://"));
            }

            // ── 6. Certificates ────────────────────────────────────────
            foreach (var cert in activeSnap.Certificates)
            {
                bool certExists = false;
                try
                {
                    foreach (var storeName in new[] {
                        System.Security.Cryptography.X509Certificates.StoreName.My,
                        System.Security.Cryptography.X509Certificates.StoreName.Root,
                        System.Security.Cryptography.X509Certificates.StoreName.TrustedPublisher })
                    {
                        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                            storeName, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                        if (store.Certificates.OfType<System.Security.Cryptography.X509Certificates.X509Certificate2>()
                            .Any(c => c.Subject.IndexOf(cert, StringComparison.OrdinalIgnoreCase) >= 0
                                   || c.Thumbprint.Equals(cert, StringComparison.OrdinalIgnoreCase)
                                   || c.FriendlyName.IndexOf(cert, StringComparison.OrdinalIgnoreCase) >= 0))
                        { certExists = true; break; }
                    }
                }
                catch { }
                _results.Add(new(idx++, cmdLabel + " · Certificate",
                    cert,
                    certExists ? QAStatus.Pass : QAStatus.Fail,
                    certExists ? null : "Certificate not found in machine certificate stores",
                    RowHighlight.None, "cert://"));
            }

            // ── 7. Firewall rules ──────────────────────────────────────
            foreach (var fwRule in activeSnap.Firewall)
            {
                bool ruleExists = false;
                try
                {
                    var netFwType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                    if (netFwType != null)
                    {
                        dynamic fwPolicy = Activator.CreateInstance(netFwType)!;
                        foreach (dynamic rule in fwPolicy.Rules)
                        {
                            if (string.Equals(rule.Name as string, fwRule, StringComparison.OrdinalIgnoreCase))
                            { ruleExists = true; break; }
                        }
                    }
                }
                catch { }
                _results.Add(new(idx++, cmdLabel + " · Firewall",
                    fwRule,
                    ruleExists ? QAStatus.Pass : QAStatus.Fail,
                    ruleExists ? null : "Firewall rule not found on this machine",
                    RowHighlight.None, "firewall://"));
            }

            // ── 8. Files captured in qasnap ──────────────────────────
            // Show all files, excluding installer temp files under \Temp\.
            // Deduplicate paths (case-insensitive) to avoid double-checking.
            // Order: exe/dll first, then other types - ensures key binaries appear
            // at the top even when the snap contains hundreds of plugin files.
            var allSnapFiles = activeSnap.Files
                .Where(f => f.IndexOf("\\Temp\\", StringComparison.OrdinalIgnoreCase) < 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(f => { var ext = IOPath.GetExtension(f).ToLowerInvariant();
                                          return ext == ".exe" ? 2 : ext == ".dll" ? 1 : 0; })
                .ToList();
            foreach (var filePath in allSnapFiles)
            {
                bool fileExists = File.Exists(filePath);
                _results.Add(new(idx++, cmdLabel + " · File",
                    filePath,
                    fileExists ? QAStatus.Pass : QAStatus.Fail,
                    fileExists ? null : "File not found on this machine",
                    RowHighlight.None, IOPath.GetDirectoryName(filePath)));
            }
        }

        /// <summary>
        /// Returns a summary string for a folder: "N files, M folders, X MB"
        /// Used in InstallLocation check rows.
        /// </summary>
        private static string GetFolderStats(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return folder;
                var di     = new System.IO.DirectoryInfo(folder);
                long bytes  = 0;
                int  files  = 0;
                int  dirs   = 0;
                foreach (var f in di.EnumerateFiles("*", System.IO.SearchOption.AllDirectories))
                { files++; try { bytes += f.Length; } catch { } }
                foreach (var _ in di.EnumerateDirectories("*", System.IO.SearchOption.AllDirectories))
                    dirs++;
                string size = bytes >= 1_073_741_824
                    ? $"{bytes / 1_073_741_824.0:F1} GB"
                    : bytes >= 1_048_576
                        ? $"{bytes / 1_048_576.0:F1} MB"
                        : bytes >= 1024
                            ? $"{bytes / 1024.0:F1} KB"
                            : $"{bytes} B";
                return $"{folder}  ({files} files, {dirs} folders, {size})";
            }
            catch { return folder; }
        }

        private void CheckStartMsi(ref int idx, string resolved, string rawCmd,
            string filesDir, QAConfig? qa, AdtLogInfo? adtLog = null, string cmdLabel = "Start-ADTMsiProcess")
        {
            string msiRaw = ExtractParam(resolved, "-FilePath").Trim('"', '\'');

            if (string.IsNullOrWhiteSpace(msiRaw))
            {
                _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip,
                    "No -FilePath parameter - pipeline or patch command, skipped"));
                return;
            }

            string msiPath = IOPath.IsPathRooted(msiRaw)
                ? msiRaw
                : IOPath.Combine(filesDir.TrimEnd('\\', '/'), msiRaw);
            msiPath = ExpandEnv(msiPath);

            if (!File.Exists(msiPath))
            {
                string msiName = IOPath.GetFileName(msiRaw);
                if (!string.IsNullOrEmpty(msiName) && Directory.Exists(filesDir))
                {
                    var found = Directory.GetFiles(filesDir, msiName, SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) msiPath = found;
                }
            }

            if (!File.Exists(msiPath))
            {
                _results.Add(new(idx++, cmdLabel, msiPath, QAStatus.Fail,
                    $"MSI file not found on disk: {msiPath}", RowHighlight.None, msiPath));
                return;
            }

            // ── Find the MSI run in ADT log ───────────────────────────
            AdtLogInfo.MsiRun? msiRun = adtLog?.MsiRuns.FirstOrDefault(r =>
                string.Equals(IOPath.GetFileName(r.MsiPath), IOPath.GetFileName(msiPath),
                    StringComparison.OrdinalIgnoreCase));

            // ── Parse MSI verbose log for all check data ──────────────
            MsiVerboseLogInfo msiVerbose = new();
            if (!string.IsNullOrEmpty(msiRun?.VerboseLogPath))
                msiVerbose = ParseMsiVerboseLog(msiRun.VerboseLogPath);

            // ── Read MSI file (for product info, shortcuts, MST) ──────
            MsiInfo? msi = ReadMsiInfo(msiPath);
            if (msi == null)
            {
                _results.Add(new(idx++, cmdLabel, msiPath, QAStatus.Fail,
                    "Could not read MSI database - Windows Installer COM unavailable"));
                return;
            }

            // ── Row 1: MSI · File ─────────────────────────────────────
            bool msiRanOk = msiRun?.Success ?? true;
            _results.Add(new(idx++, cmdLabel + " · File", msiPath,
                msiRanOk ? QAStatus.Pass : QAStatus.Fail,
                msiRanOk ? null : $"MSI exited with code {msiRun?.ExitCode}",
                RowHighlight.None, msiPath));

            // ── MST handling ──────────────────────────────────────────
            string mstRaw = ExtractTransformsParam(resolved);
            string resolvedMstPath = "";
            if (!string.IsNullOrWhiteSpace(mstRaw))
            {
                string mstPath = IOPath.IsPathRooted(mstRaw)
                    ? mstRaw
                    : IOPath.Combine(filesDir.TrimEnd('\\', '/'), mstRaw);
                mstPath = ExpandEnv(mstPath);
                if (!File.Exists(mstPath))
                {
                    string mstName = IOPath.GetFileName(mstRaw);
                    if (!string.IsNullOrEmpty(mstName) && Directory.Exists(filesDir))
                    {
                        var f = Directory.GetFiles(filesDir, mstName, SearchOption.AllDirectories).FirstOrDefault();
                        if (f != null) mstPath = f;
                    }
                }
                if (File.Exists(mstPath))
                {
                    resolvedMstPath = mstPath;
                    _results.Add(new(idx++, "MST · File", mstPath, QAStatus.Pass, null, RowHighlight.None, mstPath));
                    MsiInfo? mstInfo = ReadMsiInfoWithMst(msiPath, mstPath);
                    if (mstInfo != null) msi = mstInfo;
                    // Also read MST properties via binary
                    var mstBinary = ReadMstPropertiesViaTransformView(mstPath);
                    foreach (var kv in mstBinary) msiVerbose.Properties[kv.Key] = kv.Value;
                }
                else
                    _results.Add(new(idx++, "MST · File", mstPath, QAStatus.Fail,
                        $"MST transform file not found on disk: {mstPath}"));
            }

            string msiFile = IOPath.GetFileName(msiPath);
            string mstFile = !string.IsNullOrEmpty(resolvedMstPath) ? IOPath.GetFileName(resolvedMstPath) : "";
            string pfx = !string.IsNullOrEmpty(mstFile) ? mstFile : msiFile;
            bool hasMst = !string.IsNullOrEmpty(resolvedMstPath);

            // ── A) Registry entry ─────────────────────────────────────
            var regEntry = FindRegistryEntry(msi.ProductName, msi.ProductCode);
            if (regEntry == null)
            {
                _results.Add(new(idx++, cmdLabel, msiPath, QAStatus.Fail,
                    $"No uninstall registry entry found for product code {msi.ProductCode}",
                    RowHighlight.None, msiPath));
            }
            else
            {
                // B) InstallLocation - ARPINSTALLLOCATION first, then registry, then SetTargetFolder
                string installLoc = !string.IsNullOrEmpty(msiVerbose.InstallLocation)
                    ? msiVerbose.InstallLocation
                    : regEntry.InstallLocation;

                if (!string.IsNullOrEmpty(installLoc))
                {
                    bool locExists = Directory.Exists(installLoc);
                    string locDetail2 = locExists ? GetFolderStats(installLoc) : installLoc;
                    _results.Add(new(idx++, cmdLabel + " · Folder", locDetail2,
                        locExists ? QAStatus.Pass : QAStatus.Fail,
                        locExists ? null : "InstallLocation path does not exist",
                        RowHighlight.None, installLoc));
                }

                // B-extra) SetTargetFolder paths - only when ARPINSTALLLOCATION is absent
                // (e.g. older MSIs like Orca that have no ARPINSTALLLOCATION property)
                // Skip entirely if we already have a valid installLoc from ARPINSTALLLOCATION or registry
                if (string.IsNullOrEmpty(msiVerbose.InstallLocation) && string.IsNullOrEmpty(regEntry.InstallLocation))
                {
                    string winDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
                    foreach (var folder in msiVerbose.InstallFolders)
                    {
                        // Skip relative paths (e.g. "2\Self-Extractor\" - no drive letter)
                        if (!IOPath.IsPathRooted(folder)) continue;
                        // Skip Windows system paths
                        if (folder.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) continue;
                        if (IsSharedSystemFolder(folder)) continue;
                        // Skip if already covered by installLoc
                        if (!string.IsNullOrEmpty(installLoc) &&
                            folder.StartsWith(installLoc.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            continue;
                        bool folderExists = Directory.Exists(folder);
                        _results.Add(new(idx++, cmdLabel + " · Folder",
                            folderExists ? folder : $"{folder}  (Folder not found on system)",
                            folderExists ? QAStatus.Pass : QAStatus.Fail,
                            null, RowHighlight.None, folderExists ? folder : null));
                    }
                }

                // B1) Shortcuts - prefer exact paths from MSI verbose log (Note: 1: 2318)
                //     Fall back to MSI COM Shortcut table only when verbose log has none.
                // Load the matching .svr. If a snapshot exists, it is the GROUND TRUTH for which
                // shortcuts were actually created (the MSI's declared paths are theoretical - the
                // installer redirects them by ALLUSERS / per-user vs per-machine / 8.3 naming, so
                // they often don't match what landed on disk). In that case we SKIP the MSI-declared
                // shortcut rows here and let the snapshot block (F3) validate the real shortcuts.
                HashSet<string> excludedScPrefixes;
                bool snapshotHasShortcuts = false;
                {
                    string msiBase = IOPath.GetFileNameWithoutExtension(msiPath);
                    QaSnapshot? scSnap = _qaSnap;
                    try
                    {
                        string? sp = FindQAsnapFile(msiBase);
                        if (sp != null) scSnap = DeserializeSnapshot(File.ReadAllText(sp)) ?? _qaSnap;
                    }
                    catch { }
                    excludedScPrefixes = ExcludedShortcutPrefixes(scSnap);
                    snapshotHasShortcuts = (scSnap?.Shortcuts?.Count ?? 0) > 0;
                }

                var defaultKeywords = new[] { "Uninstall","Remove","Readme","Log","Help","Text",
                    "Website","License","Update","Upgrade","Online","Notes","Release","Document" };
                var kwRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (qa?.ShortcutKeywords != null)
                    foreach (var kv in qa.ShortcutKeywords) kwRules[kv.Key] = kv.Value ?? "";
                else
                    foreach (var kw in defaultKeywords) kwRules[kw] = "";
                string desktopRule = qa?.DesktopShortcuts ?? "";

                var scPaths = new List<string>();
                // Only validate MSI-declared shortcuts when the snapshot has none - otherwise the
                // snapshot's actual captured shortcuts (block F3) are the authoritative source.
                if (!snapshotHasShortcuts)
                {
                    if (msiVerbose.Shortcuts.Count > 0)
                        scPaths.AddRange(msiVerbose.Shortcuts);
                    else if (msi.Shortcuts.Count > 0)
                    {
                        foreach (var (scName, scDir, scDesc) in msi.Shortcuts)
                        {
                            string displayName = scName.Split('|')[0].Trim();
                            string scResolved = ResolveShortcutPath(displayName, scDir, msi.ProductName ?? "");
                            if (!string.IsNullOrEmpty(scResolved)) scPaths.Add(scResolved);
                        }
                    }
                }

                // Remove duplicate resolved paths (the MSI/verbose log can list the same shortcut
                // more than once) so each shortcut produces a single validation row.
                scPaths = scPaths
                    .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                foreach (var scPath in scPaths)
                {
                    // Skip shortcuts the user excluded in Validate Source - they are not part of
                    // the package, so they must not be validated or reported as Fail.
                    if (IsShortcutExcluded(scPath, excludedScPrefixes)) continue;

                    bool scExists = File.Exists(scPath);
                    bool isDesktopPath = scPath.IndexOf("\\Desktop\\", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isDesktopPath && desktopRule.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                        continue;
                    QAStatus status   = scExists ? QAStatus.Pass : QAStatus.Fail;
                    RowHighlight high = RowHighlight.None;
                    bool keywordFail  = false;
                    foreach (var kv in kwRules)
                    {
                        if (scPath.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string rule = kv.Value.Trim();
                        if (rule.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                        { status = QAStatus.Fail; high = RowHighlight.Red; keywordFail = true; }
                        else if (rule == "" && high != RowHighlight.Red)
                            high = RowHighlight.Amber;
                    }
                    bool desktopFail = false;
                    if (isDesktopPath && !keywordFail)
                    {
                        string dr = desktopRule.Trim();
                        if (dr.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                        { status = QAStatus.Fail; high = RowHighlight.Red; desktopFail = true; }
                        else if (dr == "" && scExists)
                            high = RowHighlight.Amber;
                    }
                    if (!scExists) { status = QAStatus.Fail; high = RowHighlight.None; }
                    string scDetail = scPath;
                    if (!scExists) scDetail += "  (Shortcut not found on disk)";
                    else if (keywordFail || desktopFail) scDetail += "  (Remove shortcut)";
                    _results.Add(new(idx++, cmdLabel + " · Shortcut", scDetail, status, null, high, IOPath.GetDirectoryName(scPath)));
                }

                // Desktop-shortcut removal - over the MSI-declared desktop paths (pre-exclusion).
                if (desktopRule.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                    CheckDesktopShortcutsRemoved(ref idx,
                        scPaths.Where(p => p.IndexOf("\\Desktop\\", StringComparison.OrdinalIgnoreCase) >= 0),
                        cmdLabel, _appName, msi.ProductName ?? "");

                // B2-B4) ARPFlags
                { bool vm = LooseMatch(regEntry.DisplayVersion, msi.ProductVersion ?? "");
                  _results.Add(new(idx++, cmdLabel + " · ARPFlags", vm ? $"DisplayVersion = {regEntry.DisplayVersion}"
                    : $"DisplayVersion = {regEntry.DisplayVersion}  (Expected value is {msi.ProductVersion})",
                    vm ? QAStatus.Pass : QAStatus.Fail, null,
                    vm ? RowHighlight.None : RowHighlight.Red, "regval://" + regEntry.RegistryKeyPath + "?DisplayVersion")); }
                { bool nm = LooseMatch(regEntry.DisplayName, msi.ProductName ?? "");
                  _results.Add(new(idx++, cmdLabel + " · ARPFlags", nm ? $"DisplayName = {regEntry.DisplayName}"
                    : $"DisplayName = {regEntry.DisplayName}  (Expected value is {msi.ProductName})",
                    nm ? QAStatus.Pass : QAStatus.Fail, null,
                    nm ? RowHighlight.None : RowHighlight.Red, "regval://" + regEntry.RegistryKeyPath + "?DisplayName")); }
                { bool pm = LooseMatch(regEntry.Publisher, msi.Manufacturer ?? "");
                  _results.Add(new(idx++, cmdLabel + " · ARPFlags", pm ? $"Publisher = {regEntry.Publisher}"
                    : $"Publisher = {regEntry.Publisher}  (Expected value is {msi.Manufacturer})",
                    pm ? QAStatus.Pass : QAStatus.Fail, null,
                    pm ? RowHighlight.None : RowHighlight.Red, "regval://" + regEntry.RegistryKeyPath + "?Publisher")); }

                // C) NoModify/NoRepair/NoRemove
                if (qa != null)
                    CheckRegistryFlags(ref idx, cmdLabel + " · ARPFlags", regEntry, qa, regEntry.RegistryKeyPath);
            }

            // D) MSI properties
            string executedCmd = adtLog?.ExecutedCommands
                .FirstOrDefault(c => c.IndexOf(IOPath.GetFileName(msiPath),
                    StringComparison.OrdinalIgnoreCase) >= 0) ?? "";
            if (qa != null)
                CheckMsiProperties(ref idx, msi, qa, "", resolved, resolvedMstPath,
                    _packageFolder, cmdLabel + " · Property", executedCmd);

            // E) Environment variables from MSI verbose log
            foreach (var (name, value, delimiter) in msiVerbose.EnvVars)
            {
                string envVarDetail = $"{name} = {value}";
                string? currentVal = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                                  ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                bool envExists = false;
                if (!string.IsNullOrEmpty(currentVal))
                {
                    string sep = delimiter.Trim() == "[~]" ? "[~]" : delimiter.Trim();
                    string[] parts = sep == "[~]"
                        ? new[] { currentVal }
                        : currentVal.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
                    envExists = parts.Any(p => string.Equals(p.Trim().TrimEnd('\\', '/'),
                        value.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                }
                _results.Add(new(idx++, cmdLabel,
                    envExists ? $"{envVarDetail}  (Environment variable set)"
                              : $"{envVarDetail}  (Environment variable not found on system)",
                    envExists ? QAStatus.Pass : QAStatus.Fail,
                    null, RowHighlight.None, "envvar://"));
            }

            // ── F) .svr-driven checks for MSI ─────────────────────────────────
            // Search for .svr written by Source Validation capture.
            // Search order:
            //   1. MSI's own folder  (<MsiName> - For QA.svr)
            //   2. Package folder
            //   3. C:\Temp (default EtwQaCapture output)
            //   4. C:\Temp with bare MSI name (no suffix)
            QaSnapshot? msiSnap = null;
            string?     msiSnapPath = null;
            try
            {
                string msiBaseName   = IOPath.GetFileNameWithoutExtension(msiPath);
                string msiFileName   = IOPath.GetFileName(msiPath);
                string msiFolder     = IOPath.GetDirectoryName(msiPath) ?? "";
                string snapSuffix    = msiBaseName + " - For QA.svr";

                // Search only QAsnapPath (from QA.json/UI) then C:\Temp\Validate Source
                var snapCandidates = new[] { FindQAsnapFile(msiBaseName) }
                    .Where(p => p != null).Select(p => p!).ToArray();

                foreach (var candidate in snapCandidates)
                {
                    if (!File.Exists(candidate)) continue;
                    string msiSnapJson = File.ReadAllText(candidate);
                    msiSnap = DeserializeSnapshot(msiSnapJson);
                    if (msiSnap != null) { msiSnapPath = candidate; break; }
                }
            }
            catch { }

            // Only once per .svr per run - see _snapsValidated. The MSI's own checks above (exit
            // code, log, ARP flags, MSI properties) have already run and are NOT deduped: those come
            // from the MSI itself and are genuinely different per app.
            if (msiSnap != null && !ClaimSnapshot(msiSnapPath))
            {
                _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip,
                    "Snapshot already validated for an earlier command in this script: "
                    + IOPath.GetFileName(msiSnapPath!)));
                msiSnap = null;   // fall through with no snapshot: the block below is skipped
            }

            if (msiSnap != null)
            {
                // F1) Files - all captured files, check existence on this machine
                foreach (var filePath in msiSnap.Files)
                {
                    bool fileExists = File.Exists(filePath);
                    _results.Add(new(idx++, cmdLabel + " · File",
                        filePath,
                        fileExists ? QAStatus.Pass : QAStatus.Fail,
                        fileExists ? null : "File not found on this machine",
                        RowHighlight.None, IOPath.GetDirectoryName(filePath)));
                }

                // F2) Folders - all captured folders
                foreach (var folder in msiSnap.Folders)
                {
                    if (IsSharedSystemFolder(folder)) continue;
                    bool exists = Directory.Exists(folder);
                    _results.Add(new(idx++, cmdLabel + " · Folder",
                        folder,
                        exists ? QAStatus.Pass : QAStatus.Fail,
                        exists ? null : "Folder not found on this machine",
                        RowHighlight.None, folder));
                }

                // F3) Shortcuts - all captured shortcuts with keyword/desktop policy checks
                foreach (var scPath in msiSnap.Shortcuts)
                {
                    bool scExists  = File.Exists(scPath);
                    bool isDesktop = scPath.IndexOf("\\Desktop\\", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isDesktop && (qa?.DesktopShortcuts ?? "").Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                        continue;
                    QAStatus   scStatus = scExists ? QAStatus.Pass : QAStatus.Fail;
                    RowHighlight scHigh = RowHighlight.None;
                    bool kwFail = false;
                    var kwRulesSnap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (qa?.ShortcutKeywords != null)
                        foreach (var kv in qa.ShortcutKeywords) kwRulesSnap[kv.Key] = kv.Value ?? "";
                    else
                        foreach (var kw in new[]{ "Uninstall","Remove","Readme","Log","Help","Text",
                            "Website","License","Update","Upgrade","Online","Notes","Release","Document" })
                            kwRulesSnap[kw] = "";
                    string desktopRuleSnap = qa?.DesktopShortcuts ?? "";
                    foreach (var kv in kwRulesSnap)
                    {
                        if (scPath.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (kv.Value.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                        { scStatus = QAStatus.Fail; scHigh = RowHighlight.Red; kwFail = true; }
                        else if (kv.Value.Trim() == "" && scHigh != RowHighlight.Red)
                            scHigh = RowHighlight.Amber;
                    }
                    if (isDesktop && !kwFail)
                    {
                        if (desktopRuleSnap.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                        { scStatus = QAStatus.Fail; scHigh = RowHighlight.Red; }
                        else if (desktopRuleSnap.Trim() == "" && scExists)
                            scHigh = RowHighlight.Amber;
                    }
                    if (!scExists) { scStatus = QAStatus.Fail; scHigh = RowHighlight.None; }
                    string scDetail = scPath;
                    if (!scExists) scDetail += "  (Shortcut not found on disk)";
                    else if (scStatus == QAStatus.Fail && scHigh == RowHighlight.Red) scDetail += "  (Remove shortcut)";
                    _results.Add(new(idx++, cmdLabel + " · Shortcut",
                        scDetail, scStatus, null, scHigh, IOPath.GetDirectoryName(scPath)));
                }

                // Desktop-shortcut removal - pre-exclusion list (see CheckStartProcess for rationale).
                if ((qa?.DesktopShortcuts ?? "").Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase))
                    CheckDesktopShortcutsRemoved(ref idx, msiSnap.AllDesktopShortcuts, cmdLabel, _appName, msi.ProductName ?? "");

                // F4) Registry - ALL keys from qasnap including Uninstall keys
                foreach (var sr in msiSnap.Registry)
                {
                    bool valExists = false;
                    string? actualData = null;
                    try
                    {
                        string[] pts  = sr.Key.Split('\\');
                        string subKey = string.Join("\\", pts.Skip(1));
                        var hive = pts[0].Contains("LOCAL_MACHINE")
                            ? Microsoft.Win32.Registry.LocalMachine
                            : Microsoft.Win32.Registry.CurrentUser;
                        using var lk = hive.OpenSubKey(subKey);
                        if (lk == null)
                            valExists = false;
                        else if (string.IsNullOrEmpty(sr.Value))
                            valExists = true;   // key existence check
                        else
                        {
                            var raw = lk.GetValue(sr.Value);
                            valExists  = raw != null;
                            actualData = raw?.ToString();
                        }
                    }
                    catch { }

                    // Build detail - show expected vs actual when data differs
                    string regDetail = string.IsNullOrEmpty(sr.Value)
                        ? $"[{sr.Key}]"
                        : $"[{sr.Key}]  {sr.Value} = {sr.Data}  ({sr.Type})";
                    if (valExists && actualData != null &&
                        !string.Equals(actualData, sr.Data, StringComparison.OrdinalIgnoreCase))
                        regDetail += $"  ⚠ Current: {actualData}";

                    _results.Add(new(idx++, cmdLabel + " · Registry",
                        regDetail,
                        valExists ? QAStatus.Pass : QAStatus.Fail,
                        valExists ? null : "Registry key/value not found on this machine",
                        RowHighlight.None, string.IsNullOrEmpty(sr.Value) ? "reg://" + sr.Key : "regval://" + sr.Key + "?" + sr.Value));
                }

                // F5) Environment Variables from qasnap
                foreach (var ev in msiSnap.EnvVars)
                {
                    string? curVal = Environment.GetEnvironmentVariable(ev.Name, EnvironmentVariableTarget.Machine)
                                  ?? Environment.GetEnvironmentVariable(ev.Name, EnvironmentVariableTarget.User);
                    bool evExists = false;
                    if (!string.IsNullOrEmpty(curVal))
                    {
                        string sep = string.IsNullOrEmpty(ev.Delimiter) ? ";" : ev.Delimiter;
                        evExists = curVal.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries)
                            .Any(p => string.Equals(p.Trim().TrimEnd('\\', '/'),
                                ev.Value.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    }
                    _results.Add(new(idx++, cmdLabel + " · EnvVar",
                        evExists ? $"{ev.Name} = {ev.Value}  (Environment variable set)"
                                 : $"{ev.Name} = {ev.Value}  (Environment variable not found on system)",
                        evExists ? QAStatus.Pass : QAStatus.Fail,
                        null, RowHighlight.None, "envvar://"));
                }

                // F6) Services
                foreach (var svc in msiSnap.Services)
                {
                    bool svcExists = false;
                    string svcStatus = "";
                    try
                    {
                        using var sc = new System.ServiceProcess.ServiceController(svc.Name);
                        svcStatus  = sc.Status.ToString();
                        svcExists  = true;
                    }
                    catch { }
                    string svcDetail = $"{svc.DisplayName}  |  StartType: {svc.StartType}  |  {svc.Binary}";
                    if (svcExists && !string.IsNullOrEmpty(svcStatus))
                        svcDetail += $"  |  Status: {svcStatus}";
                    _results.Add(new(idx++, cmdLabel + " · Service",
                        svcDetail,
                        svcExists ? QAStatus.Pass : QAStatus.Fail,
                        svcExists ? null : $"Service '{svc.Name}' not found on this machine",
                        RowHighlight.None, "service://"));
                }

                // F7) Certificates
                foreach (var cert in msiSnap.Certificates)
                {
                    bool certExists = false;
                    try
                    {
                        foreach (var storeName in new[] {
                            System.Security.Cryptography.X509Certificates.StoreName.My,
                            System.Security.Cryptography.X509Certificates.StoreName.Root,
                            System.Security.Cryptography.X509Certificates.StoreName.TrustedPublisher })
                        {
                            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                                storeName,
                                System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                            if (store.Certificates.OfType<System.Security.Cryptography.X509Certificates.X509Certificate2>()
                                .Any(c => c.Subject.IndexOf(cert, StringComparison.OrdinalIgnoreCase) >= 0
                                       || c.Thumbprint.Equals(cert, StringComparison.OrdinalIgnoreCase)
                                       || c.FriendlyName.IndexOf(cert, StringComparison.OrdinalIgnoreCase) >= 0))
                            { certExists = true; break; }
                        }
                    }
                    catch { }
                    _results.Add(new(idx++, cmdLabel + " · Certificate",
                        cert,
                        certExists ? QAStatus.Pass : QAStatus.Fail,
                        certExists ? null : "Certificate not found in machine certificate stores",
                        RowHighlight.None, "cert://"));
                }

                // F8) Drivers - check captured driver files exist on this machine
                // QaSnapshot.Drivers is populated when the capture engine detects .sys/.inf files
                // If the field is absent (older snapshots) this loop simply doesn't run.
                var snapDriverFiles = msiSnap.Files
                    .Where(f => { var e = IOPath.GetExtension(f).ToLowerInvariant();
                                  return e == ".sys" || e == ".inf"; })
                    .ToList();
                foreach (var driverFile in snapDriverFiles)
                {
                    bool driverExists = File.Exists(driverFile);
                    _results.Add(new(idx++, cmdLabel + " · Driver",
                        driverFile,
                        driverExists ? QAStatus.Pass : QAStatus.Fail,
                        driverExists ? null : "Driver file not found on this machine",
                        RowHighlight.None, IOPath.GetDirectoryName(driverFile)));
                }

                // F9) Firewall rules
                foreach (var fwRule in msiSnap.Firewall)
                {
                    bool ruleExists = false;
                    try
                    {
                        var netFwType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                        if (netFwType != null)
                        {
                            dynamic fwPolicy = Activator.CreateInstance(netFwType)!;
                            foreach (dynamic rule in fwPolicy.Rules)
                            {
                                if (string.Equals(rule.Name as string, fwRule, StringComparison.OrdinalIgnoreCase))
                                { ruleExists = true; break; }
                            }
                        }
                    }
                    catch { }
                    _results.Add(new(idx++, cmdLabel + " · Firewall",
                        fwRule,
                        ruleExists ? QAStatus.Pass : QAStatus.Fail,
                        ruleExists ? null : "Firewall rule not found on this machine",
                        RowHighlight.None, "firewall://"));
                }
            }
            else
            {
                _results.Add(new(idx++, cmdLabel + " · Snapshot",
                    $"No .svr found for {IOPath.GetFileName(msiPath)}",
                    QAStatus.Skip,
                    $"Run Source Validation capture on {IOPath.GetFileName(msiPath)} to generate a snapshot for deep file/folder/registry/service/certificate/driver checks"));
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  MSI PROPERTIES CHECK
        //  Sources: MSI binary + MST binary + Executed command line in ADT log
        // ══════════════════════════════════════════════════════════════════
        private void CheckMsiProperties(ref int idx, MsiInfo msi,
            QAConfig qa, string logPath, string installCmd = "", string mstPath = "",
            string packageFolder = "", string checkTypeLabel = "MSI · Property",
            string executedCmdLine = "")
        {
            if (qa.MSIProperties == null || qa.MSIProperties.Count == 0) return;

            bool hasMst = !string.IsNullOrEmpty(mstPath);

            // Read MST properties via binary parser (for MSI source attribution)
            var mstProps = hasMst
                ? ReadMstPropertiesViaTransformView(mstPath)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Parse PROPERTY=VALUE pairs from the "Executed" command line in ADT log
            // e.g. /i file.msi REBOOT=ReallySuppress /QN /L*V ...
            var cmdProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(executedCmdLine))
            {
                foreach (Match m in Regex.Matches(executedCmdLine,
                    @"\b([A-Z_][A-Z0-9_]*)=([^\s/""']+)",
                    RegexOptions.None))
                {
                    cmdProps[m.Groups[1].Value] = m.Groups[2].Value.Trim('"', '\'');
                }
            }
            // Also parse from installCmd (Start-ADTMsiProcess command line from script)
            foreach (Match m in Regex.Matches(installCmd,
                @"\b([A-Z_][A-Z0-9_]*)=([^\s""']+|""[^""]*""|'[^']*')",
                RegexOptions.None))
            {
                if (!cmdProps.ContainsKey(m.Groups[1].Value))
                    cmdProps[m.Groups[1].Value] = m.Groups[2].Value.Trim('"', '\'');
            }

            foreach (var prop in qa.MSIProperties)
            {
                if (string.IsNullOrWhiteSpace(prop.Property)) continue;
                if (string.IsNullOrWhiteSpace(prop.Value)) continue;

                string pName = prop.Property;
                string pVal  = prop.Value;

                // Check 1: base MSI Property table (only if no MST, or MST doesn't override)
                bool inMsi = false;
                if (msi.Properties.TryGetValue(pName, out string? dbVal)
                    && string.Equals(dbVal?.Trim(), pVal.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // If MST present, only credit MSI if MST doesn't also set it
                    inMsi = !hasMst || !mstProps.ContainsKey(pName);
                }

                // Check 2: MST property delta
                bool inMst = hasMst
                    && mstProps.TryGetValue(pName, out string? mstVal)
                    && string.Equals(mstVal?.Trim(), pVal.Trim(), StringComparison.OrdinalIgnoreCase);

                // Check 3: Executed command line (properties passed to msiexec)
                bool inCmd = cmdProps.TryGetValue(pName, out string? cmdVal)
                    && string.Equals(cmdVal?.Trim(), pVal.Trim(), StringComparison.OrdinalIgnoreCase);

                bool pass = inMsi || inMst || inCmd;

                string detail;
                if (pass)
                {
                    var sources = new List<string>();
                    if (inMsi) sources.Add("MSI");
                    if (inMst) sources.Add("MST");
                    if (inCmd) sources.Add("Commandline");
                    string sourceLabel = sources.Count switch
                    {
                        0 => "",
                        1 => sources[0],
                        2 => $"{sources[0]} and {sources[1]}",
                        _ => string.Join(", ", sources.Take(sources.Count - 1)) + " and " + sources[^1]
                    };
                    detail = $"{pName}={pVal}  (Exists in {sourceLabel})";
                }
                else
                {
                    detail = $"{pName}={pVal} is missing";
                }

                _results.Add(new(idx++, checkTypeLabel, detail,
                    pass ? QAStatus.Pass : QAStatus.Fail, null,
                    pass ? RowHighlight.None : RowHighlight.Red));
            }
        }

        // Read MST property delta using _TransformView - needs only the MST path
        // Opens MST file itself via msiOpenDatabaseModePatchFile (32) to enumerate
        // the _TransformView entries which contain the raw delta Property rows
        // ── Read MST properties: binary string pool + table row parsing ─────
        // MST string pool = concatenated ASCII stream e.g. "ARPNOREPAIR1ROOTDRIVEC:\..."
        // StringPool stream (immediately after) = (length uint16, refcount uint16) per string
        // Property table rows = 0x01 0x02 + nameIdx uint16 + valueIdx uint16
        // This correctly handles index reuse (e.g. ALLUSERS reusing the "1" string index).

        // ══════════════════════════════════════════════════════════════════
        //  REGISTRY FLAGS (NoModify / NoRepair / NoRemove)
        // ══════════════════════════════════════════════════════════════════
        private void CheckRegistryFlags(ref int idx, string checkType,
            RegistryEntry entry, QAConfig qa, string registryKeyPath = "")
        {
            // Always read the flag values from the live registry so that values written
            // by Set-ADTRegistryKey (post-install) are picked up correctly.
            // The qasnap/entry values are captured pre-install and will be null/empty for
            // flags set during installation, causing false Fail results.
            string keyPath = !string.IsNullOrEmpty(registryKeyPath)
                ? registryKeyPath
                : entry.RegistryKeyPath;

            string? liveNoModify = null, liveNoRepair = null, liveNoRemove = null;
            try
            {
                if (!string.IsNullOrEmpty(keyPath))
                {
                    string[] pts   = keyPath.Split('\\');
                    string subKey  = string.Join("\\", pts.Skip(1));
                    var hive = pts[0].Contains("LOCAL_MACHINE")
                        ? Registry.LocalMachine : Registry.CurrentUser;
                    using var lk = hive.OpenSubKey(subKey);
                    if (lk != null)
                    {
                        liveNoModify = lk.GetValue("NoModify")?.ToString();
                        liveNoRepair = lk.GetValue("NoRepair")?.ToString();
                        liveNoRemove = lk.GetValue("NoRemove")?.ToString();
                    }
                }
            }
            catch { }

            var flags = new[]
            {
                ("NoModify", qa.NoModify, liveNoModify),
                ("NoRepair", qa.NoRepair, liveNoRepair),
                ("NoRemove", qa.NoRemove, liveNoRemove),
            };
            foreach (var (name, expected, rawActual) in flags)
            {
                // "Default" (or empty) means do not check this flag.
                if (string.IsNullOrEmpty(expected)
                    || expected.Trim().Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;

                // Distinguish three states:
                //   null / empty = value not present in live registry
                //   "0"          = explicitly set to 0
                //   "1"          = explicitly set to 1
                bool notSet = string.IsNullOrEmpty(rawActual);
                string? actual = notSet ? null : NormaliseDword(rawActual);

                bool match = !notSet &&
                    string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);

                string displayActual = notSet ? "(not set by installer)" : (actual ?? "0");
                string detail = match
                    ? $"{name} = {displayActual}"
                    : $"{name} = {displayActual}  (Expected value is {expected.Trim()})";

                string? openPath = string.IsNullOrEmpty(keyPath) ? null
                    : $"regval://{keyPath}?{name}";
                _results.Add(new(idx++, checkType, detail,
                    match ? QAStatus.Pass : QAStatus.Fail, null,
                    match ? RowHighlight.None : RowHighlight.Red, openPath));
            }
        }

        private static string? NormaliseDword(string? raw)
        {
            if (raw == null) return null;
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(trimmed.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out long v))
                    return v.ToString();
            }
            return trimmed;
        }

        // ══════════════════════════════════════════════════════════════════
        //  EXTRACT UNINSTALL COMMANDS
        // ══════════════════════════════════════════════════════════════════
        private static List<(string Cmd, int LineNo)> ExtractUninstallCommands(string script)
        {
            var fnMatch = Regex.Match(script,
                @"function\s+Uninstall-ADTDeployment\s*[\r\n]*\{([\s\S]*?)\n\}",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!fnMatch.Success) return new List<(string, int)>();
            string body = fnMatch.Groups[1].Value;
            int bodyStartLine = script.Substring(0, fnMatch.Groups[1].Index).Count(c => c == '\n') + 1;

            var commands = new List<(string, int)>();
            var lines    = body.Split('\n');
            var current  = new StringBuilder();
            int lineNo = 0;
            int currentLineNo = 0;
            bool unPastParam     = false;
            bool unSkipBlock     = false;
            int  unSkipBrace     = 0;
            foreach (var rawLine in lines)
            {
                lineNo++;
                string line    = rawLine.TrimEnd();
                string trimmed = line.TrimStart();

                // Skip param() / [CmdletBinding()] block at top of function
                if (!unPastParam)
                {
                    if (trimmed.StartsWith("[CmdletBinding") ||
                        trimmed.StartsWith("param") ||
                        trimmed == "(" || trimmed == ")")
                        continue;
                    unPastParam = true;
                }

                if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed)) continue;

                // Skip entire UseDefaultMsi / UseDefaultMsp boilerplate block
                if (!unSkipBlock &&
                    Regex.IsMatch(trimmed, @"if\s*\(\s*\$adtSession\.(UseDefaultMsi|DefaultMspFiles|DefaultMstFile)",
                        RegexOptions.IgnoreCase))
                { unSkipBlock = true; unSkipBrace = 0; }
                if (unSkipBlock)
                {
                    unSkipBrace += trimmed.Count(c => c == '{');
                    unSkipBrace -= trimmed.Count(c => c == '}');
                    if (unSkipBrace <= 0 && trimmed.Contains('}')) unSkipBlock = false;
                    continue;
                }
                // Skip standalone boilerplate lines
                if (Regex.IsMatch(trimmed, @"^\$ExecuteDefaultMSISplat", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^Start-ADTMsiProcess\s+@ExecuteDefaultMSISplat", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"\$adtSession\.DefaultMspFiles\s*\|\s*Start-ADTMsiProcess", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^\$adtSession\.InstallPhase\s*=", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^Show-ADTInstallation", RegexOptions.IgnoreCase))
                    continue;
                if (line.EndsWith("`"))
                { current.Append(line.TrimEnd('`').TrimEnd() + " "); currentLineNo = bodyStartLine + lineNo - 1; continue; }
                if (currentLineNo == 0) currentLineNo = bodyStartLine + lineNo - 1;
                current.Append(line);
                string full = current.ToString().Trim();
                current.Clear();
                if (Regex.IsMatch(full, @"(Start-ADTProcess\b|Start-ADTMsiProcess\b|Remove-ADTFile\b|Remove-ADTRegistryKey\b|Set-ADTRegistryKey\b|Copy-ADTFile\b)", RegexOptions.IgnoreCase))
                    commands.Add((full, currentLineNo));
                currentLineNo = 0;
            }
            if (current.Length > 0)
            {
                string full = current.ToString().Trim();
                if (Regex.IsMatch(full, @"(Start-ADTProcess\b|Start-ADTMsiProcess\b|Remove-ADTFile\b|Remove-ADTRegistryKey\b|Set-ADTRegistryKey\b|Copy-ADTFile\b)", RegexOptions.IgnoreCase))
                    commands.Add((full, bodyStartLine + lineNo - 1));
            }
            return commands;
        }

        // ══════════════════════════════════════════════════════════════════
        //  UNINSTALL MSI CHECK
        // ══════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════
        //  UNINSTALL MSI CHECK (log-driven)
        // ══════════════════════════════════════════════════════════════════
        private void CheckUninstallMsi(ref int idx, string resolved, string rawCmd,
            string filesDir, AdtLogInfo? adtLog = null, string cmdLabel = "Start-ADTMsiProcess")
        {
            string msiRaw     = ExtractParam(resolved, "-FilePath").Trim('"', '\'');
            string productCode = ExtractParam(resolved, "-ProductCode").Trim('"', '\'', '{', '}');
            if (!string.IsNullOrEmpty(productCode))
                productCode = "{" + productCode.Trim('{', '}') + "}";

            // Find the MSI run in ADT log (uninstall uses /x)
            AdtLogInfo.MsiRun? msiRun = adtLog?.MsiRuns.FirstOrDefault();
            bool msiRanOk = msiRun?.Success ?? true;

            // Parse uninstall verbose log for shortcuts + install location
            MsiVerboseLogInfo uninstallVerbose = new();
            if (!string.IsNullOrEmpty(msiRun?.VerboseLogPath))
                uninstallVerbose = ParseMsiVerboseLog(msiRun.VerboseLogPath);

            // Resolve product info from product code or MSI file
            string? resolvedProductCode = productCode;
            string? resolvedProductName = null;
            string? resolvedInstallLoc  = uninstallVerbose.InstallLocation;

            if (!string.IsNullOrWhiteSpace(msiRaw))
            {
                string msiPath = IOPath.IsPathRooted(msiRaw)
                    ? msiRaw : IOPath.Combine(filesDir.TrimEnd('\\', '/'), msiRaw);
                msiPath = ExpandEnv(msiPath);
                if (!File.Exists(msiPath))
                {
                    var found = Directory.Exists(filesDir)
                        ? Directory.GetFiles(filesDir, IOPath.GetFileName(msiRaw), SearchOption.AllDirectories).FirstOrDefault()
                        : null;
                    if (found != null) msiPath = found;
                }
                if (File.Exists(msiPath))
                {
                    var msi = ReadMsiInfo(msiPath);
                    if (msi != null)
                    {
                        resolvedProductCode = msi.ProductCode;
                        resolvedProductName = msi.ProductName;
                    }
                }
            }

            // ── Row 1: Command + exit code ────────────────────────────
            _results.Add(new(idx++, cmdLabel,
                string.IsNullOrEmpty(msiRaw) ? $"ProductCode: {productCode}" : ExpandEnv(msiRaw),
                msiRanOk ? QAStatus.Pass : QAStatus.Fail,
                msiRanOk ? null : $"Uninstall exited with code {msiRun?.ExitCode}"));

            // ── Row 2: Registry entry removed ─────────────────────────
            var regEntry = FindRegistryEntry(resolvedProductName, resolvedProductCode);
            bool regGone = regEntry == null;
            string appLabel = resolvedProductName ?? resolvedProductCode ?? "Application";
            _results.Add(new(idx++, cmdLabel,
                regGone ? $"Registry entry removed - {appLabel} is uninstalled"
                        : $"Registry entry still exists - {appLabel} is NOT uninstalled",
                regGone ? QAStatus.Pass : QAStatus.Fail));

            // ── Row 3: InstallLocation folder removed ─────────────────
            string installLoc = !string.IsNullOrEmpty(resolvedInstallLoc)
                ? resolvedInstallLoc : regEntry?.InstallLocation ?? "";
            if (!string.IsNullOrEmpty(installLoc))
            {
                bool locGone = !Directory.Exists(installLoc) && !File.Exists(installLoc);
                _results.Add(new(idx++, cmdLabel,
                    locGone ? $"InstallLocation removed: {installLoc}"
                            : $"InstallLocation still exists: {installLoc}",
                    locGone ? QAStatus.Pass : QAStatus.Fail,
                    null, RowHighlight.None, installLoc));
            }

            // ── Rows 4+: Shortcuts removed ────────────────────────────
            // Primary: uninstall verbose log Note:2318 (exact paths removed by MSI)
            var scPaths = uninstallVerbose.Shortcuts.Count > 0
                ? uninstallVerbose.Shortcuts
                : new List<string>();

            // Fallback: read from MSI file if verbose log has no shortcuts
            if (scPaths.Count == 0 && !string.IsNullOrWhiteSpace(msiRaw))
            {
                string msiPath = ExpandEnv(IOPath.IsPathRooted(msiRaw)
                    ? msiRaw : IOPath.Combine(filesDir.TrimEnd('\\', '/'), msiRaw));
                if (!File.Exists(msiPath))
                {
                    var found = Directory.Exists(filesDir)
                        ? Directory.GetFiles(filesDir, IOPath.GetFileName(msiRaw), SearchOption.AllDirectories).FirstOrDefault()
                        : null;
                    if (found != null) msiPath = found;
                }
                if (File.Exists(msiPath))
                {
                    var msi = ReadMsiInfo(msiPath);
                    if (msi != null)
                        foreach (var (scName, scDir, _) in msi.Shortcuts)
                        {
                            string dn = scName.Split('|')[0].Trim();
                            string scResolved = ResolveShortcutPath(dn, scDir, msi.ProductName ?? "");
                            if (!string.IsNullOrEmpty(scResolved)) scPaths.Add(scResolved);
                        }
                }
            }

            foreach (var scPath in scPaths)
            {
                bool scGone = !File.Exists(scPath);
                _results.Add(new(idx++, cmdLabel,
                    scGone ? $"Shortcut removed: {scPath}"
                           : $"Shortcut still exists: {scPath}  (Remove shortcut)",
                    scGone ? QAStatus.Pass : QAStatus.Fail,
                    null, RowHighlight.None, IOPath.GetDirectoryName(scPath)));
            }

            // ── Env vars removed (from uninstall verbose log) ─────────
            CheckEnvVarsRemoved(ref idx, uninstallVerbose, cmdLabel);

            // ── .svr-driven removal checks for MSI ─────────────────────
            // Load C:\Temp\<MsiName> - For QA.svr and verify everything captured was removed
            QaSnapshot? msiUnSnap = null;

            // ── Resolve the .svr for this uninstall ────────────────────────────────
            // Strategy 1: -FilePath gives the MSI filename directly → derive snapshot name
            // Strategy 2: -ProductCode only → look up OriginalDatabase/DisplayName in registry
            //             (works when app is still installed; fails when already uninstalled)
            // Strategy 3: Scan all *.svr files in C:\Temp and match by product code
            //             embedded in the snapshot's Registry entries - the only reliable
            //             strategy when the app has already been uninstalled and the registry
            //             entry is gone.
            string msiUnSnapName = !string.IsNullOrWhiteSpace(msiRaw)
                ? IOPath.GetFileNameWithoutExtension(ExpandEnv(msiRaw))
                : "";

            // Strategy 2: look up name via registry (app still installed path)
            if (string.IsNullOrEmpty(msiUnSnapName) && !string.IsNullOrEmpty(resolvedProductCode))
            {
                try
                {
                    string pcLookup = resolvedProductCode.Trim('{', '}').ToUpperInvariant();
                    foreach (var hivePath in new[] {
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    {
                        using var hiveKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(hivePath);
                        if (hiveKey == null) continue;
                        foreach (var sub in hiveKey.GetSubKeyNames())
                        {
                            if (!sub.Trim('{', '}').ToUpperInvariant().Equals(pcLookup)) continue;
                            using var entry = hiveKey.OpenSubKey(sub);
                            if (entry == null) continue;
                            string? origDb = entry.GetValue("OriginalDatabase") as string;
                            if (!string.IsNullOrEmpty(origDb))
                            { msiUnSnapName = IOPath.GetFileNameWithoutExtension(origDb); break; }
                            string? dn = entry.GetValue("DisplayName") as string;
                            if (!string.IsNullOrEmpty(dn))
                            { msiUnSnapName = dn; break; }
                        }
                        if (!string.IsNullOrEmpty(msiUnSnapName)) break;
                    }
                }
                catch { }
            }

            // Strategy 1 or 2 succeeded - search known paths by name
            if (!string.IsNullOrEmpty(msiUnSnapName))
            {
                try
                {
                    string msiUnFolder = !string.IsNullOrWhiteSpace(msiRaw)
                        ? IOPath.GetDirectoryName(ExpandEnv(
                            IOPath.IsPathRooted(msiRaw) ? msiRaw
                            : IOPath.Combine(filesDir.TrimEnd('\\', '/'), msiRaw))) ?? ""
                        : "";
                    string snapSuffix = msiUnSnapName + " - For QA.svr";
                    // Search only QAsnapPath (from QA.json/UI) then C:\Temp\Validate Source
                    string? msiUnSnapPath = FindQAsnapFile(msiUnSnapName);
                    if (msiUnSnapPath != null && !_usedUninstallSnapPaths.Contains(msiUnSnapPath))
                    {
                        string snapJson = File.ReadAllText(msiUnSnapPath);
                        var snap = DeserializeSnapshot(snapJson);
                        if (snap != null)
                        {
                            msiUnSnap = snap;
                            _usedUninstallSnapPaths.Add(msiUnSnapPath);
                        }
                    }
                }
                catch { }
            }

            // Strategy 3: scan all *.svr files in C:\Temp and match by product code
            // This is the key strategy for uninstall QA - by the time we run, the app is
            // already removed so the registry entry is gone, but the .svr captured
            // during Source Validation contains the product code GUID in its Registry array.
            if (msiUnSnap == null && !string.IsNullOrEmpty(resolvedProductCode))
            {
                try
                {
                    string pcSearch = resolvedProductCode.Trim('{', '}').ToUpperInvariant();
                    // Scan only QAsnapPath then C:\Temp\Validate Source
                    var allSnaps3 = AllQAsnapFiles();
                    foreach (var snapFile in allSnaps3)
                    {
                        try
                        {
                            // Guard: skip if already processed in this run
                            if (_usedUninstallSnapPaths.Contains(snapFile)) continue;

                            string json = File.ReadAllText(snapFile);
                            var candidate = DeserializeSnapshot(json);
                            if (candidate == null) continue;

                            // Match: any registry key path contains the product code GUID
                            bool matched = candidate.Registry.Any(r =>
                                r.Key.IndexOf(pcSearch, StringComparison.OrdinalIgnoreCase) >= 0);

                            // Also match via FilePath - the MSI path stored in the snapshot
                            if (!matched && !string.IsNullOrEmpty(candidate.FilePath))
                            {
                                string snapMsiName = IOPath.GetFileNameWithoutExtension(candidate.FilePath)
                                    .ToLowerInvariant();
                                matched = !string.IsNullOrEmpty(msiUnSnapName) &&
                                          snapMsiName.Equals(msiUnSnapName, StringComparison.OrdinalIgnoreCase);
                            }

                            if (matched)
                            {
                                msiUnSnap = candidate;
                                _usedUninstallSnapPaths.Add(snapFile);
                                msiUnSnapName = IOPath.GetFileNameWithoutExtension(
                                    IOPath.GetFileNameWithoutExtension(snapFile)); // strip " - For QA"
                                break;
                            }
                        }
                        catch { }
                        if (msiUnSnap != null) break;
                    }
                }
                catch { }
            }

            if (msiUnSnap != null)
            {
                // U-F1) All captured files removed - verify every file is gone from the machine
                foreach (var filePath in msiUnSnap.Files)
                {
                    bool fileGone = !File.Exists(filePath);
                    _results.Add(new(idx++, cmdLabel + " · File",
                        fileGone ? $"File removed: {filePath}"
                                 : $"{filePath}  (File still exists on machine)",
                        fileGone ? QAStatus.Pass : QAStatus.Fail,
                        fileGone ? null : "File was not removed during uninstall",
                        RowHighlight.None, IOPath.GetDirectoryName(filePath)));
                }

                // U-F2) Folders removed
                foreach (var folder in msiUnSnap.Folders)
                {
                    if (IsSharedSystemFolder(folder)) continue;
                    bool folderGone = !Directory.Exists(folder);
                    _results.Add(new(idx++, cmdLabel + " · Folder",
                        folderGone ? $"Folder removed: {folder}"
                                   : $"{folder}  (Folder still exists on machine)",
                        folderGone ? QAStatus.Pass : QAStatus.Fail,
                        folderGone ? null : "Folder was not removed during uninstall",
                        RowHighlight.None, folder));
                }

                // U-F3) Shortcuts removed
                foreach (var scPath in msiUnSnap.Shortcuts)
                {
                    bool scGone = !File.Exists(scPath);
                    _results.Add(new(idx++, cmdLabel + " · Shortcut",
                        scGone ? $"Shortcut removed: {scPath}"
                               : $"{scPath}  (Shortcut still exists)",
                        scGone ? QAStatus.Pass : QAStatus.Fail,
                        scGone ? null : "Shortcut was not removed during uninstall",
                        RowHighlight.None, IOPath.GetDirectoryName(scPath)));
                }

                // U-F4) Registry keys removed - all captured keys including Uninstall
                foreach (var sr in msiUnSnap.Registry)
                {
                    bool regGoneSnap = false;
                    try
                    {
                        string[] pts  = sr.Key.Split('\\');
                        string subKey = string.Join("\\", pts.Skip(1));
                        var hive = pts[0].Contains("LOCAL_MACHINE")
                            ? Microsoft.Win32.Registry.LocalMachine
                            : Microsoft.Win32.Registry.CurrentUser;
                        using var lk = hive.OpenSubKey(subKey);
                        if (lk == null) regGoneSnap = true;
                        else if (string.IsNullOrEmpty(sr.Value)) regGoneSnap = false;
                        else regGoneSnap = lk.GetValue(sr.Value) == null;
                    }
                    catch { regGoneSnap = true; }
                    _results.Add(new(idx++, cmdLabel + " · Registry",
                        regGoneSnap ? $"Registry removed: [{sr.Key}]  {sr.Value}"
                                    : $"[{sr.Key}]  {sr.Value} = {sr.Data}  (Registry still exists)",
                        regGoneSnap ? QAStatus.Pass : QAStatus.Fail,
                        regGoneSnap ? null : "Registry key/value was not removed during uninstall",
                        RowHighlight.None, string.IsNullOrEmpty(sr.Value) ? "reg://" + sr.Key : "regval://" + sr.Key + "?" + sr.Value));
                }

                // U-F5) Environment variables removed
                foreach (var ev in msiUnSnap.EnvVars)
                {
                    string? curVal = Environment.GetEnvironmentVariable(ev.Name, EnvironmentVariableTarget.Machine)
                                  ?? Environment.GetEnvironmentVariable(ev.Name, EnvironmentVariableTarget.User);
                    bool evGone = string.IsNullOrEmpty(curVal);
                    if (!evGone && !string.IsNullOrEmpty(ev.Value))
                    {
                        string sep2 = string.IsNullOrEmpty(ev.Delimiter) ? ";" : ev.Delimiter;
                        bool stillContains = curVal!.Split(new[] { sep2 }, StringSplitOptions.RemoveEmptyEntries)
                            .Any(p => string.Equals(p.Trim().TrimEnd('\\', '/'),
                                ev.Value.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                        evGone = !stillContains;
                    }
                    _results.Add(new(idx++, cmdLabel + " · EnvVar",
                        evGone ? $"EnvVar removed: {ev.Name}"
                               : $"{ev.Name} = {ev.Value}  (Environment variable still exists)",
                        evGone ? QAStatus.Pass : QAStatus.Fail,
                        evGone ? null : "Environment variable was not removed during uninstall",
                        RowHighlight.None, "envvar://"));
                }

                // U-F6) Services removed
                foreach (var svc in msiUnSnap.Services)
                {
                    bool svcGone = false;
                    try
                    {
                        using var sc2 = new System.ServiceProcess.ServiceController(svc.Name);
                        _ = sc2.DisplayName;
                        svcGone = false;
                    }
                    catch { svcGone = true; }
                    _results.Add(new(idx++, cmdLabel + " · Service",
                        svcGone ? $"Service removed: {svc.DisplayName}"
                                : $"{svc.DisplayName}  |  {svc.Name}  (Service still exists)",
                        svcGone ? QAStatus.Pass : QAStatus.Fail,
                        svcGone ? null : $"Service '{svc.Name}' was not removed during uninstall",
                        RowHighlight.None, "service://"));
                }

                // U-F7) Certificates removed
                foreach (var cert in msiUnSnap.Certificates)
                {
                    bool certGone = true;
                    try
                    {
                        foreach (var storeName in new[] {
                            System.Security.Cryptography.X509Certificates.StoreName.My,
                            System.Security.Cryptography.X509Certificates.StoreName.Root,
                            System.Security.Cryptography.X509Certificates.StoreName.TrustedPublisher })
                        {
                            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                                storeName,
                                System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                            if (store.Certificates
                                .OfType<System.Security.Cryptography.X509Certificates.X509Certificate2>()
                                .Any(c => c.Subject.IndexOf(cert, StringComparison.OrdinalIgnoreCase) >= 0
                                       || c.Thumbprint.Equals(cert, StringComparison.OrdinalIgnoreCase)
                                       || c.FriendlyName.IndexOf(cert, StringComparison.OrdinalIgnoreCase) >= 0))
                            { certGone = false; break; }
                        }
                    }
                    catch { }
                    _results.Add(new(idx++, cmdLabel + " · Certificate",
                        certGone ? $"Certificate removed: {cert}"
                                 : $"{cert}  (Certificate still exists in store)",
                        certGone ? QAStatus.Pass : QAStatus.Fail,
                        certGone ? null : "Certificate was not removed during uninstall",
                        RowHighlight.None, "cert://"));
                }

                // U-F8) Firewall rules removed
                foreach (var fwRule in msiUnSnap.Firewall)
                {
                    bool ruleGone = true;
                    try
                    {
                        var netFwType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                        if (netFwType != null)
                        {
                            dynamic fwPolicy = Activator.CreateInstance(netFwType)!;
                            foreach (dynamic rule in fwPolicy.Rules)
                            {
                                if (string.Equals(rule.Name as string, fwRule, StringComparison.OrdinalIgnoreCase))
                                { ruleGone = false; break; }
                            }
                        }
                    }
                    catch { }
                    _results.Add(new(idx++, cmdLabel + " · Firewall",
                        ruleGone ? $"Firewall rule removed: {fwRule}"
                                 : $"{fwRule}  (Firewall rule still exists)",
                        ruleGone ? QAStatus.Pass : QAStatus.Fail,
                        ruleGone ? null : "Firewall rule was not removed during uninstall",
                        RowHighlight.None, "firewall://"));
                }
            }
            else if (!string.IsNullOrEmpty(msiUnSnapName))
            {
                _results.Add(new(idx++, cmdLabel + " · Snapshot",
                    $"No .svr found for {msiUnSnapName}",
                    QAStatus.Skip,
                    $"Run Source Validation capture on {msiUnSnapName} to enable deep removal checks"));
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  UNINSTALL EXE CHECK
        // ══════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════
        //  REMOVE-ADTFILE CHECK (uninstall)
        // ══════════════════════════════════════════════════════════════════
        private void CheckRemoveFile(ref int idx, string resolved, string rawCmd,
            AdtLogInfo? adtLog, string cmdLabel = "Remove-ADTFile")
        {
            string pathRaw = ExtractParam(resolved, "-Path").Trim('\'', '"');
            if (string.IsNullOrEmpty(pathRaw))
            {
                _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -Path"));
                return;
            }
            string pathAbs = ExpandEnv(pathRaw);
            bool gone = !File.Exists(pathAbs) && !Directory.Exists(pathAbs);
            _results.Add(new(idx++, cmdLabel,
                gone ? $"Removed: {pathAbs}" : $"Still exists: {pathAbs}  (Not removed)",
                gone ? QAStatus.Pass : QAStatus.Fail,
                null, RowHighlight.None, gone ? null : pathAbs));
        }

        // ══════════════════════════════════════════════════════════════════
        //  REMOVE-ADTREGISTRYKEY CHECK (uninstall)
        // ══════════════════════════════════════════════════════════════════
        private void CheckRemoveRegistry(ref int idx, string resolved, string rawCmd,
            AdtLogInfo? adtLog, string cmdLabel = "Remove-ADTRegistryKey")
        {
            string keyPath = ExtractParam(resolved, "-LiteralPath").Trim('"', '\'');
            if (string.IsNullOrEmpty(keyPath))
                keyPath = ExtractParam(resolved, "-Path").Trim('"', '\'');
            keyPath = Regex.Replace(keyPath, @"^Microsoft\.PowerShell\.Core\\Registry::", "",
                RegexOptions.IgnoreCase);
            string name = ExtractParam(resolved, "-Name").Trim('"', '\'');

            bool gone = false;
            try
            {
                string hiveStr = keyPath.Split('\\')[0];
                string subKey  = string.Join("\\", keyPath.Split('\\').Skip(1));
                var hive = hiveStr.Contains("LOCAL_MACHINE") ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = hive.OpenSubKey(subKey);
                if (key == null)
                    gone = true; // entire key removed
                else if (!string.IsNullOrEmpty(name))
                    gone = key.GetValue(name) == null; // specific value removed
                else
                    gone = true;
            }
            catch { gone = true; }

            string detail = string.IsNullOrEmpty(name) ? keyPath : $"{keyPath}  [{name}]";
            _results.Add(new(idx++, cmdLabel,
                gone ? $"Registry removed: {detail}"
                     : $"Registry still exists: {detail}  (Not removed)",
                gone ? QAStatus.Pass : QAStatus.Fail,
                null, RowHighlight.None,
                string.IsNullOrEmpty(keyPath) ? null : $"reg://{keyPath}"));
        }

        // ══════════════════════════════════════════════════════════════════
        //  NEW-ADTFOLDER / REMOVE-ADTFOLDER
        // ══════════════════════════════════════════════════════════════════
        /// <summary>
        /// Scans Invoke-AppDeployToolkit.ps1 for hardcoded absolute drive paths
        /// (e.g. C:\, D:\, E:\) in Install-ADTDeployment and Uninstall-ADTDeployment
        /// functions. Reports each matching line as a Fail row.
        /// Excludes: ADT variable references ($adtSession, $env:, etc.),
        /// comments, and legitimate pattern strings inside strings that reference
        /// PS variables.
        /// </summary>
        private void CheckHardcodedPaths(ref int idx, string scriptPath, string scriptContent)
        {
            // Extract only the Install/Uninstall function bodies to avoid false positives
            // from $adtSession assignments at the top of the script
            var functionBodies = new List<(string body, int startLine)>();
            foreach (var funcPattern in new[]
            {
                @"function\s+Install-ADTDeployment\s*[\r\n]*\{([\s\S]*?)\n\}",
                @"function\s+Uninstall-ADTDeployment\s*[\r\n]*\{([\s\S]*?)\n\}",
            })
            {
                var fm = Regex.Match(scriptContent, funcPattern, RegexOptions.IgnoreCase);
                if (!fm.Success) continue;
                // Find the line number of the function start in the full script
                string before = scriptContent.Substring(0, fm.Groups[1].Index);
                int startLine = before.Count(c => c == '\n') + 1;
                functionBodies.Add((fm.Groups[1].Value, startLine));
            }

            if (functionBodies.Count == 0) return;

            // Regex: hardcoded drive letter path - letter colon backslash, not preceded by $
            // Excludes PS variable paths like $env:SystemDrive\, $($adtSession.DirFiles)\
            var hardcodeRx = new Regex(
                @"(?<!\$[^\s\\]*)[A-Za-z]:\\",
                RegexOptions.None);

            foreach (var (body, startLineOffset) in functionBodies)
            {
                var lines = body.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string trimmed = line.TrimStart();

                    // Skip blank lines and comment lines
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    if (trimmed.StartsWith("#")) continue;

                    // Skip lines that only have PS variable-based paths
                    // e.g. "$($adtSession.DirFiles)\something" - no bare drive letter
                    // Check if line contains a hardcoded path NOT preceded by $( or $env:
                    if (!hardcodeRx.IsMatch(line)) continue;

                    // Additional filter: skip if the drive letter match is inside a PS variable
                    // e.g. "$env:SystemDrive\" or "$($adtSession.DirFiles)\"
                    // We check each match individually
                    bool hasTrueHardcode = false;
                    foreach (Match m in hardcodeRx.Matches(line))
                    {
                        int pos = m.Index;
                        // Look back up to 30 chars for a $ that would make this a variable
                        int lookback = Math.Max(0, pos - 30);
                        string before = line.Substring(lookback, pos - lookback);
                        // If preceded by $( ... ) variable expression or $env: - skip
                        if (Regex.IsMatch(before, @"\$\([^)]*$") ||
                            Regex.IsMatch(before, @"\$env:$", RegexOptions.IgnoreCase) ||
                            Regex.IsMatch(before, @"\$\w+$"))
                            continue;
                        hasTrueHardcode = true;
                        break;
                    }
                    if (!hasTrueHardcode) continue;

                    int lineNo = startLineOffset + i;
                    string lineLabel = $"Hardcode Path ({lineNo})";
                    string lineText = trimmed.Length > 200 ? trimmed.Substring(0, 200) + "…" : trimmed;
                    _results.Add(new(idx++, lineLabel,
                        $"{lineText}  (Hardcoded drive path found)",
                        QAStatus.Fail,
                        null,
                        RowHighlight.None, scriptPath));  // opens Invoke-AppDeployToolkit.ps1
                }
            }
        }

        private void CheckNewFolder(ref int idx, string resolved, string cmdLabel = "New-ADTFolder")
        {
            string path = ExtractParam(resolved, "-LiteralPath").Trim('"', '\'');
            if (string.IsNullOrEmpty(path)) path = ExtractParam(resolved, "-Path").Trim('"', '\'');
            path = ExpandEnv(path);
            if (string.IsNullOrEmpty(path)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -LiteralPath")); return; }
            if (IsSharedSystemFolder(path)) return;
            bool exists = Directory.Exists(path);
            _results.Add(new(idx++, cmdLabel, path,
                exists ? QAStatus.Pass : QAStatus.Fail,
                exists ? null : "Folder was not created on this machine",
                RowHighlight.None, exists ? path : null));
        }

        /// <summary>
        /// Returns true if the folder is a Windows system/shared folder that installers
        /// write into but never fully own - skip these in uninstall folder-removal checks.
        /// </summary>
        private static bool IsSharedSystemFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string norm = Environment.ExpandEnvironmentVariables(folder)
                              .TrimEnd('\\', '/')
                              .ToLowerInvariant();
            // Exact matches for well-known shared/system folders
            var systemFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"c:\programdata\microsoft\windows\start menu\programs",
                @"c:\programdata\microsoft\windows\start menu",
                @"c:\programdata\microsoft\windows\start menu\startup",
                @"c:\users\public\desktop",
                @"c:\users\public\documents",
                @"c:\users\public",
                @"c:\program files",
                @"c:\program files (x86)",
                @"c:\programdata",
                @"c:\windows",
                @"c:\windows\system32",
                @"c:\windows\syswow64",
                @"c:\temp",
                @"c:\windows\temp",
            };
            return systemFolders.Contains(norm);
        }

        private void CheckRemoveFolder(ref int idx, string resolved, string cmdLabel = "Remove-ADTFolder")
        {
            string path = ExtractParam(resolved, "-LiteralPath").Trim('"', '\'');
            if (string.IsNullOrEmpty(path)) path = ExtractParam(resolved, "-Path").Trim('"', '\'');
            path = ExpandEnv(path);
            if (string.IsNullOrEmpty(path)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse path")); return; }
            if (IsSharedSystemFolder(path)) return;
            bool gone = !Directory.Exists(path);
            _results.Add(new(idx++, cmdLabel,
                gone ? $"Folder removed: {path}" : $"{path}  (Folder still exists - not removed)",
                gone ? QAStatus.Pass : QAStatus.Fail,
                gone ? null : "Folder was not removed during uninstall",
                RowHighlight.None, gone ? null : path));
        }

        // ══════════════════════════════════════════════════════════════════
        //  NEW-ADTSHORTCUT / SET-ADTSHORTCUT (install)
        // ══════════════════════════════════════════════════════════════════
        private void CheckNewShortcut(ref int idx, string resolved, string cmdLabel = "New-ADTShortcut")
        {
            string lnkPath = ExtractParam(resolved, "-LiteralPath").Trim('"', '\'');
            if (string.IsNullOrEmpty(lnkPath)) lnkPath = ExtractParam(resolved, "-Path").Trim('"', '\'');
            lnkPath = ExpandEnv(lnkPath);
            string target  = ExpandEnv(ExtractParam(resolved, "-TargetPath").Trim('"', '\''));
            if (string.IsNullOrEmpty(lnkPath)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -LiteralPath")); return; }

            bool exists = File.Exists(lnkPath);
            string detail = string.IsNullOrEmpty(target) ? lnkPath : $"{lnkPath}  →  {target}";
            _results.Add(new(idx++, cmdLabel, detail,
                exists ? QAStatus.Pass : QAStatus.Fail,
                exists ? null : "Shortcut not found on this machine",
                RowHighlight.None, exists ? IOPath.GetDirectoryName(lnkPath) : null));
        }

        // ══════════════════════════════════════════════════════════════════
        //  SET-ADTENVIRONMENTVARIABLE (install) / REMOVE-ADTENVIRONMENTVARIABLE (uninstall)
        // ══════════════════════════════════════════════════════════════════
        private void CheckSetEnvVar(ref int idx, string resolved, string cmdLabel = "Set-ADTEnvironmentVariable")
        {
            string varName  = ExtractParam(resolved, "-Variable").Trim('"', '\'');
            string varValue = ExtractParam(resolved, "-Value").Trim('"', '\'');
            if (string.IsNullOrEmpty(varName)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -Variable")); return; }
            string? cur = Environment.GetEnvironmentVariable(varName, EnvironmentVariableTarget.Machine)
                       ?? Environment.GetEnvironmentVariable(varName, EnvironmentVariableTarget.User);
            bool exists = !string.IsNullOrEmpty(cur);
            bool valueMatch = string.IsNullOrEmpty(varValue) || string.Equals(cur, varValue, StringComparison.OrdinalIgnoreCase);
            QAStatus status = (exists && valueMatch) ? QAStatus.Pass : QAStatus.Fail;
            string detail = string.IsNullOrEmpty(varValue) ? varName : $"{varName} = {varValue}";
            if (exists && !valueMatch) detail += $"  (Current value: {cur})";
            _results.Add(new(idx++, cmdLabel, detail, status,
                status == QAStatus.Pass ? null : "Environment variable not set or value mismatch",
                RowHighlight.None, "envvar://"));
        }

        private void CheckRemoveEnvVar(ref int idx, string resolved, string cmdLabel = "Remove-ADTEnvironmentVariable")
        {
            string varName = ExtractParam(resolved, "-Variable").Trim('"', '\'');
            if (string.IsNullOrEmpty(varName)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -Variable")); return; }
            string? cur = Environment.GetEnvironmentVariable(varName, EnvironmentVariableTarget.Machine)
                       ?? Environment.GetEnvironmentVariable(varName, EnvironmentVariableTarget.User);
            bool gone = string.IsNullOrEmpty(cur);
            _results.Add(new(idx++, cmdLabel,
                gone ? $"EnvVar removed: {varName}" : $"{varName} = {cur}  (Environment variable still exists)",
                gone ? QAStatus.Pass : QAStatus.Fail,
                gone ? null : "Environment variable was not removed",
                RowHighlight.None, "envvar://"));
        }

        // ══════════════════════════════════════════════════════════════════
        //  START-ADTSERVICEANDDEPENDENCIES / STOP-ADTSERVICEANDDEPENDENCIES
        // ══════════════════════════════════════════════════════════════════
        private void CheckStartService(ref int idx, string resolved, string cmdLabel = "Start-ADTServiceAndDependencies")
        {
            string svcName = ExtractParam(resolved, "-Name").Trim('"', '\'');
            if (string.IsNullOrEmpty(svcName)) svcName = ExtractParam(resolved, "-Service").Trim('"', '\'');
            if (string.IsNullOrEmpty(svcName)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -Name")); return; }
            bool running = false; string status = ""; string displayName = svcName;
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(svcName);
                displayName = sc.DisplayName;
                status = sc.Status.ToString();
                running = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            }
            catch { }
            _results.Add(new(idx++, cmdLabel, $"{displayName} ({svcName})  |  Status: {(string.IsNullOrEmpty(status) ? "Not found" : status)}",
                running ? QAStatus.Pass : QAStatus.Fail,
                running ? null : string.IsNullOrEmpty(status) ? "Service not found on this machine" : $"Service is {status}, expected Running",
                RowHighlight.None, "service://"));
        }

        private void CheckStopService(ref int idx, string resolved, string cmdLabel = "Stop-ADTServiceAndDependencies")
        {
            string svcName = ExtractParam(resolved, "-Name").Trim('"', '\'');
            if (string.IsNullOrEmpty(svcName)) svcName = ExtractParam(resolved, "-Service").Trim('"', '\'');
            if (string.IsNullOrEmpty(svcName)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -Name")); return; }
            bool stopped = false; string status = ""; string displayName = svcName;
            try
            {
                using var sc = new System.ServiceProcess.ServiceController(svcName);
                displayName = sc.DisplayName;
                status = sc.Status.ToString();
                stopped = sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped;
            }
            catch { stopped = true; } // service not found = already gone = pass for uninstall
            _results.Add(new(idx++, cmdLabel, $"{displayName} ({svcName})  |  Status: {(string.IsNullOrEmpty(status) ? "Not found / Removed" : status)}",
                stopped ? QAStatus.Pass : QAStatus.Fail,
                stopped ? null : $"Service is {status}, expected Stopped",
                RowHighlight.None, !stopped ? "service://" : null));
        }

        // ══════════════════════════════════════════════════════════════════
        //  REGISTER-ADTDLL / UNREGISTER-ADTDLL / INVOKE-ADTREGSVR32
        // ══════════════════════════════════════════════════════════════════
        private void CheckRegisterDll(ref int idx, string resolved, string cmdLabel = "Register-ADTDll")
        {
            string filePath = ExpandEnv(ExtractParam(resolved, "-FilePath").Trim('"', '\''));
            if (string.IsNullOrEmpty(filePath)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -FilePath")); return; }
            bool exists = File.Exists(filePath);
            _results.Add(new(idx++, cmdLabel, filePath,
                exists ? QAStatus.Pass : QAStatus.Fail,
                exists ? null : "DLL file not found on this machine - may not be registered",
                RowHighlight.None, exists ? IOPath.GetDirectoryName(filePath) : null));
        }

        private void CheckUnregisterDll(ref int idx, string resolved, string cmdLabel = "Unregister-ADTDll")
        {
            string filePath = ExpandEnv(ExtractParam(resolved, "-FilePath").Trim('"', '\''));
            if (string.IsNullOrEmpty(filePath)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -FilePath")); return; }
            bool gone = !File.Exists(filePath);
            _results.Add(new(idx++, cmdLabel,
                gone ? $"DLL removed: {filePath}" : $"{filePath}  (DLL still exists on machine)",
                gone ? QAStatus.Pass : QAStatus.Fail,
                gone ? null : "DLL file was not removed - may still be registered",
                RowHighlight.None, IOPath.GetDirectoryName(filePath)));
        }

        // ══════════════════════════════════════════════════════════════════
        //  SET-ADTINIVALUE (install) / REMOVE-ADTINIVALUE (uninstall)
        // ══════════════════════════════════════════════════════════════════
        private void CheckSetIniValue(ref int idx, string resolved, string cmdLabel = "Set-ADTIniValue")
        {
            string filePath = ExpandEnv(ExtractParam(resolved, "-FilePath").Trim('"', '\''));
            string section  = ExtractParam(resolved, "-Section").Trim('"', '\'');
            string key      = ExtractParam(resolved, "-Key").Trim('"', '\'');
            string value    = ExtractParam(resolved, "-Value").Trim('"', '\'');
            if (string.IsNullOrEmpty(filePath)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -FilePath")); return; }
            bool fileExists = File.Exists(filePath);
            string actualValue = "";
            bool valueMatch = false;
            if (fileExists && !string.IsNullOrEmpty(section) && !string.IsNullOrEmpty(key))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(filePath))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase)) { /* found section */ }
                        var kvMatch = Regex.Match(trimmed, @"^([^=]+)=(.*)$");
                        if (kvMatch.Success && kvMatch.Groups[1].Value.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                        { actualValue = kvMatch.Groups[2].Value.Trim(); break; }
                    }
                    valueMatch = string.IsNullOrEmpty(value) || string.Equals(actualValue, value, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }
            string detail = $"{filePath}  [{section}]  {key} = {value}";
            if (fileExists && !string.IsNullOrEmpty(actualValue) && !valueMatch)
                detail += $"  (Current: {actualValue})";
            QAStatus status = fileExists && (string.IsNullOrEmpty(key) || valueMatch) ? QAStatus.Pass : QAStatus.Fail;
            _results.Add(new(idx++, cmdLabel, detail, status,
                !fileExists ? "INI file not found on this machine" : !valueMatch ? "INI key value does not match expected" : null,
                RowHighlight.None, IOPath.GetDirectoryName(filePath)));
        }

        private void CheckRemoveIniValue(ref int idx, string resolved, string cmdLabel = "Remove-ADTIniValue")
        {
            string filePath = ExpandEnv(ExtractParam(resolved, "-FilePath").Trim('"', '\''));
            string section  = ExtractParam(resolved, "-Section").Trim('"', '\'');
            string key      = ExtractParam(resolved, "-Key").Trim('"', '\'');
            if (string.IsNullOrEmpty(filePath)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -FilePath")); return; }
            // If key omitted, entire section should be removed; if key present, just that key
            bool gone = false;
            if (!File.Exists(filePath)) { gone = true; } // file gone = all contents gone
            else
            {
                try
                {
                    var lines = File.ReadAllLines(filePath);
                    bool inSection = false;
                    bool keyFound = false;
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("[")) inSection = trimmed.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
                        if (inSection && string.IsNullOrEmpty(key) && !trimmed.StartsWith("[") && !string.IsNullOrEmpty(trimmed))
                        { keyFound = true; break; } // section still has content
                        if (inSection && !string.IsNullOrEmpty(key))
                        {
                            var m = Regex.Match(trimmed, @"^([^=]+)=");
                            if (m.Success && m.Groups[1].Value.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                            { keyFound = true; break; }
                        }
                    }
                    gone = !keyFound;
                }
                catch { }
            }
            string detail = string.IsNullOrEmpty(key) ? $"{filePath}  [{section}]" : $"{filePath}  [{section}]  {key}";
            _results.Add(new(idx++, cmdLabel,
                gone ? $"INI entry removed: {detail}" : $"{detail}  (INI entry still exists)",
                gone ? QAStatus.Pass : QAStatus.Fail,
                gone ? null : "INI entry was not removed",
                RowHighlight.None, IOPath.GetDirectoryName(filePath)));
        }

        // ══════════════════════════════════════════════════════════════════
        //  COPY-ADTFILETOUSERPROFILES (install) / REMOVE-ADTFILEFROMUSERPROFILES (uninstall)
        // ══════════════════════════════════════════════════════════════════
        private void CheckCopyFileToUserProfiles(ref int idx, string resolved, string cmdLabel = "Copy-ADTFileToUserProfiles")
        {
            string srcPath  = ExtractParam(resolved, "-Path").Trim('"', '\'');
            string destRel  = ExtractParam(resolved, "-Destination").Trim('"', '\'');
            if (string.IsNullOrEmpty(srcPath)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -Path")); return; }
            string srcName = IOPath.GetFileName(ExpandEnv(srcPath));
            // Check at least one user profile contains the file
            string userProfiles = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string usersRoot = IOPath.GetDirectoryName(userProfiles) ?? @"C:\Users";
            bool foundAny = false;
            try
            {
                foreach (var profileDir in Directory.GetDirectories(usersRoot))
                {
                    string checkPath = string.IsNullOrEmpty(destRel)
                        ? IOPath.Combine(profileDir, srcName)
                        : IOPath.Combine(profileDir, destRel.TrimStart('\\', '/'), srcName);
                    if (File.Exists(checkPath)) { foundAny = true; break; }
                }
            }
            catch { }
            string detail = string.IsNullOrEmpty(destRel)
                ? $"{srcName}  →  [UserProfiles]"
                : $"{srcName}  →  [UserProfiles]\\{destRel}";
            _results.Add(new(idx++, cmdLabel, detail,
                foundAny ? QAStatus.Pass : QAStatus.Fail,
                foundAny ? null : "File not found in any user profile on this machine"));
        }

        private void CheckRemoveFileFromUserProfiles(ref int idx, string resolved, string cmdLabel = "Remove-ADTFileFromUserProfiles")
        {
            string srcPath = ExtractParam(resolved, "-Path").Trim('"', '\'');
            if (string.IsNullOrEmpty(srcPath)) srcPath = ExtractParam(resolved, "-LiteralPath").Trim('"', '\'');
            if (string.IsNullOrEmpty(srcPath)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -Path")); return; }
            string srcName = IOPath.GetFileName(ExpandEnv(srcPath));
            string usersRoot = IOPath.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users";
            bool anyRemains = false;
            try
            {
                foreach (var profileDir in Directory.GetDirectories(usersRoot))
                {
                    string checkPath = IOPath.Combine(profileDir, srcPath.TrimStart('\\', '/'));
                    if (File.Exists(checkPath)) { anyRemains = true; break; }
                }
            }
            catch { }
            _results.Add(new(idx++, cmdLabel,
                !anyRemains ? $"File removed from user profiles: {srcName}"
                            : $"{srcName}  (File still exists in one or more user profiles)",
                !anyRemains ? QAStatus.Pass : QAStatus.Fail,
                !anyRemains ? null : "File was not removed from all user profiles"));
        }

        // ══════════════════════════════════════════════════════════════════
        //  SET-ADTACTIVESETUP
        // ══════════════════════════════════════════════════════════════════
        private void CheckSetActiveSetup(ref int idx, string resolved, string cmdLabel = "Set-ADTActiveSetup")
        {
            string key  = ExtractParam(resolved, "-Key").Trim('"', '\'');
            string stub = ExpandEnv(ExtractParam(resolved, "-StubExePath").Trim('"', '\''));
            bool purge  = resolved.IndexOf("-PurgeActiveSetupKey", StringComparison.OrdinalIgnoreCase) >= 0;
            bool disable = resolved.IndexOf("-DisableActiveSetup", StringComparison.OrdinalIgnoreCase) >= 0;

            if (purge)
            {
                // PurgeActiveSetupKey: entry should be removed
                bool gone = true;
                string regPath = $@"SOFTWARE\Microsoft\Active Setup\Installed Components\{key}";
                try { using var k = Registry.LocalMachine.OpenSubKey(regPath); gone = k == null; } catch { }
                _results.Add(new(idx++, cmdLabel,
                    gone ? $"Active Setup key purged: {key}" : $"Active Setup key still exists: {regPath}",
                    gone ? QAStatus.Pass : QAStatus.Fail,
                    gone ? null : "Active Setup registry key was not removed"));
                return;
            }

            // Standard: verify HKLM Active Setup entry exists
            string activeSetupPath = $@"SOFTWARE\Microsoft\Active Setup\Installed Components\{key}";
            bool entryExists = false; string stubInReg = "";
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(activeSetupPath);
                entryExists = k != null;
                stubInReg = k?.GetValue("StubPath") as string ?? "";
            }
            catch { }

            bool stubOk = string.IsNullOrEmpty(stub) || File.Exists(stub);
            string detail = $"[HKLM\\{activeSetupPath}]";
            if (!string.IsNullOrEmpty(stub)) detail += $"  StubPath: {stub}";
            if (disable) detail += "  (Disabled)";

            QAStatus status = entryExists && stubOk ? QAStatus.Pass : QAStatus.Fail;
            _results.Add(new(idx++, cmdLabel, detail, status,
                !entryExists ? "Active Setup registry entry not found"
                : !stubOk ? $"StubPath executable not found: {stub}" : null,
                RowHighlight.None, entryExists ? $"reg://HKEY_LOCAL_MACHINE\\{activeSetupPath}" : null));
        }

        // ══════════════════════════════════════════════════════════════════
        //  UNINSTALL-ADTAPPLICATION / GET-ADTAPPLICATION (uninstall)
        // ══════════════════════════════════════════════════════════════════
        private void CheckUninstallApplication(ref int idx, string resolved, string cmdLabel = "Uninstall-ADTApplication")
        {
            string appName = ExtractParam(resolved, "-Name").Trim('"', '\'');
            string prodCode = ExtractParam(resolved, "-ProductCode").Trim('"', '\'', '{', '}');
            if (!string.IsNullOrEmpty(prodCode)) prodCode = "{" + prodCode + "}";

            // Application should be GONE after uninstall
            var regEntry = FindRegistryEntry(
                string.IsNullOrEmpty(appName) ? null : appName,
                string.IsNullOrEmpty(prodCode) ? null : prodCode);
            bool gone = regEntry == null;
            string label = regEntry?.DisplayName ?? appName ?? prodCode ?? "Application";
            _results.Add(new(idx++, cmdLabel,
                gone ? $"Application removed: {label}"
                     : $"{label}  still installed  ({regEntry!.RegistryKeyPath})",
                gone ? QAStatus.Pass : QAStatus.Fail,
                gone ? null : "Application uninstall registry entry still exists",
                RowHighlight.None, regEntry != null ? "reg://" + regEntry!.RegistryKeyPath : null));
        }

        // ══════════════════════════════════════════════════════════════════
        //  START-ADTMSPPROCESS (install)
        // ══════════════════════════════════════════════════════════════════
        private void CheckStartMsp(ref int idx, string resolved, string filesDir, string cmdLabel = "Start-ADTMspProcess")
        {
            string mspRaw = ExtractParam(resolved, "-FilePath").Trim('"', '\'');
            string mspPath = IOPath.IsPathRooted(mspRaw)
                ? mspRaw : IOPath.Combine(filesDir.TrimEnd('\\', '/'), mspRaw);
            mspPath = ExpandEnv(mspPath);
            if (!File.Exists(mspPath))
            {
                var found = Directory.Exists(filesDir)
                    ? Directory.GetFiles(filesDir, IOPath.GetFileName(mspRaw), SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (found != null) mspPath = found;
            }
            bool exists = File.Exists(mspPath);
            _results.Add(new(idx++, cmdLabel, mspPath,
                exists ? QAStatus.Pass : QAStatus.Fail,
                exists ? null : "MSP patch file not found on disk",
                RowHighlight.None, exists ? IOPath.GetDirectoryName(mspPath) : null));
        }

        // ══════════════════════════════════════════════════════════════════
        //  INVOKE-ADTREGSVR32 (install = register / uninstall = unregister)
        // ══════════════════════════════════════════════════════════════════
        private void CheckInvokeRegSvr32(ref int idx, string resolved, bool isUninstall, string cmdLabel = "Invoke-ADTRegSvr32")
        {
            string filePath = ExpandEnv(ExtractParam(resolved, "-FilePath").Trim('"', '\''));
            string action   = ExtractParam(resolved, "-Action").Trim('"', '\'');
            bool isUnreg = isUninstall ||
                action.Equals("Unregister", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(filePath)) { _results.Add(new(idx++, cmdLabel, resolved, QAStatus.Skip, "Could not parse -FilePath")); return; }
            bool fileExists = File.Exists(filePath);
            if (isUnreg)
            {
                _results.Add(new(idx++, cmdLabel,
                    !fileExists ? $"DLL unregistered and removed: {filePath}"
                                : $"{filePath}  (DLL still exists - may still be registered)",
                    !fileExists ? QAStatus.Pass : QAStatus.Fail,
                    !fileExists ? null : "DLL was not removed after unregistration",
                    RowHighlight.None, IOPath.GetDirectoryName(filePath)));
            }
            else
            {
                _results.Add(new(idx++, cmdLabel, filePath,
                    fileExists ? QAStatus.Pass : QAStatus.Fail,
                    fileExists ? null : "DLL file not found - cannot be registered",
                    RowHighlight.None, IOPath.GetDirectoryName(filePath)));
            }
        }


        private void CheckOrphanedCopies(ref int idx,
            List<string> installCopyResolved, List<string> uninstallCmds, string filesDir)
        {
            foreach (var installCmd in installCopyResolved)
            {
                string src  = ExtractParam(installCmd, "-Path").Trim('\'', '"');
                string dest = ExtractParam(installCmd, "-Destination").Trim('\'', '"');
                if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dest)) continue;

                string srcAbs  = ExpandEnv(src);
                string destAbs = ExpandEnv(dest);
                string fileName = IOPath.GetFileName(srcAbs);
                string destFile = IOPath.Combine(destAbs.TrimEnd('\\', '/'), fileName);

                // Check if any Remove-ADTFile in uninstall covers this destination
                bool hasRemove = uninstallCmds.Any(c =>
                    IsRemoveFile(c) &&
                    ResolveCommand(c, filesDir).IndexOf(destFile, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    IsRemoveFile(c) &&
                    ResolveCommand(c, filesDir).IndexOf(destAbs.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) >= 0);

                bool fileGone = !File.Exists(destFile);

                if (hasRemove)
                {
                    // Remove-ADTFile exists - check file is actually gone
                    _results.Add(new(idx++, "Remove-ADTFile",
                        fileGone ? $"Removed: {destFile}"
                                 : $"Still exists: {destFile}  (Remove-ADTFile ran but file not gone)",
                        fileGone ? QAStatus.Pass : QAStatus.Fail,
                        null, RowHighlight.None, fileGone ? null : destFile));
                }
                else
                {
                    // No Remove-ADTFile found for this copied file
                    _results.Add(new(idx++, "Remove-ADTFile",
                        fileGone
                            ? $"File already gone: {destFile}  (No Remove-ADTFile, file removed by MSI or other means)"
                            : $"File still exists: {destFile}  (Add Remove-ADTFile to Uninstall-ADTDeployment)",
                        fileGone ? QAStatus.Pass : QAStatus.Fail,
                        null, fileGone ? RowHighlight.None : RowHighlight.Red,
                        fileGone ? null : destFile));
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  ENV VARS REMOVED CHECK (uninstall)
        // ══════════════════════════════════════════════════════════════════
        private void CheckEnvVarsRemoved(ref int idx, MsiVerboseLogInfo verboseLog,
            string cmdLabel = "Start-ADTMsiProcess")
        {
            foreach (var (name, value, delimiter) in verboseLog.EnvVars)
            {
                string? currentVal = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                                  ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

                bool removed;
                if (string.IsNullOrEmpty(currentVal))
                {
                    // Variable completely gone
                    removed = true;
                }
                else
                {
                    // PATH-style: check the specific value is no longer in the variable
                    string sep = delimiter.Trim() == "[~]" ? null! : delimiter.Trim();
                    string[] parts = sep == null
                        ? new[] { currentVal }
                        : currentVal.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
                    bool stillPresent = parts.Any(p => string.Equals(
                        p.Trim().TrimEnd('\\', '/'),
                        value.Trim().TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase));
                    removed = !stillPresent;
                }

                _results.Add(new(idx++, cmdLabel,
                    removed ? $"{name} - Environment variable removed"
                            : $"{name} = {value}  (Still present - should be removed by MSI uninstall)",
                    removed ? QAStatus.Pass : QAStatus.Fail,
                    null, RowHighlight.None, "envvar://"));
            }
        }

        private void CheckUninstallProcess(ref int idx, string resolved, string rawCmd, string filesDir, string cmdLabel = "Start-ADTProcess")
        {
            string exeRaw  = ExtractParam(resolved, "-FilePath").Trim('"', '\'');
            string exePath = IOPath.IsPathRooted(exeRaw)
                ? exeRaw : IOPath.Combine(filesDir.TrimEnd('\\', '/'), exeRaw);
            exePath = ExpandEnv(exePath);

            // Determine whether this EXE is an absolute path (uninstall helper on the machine)
            // or a relative path (a file inside the package folder).
            bool isAbsolutePath = IOPath.IsPathRooted(exeRaw);

            // appHint: app name derived from registry path - used for both registry lookup
            // and qasnap search. Computed here so it is accessible in the qasnap section below.
            string appHint;
            {
                string exeNameHint = IOPath.GetFileNameWithoutExtension(exePath);
                string parentDir   = IOPath.GetFileName(IOPath.GetDirectoryName(exePath) ?? "") ?? "";
                string grandDir    = IOPath.GetFileName(
                                         IOPath.GetDirectoryName(
                                             IOPath.GetDirectoryName(exePath) ?? "") ?? "") ?? "";
                bool parentIsUninstDir = parentDir.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0
                                       || parentDir.IndexOf("uninst",   StringComparison.OrdinalIgnoreCase) >= 0;
                appHint = parentIsUninstDir && !string.IsNullOrEmpty(grandDir) ? grandDir : parentDir;
                if (string.IsNullOrEmpty(appHint)) appHint = exeNameHint;
            }

            if (isAbsolutePath)
            {
                // Absolute path = uninstaller helper (e.g. C:\Program Files\App\uninstall\helper.exe).
                // After a successful uninstall the file should be GONE → !File.Exists = Pass.
                bool fileGone = !File.Exists(exePath);
                _results.Add(new(idx++, cmdLabel,
                    fileGone ? $"Uninstaller removed: {exePath}"
                             : $"{exePath}  (File still exists - uninstall may have failed)",
                    fileGone ? QAStatus.Pass : QAStatus.Fail,
                    fileGone ? null : "Uninstaller executable was not removed from the machine",
                    RowHighlight.None, IOPath.GetDirectoryName(exePath)));

                // Registry check: derive app name from the parent folder of the helper
                // e.g. C:\Program Files\Smekal FileCompare\uninstall\helper.exe → "Smekal FileCompare"
                // (appHint already computed at method scope above)

                var regEntry = FindRegistryEntry(appHint, null);
                bool regGone = regEntry == null;
                string regLabel = regEntry?.DisplayName ?? appHint;
                _results.Add(new(idx++, cmdLabel,
                    regGone ? $"Registry entry removed - {regLabel} is uninstalled"
                            : $"Registry entry still exists: {regEntry!.RegistryKeyPath}  - {regLabel} is NOT uninstalled",
                    regGone ? QAStatus.Pass : QAStatus.Fail,
                    regGone ? null : "Uninstall registry entry was not removed",
                    RowHighlight.None, regEntry != null ? "reg://" + regEntry!.RegistryKeyPath : null));

                if (regEntry != null && !string.IsNullOrEmpty(regEntry.InstallLocation))
                {
                    bool locGone = !Directory.Exists(regEntry.InstallLocation);
                    _results.Add(new(idx++, cmdLabel,
                        locGone ? $"InstallLocation removed: {regEntry.InstallLocation}"
                                : $"InstallLocation still exists: {regEntry.InstallLocation}",
                        locGone ? QAStatus.Pass : QAStatus.Fail,
                        null, RowHighlight.None, regEntry.InstallLocation));
                }
            }
            else
            {
                // Relative path = EXE lives inside the package folder.
                // It should exist as part of the package → File.Exists = Pass.
                bool exeExists = File.Exists(exePath);
                _results.Add(new(idx++, cmdLabel, exePath,
                    exeExists ? QAStatus.Pass : QAStatus.Fail,
                    exeExists ? null : "EXE file not found in package"));

                string exeName   = IOPath.GetFileNameWithoutExtension(exePath);
                var regEntry = FindRegistryEntry(exeName, null);
                bool regGone = regEntry == null;
                _results.Add(new(idx++, cmdLabel,
                    regGone ? $"Registry entry removed - {exeName} is uninstalled"
                            : $"Registry entry still exists: {regEntry!.RegistryKeyPath}  - {exeName} is NOT uninstalled",
                    regGone ? QAStatus.Pass : QAStatus.Fail,
                    regGone ? null : "Uninstall registry entry was not removed",
                    RowHighlight.None, regEntry != null ? "reg://" + regEntry!.RegistryKeyPath : null));

                if (regEntry != null && !string.IsNullOrEmpty(regEntry.InstallLocation))
                {
                    bool locGone = !Directory.Exists(regEntry.InstallLocation);
                    _results.Add(new(idx++, cmdLabel,
                        locGone ? $"InstallLocation removed: {regEntry.InstallLocation}"
                                : $"InstallLocation still exists: {regEntry.InstallLocation}",
                        locGone ? QAStatus.Pass : QAStatus.Fail,
                        null, RowHighlight.None, regEntry.InstallLocation));
                }
            }

            // ── .svr-driven removal checks for EXE uninstall ──────────────────────────
            // For absolute-path helpers (e.g. helper.exe), the exe name is useless for
            // matching - use the app name from the registry (appHint) as primary keyword.
            QaSnapshot? exeUnSnap = null;
            try
            {
                string exeFileName = IOPath.GetFileName(exeRaw);
                string exeBaseName = IOPath.GetFileNameWithoutExtension(exeRaw);

                // Build keyword set: words from exe name + words from registry-derived app name
                // e.g. helper.exe + "Smekal FileCompare" -> {"helper","Smekal","FileCompare"}
                var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string appKeyword = isAbsolutePath ? appHint : exeBaseName;
                foreach (var src in new[] { exeBaseName, appKeyword })
                    foreach (var w in src.Split(new char[]{ ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries))
                        if (w.Length >= 3) keywords.Add(w);

                // Named candidates (fast path) - app name first, then exe name
                string appSnapSuffix = appKeyword + " - For QA.svr";
                string exeSnapSuffix = exeBaseName + " - For QA.svr";
                // Search only QAsnapPath (from QA.json/UI) then C:\Temp\Validate Source
                string? exeUnSnapPath = FindQAsnapFile(appKeyword) ?? FindQAsnapFile(exeBaseName);
                if (exeUnSnapPath != null)
                {
                    // Guard: skip this snapshot if it has already been processed in this run
                    // (prevents doubling when multiple Start-ADTProcess commands share a snap)
                    if (!_usedUninstallSnapPaths.Contains(exeUnSnapPath))
                    {
                        var snap = DeserializeSnapshot(File.ReadAllText(exeUnSnapPath));
                        if (snap != null)
                        {
                            exeUnSnap = snap;
                            _usedUninstallSnapPaths.Add(exeUnSnapPath);
                        }
                    }
                }

                // Scan fallback - all *.svr in QAsnapSearchDirs only
                if (exeUnSnap == null)
                {
                    foreach (var snapFile in AllQAsnapFiles())
                    {
                        try
                        {
                            var snap = DeserializeSnapshot(File.ReadAllText(snapFile));
                            if (snap == null) continue;

                            bool nameMatch = string.Equals(snap.FileName, exeFileName,
                                StringComparison.OrdinalIgnoreCase);
                            bool pathMatch = !string.IsNullOrEmpty(snap.FilePath) &&
                                string.Equals(IOPath.GetFileName(snap.FilePath), exeFileName,
                                StringComparison.OrdinalIgnoreCase);
                            bool kwMatch = keywords.Any(kw =>
                                snap.FileName?.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                snap.FilePath?.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);

                            if (nameMatch || pathMatch || kwMatch)
                            {
                                // Guard: skip if already processed
                                if (_usedUninstallSnapPaths.Contains(snapFile)) break;
                                exeUnSnap = snap;
                                _usedUninstallSnapPaths.Add(snapFile);
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (exeUnSnap != null)
            {
                // U-F1) All captured files - verify each is gone after uninstall
                foreach (var filePath in exeUnSnap.Files)
                {
                    bool fileGone = !File.Exists(filePath);
                    _results.Add(new(idx++, cmdLabel + " · File",
                        fileGone ? $"File removed: {filePath}"
                                 : $"{filePath}  (File still exists on machine)",
                        fileGone ? QAStatus.Pass : QAStatus.Fail,
                        fileGone ? null : "File was not removed during uninstall",
                        RowHighlight.None, IOPath.GetDirectoryName(filePath)));
                }

                // U-F2) Folders
                foreach (var folder in exeUnSnap.Folders)
                {
                    if (IsSharedSystemFolder(folder)) continue;
                    bool folderGone = !Directory.Exists(folder);
                    _results.Add(new(idx++, cmdLabel + " · Folder",
                        folderGone ? $"Folder removed: {folder}"
                                   : $"{folder}  (Folder still exists on machine)",
                        folderGone ? QAStatus.Pass : QAStatus.Fail,
                        folderGone ? null : "Folder was not removed during uninstall",
                        RowHighlight.None, folder));
                }

                // U-F3) Shortcuts
                foreach (var scPath in exeUnSnap.Shortcuts)
                {
                    bool scGone = !File.Exists(scPath);
                    _results.Add(new(idx++, cmdLabel + " · Shortcut",
                        scGone ? $"Shortcut removed: {scPath}"
                               : $"{scPath}  (Shortcut still exists)",
                        scGone ? QAStatus.Pass : QAStatus.Fail,
                        scGone ? null : "Shortcut was not removed during uninstall",
                        RowHighlight.None, IOPath.GetDirectoryName(scPath)));
                }

                // U-F4) Registry - all captured keys including Uninstall
                foreach (var sr in exeUnSnap.Registry)
                {
                    bool regGoneSnap = false;
                    try
                    {
                        string[] pts  = sr.Key.Split('\\');
                        string subKey = string.Join("\\", pts.Skip(1));
                        var hive = pts[0].Contains("LOCAL_MACHINE")
                            ? Microsoft.Win32.Registry.LocalMachine
                            : Microsoft.Win32.Registry.CurrentUser;
                        using var lk = hive.OpenSubKey(subKey);
                        if (lk == null) regGoneSnap = true;
                        else if (string.IsNullOrEmpty(sr.Value)) regGoneSnap = false;
                        else regGoneSnap = lk.GetValue(sr.Value) == null;
                    }
                    catch { regGoneSnap = true; }
                    _results.Add(new(idx++, cmdLabel + " · Registry",
                        regGoneSnap ? $"Registry removed: [{sr.Key}]  {sr.Value}"
                                    : $"[{sr.Key}]  {sr.Value} = {sr.Data}  (Registry still exists)",
                        regGoneSnap ? QAStatus.Pass : QAStatus.Fail,
                        regGoneSnap ? null : "Registry key/value was not removed during uninstall",
                        RowHighlight.None, string.IsNullOrEmpty(sr.Value) ? "reg://" + sr.Key : "regval://" + sr.Key + "?" + sr.Value));
                }

                // U-F5) Environment variables
                foreach (var ev in exeUnSnap.EnvVars)
                {
                    string? curVal = Environment.GetEnvironmentVariable(ev.Name, EnvironmentVariableTarget.Machine)
                                  ?? Environment.GetEnvironmentVariable(ev.Name, EnvironmentVariableTarget.User);
                    bool evGone = string.IsNullOrEmpty(curVal);
                    if (!evGone && !string.IsNullOrEmpty(ev.Value))
                    {
                        string sep2 = string.IsNullOrEmpty(ev.Delimiter) ? ";" : ev.Delimiter;
                        evGone = !curVal!.Split(new[] { sep2 }, StringSplitOptions.RemoveEmptyEntries)
                            .Any(p => string.Equals(p.Trim().TrimEnd('\\', '/'),
                                ev.Value.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    }
                    _results.Add(new(idx++, cmdLabel + " · EnvVar",
                        evGone ? $"EnvVar removed: {ev.Name}"
                               : $"{ev.Name} = {ev.Value}  (Environment variable still exists)",
                        evGone ? QAStatus.Pass : QAStatus.Fail,
                        evGone ? null : "Environment variable was not removed during uninstall",
                        RowHighlight.None, "envvar://"));
                }

                // U-F6) Services
                foreach (var svc in exeUnSnap.Services)
                {
                    bool svcGone = false;
                    try
                    {
                        using var sc2 = new System.ServiceProcess.ServiceController(svc.Name);
                        _ = sc2.DisplayName; svcGone = false;
                    }
                    catch { svcGone = true; }
                    _results.Add(new(idx++, cmdLabel + " · Service",
                        svcGone ? $"Service removed: {svc.DisplayName}"
                                : $"{svc.DisplayName}  |  {svc.Name}  (Service still exists)",
                        svcGone ? QAStatus.Pass : QAStatus.Fail,
                        svcGone ? null : $"Service '{svc.Name}' was not removed during uninstall",
                        RowHighlight.None, "service://"));
                }

                // U-F7) Certificates
                foreach (var cert in exeUnSnap.Certificates)
                {
                    bool certGone = true;
                    try
                    {
                        foreach (var storeName in new[] {
                            System.Security.Cryptography.X509Certificates.StoreName.My,
                            System.Security.Cryptography.X509Certificates.StoreName.Root,
                            System.Security.Cryptography.X509Certificates.StoreName.TrustedPublisher })
                        {
                            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                                storeName, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                            if (store.Certificates
                                .OfType<System.Security.Cryptography.X509Certificates.X509Certificate2>()
                                .Any(c => c.Subject.IndexOf(cert, StringComparison.OrdinalIgnoreCase) >= 0
                                       || c.Thumbprint.Equals(cert, StringComparison.OrdinalIgnoreCase)
                                       || c.FriendlyName.IndexOf(cert, StringComparison.OrdinalIgnoreCase) >= 0))
                            { certGone = false; break; }
                        }
                    }
                    catch { }
                    _results.Add(new(idx++, cmdLabel + " · Certificate",
                        certGone ? $"Certificate removed: {cert}"
                                 : $"{cert}  (Certificate still exists in store)",
                        certGone ? QAStatus.Pass : QAStatus.Fail,
                        certGone ? null : "Certificate was not removed during uninstall",
                        RowHighlight.None, "cert://"));
                }

                // U-F8) Driver files
                var driverFiles = exeUnSnap.Files
                    .Where(f => { var e = IOPath.GetExtension(f).ToLowerInvariant(); return e == ".sys" || e == ".inf"; })
                    .ToList();
                foreach (var driverFile in driverFiles)
                {
                    bool driverGone = !File.Exists(driverFile);
                    _results.Add(new(idx++, cmdLabel + " · Driver",
                        driverGone ? $"Driver removed: {driverFile}"
                                   : $"{driverFile}  (Driver file still exists on machine)",
                        driverGone ? QAStatus.Pass : QAStatus.Fail,
                        driverGone ? null : "Driver file was not removed during uninstall",
                        RowHighlight.None, IOPath.GetDirectoryName(driverFile)));
                }

                // U-F9) Firewall rules removed
                foreach (var fwRule in exeUnSnap.Firewall)
                {
                    bool ruleGone = true;
                    try
                    {
                        var netFwType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                        if (netFwType != null)
                        {
                            dynamic fwPolicy = Activator.CreateInstance(netFwType)!;
                            foreach (dynamic rule in fwPolicy.Rules)
                            {
                                if (string.Equals(rule.Name as string, fwRule, StringComparison.OrdinalIgnoreCase))
                                { ruleGone = false; break; }
                            }
                        }
                    }
                    catch { }
                    _results.Add(new(idx++, cmdLabel + " · Firewall",
                        ruleGone ? $"Firewall rule removed: {fwRule}"
                                 : $"{fwRule}  (Firewall rule still exists)",
                        ruleGone ? QAStatus.Pass : QAStatus.Fail,
                        ruleGone ? null : "Firewall rule was not removed during uninstall",
                        RowHighlight.None, "firewall://"));
                }
            }
            else
            {
                // No qasnap found - add a single informational skip row
                string exeFileName = IOPath.GetFileName(exeRaw);
                _results.Add(new(idx++, cmdLabel + " · Snapshot",
                    $"No .svr found for {exeFileName}",
                    QAStatus.Skip,
                    $"Run Source Validation capture on {exeFileName} to enable deep file/folder/registry/service/certificate checks"));
            }
        }

        private static Dictionary<string, string> ReadMstPropertiesViaTransformView(string mstPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(mstPath)) return result;

            try
            {
                byte[] raw = File.ReadAllBytes(mstPath);

                // ── Step 1: Find the string data (ASCII pool) ─────────────
                var knownProps = new[]
                {
                    "ALLUSERS","REBOOT","ROOTDRIVE","ARPNOMODIFY","ARPNOREPAIR",
                    "ARPNOREMOVE","REINSTALLMODE","TRANSFORMS","INSTALLLEVEL",
                    "APPLICATIONUSERS","MSIINSTALLPERUSER","INSTALLDIR"
                };

                int poolOffset = -1;
                int poolLen    = 0;
                int i = 0;
                while (i < raw.Length)
                {
                    if (raw[i] >= 0x20 && raw[i] <= 0x7E)
                    {
                        int j = i;
                        while (j < raw.Length && raw[j] >= 0x20 && raw[j] <= 0x7E) j++;
                        string run = System.Text.Encoding.ASCII.GetString(raw, i, j - i);
                        int score = knownProps.Count(p => run.Contains(p, StringComparison.Ordinal));
                        if (score > 0 && j - i > poolLen) { poolOffset = i; poolLen = j - i; }
                        i = j;
                    }
                    else i++;
                }
                if (poolOffset < 0) return result;
                string poolText = System.Text.Encoding.ASCII.GetString(raw, poolOffset, poolLen);

                // ── Step 2: Find StringPool stream right after the data ───
                // StringPool format: (length uint16 LE, refcount uint16 LE) per string
                // It starts at the first non-zero uint16 pair after the pool data + nulls.
                int spStart = poolOffset + poolLen;
                while (spStart < raw.Length - 3 && raw[spStart] == 0x00) spStart++;

                // Build the indexed string array
                var strings = new List<string> { "" }; // index 0 = empty
                int charPos = 0;
                int spIdx   = 0;
                while (spStart + spIdx * 4 + 3 < raw.Length)
                {
                    int length   = BitConverter.ToUInt16(raw, spStart + spIdx * 4);
                    int refcount = BitConverter.ToUInt16(raw, spStart + spIdx * 4 + 2);
                    if (length == 0 && refcount == 0) break;
                    if (charPos + length > poolText.Length) break;
                    strings.Add(poolText.Substring(charPos, length));
                    charPos += length;
                    spIdx++;
                }

                if (strings.Count <= 1) return result;

                // ── Step 3: Parse Property table rows ─────────────────────
                // Row format: 0x01 0x02 <nameIdx uint16 LE> <valIdx uint16 LE>
                int ptSearch = spStart + spIdx * 4 + 4; // just after StringPool
                int ptEnd    = Math.Min(raw.Length - 5, ptSearch + 512); // limited window
                int pos      = ptSearch;
                while (pos < ptEnd)
                {
                    if (raw[pos] == 0x01 && raw[pos + 1] == 0x02)
                    {
                        int nameIdx = BitConverter.ToUInt16(raw, pos + 2);
                        int valIdx  = BitConverter.ToUInt16(raw, pos + 4);
                        if (nameIdx > 0 && nameIdx < strings.Count &&
                            valIdx  > 0 && valIdx  < strings.Count)
                        {
                            string propName = strings[nameIdx];
                            string propVal  = strings[valIdx];
                            if (!string.IsNullOrEmpty(propName) &&
                                Regex.IsMatch(propName, @"^[A-Z][A-Z0-9_]{1,49}$"))
                                result[propName] = propVal;
                        }
                        pos += 6;
                    }
                    else pos++;
                }
            }
            catch { }

            // COM fallback if binary parse found nothing
            if (result.Count == 0)
            {
                try {
                    var t = Type.GetTypeFromProgID("WindowsInstaller.Installer");
                    if (t != null) {
                        dynamic installer = Activator.CreateInstance(t)!;
                        foreach (int mode in new[] { 32, 0, 1 })
                        {
                            try {
                                dynamic db = installer.OpenDatabase(mstPath, mode);
                                bool got = false;
                                try {
                                    dynamic vw = db.OpenView(
                                        "SELECT `Property`, `Value` FROM `Property`");
                                    vw.Execute();
                                    while (true) {
                                        dynamic? r = vw.Fetch();
                                        if (r == null) break;
                                        try {
                                            string pn = Convert.ToString(r.StringData[1]) ?? "";
                                            string pv = Convert.ToString(r.StringData[2]) ?? "";
                                            if (!string.IsNullOrEmpty(pn)) { result[pn] = pv; got = true; }
                                        } catch { }
                                    }
                                } catch { }
                                if (got) break;
                            } catch { }
                        }
                    }
                } catch { }
            }

            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  RENDER RESULTS
        // ══════════════════════════════════════════════════════════════════
        //  RENDER RESULTS
        // ══════════════════════════════════════════════════════════════════
        private void RenderResults(Window? progressWinToClose = null)
        {
            Dispatcher.Invoke(() =>
            {
                TxtQAStatus.Text = "";

                // Dynamic summary title
                if (FindName("TxtQASummaryTitle") is TextBlock summaryTb)
                    summaryTb.Text = _isUninstallRun
                        ? "QA SUMMARY - UNINSTALL"
                        : "QA SUMMARY - INSTALL";

                // ── Update counts (always from full _results, not filtered view) ──
                int pass   = _results.Count(r => EffectiveStatus(r) == QAStatus.Pass);
                int fail   = _results.Count(r => EffectiveStatus(r) == QAStatus.Fail);
                int skip   = _results.Count(r => EffectiveStatus(r) == QAStatus.Skip);
                int ignore = _results.Count(r => EffectiveStatus(r) == QAStatus.Ignore);
                int total  = pass + fail + skip + ignore;
                if (FindName("TxtAllCount")    is System.Windows.Controls.TextBlock allTb)  allTb.Text  = total.ToString();
                if (FindName("TxtIgnoreCount") is System.Windows.Controls.TextBlock ignTb)  ignTb.Text  = ignore.ToString();
                TxtPassCount.Text = pass.ToString();
                TxtFailCount.Text = fail.ToString();
                TxtSkipCount.Text = skip.ToString();

                // ── Build rows only when cache is empty (first render after QA run) ──
                if (_rowCache.Count == 0)
                {
                    _rowDetailTbs.Clear();
                    bool alt = false;
                    foreach (var r in _results)
                    {
                        var row = BuildResultRow(r, alt);
                        _rowCache.Add((row, r));
                        alt = !alt;
                    }
                    BuildCommandNav();
                    // Rows go into the ListBox here, through the filter, so only the admitted ones
                    // become items. Nothing is added to the visual tree until it scrolls into view.
                    ApplyRowFilter();

                    // Close the progress window now - rows are built and in the panel.
                    // Use BeginInvoke at Render priority so WPF paints the rows first,
                    // THEN the progress dialog disappears (no blank-table gap).
                    if (progressWinToClose != null)
                    {
                        var winRef = progressWinToClose;
                        Dispatcher.BeginInvoke(
                            System.Windows.Threading.DispatcherPriority.Render,
                            new Action(() => { try { winRef.Close(); } catch { } }));
                    }
                }
                else
                {
                    // ── Fast path: re-apply the filter to the cached rows ──────────
                    ApplyRowFilter();
                }

                // Re-apply search highlights if active.
                // The match list has to be REBUILT, not just re-highlighted: BuildQASearchMatches
                // only records rows visible under the filter in force at the time, so after a filter
                // change a stale match can point at a row that is now Collapsed - and
                // ScrollToSearchMatch would then silently do nothing, exactly like the command nav
                // did before. Rebuilding here keeps the matches in step with what is on screen.
                if (!string.IsNullOrEmpty(_qaSearchText))
                {
                    BuildQASearchMatches();
                    if (_qaSearchIdx >= _qaSearchMatches.Count)
                        _qaSearchIdx = _qaSearchMatches.Count - 1;
                    if (_qaSearchMatches.Count > 0) ApplyWordHighlights();
                }

                UpdateFilterButtonStyles();
                UpdateSummaryButtonStyles();
            });
        }

        // ── Build left-panel command navigation ───────────────────────────
        // Track active nav button and highlighted row border for yellow selection
        private Button? _activeNavBtn = null;
        private Border? _activeNavRow = null;
        private static readonly Brush YellowHighlight = new SolidColorBrush(Color.FromRgb(0xFF, 0xF9, 0xC4));

        /// <summary>True when the active filter admits this result.</summary>
        private bool RowVisibleUnderFilter(QAResult r) => _filterMode switch
        {
            FilterMode.PassOnly   => EffectiveStatus(r) == QAStatus.Pass,
            FilterMode.FailOnly   => EffectiveStatus(r) == QAStatus.Fail,
            FilterMode.SkipOnly   => EffectiveStatus(r) == QAStatus.Skip,
            FilterMode.IgnoreOnly => EffectiveStatus(r) == QAStatus.Ignore,
            _                     => true,
        };

        /// <summary>
        /// Rebuilds the ListBox's items to hold ONLY the rows the current filter admits, restriping
        /// as it goes, then scrolls back to the top.
        ///
        /// The whole collection is swapped in one go rather than Clear()+Add()ed row by row: each Add
        /// on a live ItemsSource raises its own CollectionChanged, and 1000+ of those is exactly the
        /// kind of per-item churn this change exists to remove.
        /// </summary>
        private void ApplyRowFilter()
        {
            var visible = new System.Collections.ObjectModel.ObservableCollection<UIElement>();
            bool alt = false;
            foreach (var (row, r) in _rowCache)
            {
                if (!RowVisibleUnderFilter(r)) continue;
                if (row is Border bdr) bdr.Background = GetRowBackground(r, alt);
                row.Visibility = Visibility.Visible;   // rows are never collapsed now, only omitted
                visible.Add(row);
                alt = !alt;
            }
            _visibleRows = visible;
            QAResultsPanel.ItemsSource = _visibleRows;   // one Reset notification, not N Adds
            ScrollResultsToTop();
        }

        /// <summary>
        /// Scrolls a row into view. ScrollIntoView, not TranslatePoint: with virtualization an
        /// off-screen row has no container and therefore no position, so TranslatePoint would return
        /// a meaningless offset. ScrollIntoView realizes the container first.
        /// </summary>
        private void ScrollRowIntoView(UIElement row)
        {
            try { QAResultsPanel.ScrollIntoView(row); } catch { }
        }

        private void ScrollResultsToTop()
        {
            if (_visibleRows.Count > 0) ScrollRowIntoView(_visibleRows[0]);
        }

        private void BuildCommandNav()
        {
            CommandNavPanel.Children.Clear();
            if (_results.Count == 0) return;

            // Collect all unique check-type labels in result order.
            // This shows every sub-type (· File, · Shortcut, · Registry etc.) as a nav item
            // for both install and uninstall QA, so the user can jump to any section.
            var navItems = _results
                .Where(r => r.CheckType != "─────────────" && r.CheckType != "Info")
                .GroupBy(r => r.CheckType)
                .Select(g => g.First())
                .ToList();

            foreach (var item in navItems)
            {
                var r = item;
                bool hasFail = _results.Any(x => x.CheckType == r.CheckType && x.Status == QAStatus.Fail);
                bool hasPass = _results.Any(x => x.CheckType == r.CheckType && x.Status == QAStatus.Pass);
                bool allSkip = _results.Where(x => x.CheckType == r.CheckType)
                                       .All(x => x.Status == QAStatus.Skip);

                // Nav item: no background - only a left border line in status colour
                // Pass=green, Fail=red, Skip=grey, Ignore=blue (matches row text colours)
                bool hasIgnore = _results.Any(x => x.CheckType == r.CheckType
                                              && EffectiveStatus(x) == QAStatus.Ignore);
                bool hasFail2  = _results.Any(x => x.CheckType == r.CheckType
                                              && EffectiveStatus(x) == QAStatus.Fail);

                Brush navAccent = (hasFail && !hasIgnore)
                    ? new SolidColorBrush(Color.FromRgb(0xE5, 0x35, 0x35))  // red
                    : hasIgnore
                        ? new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5))  // blue
                        : allSkip
                            ? new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA8))  // grey
                            : new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x44)); // green

                Brush navFg = (hasFail && !hasIgnore)
                    ? new SolidColorBrush(Color.FromRgb(0xE5, 0x35, 0x35))
                    : hasIgnore
                        ? new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5))
                        : allSkip
                            ? (Brush)(FindResource("TextMutedBrush") ?? Brushes.Gray)
                            : new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x44));

                var label = new TextBlock
                {
                    Text = r.CheckType,
                    FontSize = 10,
                    FontWeight = (hasFail && !hasIgnore) ? FontWeights.SemiBold : FontWeights.Normal,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = navFg,
                    ToolTip = r.CheckType,
                };

                var navBorder = new Border
                {
                    CornerRadius    = new CornerRadius(0, 4, 4, 0),
                    Background      = Brushes.Transparent,
                    BorderBrush     = navAccent,
                    BorderThickness = new Thickness(3, 0, 0, 0),  // left accent line only
                    Margin          = new Thickness(0, 2, 0, 2),
                    Padding         = new Thickness(8, 4, 6, 4),
                    Cursor          = System.Windows.Input.Cursors.Hand,
                    Child           = label,
                };
                navBorder.Tag = r; // keep QAResult tag for nav logic

                // Hover: tint background
                navBorder.MouseEnter += (_, _) =>
                {
                    if (_activeNavBtn?.Content != navBorder)
                        navBorder.Background = new SolidColorBrush(
                            Color.FromArgb(0x18, 0x42, 0xA5, 0xF5));
                };
                navBorder.MouseLeave += (_, _) =>
                {
                    if (_activeNavBtn?.Content != navBorder)
                        navBorder.Background = Brushes.Transparent;
                };

                navBorder.MouseLeftButtonUp += (_, _) =>
                {
                    // Deactivate previous nav item - restore its opacity
                    if (_activeNavBtn != null && _activeNavBtn.Content is Border prevNavBorder)
                    {
                        prevNavBorder.Background = Brushes.Transparent;
                        if (prevNavBorder.Child is TextBlock prevTb)
                            prevTb.FontWeight = FontWeights.Normal;
                    }
                    // Restore previous row background
                    if (_activeNavRow != null && _activeNavRow.Tag is ValueTuple<QAResult, bool> prevTag)
                    {
                        _activeNavRow.Background = GetRowBackground(prevTag.Item1, prevTag.Item2);
                        _activeNavRow.BorderThickness = new Thickness(0, 0, 0, 1);
                        _activeNavRow.BorderBrush = (Brush)(FindResource("BorderMainBrush") ?? Brushes.LightGray);
                    }

                    // Mark this nav item as active (darken slightly)
                    _activeNavBtn = new Button { Content = navBorder, Tag = r };
                    navBorder.Background = new SolidColorBrush(
                        Color.FromArgb(0x25, 0x42, 0xA5, 0xF5));  // active tint
                    label.FontWeight = FontWeights.SemiBold;

                    // The active filter omits rows it excludes. If this command group has no row on
                    // screen right now, drop the filter so it can actually be shown.
                    //
                    // Without this, clicking an all-pass group (Service, Shortcut, ARPFlags...) while
                    // the Fail filter was on did nothing at all. Only the groups that happened to
                    // contain failures (File, Folder, Registry) appeared to work.
                    if (!_rowCache.Any(rc => rc.Result.CheckType == r.CheckType
                                          && RowVisibleUnderFilter(rc.Result))
                        && _filterMode != FilterMode.All)
                    {
                        _filterMode = FilterMode.All;
                        RenderResults();   // rebuilds the item list through the filter
                    }

                    // Find the first row of this group that the filter admits, highlight it yellow,
                    // scroll to it. Resolved from _rowCache rather than by walking the panel: with a
                    // virtualizing ListBox the off-screen rows have no containers to walk.
                    foreach (var (row, res) in _rowCache)
                    {
                        if (res.CheckType != r.CheckType || !RowVisibleUnderFilter(res)) continue;
                        if (row is not Border rowBorder) continue;

                        _activeNavRow = rowBorder;
                        rowBorder.Background = YellowHighlight;
                        ScrollRowIntoView(rowBorder);
                        break;
                    }
                };

                CommandNavPanel.Children.Add(navBorder);
            }

            // Auto-select the first item
            if (CommandNavPanel.Children.Count > 0
                && CommandNavPanel.Children[0] is Border firstBorder)
            {
                firstBorder.Background = new SolidColorBrush(
                    Color.FromArgb(0x25, 0x42, 0xA5, 0xF5));
                if (firstBorder.Child is TextBlock firstLabel)
                    firstLabel.FontWeight = FontWeights.SemiBold;
                _activeNavBtn = new Button { Content = firstBorder };
            }
        }

        private Brush GetRowBackground(QAResult r, bool alt)
        {
            // Separator rows keep their dark background
            if (r.CheckType == "─────────────")
                return new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F));
            // All other rows: transparent (no colour coding, no alternating)
            return Brushes.Transparent;
        }

        /// <summary>Returns the effective status for a result, applying any user Ignore overrides.</summary>
        private QAStatus EffectiveStatus(QAResult r)
            => _statusOverrides.TryGetValue(r.No, out var s) ? s : r.Status;

        private UIElement BuildResultRow(QAResult r, bool alt)
        {
            // Background - computed centrally via GetRowBackground (also used by nav restore)
            Brush bg = GetRowBackground(r, alt);

            // Status badge - uses effective status (respects Ignore override)
            var effStatus = EffectiveStatus(r);
            string badgeText  = effStatus == QAStatus.Pass   ? "Pass"
                              : effStatus == QAStatus.Fail   ? "Fail"
                              : effStatus == QAStatus.Ignore ? "Ignore" : "Skip";

            // ── Status colour applied to ALL row text (theme-safe) ──────────────
            // Pass=Green, Fail=Red, Skip=Grey, Ignore=Blue - vivid enough for dark mode
            Brush statusFg = effStatus == QAStatus.Pass
                ? new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x44))   // green
                : effStatus == QAStatus.Fail
                ? new SolidColorBrush(Color.FromRgb(0xE5, 0x35, 0x35))   // red
                : effStatus == QAStatus.Ignore
                ? new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5))   // blue
                : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA8));  // grey (skip)

            var badgeFg       = effStatus == QAStatus.Pass   ? Color.FromRgb(0x2E, 0x7D, 0x32)
                              : effStatus == QAStatus.Fail   ? Color.FromRgb(0xC6, 0x28, 0x28)
                              : effStatus == QAStatus.Ignore ? Color.FromRgb(0x1A, 0x6E, 0xC8)
                              : Color.FromRgb(0x66, 0x66, 0x88);
            var badgeBg       = effStatus == QAStatus.Pass   ? Color.FromRgb(0xE8, 0xF5, 0xE9)
                              : effStatus == QAStatus.Fail   ? Color.FromRgb(0xFF, 0xEB, 0xEE)
                              : effStatus == QAStatus.Ignore ? Color.FromRgb(0xE3, 0xF2, 0xFD)
                              : Color.FromRgb(0xEE, 0xEE, 0xF4);
            var badgeBorder   = effStatus == QAStatus.Pass   ? Color.FromRgb(0xA5, 0xD6, 0xA7)
                              : effStatus == QAStatus.Fail   ? Color.FromRgb(0xEF, 0x9A, 0x9A)
                              : effStatus == QAStatus.Ignore ? Color.FromRgb(0x90, 0xCA, 0xF9)
                              : Color.FromRgb(0xCC, 0xCC, 0xDD);

            var badge = new Border
            {
                Background      = new SolidColorBrush(badgeBg),
                BorderBrush     = new SolidColorBrush(badgeBorder),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = badgeText,
                    FontSize   = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(badgeFg),
                }
            };

            // Detail text - single line for Fail rows: append note inline in brackets
            string detailText = r.Detail;
            if (!string.IsNullOrEmpty(r.Note))
                detailText += r.Status == QAStatus.Fail
                    ? $"  ({r.Note})"
                    : $"\n  ⚠ {r.Note}";

            var detailTb = new TextBlock
            {
                FontSize     = 12,
                FontFamily   = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground   = statusFg,  // status colour applied to all row text
            };
            detailTb.Text = detailText;

            var row = new Border
            {
                Background      = bg,
                BorderBrush     = (Brush)(FindResource("BorderMainBrush") ?? Brushes.LightGray),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 1, 0, 1),
                Padding         = new Thickness(12, 3, 12, 3),
                Child           = new Grid(),
                Tag             = (r, alt),   // store (QAResult, isAlt) for nav highlighting
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            // Left-click: select this row (blue accent border + highlight)
            row.MouseLeftButtonUp += (s, e) =>
            {
                // Deselect previous
                if (_selectedRowBorder != null && _selectedRowBorder != row)
                {
                    var prev = (ValueTuple<QAResult,bool>)_selectedRowBorder.Tag;
                    _selectedRowBorder.Background    = GetRowBackground(prev.Item1, prev.Item2);
                    _selectedRowBorder.BorderBrush   = (Brush)(FindResource("BorderMainBrush") ?? Brushes.LightGray);
                    _selectedRowBorder.BorderThickness = new Thickness(0, 0, 0, 1);
                }
                // Select this row
                row.Background      = new SolidColorBrush(Color.FromArgb(0x55, 0x42, 0xA5, 0xF5));
                row.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5));
                row.BorderThickness = new Thickness(1.5);
                _selectedRowBorder  = row;
                e.Handled = true;
            };
            // Right-click: select the row (same selection style as left-click)
            row.PreviewMouseRightButtonDown += (s, e) =>
            {
                // Deselect previous
                if (_selectedRowBorder != null && _selectedRowBorder != row)
                {
                    var prev = (ValueTuple<QAResult,bool>)_selectedRowBorder.Tag;
                    _selectedRowBorder.Background      = GetRowBackground(prev.Item1, prev.Item2);
                    _selectedRowBorder.BorderBrush     = (Brush)(FindResource("BorderMainBrush") ?? Brushes.LightGray);
                    _selectedRowBorder.BorderThickness = new Thickness(0, 0, 0, 1);
                }
                // Select this row with blue accent
                row.Background      = new SolidColorBrush(Color.FromArgb(0x55, 0x42, 0xA5, 0xF5));
                row.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5));
                row.BorderThickness = new Thickness(1.5);
                _selectedRowBorder  = row;
            };
            var grid = (Grid)row.Child;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            var noTb = new TextBlock
            {
                Text = r.No.ToString(),
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = statusFg,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0)
            };
            bool isFileRow      = IsMsiFileRow(r.CheckType) || r.CheckType == "MST · File";
            bool isSeparatorRow = r.CheckType == "─────────────";

            // Separator row: render as a full-width banner with white text, no badge/columns
            if (isSeparatorRow)
            {
                var sepLabel = new TextBlock
                {
                    Text = r.Detail,
                    FontSize = 12, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                return new Border
                {
                    Background      = bg,
                    BorderBrush     = (Brush)(FindResource("BorderMainBrush") ?? Brushes.LightGray),
                    BorderThickness = new Thickness(0, 2, 0, 2),
                    Padding         = new Thickness(12, 4, 12, 4),
                    Child           = sepLabel
                };
            }
            var (cmdText, typeText) = SplitCheckType(r.CheckType);
            var typeTb = new TextBlock
            {
                Text = cmdText,
                FontSize = 12,
                FontWeight = FontWeights.Normal,
                Foreground = statusFg,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0)
            };
            // Type column (ARPFlags, Property, File, Folder, Registry, ...) split from the label so
            // it can be filtered on its own in the CSV.
            var typeColTb = new TextBlock
            {
                Text = typeText,
                FontSize = 12,
                FontWeight = FontWeights.Normal,
                Foreground = statusFg,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0)
            };

            Grid.SetColumn(noTb,      0); grid.Children.Add(noTb);
            Grid.SetColumn(typeTb,    1); grid.Children.Add(typeTb);
            Grid.SetColumn(typeColTb, 2); grid.Children.Add(typeColTb);
            Grid.SetColumn(detailTb,  3); grid.Children.Add(detailTb);
            Grid.SetColumn(badge,     4); grid.Children.Add(badge);

            // Register detailTb so search can highlight words inside it
            // Key = index in _results list (r.No - 1 since rows are 1-based sequential)
            _rowDetailTbs[r.No - 1] = detailTb;

            // Open button (column 4) - only for rows with a resolvable path
            if (!string.IsNullOrEmpty(r.OpenPath))
            {
                string openPath = r.OpenPath;
                bool isRegPath   = openPath.StartsWith("reg://",      StringComparison.OrdinalIgnoreCase);
                bool isRegVal    = openPath.StartsWith("regval://",   StringComparison.OrdinalIgnoreCase);
                bool isEnvPath   = openPath.StartsWith("envvar://",   StringComparison.OrdinalIgnoreCase);
                bool isCertPath  = openPath.StartsWith("cert://",     StringComparison.OrdinalIgnoreCase);
                bool isSvcPath   = openPath.StartsWith("service://",  StringComparison.OrdinalIgnoreCase);
                bool isFwPath    = openPath.StartsWith("firewall://", StringComparison.OrdinalIgnoreCase);

                bool isPs1    = openPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                             || openPath.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase);
                string icon = (isRegPath || isRegVal) ? "🗝"
                            : isEnvPath  ? "⚙"
                            : isCertPath ? "🔒"
                            : isSvcPath  ? "⚙"
                            : isFwPath   ? "🛡"
                            : isPs1      ? "📄"
                            : "📂";

                string regDisplay = isRegVal
                    ? openPath.Substring(9).Replace("?", " → ")
                    : isRegPath ? openPath.Substring(6) : "";
                string tooltip = (isRegPath || isRegVal) ? $"Open in Registry Editor: {regDisplay}"
                               : isEnvPath  ? "Open Environment Variables"
                               : isCertPath ? "Open Certificate Manager (certmgr.msc)"
                               : isSvcPath  ? "Open Services (services.msc)"
                               : isFwPath   ? "Open Windows Firewall (wf.msc)"
                               : isPs1      ? $"Open in editor: {IOPath.GetFileName(openPath)}"
                               : $"Open: {openPath}";

                var openBtn = new Button
                {
                    Content  = icon,
                    FontSize = 14,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = tooltip,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Padding = new Thickness(2),
                };
                openBtn.Click += (_, _) => ExecuteOpenPath(openPath);
                Grid.SetColumn(openBtn, 5);
                grid.Children.Add(openBtn);
            }

            // ── Right-click context menu: Copy Row / Ignore / Open path ─────
            var ctxMenu = new ContextMenu();

            // Copy Row - copies No. | CheckType | Detail | Status to clipboard
            var miCopy = new MenuItem { Header = "📋  Copy Row" };
            miCopy.Click += (_, _) =>
            {
                var es = EffectiveStatus(r);
                string status = es == QAStatus.Pass ? "Pass" : es == QAStatus.Fail ? "Fail"
                              : es == QAStatus.Ignore ? "Ignore" : "Skip";
                string note   = string.IsNullOrEmpty(r.Note) ? "" : $"  ({r.Note})";
                string text   = $"{r.No}\t{r.CheckType}\t{r.Detail}{note}\t{status}";
                try { System.Windows.Clipboard.SetText(text); } catch { }
            };
            ctxMenu.Items.Add(miCopy);

            // Ignore - only shown for Fail rows; toggles between Fail and Ignore
            if (r.Status == QAStatus.Fail)
            {
                ctxMenu.Items.Add(new Separator());
                bool currentlyIgnored = _statusOverrides.TryGetValue(r.No, out var cur)
                                     && cur == QAStatus.Ignore;
                var miIgnore = new MenuItem
                {
                    Header = currentlyIgnored ? "↩  Unignore (restore Fail)" : "🚫  Ignore",
                    FontWeight = FontWeights.SemiBold,
                };
                miIgnore.Click += (_, _) =>
                {
                    if (_statusOverrides.TryGetValue(r.No, out var s2) && s2 == QAStatus.Ignore)
                        _statusOverrides.Remove(r.No);   // unignore → back to Fail
                    else
                        _statusOverrides[r.No] = QAStatus.Ignore;
                    RebuildCachedRow(r);   // refresh this row so the Result badge shows the new effective status
                    RenderResults();
                    RefreshLiveHtml();
                };
                ctxMenu.Items.Add(miIgnore);
            }

            // Copy Detail only
            var miCopyDetail = new MenuItem { Header = "📋  Copy Path / Detail" };
            miCopyDetail.Click += (_, _) =>
            {
                try { System.Windows.Clipboard.SetText(r.Detail); } catch { }
            };
            ctxMenu.Items.Add(miCopyDetail);

            // Open - only if the row has an OpenPath
            if (!string.IsNullOrEmpty(r.OpenPath))
            {
                ctxMenu.Items.Add(new Separator());
                string openPath2 = r.OpenPath;
                bool isReg2  = openPath2.StartsWith("reg://",    StringComparison.OrdinalIgnoreCase)
                            || openPath2.StartsWith("regval://", StringComparison.OrdinalIgnoreCase);
                bool isEnv2  = openPath2.StartsWith("envvar://",   StringComparison.OrdinalIgnoreCase);
                bool isCert2 = openPath2.StartsWith("cert://",     StringComparison.OrdinalIgnoreCase);
                bool isSvc2  = openPath2.StartsWith("service://",  StringComparison.OrdinalIgnoreCase);
                bool isFw2   = openPath2.StartsWith("firewall://", StringComparison.OrdinalIgnoreCase);

                string openLabel = isReg2  ? "🗝  Open in Registry Editor"
                                 : isEnv2  ? "⚙  Open Environment Variables"
                                 : isCert2 ? "🔒  Open Certificate Manager"
                                 : isSvc2  ? "⚙  Open Services"
                                 : isFw2   ? "🛡  Open Windows Firewall"
                                 : "📂  Open in Explorer";

                var miOpen = new MenuItem { Header = openLabel };
                miOpen.Click += (_, _) => ExecuteOpenPath(openPath2);
                ctxMenu.Items.Add(miOpen);
            }

            row.ContextMenu = ctxMenu;

            return row;
        }

        // Rebuilds a single cached result row in place so its Result-column badge,
        // row text colour, and context menu reflect the current EffectiveStatus.
        // The fast path in RenderResults only toggles visibility on cached rows and
        // never rebuilds them, so after an Ignore toggle the badge would otherwise
        // stay on its original status until the next full render.
        private void RebuildCachedRow(QAResult r)
        {
            for (int i = 0; i < _rowCache.Count; i++)
            {
                if (!ReferenceEquals(_rowCache[i].Result, r)) continue;

                var oldRow   = _rowCache[i].Row;
                var newRow   = BuildResultRow(r, i % 2 == 1);
                _rowCache[i] = (newRow, r);

                // Swap in place in the bound collection: one Replace notification, so the ListBox
                // re-realizes just this row. If the row is not currently on screen (the filter omits
                // it) there is nothing to swap - _rowCache is already up to date for the next filter
                // pass.
                int visIdx = _visibleRows.IndexOf(oldRow);
                if (visIdx >= 0) _visibleRows[visIdx] = newRow;
                if (ReferenceEquals(_selectedRowBorder, oldRow))
                    _selectedRowBorder = newRow as Border;
                return;
            }
        }

        /// <summary>Executes the open action for a given OpenPath URI or file/folder path.</summary>
        private void ExecuteOpenPath(string openPath)
        {
            try
            {
                bool isRegPath   = openPath.StartsWith("reg://",      StringComparison.OrdinalIgnoreCase);
                bool isRegVal    = openPath.StartsWith("regval://",   StringComparison.OrdinalIgnoreCase);
                bool isEnvPath   = openPath.StartsWith("envvar://",   StringComparison.OrdinalIgnoreCase);
                bool isCertPath  = openPath.StartsWith("cert://",     StringComparison.OrdinalIgnoreCase);
                bool isSvcPath   = openPath.StartsWith("service://",  StringComparison.OrdinalIgnoreCase);
                bool isPs1       = openPath.EndsWith(".ps1",  StringComparison.OrdinalIgnoreCase)
                                || openPath.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase);
                bool isFwPath    = openPath.StartsWith("firewall://", StringComparison.OrdinalIgnoreCase);

                if (isEnvPath)
                    Process.Start(new ProcessStartInfo("rundll32.exe",
                        "sysdm.cpl,EditEnvironmentVariables") { UseShellExecute = true });
                else if (isRegPath || isRegVal)
                {
                    // Parse: regval://HKEY_...\SubKey?ValueName  or  reg://HKEY_...\SubKey
                    string raw       = openPath.Substring(isRegVal ? 9 : 6);
                    string regKey    = raw;
                    string valueName = "";
                    if (isRegVal)
                    {
                        int qIdx = raw.IndexOf('?');
                        if (qIdx >= 0)
                        {
                            regKey    = raw.Substring(0, qIdx);
                            valueName = raw.Substring(qIdx + 1);
                        }
                    }

                    // Write LastKey so regedit navigates to the correct key on open
                    using (var applets = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    {
                        applets?.SetValue("LastKey", regKey);
                    }

                    // Kill any running regedit so it reopens fresh at the new key
                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName("regedit"))
                        try { proc.Kill(); proc.WaitForExit(1500); } catch { }

                    System.Threading.Thread.Sleep(300);

                    // Launch regedit
                    Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });


                }
                else if (isCertPath)
                    Process.Start(new ProcessStartInfo("certmgr.msc") { UseShellExecute = true });
                else if (isSvcPath)
                    Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
                else if (isFwPath)
                    Process.Start(new ProcessStartInfo("wf.msc") { UseShellExecute = true });
                else if (isPs1 && File.Exists(openPath))
                {
                    // Open ps1 in PowerShell ISE (view/edit, not execute)
                    string sys = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                    string[] isePaths = new[]
                    {
                        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                            "WindowsPowerShell", "v1.0", "PowerShell_ISE.exe"),
                        IOPath.Combine(sys, "System32",  "WindowsPowerShell", "v1.0", "PowerShell_ISE.exe"),
                        IOPath.Combine(sys, "SysWOW64",  "WindowsPowerShell", "v1.0", "PowerShell_ISE.exe"),
                    };
                    string? isePath = isePaths.FirstOrDefault(File.Exists);
                    if (isePath != null)
                    {
                        // Pass path directly (no -File flag) so ISE opens for editing, not execution
                        Process.Start(new ProcessStartInfo
                        {
                            FileName        = isePath,
                            Arguments       = "\"" + openPath + "\"",
                            UseShellExecute = false,
                            CreateNoWindow  = false
                        });
                    }
                    else
                    {
                        // Fallback: Notepad
                        Process.Start(new ProcessStartInfo
                        {
                            FileName        = "notepad.exe",
                            Arguments       = "\"" + openPath + "\"",
                            UseShellExecute = false
                        });
                    }
                }
                else if (File.Exists(openPath))
                    Process.Start("explorer.exe", $"/select,\"{openPath}\"");
                else if (Directory.Exists(openPath))
                    Process.Start("explorer.exe", openPath);
                else
                    Process.Start("explorer.exe", IOPath.GetDirectoryName(openPath) ?? openPath);
            }
            catch { }
        }

        private void ClearResults()
        {
            Dispatcher.Invoke(() =>
            {
                _results.Clear();
                _rowCache.Clear();
                _visibleRows.Clear();   // ItemsSource is bound - clear the source, not Items
                if (FindName("TxtAllCount") is System.Windows.Controls.TextBlock allReset2) allReset2.Text = "-";
                TxtPassCount.Text = "-";
                TxtFailCount.Text = "-";
                TxtSkipCount.Text = "-";
                if (FindName("TxtIgnoreCount") is System.Windows.Controls.TextBlock ignReset)
                    ignReset.Text = "-";
                CommandNavPanel.Children.Clear();
                _activeNavBtn = null;
                _activeNavRow = null;
            });
        }

        // ══════════════════════════════════════════════════════════════════
        //  EXPORT REPORT
        // ══════════════════════════════════════════════════════════════════
        private void BtnExportReport_Click(object s, RoutedEventArgs e)
        {
            // Resolve package folder - try cached value first, then re-parse the log
            string initialDir = "";

            if (!string.IsNullOrEmpty(_packageFolder) && Directory.Exists(_packageFolder))
            {
                initialDir = _packageFolder;
            }
            else
            {
                // Re-parse the log file in the browse box to extract the package folder
                // from the file="..." attribute: C:\Temp\Vendor_App_Ver\PSAppDeployToolkit\PSAppDeployToolkit.psm1
                // → package folder: C:\Temp\Vendor_App_Ver\
                string logPath = TxtLogPath?.Text.Trim() ?? "";
                if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(logPath))
                        {
                            var m = Regex.Match(line,
                                @"file=""([^""]*PSAppDeployToolkit[^""]*\.psm1)""",
                                RegexOptions.IgnoreCase);
                            if (m.Success)
                            {
                                string psm1Dir = IOPath.GetDirectoryName(m.Groups[1].Value) ?? "";
                                string pkgDir  = IOPath.GetDirectoryName(psm1Dir) ?? psm1Dir;
                                if (Directory.Exists(pkgDir)) { initialDir = pkgDir; break; }
                            }
                        }
                    }
                    catch { }

                    // Final fallback: use the log file's own directory
                    if (string.IsNullOrEmpty(initialDir))
                    {
                        string logDir = IOPath.GetDirectoryName(logPath) ?? "";
                        if (Directory.Exists(logDir)) initialDir = logDir;
                    }
                }
            }

            string appName    = InfoAppName?.Text.Trim()   ?? "";
            string appVendor  = InfoPublisher?.Text.Trim() ?? "";
            string appVersion = InfoVersion?.Text.Trim()   ?? "";
            string appRev_m      = _appRevision?.Trim() ?? "";
            string safeName_m    = (appName    == "-" || string.IsNullOrEmpty(appName))    ? "" : Regex.Replace(appName,    @"[^\w\-.]", "");
            string safeVendor_m  = (appVendor  == "-" || string.IsNullOrEmpty(appVendor))  ? "" : Regex.Replace(appVendor,  @"[^\w\-.]", "");
            string safeVersion_m = (appVersion == "-" || string.IsNullOrEmpty(appVersion)) ? "" : Regex.Replace(appVersion, @"[^\w\-.]", "");
            string safeRev_m     = (appRev_m   == "-" || string.IsNullOrEmpty(appRev_m))   ? "" : Regex.Replace(appRev_m,   @"[^\w\-.]", "");
            var defParts = new[] { "ValidatePackage", safeVendor_m, safeName_m, safeVersion_m, safeRev_m, _isUninstallRun ? "Uninstall" : "Install" }
                .Where(p => !string.IsNullOrEmpty(p));
            string defaultName = string.Join("_", defParts);
            if (string.IsNullOrEmpty(defaultName)) defaultName = "ValidatePackage";

            var dlg = new SaveFileDialog
            {
                Title            = "Export Package Validation Report",
                Filter           = "HTML Report|*.html|Text File|*.txt",
                FileName         = defaultName,
                InitialDirectory = initialDir,
            };
            if (dlg.ShowDialog() != true) return;

            bool isHtml = dlg.FilterIndex == 1;
            string content = isHtml ? BuildHtmlReport() : BuildTextReport();

            // Write the report file. If it is open in another program (e.g. Excel/a browser holding
            // the file), File.WriteAllText throws IOException/UnauthorizedAccessException - show a
            // clear message instead of letting the app crash.
            try
            {
                File.WriteAllText(dlg.FileName, content, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                VpStyledMessage(Window.GetWindow(this), "Export - File In Use",
                    "Could not save the report - the file is open in another program. " +
                    $"Close it and export again.\n\n{IOPath.GetFileName(dlg.FileName)}\n{ex.Message}", "⚠");
                return;
            }

            // Always also write a .xlsx alongside the chosen export file. The xlsx is secondary, so if
            // it is locked (commonly because it is open in Excel) warn but still open the report.
            string xlsxPath = IOPath.ChangeExtension(dlg.FileName, ".xlsx");
            try
            {
                BuildXlsxReport(xlsxPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                VpStyledMessage(Window.GetWindow(this), "Export - Xlsx In Use",
                    "The report was saved, but the .xlsx could not be updated because it is open in " +
                    "another program (for example Excel). Close it and export again to refresh the xlsx.\n\n" +
                    $"{IOPath.GetFileName(xlsxPath)}\n{ex.Message}", "⚠");
            }

            try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ValidatePackage] open report failed: " + ex.Message); }
        }

        // ══════════════════════════════════════════════════════════════════
        //  SAVE REPORT  - refreshes both .html and .csv in place, no prompt
        // ══════════════════════════════════════════════════════════════════
        // Resolves the sibling <PackageFolder>\Documents the SAME way AutoExportIfConfigured does,
        // so the Save-Report overwrite lands in the exact folder the Verify auto-export used:
        //   PendingPackageFolder (auto) → GetParent(scriptDir) (manual) → legacy _packageFolder heuristic.
        // Returns "" when no package folder is known (e.g. a standalone verify or import).
        private string ResolvePackageDocsDir()
        {
            string copyRoot = PendingPackageFolder?.Trim() ?? "";
            if (string.IsNullOrEmpty(copyRoot))
            {
                string sd = _resolvedScriptDir?.Trim() ?? "";
                if (!string.IsNullOrEmpty(sd))
                    copyRoot = IOPath.GetDirectoryName(sd) ?? "";   // parent of the script's own folder
            }
            if (string.IsNullOrEmpty(copyRoot))
            {
                string pf = _packageFolder?.Trim() ?? "";
                if (!string.IsNullOrEmpty(pf))
                    copyRoot = IOPath.GetFileName(pf).StartsWith("Package", StringComparison.OrdinalIgnoreCase)
                        ? IOPath.GetDirectoryName(pf) ?? pf
                        : pf;
            }
            return string.IsNullOrEmpty(copyRoot) ? "" : IOPath.Combine(copyRoot, "Documents");
        }

        private void BtnSaveReport_Click(object s, RoutedEventArgs e)
        {
            if (_results.Count == 0)
            {
                VpStyledMessage(Window.GetWindow(this), "Save - No Data",
                    "There are no results to save yet. Run a verification or import a report first.", "ℹ");
                return;
            }

            // Preferred target: the package's Documents folder (overwriting the report created
            // there). Fall back to the recorded run location, then the export folder.
            string htmlPath, xlsxPath;
            string docsDir = ResolvePackageDocsDir();
            if (!string.IsNullOrEmpty(_importSourcePath))
            {
                // Imported data: overwrite the file the user imported from, in that same folder
                htmlPath = IOPath.ChangeExtension(_importSourcePath, ".html");
                xlsxPath = IOPath.ChangeExtension(_importSourcePath, ".xlsx");
            }
            else if (!string.IsNullOrEmpty(docsDir))
            {
                htmlPath = IOPath.Combine(docsDir, BuildDefaultReportName() + ".html");
                xlsxPath = IOPath.ChangeExtension(htmlPath, ".xlsx");
            }
            else if (!string.IsNullOrEmpty(_savedHtmlPath))
            {
                htmlPath = _savedHtmlPath!;
                xlsxPath = !string.IsNullOrEmpty(_savedCsvPath)
                    ? _savedCsvPath! : IOPath.ChangeExtension(htmlPath, ".xlsx");
            }
            else if (!string.IsNullOrEmpty(_liveHtmlPath))
            {
                htmlPath = _liveHtmlPath!;
                xlsxPath = IOPath.ChangeExtension(htmlPath, ".xlsx");
            }
            else
            {
                // ExportPath is mandatory - no C:\Temp fallback. If it is missing there is nowhere
                // valid to save, so tell the user instead of writing to an unrelated location.
                string exportDir = Environment.ExpandEnvironmentVariables(_qaConfig?.ExportPath?.Trim() ?? "");
                if (string.IsNullOrEmpty(exportDir))
                {
                    VpStyledMessage(Window.GetWindow(this), "Save - Export Path Missing",
                        "ExportPath is not set in ValidatePackage.json. Set the Export Path in " +
                        "Settings before saving the report.", "⚠");
                    return;
                }
                htmlPath = IOPath.Combine(exportDir, BuildDefaultReportName() + ".html");
                xlsxPath = IOPath.ChangeExtension(htmlPath, ".xlsx");
            }

            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(htmlPath)!);
                File.WriteAllText(htmlPath, BuildHtmlReport(), Encoding.UTF8);
                BuildXlsxReport(xlsxPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                VpStyledMessage(Window.GetWindow(this), "Save - File In Use",
                    "Could not save - one of the files is open in another program (for example Excel). " +
                    $"Close it and try again.\n\n{ex.Message}", "⚠");
                return;
            }

            QaShowInfoDialog("Save", "💾", $"Saved both reports to:\n\n{IOPath.GetDirectoryName(htmlPath)}");
        }

        // Builds the default report file name: ValidatePackage_<Vendor>_<App>_<Version>_<Revision>_<Install|Uninstall>
        private string BuildDefaultReportName()
        {
            string appName    = InfoAppName?.Text.Trim()   ?? "";
            string appVendor  = InfoPublisher?.Text.Trim() ?? "";
            string appVersion = InfoVersion?.Text.Trim()   ?? "";
            string appRev     = _appRevision?.Trim()       ?? "";
            string safeName    = (appName    == "-" || string.IsNullOrEmpty(appName))    ? "" : Regex.Replace(appName,    @"[^\w\-.]", "");
            string safeVendor  = (appVendor  == "-" || string.IsNullOrEmpty(appVendor))  ? "" : Regex.Replace(appVendor,  @"[^\w\-.]", "");
            string safeVersion = (appVersion == "-" || string.IsNullOrEmpty(appVersion)) ? "" : Regex.Replace(appVersion, @"[^\w\-.]", "");
            string safeRev     = (appRev     == "-" || string.IsNullOrEmpty(appRev))     ? "" : Regex.Replace(appRev,     @"[^\w\-.]", "");
            var parts = new[] { "ValidatePackage", safeVendor, safeName, safeVersion, safeRev, _isUninstallRun ? "Uninstall" : "Install" }
                .Where(p => !string.IsNullOrEmpty(p));
            string name = string.Join("_", parts);
            return string.IsNullOrEmpty(name) ? "ValidatePackage" : name;
        }

        /// <summary>
        /// Auto-exports HTML+xlsx after QA completes.
        /// Uses the mandatory ExportPath from ValidatePackage.json; if it is missing, the auto-export is skipped.
        /// Copies to &lt;PackageFolder&gt;\Documents only when CopyXlsxToDocuments / CopyHtmlToDocuments = "Yes".
        /// </summary>
        private void AutoExportIfConfigured()
        {
            try
            {
                // ── Resolve the sibling <PackageFolder>\Documents root ─────────────
                // Documents is a SIBLING of the app-package folder — i.e. the PARENT of the folder
                // that CONTAINS Invoke-AppDeployToolkit.ps1 — matching where Execute copies its logs
                // (GetParent(scriptDir)\Documents) and how Create Package lays out the tree:
                //   script at <pkg>\Package                 → <pkg>\Documents
                //   script at <pkg>\AppPackages             → <pkg>\Documents
                //   script at <pkg>\Output\AppPackages\Test → <pkg>\Output\AppPackages\Documents
                // Priority: PendingPackageFolder (auto-flow) → GetParent(scriptDir) (manual flow) →
                //           legacy log-derived _packageFolder heuristic (last resort).
                string copyRoot = PendingPackageFolder?.Trim() ?? "";
                if (string.IsNullOrEmpty(copyRoot))
                {
                    string sd = _resolvedScriptDir?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(sd))
                        copyRoot = IOPath.GetDirectoryName(sd) ?? "";   // parent of the script's own folder
                }
                if (string.IsNullOrEmpty(copyRoot))
                {
                    string pf = Dispatcher.Invoke(() => _packageFolder?.Trim() ?? "");
                    if (!string.IsNullOrEmpty(pf))
                        copyRoot = IOPath.GetFileName(pf).StartsWith("Package", StringComparison.OrdinalIgnoreCase)
                            ? IOPath.GetDirectoryName(pf) ?? pf
                            : pf;
                }
                string docsDir = !string.IsNullOrEmpty(copyRoot)
                    ? IOPath.Combine(copyRoot, "Documents")
                    : "";

                // ── Resolve export directory ───────────────────────────────────────
                // ExportPath from ValidatePackage.json is mandatory - there is no C:\Temp fallback.
                // With nothing configured there is no valid export target, so skip the auto-export
                // rather than writing reports to an unrelated location.
                string exportDir = _qaConfig?.ExportPath?.Trim() ?? "";
                exportDir = Environment.ExpandEnvironmentVariables(exportDir);
                if (string.IsNullOrEmpty(exportDir))
                    return;

                Directory.CreateDirectory(exportDir);

                // ── Build filename: ValidatePackage_<Vendor>_<AppName>_<AppVersion>_<AppRevision>_<Install|Uninstall> ──
                string appName2    = Dispatcher.Invoke(() => InfoAppName?.Text.Trim()   ?? "");
                string appVendor2  = Dispatcher.Invoke(() => InfoPublisher?.Text.Trim() ?? "");
                string appVersion2 = Dispatcher.Invoke(() => InfoVersion?.Text.Trim()   ?? "");
                string appRev2     = _appRevision?.Trim() ?? "";

                string safeName    = (appName2    == "-" || string.IsNullOrEmpty(appName2))    ? "" : Regex.Replace(appName2,    @"[^\w\-.]", "");
                string safeVendor  = (appVendor2  == "-" || string.IsNullOrEmpty(appVendor2))  ? "" : Regex.Replace(appVendor2,  @"[^\w\-.]", "");
                string safeVersion = (appVersion2 == "-" || string.IsNullOrEmpty(appVersion2)) ? "" : Regex.Replace(appVersion2, @"[^\w\-.]", "");
                string safeRev     = (appRev2     == "-" || string.IsNullOrEmpty(appRev2))     ? "" : Regex.Replace(appRev2,     @"[^\w\-.]", "");

                string deployModeSuffix = _isUninstallRun ? "Uninstall" : "Install";
                var parts = new[] { "ValidatePackage", safeVendor, safeName, safeVersion, safeRev, deployModeSuffix }
                    .Where(p => !string.IsNullOrEmpty(p));
                string prefix = string.Join("_", parts);

                string htmlOut = IOPath.Combine(exportDir, $"{prefix}.html");
                _liveHtmlPath = htmlOut;
                string xlsxOut = IOPath.Combine(exportDir, $"{prefix}.xlsx");

                File.WriteAllText(htmlOut, BuildHtmlReport(), Encoding.UTF8);
                BuildXlsxReport(xlsxOut);

                // ── Conditionally copy to <PackageFolder>\Documents ──────────────
                // Controlled by CopyHtmlToDocuments and CopyXlsxToDocuments in ValidatePackage.json.
                // Only copies when the respective flag is "Yes" (case-insensitive).
                bool copyHtml = _qaConfig?.ShouldCopyHtml ?? false;
                bool copyXlsx = _qaConfig?.ShouldCopyXlsx ?? false;

                bool toDocs = (copyHtml || copyXlsx) && !string.IsNullOrEmpty(docsDir) && !string.IsNullOrEmpty(copyRoot);
                if (toDocs)
                {
                    Directory.CreateDirectory(docsDir);
                    if (copyHtml) File.Copy(htmlOut, IOPath.Combine(docsDir, IOPath.GetFileName(htmlOut)), overwrite: true);
                    if (copyXlsx) File.Copy(xlsxOut, IOPath.Combine(docsDir, IOPath.GetFileName(xlsxOut)), overwrite: true);
                }

                // Remember where the user-visible reports live so Save overwrites them in place.
                string finalDir = toDocs ? docsDir : exportDir;
                _savedHtmlPath = IOPath.Combine(finalDir, $"{prefix}.html");
                _savedCsvPath = IOPath.Combine(finalDir, $"{prefix}.xlsx");
                // Note: PendingPackageFolder is NOT reset here - it must stay set for the Uninstall
                // log copy in ExecutePackagePage. AutoPackagePage resets it after the full chain ends.
            }
            catch { /* auto-export is best-effort */ }
        }

        // Splits a result's CheckType "Command \u00B7 Type" into the command label and the trailing
        // type (ARPFlags, Property, File, Folder, Registry, Service, etc.). When there is no
        // " \u00B7 " separator the whole string is the command and the type is empty.
        private static (string Command, string Type) SplitCheckType(string checkType)
        {
            if (string.IsNullOrEmpty(checkType)) return ("", "");
            int i = checkType.LastIndexOf(" \u00B7 ", StringComparison.Ordinal);
            return i < 0 ? (checkType, "") : (checkType.Substring(0, i), checkType.Substring(i + 3));
        }

        private string BuildCsvReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("App Name,\""    + (_appName    ?? "-").Replace("\"", "\"\"") + "\"");
            sb.AppendLine("App Vendor,\""  + (_appVendor  ?? "-").Replace("\"", "\"\"") + "\"");
            sb.AppendLine("App Version,\"" + (_appVersion ?? "-").Replace("\"", "\"\"") + "\"");

            // Summary counts (effective status, so Ignore overrides are reflected)
            int passN   = _results.Count(r => EffectiveStatus(r) == QAStatus.Pass);
            int failN   = _results.Count(r => EffectiveStatus(r) == QAStatus.Fail);
            int skipN   = _results.Count(r => EffectiveStatus(r) == QAStatus.Skip);
            int ignoreN = _results.Count(r => EffectiveStatus(r) == QAStatus.Ignore);
            sb.AppendLine($"Pass,{passN}");
            sb.AppendLine($"Fail,{failN}");
            sb.AppendLine($"Skip,{skipN}");
            sb.AppendLine($"Ignore,{ignoreN}");
            sb.AppendLine($"Total,{passN + failN + skipN + ignoreN}");
            sb.AppendLine();
            sb.AppendLine("No.,Commands,Type,Detail,Note,Result");
            foreach (var r in _results)
            {
                // Use EffectiveStatus so Ignore overrides are reflected in CSV
                var eff = EffectiveStatus(r);
                string lbl = eff == QAStatus.Pass   ? "Pass"
                           : eff == QAStatus.Fail   ? "Fail"
                           : eff == QAStatus.Ignore ? "Ignore" : "Skip";
                var (cmdPart, typePart) = SplitCheckType(r.CheckType);
                string detail = "\"" + r.Detail.Replace("\"", "\"\"").Replace("\n", " ") + "\"";
                string note   = "\"" + (r.Note ?? "").Replace("\"", "\"\"") + "\"";
                string cmd    = "\"" + cmdPart.Replace("\"", "\"\"") + "\"";
                string type   = "\"" + typePart.Replace("\"", "\"\"") + "\"";
                sb.AppendLine($"{r.No},{cmd},{type},{detail},{note},{lbl}");
            }
            return sb.ToString();
        }

        // Column letters for the 6-column report layout, indexed 1..6.
        private static readonly string[] XlsxCols = { "A", "B", "C", "D", "E", "F" };

        // Writes a coloured .xlsx report to <path>: Pass rows filled green, Fail rows filled red,
        // matching the column layout of the CSV report. Built as a minimal hand-rolled SpreadsheetML
        // package via System.IO.Compression, so no third-party spreadsheet library is required.
        // Throws IOException/UnauthorizedAccessException when <path> is locked (open in Excel);
        // callers already handle that.
        private void BuildXlsxReport(string path)
        {
            int passN   = _results.Count(r => EffectiveStatus(r) == QAStatus.Pass);
            int failN   = _results.Count(r => EffectiveStatus(r) == QAStatus.Fail);
            int skipN   = _results.Count(r => EffectiveStatus(r) == QAStatus.Skip);
            int ignoreN = _results.Count(r => EffectiveStatus(r) == QAStatus.Ignore);

            // Build the sheet body first so the final row count is known for the dimension ref.
            var body = new StringBuilder();
            int rowNum = 1;
            AppendXlsxKvRow(body, ref rowNum, "App Name",    _appName    ?? "-");
            AppendXlsxKvRow(body, ref rowNum, "App Vendor",  _appVendor  ?? "-");
            AppendXlsxKvRow(body, ref rowNum, "App Version", _appVersion ?? "-");
            AppendXlsxKvRow(body, ref rowNum, "Deployment",  _isUninstallRun ? "Uninstall" : "Install");
            AppendXlsxKvRow(body, ref rowNum, "Pass",   passN.ToString());
            AppendXlsxKvRow(body, ref rowNum, "Fail",   failN.ToString());
            AppendXlsxKvRow(body, ref rowNum, "Skip",   skipN.ToString());
            AppendXlsxKvRow(body, ref rowNum, "Ignore", ignoreN.ToString());
            AppendXlsxKvRow(body, ref rowNum, "Total",  (passN + failN + skipN + ignoreN).ToString());

            // Blank spacer row.
            body.Append("<row r=\"" + rowNum++ + "\"/>");

            // Column header row (style 2 = bold + header fill).
            int hdrRow = rowNum++;
            body.Append("<row r=\"" + hdrRow + "\">");
            // Headers match the page's result grid exactly. No separate Note column: the page has
            // none either - it folds the note into the detail cell - so the note is merged in below
            // and nothing is lost.
            AppendXlsxTextCell(body, hdrRow, 1, "No.",                  2);
            AppendXlsxTextCell(body, hdrRow, 2, "Commands",             2);
            AppendXlsxTextCell(body, hdrRow, 3, "Type",                 2);
            AppendXlsxTextCell(body, hdrRow, 4, "Check List and Paths", 2);
            AppendXlsxTextCell(body, hdrRow, 5, "Result",               2);
            body.Append("</row>");

            // Data rows: Pass -> style 3 (green), Fail -> style 4 (red), others -> style 0 (none).
            foreach (var r in _results)
            {
                var eff = EffectiveStatus(r);
                string lbl = eff == QAStatus.Pass   ? "Pass"
                           : eff == QAStatus.Fail   ? "Fail"
                           : eff == QAStatus.Ignore ? "Ignore" : "Skip";
                int st = eff == QAStatus.Pass ? 3 : eff == QAStatus.Fail ? 4 : 0;
                var (cmdPart, typePart) = SplitCheckType(r.CheckType);
                int dRow = rowNum++;
                body.Append("<row r=\"" + dRow + "\">");
                AppendXlsxNumCell(body,  dRow, 1, r.No, st);
                AppendXlsxTextCell(body, dRow, 2, cmdPart, st);
                AppendXlsxTextCell(body, dRow, 3, typePart, st);
                string detailCell = (r.Detail ?? "").Replace("\n", " ");
                if (!string.IsNullOrEmpty(r.Note))
                    detailCell += r.Status == QAStatus.Fail ? $"  ({r.Note})" : $"  \u26a0 {r.Note}";
                AppendXlsxTextCell(body, dRow, 4, detailCell, st);
                AppendXlsxTextCell(body, dRow, 5, lbl, st);
                body.Append("</row>");
            }
            int lastRow = rowNum - 1;

            var sheet = new StringBuilder();
            sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sheet.Append("<dimension ref=\"A1:E" + lastRow + "\"/>");
            sheet.Append("<cols>");
            sheet.Append("<col min=\"1\" max=\"1\" width=\"6\" customWidth=\"1\"/>");
            sheet.Append("<col min=\"2\" max=\"2\" width=\"30\" customWidth=\"1\"/>");
            sheet.Append("<col min=\"3\" max=\"3\" width=\"12\" customWidth=\"1\"/>");
            sheet.Append("<col min=\"4\" max=\"4\" width=\"90\" customWidth=\"1\"/>");
            sheet.Append("<col min=\"5\" max=\"5\" width=\"10\" customWidth=\"1\"/>");
            sheet.Append("</cols>");
            sheet.Append("<sheetData>");
            sheet.Append(body.ToString());
            sheet.Append("</sheetData></worksheet>");

            const string contentTypes =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>";

            const string relsRoot =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>";

            const string workbook =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Validation\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                "</workbook>";

            const string workbookRels =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>";

            const string styles =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\">" +
                "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                "</fonts>" +
                "<fills count=\"5\">" +
                "<fill><patternFill patternType=\"none\"/></fill>" +
                "<fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFC6EFCE\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFC7CE\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                "</fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"5\">" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"4\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/>" +
                "</cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                "</styleSheet>";

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteXlsxEntry(zip, "[Content_Types].xml",        contentTypes);
                WriteXlsxEntry(zip, "_rels/.rels",                relsRoot);
                WriteXlsxEntry(zip, "xl/workbook.xml",            workbook);
                WriteXlsxEntry(zip, "xl/_rels/workbook.xml.rels", workbookRels);
                WriteXlsxEntry(zip, "xl/styles.xml",              styles);
                WriteXlsxEntry(zip, "xl/worksheets/sheet1.xml",   sheet.ToString());
            }
        }

        private static void WriteXlsxEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(content);
        }

        private static string XlsxEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static void AppendXlsxTextCell(StringBuilder sb, int row, int col, string text, int style)
        {
            string cellRef = XlsxCols[col - 1] + row;
            sb.Append("<c r=\"" + cellRef + "\" s=\"" + style + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"
                      + XlsxEscape(text) + "</t></is></c>");
        }

        private static void AppendXlsxNumCell(StringBuilder sb, int row, int col, int value, int style)
        {
            string cellRef = XlsxCols[col - 1] + row;
            sb.Append("<c r=\"" + cellRef + "\" s=\"" + style + "\"><v>" + value + "</v></c>");
        }

        private void AppendXlsxKvRow(StringBuilder sb, ref int rowNum, string label, string value)
        {
            int row = rowNum++;
            sb.Append("<row r=\"" + row + "\">");
            AppendXlsxTextCell(sb, row, 1, label, 1);
            AppendXlsxTextCell(sb, row, 2, value, 0);
            sb.Append("</row>");
        }

        // ══════════════════════════════════════════════════════════════════
        //  IMPORT  - load a previously exported .html / .csv report onto the page
        // ══════════════════════════════════════════════════════════════════
        private void BtnImportReport_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Import Package Validation Report",
                Filter = "Report files (*.html;*.htm;*.csv)|*.html;*.htm;*.csv|HTML report|*.html;*.htm|CSV file|*.csv",
            };
            if (dlg.ShowDialog() != true) return;

            List<QAResult> imported;
            string impName = "-", impVendor = "-", impVersion = "-";
            try
            {
                string text = File.ReadAllText(dlg.FileName);
                string ext  = IOPath.GetExtension(dlg.FileName).ToLowerInvariant();
                imported = ext == ".csv"
                    ? ParseCsvReport(text, out impName, out impVendor, out impVersion)
                    : ParseHtmlReport(text, out impName, out impVendor, out impVersion);
            }
            catch (Exception ex)
            {
                VpStyledMessage(Window.GetWindow(this), "Import - Read Failed",
                    $"Could not read the report file.\n\n{IOPath.GetFileName(dlg.FileName)}\n{ex.Message}", "⚠");
                return;
            }

            if (imported.Count == 0)
            {
                VpStyledMessage(Window.GetWindow(this), "Import - No Data",
                    "No result rows were found in the selected file. Make sure it is a report " +
                    "exported by Validate Package (.html or .csv).", "ℹ");
                return;
            }

            // ── Reset the current view, then load the imported rows ──────────────
            _results.Clear();
            _rowCache.Clear();
            _statusOverrides.Clear();
            _selectedRowBorder = null;
            _liveHtmlPath      = null;
            _savedHtmlPath = _savedCsvPath = null;
            _filterMode        = FilterMode.All;
            _qaSearchText      = "";
            _qaSearchMatches.Clear();
            _visibleRows.Clear();   // ItemsSource is bound - clear the source, not Items
            CommandNavPanel.Children.Clear();
            TxtQASearch.Clear();

            // Title reflects the run type when the export filename carries the suffix
            _isUninstallRun = IOPath.GetFileNameWithoutExtension(dlg.FileName)
                                    .EndsWith("Uninstall", StringComparison.OrdinalIgnoreCase);

            _results.AddRange(imported);

            // Remember the imported file - Save will overwrite it (and its sibling) in place
            _importSourcePath = dlg.FileName;

            // Restore app name / vendor / version captured from the report
            _appName   = impName;
            _appVendor = impVendor;
            _appVersion = impVersion;
            _appRevision = "-";   // not stored in the report - clear it so a stale revision cannot leak into the export name
            InfoAppName.Text   = impName;
            InfoPublisher.Text = impVendor;
            InfoVersion.Text   = impVersion;

            RenderResults();
        }

        // Parses a Validate Package CSV report. Pre-header "App Name/Vendor/Version" rows
        // (if present) are returned via the out parameters; the table header is "No.,...".
        private List<QAResult> ParseCsvReport(string text, out string appName, out string appVendor, out string appVersion)
        {
            appName = appVendor = appVersion = "-";
            var list = new List<QAResult>();
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int idx = 1;
            bool inRows = false;
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var f = SplitCsvLine(raw);

                if (!inRows)
                {
                    // The "No." header row switches us into row-parsing mode
                    if (f.Count >= 6 && f[0].Trim().Equals("No.", StringComparison.OrdinalIgnoreCase))
                    {
                        inRows = true;
                        continue;
                    }
                    // Metadata key,value rows that precede the header
                    if (f.Count >= 2)
                    {
                        string key = f[0].Trim();
                        if      (key.Equals("App Name",    StringComparison.OrdinalIgnoreCase)) appName    = DashIfEmpty(f[1]);
                        else if (key.Equals("App Vendor",  StringComparison.OrdinalIgnoreCase)) appVendor  = DashIfEmpty(f[1]);
                        else if (key.Equals("App Version", StringComparison.OrdinalIgnoreCase)) appVersion = DashIfEmpty(f[1]);
                    }
                    continue;
                }

                if (f.Count < 6) continue;   // malformed row - skip

                if (!int.TryParse(f[0].Trim(), out int no)) no = idx;
                string checkType = string.IsNullOrEmpty(f[2]) ? f[1] : f[1] + " \u00B7 " + f[2];
                list.Add(new QAResult(no, checkType, f[3], ParseStatusLabel(f[5]),
                                      string.IsNullOrEmpty(f[4]) ? null : f[4]));
                idx++;
            }
            return list;
        }

        // Splits one CSV line into fields, honouring "quoted" values and doubled "" quotes.
        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields;
        }

        // Parses a Validate Package HTML report by reading the rows of its results table.
        // App Name/Vendor/Version are read from the header meta line via the out parameters.
        private List<QAResult> ParseHtmlReport(string html, out string appName, out string appVendor, out string appVersion)
        {
            appName    = HtmlMeta(html, "App Name");
            appVendor  = HtmlMeta(html, "App Vendor");
            appVersion = HtmlMeta(html, "App Version");

            var list = new List<QAResult>();
            var bodyM = Regex.Match(html, "<tbody>(.*?)</tbody>",
                                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
            string body = bodyM.Success ? bodyM.Groups[1].Value : html;

            int idx = 1;
            foreach (Match tr in Regex.Matches(body, "<tr[^>]*>(.*?)</tr>",
                                               RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var cells = Regex.Matches(tr.Groups[1].Value, "<td[^>]*>(.*?)</td>",
                                          RegexOptions.Singleline | RegexOptions.IgnoreCase)
                                 .Select(m => m.Groups[1].Value).ToList();
                if (cells.Count < 6) continue;

                if (!int.TryParse(HtmlCellText(cells[0]), out int no)) no = idx;
                string cmd    = HtmlCellText(cells[1]);
                string type   = HtmlCellText(cells[2]);
                string detail = HtmlCellText(cells[3].Replace("<br/>", "\n").Replace("<br>", "\n"));
                string note   = HtmlCellText(cells[4]).TrimStart('\u26A0', ' ').Trim();
                QAStatus st   = ParseStatusLabel(HtmlCellText(cells[5]));

                string checkType = string.IsNullOrEmpty(type) ? cmd : cmd + " \u00B7 " + type;
                list.Add(new QAResult(no, checkType, detail, st,
                                      string.IsNullOrEmpty(note) ? null : note));
                idx++;
            }
            return list;
        }

        // Strips HTML tags from a table cell and decodes the entities Esc() emits.
        private static string HtmlCellText(string cell)
        {
            string s = Regex.Replace(cell, "<[^>]+>", "");
            return s.Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&amp;", "&").Trim();
        }

        // Reads a "<label>: <b>value</b>" pair from the HTML report header meta line.
        private static string HtmlMeta(string html, string label)
        {
            var m = Regex.Match(html, Regex.Escape(label) + @":\s*<b>(.*?)</b>",
                                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return m.Success ? DashIfEmpty(HtmlCellText(m.Groups[1].Value)) : "-";
        }

        // Returns a trimmed value, or "-" when blank.
        private static string DashIfEmpty(string v) => string.IsNullOrWhiteSpace(v) ? "-" : v.Trim();

        // Maps a Result label ("Pass"/"Fail"/"Ignore"/"Skip") back to a QAStatus.
        private static QAStatus ParseStatusLabel(string label)
        {
            string v = label.Trim();
            if (v.Equals("Pass",   StringComparison.OrdinalIgnoreCase)) return QAStatus.Pass;
            if (v.Equals("Fail",   StringComparison.OrdinalIgnoreCase)) return QAStatus.Fail;
            if (v.Equals("Ignore", StringComparison.OrdinalIgnoreCase)) return QAStatus.Ignore;
            return QAStatus.Skip;
        }

        // ══════════════════════════════════════════════════════════════════
        //  SEARCH IN RESULTS  - word-level inline highlighting
        // ══════════════════════════════════════════════════════════════════
        // Orange = current match occurrence, Yellow = all other occurrences
        private static readonly Brush SearchOrangeBg = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
        private static readonly Brush SearchYellowBg = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x76));
        // Map: result-list index → the detail TextBlock in that row
        private readonly Dictionary<int, TextBlock> _rowDetailTbs = new();

        private void TxtQASearch_TextChanged(object s, TextChangedEventArgs e)
        {
            _qaSearchText = _txtQASearch?.Text ?? "";
            // Show/hide placeholder text
            if (FindName("TxtSearchPlaceholder") is TextBlock ph)
                ph.Visibility = string.IsNullOrEmpty(_qaSearchText)
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            // Clear immediately so UI feels responsive
            ClearWordHighlights();
            _qaSearchIdx = -1;
            _qaSearchMatches.Clear();
            if (_txtQASearchCount != null) _txtQASearchCount.Text = "";
            if (string.IsNullOrEmpty(_qaSearchText)) return;

            // Debounce: wait 300ms after last keystroke before searching
            _searchDebounce?.Stop();
            _searchDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce?.Stop();
                _searchDebounce = null;
                BuildQASearchMatches();
                if (_qaSearchMatches.Count > 0) { _qaSearchIdx = 0; ApplyWordHighlights(); ScrollToSearchMatch(); }
            };
            _searchDebounce.Start();
        }

        private void BtnQASearchPrev_Click(object s, RoutedEventArgs e)
        {
            if (_qaSearchMatches.Count == 0) { BuildQASearchMatches(); ApplyWordHighlights(); }
            if (_qaSearchMatches.Count == 0) return;
            int prev = _qaSearchIdx - 1;
            if (prev < 0)
            {
                bool again = QaShowSearchWrapDialog("Search completed. Do you want to search again?");
                if (!again) { QaClearSearch(); return; }
                prev = _qaSearchMatches.Count - 1;
            }
            _qaSearchIdx = prev;
            ApplyWordHighlights();
            ScrollToSearchMatch();
        }

        private void BtnQASearchNext_Click(object s, RoutedEventArgs e)
        {
            if (_qaSearchMatches.Count == 0) { BuildQASearchMatches(); ApplyWordHighlights(); }
            if (_qaSearchMatches.Count == 0) return;
            int next = _qaSearchIdx + 1;
            if (next >= _qaSearchMatches.Count)
            {
                bool again = QaShowSearchWrapDialog("Search completed. Do you want to search again?");
                if (!again) { QaClearSearch(); return; }
                next = 0;
            }
            _qaSearchIdx = next;
            ApplyWordHighlights();
            ScrollToSearchMatch();
        }

        /// <summary>Clears search state and resets the search box.</summary>
        private void QaClearSearch()
        {
            ClearWordHighlights();
            _qaSearchMatches.Clear();
            _qaSearchIdx = -1;
            if (_txtQASearch != null) _txtQASearch.Text = "";
            if (_txtQASearchCount != null) _txtQASearchCount.Text = "";
            if (FindName("TxtSearchPlaceholder") is TextBlock ph) ph.Visibility = System.Windows.Visibility.Visible;
        }

        /// <summary>
        /// Styled wrap-around search dialog - matches Source Validation feature exactly:
        /// dark title bar, "Status" label + value row, ❓ icon, ✔ Yes / ✕ No buttons.
        /// </summary>
        // ══════════════════════════════════════════════════════════════
        //  VALIDATION MESSAGE DIALOG  (standard themed template)
        // ══════════════════════════════════════════════════════════════
        private void ShowQaValidationDialog(string title, string message, string icon = "⚠️")
        {
            var winBg     = Application.Current.TryFindResource("NavBarBgBrush") as Brush
                            ?? new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF5));
            var borderClr = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));

            var win = new Window
            {
                Title = title, Width = 460, MaxWidth = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false,
            };

            var outer = new Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            win.Content = outer;

            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));
            Brush titleTxt = Brushes.White;
            Brush titleSec = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            var tb = new Border
            {
                Background = titleBarBg,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? Brushes.LightGray,
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 6, 12, 6),
            };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);

            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ti  = new TextBlock { Text = "☑", FontSize = 14, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            var tt  = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button   { Content = "✕", FontSize = 11, Foreground = titleSec, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6,2,6,2) };
            tcl.Click += (_, _) => win.Close();

            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg;
            og.Children.Add(tb);

            var cg = new Grid { Margin = new Thickness(8), Background = Brushes.Transparent };
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cg, 1);
            og.Children.Add(cg);

            var boxBg  = Application.Current.TryFindResource("WindowBgBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")  as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextPrimaryBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));
            var sec    = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;

            int r = 0;
            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); r++;

            // Message row
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var msgLbl = new TextBlock { Text = "Message", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var msgBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10,6,10,6) };
            msgBox.Child = new TextBlock { Text = message, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = lblFg, FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
            Grid.SetRow(msgLbl, r); Grid.SetColumn(msgLbl, 0);
            Grid.SetRow(msgBox, r); Grid.SetColumn(msgBox, 1);
            cg.Children.Add(msgLbl); cg.Children.Add(msgBox); r++;

            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); r++;

            // Icon + OK button row
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = VpDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);

            var okBtn = new Button { Content = "✔  OK", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), IsDefault = true };
            VpApplyPrimaryLook(okBtn);
            okBtn.Click += (_, _) => win.Close();
            Grid.SetRow(okBtn, r); Grid.SetColumn(okBtn, 1);
            cg.Children.Add(okBtn); r++;

            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            win.ShowDialog();
        }

        // Themed information dialog (single OK) matching the page's other custom dialogs.
        private void QaShowInfoDialog(string title, string titleIcon, string message)
        {
            var winBg     = Application.Current.TryFindResource("NavBarBgBrush") as Brush
                            ?? new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF5));
            var borderClr = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));

            var win = new Window
            {
                Title = title,
                Width = 440, MaxWidth = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false,
            };

            var outer = new Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            win.Content = outer;

            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));
            Brush titleTxt = Brushes.White;
            Brush titleSec = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            var tb = new Border
            {
                Background = titleBarBg,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? Brushes.LightGray,
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 6, 12, 6),
            };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);

            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ti  = new TextBlock { Text = titleIcon, FontSize = 14, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            var tt  = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button   { Content = "✕", FontSize = 11, Foreground = titleSec, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6,2,6,2) };
            tcl.Click += (_, _) => win.Close();

            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg;
            og.Children.Add(tb);

            // Content: info icon + message, then a right-aligned OK button
            var cg = new Grid { Margin = new Thickness(18, 16, 18, 14), Background = Brushes.Transparent };
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cg, 1);
            og.Children.Add(cg);

            var lblFg = Application.Current.TryFindResource("TextPrimaryBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));
            var sec   = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;

            var infoIcon = VpDecisionIcon();
            infoIcon.VerticalAlignment = VerticalAlignment.Top;
            infoIcon.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetRow(infoIcon, 0); Grid.SetColumn(infoIcon, 0);
            cg.Children.Add(infoIcon);

            var msg = new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(msg, 0); Grid.SetColumn(msg, 1);
            cg.Children.Add(msg);

            var okBtn = new Button { Content = "✔  OK", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, MinWidth = 84, Padding = new Thickness(12,0,12,0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            VpApplyPrimaryLook(okBtn);
            okBtn.Click += (_, _) => win.Close();
            Grid.SetRow(okBtn, 2); Grid.SetColumn(okBtn, 1);
            cg.Children.Add(okBtn);

            win.ShowDialog();
        }

        private bool QaShowSearchWrapDialog(string message)
            => QaShowConfirmDialog("Package Validation - Search", message);

        // Themed Yes/No confirmation dialog: Status row, blue decision icon and a
        // highlighted Yes button. Shared by the search-wrap prompt and the New button.
        private bool QaShowConfirmDialog(string title, string message)
        {
            bool result = false;

            // ── Window shell (mirrors SvBuildDialog) ──────────────────────────────
            var winBg     = Application.Current.TryFindResource("NavBarBgBrush") as Brush
                            ?? new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF5));
            var borderClr = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));

            var win = new Window
            {
                Title = title,
                Width = 380, MaxWidth = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false,
            };

            var outer = new Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            win.Content = outer;

            // ── Title bar (mirrors SvBuildDialogShell) ────────────────────────────
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));
            Brush titleTxt = Brushes.White;
            Brush titleSec = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            var tb = new Border
            {
                Background = titleBarBg,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? Brushes.LightGray,
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 6, 12, 6),
            };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);

            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ti  = new TextBlock { Text = "🔍", FontSize = 14, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            var tt  = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button   { Content = "✕", FontSize = 11, Foreground = titleSec, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6,2,6,2) };
            tcl.Click += (_, _) => { result = false; win.Close(); };

            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg;
            og.Children.Add(tb);

            // ── Content grid (mirrors SvBuildDialogShell cg) ─────────────────────
            var cg = new Grid { Margin = new Thickness(8), Background = Brushes.Transparent };
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cg, 1);
            og.Children.Add(cg);

            var boxBg  = Application.Current.TryFindResource("WindowBgBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")  as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextPrimaryBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));
            var valFg  = lblFg;
            var sec    = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;

            int row = 0;

            // Spacer top
            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            row++;

            // Status row (label + value box - mirrors SvNdAddRow)
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var statusLbl = new TextBlock { Text = "Status", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var statusBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10,6,10,6) };
            statusBox.Child = new TextBlock { Text = message, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg, FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
            Grid.SetRow(statusLbl, row); Grid.SetColumn(statusLbl, 0);
            Grid.SetRow(statusBox, row); Grid.SetColumn(statusBox, 1);
            cg.Children.Add(statusLbl); cg.Children.Add(statusBox);
            row++;

            // Spacer
            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            row++;

            // ❓ icon + buttons row (mirrors SvShowSearchWrapDialog)
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = VpDecisionIcon();
            Grid.SetRow(rowIcon, row); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);

            var ug = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };
            var btnYes = new Button { Content = "✔  Yes", Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(0,0,4,0) };
            var btnNo  = new Button { Content = "✕  No",  Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = new Thickness(4,0,0,0) };
            VpApplyPrimaryLook(btnYes);
            btnYes.Click += (_, _) => { result = true;  win.Close(); };
            btnNo.Click  += (_, _) => { result = false; win.Close(); };
            ug.Children.Add(btnYes); ug.Children.Add(btnNo);
            Grid.SetRow(ug, row); Grid.SetColumn(ug, 1);
            cg.Children.Add(ug);
            row++;

            // Spacer bottom
            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            win.ShowDialog();
            return result;
        }

        private void BuildQASearchMatches()
        {
            _qaSearchMatches.Clear();
            if (string.IsNullOrEmpty(_qaSearchText)) { if (_txtQASearchCount != null) _txtQASearchCount.Text = ""; return; }
            for (int i = 0; i < _results.Count; i++)
            {
                var r = _results[i];
                // Only search within rows visible in the current filter
                bool visibleInFilter = _filterMode switch
                {
                    FilterMode.PassOnly   => EffectiveStatus(r) == QAStatus.Pass,
                    FilterMode.FailOnly   => EffectiveStatus(r) == QAStatus.Fail,
                    FilterMode.SkipOnly   => EffectiveStatus(r) == QAStatus.Skip,
                    FilterMode.IgnoreOnly => EffectiveStatus(r) == QAStatus.Ignore,
                    _                     => true,
                };
                if (!visibleInFilter) continue;
                if (r.Detail.IndexOf(_qaSearchText, StringComparison.OrdinalIgnoreCase) >= 0
                 || r.CheckType.IndexOf(_qaSearchText, StringComparison.OrdinalIgnoreCase) >= 0
                 || (r.Note?.IndexOf(_qaSearchText, StringComparison.OrdinalIgnoreCase) >= 0))
                    _qaSearchMatches.Add(i);
            }
            UpdateQASearchCount();
        }

        /// <summary>
        /// Clears all word-level highlights by restoring each detail TextBlock to plain text.
        /// </summary>
        private void ClearWordHighlights()
        {
            foreach (var kv in _rowDetailTbs)
            {
                var tb  = kv.Value;
                var res = kv.Key < _results.Count ? _results[kv.Key] : null;
                if (res == null) continue;
                string detailText = res.Detail;
                if (!string.IsNullOrEmpty(res.Note))
                    detailText += res.Status == QAStatus.Fail ? $"  ({res.Note})" : $"\n  \u26a0 {res.Note}";
                // Restore plain text with original status colour (must match BuildResultRow)
                tb.Inlines.Clear();
                var effStatus = EffectiveStatus(res);
                Brush fg = effStatus == QAStatus.Pass
                    ? new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x44))   // green
                    : effStatus == QAStatus.Fail
                    ? new SolidColorBrush(Color.FromRgb(0xE5, 0x35, 0x35))   // red
                    : effStatus == QAStatus.Ignore
                    ? new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5))   // blue
                    : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA8));  // grey (skip)
                tb.Inlines.Add(new Run(detailText) { Foreground = fg });
            }
        }

        /// <summary>
        /// Applies word-level inline highlights to every matched detail TextBlock.
        /// Current match occurrence = orange, all others = yellow.
        /// </summary>
        private void ApplyWordHighlights()
        {
            ClearWordHighlights();
            if (string.IsNullOrEmpty(_qaSearchText) || _qaSearchMatches.Count == 0) return;

            for (int matchPos = 0; matchPos < _qaSearchMatches.Count; matchPos++)
            {
                int resultIdx = _qaSearchMatches[matchPos];
                if (!_rowDetailTbs.TryGetValue(resultIdx, out var tb)) continue;
                var res = _results[resultIdx];
                string detailText = res.Detail;
                if (!string.IsNullOrEmpty(res.Note))
                    detailText += res.Status == QAStatus.Fail ? $"  ({res.Note})" : $"\n  \u26a0 {res.Note}";

                bool isCurrent = (matchPos == _qaSearchIdx);
                var effStatus2 = EffectiveStatus(res);
                Brush fg = effStatus2 == QAStatus.Pass
                    ? new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x44))   // green
                    : effStatus2 == QAStatus.Fail
                    ? new SolidColorBrush(Color.FromRgb(0xE5, 0x35, 0x35))   // red
                    : effStatus2 == QAStatus.Ignore
                    ? new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5))   // blue
                    : new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA8));  // grey (skip)

                tb.Inlines.Clear();
                int pos = 0;
                bool firstHitDone = false;
                while (pos <= detailText.Length)
                {
                    int hit = detailText.IndexOf(_qaSearchText, pos, StringComparison.OrdinalIgnoreCase);
                    if (hit < 0)
                    {
                        // Remainder as normal text
                        if (pos < detailText.Length)
                            tb.Inlines.Add(new Run(detailText[pos..]) { Foreground = fg });
                        break;
                    }
                    // Text before hit
                    if (hit > pos)
                        tb.Inlines.Add(new Run(detailText[pos..hit]) { Foreground = fg });
                    // The matched word
                    string matchedWord = detailText.Substring(hit, _qaSearchText.Length);
                    Brush hilite = (isCurrent && !firstHitDone) ? SearchOrangeBg : SearchYellowBg;
                    tb.Inlines.Add(new Run(matchedWord)
                    {
                        Foreground = Brushes.Black,
                        Background = hilite,
                    });
                    firstHitDone = true;
                    pos = hit + _qaSearchText.Length;
                }
            }
            UpdateQASearchCount();
        }

        private void ScrollToSearchMatch()
        {
            if (_qaSearchIdx < 0 || _qaSearchIdx >= _qaSearchMatches.Count) return;
            int resultIdx = _qaSearchMatches[_qaSearchIdx];

            // _qaSearchMatches holds indices into _results, and _rowCache is built in _results order,
            // so the row is a direct lookup. The old code counted Borders in the panel to find it -
            // which a virtualizing ListBox makes impossible, since the off-screen rows have no
            // containers to count.
            if (resultIdx < 0 || resultIdx >= _rowCache.Count) return;
            ScrollRowIntoView(_rowCache[resultIdx].Row);
            UpdateQASearchCount();
        }

        private void UpdateQASearchCount()
        {
            int total = _qaSearchMatches.Count;
            int cur   = total == 0 ? 0 : _qaSearchIdx + 1;
            if (_txtQASearchCount != null)
                _txtQASearchCount.Text = total > 0 ? $"{cur}/{total}" : "0";
        }

        /// <summary>
        /// Silently overwrites the live HTML report file (if one was auto-generated
        /// this session) so the browser tab can be refreshed to see Ignore changes.
        /// </summary>
        private void RefreshLiveHtml()
        {
            if (string.IsNullOrEmpty(_liveHtmlPath)) return;
            try
            {
                File.WriteAllText(_liveHtmlPath, BuildHtmlReport(), Encoding.UTF8);
                // Also update the xlsx so Ignore status is reflected
                string xlsxPath = IOPath.ChangeExtension(_liveHtmlPath, ".xlsx");
                if (File.Exists(xlsxPath))
                    BuildXlsxReport(xlsxPath);
            }
            catch { /* silently ignore write errors */ }
        }

        private string BuildHtmlReport()
        {
            int pass   = _results.Count(r => EffectiveStatus(r) == QAStatus.Pass);
            int fail   = _results.Count(r => EffectiveStatus(r) == QAStatus.Fail);
            int skip   = _results.Count(r => EffectiveStatus(r) == QAStatus.Skip);
            int ignore = _results.Count(r => EffectiveStatus(r) == QAStatus.Ignore);
            int total  = pass + fail + ignore;
            double passPct   = total == 0 ? 0 : Math.Round(pass   * 100.0 / total, 1);
            double failPct   = total == 0 ? 0 : Math.Round(fail   * 100.0 / total, 1);
            double ignorePct = total == 0 ? 0 : Math.Round(ignore * 100.0 / total, 1);

            // Build per-command-type breakdown for the horizontal bar chart
            var groups = _results
                .Where(r => EffectiveStatus(r) == QAStatus.Pass || EffectiveStatus(r) == QAStatus.Fail
                         || EffectiveStatus(r) == QAStatus.Ignore)
                .GroupBy(r =>
                {
                    // Strip the line-number suffix to get a clean category label
                    // e.g. "Start-ADTMsiProcess (167) · File" → "· File"
                    var m = Regex.Match(r.CheckType, @"·\s*(.+)$");
                    return m.Success ? "· " + m.Groups[1].Value.Trim() : r.CheckType;
                })
                .Select(g => new {
                    Label  = g.Key,
                    Pass   = g.Count(x => EffectiveStatus(x) == QAStatus.Pass),
                    Fail   = g.Count(x => EffectiveStatus(x) == QAStatus.Fail),
                    Ignore = g.Count(x => EffectiveStatus(x) == QAStatus.Ignore),
                })
                .OrderByDescending(g => g.Pass + g.Fail)
                .Take(12)   // top 12 categories to keep chart readable
                .ToList();

            // Deployment phase (Install vs Uninstall) so the report is self-identifying.
            // Uses the same _isUninstallRun flag that drives the filename and on-screen summary.
            string phase      = _isUninstallRun ? "Uninstall" : "Install";
            string phaseClass = _isUninstallRun ? "phase-uninstall" : "phase-install";

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
            sb.AppendLine($"<title>SwiftPack QA Report - {phase}</title><style>");
            sb.AppendLine("*{box-sizing:border-box;margin:0;padding:0}");
            sb.AppendLine("body{font-family:'Segoe UI',sans-serif;font-size:12px;background:#f0f2f5;color:#333}");
            sb.AppendLine(".page{max-width:1400px;margin:0 auto;padding:24px}");
            sb.AppendLine("h1{font-size:22px;font-weight:700;margin-bottom:4px;color:#1a1a2e}");
            sb.AppendLine(".meta{font-size:11px;color:#666;margin-bottom:20px}");
            sb.AppendLine(".meta b{color:#333}");
            // Phase pill (Install / Uninstall) shown next to the report title
            sb.AppendLine(".phase{display:inline-block;font-size:12px;font-weight:700;letter-spacing:.5px;padding:3px 12px;border-radius:6px;vertical-align:middle;margin-left:10px}");
            sb.AppendLine(".phase-install{background:#e3f2fd;color:#1565c0;border:1.5px solid #90caf9}");
            sb.AppendLine(".phase-uninstall{background:#f3e5f5;color:#6a1b9a;border:1.5px solid #ce93d8}");
            // Top section: summary badges + chart side by side
            sb.AppendLine(".top{display:flex;gap:24px;margin-bottom:24px;align-items:flex-start}");
            sb.AppendLine(".left-panel{display:flex;flex-direction:column;gap:12px;min-width:220px}");
            sb.AppendLine(".badges{display:flex;gap:12px}");
            sb.AppendLine(".badge{flex:1;padding:16px 20px;border-radius:10px;font-weight:700;font-size:26px;text-align:center;line-height:1.2}");
            sb.AppendLine(".badge span{display:block;font-size:11px;font-weight:600;margin-top:2px;letter-spacing:.5px}");
            sb.AppendLine(".pass-b{background:#e8f5e9;border:1.5px solid #a5d6a7;color:#2e7d32}");
            sb.AppendLine(".fail-b{background:#ffebee;border:1.5px solid #ef9a9a;color:#c62828}");
            sb.AppendLine(".skip-b{background:#f5f5f5;border:1.5px solid #bdbdbd;color:#757575}");
            sb.AppendLine(".ign-b{background:#fff8e1;border:1.5px solid #ffe082;color:#e65100}");
            // Stacked progress bar
            sb.AppendLine(".bar-wrap{background:#fff;border-radius:10px;padding:16px;box-shadow:0 1px 4px #0001}");
            sb.AppendLine(".bar-wrap h3{font-size:12px;font-weight:600;color:#555;margin-bottom:10px;text-transform:uppercase;letter-spacing:.5px}");
            sb.AppendLine(".bar-total{height:22px;border-radius:6px;overflow:hidden;display:flex;margin-bottom:8px}");
            sb.AppendLine(".bar-pass{background:#43a047;height:100%}");
            sb.AppendLine(".bar-fail{background:#e53935;height:100%}");
            sb.AppendLine(".bar-ign{background:#ff9800;height:100%}");
            sb.AppendLine(".bar-legend{display:flex;gap:16px;font-size:11px}");
            sb.AppendLine(".dot{width:10px;height:10px;border-radius:50%;display:inline-block;margin-right:4px}");
            sb.AppendLine(".dot-p{background:#43a047}.dot-f{background:#e53935}.dot-i{background:#ff9800}");
            // Chart (horizontal bars per category)
            sb.AppendLine(".chart-wrap{flex:1;background:#fff;border-radius:10px;padding:20px;box-shadow:0 1px 4px #0001}");
            sb.AppendLine(".chart-wrap h3{font-size:12px;font-weight:600;color:#555;margin-bottom:14px;text-transform:uppercase;letter-spacing:.5px}");
            sb.AppendLine(".chart-row{display:flex;align-items:center;margin-bottom:8px;gap:8px}");
            sb.AppendLine(".chart-label{width:160px;font-size:10px;color:#555;text-align:right;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;flex-shrink:0}");
            sb.AppendLine(".chart-bars{flex:1;display:flex;height:16px;border-radius:4px;overflow:hidden;background:#f5f5f5}");
            sb.AppendLine(".chart-pass{background:#43a047;height:100%}");
            sb.AppendLine(".chart-fail{background:#e53935;height:100%}");
            sb.AppendLine(".chart-count{width:50px;font-size:10px;color:#777;text-align:left;flex-shrink:0}");
            // Table
            sb.AppendLine("table{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 4px #0001}");
            sb.AppendLine("th{background:#eeeeee;padding:9px 12px;text-align:left;font-size:11px;font-weight:600;color:#555;text-transform:uppercase;letter-spacing:.3px}");
            sb.AppendLine("td{padding:7px 12px;border-bottom:1px solid #f0f0f0;vertical-align:top;font-family:Consolas,monospace;font-size:11px}");
            sb.AppendLine("tr:last-child td{border-bottom:none}");
            sb.AppendLine("tr.fail-row{background:#fff8f8}");
            sb.AppendLine("tr.ign-row{background:#fffde7}");
            sb.AppendLine(".p{background:#e8f5e9;color:#2e7d32;border-radius:4px;padding:2px 10px;font-weight:700;font-size:11px}");
            sb.AppendLine(".f{background:#ffebee;color:#c62828;border-radius:4px;padding:2px 10px;font-weight:700;font-size:11px}");
            sb.AppendLine(".s{background:#f5f5f5;color:#999;border-radius:4px;padding:2px 10px;font-weight:700;font-size:11px}");
            sb.AppendLine(".i{background:#fff8e1;color:#e65100;border-radius:4px;padding:2px 10px;font-weight:700;font-size:11px}");
            sb.AppendLine(".note{color:#c62828;font-size:10px;margin-top:3px}");
            sb.AppendLine("</style></head><body><div class='page'>");

            // Header
            sb.AppendLine($"<h1>SwiftPack QA Report <span class='phase {phaseClass}'>{phase.ToUpperInvariant()}</span></h1>");
            sb.AppendLine($"<div class='meta'>App Name: <b>{Esc(_appName)}</b> &nbsp;|&nbsp; App Vendor: <b>{Esc(_appVendor)}</b> &nbsp;|&nbsp; App Version: <b>{Esc(_appVersion)}</b></div>");
            sb.AppendLine($"<div class='meta'>Package: <b>{Esc(_packageFolder)}</b> &nbsp;|&nbsp; Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");

            // Top section
            sb.AppendLine("<div class='top'>");

            // Left: badges + stacked bar
            sb.AppendLine("<div class='left-panel'>");
            sb.AppendLine("<div class='badges'>");
            sb.AppendLine($"<div class='badge pass-b'>✓ {pass}<span>PASS</span></div>");
            sb.AppendLine($"<div class='badge fail-b'>✗ {fail}<span>FAIL</span></div>");
            if (skip   > 0) sb.AppendLine($"<div class='badge skip-b'>&raquo; {skip}<span>SKIP</span></div>");
            if (ignore > 0) sb.AppendLine($"<div class='badge ign-b'>⊘ {ignore}<span>IGNORE</span></div>");
            sb.AppendLine("</div>");

            // Stacked pass/fail bar
            sb.AppendLine("<div class='bar-wrap'>");
            sb.AppendLine("<h3>Pass vs Fail</h3>");
            double passW   = total == 0 ? 50 : passPct;
            double failW   = total == 0 ? 50 : failPct;
            double ignoreW = total == 0 ? 0  : ignorePct;
            sb.AppendLine($"<div class='bar-total'><div class='bar-pass' style='width:{passW}%'></div><div class='bar-fail' style='width:{failW}%'></div><div class='bar-ign' style='width:{ignoreW}%'></div></div>");
            sb.AppendLine("<div class='bar-legend'>");
            sb.AppendLine($"<span><span class='dot dot-p'></span>Pass {passPct}%</span>");
            sb.AppendLine($"<span><span class='dot dot-f'></span>Fail {failPct}%</span>");
            if (ignore > 0) sb.AppendLine($"<span><span class='dot dot-i'></span>Ignore {ignorePct}%</span>");
            sb.AppendLine("</div></div></div>"); // end bar-wrap, left-panel

            // Right: horizontal bar chart by category
            sb.AppendLine("<div class='chart-wrap'>");
            sb.AppendLine("<h3>Results by Check Type</h3>");
            int maxTotal = groups.Count > 0 ? groups.Max(g => g.Pass + g.Fail) : 1;
            foreach (var g in groups)
            {
                int gTotal = g.Pass + g.Fail;
                double gPassW = maxTotal == 0 ? 0 : g.Pass  * 100.0 / maxTotal;
                double gFailW = maxTotal == 0 ? 0 : g.Fail  * 100.0 / maxTotal;
                sb.AppendLine("<div class='chart-row'>");
                sb.AppendLine($"<div class='chart-label' title='{Esc(g.Label)}'>{Esc(g.Label)}</div>");
                double gIgnW = maxTotal == 0 ? 0 : g.Ignore * 100.0 / maxTotal;
                sb.AppendLine($"<div class='chart-bars'><div class='chart-pass' style='width:{gPassW:F1}%'></div><div class='chart-fail' style='width:{gFailW:F1}%'></div></div>");
                sb.AppendLine($"<div class='chart-count'>{g.Pass}P / {g.Fail}F{(g.Ignore>0?$"/{g.Ignore}I":"")}</div>");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>"); // end chart-wrap
            sb.AppendLine("</div>"); // end top

            // Table columns match the page's result grid exactly: No. / Commands / Type /
            // Check List and Paths / Result. There is no separate Note column, because the page
            // does not have one either - it folds the note into the detail cell (see RenderResults),
            // so the reports now do the same and no information is lost.
            sb.AppendLine("<table><thead><tr><th>No.</th><th>Commands</th><th>Type</th><th>Check List and Paths</th><th>Result</th></tr></thead><tbody>");
            foreach (var r in _results)
            {
                var es     = EffectiveStatus(r);
                string cls   = es == QAStatus.Pass ? "p" : es == QAStatus.Fail ? "f"
                             : es == QAStatus.Ignore ? "i" : "s";
                string lbl   = es == QAStatus.Pass ? "Pass" : es == QAStatus.Fail ? "Fail"
                             : es == QAStatus.Ignore ? "Ignore" : "Skip";
                string rowCl = es == QAStatus.Fail   ? " class='fail-row'"
                             : es == QAStatus.Ignore ? " class='ign-row'" : "";
                var (cmdPart, typePart) = SplitCheckType(r.CheckType);

                // Same shape the page uses: a Fail note is appended inline in parentheses, anything
                // else goes on its own line with the warning glyph.
                string detailCell = Esc(r.Detail).Replace("\n", "<br/>");
                if (!string.IsNullOrEmpty(r.Note))
                    detailCell += r.Status == QAStatus.Fail
                        ? $" <span class='note'>({Esc(r.Note)})</span>"
                        : $"<br/><span class='note'>⚠ {Esc(r.Note)}</span>";

                sb.AppendLine($"<tr{rowCl}><td>{r.No}</td><td>{Esc(cmdPart)}</td><td>{Esc(typePart)}</td><td>{detailCell}</td><td><span class='{cls}'>{lbl}</span></td></tr>");
            }
            sb.AppendLine("</tbody></table></div></body></html>");
            return sb.ToString();
        }

        private string BuildTextReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("SwiftPack QA Report");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine($"Package : {_packageFolder}");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Pass: {_results.Count(r => r.Status == QAStatus.Pass)}  " +
                          $"Fail: {_results.Count(r => r.Status == QAStatus.Fail)}  " +
                          $"Skip: {_results.Count(r => r.Status == QAStatus.Skip)}");
            sb.AppendLine(new string('-', 80));
            foreach (var r in _results)
            {
                string lbl = r.Status == QAStatus.Pass ? "[PASS]" : r.Status == QAStatus.Fail ? "[FAIL]" : "[SKIP]";
                sb.AppendLine($"{r.No,3}. {lbl,-8} {r.CheckType,-30} {r.Detail}");
                if (r.Note != null) sb.AppendLine($"          ⚠ {r.Note}");
            }
            return sb.ToString();
        }

        private static string Esc(string s) => s
            .Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace("\"","&quot;");

        // ══════════════════════════════════════════════════════════════════
        //  HELPERS - SCRIPT PARSING
        // ══════════════════════════════════════════════════════════════════
        private void SetVerifyingState(bool verifying)
        {
            // During verify: disable all except Cancel; after: re-enable all, disable Cancel
            BtnRunQA.IsEnabled        = !verifying;
            BtnExportReport.IsEnabled = !verifying;
            BtnCopyRow.IsEnabled      = !verifying;
            BtnClearQA.IsEnabled      = !verifying;
            SetQAControlsEnabled(!verifying);
            PrgVerifyProgress.Visibility = verifying ? Visibility.Visible : Visibility.Collapsed;
        }

        // Fetches the SVR path (QAsnapPath) from ValidatePackage.json and populates the
        // SVR Path textbox when present. Used by the New button to pre-fill the path on reset.
        // ── Settings: edit ValidatePackage.json ────────────────────────────
        // Modal editor over ValidatePackage.json: SVR/export paths, Yes/No + ARP flags, and the
        // editable MSI Properties and Shortcut Keywords lists (add / delete / modify rows).
        // Writes changes back via a JsonNode round-trip. Mirrors the Auto Package / Create
        // Package Settings dialogs.
        private void BtnVpSettings_Click(object sender, RoutedEventArgs e) => OpenValidatePackageSettings();

        private void OpenValidatePackageSettings()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidatePackage.json",
                IOPath.Combine(exeDir, "Templates", "ValidatePackage.json"),
                IOPath.Combine(exeDir, "ValidatePackage.json"),
            };
            string cfgPath = candidates.FirstOrDefault(File.Exists) ?? candidates[0];

            System.Text.Json.Nodes.JsonObject root;
            try
            {
                var docOpts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
                root = (File.Exists(cfgPath)
                    ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath), null, docOpts)?.AsObject()
                    : null) ?? new System.Text.Json.Nodes.JsonObject();
            }
            catch
            {
                VpStyledMessage(null, "Validate Package - Settings",
                    "ValidatePackage.json is not valid JSON. Fix the file, then try again.\n\n" + cfgPath, "⚠");
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
                Width = 600, SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 48),
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
            var tt = new TextBlock { Text = "Validate Package - Settings", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button { Content = "✕", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
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
            TextBox MkTx(string t) => new TextBox { Text = t, FontSize = 11, MinHeight = 26, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = valFg, VerticalContentAlignment = VerticalAlignment.Center };
            Border MkBox(TextBox tx) => new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 6, 2), Margin = new Thickness(0, 0, 6, 0), Child = tx };
            TextBlock SectionHdr(string t) => new TextBlock { Text = t, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = valFg, Margin = new Thickness(0, form.Children.Count == 0 ? 0 : 12, 0, 6) };
            Grid PairHdr(string a, string b)
            {
                var h = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                h.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                h.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                h.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                var ha = new TextBlock { Text = a, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = lblFg, Margin = new Thickness(2, 0, 0, 0) };
                var hb = new TextBlock { Text = b, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = lblFg, Margin = new Thickness(2, 0, 0, 0) };
                Grid.SetColumn(ha, 0); Grid.SetColumn(hb, 1);
                h.Children.Add(ha); h.Children.Add(hb);
                return h;
            }
            void AddPairRow(StackPanel panel, System.Collections.Generic.List<(TextBox a, TextBox b)> rows, string aVal, string bVal)
            {
                var rg = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var aTx = MkTx(aVal); var bTx = MkTx(bVal);
                var del = new Button { Content = "✕", FontSize = 11, Width = 26, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = lblFg, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Remove this row" };
                var entry = (aTx, bTx);
                del.Click += (_, _) => { panel.Children.Remove(rg); rows.Remove(entry); };
                var aWrap = MkBox(aTx); var bWrap = MkBox(bTx);
                Grid.SetColumn(aWrap, 0); Grid.SetColumn(bWrap, 1); Grid.SetColumn(del, 2);
                rg.Children.Add(aWrap); rg.Children.Add(bWrap); rg.Children.Add(del);
                panel.Children.Add(rg);
                rows.Add(entry);
            }

            // ── Typed flat fields ──  kind: p = folder path (browse), preq = required folder path (*), yn = Yes/No, yn10 = Yes/No mapped to 1/0
            var fields = new (string section, string key, string legacyKey, string label, string kind)[]
            {
                ("Paths",     "SVRPath",    "SVRPath",    "SVR Path",    "preq"),
                ("Paths",     "ExportPath", "ExportPath", "Export Path", "preq"),
                ("Options",   "CheckDesktopShortcutsRemoved", "DesktopShortcuts", "Check Desktop Shortcuts Removed", "yn"),
                ("Options",   "CopyXlsxToDocuments",    "CopyCSVToDocuments",  "Copy XLSX To Documents",   "yn"),
                ("Options",   "CopyHtmlToDocuments",    "CopyHtmlToDocuments", "Copy HTML To Documents",   "yn"),
                ("ARP Flags", "CheckNoModify", "NoModify", "Check No Modify", "tri10"),
                ("ARP Flags", "CheckNoRepair", "NoRepair", "Check No Repair", "tri10"),
                ("ARP Flags", "CheckNoRemove", "NoRemove", "Check No Remove", "tri10"),
            };
            var editors = new System.Collections.Generic.Dictionary<string, Control>();
            var triEditors = new System.Collections.Generic.Dictionary<string, RadioButton[]>();
            string curSection = "";
            foreach (var f in fields)
            {
                if (f.section != curSection) { curSection = f.section; form.Children.Add(SectionHdr(curSection)); }
                string cur = ReadStr(root[f.key] ?? root[f.legacyKey]);
                var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 6), MinHeight = 30 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
                lbl.Inlines.Add(new Run(f.label));
                if (f.kind == "preq")
                    lbl.Inlines.Add(new Run(" *") { Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), FontWeight = FontWeights.Bold });
                Grid.SetColumn(lbl, 0); rowGrid.Children.Add(lbl);

                if (f.kind == "p" || f.kind == "preq")
                {
                    var txv = MkTx(cur);
                    var browse = new Button { Content = "📁", FontSize = 14, Foreground = valFg, Width = 28, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Browse for a folder" };
                    browse.Click += (_, _) =>
                    {
                        try
                        {
                            var fbd = new System.Windows.Forms.FolderBrowserDialog { Description = "Select a folder", UseDescriptionForTitle = true };
                            try { if (!string.IsNullOrWhiteSpace(txv.Text)) fbd.SelectedPath = txv.Text; } catch { }
                            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK) txv.Text = fbd.SelectedPath;
                        }
                        catch { }
                    };
                    var ig = new Grid();
                    ig.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    ig.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(txv, 0); Grid.SetColumn(browse, 1);
                    ig.Children.Add(txv); ig.Children.Add(browse);
                    var wrap = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 6, 2), Child = ig };
                    Grid.SetColumn(wrap, 1); rowGrid.Children.Add(wrap);
                    editors[f.key] = txv;
                }
                else if (f.kind == "tri10")
                {
                    string triVal = cur.Trim();
                    var rb1 = new RadioButton { Content = "1",       FontSize = 11, IsChecked = triVal == "1", Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Cursor = System.Windows.Input.Cursors.Hand };
                    var rb0 = new RadioButton { Content = "0",       FontSize = 11, IsChecked = triVal == "0", Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Cursor = System.Windows.Input.Cursors.Hand };
                    // Label only. The value persisted to QA.json stays "Default", so existing configs
                    // keep working and nothing else that reads QA.json has to change.
                    var rbD = new RadioButton { Content = "Do not check", FontSize = 11, IsChecked = triVal != "1" && triVal != "0", Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Do not check this flag" };
                    var triPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    triPanel.Children.Add(rb1); triPanel.Children.Add(rb0); triPanel.Children.Add(rbD);
                    Grid.SetColumn(triPanel, 1); rowGrid.Children.Add(triPanel);
                    triEditors[f.key] = new[] { rb1, rb0, rbD };
                }
                else
                {
                    bool isYes = f.kind == "yn10"
                        ? cur.Trim() == "1"
                        : string.Equals(cur.Trim(), "Yes", StringComparison.OrdinalIgnoreCase) || string.Equals(cur.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    var rbY = new RadioButton { Content = "Yes", FontSize = 11, IsChecked = isYes,  Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Cursor = System.Windows.Input.Cursors.Hand };
                    var rbN = new RadioButton { Content = "No",  FontSize = 11, IsChecked = !isYes, Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
                    var rbPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    rbPanel.Children.Add(rbY); rbPanel.Children.Add(rbN);   // grouped by their parent panel
                    Grid.SetColumn(rbPanel, 1); rowGrid.Children.Add(rbPanel);
                    editors[f.key] = rbY;
                }
                form.Children.Add(rowGrid);
            }

            // ── MSI Properties (editable list) ──
            form.Children.Add(SectionHdr("Check MSI Properties added"));
            form.Children.Add(PairHdr("Property", "Value"));
            var msiPanel = new StackPanel(); form.Children.Add(msiPanel);
            var msiRows = new System.Collections.Generic.List<(TextBox a, TextBox b)>();
            if ((root["CheckMSIPropertiesAdded"] ?? root["MSIProperties"]) is System.Text.Json.Nodes.JsonArray marr)
                foreach (var el in marr)
                    if (el is System.Text.Json.Nodes.JsonObject mo)
                        AddPairRow(msiPanel, msiRows, ReadStr(mo["Property"]), ReadStr(mo["Value"]));
            var addMsi = new Button { Content = "＋  Add Property", Style = sec, Height = 26, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
            addMsi.Click += (_, _) => AddPairRow(msiPanel, msiRows, "", "");
            form.Children.Add(addMsi);

            // ── Shortcut Keywords (editable list; each keyword flagged Yes/No) ──
            form.Children.Add(SectionHdr("Check Shortcut Keywords to remove"));
            form.Children.Add(PairHdr("Keyword", "Flag"));
            var kwPanel = new StackPanel(); form.Children.Add(kwPanel);
            var kwRows = new System.Collections.Generic.List<(TextBox key, RadioButton yes)>();
            void AddKeywordRow(string keyword, bool isYes)
            {
                var rg = new Grid { Margin = new Thickness(0, 0, 0, 4), MinHeight = 30 };
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var kTx = MkTx(keyword);
                var kWrap = MkBox(kTx);
                var rbY = new RadioButton { Content = "Yes", FontSize = 11, IsChecked = isYes,  Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Cursor = System.Windows.Input.Cursors.Hand };
                var rbN = new RadioButton { Content = "No",  FontSize = 11, IsChecked = !isYes, Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
                var rbPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rbPanel.Children.Add(rbY); rbPanel.Children.Add(rbN);
                var del = new Button { Content = "✕", FontSize = 11, Width = 26, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = lblFg, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Remove this keyword" };
                var entry = (kTx, rbY);
                del.Click += (_, _) => { kwPanel.Children.Remove(rg); kwRows.Remove(entry); };
                Grid.SetColumn(kWrap, 0); Grid.SetColumn(rbPanel, 1); Grid.SetColumn(del, 2);
                rg.Children.Add(kWrap); rg.Children.Add(rbPanel); rg.Children.Add(del);
                kwPanel.Children.Add(rg);
                kwRows.Add(entry);
            }
            if ((root["CheckShortcutKeywordsToRemove"] ?? root["ShortcutKeywords"]) is System.Text.Json.Nodes.JsonObject kwo)
                foreach (var kv in kwo)
                    AddKeywordRow(kv.Key, string.Equals(ReadStr(kv.Value).Trim(), "Yes", StringComparison.OrdinalIgnoreCase));
            var addKw = new Button { Content = "＋  Add Keyword", Style = sec, Height = 26, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
            addKw.Click += (_, _) => AddKeywordRow("", false);
            form.Children.Add(addKw);

            // Button bar
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 4, 16, 14) };
            Grid.SetRow(btnBar, 2);
            var bCancel = new Button { Content = "✕  Cancel", Style = sec, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            bCancel.Click += (_, _) => cwin.Close();
            var bSave = new Button { Content = "💾  Save", Style = sec, Height = 30, Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            VpApplyPrimaryLook(bSave);
            bSave.Click += (_, _) =>
            {
                try
                {
                    // Required folder paths must be set before saving.
                    var missing = new System.Collections.Generic.List<string>();
                    foreach (var f in fields)
                        if (f.kind == "preq" && string.IsNullOrWhiteSpace((editors[f.key] as TextBox)?.Text))
                            missing.Add(f.label);
                    if (missing.Count > 0)
                    {
                        VpStyledMessage(cwin, "Validate Package - Settings",
                            "These field(s) are required:\n\n" + string.Join("\n", missing), "⚠");
                        return;
                    }

                    // Flat fields
                    foreach (var f in fields)
                    {
                        if (f.kind == "p" || f.kind == "preq")
                            root[f.key] = ((editors[f.key] as TextBox)?.Text ?? "").Trim();
                        else if (f.kind == "tri10")
                        {
                            var rbs = triEditors[f.key];
                            root[f.key] = rbs[0].IsChecked == true ? "1"
                                        : rbs[1].IsChecked == true ? "0"
                                        : "Default";
                        }
                        else
                        {
                            bool yes = (editors[f.key] as RadioButton)?.IsChecked == true;
                            root[f.key] = f.kind == "yn10" ? (yes ? "1" : "0") : (yes ? "Yes" : "No");
                        }
                        // Migrate: drop the legacy key when it was renamed.
                        if (!string.Equals(f.key, f.legacyKey, StringComparison.Ordinal))
                            root.Remove(f.legacyKey);
                    }

                    // MSI Properties -> array of { Property, Value } (skip rows with no property)
                    var marrOut = new System.Text.Json.Nodes.JsonArray();
                    foreach (var (a, b) in msiRows)
                    {
                        string p = a.Text.Trim();
                        if (p.Length == 0) continue;
                        marrOut.Add(new System.Text.Json.Nodes.JsonObject { ["Property"] = p, ["Value"] = b.Text.Trim() });
                    }
                    root["CheckMSIPropertiesAdded"] = marrOut;
                    root.Remove("MSIProperties");

                    // Shortcut Keywords -> object of keyword: Yes/No (skip rows with no keyword)
                    var kwOut = new System.Text.Json.Nodes.JsonObject();
                    foreach (var (kTx, rbY) in kwRows)
                    {
                        string k = kTx.Text.Trim();
                        if (k.Length == 0) continue;
                        kwOut[k] = rbY.IsChecked == true ? "Yes" : "No";
                    }
                    root["CheckShortcutKeywordsToRemove"] = kwOut;
                    root.Remove("ShortcutKeywords");

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

                    // Refresh the SVR path shown on the page (config is re-read on the next run).
                    LoadSvrPathFromConfig();

                    cwin.Close();
                    VpStyledMessage(null, "Validate Package - Settings",
                        "Settings written to ValidatePackage.json.", "✅");
                }
                catch (Exception ex)
                {
                    VpStyledMessage(cwin, "Validate Package - Settings",
                        "Save failed:\n" + ex.Message + "\n\n" + cfgPath, "⚠");
                }
            };
            btnBar.Children.Add(bCancel); btnBar.Children.Add(bSave);
            og.Children.Add(btnBar);

            cwin.ShowDialog();
        }

        // Small branded message dialog matching the app's window style. Single OK button.
        private void VpStyledMessage(Window? owner, string title, string message, string icon)
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
            var tcl = new Button { Content = "✕", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
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
            var dIcon = VpDecisionIcon();
            Grid.SetColumn(dIcon, 0);
            actionRow.Children.Add(dIcon);
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(btnBar, 1);
            var bOk = new Button { Content = "✔  OK", Style = sec, Height = 30, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsDefault = true };
            VpApplyPrimaryLook(bOk);
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
        private static void VpApplyPrimaryLook(Button b)
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
        private static TextBlock VpDecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };

        private void LoadSvrPathFromConfig()
        {
            try
            {
                var cfg = LoadQAConfig(_packageFolder);
                if (!string.IsNullOrEmpty(cfg?.QAsnapPath))
                {
                    string expanded = Environment.ExpandEnvironmentVariables(cfg.QAsnapPath.Trim());
                    // Keep the search folder INTERNAL only. The SVR Path field is a file field now,
                    // so it shows a .svr FILE (or stays empty) - never a folder like C:\Temp.
                    if (_qaConfig == null) _qaConfig = new QAConfig();
                    _qaConfig.QAsnapPath = expanded;
                }
                // No message box - silently do nothing if QAsnapPath not set
            }
            catch { /* ignore */ }
        }

        private void SetQAControlsEnabled(bool enabled)
        {
            // Disabled == a verify is running, which is exactly when Auto Package must wait.
            AppBusy.SetManual("Validate Package", !enabled);
            // Header buttons
            if (BtnRunQA         != null) BtnRunQA.IsEnabled         = enabled;
            if (BtnExportReport  != null) BtnExportReport.IsEnabled   = enabled;
            if (BtnCopyRow       != null) BtnCopyRow.IsEnabled        = enabled;
            if (BtnSaveReport    != null) BtnSaveReport.IsEnabled     = enabled;
            if (BtnImportReport  != null) BtnImportReport.IsEnabled   = enabled;
            if (BtnClearQA       != null) BtnClearQA.IsEnabled        = enabled;
            if (BtnClearLogPath  != null) BtnClearLogPath.IsEnabled   = enabled;
            if (BtnQASearchPrev  != null) BtnQASearchPrev.IsEnabled   = enabled;
            if (BtnQASearchNext  != null) BtnQASearchNext.IsEnabled   = enabled;
            // Log file textbox
            if (TxtLogPath       != null) TxtLogPath.IsReadOnly       = !enabled;
        }

        private static string? FindInvokeScript(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return null;
            try
            {
                // Prefer the REAL deployment script over the copy bundled inside the
                // PSAppDeployToolkit module (…\PSAppDeployToolkit\Frontend\v4\…), then the
                // shallowest path. Enumeration order is not contractual, so choose explicitly.
                return Directory.GetFiles(folder, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories)
                                .OrderBy(p => p.IndexOf(@"\PSAppDeployToolkit\", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                                .ThenBy(p => p.Count(c => c == '\\' || c == '/'))
                                .ThenBy(p => p.Length)
                                .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
            {
                return null;
            }
        }

        private static string ExtractSessionParam(string script, string param)
        {
            // Hashtable style:  AppName = 'value'
            var ht = Regex.Match(script, $@"(?m)^\s*{Regex.Escape(param)}\s*=\s*'([^']*)'");
            if (ht.Success) return ht.Groups[1].Value.Trim();
            // Switch style: -AppName 'value'
            var sw = Regex.Match(script, $@"-{Regex.Escape(param)}\s+'([^']*)'");
            if (sw.Success) return sw.Groups[1].Value.Trim();
            return "";
        }

        // ══════════════════════════════════════════════════════════════════
        //  ADT LOG PARSER - parses PSAppDeployToolkit *.log file
        // ══════════════════════════════════════════════════════════════════
        private class AdtLogInfo
        {
            public string PackageFolder   { get; set; } = "";
            public string DeploymentType  { get; set; } = "";
            // MSI runs: keyed by msi filename → (msiPath, msiVerboseLogPath, exitCode, success)
            public List<MsiRun>    MsiRuns    { get; } = new();
            // Full "Executed [...]" command lines - for extracting command-line properties
            public List<string>    ExecutedCommands { get; } = new();
            // Copy-ADTFile: source → dest, success
            public List<FileCopyEntry> FileCopies { get; } = new();
            // Set-ADTRegistryKey entries
            public List<RegKeyEntry>   RegEntries { get; } = new();

            public record MsiRun(string MsiPath, string VerboseLogPath, long ExitCode, bool Success);
            public record FileCopyEntry(string Src, string Dest, bool Success);
            public record RegKeyEntry(string KeyPath, string ValueName, string Value);
        }

        private static AdtLogInfo ParseAdtLog(string logPath)
        {
            var info = new AdtLogInfo();
            if (!File.Exists(logPath)) return info;

            string text;
            try { text = File.ReadAllText(logPath, System.Text.Encoding.UTF8); }
            catch { return info; }

            // Extract all LOG entries with their text
            var entries = Regex.Matches(text,
                @"<!\[LOG\[([\s\S]*?)\]LOG\]!><time=""[^""]*""\s+date=""[^""]*""\s+component=""([^""]*)""\s+context=""[^""]*""\s+type=""[^""]*""\s+thread=""[^""]*""\s+file=""([^""]*)""",
                RegexOptions.IgnoreCase);

            string? pendingMsiPath    = null;
            string? pendingVerboseLog = null;

            foreach (Match e in entries)
            {
                string msg  = e.Groups[1].Value.Trim();
                string comp = e.Groups[2].Value;
                string file = e.Groups[3].Value; // e.g. C:\Temp\...\PSAppDeployToolkit.psm1

                // Derive package folder from file path (PSAppDeployToolkit subfolder)
                if (string.IsNullOrEmpty(info.PackageFolder) && file.Contains("PSAppDeployToolkit"))
                {
                    string psm1Dir = IOPath.GetDirectoryName(file) ?? "";
                    info.PackageFolder = IOPath.GetDirectoryName(psm1Dir) ?? psm1Dir;
                }

                // Deployment type
                if (string.IsNullOrEmpty(info.DeploymentType))
                {
                    var dtm = Regex.Match(msg, @"Deployment type is \[(Install|Uninstall)\]", RegexOptions.IgnoreCase);
                    if (dtm.Success) info.DeploymentType = dtm.Groups[1].Value;
                }

                // MSI Executing line: grab msi path + verbose log path
                // "Executing ["C:\Windows\system32\msiexec.exe" /i <msiPath> ... /L*V <logPath>]"
                var exm = Regex.Match(msg,
                    @"Executing\s+\[""[^""]*msiexec\.exe""\s+/[iI]\s+""?([^""\s]+\.msi)""?\s+([^\]]*)/L\*V\s+""?([^""\]\s]+)""?",
                    RegexOptions.IgnoreCase);
                if (!exm.Success)
                    exm = Regex.Match(msg,
                        @"Executing\s+\[""[^""]*msiexec\.exe""\s+/[iI]\s+([^\s]+\.msi)\s+([^\]]*)/L\*V\s+([^\]\s]+)",
                        RegexOptions.IgnoreCase);
                if (exm.Success)
                {
                    pendingMsiPath    = exm.Groups[1].Value.Trim();
                    pendingVerboseLog = exm.Groups[3].Value.Trim().TrimEnd(']', '"');
                }

                // "Executed [...]" line - capture full command for property extraction
                var execDoneLine = Regex.Match(msg,
                    @"Executed\s+\[(.+)\],\s+awaiting completion", RegexOptions.IgnoreCase);
                if (execDoneLine.Success)
                    info.ExecutedCommands.Add(execDoneLine.Groups[1].Value.Trim());

                // MSI Executed (awaiting) or exit code
                var ecm = Regex.Match(msg, @"Execution completed successfully with exit code \[(\d+)\]", RegexOptions.IgnoreCase);
                var failm = Regex.Match(msg, @"execution.*?exit code \[(\d+)\]", RegexOptions.IgnoreCase);
                if ((ecm.Success || failm.Success) && pendingMsiPath != null)
                {
                    string codeText = ecm.Success ? ecm.Groups[1].Value : failm.Groups[1].Value;
                    long code = 0;
                    long.TryParse(codeText, out code);   // large codes (e.g. 3221225477) overflow int.Parse
                    bool ok  = ecm.Success || code == 0 || code == 3010;
                    info.MsiRuns.Add(new(pendingMsiPath, pendingVerboseLog ?? "", code, ok));
                    pendingMsiPath = pendingVerboseLog = null;
                }

                // Copy-ADTFile
                // "Copying file in path [src] to destination [dest]."
                var cpm = Regex.Match(msg, @"Copying file in path \[([^\]]+)\] to destination \[([^\]]+)\]", RegexOptions.IgnoreCase);
                if (cpm.Success && comp.IndexOf("Copy-ADTFile", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string src  = cpm.Groups[1].Value.Trim();
                    string dest = cpm.Groups[2].Value.Trim();
                    // Check if the next entry says "File copy completed successfully"
                    // We'll resolve this after collecting all entries - use a placeholder
                    info.FileCopies.Add(new(src, dest, false)); // success resolved below
                }
                // "File copy completed successfully" - marks last copy as successful
                if (msg.Contains("File copy completed successfully", StringComparison.OrdinalIgnoreCase)
                    && info.FileCopies.Count > 0)
                {
                    var last = info.FileCopies[info.FileCopies.Count - 1];
                    info.FileCopies[info.FileCopies.Count - 1] = new(last.Src, last.Dest, true);
                }

                // Set-ADTRegistryKey
                // "Setting registry key value: [Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SOFTWARE\test] [abc = 1]."
                var rkm = Regex.Match(msg,
                    @"Setting registry key value:\s+\[(?:Microsoft\.PowerShell\.Core\\Registry::)?([^\]]+)\]\s+\[([^\]=]+)\s*=\s*([^\]]*)\]",
                    RegexOptions.IgnoreCase);
                if (rkm.Success)
                {
                    info.RegEntries.Add(new(
                        rkm.Groups[1].Value.Trim(),
                        rkm.Groups[2].Value.Trim(),
                        rkm.Groups[3].Value.Trim()));
                }
            }
            return info;
        }

        // ══════════════════════════════════════════════════════════════════
        //  MSI VERBOSE LOG PARSER - parses msiexec /L*V log (UTF-16 LE)
        // ══════════════════════════════════════════════════════════════════
        private class MsiVerboseLogInfo
        {
            public string InstallLocation { get; set; } = "";
            public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(string Name, string Value, string Delimiter)> EnvVars { get; } = new();
            public List<string> Shortcuts { get; } = new();
            // Additional install folders from SetTargetFolder ops
            public List<string> InstallFolders { get; } = new();
            // Registry key writes: (hive\key, valueName, value)
            public List<(string Key, string Name, string Value)> RegWrites { get; } = new();
        }

        private static MsiVerboseLogInfo ParseMsiVerboseLog(string logPath)
        {
            var info = new MsiVerboseLogInfo();
            if (!File.Exists(logPath)) return info;

            string text;
            try { text = File.ReadAllText(logPath, System.Text.Encoding.Unicode); }
            catch {
                try { text = File.ReadAllText(logPath, System.Text.Encoding.UTF8); }
                catch { return info; }
            }

            // Property(S): NAME = VALUE  or  PROPERTY CHANGE: Adding NAME property. Its value is 'VALUE'.
            foreach (Match m in Regex.Matches(text, @"Property\([SC]\):\s+(\w+)\s*=\s*(.+)", RegexOptions.Multiline))
                info.Properties[m.Groups[1].Value] = m.Groups[2].Value.Trim();

            // SetTargetFolder ops - find common ancestor paths across all install dirs
            var allTargetFolders = new List<string>();
            string windowsDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
            foreach (Match m in Regex.Matches(text,
                @"SetTargetFolder\(Folder=(.+)\)", RegexOptions.IgnoreCase))
            {
                string folder = m.Groups[1].Value.Trim().TrimEnd('\\');
                if (Regex.IsMatch(folder, @"^\d+$")) continue;       // numeric only e.g. "16", "23"
                if (folder.Length <= 5) continue;
                if (!System.IO.Path.IsPathRooted(folder)) continue;  // skip relative paths e.g. "2\Self-Extractor..."
                if (folder.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (!allTargetFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
                    allTargetFolders.Add(folder);
            }

            // Group by first 4 path segments, then find the common ancestor within each group
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in allTargetFolders)
            {
                var parts = folder.Split('\\');
                string key = string.Join("\\", parts.Take(Math.Min(4, parts.Length)));
                if (!groups.ContainsKey(key)) groups[key] = new List<string>();
                groups[key].Add(folder);
            }
            foreach (var grp in groups.Values)
            {
                // Find longest common path prefix of all paths in this group
                var aParts = grp[0].Split('\\');
                int common = aParts.Length;
                foreach (string path in grp.Skip(1))
                {
                    var bParts = path.Split('\\');
                    int match = 0;
                    while (match < common && match < bParts.Length &&
                           string.Equals(aParts[match], bParts[match], StringComparison.OrdinalIgnoreCase))
                        match++;
                    common = match;
                }
                string ancestor = string.Join("\\", aParts.Take(common));
                if (!string.IsNullOrEmpty(ancestor) && ancestor.Length > 5 &&
                    !info.InstallFolders.Any(f => string.Equals(f, ancestor, StringComparison.OrdinalIgnoreCase)))
                    info.InstallFolders.Add(ancestor);
            }

            foreach (Match m in Regex.Matches(text,
                @"PROPERTY CHANGE:\s+(?:Adding|Modifying)\s+(\w+)\s+property\.\s+Its value is '([^']*)'", RegexOptions.IgnoreCase))
                info.Properties[m.Groups[1].Value] = m.Groups[2].Value.Trim();

            // ARPINSTALLLOCATION
            if (info.Properties.TryGetValue("ARPINSTALLLOCATION", out string? ail))
                info.InstallLocation = ail;

            // Environment variables: UpdateEnvironmentStrings(Name=X,Value=Y,Delimiter=Z,Action=N,)
            foreach (Match m in Regex.Matches(text,
                @"UpdateEnvironmentStrings\(Name=([^,]+),Value=([^,]+),Delimiter=([^,]+),Action=\d+,?\)", RegexOptions.IgnoreCase))
                info.EnvVars.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim()));

            // Shortcuts - use "Note: 1: 2318 2: <path>" entries which give the exact
            // real .lnk paths created during install (avoids 8.3 short-name resolution errors)
            foreach (Match m in Regex.Matches(text,
                @"Note: 1: 2318 2: (.+\.lnk)", RegexOptions.IgnoreCase))
            {
                string lnk = m.Groups[1].Value.Trim();
                if (!info.Shortcuts.Contains(lnk, StringComparer.OrdinalIgnoreCase))
                    info.Shortcuts.Add(lnk);
            }

            return info;
        }

        private static List<(string Cmd, int LineNo)> ExtractInstallCommands(string script)
        {
            // Extract body of function Install-ADTDeployment { ... }
            var fnMatch = Regex.Match(script,
                @"function\s+Install-ADTDeployment\s*[\r\n]*\{([\s\S]*?)\n\}",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            string body = fnMatch.Success ? fnMatch.Groups[1].Value : script;

            // Calculate line offset of the function body in the full script
            int bodyStartLine = 1;
            if (fnMatch.Success)
            {
                string before = script.Substring(0, fnMatch.Groups[1].Index);
                bodyStartLine = before.Count(c => c == '\n') + 1;
            }

            var commands = new List<(string, int)>();
            var lines    = body.Split('\n');
            var current  = new StringBuilder();
            int currentLineNo = 0;

            // Collect ALL ADT commands from within Install-ADTDeployment function body.
            // No marker required - users may not include the standard section comments.
            // Only skip: boilerplate UseDefaultMsi/UseDefaultMsp blocks, param block,
            // CmdletBinding, and $adtSession.InstallPhase assignments.
            int  skipBraceDepth = 0;
            bool insideSkipBlock = false;
            bool pastParamBlock  = false;

            foreach (var rawLine in lines)
            {
                currentLineNo++;
                string line    = rawLine.TrimEnd();
                string trimmed = line.TrimStart();

                // Skip param() / [CmdletBinding()] block at top of function
                if (!pastParamBlock)
                {
                    if (trimmed.StartsWith("[CmdletBinding") ||
                        trimmed.StartsWith("param") ||
                        trimmed == "(" || trimmed == ")")
                        continue;
                    pastParamBlock = true;
                }

                // Skip entire UseDefaultMsi / UseDefaultMsp boilerplate block
                // The { is on the line AFTER the if(...), so we enter skip mode on the
                // if-line itself and count braces from that point forward.
                if (!insideSkipBlock &&
                    Regex.IsMatch(trimmed, @"if\s*\(\s*\$adtSession\.(UseDefaultMsi|DefaultMspFiles|DefaultMstFile)",
                        RegexOptions.IgnoreCase))
                {
                    insideSkipBlock = true;
                    skipBraceDepth  = 0;
                }
                if (insideSkipBlock)
                {
                    skipBraceDepth += trimmed.Count(c => c == '{');
                    skipBraceDepth -= trimmed.Count(c => c == '}');
                    // Exit skip block only after we've seen at least one { and returned to 0
                    if (skipBraceDepth <= 0 && trimmed.Contains('}')) insideSkipBlock = false;
                    continue;
                }
                // Also skip standalone boilerplate lines outside the if-block
                if (Regex.IsMatch(trimmed, @"^\$ExecuteDefaultMSISplat", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^Start-ADTMsiProcess\s+@ExecuteDefaultMSISplat", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"\$adtSession\.DefaultMspFiles\s*\|\s*Start-ADTMsiProcess", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^\$adtSession\.InstallPhase\s*=", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^Show-ADTInstallation", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^\$saiwParams", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(trimmed, @"^\$adtSession\.AppProcessesToClose", RegexOptions.IgnoreCase))
                    continue;

                // Skip comments and blank lines
                if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed)) continue;

                // Handle PowerShell line continuations (backtick)
                if (line.EndsWith("`"))
                {
                    current.Append(line.TrimEnd('`').TrimEnd() + " ");
                    continue;
                }
                int cmdLineNo = bodyStartLine + currentLineNo - 1;
                current.Append(line);
                string full = current.ToString().Trim();
                current.Clear();

                if (Regex.IsMatch(full, @"(Copy-ADTFile|Start-ADTProcess\b|Start-ADTMsiProcess\b|Set-ADTRegistryKey\b)",
                    RegexOptions.IgnoreCase))
                    commands.Add((full, cmdLineNo));
            }
            // Flush any remaining continuation
            if (current.Length > 0)
            {
                string full = current.ToString().Trim();
                if (Regex.IsMatch(full, @"(Copy-ADTFile|Start-ADTProcess\b|Start-ADTMsiProcess\b|Set-ADTRegistryKey\b)",
                    RegexOptions.IgnoreCase))
                    commands.Add((full, bodyStartLine + currentLineNo - 1));
            }
            return commands;
        }

        // ── Resolve $env:SystemDrive, $($adtSession.DirFiles) etc. ───────
        private static string ResolveCommand(string cmd, string filesDir)
        {
            // Ensure filesDir has no trailing backslash so path joins are clean
            string fd    = filesDir.TrimEnd('\\', '/');
            string drive = IOPath.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!.TrimEnd('\\');
            return cmd
                .Replace("$env:SystemDrive",          drive,         StringComparison.OrdinalIgnoreCase)
                .Replace("$env:SystemRoot",            Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:ProgramFiles(x86)",     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:ProgramFiles",          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:CommonProgramFiles",    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:APPDATA",               Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:LOCALAPPDATA",          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:PUBLIC",                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), StringComparison.OrdinalIgnoreCase)
                .Replace("$($adtSession.DirFiles)",    fd,            StringComparison.OrdinalIgnoreCase)
                .Replace("$adtSession.DirFiles",       fd,            StringComparison.OrdinalIgnoreCase);
        }

        private static string ExpandEnv(string s)
        {
            string drive = IOPath.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!.TrimEnd('\\');
            return s
                .Replace("$env:SystemDrive",  drive,   StringComparison.OrdinalIgnoreCase)
                .Replace("$env:SystemRoot",   Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:TEMP",         IOPath.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                .Replace("$env:WINDIR",       Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                .Replace("%SystemDrive%",     drive,   StringComparison.OrdinalIgnoreCase)
                .Replace("%SystemRoot%",      Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                .Replace("%ProgramFiles%",    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                .Trim('"').Trim('\'');
        }

        // ── Parse a named parameter - handles quoted paths with spaces ────
        private static string ExtractParam(string cmd, string paramName)
        {
            // Try double-quoted first (handles spaces in paths)
            var mDq = Regex.Match(cmd,
                $@"{Regex.Escape(paramName)}\s+""([^""]+)""",
                RegexOptions.IgnoreCase);
            if (mDq.Success) return mDq.Groups[1].Value;

            // Try single-quoted
            var mSq = Regex.Match(cmd,
                $@"{Regex.Escape(paramName)}\s+'([^']+)'",
                RegexOptions.IgnoreCase);
            if (mSq.Success) return mSq.Groups[1].Value;

            // Try parenthesised quoted: ("value") or ('value')
            var mPq = Regex.Match(cmd,
                $@"{Regex.Escape(paramName)}\s+\([""']([^""']+)[""']\)",
                RegexOptions.IgnoreCase);
            if (mPq.Success) return mPq.Groups[1].Value;

            // Unquoted token (no spaces)
            var mUq = Regex.Match(cmd,
                $@"{Regex.Escape(paramName)}\s+(\S+)",
                RegexOptions.IgnoreCase);
            return mUq.Success ? mUq.Groups[1].Value : "";
        }

        private static string GetCommandLabel(string cmd, int lineNo)
        {
            string trimmed = cmd.TrimStart();
            string name =
                trimmed.StartsWith("Start-ADTMsiProcess", StringComparison.OrdinalIgnoreCase) ? "Start-ADTMsiProcess" :
                trimmed.StartsWith("Start-ADTProcess",    StringComparison.OrdinalIgnoreCase) ? "Start-ADTProcess" :
                trimmed.StartsWith("Copy-ADTFile",        StringComparison.OrdinalIgnoreCase) ? "Copy-ADTFile" :
                trimmed.StartsWith("Set-ADTRegistryKey",  StringComparison.OrdinalIgnoreCase) ? "Set-ADTRegistryKey" :
                Regex.Match(trimmed, @"^[\w\-]+").Value;
            return lineNo > 0 ? $"{name} ({lineNo})" : name;
        }

        // Returns true for Start-ADTMsiProcess rows (the blue header rows)
        private static bool IsMsiFileRow(string checkType) =>
            // Only the header row (no · suffix) gets the blue section background.
            // Sub-rows like "Start-ADTMsiProcess (167) · File" are excluded so Fail rows get red.
            (Regex.IsMatch(checkType, @"^Start-ADTMsiProcess\s*\(\d+\)$", RegexOptions.IgnoreCase) ||
             checkType.Equals("Start-ADTMsiProcess", StringComparison.OrdinalIgnoreCase) ||
             checkType == "MSI · File");

        private static bool IsCopyFile(string cmd)      => cmd.TrimStart().StartsWith("Copy-ADTFile",   StringComparison.OrdinalIgnoreCase);
        private static bool IsRemoveFile(string cmd)    => cmd.TrimStart().StartsWith("Remove-ADTFile", StringComparison.OrdinalIgnoreCase);
        private static bool IsStartProcess(string cmd)  => Regex.IsMatch(cmd, @"^\s*Start-ADTProcess\b", RegexOptions.IgnoreCase);
        private static bool IsSetRegistry(string cmd)   => cmd.TrimStart().StartsWith("Set-ADTRegistryKey", StringComparison.OrdinalIgnoreCase);
        private static bool IsRemoveRegistry(string cmd)=> cmd.TrimStart().StartsWith("Remove-ADTRegistryKey", StringComparison.OrdinalIgnoreCase);
        // Only match direct Start-ADTMsiProcess calls, not pipeline commands like "$x | Start-ADTMsiProcess"
        private static bool IsStartMsi(string cmd)      => Regex.IsMatch(cmd, @"^\s*Start-ADTMsiProcess\b", RegexOptions.IgnoreCase);
        // New command detectors
        private static bool IsNewFolder(string cmd)              => cmd.TrimStart().StartsWith("New-ADTFolder",                    StringComparison.OrdinalIgnoreCase);
        private static bool IsRemoveFolder(string cmd)           => cmd.TrimStart().StartsWith("Remove-ADTFolder",                 StringComparison.OrdinalIgnoreCase);
        private static bool IsNewShortcut(string cmd)            => cmd.TrimStart().StartsWith("New-ADTShortcut",                  StringComparison.OrdinalIgnoreCase);
        private static bool IsSetShortcut(string cmd)            => cmd.TrimStart().StartsWith("Set-ADTShortcut",                  StringComparison.OrdinalIgnoreCase);
        private static bool IsSetEnvVar(string cmd)              => cmd.TrimStart().StartsWith("Set-ADTEnvironmentVariable",       StringComparison.OrdinalIgnoreCase);
        private static bool IsRemoveEnvVar(string cmd)           => cmd.TrimStart().StartsWith("Remove-ADTEnvironmentVariable",    StringComparison.OrdinalIgnoreCase);
        private static bool IsStartService(string cmd)           => cmd.TrimStart().StartsWith("Start-ADTServiceAndDependencies",  StringComparison.OrdinalIgnoreCase);
        private static bool IsStopService(string cmd)            => cmd.TrimStart().StartsWith("Stop-ADTServiceAndDependencies",   StringComparison.OrdinalIgnoreCase);
        private static bool IsRegisterDll(string cmd)            => cmd.TrimStart().StartsWith("Register-ADTDll",                  StringComparison.OrdinalIgnoreCase);
        private static bool IsUnregisterDll(string cmd)          => cmd.TrimStart().StartsWith("Unregister-ADTDll",                StringComparison.OrdinalIgnoreCase);
        private static bool IsInvokeRegSvr32(string cmd)         => cmd.TrimStart().StartsWith("Invoke-ADTRegSvr32",               StringComparison.OrdinalIgnoreCase);
        private static bool IsSetIniValue(string cmd)            => cmd.TrimStart().StartsWith("Set-ADTIniValue",                  StringComparison.OrdinalIgnoreCase);
        private static bool IsRemoveIniValue(string cmd)         => cmd.TrimStart().StartsWith("Remove-ADTIniValue",               StringComparison.OrdinalIgnoreCase);
        private static bool IsCopyFileToUserProfiles(string cmd) => cmd.TrimStart().StartsWith("Copy-ADTFileToUserProfiles",       StringComparison.OrdinalIgnoreCase);
        private static bool IsRemoveFileFromUserProfiles(string cmd) => cmd.TrimStart().StartsWith("Remove-ADTFileFromUserProfiles", StringComparison.OrdinalIgnoreCase);
        private static bool IsSetActiveSetup(string cmd)         => cmd.TrimStart().StartsWith("Set-ADTActiveSetup",               StringComparison.OrdinalIgnoreCase);
        private static bool IsUninstallApp(string cmd)           => cmd.TrimStart().StartsWith("Uninstall-ADTApplication",         StringComparison.OrdinalIgnoreCase);
        private static bool IsStartMsp(string cmd)               => Regex.IsMatch(cmd, @"^\s*Start-ADTMspProcess\b",              RegexOptions.IgnoreCase);

        // ── Find log path from config.psd1 ────────────────────────────────
        private static string FindLogPath(string packageFolder, string appName, string vendor, string version)
        {
            string configPath = "";
            if (!string.IsNullOrEmpty(packageFolder) && Directory.Exists(packageFolder))
            {
                try
                {
                    configPath = Directory.GetFiles(packageFolder, "config.psd1", SearchOption.AllDirectories)
                                         .FirstOrDefault() ?? "";
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
                {
                    configPath = "";
                }
            }
            if (!string.IsNullOrEmpty(configPath))
            {
                string cfg = File.ReadAllText(configPath);
                var m = Regex.Match(cfg, @"LogPath\s*=\s*'([^']*)'", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
            }
            // Fallback: %TEMP%  - ValidatePackage_<AppName>_<AppVersion>_<AppVendor>.log
            // Skip blank segments so no double/trailing underscores when fields are empty
            string safeName3    = string.IsNullOrWhiteSpace(appName)  ? "" : Regex.Replace(appName,  @"[^\w\-.]", "");
            string safeVendor3  = string.IsNullOrWhiteSpace(vendor)   ? "" : Regex.Replace(vendor,   @"[^\w\-.]", "");
            string safeVersion3 = string.IsNullOrWhiteSpace(version)  ? "" : Regex.Replace(version,  @"[^\w\-.]", "");
            var logParts = new[] { "ValidatePackage", safeVendor3, safeName3, safeVersion3 }
                .Where(p => !string.IsNullOrEmpty(p));
            string logBase = string.Join("_", logParts);
            return IOPath.Combine(IOPath.GetTempPath(), $"{logBase}.log");
        }

        // ══════════════════════════════════════════════════════════════════
        //  REGISTRY HELPERS
        // ══════════════════════════════════════════════════════════════════
        private record RegistryEntry(
            string DisplayName, string DisplayVersion, string Publisher,
            string InstallLocation, string? NoModify, string? NoRepair, string? NoRemove,
            string RegistryKeyPath = "");

        private static RegistryEntry? FindRegistryEntry(string? productName, string? productCode)
        {
            var hives = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (var hive in hives)
            {
                foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    using var key = root.OpenSubKey(hive);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var entry = key.OpenSubKey(sub);
                        if (entry == null) continue;
                        string dn = entry.GetValue("DisplayName") as string ?? "";
                        bool codeMatch = !string.IsNullOrEmpty(productCode) &&
                            sub.Equals(productCode, StringComparison.OrdinalIgnoreCase);
                        bool nameMatch = !string.IsNullOrEmpty(productName) &&
                            dn.Contains(productName, StringComparison.OrdinalIgnoreCase);
                        if (!codeMatch && !nameMatch) continue;

                        // Build full registry path for regedit navigation
                        string rootName = root == Registry.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
                        string regKeyPath = $"{rootName}\\{hive}\\{sub}";

                        return new RegistryEntry(
                            DisplayName:     dn,
                            DisplayVersion:  entry.GetValue("DisplayVersion") as string ?? "",
                            Publisher:       entry.GetValue("Publisher") as string ?? "",
                            InstallLocation: entry.GetValue("InstallLocation") as string ?? "",
                            NoModify:        entry.GetValue("NoModify")?.ToString(),
                            NoRepair:        entry.GetValue("NoRepair")?.ToString(),
                            NoRemove:        entry.GetValue("NoRemove")?.ToString(),
                            RegistryKeyPath: regKeyPath
                        );
                    }
                }
            }
            return null;
        }

        private static bool LooseMatch(string a, string b) =>
            a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
            b.Contains(a, StringComparison.OrdinalIgnoreCase);

        // ══════════════════════════════════════════════════════════════════
        //  SMART REGISTRY LOOKUP FOR EXE INSTALLS
        //  Handles cases like "FileCompare Setup 1.0.exe" → "FileCompare"
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Derives a short keyword from the EXE filename for registry lookup.
        /// e.g. "FileCompare Setup 1.0.exe" -> "filecompare"
        ///      "GoogleChromeSetup.exe"      -> "googlechromesetup"
        /// </summary>
        private static string GetExeKeyword(string exeFileName)
        {
            string baseName = IOPath.GetFileNameWithoutExtension(exeFileName);

            // Step 1: Strip arch/platform tokens that may be joined with dashes
            // e.g. "filecompare-1.0-x64" → "filecompare-1.0-x", then "filecompare-1.0"
            // Handles: -win64, -win32, -x64, -x86, _x64, _x86, (space)x64 etc.
            string keyword = Regex.Replace(baseName,
                @"[-_\s]*(win32|win64|x64|x86|setup|install|installer|update)(?=[-_\s]|$)",
                "", RegexOptions.IgnoreCase).Trim('-', '_', ' ');

            // Step 2: Strip trailing version numbers (any dash/dot separated digit segments)
            // e.g. "filecompare-1.0" → "filecompare", "FileCompare-1.0.2" → "FileCompare"
            keyword = Regex.Replace(keyword,
                @"[-_.\s]+\d[\d.\-]*$", "", RegexOptions.None).Trim('-', '_', ' ');

            // Step 3: Also strip versions embedded mid-name before taking first word
            // e.g. "filecompare-1.0-x" → after step 2 may still have partial, strip again
            keyword = Regex.Replace(keyword,
                @"[-_.\s]+\d[\d.\-]*$", "", RegexOptions.None).Trim('-', '_', ' ');

            // Step 4: Take the first dash/space/underscore separated segment as the keyword
            var parts = Regex.Split(keyword, @"[-_\s]+");
            string result = parts.FirstOrDefault(p => p.Length >= 2) ?? keyword;
            return result.ToLowerInvariant();
        }

        /// <summary>
        /// Smarter registry lookup for EXE installs.
        /// Priority: (1) qasnap registry data, (2) live registry with keyword+alias matching.
        /// qasnap = what was actually written during install on the clean capture machine.
        /// </summary>
        private static RegistryEntry? FindRegistryEntryForExe(
            string exeFileName, string exeName, QaSnapshot? qaSnap = null)
        {
            // Matched on the keyword taken from the installer filename alone.
            //
            // There was an alias map here - a list of products paired with the vendor words their
            // registry entries use, so an installer's name would also accept its publisher's. It is
            // gone with every other compiled list of other people's software. The comparisons below
            // still catch the ordinary cases (keyword "duplicatecleaner" against DisplayName
            // "Duplicate Cleaner 1.2"); what is lost is the app whose registry name shares no word
            // with its installer filename, and for those the qasnap capture is the better answer
            // anyway - it records what the install actually wrote.
            string keyword = GetExeKeyword(exeFileName);
            var matchTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keyword };

            // Pass 1: search qasnap registry (preferred source of truth) ─────────
            if (qaSnap?.Registry != null)
            {
                var snapKeys = qaSnap.Registry
                    .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                foreach (var (keyPath, entries) in snapKeys)
                {
                    if (keyPath.IndexOf(@"CurrentVersion\Uninstall", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string dn  = entries.FirstOrDefault(e => e.Value.Equals("DisplayName",  StringComparison.OrdinalIgnoreCase))?.Data ?? "";
                    string pub = entries.FirstOrDefault(e => e.Value.Equals("Publisher",    StringComparison.OrdinalIgnoreCase))?.Data ?? "";
                    string sub = keyPath.Split('\\').Last();

                    if (string.IsNullOrEmpty(dn)) continue;

                    // Also match normalised (space-removed) to catch e.g.
                    // keyword="duplicatecleaner" vs DisplayName="Duplicate Cleaner 1.2"
                    string dnNorm  = Regex.Replace(dn,  @"[\s\-_.]", "").ToLowerInvariant();
                    string pubNorm = Regex.Replace(pub, @"[\s\-_.]", "").ToLowerInvariant();
                    string subNorm = Regex.Replace(sub, @"[\s\-_.]", "").ToLowerInvariant();
                    bool matched = matchTerms.Any(term =>
                        dn.IndexOf(term,  StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pub.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sub.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        dnNorm.Contains(term.ToLowerInvariant()) ||
                        pubNorm.Contains(term.ToLowerInvariant()) ||
                        subNorm.Contains(term.ToLowerInvariant()));
                    if (!matched) continue;

                    string Get(string valueName) =>
                        entries.FirstOrDefault(e => e.Value.Equals(valueName, StringComparison.OrdinalIgnoreCase))?.Data ?? "";

                    return new RegistryEntry(
                        DisplayName:     dn,
                        DisplayVersion:  Get("DisplayVersion"),
                        Publisher:       pub,
                        InstallLocation: Get("InstallLocation"),
                        NoModify:        Get("NoModify"),
                        NoRepair:        Get("NoRepair"),
                        NoRemove:        Get("NoRemove"),
                        RegistryKeyPath: keyPath
                    );
                }
            }

            // Pass 2: exact display name match (live registry) ────────────────────
            var exact = FindRegistryEntry(exeName, null);
            if (exact != null) return exact;

            // Pass 3: keyword + vendor alias search (live registry) ───────────────
            var hives = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (var hive in hives)
            {
                foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    using var key = root.OpenSubKey(hive);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var entry = key.OpenSubKey(sub);
                        if (entry == null) continue;
                        string dn  = entry.GetValue("DisplayName") as string ?? "";
                        string pub = entry.GetValue("Publisher")   as string ?? "";
                        if (string.IsNullOrEmpty(dn)) continue;

                        // Also normalised match (removes spaces/dashes)
                        string dnN  = Regex.Replace(dn,  @"[\s\-_.]", "").ToLowerInvariant();
                        string pubN = Regex.Replace(pub, @"[\s\-_.]", "").ToLowerInvariant();
                        string subN = Regex.Replace(sub, @"[\s\-_.]", "").ToLowerInvariant();
                        bool matched = matchTerms.Any(term =>
                            dn.IndexOf(term,  StringComparison.OrdinalIgnoreCase) >= 0 ||
                            pub.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            sub.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            dnN.Contains(term.ToLowerInvariant()) ||
                            pubN.Contains(term.ToLowerInvariant()) ||
                            subN.Contains(term.ToLowerInvariant()));
                        if (!matched) continue;

                        string rootName   = root == Registry.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
                        string regKeyPath = $"{rootName}\\{hive}\\{sub}";
                        return new RegistryEntry(
                            DisplayName:     dn,
                            DisplayVersion:  entry.GetValue("DisplayVersion")  as string ?? "",
                            Publisher:       entry.GetValue("Publisher")       as string ?? "",
                            InstallLocation: entry.GetValue("InstallLocation") as string ?? "",
                            NoModify:        entry.GetValue("NoModify")?.ToString(),
                            NoRepair:        entry.GetValue("NoRepair")?.ToString(),
                            NoRemove:        entry.GetValue("NoRemove")?.ToString(),
                            RegistryKeyPath: regKeyPath
                        );
                    }
                }
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        //  MSI DATABASE READER
        // ══════════════════════════════════════════════════════════════════
        private record MsiInfo(
            string? ProductName, string? ProductVersion, string? Manufacturer,
            string? ProductCode, List<(string Name, string Directory, string Description)> Shortcuts, Dictionary<string, string> Properties);

        private static MsiInfo? ReadMsiInfo(string msiPath)
        {
            if (!File.Exists(msiPath)) return null;
            try
            {
                Type? t = Type.GetTypeFromProgID("WindowsInstaller.Installer");
                if (t == null) return null;
                dynamic installer = Activator.CreateInstance(t)!;
                dynamic db = installer.OpenDatabase(msiPath, 0 /*msiOpenDatabaseModeReadOnly*/);

                string? Get(string prop) {
                    try {
                        dynamic view = db.OpenView($"SELECT `Value` FROM `Property` WHERE `Property`='{prop}'");
                        view.Execute();
                        dynamic? rec = view.Fetch();
                        return rec?.StringData[1];
                    } catch { return null; }
                }

                // Read all properties
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try {
                    dynamic pv = db.OpenView("SELECT `Property`, `Value` FROM `Property`");
                    pv.Execute();
                    while (true) {
                        dynamic? r = pv.Fetch();
                        if (r == null) break;
                        props[r.StringData[1]] = r.StringData[2];
                    }
                } catch { }

                // Read shortcuts - capture Name, Directory_ and Description (full display name)
                var shortcuts = new List<(string Name, string Directory, string Description)>();
                try {
                    dynamic sv = db.OpenView("SELECT `Name`, `Directory_`, `Description` FROM `Shortcut`");
                    sv.Execute();
                    while (true) {
                        dynamic? r = sv.Fetch();
                        if (r == null) break;
                        shortcuts.Add((r.StringData[1], r.StringData[2] ?? "", r.StringData[3] ?? ""));
                    }
                } catch { }

                return new MsiInfo(
                    Get("ProductName"), Get("ProductVersion"), Get("Manufacturer"),
                    Get("ProductCode"), shortcuts, props);
            }
            catch { return null; }
        }

        // ── Extract -Transforms value: handles ('file.mst') array syntax ─
        private static string ExtractTransformsParam(string cmd)
        {
            // Patterns to try in order:
            // 1. -Transforms ('path.mst')   single-quoted array
            // 2. -Transforms ("path.mst")   double-quoted array
            // 3. -Transforms 'path.mst'     plain single-quoted
            // 4. -Transforms "path.mst"     plain double-quoted
            // 5. -Transforms path.mst       unquoted
            var patterns = new[]
            {
                @"-Transforms\s+\(\s*'([^']+\.mst)'\s*\)",
                @"-Transforms\s+\(\s+""([^""]+\.mst)""\s*\)",
                @"-Transforms\s+'([^']+\.mst)'",
                @"-Transforms\s+""([^""]+\.mst)""",
                @"-Transforms\s+(\S+\.mst)",
            };
            foreach (var pattern in patterns)
            {
                var m = Regex.Match(cmd, pattern, RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value.Trim('\'', '"');
            }
            return "";
        }

        // ── Read MSI database with MST transform applied ──────────────────
        // MST files are Windows Installer databases. We read them directly
        // (mode 0 = ReadOnly) to get the property delta, then merge over
        // the base MSI. ApplyTransform is used additionally for shortcuts.
        private static MsiInfo? ReadMsiInfoWithMst(string msiPath, string mstPath)
        {
            if (!File.Exists(msiPath) || !File.Exists(mstPath)) return null;
            try
            {
                Type? t = Type.GetTypeFromProgID("WindowsInstaller.Installer");
                if (t == null) return null;
                dynamic installer = Activator.CreateInstance(t)!;

                // ── Step 1: Read base MSI properties and shortcuts ────────
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var shortcuts = new List<(string Name, string Directory, string Description)>();

                try {
                    dynamic dbBase = installer.OpenDatabase(msiPath, 0);
                    try {
                        dynamic pv = dbBase.OpenView("SELECT `Property`, `Value` FROM `Property`");
                        pv.Execute();
                        while (true) {
                            dynamic? r = pv.Fetch();
                            if (r == null) break;
                            props[r.StringData[1]] = r.StringData[2];
                        }
                    } catch { }
                    try {
                        dynamic sv = dbBase.OpenView(
                            "SELECT `Name`, `Directory_`, `Description` FROM `Shortcut`");
                        sv.Execute();
                        while (true) {
                            dynamic? r = sv.Fetch();
                            if (r == null) break;
                            shortcuts.Add((r.StringData[1], r.StringData[2] ?? "",
                                           r.StringData[3] ?? ""));
                        }
                    } catch { }
                } catch { }

                // ── Step 2: Read MST properties directly ─────────────────
                // MST is a valid Windows Installer database (mode 0 = ReadOnly).
                // Try both StringData[1]/[2] and StringData[0]/[1] - COM index
                // can be 0-based or 1-based depending on the WI version.
                try {
                    dynamic dbMst = installer.OpenDatabase(mstPath, 0);
                    bool readOk = false;
                    // Try 1-based indices first (most common)
                    try {
                        dynamic mv = dbMst.OpenView("SELECT `Property`, `Value` FROM `Property`");
                        mv.Execute();
                        while (true) {
                            dynamic? r = mv.Fetch();
                            if (r == null) break;
                            string pn = Convert.ToString(r.StringData[1]) ?? "";
                            string pv = Convert.ToString(r.StringData[2]) ?? "";
                            if (!string.IsNullOrEmpty(pn)) {
                                props[pn] = pv;
                                readOk = true;
                            }
                        }
                    } catch { }
                    // If nothing read, try 0-based indices
                    if (!readOk) {
                        try {
                            dynamic mv = dbMst.OpenView("SELECT `Property`, `Value` FROM `Property`");
                            mv.Execute();
                            while (true) {
                                dynamic? r = mv.Fetch();
                                if (r == null) break;
                                string pn = Convert.ToString(r.StringData[0]) ?? "";
                                string pv = Convert.ToString(r.StringData[1]) ?? "";
                                if (!string.IsNullOrEmpty(pn))
                                    props[pn] = pv;
                            }
                        } catch { }
                    }
                    // Read shortcuts from MST
                    try {
                        dynamic sv = dbMst.OpenView(
                            "SELECT `Name`, `Directory_`, `Description` FROM `Shortcut`");
                        sv.Execute();
                        var mstShortcuts = new List<(string Name, string Directory, string Description)>();
                        while (true) {
                            dynamic? r = sv.Fetch();
                            if (r == null) break;
                            mstShortcuts.Add((r.StringData[1], r.StringData[2] ?? "",
                                              r.StringData[3] ?? ""));
                        }
                        if (mstShortcuts.Count > 0)
                            shortcuts = mstShortcuts;
                    } catch { }
                } catch { }

                // ── Step 3: Apply transform - try all error suppression flags
                // msiTransformErrorAll = 65535 suppresses all validation errors.
                // Also try 0, 256 (ViewTransform), 512 (AddExistingRow),
                // 768 and full 65535 until one succeeds.
                foreach (int flags in new[] { 65535, 0, 256, 512, 768 })
                {
                    try {
                        dynamic dbTx = installer.OpenDatabase(msiPath, 1);
                        dbTx.ApplyTransform(mstPath, flags);
                        // Read ALL properties from merged view - overrides everything
                        try {
                            dynamic pv2 = dbTx.OpenView(
                                "SELECT `Property`, `Value` FROM `Property`");
                            pv2.Execute();
                            while (true) {
                                dynamic? r = pv2.Fetch();
                                if (r == null) break;
                                string pn = Convert.ToString(r.StringData[1]) ?? "";
                                string pv = Convert.ToString(r.StringData[2]) ?? "";
                                if (!string.IsNullOrEmpty(pn))
                                    props[pn] = pv;
                            }
                        } catch { }
                        if (shortcuts.Count == 0)
                        {
                            try {
                                dynamic sv = dbTx.OpenView(
                                    "SELECT `Name`, `Directory_`, `Description` FROM `Shortcut`");
                                sv.Execute();
                                while (true) {
                                    dynamic? r = sv.Fetch();
                                    if (r == null) break;
                                    shortcuts.Add((r.StringData[1], r.StringData[2] ?? "",
                                                   r.StringData[3] ?? ""));
                                }
                            } catch { }
                        }
                        break;
                    } catch { }
                }

                props.TryGetValue("ProductName",    out string? productName);
                props.TryGetValue("ProductVersion", out string? productVersion);
                props.TryGetValue("Manufacturer",   out string? manufacturer);
                props.TryGetValue("ProductCode",    out string? productCode);

                // If shortcuts still empty, fall back to base MSI shortcuts
                if (shortcuts.Count == 0)
                {
                    try {
                        dynamic dbFb = installer.OpenDatabase(msiPath, 0);
                        dynamic sv = dbFb.OpenView(
                            "SELECT `Name`, `Directory_`, `Description` FROM `Shortcut`");
                        sv.Execute();
                        while (true) {
                            dynamic? r = sv.Fetch();
                            if (r == null) break;
                            shortcuts.Add((r.StringData[1], r.StringData[2] ?? "",
                                           r.StringData[3] ?? ""));
                        }
                    } catch { }
                }

                return new MsiInfo(productName, productVersion, manufacturer,
                                   productCode, shortcuts, props);
            }
            catch { return null; }
        }

        // ── Win32 P/Invoke for GetShortPathName ───────────────────────────
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern uint GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

        /// <summary>
        /// Returns the 8.3 short name stem (no extension, uppercase) for a given file path.
        /// e.g. "C:\...\Online Uninstall.lnk" → "ONLINE~1"
        /// </summary>
        private static string GetShortStem(string fullPath)
        {
            var sb = new System.Text.StringBuilder(512);
            uint len = GetShortPathName(fullPath, sb, (uint)sb.Capacity);
            if (len == 0) return "";
            return IOPath.GetFileNameWithoutExtension(sb.ToString()).ToUpperInvariant();
        }

        // ── Resolve shortcut to real long-name disk path ──────────────────
        private static string ResolveShortcutPath(string displayName, string msiDirectory = "", string productName = "")
        {
            // displayName may be the MSI 8.3 short stem (e.g. "ONLINE~1") or a full name.
            // Strip any .lnk suffix to get just the stem for matching.
            string rawStem = IOPath.GetFileNameWithoutExtension(displayName.Trim()).ToUpperInvariant();
            bool isShortenedName = rawStem.Contains('~'); // 8.3 indicator

            // Determine Desktop vs Start Menu from MSI Directory_ value
            bool isDesktop = msiDirectory.IndexOf("Desktop", StringComparison.OrdinalIgnoreCase) >= 0
                          || msiDirectory.Equals("DesktopFolder", StringComparison.OrdinalIgnoreCase);

            // Ordered candidate base directories
            string userDesktop  = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string sysDesktop   = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            string sysPrograms  = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

            IEnumerable<string> SearchDirs(string baseDir)
            {
                yield return baseDir;
                if (!Directory.Exists(baseDir)) yield break;
                // product-name subfolder first
                if (!string.IsNullOrWhiteSpace(productName))
                    yield return IOPath.Combine(baseDir, productName.Trim());
                // then all one-level subfolders
                foreach (var sub in Directory.GetDirectories(baseDir))
                    yield return sub;
            }

            var baseDirs = isDesktop
                ? new[] { userDesktop, sysDesktop }
                : new[] { sysPrograms, userPrograms }; // sysPrograms first - Orca.lnk is in C:\ProgramData\...

            foreach (var baseDir in baseDirs)
            {
                foreach (var dir in SearchDirs(baseDir))
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
                    {
                        if (isShortenedName)
                        {
                            // Compare the file's own 8.3 short stem against MSI stem
                            string fileStem = GetShortStem(lnk);
                            if (fileStem == rawStem) return lnk; // returns full long path
                        }
                        else
                        {
                            // Direct long-name match (case-insensitive)
                            string fileStem = IOPath.GetFileNameWithoutExtension(lnk);
                            if (string.Equals(fileStem, displayName.Trim(),
                                StringComparison.OrdinalIgnoreCase)) return lnk;
                            // Also try matching without .lnk in displayName
                            string dStem = IOPath.GetFileNameWithoutExtension(displayName.Trim());
                            if (string.Equals(fileStem, dStem,
                                StringComparison.OrdinalIgnoreCase)) return lnk;
                        }
                    }
                }
            }

            // Fallback: construct expected path (file not found yet)
            string fallbackName = displayName.Trim();
            if (!fallbackName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) fallbackName += ".lnk";
            if (isDesktop)
                return IOPath.Combine(userDesktop, fallbackName);
            // For Start Menu shortcuts: prefer sysPrograms (C:\ProgramData\...) directly,
            // without a product subfolder - many shortcuts install to Programs root
            return IOPath.Combine(sysPrograms, fallbackName);
        }

        // ══════════════════════════════════════════════════════════════════
        //  QA.JSON CONFIG
        // ══════════════════════════════════════════════════════════════════
        private class QAMsiProp { public string Property { get; set; } = ""; public string Value { get; set; } = ""; }
        private class QAConfig
        {
            [JsonPropertyName("CheckNoModify")]
            public string? NoModify { get; set; }
            [JsonPropertyName("CheckNoRepair")]
            public string? NoRepair { get; set; }
            [JsonPropertyName("CheckNoRemove")]
            public string? NoRemove { get; set; }
            [JsonPropertyName("CheckMSIPropertiesAdded")]
            public List<QAMsiProp>? MSIProperties { get; set; }
            [JsonPropertyName("CheckShortcutKeywordsToRemove")]
            public Dictionary<string, string>? ShortcutKeywords { get; set; }
            [JsonPropertyName("CheckDesktopShortcutsRemoved")]
            public string? DesktopShortcuts { get; set; }
            public string? ExportPath { get; set; }
            [JsonPropertyName("SVRPath")]
            public string? QAsnapPath { get; set; }
            public string? CopyXlsxToDocuments { get; set; }
            // Backward compatibility: older configs used the key "CopyCSVToDocuments".
            [JsonPropertyName("CopyCSVToDocuments")]
            public string? CopyCSVToDocumentsLegacy { get; set; }
            public string? CopyHtmlToDocuments { get; set; }

            public bool ShouldCopyXlsx =>
                string.Equals(ValidatePackagePage.RuntimeCopyCSVOverride  ?? CopyXlsxToDocuments ?? CopyCSVToDocumentsLegacy, "Yes", StringComparison.OrdinalIgnoreCase);
            public bool ShouldCopyHtml =>
                string.Equals(ValidatePackagePage.RuntimeCopyHtmlOverride ?? CopyHtmlToDocuments, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        // ── QAsnap lookup ────────────────────────────────────────────────────────
        // Priority: (1) QAsnapPath from QA.json / UI TextBox
        //           (2) C:\Temp\Validate Source (default capture output)
        //           (3) Not found → return empty list (do NOT fall back to other dirs)
        private static readonly string DefaultQAsnapDir = @"C:\Temp\Validate Source";

        /// <summary>
        /// Returns the ordered list of directories to search for .svr files.
        /// Only two locations are ever considered: the configured QAsnapPath and the
        /// default capture output folder. Callers must treat an empty result as "no qasnap".
        /// </summary>
        // When the SVR Path field points to a specific .svr FILE (browsed by the user, or
        // auto-filled from the Execute handoff), that file is the ground truth for every check -
        // no folder scanning / fuzzy matching. Returns null when the field is a folder or empty,
        // in which case the existing scan behaviour applies. _qaConfig.QAsnapPath tracks the field
        // (TxtQAsnapPath_TextChanged and the resolve step both keep it in sync).
        private string? SelectedSvrFile()
        {
            string p = _qaConfig?.QAsnapPath?.Trim() ?? "";
            return (!string.IsNullOrEmpty(p)
                    && p.EndsWith(".svr", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(p)) ? p : null;
        }

        private IEnumerable<string> QAsnapSearchDirs()
        {
            // _qaConfig.QAsnapPath is always resolved before checks run:
            //   user UI value → QA.json value → C:\Temp\Validate Source (default)
            // So we only need to yield the single resolved path - no second fallback needed.
            string cfgPath = _qaConfig?.QAsnapPath?.Trim() ?? "";

            // If the field is a specific .svr file, search its containing folder.
            if (!string.IsNullOrEmpty(cfgPath)
                && cfgPath.EndsWith(".svr", StringComparison.OrdinalIgnoreCase)
                && File.Exists(cfgPath))
            {
                string? d = IOPath.GetDirectoryName(cfgPath);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) yield return d;
                yield break;
            }

            if (!string.IsNullOrEmpty(cfgPath) && Directory.Exists(cfgPath))
                yield return cfgPath;

            // If cfgPath is not set or doesn't exist, yield the default as last resort
            if (string.IsNullOrEmpty(cfgPath) && Directory.Exists(DefaultQAsnapDir))
                yield return DefaultQAsnapDir;

            // Nothing else - callers must treat missing .svr as "skip"
        }

        /// <summary>
        /// Resolves a .svr file for the given base name by checking QAsnapSearchDirs only.
        /// Pass 1 - exact filename patterns: "<baseName> - For QA.svr" and "<baseName>.svr"
        /// Pass 2 - scan every *.svr in the dirs and match by the FileName field stored
        ///          inside the JSON (e.g. VideoLAN_VLCmediaplayer_3.0.23.svr contains
        ///          FileName="filecompare-1.0-x64.exe" - matched when baseName="filecompare-1.0-x64").
        ///          Also matches by normalised tokens (strips version/vendor noise).
        /// Returns null only when not found in either pass - callers treat null as "skip".
        /// </summary>
        private string? FindQAsnapFile(string baseName)
        {
            // An explicitly chosen .svr file overrides name/keyword matching entirely.
            var chosen = SelectedSvrFile();
            if (chosen != null) return chosen;

            if (string.IsNullOrWhiteSpace(baseName)) return null;

            // Pass 1: exact filename patterns (fast)
            string suffix1 = baseName + " - For QA.svr";
            string suffix2 = baseName + ".svr";
            foreach (var dir in QAsnapSearchDirs())
            {
                string p1 = IOPath.Combine(dir, suffix1);
                if (File.Exists(p1)) return p1;
                string p2 = IOPath.Combine(dir, suffix2);
                if (File.Exists(p2)) return p2;
            }

            // Pass 2: fuzzy scan - use GetExeKeyword for precise token extraction
            // (handles version/arch stripping correctly e.g. filecompare-1.0-x64 → "filecompare")
            // then scan all *.svr files matching by filename or JSON FileName field.
            string keyword = GetExeKeyword(baseName + ".exe"); // e.g. "filecompare", "duplicatecleaner"
            string baseNorm = NormaliseSnapName(baseName);     // fallback normalisation

            foreach (var snapFile in AllQAsnapFiles())
            {
                try
                {
                    // Fast check: does the .svr filename contain the keyword?
                    string fileBase = IOPath.GetFileNameWithoutExtension(snapFile);
                    string fileNorm = NormaliseSnapName(fileBase);
                    if (fileNorm.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        fileNorm.Contains(baseNorm, StringComparison.OrdinalIgnoreCase))
                        return snapFile;

                    // Slower check: read the JSON and match FileName field
                    string json = File.ReadAllText(snapFile);
                    var snap = DeserializeSnapshot(json);
                    if (snap == null) continue;

                    string snapFileBaseName = IOPath.GetFileNameWithoutExtension(snap.FileName ?? "");
                    string snapKeyword = string.IsNullOrEmpty(snapFileBaseName) ? "" : GetExeKeyword(snap.FileName!);
                    if (!string.IsNullOrEmpty(snapFileBaseName) &&
                        (string.Equals(snapFileBaseName, baseName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(snapKeyword, keyword, StringComparison.OrdinalIgnoreCase) ||
                         NormaliseSnapName(snapFileBaseName).Contains(baseNorm, StringComparison.OrdinalIgnoreCase)))
                        return snapFile;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Strips version numbers, vendor prefixes and punctuation from a filename base
        /// to produce a short token suitable for fuzzy matching.
        /// e.g. "filecompare-1.0-x64" → "filecompare", "Smekal_FileCompare_1.0" → "filecompare"
        /// </summary>
        private static string NormaliseSnapName(string name)
        {
            // Remove version segments (digits separated by dots/dashes)
            string s = System.Text.RegularExpressions.Regex.Replace(name,
                @"[-_.]?\d[\d.\-]*", "", System.Text.RegularExpressions.RegexOptions.None);
            // Remove common vendor/arch noise
            s = System.Text.RegularExpressions.Regex.Replace(s,
                @"(?i)(win32|win64|x64|x86|setup|install|installer|For\s*QA)", "");
            // Collapse separators and lower-case
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s\-_.]", "").ToLowerInvariant();
            return s;
        }

        /// <summary>
        /// Scans QAsnapSearchDirs for all *.svr files (for product-code matching).
        /// Returns an empty sequence if neither directory exists or contains any snaps.
        /// </summary>
        private IEnumerable<string> AllQAsnapFiles()
        {
            // An explicitly chosen .svr file is the only candidate (overrides product-code scans).
            var chosen = SelectedSvrFile();
            if (chosen != null) { yield return chosen; yield break; }

            foreach (var dir in QAsnapSearchDirs())
            {
                IEnumerable<string> files;
                try { files = Directory.GetFiles(dir, "*.svr"); }
                catch { continue; }
                foreach (var f in files) yield return f;
            }
        }

        /// <summary>
        /// Fixes unescaped backslashes in JSON string values.
        /// Handles the common authoring mistake of writing Windows paths as
        /// "C:\Temp" in source but saving as "C:\Temp" (single backslash).
        /// Uses a simple state machine to only touch characters inside string values.
        /// </summary>
        private static string FixJsonBackslashes(string json)
        {
            var sb = new System.Text.StringBuilder(json.Length + 16);
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        // Check what follows - if it's a valid JSON escape char, keep as-is
                        if (i + 1 < json.Length)
                        {
                            char next = json[i + 1];
                            bool validEscape = next == '"' || next == '\\' || next == '/'
                                            || next == 'b'  || next == 'f'  || next == 'n'
                                            || next == 'r'  || next == 't'  || next == 'u';
                            if (validEscape)
                            {
                                sb.Append(c); // keep the backslash
                                sb.Append(next);
                                i++;          // skip next char
                                continue;
                            }
                        }
                        // Unescaped backslash - double it
                        sb.Append("\\\\");
                        continue;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else if (c == '"')
                {
                    inString = true;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Returns the set of normalised shortcut "prefixes" that the user EXCLUDED in the snapshot.
        // A prefix is the .lnk base name with any 8.3 "~N" suffix stripped and reduced to its first
        // token, lowercased - so the MSI's 8.3 name "ONLINE~1.lnk" matches the captured long name
        // "Online Uninstall.lnk". Used to skip MSI-declared shortcuts the user already excluded.
        private static HashSet<string> ExcludedShortcutPrefixes(QaSnapshot? snap)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snap?.ExcludedPaths == null) return set;
            foreach (var raw in snap.ExcludedPaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var parts = raw.Split('|');
                if (parts.Length < 2 || parts[0] != "Shortcut") continue;
                string pfx = NormalizeShortcutPrefix(parts[1]);
                if (!string.IsNullOrEmpty(pfx)) set.Add(pfx);
            }
            return set;
        }

        // "C:\...\Online Uninstall.lnk" → "online" ; "ONLINE~1.lnk" → "online"
        private static string NormalizeShortcutPrefix(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string name = path.Replace('/', '\\');
            int slash = name.LastIndexOf('\\');
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            int tilde = name.IndexOf('~');
            if (tilde >= 0) name = name.Substring(0, tilde);   // strip 8.3 "~N"
            int space = name.IndexOf(' ');
            if (space >= 0) name = name.Substring(0, space);    // first token of long name
            return name.Trim().ToLowerInvariant();
        }

        private static bool IsShortcutExcluded(string scPath, HashSet<string> excludedPrefixes)
        {
            if (excludedPrefixes.Count == 0) return false;
            return excludedPrefixes.Contains(NormalizeShortcutPrefix(scPath));
        }

        // ── Snapshot deserialization with exclusion pruning ──────────────────────
        // Deserializes a .svr and removes any items the user UNCHECKED in Validate Source
        // (recorded in ExcludedPaths). Excluded items are not part of the package, so they must
        // never be validated or reported as failures.
        private static QaSnapshot? DeserializeSnapshot(string json)
        {
            var snap = JsonSerializer.Deserialize<QaSnapshot>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (snap != null)
            {
                // Capture desktop shortcuts BEFORE exclusion pruning so the desktop-removed
                // check can still see a desktop shortcut the user excluded in Validate Source.
                snap.AllDesktopShortcuts = snap.Shortcuts
                    .Where(s => !string.IsNullOrWhiteSpace(s)
                             && s.IndexOf("\\Desktop\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                ApplyExclusions(snap);
            }
            return snap;
        }

        // Validates the "Desktop shortcuts removed" rule (CheckDesktopShortcutsRemoved = Yes).
        // Candidates come from TWO sources so the check is robust to snapshot gaps:
        //   1. desktop shortcuts captured BEFORE exclusion pruning (catches ones the user excluded
        //      in Validate Source - e.g. WinSCP), and
        //   2. a LIVE scan of the actual desktops (catches a desktop shortcut the source never
        //      captured but the installed package still left behind - e.g. Smekal File Compare).
        // Only the app's OWN desktop shortcut is flagged (matched by app/installer name); unrelated
        // desktop shortcuts are ignored. Present on disk = Fail (not removed); a captured shortcut
        // that is now absent = Pass (removed). One row per unique shortcut.
        private void CheckDesktopShortcutsRemoved(ref int idx, IEnumerable<string>? desktopShortcuts,
            string cmdLabel, string appName, string exeHint)
        {
            var candidates = new List<string>();
            if (desktopShortcuts != null) candidates.AddRange(desktopShortcuts);
            candidates.AddRange(EnumerateDesktopShortcuts());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scPath in candidates)
            {
                if (string.IsNullOrWhiteSpace(scPath) || !seen.Add(scPath)) continue;
                if (!IsAppRelatedDesktopShortcut(scPath, appName, exeHint)) continue;
                bool exists = File.Exists(scPath);
                _results.Add(new(idx++, cmdLabel + " · Shortcut",
                    exists ? scPath + "  (Desktop shortcut not removed)" : scPath,
                    exists ? QAStatus.Fail : QAStatus.Pass,
                    null,
                    exists ? RowHighlight.Red : RowHighlight.None,
                    exists ? IOPath.GetDirectoryName(scPath) : null));
            }
        }

        // Enumerates *.lnk files on the live desktops: the all-users (public) desktop, the current
        // user's desktop, and every C:\Users\*\Desktop (best-effort). Used by the desktop-removed
        // check so a shortcut the source snapshot never captured is still found on disk.
        private static IEnumerable<string> EnumerateDesktopShortcuts()
        {
            var dirs = new List<string>();
            void Add(string d) { if (!string.IsNullOrWhiteSpace(d)) dirs.Add(d); }
            try { Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
            try { Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
            try
            {
                string usersRoot = IOPath.GetDirectoryName(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users";
                if (Directory.Exists(usersRoot))
                    foreach (var prof in Directory.GetDirectories(usersRoot))
                        Add(IOPath.Combine(prof, "Desktop"));
            }
            catch { }

            var seenDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                if (!seenDir.Add(dir) || !Directory.Exists(dir)) continue;
                string[] lnks;
                try { lnks = Directory.GetFiles(dir, "*.lnk"); } catch { continue; }
                foreach (var l in lnks) yield return l;
            }
        }

        // True when a desktop shortcut's file name relates to the app/installer name, so unrelated
        // pre-existing desktop shortcuts captured by Validate Source are not flagged as the app's.
        private static bool IsAppRelatedDesktopShortcut(string scPath, string appName, string exeHint)
        {
            string stem = IOPath.GetFileNameWithoutExtension(scPath).Trim().ToLowerInvariant();
            if (stem.Length == 0) return false;
            foreach (var hint in new[] { appName, exeHint })
            {
                if (string.IsNullOrWhiteSpace(hint)) continue;
                string h = hint.Trim().ToLowerInvariant();
                if (h.Length == 0) continue;
                if (h.Contains(stem) || stem.Contains(h)) return true;
                string first = h.Split(new[] { ' ', '-', '_' },
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (first.Length >= 3 && (stem.Contains(first) || first.Contains(stem))) return true;
            }
            return false;
        }

        // Prunes excluded items from a snapshot's lists. ExcludedPaths entries are
        // "Category|DisplayPath|Value"; matching is by DisplayPath for Files/Folders/Shortcuts
        // and by Key (+optional Value) for Registry.
        private static void ApplyExclusions(QaSnapshot snap)
        {
            if (snap.ExcludedPaths == null || snap.ExcludedPaths.Count == 0) return;

            // Build fast lookup sets per category from the excluded paths.
            var exFiles     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exFolders   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exShortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exRegistry  = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "Key|Value"

            foreach (var raw in snap.ExcludedPaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var parts = raw.Split('|');
                string cat   = parts.Length > 0 ? parts[0] : "";
                string path  = parts.Length > 1 ? parts[1] : "";
                string value = parts.Length > 2 ? parts[2] : "";
                switch (cat)
                {
                    case "File":      exFiles.Add(path); break;
                    case "Folder":    exFolders.Add(path); break;
                    case "Shortcut":  exShortcuts.Add(path); break;
                    case "Registry":  exRegistry.Add(path + "|" + value); break;
                }
            }

            if (exFiles.Count > 0)
                snap.Files = snap.Files.Where(f => !exFiles.Contains(f)).ToList();
            if (exFolders.Count > 0)
                snap.Folders = snap.Folders.Where(f => !exFolders.Contains(f)).ToList();
            if (exShortcuts.Count > 0)
                snap.Shortcuts = snap.Shortcuts.Where(s => !exShortcuts.Contains(s)).ToList();
            if (exRegistry.Count > 0)
                snap.Registry = snap.Registry
                    .Where(r => !exRegistry.Contains((r.Key ?? "") + "|" + (r.Value ?? "")))
                    .ToList();
        }

        private static QAConfig? LoadQAConfig(string packageFolder = "")
        {
            string exeDir = IOPath.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)!;

            var candidates = new List<string>
            {
                // Primary - always present in installed app
                @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidatePackage.json",
                // Fallbacks for dev / portable builds
                IOPath.Combine(exeDir, "Templates", "ValidatePackage.json"),
                IOPath.Combine(exeDir, "ValidatePackage.json"),
                IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", "ValidatePackage.json"),
                IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "ValidatePackage.json"),
            };

            if (!string.IsNullOrWhiteSpace(packageFolder))
            {
                candidates.Add(IOPath.Combine(packageFolder, "ValidatePackage.json"));
                string parent = IOPath.GetDirectoryName(packageFolder) ?? "";
                if (!string.IsNullOrEmpty(parent))
                    candidates.Add(IOPath.Combine(parent, "ValidatePackage.json"));
            }

            foreach (var path in candidates.Distinct())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    string json = File.ReadAllText(path);
                    // Handle empty file gracefully - return default config
                    if (string.IsNullOrWhiteSpace(json)) return new QAConfig();

                    var opts = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                    };

                    // First attempt: parse as-is
                    try
                    {
                        var cfg = JsonSerializer.Deserialize<QAConfig>(json, opts);
                        return cfg ?? new QAConfig();
                    }
                    catch { }

                    // Second attempt: fix unescaped backslashes in string values.
                    // Common authoring error: "C:\Temp" instead of "C:\Temp"
                    // Strategy: inside JSON string values only, replace single                     // that is NOT already a valid escape with \
                    string fixedJson = FixJsonBackslashes(json);
                    var cfg2 = JsonSerializer.Deserialize<QAConfig>(fixedJson, opts);
                    return cfg2 ?? new QAConfig();
                }
                catch { }
            }
            return null;
        }
    }
}
