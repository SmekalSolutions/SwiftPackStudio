// ValidateSourcePage.xaml.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace SwiftPack.Views
{
    /// <summary>
    /// One Windows Application-log entry written while the source was running. VIEW ONLY - this is
    /// deliberately NOT a SnapItem, so it can never be included, saved to .svr or packaged.
    /// </summary>
    public class EventLogRow
    {
        public string Level   { get; set; } = "";
        public string Logged  { get; set; } = "";
        public string Source  { get; set; } = "";
        public string EventId { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class SnapItem : INotifyPropertyChanged
    {
        private bool _isIncluded = true;
        public bool IsIncluded
        {
            get => _isIncluded;
            set { _isIncluded = value; OnPropertyChanged(); }
        }
        public string Category      { get; set; } = "";
        public string DisplayPath   { get; set; } = "";
        // For manually-added File items: the file's source location on disk (where to read the
        // content from at MSI-build time). Empty for captured items, which read from DisplayPath.
        public string SourceOnDisk  { get; set; } = "";
        // For Driver items: the DriverStore\FileRepository package folder this row represents
        // ("<inf>_<arch>_<hash>"). Stable identity used to reconcile rows across capture passes
        // and to preserve include/exclude state when display names are recomputed.
        public string DriverFolder  { get; set; } = "";
        // True for items the user added manually (vs captured), for display / persistence.
        public bool   IsManual      { get; set; }
        public string Value
        {
            get => (Category == "Registry" && string.IsNullOrEmpty(_value)) ? "(Default)" : _value;
            set => _value = value;
        }
        private string _value = "";
        public string Data          { get; set; } = "";
        public string DataType      { get; set; } = "";
        public string ServiceStatus { get; set; } = "";
        /// <summary>For Process items: the full command line the process was started with, switches
        /// and all. Empty for every other category, and empty for a process whose command line could
        /// not be read. Written into the .svr, so older captures simply load with it blank.</summary>
        public string CommandLine   { get; set; } = "";

        // ── Computed display columns ────────────────────────────────
        /// <summary>Registry data (for Registry items). Same as Data field.</summary>
        /// <summary>Registry data for Registry; service status for Service.</summary>
        public string RegistryData  => Category == "Registry" ? Data
                                     : Category == "Service"  ? ServiceStatus
                                     : Category == "EnvVar"   ? EnvScope
                                     : "";

        /// <summary>Scope label for EnvVar items: "System" for Machine, else "User"/"Process".</summary>
        public string EnvScope => Category != "EnvVar" ? ""
                                : Data.Equals("Machine", StringComparison.OrdinalIgnoreCase) ? "System"
                                : string.IsNullOrEmpty(Data) ? "" : Data;

        // ── Open column ─────────────────────────────────────────────
        /// <summary>URI or path used to open this item (same scheme as PerformQA).</summary>
        public string OpenPath =>
            Category == "Registry" ? $"reg://{DisplayPath}"
          : Category == "EnvVar"   ? "envvar://"
          : Category == "Service"  ? "service://"
          : Category == "Certificate" ? "cert://"
          : Category == "Firewall" ? "firewall://"
          : DisplayPath; // File / Folder / Shortcut — open directly

        public string OpenIcon =>
            Category == "Registry"    ? "🗝"
          : Category == "EnvVar"      ? "⚙"
          : Category == "Service"     ? "⚙"
          : Category == "Certificate" ? "🔒"
          : Category == "Firewall"    ? "🛡"
          : ""; // File/Folder/Shortcut use ShellIcon (real Windows icon)

        /// <summary>Real Windows shell icon for File/Folder/Shortcut items; null for others.</summary>
        public System.Windows.Media.ImageSource? ShellIcon => _shellIcon;
        private System.Windows.Media.ImageSource? _shellIcon;

        public string OpenTooltip =>
            Category == "Registry"    ? $"Open in Registry Editor: {DisplayPath}"
          : Category == "EnvVar"      ? "Open Environment Variables"
          : Category == "Service"     ? "Open Services (services.msc)"
          : Category == "Certificate" ? "Open Certificate Manager (certmgr.msc)"
          : Category == "Firewall"    ? "Open Windows Firewall (wf.msc)"
          : Category == "Folder"      ? $"Open folder: {DisplayPath}"
          : $"Open in Explorer: {DisplayPath}";

        /// <summary>Shows registry value type (REG_SZ etc.) for Registry items;
        /// for File/Shortcut items shows the Windows shell file type description (e.g. "Application").</summary>
        public string RegType       => Category == "Registry"   ? DataType
                                     : Category == "Service"    ? Data  // StartType (Manual/Automatic/etc.)
                                     : (Category == "File" || Category == "Shortcut" || Category == "Folder")
                                         ? (!string.IsNullOrEmpty(_fileType) ? _fileType : Category)
                                     : Category switch
                                     {
                                         "Certificate" => "Certificate",
                                         "Firewall"    => "Firewall",
                                         "EnvVar"      => "Environment",
                                         "Driver"      => "Driver",
                                         _             => Category
                                     };

        /// <summary>Date modified (same format as Windows Explorer) for File/Folder items.</summary>
        public string FileDate      => _fileDate;

        /// <summary>File size (same format as Windows Explorer) for File items.</summary>
        public string FileSize      => _fileSize;

        // Backing stores — populated by EnrichFromDisk()
        private string _fileType = "";
        private string _fileDate = "";
        private string _fileSize = "";

        /// <summary>
        /// Reads file type, date modified, and size from disk for File/Folder/Shortcut items.
        /// Call once after construction.
        /// </summary>
        public void EnrichFromDisk()
        {
            if (Category != "File" && Category != "Folder" && Category != "Shortcut") return;
            try
            {
                if (Category == "Folder" && System.IO.Directory.Exists(DisplayPath))
                {
                    var di = new System.IO.DirectoryInfo(DisplayPath);
                    _fileDate = di.LastWriteTime.ToString("dd/MM/yyyy HH:mm");
                    _fileType = "File folder";
                    try
                    {
                        long totalBytes = di.EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                                            .Sum(f => { try { return f.Length; } catch { return 0L; } });
                        _fileSize = totalBytes == 0 ? "" : FormatFileSize(totalBytes);
                    }
                    catch { _fileSize = ""; }
                    _shellIcon = ExtractShellIcon(DisplayPath, isFolder: true);
                }
                else if (System.IO.File.Exists(DisplayPath))
                {
                    var fi = new System.IO.FileInfo(DisplayPath);
                    _fileDate = fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm");
                    _fileSize = FormatFileSize(fi.Length);
                    _fileType = GetShellFileType(DisplayPath);
                    _shellIcon = ExtractShellIcon(DisplayPath, isFolder: false);
                }
                // File/folder not on disk — nothing to enrich
            }
            catch { }
        }

        private static System.Windows.Media.ImageSource? ExtractShellIcon(string path, bool isFolder)
        {
            try
            {
                var shfi = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
                uint attr  = isFolder ? 0x10u : 0x80u; // FILE_ATTRIBUTE_DIRECTORY or FILE_ATTRIBUTE_NORMAL
                IntPtr res = SHGetFileInfo(path, attr, ref shfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(shfi), flags);
                if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

                var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(shfi.hIcon);
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static string FormatFileSize(long bytes)
        {
            // Windows Explorer rounds UP to nearest KB/MB/GB (ceiling division)
            if (bytes == 0)                  return "0 B";
            if (bytes < 1024)                return $"{bytes} B";
            if (bytes < 1024 * 1024)         return $"{(bytes + 1023) / 1024:N0} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{(bytes + 1024 * 1024 - 1) / (1024 * 1024):N0} MB";
            return $"{bytes / (1024L * 1024 * 1024):N1} GB";
        }

        private static string GetShellFileType(string path)
        {
            try
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                // Use SHGetFileInfo via Shell to get Windows Explorer type string
                var shfi = new SHFILEINFO();
                uint flags = SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES;
                SHGetFileInfo(path, 0x80, ref shfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(shfi), flags);
                string type = shfi.szTypeName?.Trim() ?? "";
                if (!string.IsNullOrEmpty(type)) return type;
                // Fallback map for common extensions
                return ext switch
                {
                    ".exe"  => "Application",
                    ".msi"  => "Windows Installer Package",
                    ".dll"  => "Application Extension",
                    ".lnk"  => "Shortcut",
                    ".url"  => "Internet Shortcut",
                    ".txt"  => "Text Document",
                    ".pdf"  => "PDF Document",
                    ".zip"  => "Compressed (zipped) Folder",
                    ".ini"  => "Configuration Settings",
                    ".xml"  => "XML Document",
                    ".json" => "JSON File",
                    ".log"  => "Text Document",
                    ".bat"  => "Windows Batch File",
                    ".ps1"  => "Windows PowerShell Script",
                    ".reg"  => "Registration Entries",
                    ".dat"  => "DAT File",
                    _       => string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " File"
                };
            }
            catch { return ""; }
        }

        // P/Invoke for SHGetFileInfo
        private const uint SHGFI_TYPENAME         = 0x000000400;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint SHGFI_ICON             = 0x000000100;
        private const uint SHGFI_SMALLICON        = 0x000000001;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int    iIcon;
            public uint   dwAttributes;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Lightweight data-bound tree node for the Registry (and other large trees). The TreeView
    /// binds to a tree of these via HierarchicalDataTemplate + UI virtualization, so WPF only
    /// realizes visual containers for visible rows. Checking/unchecking mutates the model (and the
    /// linked SnapItems) without walking the visual tree — this is what keeps large hives fast.
    /// </summary>
    public class RegTreeNode : INotifyPropertyChanged
    {
        public string Name     { get; set; } = "";
        public string Icon     { get; set; } = "🗝";
        public string FullPath { get; set; } = "";        // upper-cased key path for prefix ops
        public string OriginalPath { get; set; } = "";     // real-case path, for pre-filling Add dialogs
        public bool   IsLeaf   { get; set; }              // a registry value leaf (vs a key folder)
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
        }

        public RegTreeNode? Parent { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<RegTreeNode> Children { get; }
            = new();

        // SnapItems whose data this node directly represents (leaf values; key nodes usually none).
        public System.Collections.Generic.List<SnapItem> Items { get; } = new();

        // Owner page set once at build time so model changes can push include/exclude into _allItems
        // by prefix and refresh the summary, without per-node visual work.
        public ValidateSourcePage? Owner { get; set; }

        private bool _suppress;   // re-entrancy guard while propagating up/down
        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                OnPropertyChanged();
                if (_suppress) return;

                _suppress = true;
                try
                {
                    // Propagate DOWN to children (model only — bindings update visible rows).
                    foreach (var c in Children) c.SetCheckedCascade(value);
                    // Propagate UP: parent becomes checked if any child checked, unchecked if none.
                    Parent?.RecalcFromChildren();
                    // Push the include/exclude into the underlying SnapItems by path prefix (ONCE),
                    // then refresh the summary. This is O(items), not O(nodes×items).
                    Owner?.ApplyRegNodeCheck(this, value);
                }
                finally { _suppress = false; }
            }
        }

        // Sets checked state through the subtree without re-triggering the heavy owner callback per
        // node (the owner applies the change once by prefix at the toggled node).
        public void SetCheckedCascade(bool value)
        {
            if (_isChecked != value) { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
            foreach (var c in Children) c.SetCheckedCascade(value);
        }

        // Recomputes this node's checked state from its children (used when a child toggles).
        //
        // Propagates CHECKING upward only. Clearing the last checked child must NOT clear the
        // ancestors: a folder / registry key is a captured item in its own right and the user may
        // still want it created even with nothing selected inside it. Unchecking one leaf used to
        // blank the whole branch above it up to the root - and misleadingly so, because this method
        // writes _isChecked directly rather than through the setter, so ApplyRegNodeCheck never ran
        // and the underlying SnapItems stayed included. The checkboxes said one thing and the data
        // said another.
        //
        // Checking a child still pulls its ancestors in, so the path to an included item is always
        // complete. To drop a folder, untick the folder itself - which cascades down as before.
        public void RecalcFromChildren()
        {
            if (Children.Count == 0) return;
            bool any = false;
            foreach (var c in Children) if (c.IsChecked) { any = true; break; }
            if (any && !_isChecked) { _isChecked = true; OnPropertyChanged(nameof(IsChecked)); }
            Parent?.RecalcFromChildren();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public partial class ValidateSourcePage : Page
    {
        // ── State ─────────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private string   _currentTab    = "All";
        private string   _searchText    = "";
        private string   _summaryFilter = ""; // when set, All tab shows only this category
        // Cached app info (name/vendor/version) keyed by source path, so UpdateSummary doesn't
        // re-read the registry/MSI on every checkbox toggle.
        private (string Name, string Vendor, string Version) _appInfoCache = ("", "", "");
        private string _appInfoCacheKey = "\0"; // sentinel so first call always reads
        // The package's app info shown in the Summary and used to name the MSI/qasnap. Set from the
        // FIRST installed app, and overridden when the user edits it in the Create MSI dialog. Once
        // set, it sticks across merged captures (so installing app2 doesn't change the package name).
        private (string Name, string Vendor, string Version)? _packageAppInfo = null;
        private bool     _capturing   = false;
        private Process? _captureProcess;
        private EtwQaCapture? _capture;
        // Snapshot of DriverStore\FileRepository package folders taken at capture START. A driver
        // package appears here as a folder named "<inf>_<arch>_<hash>". EtwQaCapture has no driver
        // stream, so the Drivers tab is fed by diffing this baseline against the live folder list -
        // any folder not in the baseline is a package the source itself installed.
        private HashSet<string>? _driverBaseline;
        // DSNs present before the source ran, as "scope|name". Diffed during the run so only the
        // data sources the source itself registered are shown. Same technique as _driverBaseline.
        private HashSet<string>? _odbcBaseline;
        // Full task paths present before the source ran, e.g. "\\DropboxSystem\\DropboxUpdater\\Task".
        private HashSet<string>? _taskBaseline;

        // Processes. PIDs alive at capture start, plus every PID seen since, so a process is only
        // ever inspected once. Cumulative on purpose: an installer's children are gone long before
        // the capture ends, so rows are never removed once recorded.
        private readonly HashSet<int> _knownPids = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SnapItem> _seenProcs = new();
        private System.Threading.CancellationTokenSource? _procPollCts;

        // Add/Remove Programs entries present before the source ran, keyed "scope|registry key name",
        // plus when that snapshot was taken - an entry that already existed still counts if the
        // source rewrote it during the run.
        private HashSet<string>? _appBaseline;
        private DateTime _appBaselineUtc = DateTime.MinValue;
        // Driver package folders whose payload files have already been listed into the Files tree,
        // so large packages are not re-enumerated every live tick (a final forced pass on Stop
        // catches any files PnP was still copying when first seen).
        private readonly HashSet<string> _driverPayloadDone = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string DriverStoreRepo =
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\DriverStore\FileRepository");
        // "scope|name" -> value for Machine + User environment variables, captured BEFORE the source
        // runs. Env-var writes land in the registry (Session Manager\Environment / HKCU\Environment),
        // which the capture does not monitor, and there is no env stream - so new/changed variables
        // are found by diffing this baseline, the same way drivers are handled.
        private Dictionary<string, string>? _envBaseline;
        // "rulename|direction" of firewall rules present BEFORE the source runs. Rules are added via
        // New-NetFirewallRule / netsh (registry + WFP store) which the capture does not monitor and
        // there is no firewall stream, so new rules are found by diffing this baseline (COM) and by
        // parsing the script. Null until snapshotted at capture start.
        private HashSet<string>? _firewallBaseline;
        // Rule names whose FirewallRules registry value has already been surfaced, so the (large)
        // FirewallRules key is not re-scanned every tick once a rule's value has been found.
        private readonly HashSet<string> _firewallRegDone = new(StringComparer.OrdinalIgnoreCase);
        // Service names whose HKLM\...\Services\<name> values have already been surfaced as registry
        // rows, so each service key is read only once.
        private readonly HashSet<string> _serviceRegDone = new(StringComparer.OrdinalIgnoreCase);
        // The capture's diag log path (%TEMP%\ValidateSource_<exeName>.log). The action log is
        // written to this same file so only ONE ValidateSource_*.log is produced per execute.
        private string _lastDiagPath = "";

        // Search navigation
        // Each entry = one occurrence: (row item, 0-based occurrence index within that row)
        private List<(SnapItem Item, int OccIdx, string Field)> _searchMatches = new();
        // Tree tab occurrences: (TextBlock, 0-based occurrence index within that label)
        private List<(TextBlock Tb, int OccIdx)> _treeMatchTbs = new();
        // Map SnapItem → its path TextBlock for word-level highlight (All tab ListView)
        private readonly Dictionary<SnapItem, TextBlock> _pathCellTbs  = new();
        private readonly Dictionary<SnapItem, TextBlock> _valueCellTbs = new();
        private readonly Dictionary<SnapItem, TextBlock> _dataCellTbs  = new();
        private readonly Dictionary<SnapItem, TextBlock> _typeCellTbs  = new();
        private readonly Dictionary<SnapItem, TextBlock> _dateCellTbs  = new();
        private readonly Dictionary<SnapItem, TextBlock> _sizeCellTbs  = new();
        private readonly Dictionary<SnapItem, TextBlock> _itemsCellTbs = new();
        // Map label TextBlock → full label string for tree tab highlight
        private readonly Dictionary<TextBlock, string> _treeLabelTbs = new();
        // Map label TextBlock → its owning TreeViewItem (for scroll-into-view on Next/Prev)
        private readonly Dictionary<TextBlock, TreeViewItem> _treeLabelNodes = new();
        private static readonly Brush _hlOrange = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
        private static readonly Brush _hlYellow = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x76));
        private int            _searchMatchIdx = -1;

        // Currently selected tree node (for Include/Exclude footer buttons — tree tabs)
        private TreeViewItem? _selectedTreeNode = null;

        // Currently selected DATA-BOUND tree node (Files/Registry use RegTreeNode + ItemsSource).
        private RegTreeNode? _selectedRegNode = null;

        // Currently selected list item (for Include/Exclude footer buttons — flat-list tabs)
        private SnapItem? _selectedListItem = null;

        // Guard to prevent re-entrant cascade when SetIncludedCascade sets cb.IsChecked
        private bool _cascading = false;

        // All captured items
        private readonly ConcurrentDictionary<string, SnapItem> _allItems =
            new(StringComparer.OrdinalIgnoreCase);

        private ExclusionConfig _exclusions = new();

        // ── Temp tab (VIEW ONLY) ────────────────────────────────────────────────────────────
        // %TEMP% items are excluded by ValidateSource.json (FolderPaths contains
        // "\AppData\Local\Temp\"), and IsExcluded is applied at INGEST - excluded items are
        // discarded outright, not flagged. They are captured here instead so the packager can see
        // what the installer dropped in temp.
        //
        // This dictionary is deliberately SEPARATE from _allItems. Everything downstream - the
        // summary counts, the other tabs, the .svr writer, CreateMsi - reads _allItems, so items
        // parked here cannot be included, saved or packaged. That is what makes the tab view-only
        // by construction rather than by a disabled checkbox.
        private readonly Dictionary<string, SnapItem> _tempItems =
            new(StringComparer.OrdinalIgnoreCase);

        // ── Event Monitor tab (VIEW ONLY) ───────────────────────────────────────────────────
        // Windows Application-log entries written while the source was running. Like the Temp tab,
        // this is NOT capture data - EtwQaCapture does not read the event log - so it is produced
        // by the same baseline-and-diff technique the driver and temp detection already use.
        //
        // Application log only, as asked. Nothing here is a SnapItem, so it cannot be included,
        // saved to .svr or packaged. _eventRecordIds dedupes across the repeated live ticks.
        private readonly List<EventLogRow> _eventItems     = new();
        private readonly HashSet<long>     _eventRecordIds = new();
        private DateTime                   _eventBaselineUtc = DateTime.MinValue;

        private System.Windows.Threading.DispatcherTimer? _refreshTimer;

        // Action log
        private readonly StringBuilder _actionLog = new();
        private string _lastLogPath  = "";   // path of last written .log  — for Open Log button
        private string _lastSnapPath = "";   // path of last written .svr — for Open Path button
        private bool   _captureRunning = false;   // true while a capture/Execute is in progress
        private string _lastCapturedSource = "";  // last source path that was captured this session
        private DateTime _captureStartTime = DateTime.MinValue; // set when capture begins
        private System.Windows.Threading.DispatcherTimer? _autoSaveTimer; // debounce auto-save on include/exclude changes
        private bool _svFirstLoad = true; // apply SourcePath from config only on first load

        // Sequential post-install callbacks — set by SetSourcePathAndExecute, fired in StopCaptureAsync
        private string?          _postInstallFilePath      = null;
        private bool             _postInstallDoCreatePkg   = false;
        private bool             _postInstallDoUninstall   = false;
        private string?          _postInstallUninstallCmd  = null;
        private string?          _postInstallAppName       = null; // used to find UninstallString in registry
        private string?          _postInstallAppVendor     = null; // used to name the qasnap file
        private string?          _postInstallAppVersion    = null; // used to name the qasnap file
        private Action<string>?  _onCreatePackageCallback  = null;
        private Action<string>?  _onUninstallCallback      = null;

        // Primary location: user-editable Templates folder.
        // Fallback: Resources folder next to the executable (for dev/portable use).
        private static readonly string SourceValidationConfigPath =
            File.Exists(@"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidateSource.json")
                ? @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidateSource.json"
                : System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                    "Resources", "ValidateSource.json");

        private SourceValidationConfig _svCfg = new();

        public ValidateSourcePage()
        {
            InitializeComponent();
            // Up/Down step the Summary rows, Left/Right step the category tabs; the page
            // must be focusable for its PreviewKeyDown to see the keys at all.
            Focusable = true;
            PreviewKeyDown += OnValidateKeyNav;
            LoadExclusions();
            LoadSvConfig();
            // Recalculate Path column width when the ListView is resized
            Loaded += (_, _) =>
            {
                UpdateAllTabColumns();
                LvAll.SizeChanged += (_, _) => UpdateAllTabColumns();
                foreach (var lv in new[] { LvEnv, LvCerts, LvServices, LvFirewall, LvDrivers, LvOdbc, LvTasks, LvProcess, LvApps, LvTemp, LvEvents })
                    lv.SizeChanged += (_, _) => FitTabColumns();
                // Buttons start disabled — require an item selection before acting
                UpdateIncludeExcludeButtons();
                // Attach caret/drag-select handler to all TextBoxes — mirrors AutoPackage
                this.PreviewMouseLeftButtonDown -= Page_PreviewMouseLeftButtonDown;
                this.PreviewMouseLeftButtonDown += Page_PreviewMouseLeftButtonDown;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => AttachCaretHandlerToAllTextBoxes(this)));
            };
        }

        // ── Caret / drag-select — copied from AutoPackage/CreatePackage ─────────────
        // Page-level: deselects focused TextBox when user clicks anywhere else on the page.
        private void Page_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (System.Windows.Input.Keyboard.FocusedElement is not TextBox focused) return;
            if (focused.SelectionLength == 0) return;
            var hit = e.OriginalSource as DependencyObject;
            bool clickedSameBox = false;
            var el = hit;
            while (el != null)
            {
                if (ReferenceEquals(el, focused)) { clickedSameBox = true; break; }
                el = System.Windows.Media.VisualTreeHelper.GetParent(el);
            }
            if (!clickedSameBox) focused.SelectionLength = 0;
        }

        // Walks the visual tree and attaches PathBox_PreviewMouseLeftButtonDown to every TextBox.
        private void AttachCaretHandlerToAllTextBoxes(DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox tb)
                {
                    tb.PreviewMouseLeftButtonDown -= PathBox_PreviewMouseLeftButtonDown;
                    tb.PreviewMouseLeftButtonDown += PathBox_PreviewMouseLeftButtonDown;
                }
                else
                    AttachCaretHandlerToAllTextBoxes(child);
            }
        }

        // Fixes caret placement AND drag-selection when clicking in the empty area of a TextBox.
        // Click in empty space before text → caret at beginning; drag right selects forward.
        // Click in empty space after text  → caret at end; drag left selects backward.
        private void PathBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not TextBox tb) return;

            var clickPoint = e.GetPosition(tb);
            int exactIndex = tb.GetCharacterIndexFromPoint(clickPoint, snapToText: false);

            // Click landed directly on a character — clear selection if any, then let WPF handle
            if (exactIndex != -1)
            {
                if (tb.SelectionLength > 0) tb.Select(exactIndex, 0);
                return;
            }

            // Click landed in empty area beyond the text
            if (string.IsNullOrEmpty(tb.Text)) { if (!tb.IsFocused) tb.Focus(); return; }

            var firstRect = tb.GetRectFromCharacterIndex(0);
            var lastRect  = tb.GetRectFromCharacterIndex(tb.Text.Length);
            bool goToStart = Math.Abs(clickPoint.X - firstRect.Left) < Math.Abs(clickPoint.X - lastRect.Right);

            e.Handled = true;
            if (!tb.IsFocused) tb.Focus();
            int anchor = goToStart ? 0 : tb.Text.Length;
            tb.Select(anchor, 0);
            if (anchor == 0) tb.ScrollToHome(); else tb.ScrollToEnd();
            tb.CaptureMouse();

            System.Windows.Input.MouseEventHandler?       moveHandler = null;
            System.Windows.Input.MouseButtonEventHandler? upHandler   = null;

            moveHandler = (s, me) =>
            {
                if (s is not TextBox t) return;
                var pos       = me.GetPosition(t);
                int dragIndex = t.GetCharacterIndexFromPoint(pos, snapToText: true);
                var lr = t.GetRectFromCharacterIndex(t.Text.Length);
                var fr = t.GetRectFromCharacterIndex(0);
                if (pos.X >= lr.Right) dragIndex = t.Text.Length;
                if (pos.X <= fr.Left)  dragIndex = 0;
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

        // ── Exclusion list ────────────────────────────────────────
        // Exclusions are now stored in ValidateSource.json (consolidated — the separate
        // ValidateSourceExclusionList.json is no longer used). The same JSON is deserialized
        // into ExclusionConfig to pick up the FilePaths/FileExtensions/RegistryKeys/etc. fields.
        private void LoadExclusions()
        {
            try
            {
                if (File.Exists(SourceValidationConfigPath))
                    _exclusions = JsonSerializer.Deserialize<ExclusionConfig>(
                        File.ReadAllText(SourceValidationConfigPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new ExclusionConfig();
            }
            catch { _exclusions = new ExclusionConfig(); }
        }

        private void LoadSvConfig()
        {
            try
            {
                if (!File.Exists(SourceValidationConfigPath)) return;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cfg  = JsonSerializer.Deserialize<SourceValidationConfig>(
                               File.ReadAllText(SourceValidationConfigPath), opts);
                if (cfg == null) return;
                _svCfg = cfg;

                // Apply SourcePath only on first load — preserves user-changed path on navigation
                if (_svFirstLoad)
                {
                    _svFirstLoad = false;
                    ApplySourcePathFromConfig();
                }
            }
            catch { }
        }

        /// <summary>
        /// Resolves SourcePath from ValidateSource.json (file or folder) and
        /// pre-fills TxtSourceFile. Called on first load only; Refresh restores it.
        /// If a folder is given, picks the first .exe or .msi alphabetically.
        /// </summary>
        private void ApplySourcePathFromConfig()
        {
            if (string.IsNullOrWhiteSpace(_svCfg.SourcePath)) return;
            string sp = Environment.ExpandEnvironmentVariables(_svCfg.SourcePath.Trim());
            string? fileToLoad = null;

            // Direct installer file
            if (File.Exists(sp))
            {
                string ext = IOPath.GetExtension(sp).ToLowerInvariant();
                if (ext == ".exe" || ext == ".msi")
                    fileToLoad = sp;
            }
            // Folder — pick first .exe or .msi alphabetically
            else if (IOPath.GetExtension(sp) == "" && System.IO.Directory.Exists(sp))
            {
                var installers = System.IO.Directory
                    .GetFiles(sp, "*.*", System.IO.SearchOption.TopDirectoryOnly)
                    .Where(f => { var e = IOPath.GetExtension(f).ToLowerInvariant(); return e == ".exe" || e == ".msi"; })
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (installers.Length > 0) fileToLoad = installers[0];
            }

            if (fileToLoad != null && TxtSourceFile != null)
                TxtSourceFile.Text = fileToLoad;
        }

        private bool IsExcluded(SnapItem item)
        {
            // Empty/whitespace patterns are skipped everywhere: an empty string matches
            // every path (IndexOf("") >= 0 is always true) and would wrongly exclude an
            // entire category. A blank entry in the JSON must never nuke all items.
            if (item.Category is "File" or "Folder")
            {
                foreach (var p in _exclusions.FilePaths ?? new())
                    if (!string.IsNullOrWhiteSpace(p) &&
                        item.DisplayPath.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                var ext = IOPath.GetExtension(item.DisplayPath).ToLowerInvariant();
                if (_exclusions.FileExtensions?.Contains(ext) == true) return true;
                foreach (var fp in _exclusions.FolderPaths ?? new())
                    if (!string.IsNullOrWhiteSpace(fp) &&
                        item.DisplayPath.IndexOf(fp, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }
            if (item.Category == "Registry")
                foreach (var rk in _exclusions.RegistryKeys ?? new())
                    if (!string.IsNullOrWhiteSpace(rk) &&
                        item.DisplayPath.StartsWith(rk, StringComparison.OrdinalIgnoreCase))
                        return true;
            if (item.Category == "Shortcut")
                foreach (var sn in _exclusions.ShortcutNames ?? new())
                    if (!string.IsNullOrWhiteSpace(sn) &&
                        item.DisplayPath.IndexOf(sn, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            if (item.Category == "Service")
                foreach (var svc in _exclusions.Services ?? new())
                    if (!string.IsNullOrWhiteSpace(svc) &&
                        item.DisplayPath.IndexOf(svc, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            return false;
        }

        // ── Browse ────────────────────────────────────────────────
        private void BtnBrowseSource_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Source File or .svr",
                Filter = "Installers & Snapshots|*.exe;*.msi;*.msp;*.bat;*.cmd;*.ps1;*.lnk;*.cer;*.crt;*.inf;*.reg;*.ttf;*.otf;*.svr|QA Snapshot|*.svr|All Installers|*.exe;*.msi;*.msp;*.bat;*.cmd;*.ps1;*.lnk;*.cer;*.crt;*.inf;*.reg;*.ttf;*.otf|All files|*.*",
            };

            // Shortcut locations, added to the dialog's sidebar.
            //
            // The Desktop shown by Explorer is a merge of two folders: the per-user desktop and
            // C:\Users\Public\Desktop. The dialog's own "Desktop" entry navigates to the per-user
            // one ONLY, so every shortcut written by a machine-wide installer is invisible there
            // even though it is plainly on screen. The Start Menu splits the same way. Since
            // picking a .lnk is now a real workflow on this page, all four are one click away.
            foreach (var place in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), // C:\Users\Public\Desktop
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),       // this user's Desktop
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),         // All Users Start Menu
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),               // this user's Start Menu
            })
            {
                // A custom place whose folder is missing makes the dialog throw as it opens, so
                // each one is checked and any failure is skipped rather than blocking Browse.
                try
                {
                    if (!string.IsNullOrWhiteSpace(place) && Directory.Exists(place))
                        dlg.CustomPlaces.Add(new FileDialogCustomPlace(place));
                }
                catch { }
            }

            if (dlg.ShowDialog() != true) return;

            // If the user picked a .svr, load it directly for re-editing/rebuilding
            // instead of treating it as an installer to capture.
            if (dlg.FileName.EndsWith(".svr", StringComparison.OrdinalIgnoreCase))
            {
                LoadQasnapForEdit(dlg.FileName);
                return;
            }

            TxtSourceFile.Text = dlg.FileName;
        }

        /// <summary>
        /// Loads a previously exported .svr back into the page so the user can
        /// uncheck unwanted items and rebuild the MSI. Clears current items first.
        /// </summary>
        private void LoadQasnapForEdit(string qasnapPath)
        {
            try
            {
                _allItems.Clear();
                _tempItems.Clear();        // temp items belong to a live capture, not to an imported .svr
                _eventItems.Clear(); _eventRecordIds.Clear(); _eventBaselineUtc = DateTime.MinValue;
                _packageAppInfo  = null;   // re-derive app info for the imported snapshot
                _appInfoCacheKey = "\0";
                LoadSnapItems(qasnapPath);
                _lastSnapPath     = qasnapPath;
                TxtSourceFile.Text = qasnapPath;
                _qasnapDirty       = false;

                RefreshCurrentTab();
                UpdateSummary();
                SetFooterStatus($"Loaded SVR: {IOPath.GetFileName(qasnapPath)} - {_allItems.Count} items. Uncheck items, then Create MSI.");
            }
            catch (Exception ex)
            {
                VsStyledMessage(null, "Source Validation", $"Could not load snapshot:\n{ex.Message}", "⛔");
            }
        }

        /// <summary>Page-wide drag-over: accept installer files dropped anywhere on the page.</summary>
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

        /// <summary>Page-wide drop: load the dropped file into the Source File textbox.</summary>
        private void Page_Drop(object s, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (files[0].EndsWith(".svr", StringComparison.OrdinalIgnoreCase))
                    LoadQasnapForEdit(files[0]);
                else
                    TxtSourceFile.Text = files[0];
            }
        }

        // ── Execute ───────────────────────────────────────────────

        /// <summary>
        /// Splits a raw source-path string that may contain inline arguments
        /// (e.g. "C:\Foo\setup.exe /VERYSILENT" or
        ///        "\"C:\My Path\setup.exe\" /VERYSILENT /SUPPRESSMSGBOXES")
        /// into the actual file path and any trailing arguments.
        /// Handles both quoted paths and unquoted paths where arguments follow
        /// the installer extension.
        /// </summary>
        private static (string FilePath, string InlineArgs) SplitPathAndArgs(string raw)
        {
            raw = raw.Trim();

            // Quoted path: "C:\Some Path\file.exe" /args
            if (raw.StartsWith("\""))
            {
                int endQuote = raw.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    string fp   = raw.Substring(1, endQuote - 1).Trim();
                    string args = raw.Substring(endQuote + 1).Trim();
                    return (fp, args);
                }
            }

            // Unquoted: split at the first space after a known installer extension
            string[] knownExts = { ".exe", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".lnk", ".cer", ".crt", ".inf", ".reg", ".ttf", ".otf" };
            foreach (var ext in knownExts)
            {
                int extIdx = raw.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (extIdx < 0) continue;
                int afterExt = extIdx + ext.Length;
                if (afterExt == raw.Length)
                    return (raw, "");
                if (afterExt < raw.Length && raw[afterExt] == ' ')
                    return (raw.Substring(0, afterExt).Trim(), raw.Substring(afterExt).Trim());
            }

            return (raw, "");
        }

        // Installs a font (.ttf/.otf) the same way double-clicking it does: copies it to the
        // machine Fonts folder, registers it under HKLM Fonts, and notifies running apps. Also
        // adds the font file + its registry entry to the GUI (no capture, so no stray qasnap).
        private void InstallFontFile(string fontPath)
        {
            try
            {
                string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts); // C:\Windows\Fonts
                string fileName = IOPath.GetFileName(fontPath);
                string destPath = IOPath.Combine(fontsDir, fileName);

                // 1) Copy the font file into the Fonts folder (all users).
                try
                {
                    File.Copy(fontPath, destPath, overwrite: true);
                }
                catch (UnauthorizedAccessException)
                {
                    SvShowMessage("Source Validation - Install Font",
                        $"Access denied copying the font to:\n{destPath}\n\nRun the app as administrator to install fonts for all users.");
                    return;
                }

                // 2) Friendly registry value name: "<FaceName> (TrueType)" / "(OpenType)".
                string ext = IOPath.GetExtension(fontPath).ToLowerInvariant();
                string typeSuffix = ext == ".otf" ? " (OpenType)" : " (TrueType)";
                string faceName = TryGetFontFaceName(fontPath);
                if (string.IsNullOrWhiteSpace(faceName))
                    faceName = IOPath.GetFileNameWithoutExtension(fontPath);
                string regValueName = faceName + typeSuffix;

                // 3) Register the font so the system can use it after a restart, and load it now.
                const string fontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
                bool regOk; string regErr = "";
                if (_runSystemContext)
                {
                    // Write the Fonts registry entry as SYSTEM via PsExec (reg add). The font file copy
                    // above already targets the machine-wide Fonts folder, identical for SYSTEM/admin.
                    var psi = BuildSystemContextPsi("reg.exe",
                        $"add \"HKLM\\{fontsKey}\" /v \"{regValueName}\" /t REG_SZ /d \"{fileName}\" /f", fontsDir);
                    if (psi == null) return;   // PsExec missing (message already shown)
                    try { var pr = Process.Start(psi); pr?.WaitForExit(15000); regOk = pr?.ExitCode == 0; }
                    catch (Exception rex) { regOk = false; regErr = rex.Message; }
                }
                else
                {
                    regOk = WriteLiveRegistry("HKEY_LOCAL_MACHINE", fontsKey, regValueName, fileName, "REG_SZ", out regErr);
                }

                // 4) Make the font available immediately (no restart) and notify other windows.
                try
                {
                    AddFontResource(destPath);
                    const int HWND_BROADCAST = 0xffff;
                    const int WM_FONTCHANGE  = 0x001D;
                    SendMessage((IntPtr)HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero);
                }
                catch { /* non-fatal: font still registered for next logon */ }

                // 5) Add to the GUI: the font FILE and its registry entry.
                AddManualItem(new SnapItem
                {
                    Category     = "File",
                    DisplayPath  = destPath,
                    SourceOnDisk = destPath,
                });
                AddManualItem(new SnapItem
                {
                    Category    = "Registry",
                    DisplayPath = @"HKEY_LOCAL_MACHINE\" + fontsKey,
                    Value       = regValueName,
                    Data        = fileName,
                    DataType    = "REG_SZ",
                });

                RefreshCurrentTab();
                UpdateSummary();

                string msg = $"Installed font '{regValueName}'.";
                if (!regOk) msg += $"\nNote: registry entry could not be written ({regErr}). Run as administrator.";
                SvShowMessage("Source Validation - Install Font", msg, "ℹ️");
                SetFooterStatus(msg);
            }
            catch (Exception ex)
            {
                SvShowMessage("Source Validation - Install Font", $"Could not install the font:\n{ex.Message}");
            }
        }

        // Reads the font's family/face name from its name table (best-effort). Falls back to "".
        private static string TryGetFontFaceName(string fontPath)
        {
            try
            {
                var uri = new Uri(fontPath);
                var gtf = new System.Windows.Media.GlyphTypeface(uri);
                // Prefer the Win32 family name (matches how the Fonts registry names entries);
                // else fall back to the first available family name.
                foreach (var kv in gtf.Win32FamilyNames)
                    if (!string.IsNullOrWhiteSpace(kv.Value)) return kv.Value;
                foreach (var kv in gtf.FamilyNames)
                    if (!string.IsNullOrWhiteSpace(kv.Value)) return kv.Value;
            }
            catch { }
            return "";
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int AddFontResource(string lpFileName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        // Wrapper so callers read a friendlier name.
        private static uint WtsGetActiveConsoleSessionId()
        {
            try { return WTSGetActiveConsoleSessionId(); } catch { return 0xFFFFFFFF; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        // True when the run-context dropdown is set to "System Context".
        private bool UseSystemContext =>
            (CmbRunContext?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()
                ?.StartsWith("System", StringComparison.OrdinalIgnoreCase) == true;

        // Locates psexec.exe — Templates folder first (canonical), then app dir, install root, PATH.
        // Mirrors the Execute Package page's resolution so both features behave the same.
        private static string? FindPsExec()
        {
            string exeDir     = AppDomain.CurrentDomain.BaseDirectory;
            string installDir = IOPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Smekal", "SwiftPack Studio");
            string templatesDir = IOPath.Combine(installDir, "Templates");

            var candidates = new[]
            {
                IOPath.Combine(templatesDir, "psexec.exe"),
                IOPath.Combine(templatesDir, "PsExec.exe"),
                IOPath.Combine(exeDir,       "psexec.exe"),
                IOPath.Combine(exeDir,       "PsExec.exe"),
                IOPath.Combine(installDir,   "psexec.exe"),
                IOPath.Combine(installDir,   "PsExec.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv != null)
                foreach (var dir in pathEnv.Split(';'))
                {
                    try
                    {
                        string full = IOPath.Combine(dir.Trim(), "psexec.exe");
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            return null;
        }

        // Builds a ProcessStartInfo that runs <program> <args> as SYSTEM via PsExec -s.
        // Returns null (and shows a message) when PsExec can't be found. workingDir optional.
        private ProcessStartInfo? BuildSystemContextPsi(string program, string args, string workingDir)
        {
            string? psExec = FindPsExec();
            if (psExec == null)
            {
                SvShowMessage("Source Validation - System Context",
                    "psexec.exe was not found.\n\nPlace PsExec.exe in:\n" +
                    @"C:\Program Files\Smekal\SwiftPack Studio\Templates\" +
                    "\n\nthen try again.");
                return null;
            }
            // PsExec: -accepteula (no EULA prompt), -s (SYSTEM), -nobanner, -i (interactive: show
            // the process UI so the user can interact), -w (working dir).
            //
            // -i targets the session SWIFTPACK ITSELF is running in, which is by definition the
            // desktop the user is looking at. It previously used WTSGetActiveConsoleSessionId,
            // which names the PHYSICAL CONSOLE session - over RDP that is a different desktop,
            // so the installer ran as SYSTEM with its wizard faithfully shown on a screen nobody
            // could see: an MSI sat waiting forever for a Next click, and setups that go
            // unattended when unwatched installed silently. The console id remains only as the
            // fallback for the degenerate case where the own-session id cannot be read.
            uint sessionId;
            try { sessionId = (uint)Process.GetCurrentProcess().SessionId; }
            catch { sessionId = WtsGetActiveConsoleSessionId(); }
            string interactive = sessionId != 0xFFFFFFFF ? $"-i {sessionId} " : "-i ";
            string wdSwitch = string.IsNullOrEmpty(workingDir) ? "" : $"-w \"{workingDir}\" ";
            string psArgs   = $"-accepteula -s -nobanner {interactive}{wdSwitch}\"{program}\" {args}".TrimEnd();
            return new ProcessStartInfo(psExec, psArgs)
            {
                UseShellExecute  = true,
                Verb             = "runas",     // PsExec needs elevation to install its service
                WindowStyle      = ProcessWindowStyle.Hidden,   // hides PsExec's own console, not the target UI
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? "" : workingDir,
            };
        }

        // GUI (no system capture, so the result is exactly the file's contents - no noise).
        private void ImportRegFile(string regPath)
        {
            try
            {
                _sysSkippedCount = 0;
                string[] rawLines = File.ReadAllLines(regPath);

                // Join backslash-continued lines (long hex values span multiple lines in .reg files).
                // Only the TRAILING continuation backslash is removed; backslashes inside the data
                // (e.g. paths in string values) are preserved.
                var merged = new List<string>();
                System.Text.StringBuilder? cont = null;
                foreach (var rl in rawLines)
                {
                    string l = rl;
                    bool continues = l.TrimEnd().EndsWith("\\");
                    if (cont != null)
                    {
                        // Append this (continued) line's content without its trailing backslash.
                        cont.Append(continues ? l.TrimEnd().TrimEnd('\\').Trim() : l.Trim());
                        if (!continues) { merged.Add(cont.ToString()); cont = null; }
                        continue;
                    }
                    if (continues)
                        cont = new System.Text.StringBuilder(l.TrimEnd().TrimEnd('\\'));
                    else
                        merged.Add(l);
                }
                if (cont != null) merged.Add(cont.ToString());
                string[] lines = merged.ToArray();

                string currentKey = "";       // full key incl. hive, e.g. HKEY_CURRENT_USER\Software\ExampleVendor
                int added = 0, failed = 0;
                string? firstError = null;

                string Unescape(string s) => s.Replace("\\\\", "\\").Replace("\\\"", "\"");

                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("REGEDIT4", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith(";")) continue;   // comment

                    // Key line: [HKEY_...\Sub\Key]  (a leading "-" means delete; we skip deletes)
                    if (line.StartsWith("["))
                    {
                        currentKey = line.Trim('[', ']').Trim();
                        if (currentKey.StartsWith("-")) currentKey = ""; // delete-key directive: ignore
                        continue;
                    }

                    if (string.IsNullOrEmpty(currentKey)) continue;

                    // Value line:  "Name"=data   or   @=data  (@ = default value)
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string namePart = line.Substring(0, eq).Trim();
                    string dataPart = line.Substring(eq + 1).Trim();

                    string valueName = namePart == "@" ? "" : Unescape(namePart.Trim('"'));

                    // Determine type + data. Supported: string ("..."), dword:, hex:, hex(b): (qword),
                    // hex(2): (expand), hex(7): (multi). Falls back to REG_SZ.
                    string regType = "REG_SZ";
                    string dataValue;
                    if (dataPart.StartsWith("\""))
                    {
                        regType = "REG_SZ";
                        dataValue = Unescape(dataPart.Trim('"'));
                    }
                    else if (dataPart.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                    {
                        regType = "REG_DWORD";
                        string hex = dataPart.Substring(6).Trim();
                        dataValue = long.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv.ToString() : "0";
                    }
                    else if (dataPart.StartsWith("hex(b):", StringComparison.OrdinalIgnoreCase))
                    {
                        regType = "REG_QWORD";
                        var bytes = ParseRegHexBytes(dataPart.Substring(dataPart.IndexOf(':') + 1));
                        dataValue = (bytes.Length >= 8 ? BitConverter.ToInt64(bytes, 0) : 0).ToString();
                    }
                    else if (dataPart.StartsWith("hex(2):", StringComparison.OrdinalIgnoreCase))
                    {
                        regType = "REG_EXPAND_SZ";
                        var bytes = ParseRegHexBytes(dataPart.Substring(dataPart.IndexOf(':') + 1));
                        dataValue = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                    }
                    else if (dataPart.StartsWith("hex(7):", StringComparison.OrdinalIgnoreCase))
                    {
                        regType = "REG_MULTI_SZ";
                        var bytes = ParseRegHexBytes(dataPart.Substring(dataPart.IndexOf(':') + 1));
                        dataValue = System.Text.Encoding.Unicode.GetString(bytes)
                            .Replace('\0', '\n').TrimEnd('\n');
                    }
                    else if (dataPart.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
                    {
                        regType = "REG_BINARY";
                        var bytes = ParseRegHexBytes(dataPart.Substring(4));
                        dataValue = BitConverter.ToString(bytes).Replace("-", " ");
                    }
                    else
                    {
                        dataValue = dataPart;   // unknown: store raw
                    }

                    // Split hive from key path for the live write.
                    string hive = currentKey;
                    string subKey = "";
                    int slash = currentKey.IndexOf('\\');
                    if (slash > 0) { hive = currentKey.Substring(0, slash); subKey = currentKey.Substring(slash + 1); }
                    // Normalise common hive abbreviations.
                    hive = hive.ToUpperInvariant() switch
                    {
                        "HKLM" => "HKEY_LOCAL_MACHINE",
                        "HKCU" => "HKEY_CURRENT_USER",
                        "HKCR" => "HKEY_CLASSES_ROOT",
                        "HKU"  => "HKEY_USERS",
                        "HKCC" => "HKEY_CURRENT_CONFIG",
                        _      => hive.ToUpperInvariant(),
                    };

                    // The actual registry write is performed by regedit after the loop (with its
                    // standard confirmation prompt), so here we only count the parsed values and
                    // add them to the GUI/qasnap.
                    added++;

                    // Add to the GUI (and qasnap) as a manual registry item.
                    AddManualItem(new SnapItem
                    {
                        Category    = "Registry",
                        DisplayPath = hive + (string.IsNullOrEmpty(subKey) ? "" : "\\" + subKey),
                        Value       = valueName,
                        Data        = dataValue,
                        DataType    = regType,
                    });
                }

                // Apply the .reg through regedit WITHOUT /s, so Windows shows its standard
                // "Adding information can ... Are you sure you want to continue?" confirmation -
                // exactly like double-clicking the .reg file - in both Administrator and SYSTEM context.
                try
                {
                    if (_runSystemContext)
                    {
                        var psi = BuildSystemContextPsi("regedit.exe", $"\"{regPath}\"", IOPath.GetDirectoryName(regPath) ?? "");
                        if (psi == null) return;   // PsExec missing (message already shown)
                        Process.Start(psi)?.WaitForExit(120000);
                    }
                    else
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("regedit.exe", $"\"{regPath}\"")
                        {
                            UseShellExecute = true
                        };
                        Process.Start(psi)?.WaitForExit(120000);
                    }
                }
                catch (Exception pex)
                {
                    SvShowMessage("Source Validation - Registry Import",
                        $"Could not apply the .reg:\n{pex.Message}");
                }

                // Lock the source app info to the file name (no app to detect for a .reg).
                SwitchToTab("Registry");
                RefreshCurrentTab();
                UpdateSummary();

                string msg = $"Imported {added} value(s) from {IOPath.GetFileName(regPath)}.";
                if (failed > 0) msg += $"\n{failed} value(s) could not be written (run as administrator for HKLM).\nFirst error: {firstError}";
                if (_runSystemContext && _sysSkippedCount > 0)
                    msg += $"\n\n{_sysSkippedCount} user-context item(s) shown at their SYSTEM location (HKCU -> HKEY_USERS\\.DEFAULT). See the log for details.";
                SvShowMessage("Source Validation - Registry Import", msg, "ℹ️");
                SetFooterStatus(msg);
            }
            catch (Exception ex)
            {
                SvShowMessage("Source Validation - Registry Import", $"Could not import the .reg file:\n{ex.Message}");
            }
        }

        // Parses a .reg hex byte list (e.g. "00,1a,ff" possibly with line continuations) to bytes.
        private static byte[] ParseRegHexBytes(string s)
        {
            var hexParts = s.Replace("\\", "").Replace(" ", "")
                            .Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new List<byte>();
            foreach (var h in hexParts)
                if (byte.TryParse(h, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var b)) bytes.Add(b);
            return bytes.ToArray();
        }

        // Enable/disable the controls that must not change mid-capture: Browse, the
        // run-context dropdown, Import, Add, New and Settings. Toggled around an Execute run.
        private void SetExecControlsEnabled(bool on)
        {
            if (BtnBrowseSource != null) BtnBrowseSource.IsEnabled = on;
            if (CmbRunContext   != null) CmbRunContext.IsEnabled   = on;
            if (BtnImport       != null) BtnImport.IsEnabled       = on;
            if (BtnAdd          != null) BtnAdd.IsEnabled          = on;
            if (BtnClearAll     != null) BtnClearAll.IsEnabled     = on;
            if (BtnSettings     != null) BtnSettings.IsEnabled     = on;
        }

        private async void BtnExecute_Click(object s, RoutedEventArgs e)
        {
            string rawInput = TxtSourceFile.Text.Trim();

            // Capture the run-context selection now (UI thread) for use by background launch,
            // .reg import, and font install.
            _runSystemContext = UseSystemContext;

            // Split file path from any inline install arguments the user (or automation) typed
            var (srcPath, inlineArgs) = SplitPathAndArgs(rawInput);

            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
            {
                SvShowMessage("Source Validation", "Please select a valid source file.");
                return;
            }

            // System Context runs everything through PsExec (-s). If PsExec isn't
            // available, stop here with a clear message instead of silently falling
            // back to a normal-context run and still producing a .svr.
            if (_runSystemContext && FindPsExec() == null)
            {
                SvShowMessage("Source Validation - System Context",
                    "psexec.exe was not found.\n\nPlace PsExec.exe in:\n" +
                    @"C:\Program Files\Smekal\SwiftPack Studio\Templates\" +
                    "\n\nthen try again.");
                return;
            }

            // If a different source was already captured in this session, ask whether to merge
            // the new source into the same file. Cancel stops here (nothing is installed);
            // Continue proceeds with the capture/installation.
            if (!string.IsNullOrEmpty(_lastCapturedSource)
                && !string.Equals(_lastCapturedSource, srcPath, StringComparison.OrdinalIgnoreCase))
            {
                bool go = SvConfirm("Source Validation - Already captured",
                    "A source has already been captured in this session. Click Continue to execute and capture this source into the same file, or Cancel to stop. To capture it separately, click Cancel, then New.");
                if (!go) return;   // Cancel -> do not trigger execution
            }
            _lastCapturedSource = srcPath;   // remember this source for the next Execute

            // .reg files are handled directly (not via system capture): the capture only monitors
            // the Uninstall hive, so arbitrary keys (e.g. HKCU\Software\...) would be missed. Parse
            // the file, write its keys to the live registry, and add them straight to the GUI.
            if (srcPath.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            {
                ImportRegFile(srcPath);
                return;
            }

            // Fonts (.ttf/.otf) install directly (copy to Fonts + register), not via capture: a
            // font install touches C:\Windows\Fonts and HKLM\...\Fonts, neither of which the capture
            // monitors. Installing directly mirrors double-clicking the font, with no stray qasnap.
            if (srcPath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                || srcPath.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            {
                InstallFontFile(srcPath);
                return;
            }

            // Certificates (.cer/.crt) are read from the file and imported directly (not via the
            // system capture): the cert IS the source, and the store import is done out-of-process
            // by crypto services, so a capture/diff is unreliable (and misses an already-installed
            // cert). Mirrors the .reg / font direct-apply handling above.
            if (srcPath.EndsWith(".cer", StringComparison.OrdinalIgnoreCase)
                || srcPath.EndsWith(".crt", StringComparison.OrdinalIgnoreCase))
            {
                ImportCertFile(srcPath);
                return;
            }

            string exeName = "";
            string exeArgs = inlineArgs; // carry any inline args through
            if (srcPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                var detected = EtwQaCapture.DetectExeInstall(srcPath);
                if (detected.HasValue)
                {
                    exeName = detected.Value.FileName;
                    exeArgs = detected.Value.ExeArgs;
                }
                else
                    exeName = IOPath.GetFileName(srcPath);
            }
            else
            {
                exeName = IOPath.GetFileName(srcPath);
                // exeArgs already set from inlineArgs above
            }

            // Always reset to "All" tab when Execute starts
            _currentTab = "All";
            foreach (Button tb in new[] { TabAll, TabFiles, TabShortcuts, TabRegistry,
                                          TabEnv, TabCerts, TabServices, TabFirewall, TabDrivers, TabOdbc, TabTasks, TabProcess, TabApps })
                tb.Style = (Style)FindResource("TabBtnStyle");
            TabAll.Style = (Style)FindResource("TabBtnActiveStyle");
            RefreshCurrentTab();

            // MERGE MODE: do NOT clear existing items. Each Execute adds the new source's captured
            // delta to whatever is already there (previously-installed apps + manually-added items),
            // so you can install abc.exe then xyz.exe and capture both together. Use the Clear
            // button to start a brand-new capture. Items are keyed by Category|DisplayPath|Value,
            // so an item touched by more than one installer is deduplicated, not double-counted.
            _sysSkippedCount = 0;   // reset System-context skip tally for this run
            _captureRunning = true;
            if (BtnCreateQasnap != null) BtnCreateQasnap.IsEnabled = false;
            if (BtnCreateMsi   != null) BtnCreateMsi.IsEnabled    = false;
            if (BtnSaveQasnap  != null) BtnSaveQasnap.IsEnabled   = false;
            SetExecControlsEnabled(false);   // Browse / run-context / Import / Add / New
            _actionLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Capture started (merge into existing): {srcPath}");
            BtnExecute.Visibility  = Visibility.Collapsed;
            BtnStop.Visibility     = Visibility.Visible;
            SummaryPanel.Children.Clear();
            SetFooterStatus("Starting capture...");
            PrgExecuteProgress.Visibility = Visibility.Visible;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _captureStartTime = DateTime.Now;
            // Derive the app keyword the same way the capture does (filename -> first word ->
            // strip trailing version digits), used to drop OTHER apps' Uninstall-hive noise.
            _sourceKeyword = DeriveSourceKeyword(exeName);
            _capture = new EtwQaCapture(exeName, exeArgs,
                msg => Dispatcher.Invoke(() => SetFooterStatus(msg)));
            _capture.SetSourcePath(srcPath);   // store full path for MSI product code lookup

            // For scripts (.bat/.cmd), parse copy/robocopy/xcopy operations and register the exact
            // destination FILES the script would copy (computed from the files present in each copy
            // source). This makes an already-existing destination still show the right files, while
            // excluding stray pre-existing files in the destination that the script didn't copy.
            if (srcPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                || srcPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var destFiles = ParseScriptDestinationFiles(srcPath);
                    if (destFiles.Count > 0) _capture.AddExplicitScanFiles(destFiles);
                }
                catch { }
            }

            _capture.StartCapture();
            _capturing = true;
            // The source installer is about to run and the capture is watching the whole
            // machine, so Auto Package must not start a download or a chain underneath it.
            AppBusy.SetManual("Validate Source", true);

            // Record the driver packages present BEFORE the source runs, so the Drivers tab shows
            // only packages the source itself adds to DriverStore\FileRepository.
            SnapshotDriverBaseline();
            SnapshotOdbcBaseline();
            SnapshotTaskBaseline();
            SnapshotProcessBaseline();
            SnapshotAppBaseline();
            StartProcessPoller();
            SnapshotTempBaseline();
            SnapshotEventBaseline();

            // Record Machine + User environment variables now, so the Environment tab shows the ones
            // the source adds or changes (these are registry writes the capture does not monitor).
            SnapshotEnvBaseline();

            // Record existing firewall rules, so the Firewall tab shows rules the source adds.
            SnapshotFirewallBaseline();
            _serviceRegDone.Clear();

            // Live UI refresh every 800ms
            _refreshTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(800) };
            _refreshTimer.Tick += LiveRefreshTick;
            _refreshTimer.Start();

            try { await Task.Run(() => RunSourceFile(srcPath, exeArgs, ct), ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                VsStyledMessage(null, "Source Validation", $"Error running source file:\n{ex.Message}", "⛔");
            }
            finally { StopCapture(); }
        }

        /// <summary>
        /// Parses a .bat/.cmd for file-copy operations and returns the exact destination FILE paths
        /// the script would produce, computed from the files that actually exist in each copy SOURCE.
        /// This is what the script targets — so an already-existing destination still shows the right
        /// files, while stray pre-existing files in the destination (not from the source) are NOT
        /// included. Handles: robocopy SRC DEST [/E ...], xcopy SRC DEST, copy/move SRC DEST.
        /// Environment variables (e.g. %APPDATA%) are expanded for the context the script runs in.
        /// </summary>
        private List<string> ParseScriptDestinationFiles(string scriptPath)
        {
            var destFiles = new List<string>();
            string[] lines;
            try { lines = File.ReadAllLines(scriptPath); }
            catch { return destFiles; }

            // Tokenise a command line into args, honouring double-quoted segments.
            static List<string> Tokenize(string s)
            {
                var toks = new List<string>();
                var sb = new System.Text.StringBuilder();
                bool inQ = false;
                foreach (char c in s)
                {
                    if (c == '"') { inQ = !inQ; continue; }
                    if (!inQ && char.IsWhiteSpace(c))
                    {
                        if (sb.Length > 0) { toks.Add(sb.ToString()); sb.Clear(); }
                    }
                    else sb.Append(c);
                }
                if (sb.Length > 0) toks.Add(sb.ToString());
                return toks;
            }

            string Expand(string token) => _runSystemContext
                ? ExpandForSystemContext(token)
                : Environment.ExpandEnvironmentVariables(token);

            void AddFile(string path)
            {
                string p = path.TrimEnd('\\');
                if (!string.IsNullOrWhiteSpace(p)
                    && !destFiles.Contains(p, StringComparer.OrdinalIgnoreCase))
                    destFiles.Add(p);
            }

            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("::")
                    || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (line.StartsWith("@")) line = line.Substring(1).TrimStart();

                var toks = Tokenize(line);
                if (toks.Count < 2) continue;
                string cmd = toks[0].ToLowerInvariant();
                var positional = toks.Skip(1).Where(t => !t.StartsWith("/")).ToList();

                try
                {
                    if (cmd == "robocopy" || cmd == "xcopy")
                    {
                        // <command> <source-dir> <dest-dir> [files...] [/switches]
                        if (positional.Count < 2) continue;
                        string srcDir  = Expand(positional[0]).TrimEnd('\\');
                        string destDir = Expand(positional[1]).TrimEnd('\\');
                        bool recursive = toks.Any(t =>
                            t.Equals("/E", StringComparison.OrdinalIgnoreCase) ||
                            t.Equals("/S", StringComparison.OrdinalIgnoreCase));

                        if (!Directory.Exists(srcDir)) continue;

                        // Map each file in the SOURCE to its path under the DESTINATION. Only these
                        // are what the script copies — so stray files already in the destination are
                        // excluded.
                        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        foreach (var sf in Directory.EnumerateFiles(srcDir, "*", opt))
                        {
                            string rel = sf.Substring(srcDir.Length).TrimStart('\\');
                            AddFile(IOPath.Combine(destDir, rel));
                        }
                    }
                    else if (cmd == "copy" || cmd == "move")
                    {
                        // <command> <source> <destination>
                        if (positional.Count < 2) continue;
                        string src  = Expand(positional[0]);
                        string dest = Expand(positional[positional.Count - 1]);

                        // Destination may be a directory (then keep the source filename) or a file.
                        if (Directory.Exists(dest) || dest.EndsWith("\\"))
                        {
                            string fileName = IOPath.GetFileName(src);
                            if (!string.IsNullOrEmpty(fileName))
                                AddFile(IOPath.Combine(dest.TrimEnd('\\'), fileName));
                        }
                        else
                        {
                            AddFile(dest);
                        }
                    }
                }
                catch { }
            }
            return destFiles;
        }

        /// <summary>
        /// Expands environment variables for SYSTEM context: %APPDATA%/%LOCALAPPDATA%/%USERPROFILE%
        /// resolve to the SYSTEM profile (config\systemprofile), matching where a SYSTEM-run script
        /// actually writes. Other variables fall back to normal expansion.
        /// </summary>
        private static string ExpandForSystemContext(string token)
        {
            string sysProfile = Environment.ExpandEnvironmentVariables(
                @"%SystemRoot%\System32\config\systemprofile");
            string s = token;
            s = System.Text.RegularExpressions.Regex.Replace(s, "%APPDATA%",
                IOPath.Combine(sysProfile, @"AppData\Roaming"),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, "%LOCALAPPDATA%",
                IOPath.Combine(sysProfile, @"AppData\Local"),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, "%USERPROFILE%",
                sysProfile, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return Environment.ExpandEnvironmentVariables(s);
        }

        private void RunSourceFile(string srcPath, string exeArgs, CancellationToken ct)
        {
            try
            {
                string dir = IOPath.GetDirectoryName(srcPath) ?? "";
                string ext2 = IOPath.GetExtension(srcPath).ToLowerInvariant();
                bool sysCtx = _runSystemContext;   // captured at Execute time (UI thread)

                // Resolve the program + arguments for this source type.
                string program, args;
                bool shellLaunchLnk = false;   // set for advertised shortcuts, see the .lnk branch
                if (ext2 == ".ps1")
                {
                    program = "powershell.exe";
                    args    = $"-ExecutionPolicy Bypass -NoProfile -File \"{srcPath}\"";
                }
                else if (ext2 == ".msi" || ext2 == ".msp")
                {
                    program = "msiexec.exe";
                    // System context is now interactive (PsExec -i), so keep the normal MSI UI.
                    // Verbose log to %TEMP%\{msi name}_Install.log, the same convention the Auto
                    // Package silent runner uses, so a validation run leaves the same evidence as a
                    // packaged one and Validate Package can parse it. Safe under PsExec: args are
                    // appended to the PsExec command line verbatim, and /i already passes a quoted
                    // path the same way. Expanding %TEMP% here also means a SYSTEM-context run
                    // still writes where the signed-in user can find it, rather than into the
                    // SYSTEM profile.
                    args = $"/i \"{srcPath}\" {MsiLog.With(exeArgs, srcPath, "Install")}".TrimEnd();
                }
                else if (ext2 == ".bat" || ext2 == ".cmd")
                {
                    program = "cmd.exe";
                    args    = $"/c \"{srcPath}\" {exeArgs}".TrimEnd();
                }
                else if (ext2 == ".inf")
                {
                    // A driver INF (has a [Manufacturer] section => PnP driver package) installs with
                    // pnputil /add-driver "<inf>" /install - the correct method for driver packages.
                    // Legacy [DefaultInstall]-only config INFs still use the setupapi right-click path.
                    if (InfIsDriverPackage(srcPath))
                    {
                        program = "pnputil.exe";
                        args    = $"/add-driver \"{srcPath}\" /install";
                    }
                    else
                    {
                        program = "rundll32.exe";
                        args    = $"setupapi.dll,InstallHinfSection DefaultInstall 128 \"{srcPath}\"";
                    }
                }
                else if (ext2 == ".cer" || ext2 == ".crt")
                {
                    program = "certutil.exe";
                    args    = $"-addstore Root \"{srcPath}\"";
                }
                else if (ext2 == ".lnk")
                {
                    // Two kinds of shortcut, and they need opposite handling.
                    //
                    // A plain shortcut stores a real target path, so it is resolved here and the
                    // target is run directly. That matters for System Context: PsExec needs an
                    // executable and cannot resolve a .lnk itself.
                    //
                    // An ADVERTISED (Windows Installer / Darwin) shortcut stores no path at all -
                    // it holds a product/feature/component descriptor that Windows Installer
                    // resolves when clicked. WScript.Shell cannot express that, so TargetPath comes
                    // back as the icon, e.g. C:\Windows\Installer\{ProductCode}\app.ico. Trying to
                    // start that gives "No application is associated with the specified file".
                    // Only the shell can resolve these, so the .lnk is handed over untouched.
                    var (lnkTarget, lnkArgs, lnkWorkDir, _) = ResolveLnk(srcPath);
                    string tExt = string.IsNullOrWhiteSpace(lnkTarget)
                        ? "" : IOPath.GetExtension(lnkTarget).ToLowerInvariant();
                    bool targetRunnable = !string.IsNullOrWhiteSpace(lnkTarget)
                        && File.Exists(lnkTarget)
                        && (tExt == ".exe" || tExt == ".com" || tExt == ".bat"
                            || tExt == ".cmd" || tExt == ".msi" || tExt == ".msp");

                    if (!string.IsNullOrWhiteSpace(lnkWorkDir) && Directory.Exists(lnkWorkDir))
                        dir = lnkWorkDir;

                    if (targetRunnable)
                    {
                        program = lnkTarget;
                        // The shortcut's own arguments come first; anything typed in the arguments
                        // box is appended, so it can add to the shortcut rather than replace it.
                        args = (lnkArgs + " " + (exeArgs ?? "")).Trim();
                    }
                    else
                    {
                        shellLaunchLnk = true;
                        program = srcPath;
                        args    = exeArgs ?? "";
                        SvLogRunFailure(srcPath,
                            $"advertised shortcut (target reported as \"{lnkTarget}\") - launching via the shell",
                            show: false);
                    }
                }
                else
                {
                    program = srcPath;
                    args    = string.IsNullOrEmpty(exeArgs) ? "" : exeArgs;
                }

                ProcessStartInfo? psi;
                if (sysCtx && shellLaunchLnk)
                {
                    // PsExec launches executables, not shell shortcuts, and an advertised shortcut
                    // has no executable to hand it. Say so rather than failing obscurely.
                    SvLogRunFailure(srcPath,
                        "This is an advertised (Windows Installer) shortcut, which only the shell can resolve. "
                        + "System Context runs through PsExec and cannot start it. Run it as Administrator instead, "
                        + "or pick the application's executable directly.", show: true);
                    return;
                }
                if (sysCtx)
                {
                    // SYSTEM context via PsExec -s (non-interactive). Returns null if PsExec missing.
                    psi = BuildSystemContextPsi(program, args, dir);
                    if (psi == null) return;
                }
                else if (shellLaunchLnk)
                {
                    // Double-click semantics: let the shell resolve the shortcut. No Verb is set -
                    // forcing "runas" here is what fails, because the shell has to follow the link
                    // before any verb can apply to the real target.
                    psi = new ProcessStartInfo(srcPath)
                        { UseShellExecute = true, WorkingDirectory = dir };
                    if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
                }
                else if (program == srcPath)
                {
                    psi = new ProcessStartInfo(srcPath, args)
                        { UseShellExecute = true, Verb = "runas", WorkingDirectory = dir };
                }
                else
                {
                    psi = new ProcessStartInfo(program, args)
                        { UseShellExecute = true, Verb = "runas", WorkingDirectory = dir };
                }

                _captureProcess = Process.Start(psi);
                if (_captureProcess == null && shellLaunchLnk)
                {
                    // ShellExecute brokered the launch (Windows Installer resolves an advertised
                    // shortcut out of process), so there is no handle to wait on. The app still
                    // starts; the capture simply cannot tell when it finishes.
                    SvLogRunFailure(srcPath,
                        "Started via the shell, which returned no process to wait for. Use Stop when the app has finished.",
                        show: false);
                }
                while (_captureProcess != null && !_captureProcess.WaitForExit(300))
                    if (ct.IsCancellationRequested)
                    { try { _captureProcess?.Kill(true); } catch { } break; }
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the user dismissed the UAC prompt. Expected, not a fault, so it
                // is logged but not raised as a dialog.
                SvLogRunFailure(srcPath, "Elevation was declined at the UAC prompt.", show: false);
            }
            catch (Exception ex)
            {
                // Was a bare "catch { }". Swallowing here produces the worst possible symptom: the
                // source never runs, the capture ends instantly with nothing in it, and there is no
                // reason given anywhere. Report it instead.
                SvLogRunFailure(srcPath, $"{ex.GetType().Name}: {ex.Message}", show: true);
            }
        }

        /// <summary>
        /// Records why a source failed to launch, and optionally shows it. Called from the capture
        /// worker thread, so the dialog is marshalled to the UI thread.
        /// </summary>
        private void SvLogRunFailure(string srcPath, string reason, bool show)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try { _actionLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Execute failed: {srcPath} - {reason}"); } catch { }
                    SetFooterStatus("Execute failed: " + reason);
                    if (show)
                        SvShowMessage("Source Validation - Execute failed",
                            $"The source could not be started.\n\n{srcPath}\n\n{reason}");
                });
            }
            catch { }
        }

        /// <summary>
        /// True when an INF is a PnP driver package - it has a [Manufacturer] section, which means it
        /// must be installed with pnputil /add-driver (not the legacy setupapi DefaultInstall path).
        /// A [DefaultInstall]-only INF returns false; an unreadable INF defaults to driver (pnputil).
        /// </summary>
        private static bool InfIsDriverPackage(string infPath)
        {
            try
            {
                string text = File.ReadAllText(infPath);
                if (text.IndexOf("[Manufacturer]", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                // No [Manufacturer]: only a legacy [DefaultInstall] INF should use setupapi.
                return text.IndexOf("[DefaultInstall]", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch { return true; }
        }

        // ── Stop ──────────────────────────────────────────────────
        private void BtnStop_Click(object s, RoutedEventArgs e)
        {
            _cts?.Cancel();
            try { _captureProcess?.Kill(true); } catch { }
            _ = StopCaptureAsync();
        }

        private async Task StopCaptureAsync()
        {
            _capturing = false;
            // Every end of a capture funnels through here (finish, stop, failure), so this
            // is the one place that has to give Auto Package back.
            AppBusy.SetManual("Validate Source", false);
            _refreshTimer?.Stop();

            // Update buttons and show progress window immediately on UI thread
            BtnStop.Visibility    = Visibility.Collapsed;
            BtnExecute.Visibility = Visibility.Visible;
            PrgExecuteProgress.Visibility = Visibility.Collapsed;
            var progressWin = ShowProcessingWindow();

            // Run all heavy work on a background thread so the spinner animates
            var captureToStop = _capture;
            _capture = null;
            await Task.Run(() =>
            {
                if (captureToStop != null)
                {
                    try { captureToStop.StopCapture(); } catch { }
                    _lastSnapPath = captureToStop.OutputPath ?? "";
                    _lastDiagPath = captureToStop.DiagPath ?? "";   // single log file (exe-based name)
                    Dispatcher.Invoke(() => LoadSnapItems(captureToStop.OutputPath));

                    // Append post-exclusion counts to the diag log so it matches the UI summary.
                    // The snapshot log line written by EtwQaCapture reflects raw pre-exclusion counts;
                    // this line shows what is actually visible in the UI after ValidateSourceExclusionList filtering.
                    try
                    {
                        var counts = new System.Text.StringBuilder();
                        counts.Append($"[{DateTime.Now:HH:mm:ss}] After exclusions (UI summary): ");
                        var groups = _allItems.Values
                            .GroupBy(i => i.Category)
                            .OrderBy(g => g.Key)
                            .Select(g => $"{g.Key.ToLower()}s={g.Count()}");
                        counts.AppendLine(string.Join(", ", groups));
                        File.AppendAllText(captureToStop.DiagPath, counts.ToString(),
                            new System.Text.UTF8Encoding(false));
                    }
                    catch { }
                }
            });

            // Back on UI thread — close progress and show results
            try { progressWin.Close(); } catch { }

            _captureRunning = false;   // capture finished: let UpdateSummary re-enable the buttons
            SetExecControlsEnabled(true);
            // Final driver pass: catch packages that settled into FileRepository after the last
            // live tick. LoadSnapItems (above) only adds, never clears, so live-added drivers and
            // this final pass both survive.
            MergeNewDrivers();
            MergeNewOdbc();               // final pass: DSNs registered after the last tick
            MergeNewTasks();              // final pass: tasks registered after the last tick
            StopProcessPoller();          // capture is over: stop watching for new processes
            MergeNewProcesses();          // final pass: publish anything the last sweep caught
            MergeNewApps();               // final pass: entries registered after the last tick
            RefreshDriverPayload(true);   // forced re-scan: catch files still copying when first seen
            MergeNewEnvVars();            // final pass: catch env vars set after the last tick
            MergeScriptEnvVars();         // and env vars the script sets (idempotent re-sets)
            MergeScriptFirewall();        // firewall rules the script adds (idempotent re-adds)
            MergeNewFirewall();           // and firewall rules any source added (COM diff)
            MergeServiceRegistry();       // service key registry values for captured services
            RefreshCurrentTab();
            UpdateSummary();

            int total = _allItems.Count;

            // If no application was detected (App Name empty in the Summary — e.g. a plain
            // script like test.bat that only copies a file and registers nothing in Add/Remove
            // Programs), do NOT keep a .svr on disk. A qasnap represents a packageable app;
            // with no app there is nothing to package. Delete the file written by the capture and
            // clear the path so the Capture Complete dialog shows Path "-" and no Open Path button.
            string resolvedAppName = (_packageAppInfo?.Name ?? _appInfoCache.Name) ?? "";
            bool noAppDetected = string.IsNullOrWhiteSpace(resolvedAppName);
            if (noAppDetected)
            {
                try
                {
                    if (!string.IsNullOrEmpty(_lastSnapPath) && File.Exists(_lastSnapPath))
                    {
                        File.Delete(_lastSnapPath);
                        LogAction($"No app detected — qasnap not created (deleted {IOPath.GetFileName(_lastSnapPath)}).");
                    }
                }
                catch (Exception delEx)
                {
                    LogAction($"No app detected — could not delete qasnap: {delEx.Message}");
                }
                _lastSnapPath = "";
                // Refresh so Save/Export disable (they require a qasnap on disk).
                UpdateSummary();
            }

            SetFooterStatus($"Capture complete \u2014 {total} items captured");

            _actionLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Capture complete. Total items: {total}");
            foreach (var grp in _allItems.Values.GroupBy(i => i.Category).OrderBy(g => g.Key))
                _actionLog.AppendLine($"  {grp.Key}: {grp.Count()}");
            WriteActionLog();

            ShowCaptureCompleteDialog(total);

            // No application name means no .svr was written automatically. Tell the user
            // they can still produce one manually with the Create SVR button.
            if (noAppDetected)
                SvShowMessage("Source Validation",
                    "No application name was detected for this source, so an .svr file was not created automatically.\n\n" +
                    "Use the \"Create SVR\" button to generate the .svr file.", "ℹ️");

            // ── Sequential post-install flow ───────────────────────────────
            // Fire AFTER ShowDialog returns (dialog is closed) so the UI thread is
            // fully free before we navigate away and trigger AutoPackage.
            // Use BeginInvoke (async) so the current call stack unwinds first.
            if (_postInstallDoCreatePkg && _onCreatePackageCallback != null && !string.IsNullOrWhiteSpace(_postInstallFilePath))
            {
                var filePath        = _postInstallFilePath!;
                var uninstallCmd    = _postInstallUninstallCmd ?? "";
                var doUninstall     = _postInstallDoUninstall;
                var uninstallCb     = _onUninstallCallback;
                var createCb        = _onCreatePackageCallback;
                var appName         = _postInstallAppName ?? "";

                _postInstallDoCreatePkg  = false;
                _postInstallDoUninstall  = false;
                _onCreatePackageCallback = null;
                _onUninstallCallback     = null;
                _postInstallFilePath     = null;
                _postInstallUninstallCmd = null;
                _postInstallAppName      = null;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    createCb(filePath);

                    if (doUninstall && uninstallCb != null)
                        uninstallCb(uninstallCmd + "|" + appName); // pack appName into callback arg
                }), System.Windows.Threading.DispatcherPriority.Normal);
            }
            else if (_postInstallDoUninstall && _onUninstallCallback != null)
            {
                var uninstallCmd = _postInstallUninstallCmd ?? "";
                var appName      = _postInstallAppName ?? "";
                var uninstallCb  = _onUninstallCallback;
                _postInstallDoUninstall  = false;
                _onUninstallCallback     = null;
                _postInstallUninstallCmd = null;
                _postInstallAppName      = null;

                Dispatcher.BeginInvoke(new Action(() => uninstallCb(uninstallCmd + "|" + appName)),
                    System.Windows.Threading.DispatcherPriority.Normal);
            }
            else
            {
                _postInstallDoCreatePkg  = false;
                _postInstallDoUninstall  = false;
                _onCreatePackageCallback = null;
                _onUninstallCallback     = null;
                _postInstallFilePath     = null;
                _postInstallUninstallCmd = null;
            }
        }

        private void StopCapture() => _ = StopCaptureAsync();

        private Window ShowProcessingWindow()
        {
            var win = new Window
            {
                Title = "Source Validation",
                Width = 320, Height = 140,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current.MainWindow,
                Topmost = true,
                ShowInTaskbar = false,
                Background = (Brush)(FindResource("WindowBgBrush") ?? Brushes.White),
            };

            var border = new Border
            {
                BorderBrush = (Brush)(FindResource("BorderMainBrush") ?? Brushes.LightGray),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24)
            };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            var spinEllipse = new System.Windows.Shapes.Ellipse
            {
                Width = 36, Height = 36,
                Stroke = (Brush)(FindResource("PrimaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2))),
                StrokeThickness = 4,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 6, 3 },
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
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
                Text = "Processing captured items…",
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)(FindResource("TextPrimaryBrush") ?? Brushes.Black),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var subTb = new TextBlock
            {
                Text = "This window will close automatically.",
                FontSize = 11,
                Foreground = (Brush)(FindResource("TextMutedBrush") ?? Brushes.Gray),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stack.Children.Add(spinEllipse);
            stack.Children.Add(msgTb);
            stack.Children.Add(subTb);
            border.Child = stack;
            win.Content = border;
            win.Show();
            return win;
        }

        // ── Action log ────────────────────────────────────────────
        /// <summary>
        /// Builds the standard base filename from app info:
        /// AppVendor_AppName_AppVersion  (omitting any blank parts)
        /// Sanitises characters that are invalid in file names.
        /// </summary>
        private static string BuildStandardBaseName(string vendor, string name, string version)
        {
            static string San(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                foreach (char c in IOPath.GetInvalidFileNameChars())
                    s = s.Replace(c, '_');
                s = s.Replace(" ", ""); // remove spaces
                return s.Trim().Trim('_');
            }
            var parts = new[] { San(vendor), San(name), San(version) }
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();
            return parts.Length > 0 ? string.Join("_", parts) : "ValidateSource";
        }

        private void WriteActionLog()
        {
            try
            {
                // Resolve app info. Captured registry first: the live-hive lookup cannot tell an
                // app from a sub-installer bundled inside the same source (see ResolveAppInfo), and
                // this call names the .svr and the .log.
                var (appName, appVendor, appVersion) = ResolveAppInfo(TxtSourceFile?.Text?.Trim() ?? "");

                // If the auto-package flow supplied explicit vendor/version (from the app JSON),
                // prefer those over the registry lookup — they are already correctly formatted.
                if (!string.IsNullOrWhiteSpace(_postInstallAppVendor))  appVendor  = _postInstallAppVendor;
                if (!string.IsNullOrWhiteSpace(_postInstallAppVersion))  appVersion = _postInstallAppVersion;
                if (!string.IsNullOrWhiteSpace(_postInstallAppName) && string.IsNullOrWhiteSpace(appName))
                    appName = _postInstallAppName ?? "";

                // In merge mode the package is named after the FIRST app (shown in the Summary), so
                // the .svr and log keep that name regardless of which installer ran last. If the
                // first app's info isn't locked in yet, lock it in now from this (first) capture.
                if (_packageAppInfo == null && !string.IsNullOrWhiteSpace(appName))
                    _packageAppInfo = (appName, appVendor, appVersion);
                if (_packageAppInfo != null)
                    (appName, appVendor, appVersion) = _packageAppInfo.Value;

                // Build standard base name: AppVendor_AppName_AppVersion
                string baseName = BuildStandardBaseName(appVendor, appName, appVersion);

                // ── Rename the .svr file on disk to the standard name ──────────────
                if (!string.IsNullOrEmpty(_lastSnapPath) && File.Exists(_lastSnapPath))
                {
                    string snapDir     = IOPath.GetDirectoryName(_lastSnapPath)!;
                    string newSnapName = baseName + ".svr";
                    string newSnapPath = IOPath.Combine(snapDir, newSnapName);
                    try
                    {
                        if (!string.Equals(_lastSnapPath, newSnapPath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(newSnapPath))
                            {
                                // Standard-named .svr already exists: overwrite, choose a new
                                // name, or cancel (cancel discards the just-captured file).
                                string? target = ResolveSvrTarget(newSnapPath);
                                if (target == null)
                                {
                                    try { File.Delete(_lastSnapPath); } catch { }
                                    _lastSnapPath = "";
                                }
                                else
                                {
                                    if (File.Exists(target)) File.Delete(target);
                                    File.Move(_lastSnapPath, target);
                                    _lastSnapPath = target;
                                }
                            }
                            else
                            {
                                File.Move(_lastSnapPath, newSnapPath);
                                _lastSnapPath = newSnapPath;
                            }
                        }
                    }
                    catch { /* keep original path if rename fails */ }
                }

                // ── Write log to the SINGLE capture diag file (exe-based name) ─────────
                // EtwQaCapture already created %TEMP%\ValidateSource_<exeName>.log during capture.
                // Append the action log to that same file so only ONE ValidateSource_*.log exists,
                // instead of creating a second file named after AppVendor_AppName_AppVersion.
                if (!string.IsNullOrEmpty(_lastDiagPath))
                {
                    _lastLogPath = _lastDiagPath;
                    File.AppendAllText(_lastLogPath,
                        Environment.NewLine + "──────── Action log ────────" + Environment.NewLine +
                        _actionLog.ToString(), new UTF8Encoding(false));
                }
                else
                {
                    // Fallback (no capture ran — e.g. imported items): use the standard name.
                    string logBaseName = "ValidateSource_" + baseName;
                    _lastLogPath = IOPath.Combine(IOPath.GetTempPath(), logBaseName + ".log");
                    File.WriteAllText(_lastLogPath, _actionLog.ToString(), new UTF8Encoding(false));
                }
                LogAction($"Action log saved: {_lastLogPath}");
            }
            catch { }
        }

        // Derives (Name, Vendor, Version) from the CAPTURED registry items — specifically an
        // "...\CurrentVersion\Uninstall\<App>" key's DisplayName / Publisher / DisplayVersion.
        // This is a deterministic fallback when the live-registry lookup (ReadUninstallInfo) returns
        // nothing, which happens intermittently for stripped EXEs or due to timing.
        private (string Name, string Vendor, string Version) AppInfoFromCapturedRegistry()
        {
            // Group captured registry values by their parent Uninstall key.
            // Key path looks like: HKEY_LOCAL_MACHINE\...\Uninstall\<AppKey>
            var byKey = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in _allItems.Values)
            {
                if (it.Category != "Registry") continue;
                int idx = it.DisplayPath.IndexOf(@"\Uninstall\", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                // Parent key = everything up to and including the <AppKey> segment.
                string after = it.DisplayPath.Substring(idx + @"\Uninstall\".Length);
                string appSeg = after.Split('\\')[0];
                if (string.IsNullOrEmpty(appSeg)) continue;
                string parentKey = it.DisplayPath.Substring(0, idx + @"\Uninstall\".Length) + appSeg;

                if (!byKey.TryGetValue(parentKey, out var vals))
                    byKey[parentKey] = vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(it.Value)) vals[it.Value] = it.Data ?? "";
            }

            // Prefer a key that actually has a DisplayName; among those, the one with the most
            // identifying values (DisplayName/Publisher/DisplayVersion present).
            //
            // Ties are broken by how SPECIFICALLY the DisplayName matches the source keyword. An
            // installer often registers a bundled sub-component alongside the app itself, and both
            // entries are complete, so completeness alone cannot separate them - an installer that bundles an
            // updater writes BOTH "FileCompare" and "FileCompare Update Helper". The helper's name contains the app's name
            // plus a suffix, so the entry whose name is MOSTLY the keyword is the real app:
            //     keyword "filecompare" -> "FileCompare"              11/11 = 1.00   <- wins
            //     keyword "filecompare" -> "FileCompare Update Helper" 11/25 = 0.44
            // With no keyword, or a single candidate, this contributes nothing and the old
            // behaviour stands.
            double Specificity(string displayName)
            {
                if (string.IsNullOrEmpty(_sourceKeyword) || string.IsNullOrWhiteSpace(displayName)) return 0;
                string dn = new string(displayName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                string kw = new string(_sourceKeyword.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                if (kw.Length == 0 || dn.Length == 0 || !dn.Contains(kw)) return 0;
                return (double)kw.Length / dn.Length;   // 1.0 = the name IS the keyword
            }

            (string Name, string Vendor, string Version) best = ("", "", "");
            int    bestScore = -1;
            double bestSpec  = -1;
            foreach (var kv in byKey)
            {
                var vals = kv.Value;
                if (!vals.TryGetValue("DisplayName", out var dn) || string.IsNullOrWhiteSpace(dn)) continue;
                vals.TryGetValue("Publisher",      out var pub);
                vals.TryGetValue("DisplayVersion", out var ver);
                int    score = (string.IsNullOrWhiteSpace(pub) ? 0 : 1) + (string.IsNullOrWhiteSpace(ver) ? 0 : 1);
                double spec  = Specificity(dn);
                if (score > bestScore || (score == bestScore && spec > bestSpec))
                {
                    bestScore = score;
                    bestSpec  = spec;
                    best = (dn.Trim(), (pub ?? "").Trim(), (ver ?? "").Trim());
                }
            }
            return best;
        }

        // ── Professional capture-complete dialog with Open Path + Open Log buttons ──
        private void ShowCaptureCompleteDialog(int total)
        {
            var win = SvBuildDialog("Source Validation - Capture Complete");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(win, "Source Validation - Capture Complete", "🔬");

            int r = 0;

            // Time row — matches AutoPackage ShowSuccessDialog format exactly
            var endTime   = DateTime.Now;
            var elapsed   = _captureStartTime != DateTime.MinValue ? endTime - _captureStartTime : TimeSpan.Zero;
            string elapsedStr = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                : elapsed.TotalSeconds >= 1
                    ? $"{(int)elapsed.TotalSeconds}s"
                    : $"{(int)elapsed.TotalMilliseconds}ms";
            string timeVal = _captureStartTime != DateTime.MinValue
                ? $"Started: {_captureStartTime:HH:mm:ss}  Finished: {endTime:HH:mm:ss}  Duration: {elapsedStr}"
                : $"Finished: {endTime:HH:mm:ss}";
            SvNdAddRow(cg, r++, "Time", timeVal, lblFg, boxBg, boxBdr, valFg, true);
            SvNdAddSpacer(cg, r++);

            // Path — where the .svr was written
            string snapPath = !string.IsNullOrEmpty(_lastSnapPath) ? _lastSnapPath : "-";
            SvNdAddRow(cg, r++, "Path", snapPath, lblFg, boxBg, boxBdr, valFg, false);
            SvNdAddSpacer(cg, r++);

            // Log — where the .txt log was written (%temp%)
            string logPath = !string.IsNullOrEmpty(_lastLogPath) ? _lastLogPath : "-";
            SvNdAddRow(cg, r++, "Log", logPath, lblFg, boxBg, boxBdr, valFg, false);
            SvNdAddSpacer(cg, r++, 4);

            // System-context note: current-user-profile activity dropped as noise (a SYSTEM source
            // can't write to the user's profile). Direct .reg/font items are shown at their SYSTEM
            // location instead. See the log for the full list either way.
            if (_runSystemContext && _sysSkippedCount > 0)
            {
                SvNdAddRow(cg, r++, "System",
                    $"{_sysSkippedCount} current-user item(s) handled for System context (user-profile noise dropped; HKCU/user writes shown at their SYSTEM location). See the log for the full list.",
                    lblFg, boxBg, boxBdr, valFg, false);
                SvNdAddSpacer(cg, r++, 4);
            }

            // Icon + buttons on the same row — mirrors CreatePackagePage pattern exactly
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = SvNdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bool hasSnap = !string.IsNullOrEmpty(_lastSnapPath) && File.Exists(_lastSnapPath);
            bool hasLog  = !string.IsNullOrEmpty(_lastLogPath)  && File.Exists(_lastLogPath);

            var buttonList = new System.Collections.Generic.List<(string label, Action onClick)>();
            if (hasSnap)
                buttonList.Add(("📂 Open Path", () =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe",
                        $"/select,\"{_lastSnapPath}\"") { UseShellExecute = true }); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ValidateSource] open path failed: " + ex.Message); }
                }));
            if (hasLog)
                buttonList.Add(("📄 Open Log", () =>
                {
                    try { Process.Start(new ProcessStartInfo(_lastLogPath!) { UseShellExecute = true }); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ValidateSource] open log failed: " + ex.Message); }
                }));
            buttonList.Add(("✕ Close", () => win.Close()));

            var btns = SvNdMakeButtons(sec, buttonList.ToArray());
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            // Auto-close if ShowMessageBox = No in per-app JSON; errors always show
            if (!SwiftPack.Views.SourceDownloadPage.GlobalShowMessageBox)
            {
                win.Loaded += (_, _) => win.Dispatcher.BeginInvoke(
                    new Action(() => win.Close()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            win.ShowDialog();
            // Do NOT reset GlobalShowMessageBox here — downstream Execute/AutoPackage dialogs still need it
        }

        private StackPanel MakeInfoRow(string label, string value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(new TextBlock
            {
                Text = label + ":", Width = 100, FontSize = 11,
                Foreground = (Brush)(FindResource("TextMutedBrush") ?? Brushes.Gray),
            });
            sp.Children.Add(new TextBlock
            {
                Text = value, FontSize = 11, FontWeight = FontWeights.Medium,
                Foreground = (Brush)(FindResource("TextPrimaryBrush") ?? Brushes.Black),
            });
            return sp;
        }

        private void LogAction(string msg)
        {
            _actionLog.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }

        // Temporary performance logging to diagnose checkbox-toggle latency.
        private void LogPerf(string msg)
        {
            try { File.AppendAllText(
                IOPath.Combine(IOPath.GetTempPath(), "swiftpack_perf.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}", new UTF8Encoding(false)); }
            catch { }
        }

        // ── Live refresh tick ───────────────────────────────────────────────────────
        private void LiveRefreshTick(object? s, EventArgs e)
        {
            if (!_capturing || _capture == null) return;

            // Read items directly from EtwQaCapture live collections
            bool added = false;
            void TryAdd(SnapItem item)
            {
                // Drop OTHER apps' Uninstall-hive entries (background ARP noise like MementoSection
                // or unrelated app updates). Keeps the source app's own Uninstall key. Applies in
                // all contexts; only active when a usable source keyword was derived (exe/msi).
                if (IsOtherAppUninstallNoise(item))
                {
                    LogAction($"Dropped (Uninstall noise, other app): {item.DisplayPath} [{item.Value}]");
                    return;
                }

                // System context noise filter: the capture watches the USER's folders, but a source
                // running as SYSTEM cannot write to the current user's profile. The live capture's
                // items therefore reflect background activity in the user's folders, not the source.
                // Drop current-user HKCU / C:\Users\<me> items as noise. (Machine-wide items - HKLM,
                // ProgramData, ProgramFiles - are kept, since SYSTEM can legitimately write there.)
                // The HKCU->.DEFAULT / systemprofile rewrite is applied to the direct-handled types
                // (.reg, fonts) in AddManualItem, where the item is known to be the source's own.
                if (_runSystemContext && IsCurrentUserProfileItem(item))
                {
                    LogAction($"Dropped (System context noise, current-user profile): {item.Category} {item.DisplayPath} [{item.Value}]");
                    _sysSkippedCount++;
                    return;
                }

                string key = item.Category + "|" + item.DisplayPath + "|" + item.Value;
                if (!_allItems.ContainsKey(key) && !IsExcluded(item))
                {
                    item.EnrichFromDisk();
                    _allItems[key] = item;
                    added = true;
                }
            }

            foreach (var f  in _capture.LiveFiles)
                if (!f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    TryAdd(new SnapItem { Category="File", DisplayPath=f });
            foreach (var f  in _capture.LiveFolders)   TryAdd(new SnapItem { Category="Folder",      DisplayPath=f });
            foreach (var sc in _capture.LiveShortcuts) TryAdd(new SnapItem { Category="Shortcut",    DisplayPath=sc });
            foreach (var r  in _capture.LiveRegistry)  TryAdd(new SnapItem { Category="Registry",    DisplayPath=r.Key, Value=r.Value, Data=r.Data, DataType=r.Type });
            foreach (var sv in _capture.LiveServices)  TryAdd(new SnapItem { Category="Service",     DisplayPath=sv.Name, Value=sv.DisplayName, Data=sv.StartType, DataType=sv.Binary, ServiceStatus=GetServiceStatus(sv.Name) });
            foreach (var c  in _capture.LiveCerts)     TryAdd(new SnapItem { Category="Certificate", DisplayPath=c });

            // Drivers are not part of EtwQaCapture's streams. Detect packages the source installed
            // into DriverStore\FileRepository by diffing against the start-of-capture baseline.
            if (MergeNewDrivers()) added = true;
            // ODBC data sources are registry entries, not an EtwQaCapture stream: same baseline diff.
            if (MergeNewOdbc()) added = true;
            // Scheduled tasks: same baseline-diff technique, TaskCache\Tree vs start of capture.
            if (MergeNewTasks()) added = true;
            // Processes come from the 200ms poller rather than a diff; this only publishes them.
            if (MergeNewProcesses()) added = true;
            // Add/Remove Programs entries: baseline diff of the three Uninstall keys.
            if (MergeNewApps()) added = true;
            // %TEMP% is not part of EtwQaCapture's streams either. Same technique: diff the user's
            // temp folder against a start-of-capture baseline. VIEW ONLY - lands in _tempItems.
            MergeTempItems();
            // Application-log entries the source wrote. VIEW ONLY - lands in _eventItems.
            MergeEventLog();
            // List each captured driver's payload files into the Files tree (unchecked, reference).
            if (RefreshDriverPayload(false)) added = true;
            // Environment variables the source added or changed (registry writes not in the capture).
            if (MergeNewEnvVars()) added = true;
            // Script sources: env vars the script sets (catches idempotent re-sets the diff misses).
            if (MergeScriptEnvVars()) added = true;
            // Script sources: firewall rules the script adds (cheap parse; COM diff runs on Stop).
            if (MergeScriptFirewall()) added = true;
            // Surface each captured service's registry key values on the Registry tab.
            if (MergeServiceRegistry()) added = true;

            Dispatcher.Invoke(() =>
            {
                SetFooterStatus($"Capturing… {_allItems.Count} items");
                if (added)
                {
                    // New items — trees need to rebuild on next visit
                    RefreshCurrentTab();
                    UpdateSummary();
                }
            });
        }

        // ── Driver detection (DriverStore\FileRepository diff) ─────
        // EtwQaCapture exposes file/registry/service/cert streams but no driver stream, so the
        // Drivers tab is populated here. Installing an INF-based driver drops a package folder named
        // "<originalinf>_<arch>_<hash>" under DriverStore\FileRepository (e.g.
        // "ftdibus.inf_amd64_9be5c6138131d249"). Snapshotting that folder set at capture start and
        // diffing it during the run isolates the packages the source itself installed. This is
        // locale-independent (no pnputil output parsing) and needs no change to EtwQaCapture.

        /// <summary>Records the driver packages present before the source runs.</summary>
        private void SnapshotDriverBaseline()
        {
            _driverBaseline = EnumDriverPackageFolders();
            _driverPayloadDone.Clear();
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  ODBC TAB
        // ══════════════════════════════════════════════════════════════════════════════════
        // ODBC data sources are registry entries, not a stream EtwQaCapture reports, so they are
        // found the same way drivers are: snapshot before the source runs, diff during it.
        //
        // Each DSN is registered in two places - a value under "ODBC Data Sources" naming its
        // driver, and a subkey of the same name holding its settings. The list key is the one worth
        // reading: it gives name and driver together, and it is what the ODBC Data Source
        // Administrator lists.
        //
        // Three scopes, matching the tabs in that tool:
        //   User DSN               HKCU\SOFTWARE\ODBC\ODBC.INI
        //   System DSN (64-bit)    HKLM\SOFTWARE\ODBC\ODBC.INI
        //   System DSN (32-bit)    HKLM\SOFTWARE\WOW6432Node\ODBC\ODBC.INI
        //
        // A 32-bit installer on 64-bit Windows writes to the WOW6432Node copy, and the two are
        // genuinely different data sources, so both are read rather than merged. File DSNs are
        // plain .dsn files and already appear on the Files tab.
        // Views are stated explicitly rather than relying on Registry.LocalMachine, whose view
        // follows the bitness of the running process. Built 32-bit, HKLM\SOFTWARE\ODBC is silently
        // redirected to WOW6432Node, and the "64-bit" scope would quietly report 32-bit data.
        private static readonly (string Scope, Microsoft.Win32.RegistryHive Hive, Microsoft.Win32.RegistryView View, string Path)[] OdbcScopes =
        {
            ("User DSN",            Microsoft.Win32.RegistryHive.CurrentUser,  Microsoft.Win32.RegistryView.Default,    @"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources"),
            ("System DSN (64-bit)", Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64, @"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources"),
            ("System DSN (32-bit)", Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32, @"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources"),
        };

        /// <summary>
        /// Reads every registered DSN as (scope, name, driver, full registry key of its settings).
        /// </summary>
        private static List<(string Scope, string Name, string Driver, string Key)> EnumOdbcDataSources()
        {
            var found = new List<(string, string, string, string)>();
            foreach (var (scope, hive, view, path) in OdbcScopes)
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var k = baseKey.OpenSubKey(path);
                    if (k == null) continue;
                    foreach (string name in k.GetValueNames())
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string driver = k.GetValue(name)?.ToString() ?? "";
                        // The settings subkey sits beside the list key, not inside it.
                        // The 32-bit view maps SOFTWARE onto SOFTWARE\WOW6432Node, so the displayed
                        // key spells that out - it is the path an admin would actually open.
                        string shown = view == Microsoft.Win32.RegistryView.Registry32
                            ? path.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\")
                            : path;
                        string settingsKey =
                            (hive == Microsoft.Win32.RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE")
                            + "\\" + shown.Substring(0, shown.LastIndexOf('\\')) + "\\" + name;
                        found.Add((scope, name, driver, settingsKey));
                    }
                }
                catch (Exception exSwallow)
                {
                    System.Diagnostics.Debug.WriteLine("[ODBC] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
                }
            }
            return found;
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  SCHEDULED TASKS TAB
        // ══════════════════════════════════════════════════════════════════════════════════
        // Detected the same way as drivers and ODBC: snapshot before the source runs, diff during.
        //
        // The task list is read from the registry rather than from %windir%\System32\Tasks. That
        // folder is the authoritative store, but System32 is redirected to SysWOW64 for a 32-bit
        // process and the Tasks folder does not exist there, so a 32-bit build would see nothing.
        // TaskCache\Tree mirrors the same hierarchy, is not path-redirected, and needs no COM.
        //
        // Metadata (author, description, the command it runs) is then read from the task's XML
        // definition, which is locale-independent - unlike parsing schtasks.exe output. If the XML
        // cannot be read the row still appears, just without those extra columns.
        private const string TaskCacheTree = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";

        /// <summary>Walks TaskCache\Tree and returns every registered task path.</summary>
        private static List<string> EnumScheduledTasks()
        {
            var found = new List<string>();
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var root = baseKey.OpenSubKey(TaskCacheTree);
                if (root != null) WalkTaskTree(root, "", found, 0);
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[Tasks] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
            }
            return found;
        }

        private static void WalkTaskTree(Microsoft.Win32.RegistryKey key, string prefix, List<string> found, int depth)
        {
            if (depth > 12) return;   // the real tree is shallow; this only guards a cycle
            foreach (string sub in key.GetSubKeyNames())
            {
                string path = prefix + "\\" + sub;
                try
                {
                    using var child = key.OpenSubKey(sub);
                    if (child == null) continue;
                    // A node with an "Id" value is a task; anything else is a folder.
                    if (child.GetValue("Id") != null) found.Add(path);
                    else WalkTaskTree(child, path, found, depth + 1);
                }
                catch { }
            }
        }

        /// <summary>
        /// Reads Author, Description and the first Exec command out of a task's XML definition.
        /// Returns empty strings when the file cannot be read.
        /// </summary>
        private static (string Author, string Description, string Command) ReadTaskXml(string taskPath)
        {
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                // Sysnative first: it is how a 32-bit process reaches the real System32.
                foreach (string sys in new[] { "Sysnative", "System32" })
                {
                    string file = IOPath.Combine(windir, sys, "Tasks") + taskPath;
                    if (!File.Exists(file)) continue;

                    var doc = new System.Xml.XmlDocument();
                    doc.Load(file);
                    string Pick(string localName)
                    {
                        foreach (System.Xml.XmlNode n in doc.GetElementsByTagName(localName))
                            if (!string.IsNullOrWhiteSpace(n.InnerText)) return n.InnerText.Trim();
                        return "";
                    }
                    return (Pick("Author"), Pick("Description"), Pick("Command"));
                }
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[Tasks] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
            }
            return ("", "", "");
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  PROCESS TAB
        // ══════════════════════════════════════════════════════════════════════════════════
        // Unlike drivers, ODBC and tasks, a process is not a thing that exists at the end of the
        // run - it is an event. Diffing at the start and end would catch almost nothing, because an
        // installer's custom actions live for a second or two and are gone. Even the 800ms refresh
        // tick is too slow to see most of them.
        //
        // So processes get their own poller, running every 200ms for the life of the capture. It
        // stays cheap by touching only PIDs it has not seen before: the expensive parts (StartTime,
        // MainModule) are read once per process, and an unchanged machine costs one enumeration.
        //
        // _knownPids is seeded with everything already running, which doubles as the baseline - a
        // process that existed before the source ran is never recorded. The one known gap is PID
        // reuse: if Windows reissues a PID that was live at capture start, that process is missed.
        // Over an install window that is rare, and the alternative (reading StartTime for every
        // process on every poll) costs far more than it is worth.
        private void SnapshotProcessBaseline()
        {
            _seenProcs.Clear();
            _knownPids.Clear();
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try { _knownPids.Add(p.Id); } catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[Proc] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
            }
        }

        /// <summary>Starts the 200ms process poller. Safe to call when one is already running.</summary>
        private void StartProcessPoller()
        {
            StopProcessPoller();
            var cts = new System.Threading.CancellationTokenSource();
            _procPollCts = cts;
            var token = cts.Token;

            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    PollProcessesOnce();
                    try { await System.Threading.Tasks.Task.Delay(200, token); }
                    catch (OperationCanceledException) { break; }
                }
                PollProcessesOnce();   // one last sweep so a process started just before Stop is kept
            }, token);
        }

        private void StopProcessPoller()
        {
            try { _procPollCts?.Cancel(); } catch { }
            _procPollCts = null;
        }

        /// <summary>
        /// The command line a process was started with, plus its image path, from WMI.
        ///
        /// Win32_Process is the only practical source: System.Diagnostics.Process exposes the name,
        /// the PID, the start time and MainModule, and nothing that carries the arguments. It is
        /// asked ONE PID at a time, and only for a process that has just appeared - the baseline
        /// sweep has already claimed everything that was running beforehand, so this runs a handful
        /// of times across an install rather than hundreds.
        ///
        /// ExecutablePath comes back in the same query and is worth having: it frequently succeeds
        /// where MainModule throws, which is why some rows have shown a blank Path.
        ///
        /// Everything is swallowed. A process can exit between being seen and being asked about,
        /// WMI can be disabled, and a protected process will refuse - none of which is a reason to
        /// lose the row.
        /// </summary>
        private static (string cmdLine, string exePath) ProcessDetailsFromWmi(int pid)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE ProcessId = " + pid);
                foreach (System.Management.ManagementBaseObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        string cmd  = mo["CommandLine"]?.ToString()     ?? "";
                        string path = mo["ExecutablePath"]?.ToString()  ?? "";
                        return (cmd.Trim(), path.Trim());
                    }
                }
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[Proc] wmi " + pid + ": " + exSwallow.Message);
            }
            return ("", "");
        }

        private void PollProcessesOnce()
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        int pid = p.Id;
                        if (!_knownPids.Add(pid)) continue;   // already seen, or pre-existing

                        string name = "";
                        try { name = p.ProcessName; } catch { }
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        string started = "";
                        try { started = p.StartTime.ToString("HH:mm:ss"); } catch { }

                        // MainModule throws for protected processes and across bitness. The row is
                        // still worth keeping without a path.
                        string path = "";
                        try { path = p.MainModule?.FileName ?? ""; } catch { }

                        // What the installer actually invoked, switches included - the point of the
                        // tab is what ran, and "msiexec" without its arguments says very little.
                        var (cmdLine, wmiPath) = ProcessDetailsFromWmi(pid);
                        if (path.Length == 0) path = wmiPath;

                        _seenProcs[pid + "|" + name] = new SnapItem
                        {
                            Category    = "Process",
                            DisplayPath = name,
                            Value       = pid.ToString(),
                            Data        = started,
                            DataType    = path,
                            CommandLine = cmdLine,
                            IsIncluded  = true,
                        };
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[Proc] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
            }
        }

        /// <summary>
        /// Publishes anything the poller has recorded. Add-only: a process that has already exited
        /// must stay on the list, which is the whole point of the tab.
        /// </summary>
        private bool MergeNewProcesses()
        {
            bool added = false;
            foreach (var kv in _seenProcs)
            {
                string key = "Process|" + kv.Key;
                if (_allItems.ContainsKey(key)) continue;
                _allItems[key] = kv.Value;
                LogAction($"Process observed: {kv.Value.DisplayPath} (PID {kv.Value.Value})");
                added = true;
            }
            return added;
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  INSTALLED APPS TAB
        // ══════════════════════════════════════════════════════════════════════════════════
        // Add/Remove Programs reads three Uninstall keys, and a single source routinely writes to
        // more than one of them - a bundled installer registers the app, its updater and its
        // redistributables separately - so every new entry becomes its own row rather than trying
        // to pick a "main" one.
        //
        // Views are explicit for the same reason as ODBC: Registry.LocalMachine follows the
        // bitness of the running process, so a 32-bit build would silently read WOW6432Node twice
        // and never see a 64-bit install.
        //
        // EtwQaCapture does scan these keys, but it reports raw registry values. This reads the
        // entry as an application: name, publisher, version.
        private static readonly (string Scope, Microsoft.Win32.RegistryHive Hive, Microsoft.Win32.RegistryView View)[] AppScopes =
        {
            ("Machine (64-bit)", Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
            ("Machine (32-bit)", Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
            ("Current user",     Microsoft.Win32.RegistryHive.CurrentUser,  Microsoft.Win32.RegistryView.Default),
        };

        private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        // A registry key's last-write time is the only reliable way to tell "this entry was touched
        // during the run" from "this entry was already here". .NET does not expose it, so it comes
        // from RegQueryInfoKey. Without this, re-running an installer for an app that is already
        // present finds nothing new and the tab looks empty even though the install just happened.
        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegQueryInfoKey(
            Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey,
            System.Text.StringBuilder? lpClass, int[]? lpcbClass, IntPtr lpReserved,
            out int lpcSubKeys, out int lpcbMaxSubKeyLen, out int lpcbMaxClassLen,
            out int lpcValues, out int lpcbMaxValueNameLen, out int lpcbMaxValueLen,
            out int lpcbSecurityDescriptor,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpftLastWriteTime);

        private static DateTime KeyLastWriteUtc(Microsoft.Win32.RegistryKey key)
        {
            try
            {
                int rc = RegQueryInfoKey(key.Handle, null, null, IntPtr.Zero,
                    out _, out _, out _, out _, out _, out _, out _, out var ft);
                if (rc != 0) return DateTime.MinValue;
                long ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
                return DateTime.FromFileTimeUtc(ticks);
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Reads every Add/Remove Programs entry across all three scopes.</summary>
        private static List<(string Scope, string Id, string Name, string Publisher, string Version, string Key, DateTime Written)> EnumInstalledApps()
        {
            var found = new List<(string, string, string, string, string, string, DateTime)>();
            foreach (var (scope, hive, view) in AppScopes)
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var root = baseKey.OpenSubKey(UninstallPath);
                    if (root == null) continue;

                    foreach (string sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var k = root.OpenSubKey(sub);
                            if (k == null) continue;

                            string name = k.GetValue("DisplayName")?.ToString() ?? "";
                            // Entries with no DisplayName are patches and hidden components; Add/Remove
                            // Programs does not list them either.
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if ("1".Equals(k.GetValue("SystemComponent")?.ToString())) continue;

                            string shownRoot =
                                (hive == Microsoft.Win32.RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE")
                                + "\\" + (view == Microsoft.Win32.RegistryView.Registry32
                                    ? UninstallPath.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\")
                                    : UninstallPath);

                            found.Add((scope, sub, name,
                                       k.GetValue("Publisher")?.ToString() ?? "",
                                       k.GetValue("DisplayVersion")?.ToString() ?? "",
                                       shownRoot + "\\" + sub,
                                       KeyLastWriteUtc(k)));
                        }
                        catch { }
                    }
                }
                catch (Exception exSwallow)
                {
                    System.Diagnostics.Debug.WriteLine("[Apps] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
                }
            }
            return found;
        }

        /// <summary>Records the applications registered before the source runs.</summary>
        private void SnapshotAppBaseline()
        {
            // A second of slack: the key may be written moments before this snapshot completes.
            _appBaselineUtc = DateTime.UtcNow.AddSeconds(-1);
            try { _appBaseline = EnumInstalledApps().Select(a => a.Scope + "|" + a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch { _appBaseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// Adds a row for every application registered since the baseline. Add-only: an installer
        /// that registers an entry and then replaces it during the same run should leave both
        /// visible, because both are things it did.
        /// </summary>
        private bool MergeNewApps()
        {
            if (_appBaseline == null) return false;

            List<(string Scope, string Id, string Name, string Publisher, string Version, string Key, DateTime Written)> now;
            try { now = EnumInstalledApps(); } catch { return false; }

            bool added = false;
            foreach (var (scope, id, name, publisher, version, key, written) in now)
            {
                // Either the entry is new, or it existed and the source rewrote it - a repair, an
                // upgrade, or simply reinstalling the same version over itself. All three are the
                // source installing an application, and all three belong on this tab.
                bool isNew     = !_appBaseline.Contains(scope + "|" + id);
                bool rewritten = written > _appBaselineUtc;
                if (!isNew && !rewritten) continue;

                string itemKey = "InstalledApp|" + scope + "|" + id;
                if (_allItems.ContainsKey(itemKey)) continue;

                _allItems[itemKey] = new SnapItem
                {
                    Category     = "InstalledApp",
                    DisplayPath  = name,
                    Value        = publisher,
                    Data         = version,
                    DataType     = scope,
                    SourceOnDisk = key,
                    IsIncluded   = true,
                };
                LogAction($"Application {(isNew ? "registered" : "updated")}: {name} {version} [{scope}]");
                added = true;
            }
            return added;
        }

        /// <summary>Records the scheduled tasks present before the source runs.</summary>
        private void SnapshotTaskBaseline()
        {
            try { _taskBaseline = EnumScheduledTasks().ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch { _taskBaseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// Adds a row for every task registered since the baseline. Include/exclude state is kept
        /// per task path across passes. Returns true when the row set changed.
        /// </summary>
        private bool MergeNewTasks()
        {
            if (_taskBaseline == null) return false;

            List<string> now;
            try { now = EnumScheduledTasks(); } catch { return false; }

            var prevIncluded = _allItems.Values
                .Where(i => i.Category == "SchedTask")
                .GroupBy(i => i.Value + i.DisplayPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().IsIncluded, StringComparer.OrdinalIgnoreCase);

            var newRows = new List<(string Key, SnapItem Item)>();
            foreach (string taskPath in now)
            {
                if (_taskBaseline.Contains(taskPath)) continue;   // pre-existing, not ours

                int cut     = taskPath.LastIndexOf('\\');
                string name = cut >= 0 ? taskPath.Substring(cut + 1) : taskPath;
                string loc  = cut > 0  ? taskPath.Substring(0, cut) : "\\";
                var (author, desc, command) = ReadTaskXml(taskPath);

                var item = new SnapItem
                {
                    Category    = "SchedTask",
                    DisplayPath = name,
                    Value       = loc,
                    Data        = string.IsNullOrWhiteSpace(author) ? desc : author,
                    DataType    = command,
                    IsIncluded  = prevIncluded.TryGetValue(loc + name, out var inc) ? inc : true,
                };
                newRows.Add(("SchedTask|" + taskPath, item));
            }

            var existingKeys = _allItems.Where(kv => kv.Value.Category == "SchedTask").Select(kv => kv.Key).ToList();
            var newKeySet = new HashSet<string>(newRows.Select(r => r.Key));
            bool changed = newKeySet.Count != existingKeys.Count || existingKeys.Any(k => !newKeySet.Contains(k));
            if (!changed) return false;

            foreach (var k in existingKeys) _allItems.TryRemove(k, out _);
            foreach (var (key, item) in newRows)
            {
                _allItems[key] = item;
                if (!prevIncluded.ContainsKey(item.Value + item.DisplayPath))
                    LogAction($"Scheduled task captured: {item.Value}\\{item.DisplayPath}");
            }
            return true;
        }

        /// <summary>Records the ODBC data sources present before the source runs.</summary>
        private void SnapshotOdbcBaseline()
        {
            try { _odbcBaseline = EnumOdbcDataSources().Select(d => d.Scope + "|" + d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch { _odbcBaseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// Adds a row for every DSN registered since the baseline. Include/exclude state is kept
        /// per DSN across passes. Returns true when the row set changed.
        /// </summary>
        private bool MergeNewOdbc()
        {
            if (_odbcBaseline == null) return false;

            List<(string Scope, string Name, string Driver, string Key)> now;
            try { now = EnumOdbcDataSources(); } catch { return false; }

            var prevIncluded = _allItems.Values
                .Where(i => i.Category == "Odbc")
                .GroupBy(i => i.Value + "|" + i.DisplayPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().IsIncluded, StringComparer.OrdinalIgnoreCase);

            var newRows = new List<(string Key, SnapItem Item)>();
            foreach (var (scope, name, driver, key) in now)
            {
                string id = scope + "|" + name;
                if (_odbcBaseline.Contains(id)) continue;   // pre-existing, not ours

                var item = new SnapItem
                {
                    Category    = "Odbc",
                    DisplayPath = name,
                    Value       = scope,
                    Data        = driver,
                    DataType    = key,
                    IsIncluded  = prevIncluded.TryGetValue(id, out var inc) ? inc : true,
                };
                newRows.Add(("Odbc|" + id, item));
            }

            var existingKeys = _allItems.Where(kv => kv.Value.Category == "Odbc").Select(kv => kv.Key).ToList();
            var newKeySet = new HashSet<string>(newRows.Select(r => r.Key));
            bool changed = newKeySet.Count != existingKeys.Count || existingKeys.Any(k => !newKeySet.Contains(k));
            if (!changed) return false;

            foreach (var k in existingKeys) _allItems.TryRemove(k, out _);
            foreach (var (key, item) in newRows)
            {
                _allItems[key] = item;
                if (!prevIncluded.ContainsKey(item.Value + "|" + item.DisplayPath))
                    LogAction($"ODBC data source captured: {item.DisplayPath} [{item.Value}] driver: {item.Data}");
            }
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  TEMP TAB (VIEW ONLY)
        // ══════════════════════════════════════════════════════════════════════════════════
        // %TEMP% is NOT one of EtwQaCapture's streams, so nothing the installer writes there ever
        // reaches LiveFiles - which is why hooking the capture ingest produced an empty tab. Use
        // the same technique the driver detection already uses: snapshot the folder before the
        // source runs, then diff it on every live tick.
        //
        // Diffing on each tick (rather than once at the end) is deliberate. Installers routinely
        // delete their temp payload before they exit - NSIS unpacks to %TEMP%\ns*.tmp\ and removes
        // it on completion - so a single post-install scan would find nothing. Ticking catches the
        // files while they still exist, enriches size/date from disk there and then, and KEEPS the
        // row afterwards. The tab therefore shows what the installer used, not what survived.
        //
        // Everything lands in _tempItems, never _allItems: not counted, not saved to .svr, not
        // packaged.

        /// <summary>Files and folders already in %TEMP% before the source runs - these are not ours.</summary>
        private HashSet<string>? _tempBaseline;

        /// <summary>C:\Users\&lt;user&gt;\AppData\Local\Temp for the interactive user.</summary>
        private static string UserTempDir()
        {
            try { return IOPath.GetTempPath().TrimEnd('\\'); }
            catch { return ""; }
        }

        /// <summary>Every file and folder currently under %TEMP%, recursively. Never throws.</summary>
        private static HashSet<string> EnumTempEntries()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = UserTempDir();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return set;
            try
            {
                // EnumerateFileSystemEntries with a manual stack: a plain AllDirectories walk throws
                // the moment it meets one locked or vanished subfolder, and %TEMP% is full of both.
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    string dir = stack.Pop();
                    try
                    {
                        foreach (var d in Directory.EnumerateDirectories(dir))
                        {
                            set.Add(d);
                            stack.Push(d);
                        }
                        foreach (var f in Directory.EnumerateFiles(dir))
                            set.Add(f);
                    }
                    catch { /* locked / deleted mid-walk - skip this folder, keep going */ }
                }
            }
            catch { }
            return set;
        }

        /// <summary>Records what was already in %TEMP% before the source runs.</summary>
        private void SnapshotTempBaseline()
        {
            try { _tempBaseline = EnumTempEntries(); }
            catch { _tempBaseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// Adds anything that has appeared under %TEMP% since the baseline to _tempItems.
        /// Additive only - entries already recorded are kept even once the installer deletes them.
        /// </summary>
        private void MergeTempItems()
        {
            if (_tempBaseline == null) return;

            HashSet<string> now;
            try { now = EnumTempEntries(); }
            catch { return; }

            foreach (var path in now)
            {
                if (_tempBaseline.Contains(path)) continue;   // was there before the source ran

                bool isDir;
                try { isDir = Directory.Exists(path); }
                catch { continue; }

                string cat = isDir ? "Folder" : "File";
                string key = cat + "|" + path + "|";
                if (_tempItems.ContainsKey(key)) continue;

                var item = new SnapItem { Category = cat, DisplayPath = path, IsIncluded = false };
                item.EnrichFromDisk();   // read size/date NOW, while the file still exists
                _tempItems[key] = item;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  EVENT VIEWER TAB (VIEW ONLY) - Application log
        // ══════════════════════════════════════════════════════════════════════════════════
        // REQUIRES the System.Diagnostics.EventLog package (first-party Microsoft). It is already
        // present if the project references Microsoft.Windows.Compatibility; otherwise add:
        //     <PackageReference Include="System.Diagnostics.EventLog" Version="8.0.0" />
        //
        // Baseline the clock at capture start, then on each live tick pull Application-log entries
        // written since. Ticking (rather than one read at the end) keeps this consistent with the
        // driver and temp detection, and means the tab fills as the install runs.

        /// <summary>Records the moment the source starts, so only entries it writes are listed.</summary>
        private void SnapshotEventBaseline()
        {
            _eventItems.Clear();
            _eventRecordIds.Clear();
            _eventBaselineUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Adds Application-log entries written since the baseline. Additive and deduped by
        /// EventRecordID, so repeated ticks re-reading the same window cost nothing.
        /// </summary>
        private void MergeEventLog()
        {
            if (_eventBaselineUtc == DateTime.MinValue) return;
            try
            {
                // Canonical Event Log XPath: filter server-side by time so the reader only ever
                // walks the entries written during this run, not the whole Application log.
                string xpath = "*[System[TimeCreated[@SystemTime>='"
                             + _eventBaselineUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ",
                                   System.Globalization.CultureInfo.InvariantCulture)
                             + "']]]";

                var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                    "Application", System.Diagnostics.Eventing.Reader.PathType.LogName, xpath);

                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
                for (var rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        if (rec.RecordId is not long id) continue;
                        if (!_eventRecordIds.Add(id)) continue;   // already listed from an earlier tick

                        // Every one of these accessors can throw when the publishing provider is not
                        // registered on this machine (common for third-party installers), so each is
                        // guarded individually rather than losing the whole row to one bad field.
                        string level = "";
                        try { level = rec.LevelDisplayName ?? ""; } catch { }
                        string msg = "";
                        try { msg = rec.FormatDescription() ?? ""; } catch { }
                        string src = "";
                        try { src = rec.ProviderName ?? ""; } catch { }
                        string when = "";
                        try { when = rec.TimeCreated?.ToString("dd-MM-yyyy HH:mm:ss") ?? ""; } catch { }

                        _eventItems.Add(new EventLogRow
                        {
                            Level   = level,
                            Logged  = when,
                            Source  = src,
                            EventId = rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            // Collapse to one line: the grid row is single-line, and event messages
                            // are routinely several paragraphs.
                            Message = msg.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim(),
                        });
                    }
                }
            }
            catch { /* log unreadable / access denied - leave the tab as it stands */ }
        }

        /// <summary>The source file name when the source is a .inf (e.g. "ftdibus.inf"), else "".</summary>
        private string SourceInfName()
        {
            string src = _lastCapturedSource ?? "";
            return src.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ? IOPath.GetFileName(src) : "";
        }

        /// <summary>Folder names under DriverStore\FileRepository (one per installed package).</summary>
        private static HashSet<string> EnumDriverPackageFolders()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(DriverStoreRepo))
                    foreach (var d in Directory.GetDirectories(DriverStoreRepo))
                        set.Add(IOPath.GetFileName(d));
            }
            catch { /* FileRepository may be ACL-restricted; treat as empty baseline */ }
            return set;
        }

        /// <summary>
        /// Reconciles the Driver rows against the packages installed since SnapshotDriverBaseline.
        /// One install can stage several packages sharing one INF name (e.g. two ftdibus builds,
        /// each in its own arch_hash folder); those are distinct DriverStore packages, so each gets
        /// its own row, disambiguated by FileRepository hash. Rows are keyed by package folder and
        /// rebuilt every pass (not just appended), so a package first seen alone - and named plainly
        /// that pass - is corrected once its sibling appears, with no stale duplicate left behind.
        /// Include/exclude state is preserved per folder. Returns true if the row set changed.
        /// </summary>
        private bool MergeNewDrivers()
        {
            if (_driverBaseline == null) return false;

            HashSet<string> now;
            try { now = EnumDriverPackageFolders(); }
            catch { return false; }

            // The packages to show: those staged since the baseline (any source), PLUS - when the
            // source IS a .inf - the folder(s) for that INF even if already staged from an earlier
            // run. pnputil does not re-stage an identical package, so a browsed .inf that is already
            // in the DriverStore produces no baseline change; matching by source INF name shows it.
            string srcInf = SourceInfName();   // "ftdibus.inf", or "" when the source is not a .inf
            var newPkgs = new List<(string Folder, string Inf)>();
            foreach (var folder in now)
            {
                string inf = DriverInfFromFolder(folder);
                if (string.IsNullOrEmpty(inf)) continue;
                bool staged    = !_driverBaseline.Contains(folder);   // appeared this run
                bool isSrcInf  = srcInf.Length > 0 && string.Equals(inf, srcInf, StringComparison.OrdinalIgnoreCase);
                if (!staged && !isSrcInf) continue;                   // pre-existing and unrelated
                newPkgs.Add((folder, inf));
            }

            // INF names staged more than once need a hash suffix so each package is its own row;
            // a uniquely-named INF stays clean (no hash noise for single-driver apps).
            var dupInf = newPkgs.GroupBy(p => p.Inf, StringComparer.OrdinalIgnoreCase)
                                .Where(g => g.Count() > 1)
                                .Select(g => g.Key)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Desired rows, keyed by package folder (stable identity). Display name is recomputed
            // every pass from the *current* full set, so names never go stale.
            var prevIncluded = _allItems.Values
                .Where(i => i.Category == "Driver" && !string.IsNullOrEmpty(i.DriverFolder))
                .GroupBy(i => i.DriverFolder, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().IsIncluded, StringComparer.OrdinalIgnoreCase);

            var newRows = new List<(string Key, SnapItem Item)>();
            foreach (var (folder, inf) in newPkgs)
            {
                string baseName = IOPath.GetFileNameWithoutExtension(inf);
                string disp = dupInf.Contains(inf)
                    ? $"{baseName} ({DriverFolderHash(folder)})"
                    : baseName;
                var item = new SnapItem
                {
                    Category     = "Driver",
                    DisplayPath  = disp,
                    Value        = inf,
                    DriverFolder = folder,
                    IsIncluded   = prevIncluded.TryGetValue(folder, out var inc) ? inc : true,
                };
                newRows.Add((item.Category + "|" + item.DisplayPath + "|" + item.Value, item));
            }

            // Only touch _allItems when the Driver row set actually changed (avoids churn each tick).
            var existingDriverKeys = _allItems.Where(kv => kv.Value.Category == "Driver")
                                              .Select(kv => kv.Key).ToList();
            var newKeySet = new HashSet<string>(newRows.Select(r => r.Key));
            bool changed = newKeySet.Count != existingDriverKeys.Count
                           || existingDriverKeys.Any(k => !newKeySet.Contains(k));
            if (!changed) return false;

            foreach (var k in existingDriverKeys) _allItems.TryRemove(k, out _);
            foreach (var (key, item) in newRows)
            {
                _allItems[key] = item;
                if (!prevIncluded.ContainsKey(item.DriverFolder))
                    LogAction($"Driver captured: {item.Value} [{item.DisplayPath}] (FileRepository folder: {item.DriverFolder})");
            }
            return true;
        }

        /// <summary>
        /// Extracts the original INF name from a FileRepository package folder name.
        /// "ftdibus.inf_amd64_9be5c6138131d249" -> "ftdibus.inf".
        /// </summary>
        private static string DriverInfFromFolder(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return "";
            int idx = folderName.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return folderName.Substring(0, idx + 4);   // keep ".inf"
            return folderName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) ? folderName : "";
        }

        /// <summary>
        /// The hash segment of a FileRepository package folder, used to tell apart packages that
        /// share an INF name. "ftdibus.inf_amd64_9be5c6138131d249" -> "9be5c6138131d249".
        /// </summary>
        private static string DriverFolderHash(string folderName)
        {
            if (string.IsNullOrEmpty(folderName)) return "";
            int us = folderName.LastIndexOf('_');
            return (us >= 0 && us < folderName.Length - 1) ? folderName.Substring(us + 1) : folderName;
        }

        /// <summary>
        /// Lists the payload files inside each captured driver's FileRepository package folder into
        /// the Files tree, so the driver's file/folder structure is visible. These are the OS-staged
        /// copies and are added UNCHECKED (IsIncluded = false): they are shown for reference only and
        /// are not pulled into the MSI - a driver is deployed with pnputil, not by file-copy.
        /// Enumerated once per package (cheap for typical packages); the Stop pass forces a re-scan
        /// to catch files PnP was still copying when the package first appeared. Reading inside
        /// FileRepository needs an elevated capture; if access is denied the package is skipped.
        /// Returns true if any file row was added.
        /// </summary>
        private bool RefreshDriverPayload(bool force)
        {
            if (_driverBaseline == null) return false;

            var folders = _allItems.Values
                .Where(i => i.Category == "Driver" && !string.IsNullOrEmpty(i.DriverFolder))
                .Select(i => i.DriverFolder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool any = false;
            foreach (var folder in folders)
            {
                if (!force && _driverPayloadDone.Contains(folder)) continue;

                string root = IOPath.Combine(DriverStoreRepo, folder);
                string[] files;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    _driverPayloadDone.Add(folder);   // ACL-denied: do not retry every tick
                    continue;
                }

                foreach (var f in files)
                {
                    var item = new SnapItem { Category = "File", DisplayPath = f, IsIncluded = false };
                    string key = item.Category + "|" + item.DisplayPath + "|" + item.Value;
                    if (_allItems.ContainsKey(key)) continue;
                    try { item.EnrichFromDisk(); } catch { }
                    _allItems[key] = item;
                    any = true;
                }
                _driverPayloadDone.Add(folder);
            }
            return any;
        }

        // ── Environment variable detection (registry diff) ─────────
        // SetEnvironmentVariable / setx write to the registry (Machine: Session Manager\Environment;
        // User: HKCU\Environment) - keys the capture does not monitor - and there is no env stream,
        // so the Environment tab is fed by diffing a baseline snapshot, the same way drivers are.

        /// <summary>Records Machine + User environment variables before the source runs.</summary>
        private void SnapshotEnvBaseline()
        {
            _envBaseline = EnumEnvVars();
        }

        /// <summary>"scope|name" -> value across the Machine and User environment scopes.</summary>
        private static Dictionary<string, string> EnumEnvVars()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Read(EnvironmentVariableTarget target, string scope)
            {
                try
                {
                    foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables(target))
                    {
                        string name = kv.Key?.ToString() ?? "";
                        if (name.Length == 0) continue;
                        d[scope + "|" + name] = kv.Value?.ToString() ?? "";
                    }
                }
                catch { /* scope unreadable: skip */ }
            }
            Read(EnvironmentVariableTarget.Machine, "Machine");
            Read(EnvironmentVariableTarget.User,    "User");
            return d;
        }

        /// <summary>
        /// Adds Environment rows for variables the source added or changed since SnapshotEnvBaseline.
        /// Returns true if any row was added. Variable names in the exclusion list are skipped.
        /// </summary>
        private bool MergeNewEnvVars()
        {
            if (_envBaseline == null) return false;

            var now = EnumEnvVars();
            bool any = false;
            foreach (var kv in now)
            {
                // Unchanged from the baseline: not the source's doing.
                if (_envBaseline.TryGetValue(kv.Key, out var oldVal) && oldVal == kv.Value) continue;

                int bar = kv.Key.IndexOf('|');
                string scope = bar > 0 ? kv.Key.Substring(0, bar) : "";
                string name  = bar > 0 ? kv.Key.Substring(bar + 1) : kv.Key;

                if (_exclusions.EnvVars?.Any(x => !string.IsNullOrWhiteSpace(x)
                        && name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) == true)
                    continue;

                if (AddEnvVarItem(name, kv.Value, scope)) any = true;
            }
            return any;
        }

        /// <summary>
        /// Adds an Environment row plus the variable's registry representation (an env var IS a
        /// registry value: Machine -> Session Manager\Environment, User -> HKCU\Environment), so it
        /// appears on both the Environment and Registry tabs and can be packaged via the registry.
        /// Process-scope (transient) variables get no registry entry. Returns true if anything new
        /// was added.
        /// </summary>
        private bool AddEnvVarItem(string name, string value, string scope)
        {
            bool any = false;

            var env = new SnapItem { Category = "EnvVar", DisplayPath = name, Value = value, Data = scope };
            string envKey = env.Category + "|" + env.DisplayPath + "|" + env.Value;
            if (!_allItems.ContainsKey(envKey))
            {
                _allItems[envKey] = env;
                LogAction($"EnvVar captured: {name}={value} ({scope})");
                any = true;
            }

            // Persistent (Machine/User) variables are registry values - surface that representation.
            if (scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)
             || scope.Equals("User",    StringComparison.OrdinalIgnoreCase))
            {
                string regPath = scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)
                    ? @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment"
                    : @"HKEY_CURRENT_USER\Environment";
                string regType = value.Contains('%') ? "REG_EXPAND_SZ" : "REG_SZ";

                var reg = new SnapItem
                {
                    Category = "Registry", DisplayPath = regPath, Value = name, Data = value, DataType = regType,
                };
                string regKey = reg.Category + "|" + reg.DisplayPath + "|" + reg.Value;
                if (!_allItems.ContainsKey(regKey))
                {
                    _allItems[regKey] = reg;
                    LogAction($"EnvVar registry captured: {regPath}\\{name}={value} ({regType})");
                    any = true;
                }
            }
            return any;
        }

        /// <summary>
        /// For a script source (.ps1/.bat/.cmd), reads the variables the script SETS straight from
        /// the script text and shows them with their current system values. This catches a variable
        /// the script re-sets to a value it already has - which the value-diff cannot see because the
        /// before/after snapshots are identical. Returns true if any row was added.
        /// </summary>
        private bool MergeScriptEnvVars()
        {
            string src = _lastCapturedSource ?? "";
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return false;
            string ext = IOPath.GetExtension(src).ToLowerInvariant();
            if (ext != ".ps1" && ext != ".bat" && ext != ".cmd") return false;

            string text;
            try { text = File.ReadAllText(src); } catch { return false; }

            bool any = false;
            foreach (var (name, scope) in ParseScriptEnvVarOps(text))
            {
                // Process-scope variables are transient (gone when the script's shell exits) and
                // never persist to the system, so only Machine/User are meaningful to capture.
                if (!scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)
                 && !scope.Equals("User",    StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_exclusions.EnvVars?.Any(x => !string.IsNullOrWhiteSpace(x)
                        && name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) == true)
                    continue;

                // Show the actual value now in the system for the scope the script targeted.
                string value = "";
                try
                {
                    var target = scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)
                        ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User;
                    value = Environment.GetEnvironmentVariable(name, target) ?? "";
                }
                catch { }

                if (AddEnvVarItem(name, value, scope)) any = true;
            }
            return any;
        }

        /// <summary>
        /// Extracts (name, scope) pairs from environment-variable-setting commands in a script:
        /// [Environment]::SetEnvironmentVariable("NAME", value, "Machine|User|Process") and
        /// setx NAME value [/M]  (/M => Machine, otherwise User).
        /// </summary>
        private static IEnumerable<(string Name, string Scope)> ParseScriptEnvVarOps(string text)
        {
            var results = new List<(string, string)>();
            if (string.IsNullOrEmpty(text)) return results;

            const System.Text.RegularExpressions.RegexOptions ic =
                System.Text.RegularExpressions.RegexOptions.IgnoreCase;

            // [Environment]::SetEnvironmentVariable("NAME", <value>, "Machine|User|Process")
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                text,
                @"SetEnvironmentVariable\(\s*['""](?<name>[^'""]+)['""]\s*,\s*.*?,\s*['""](?<scope>Machine|User|Process)['""]",
                ic))
            {
                results.Add((m.Groups["name"].Value, m.Groups["scope"].Value));
            }

            // setx NAME value [/M]   (/M => Machine scope, default User)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                text,
                @"\bsetx\s+['""]?(?<name>[A-Za-z_][A-Za-z0-9_]*)['""]?\s+(?<rest>[^\r\n]*)",
                ic))
            {
                string rest  = m.Groups["rest"].Value;
                string scope = System.Text.RegularExpressions.Regex.IsMatch(rest, @"/[mM]\b") ? "Machine" : "User";
                results.Add((m.Groups["name"].Value, scope));
            }

            return results;
        }

        // ── Firewall rule detection (COM diff + script parse) ──────
        // New-NetFirewallRule / netsh write to the registry + WFP store, which the capture does not
        // monitor, and there is no firewall stream. New rules are found by diffing the Windows
        // Firewall COM store (catches any source), and by parsing the script (catches a rule the
        // script re-adds that already exists - which the diff cannot see).

        /// <summary>Records firewall rules present before the source runs (for the COM diff).</summary>
        private void SnapshotFirewallBaseline()
        {
            _firewallBaseline = EnumFirewallRuleKeys();
            _firewallRegDone.Clear();
        }

        /// <summary>"name|direction" for every rule in the Windows Firewall COM store.</summary>
        private static HashSet<string> EnumFirewallRuleKeys()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Type? t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return set;
                dynamic? policy = Activator.CreateInstance(t);
                if (policy == null) return set;
                foreach (dynamic rule in policy.Rules)
                {
                    try
                    {
                        string name = (string)rule.Name ?? "";
                        if (name.Length > 0) set.Add(name + "|" + (int)rule.Direction);
                    }
                    catch { }
                }
            }
            catch { /* firewall service/COM unavailable */ }
            return set;
        }

        /// <summary>
        /// Adds Firewall rows for rules present now that were not in the baseline (any source).
        /// Returns true if any row was added.
        /// </summary>
        private bool MergeNewFirewall()
        {
            if (_firewallBaseline == null) return false;
            bool any = false;
            try
            {
                Type? t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return false;
                dynamic? policy = Activator.CreateInstance(t);
                if (policy == null) return false;
                foreach (dynamic rule in policy.Rules)
                {
                    string name; int dir; int action;
                    try
                    {
                        name   = (string)rule.Name ?? "";
                        dir    = (int)rule.Direction;   // 1 = In, 2 = Out
                        action = (int)rule.Action;      // 0 = Block, 1 = Allow
                    }
                    catch { continue; }
                    if (name.Length == 0) continue;
                    if (_firewallBaseline.Contains(name + "|" + dir)) continue;   // pre-existing

                    if (AddFirewallItem(name, dir == 2 ? "Outbound" : "Inbound", action == 0 ? "Block" : "Allow"))
                        any = true;
                }
            }
            catch { }
            return any;
        }

        /// <summary>
        /// For a script source, reads the firewall rules the script adds straight from the script
        /// text - catches a rule re-added with the same name that already exists (which the COM diff
        /// cannot see). Returns true if any row was added.
        /// </summary>
        private bool MergeScriptFirewall()
        {
            string src = _lastCapturedSource ?? "";
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return false;
            string ext = IOPath.GetExtension(src).ToLowerInvariant();
            if (ext != ".ps1" && ext != ".bat" && ext != ".cmd") return false;

            string text;
            try { text = File.ReadAllText(src); } catch { return false; }

            bool any = false;
            foreach (var (name, dir, act) in ParseScriptFirewallRules(text))
                if (!string.IsNullOrWhiteSpace(name) && AddFirewallItem(name, dir, act))
                    any = true;
            return any;
        }

        /// <summary>Adds a Firewall row (deduped) plus the rule's registry value. Value = direction, Data = action.</summary>
        private bool AddFirewallItem(string name, string direction, string action)
        {
            bool any = false;
            var item = new SnapItem { Category = "Firewall", DisplayPath = name, Value = direction, Data = action };
            string key = item.Category + "|" + item.DisplayPath + "|" + item.Value;
            if (!_allItems.ContainsKey(key))
            {
                _allItems[key] = item;
                LogAction($"Firewall captured: {name} [{direction}/{action}]");
                any = true;
            }
            if (AddFirewallRegistry(name)) any = true;
            return any;
        }

        /// <summary>
        /// Surfaces the firewall rule's registry value (a rule IS a REG_SZ under
        /// ...\SharedAccess\Parameters\FirewallPolicy\FirewallRules, keyed by a GUID with the rule
        /// definition as data) so it shows on the Registry tab and can be packaged via the registry.
        /// The matching value is found by the "Name=&lt;rule&gt;" segment in its data.
        /// </summary>
        private bool AddFirewallRegistry(string ruleName)
        {
            if (_firewallRegDone.Contains(ruleName)) return false;
            const string sub = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
            const string fullKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(sub);
                if (key == null) return false;
                foreach (string valName in key.GetValueNames())
                {
                    string data = key.GetValue(valName) as string ?? "";
                    if (data.Length == 0) continue;
                    // Rule data is pipe-delimited key=value segments; match the Name segment exactly.
                    if (!data.Split('|').Any(seg => seg.Equals("Name=" + ruleName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    _firewallRegDone.Add(ruleName);   // found it: don't re-scan the key for this rule
                    var reg = new SnapItem
                    {
                        Category = "Registry", DisplayPath = fullKey, Value = valName, Data = data, DataType = "REG_SZ",
                    };
                    string k = reg.Category + "|" + reg.DisplayPath + "|" + reg.Value;
                    if (_allItems.ContainsKey(k)) return false;
                    _allItems[k] = reg;
                    LogAction($"Firewall registry captured: {fullKey}\\{valName}");
                    return true;
                }
            }
            catch { }
            return false;
        }

        // ── Service registry representation ────────────────────────
        // A Windows service IS defined by its registry key HKLM\SYSTEM\CurrentControlSet\Services\
        // <name> (Type, Start, ErrorControl, ImagePath, DisplayName, Description, ObjectName, ...).
        // The capture shows the service on the Services tab but not those registry values, so surface
        // them on the Registry tab (and make the service packageable via the registry).

        /// <summary>Adds the registry values of each captured service's key. Returns true if any added.</summary>
        private bool MergeServiceRegistry()
        {
            bool any = false;
            var services = _allItems.Values
                .Where(i => i.Category == "Service" && !string.IsNullOrEmpty(i.DisplayPath))
                .Select(i => i.DisplayPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var svc in services)
            {
                if (_serviceRegDone.Contains(svc)) continue;
                if (AddServiceRegistry(svc)) any = true;
            }
            return any;
        }

        /// <summary>Reads HKLM\...\Services\&lt;name&gt; and adds a Registry row per value (top level).</summary>
        private bool AddServiceRegistry(string serviceName)
        {
            string sub     = @"SYSTEM\CurrentControlSet\Services\" + serviceName;
            string fullKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + serviceName;
            bool any = false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(sub);
                if (key == null) return false;
                _serviceRegDone.Add(serviceName);   // key exists: don't re-scan for this service

                foreach (string valName in key.GetValueNames())
                {
                    object? raw = key.GetValue(valName, null,
                        Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (raw == null) continue;   // e.g. unset (Default)
                    var kind = key.GetValueKind(valName);

                    var reg = new SnapItem
                    {
                        Category    = "Registry",
                        DisplayPath = fullKey,
                        Value       = string.IsNullOrEmpty(valName) ? "(Default)" : valName,
                        Data        = FormatRegValue(raw, kind),
                        DataType    = RegKindToString(kind),
                    };
                    string k = reg.Category + "|" + reg.DisplayPath + "|" + reg.Value;
                    if (!_allItems.ContainsKey(k)) { _allItems[k] = reg; any = true; }
                }
                if (any) LogAction($"Service registry captured: {fullKey}");
            }
            catch { }
            return any;
        }

        /// <summary>Registry value-kind to the tool's REG_* type string.</summary>
        private static string RegKindToString(Microsoft.Win32.RegistryValueKind k) => k switch
        {
            Microsoft.Win32.RegistryValueKind.DWord        => "REG_DWORD",
            Microsoft.Win32.RegistryValueKind.QWord        => "REG_QWORD",
            Microsoft.Win32.RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
            Microsoft.Win32.RegistryValueKind.MultiString  => "REG_MULTI_SZ",
            Microsoft.Win32.RegistryValueKind.Binary       => "REG_BINARY",
            _                                              => "REG_SZ",
        };

        /// <summary>Formats a registry value to match the tool's convention (DWORD/QWORD as decimal,
        /// MULTI_SZ newline-joined, BINARY as space-separated hex, strings verbatim).</summary>
        private static string FormatRegValue(object? raw, Microsoft.Win32.RegistryValueKind kind)
        {
            if (raw == null) return "";
            try
            {
                switch (kind)
                {
                    case Microsoft.Win32.RegistryValueKind.DWord:
                        return ((uint)Convert.ToInt32(raw)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case Microsoft.Win32.RegistryValueKind.QWord:
                        return ((ulong)Convert.ToInt64(raw)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case Microsoft.Win32.RegistryValueKind.MultiString:
                        return string.Join("\n", (string[])raw);
                    case Microsoft.Win32.RegistryValueKind.Binary:
                        return BitConverter.ToString((byte[])raw).Replace("-", " ");
                    default:
                        return raw.ToString() ?? "";
                }
            }
            catch { return raw.ToString() ?? ""; }
        }

        /// <summary>
        /// Extracts (name, direction, action) from firewall commands in a script:
        /// New-NetFirewallRule -DisplayName "X" -Direction Inbound -Action Allow, and
        /// netsh advfirewall firewall add rule name="X" dir=in action=allow.
        /// </summary>
        private static IEnumerable<(string Name, string Direction, string Action)> ParseScriptFirewallRules(string text)
        {
            var results = new List<(string, string, string)>();
            if (string.IsNullOrEmpty(text)) return results;

            const System.Text.RegularExpressions.RegexOptions ic =
                System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            const System.Text.RegularExpressions.RegexOptions ics =
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline;

            static string Dir(string d) => d.StartsWith("Out", StringComparison.OrdinalIgnoreCase) ? "Outbound"
                                         : d.StartsWith("In",  StringComparison.OrdinalIgnoreCase) ? "Inbound" : d;
            static string Act(string a) => a.Equals("Block", StringComparison.OrdinalIgnoreCase) ? "Block"
                                         : a.Equals("Allow", StringComparison.OrdinalIgnoreCase) ? "Allow" : a;

            // New-NetFirewallRule ... -DisplayName "X" ... -Direction Inbound ... -Action Allow
            foreach (System.Text.RegularExpressions.Match stmt in System.Text.RegularExpressions.Regex.Matches(
                text, @"New-NetFirewallRule\b(?<body>.*?)(?=New-NetFirewallRule\b|\z)", ics))
            {
                string body = stmt.Groups["body"].Value;
                var nameM = System.Text.RegularExpressions.Regex.Match(body,
                    @"-DisplayName\s+['""]?(?<v>[^'""\r\n]+?)['""]?(?=\s+-|\s*$)", ic);
                if (!nameM.Success) continue;
                string dir = System.Text.RegularExpressions.Regex.Match(body,
                    @"-Direction\s+['""]?(?<v>Inbound|Outbound|In|Out)['""]?", ic).Groups["v"].Value;
                string act = System.Text.RegularExpressions.Regex.Match(body,
                    @"-Action\s+['""]?(?<v>Allow|Block)['""]?", ic).Groups["v"].Value;
                results.Add((nameM.Groups["v"].Value.Trim(), Dir(dir), Act(act)));
            }

            // netsh advfirewall firewall add rule name="X" dir=in action=allow
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                text, @"advfirewall\s+firewall\s+add\s+rule\b(?<body>[^\r\n]*)", ic))
            {
                string body = m.Groups["body"].Value;
                var nameM = System.Text.RegularExpressions.Regex.Match(body,
                    @"\bname\s*=\s*['""]?(?<v>[^'""]+?)['""]?(?=\s+\w+\s*=|\s*$)", ic);
                if (!nameM.Success) continue;
                string dir = System.Text.RegularExpressions.Regex.Match(body, @"\bdir\s*=\s*(?<v>in|out)", ic).Groups["v"].Value;
                string act = System.Text.RegularExpressions.Regex.Match(body, @"\baction\s*=\s*(?<v>allow|block)", ic).Groups["v"].Value;
                results.Add((nameM.Groups["v"].Value.Trim(), Dir(dir), Act(act)));
            }

            return results;
        }

        // ── Certificate import (.cer/.crt) ─────────────────────────
        // A certificate file IS the source: read the cert straight from the file and import it with
        // certutil, the same way .reg and fonts are applied directly. A capture/store-diff is
        // unreliable here - the store write is done out-of-process by crypto services, and a diff
        // also misses a cert that is already present in the store from an earlier run.

        /// <summary>
        /// Reads the certificate from a .cer/.crt file, imports it into the LocalMachine Trusted
        /// Root store (certutil -addstore Root), and shows it on the Certificates tab. Runs in the
        /// page's current context (Administrator or SYSTEM via PsExec).
        /// </summary>
        private void ImportCertFile(string certPath)
        {
            try
            {
                // Parse first, so the cert's identity is shown even if the import is a no-op
                // (already in the store) or cannot be confirmed.
                string subject, thumbprint, storeName;
                try
                {
                    using var cert = new X509Certificate2(certPath);
                    subject    = CertFriendlyName(cert);
                    thumbprint = cert.Thumbprint ?? "";
                    storeName  = ResolveCertStore(cert);   // dropdown choice, or Auto -> detect
                }
                catch (Exception pex)
                {
                    SvShowMessage("Source Validation - Install Certificate",
                        $"Could not read the certificate:\n{pex.Message}");
                    return;
                }

                string storeLabel = $"LocalMachine\\{storeName}";
                string workDir    = IOPath.GetDirectoryName(certPath) ?? "";

                // Import into the store this cert belongs in (certutil -addstore <store>), mirroring
                // how the source would be applied on a target. certutil -addstore is silent when
                // elevated. A publisher cert in TrustedPublisher lets signed packages install
                // without the "trust this publisher?" prompt.
                bool addOk = false; string addErr = "";
                try
                {
                    ProcessStartInfo? psi = _runSystemContext
                        ? BuildSystemContextPsi("certutil.exe", $"-addstore {storeName} \"{certPath}\"", workDir)
                        : new ProcessStartInfo("certutil.exe", $"-addstore {storeName} \"{certPath}\"")
                            { UseShellExecute = true, Verb = "runas", WorkingDirectory = workDir };
                    if (psi == null) return;   // PsExec missing (message already shown)
                    var pr = Process.Start(psi);
                    pr?.WaitForExit(60000);
                    addOk = pr != null && pr.ExitCode == 0;
                    if (!addOk && pr != null) addErr = $"certutil exit code {pr.ExitCode}";
                }
                catch (Exception iex) { addErr = iex.Message; }

                // Show the cert regardless of import result, so it is available for packaging.
                AddManualItem(new SnapItem
                {
                    Category    = "Certificate",
                    DisplayPath = subject,
                    Value       = storeLabel,
                });

                // A cert in a store is also a registry value (Blob under SystemCertificates\<store>\
                // Certificates\<thumbprint>) - surface it on the Registry tab and make it packageable.
                AddCertRegistry(storeName, thumbprint);

                SwitchToTab("Certificates");
                RefreshCurrentTab();
                UpdateSummary();

                string msg = addOk
                    ? $"Imported certificate '{subject}' into {storeLabel}."
                    : $"Added certificate '{subject}' to the list. Store import could not be confirmed ({addErr}). Run as administrator to import into {storeLabel}.";
                SvShowMessage("Source Validation - Install Certificate", msg, "ℹ️");
                SetFooterStatus(msg);
                LogAction($"Certificate import: {subject} [{storeLabel}] thumbprint {thumbprint} ok={addOk} {addErr}");
            }
            catch (Exception ex)
            {
                SvShowMessage("Source Validation - Install Certificate",
                    $"Could not import the certificate:\n{ex.Message}");
            }
        }

        /// <summary>Friendly cert name (CN / simple name), falling back to the full subject.</summary>
        private static string CertFriendlyName(X509Certificate2 c)
        {
            try
            {
                string cn = c.GetNameInfo(X509NameType.SimpleName, false);
                if (!string.IsNullOrWhiteSpace(cn)) return cn;
            }
            catch { }
            return string.IsNullOrWhiteSpace(c.Subject) ? c.Thumbprint : c.Subject;
        }

        /// <summary>
        /// Surfaces the certificate's registry value on the Registry tab. A machine cert is stored as
        /// a Blob (REG_BINARY) under HKLM\SOFTWARE\Microsoft\SystemCertificates\&lt;store&gt;\
        /// Certificates\&lt;thumbprint&gt;, so reading that key recreates the cert's registry form
        /// (which also makes it packageable via the registry). Returns true if any row was added.
        /// </summary>
        private bool AddCertRegistry(string storeName, string thumbprint)
        {
            if (string.IsNullOrEmpty(thumbprint)) return false;
            string sub     = $@"SOFTWARE\Microsoft\SystemCertificates\{storeName}\Certificates\{thumbprint}";
            string fullKey = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\SystemCertificates\{storeName}\Certificates\{thumbprint}";
            bool any = false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(sub);
                if (key == null) return false;
                foreach (string valName in key.GetValueNames())
                {
                    object? raw = key.GetValue(valName, null,
                        Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (raw == null) continue;   // e.g. unset (Default)
                    var kind = key.GetValueKind(valName);

                    var reg = new SnapItem
                    {
                        Category    = "Registry",
                        DisplayPath = fullKey,
                        Value       = string.IsNullOrEmpty(valName) ? "(Default)" : valName,
                        Data        = FormatRegValue(raw, kind),
                        DataType    = RegKindToString(kind),
                    };
                    string k = reg.Category + "|" + reg.DisplayPath + "|" + reg.Value;
                    if (!_allItems.ContainsKey(k)) { _allItems[k] = reg; any = true; }
                }
                if (any) LogAction($"Certificate registry captured: {fullKey}");
            }
            catch { }
            return any;
        }

        /// <summary>
        /// Chooses the target certificate store from the cert's own attributes:
        ///  - a code-signing (publisher) cert -> TrustedPublisher, so signed packages install
        ///    without the "do you want to trust this publisher?" prompt;
        ///  - a CA / root cert -> Root;
        ///  - anything else -> Root (a safe default).
        /// </summary>
        private static string PickCertStore(X509Certificate2 cert)
        {
            const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
            bool codeSigning = false, isCA = false;
            try
            {
                foreach (var ext in cert.Extensions)
                {
                    if (ext is X509EnhancedKeyUsageExtension eku)
                        foreach (var oid in eku.EnhancedKeyUsages)
                            if (oid.Value == CodeSigningOid) codeSigning = true;
                    if (ext is X509BasicConstraintsExtension bc && bc.CertificateAuthority)
                        isCA = true;
                }
            }
            catch { }

            if (codeSigning) return "TrustedPublisher";
            if (isCA)        return "Root";
            return "Root";
        }

        /// <summary>
        /// The certificate store to import into: the explicit choice from the store dropdown, or -
        /// when "Auto" is selected - the store inferred from the certificate (PickCertStore).
        /// </summary>
        private string ResolveCertStore(X509Certificate2 cert)
        {
            string sel = (CmbCertStore?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto";
            return (string.IsNullOrEmpty(sel) || sel.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                ? PickCertStore(cert)
                : sel;
        }

        /// <summary>Source path changed: show the cert-store dropdown only for .cer/.crt sources.</summary>
        private void TxtSourceFile_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CmbCertStore == null) return;
            string p = TxtSourceFile?.Text ?? "";
            bool isCert = p.EndsWith(".cer", StringComparison.OrdinalIgnoreCase)
                       || p.EndsWith(".crt", StringComparison.OrdinalIgnoreCase);
            CmbCertStore.Visibility = isCert ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Load items from qasnap ────────────────────────────────
        /// <summary>Sets the Source Path field from an external source (e.g. Source Download feature).</summary>
        public void SetSourceFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            Dispatcher.Invoke(() =>
            {
                if (TxtSourceFile != null)
                    TxtSourceFile.Text = filePath;
            });
        }

        /// <summary>
        /// Pre-fills the source path textbox and triggers Execute.
        /// After capture stops, fires onCreatePackage (if doCreatePackage) then
        /// onUninstall (if doUninstall) in sequence.
        /// </summary>
        public void SetSourcePathAndExecute(
            string          sourcePath,
            string          uninstallCmd,
            string          downloadedFilePath  = "",
            bool            doCreatePackage     = false,
            bool            doUninstall         = false,
            Action<string>? onCreatePackage     = null,
            Action<string>? onUninstall         = null,
            string          appName             = "",
            string          appVendor           = "",
            string          appVersion          = "")
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return;

            // Store callbacks for use in StopCaptureAsync
            _postInstallFilePath     = downloadedFilePath;
            _postInstallDoCreatePkg  = doCreatePackage;
            _postInstallDoUninstall  = doUninstall;
            _postInstallUninstallCmd = uninstallCmd;
            _postInstallAppName      = appName;
            _postInstallAppVendor    = appVendor;
            _postInstallAppVersion   = appVersion;
            _onCreatePackageCallback = onCreatePackage;
            _onUninstallCallback     = onUninstall;

            Dispatcher.Invoke(() =>
            {
                if (TxtSourceFile != null)
                    TxtSourceFile.Text = sourcePath;

                // Simulate the user clicking Execute — reuses all existing
                // path-parsing and validation logic in BtnExecute_Click.
                BtnExecute_Click(this, new System.Windows.RoutedEventArgs());
            });
        }

        /// <summary>
        /// Runs a silent uninstall command without capture (post-package step).
        /// Called after Create Package completes in the sequential flow.
        /// </summary>
        /// <summary>
        /// Searches all three standard Uninstall registry hives for the app's
        /// UninstallString, falling back to the provided uninstallCmd if not found.
        /// Hives checked (in order):
        ///   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\
        ///   HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\
        ///   HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\
        /// </summary>
        private static string? FindUninstallStringFromRegistry(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return null;

            string[] hivePaths =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            // Search HKLM hives
            foreach (var hivePath in hivePaths)
            {
                try
                {
                    using var hive = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(hivePath);
                    if (hive == null) continue;
                    var result = SearchUninstallHive(hive, appName);
                    if (result != null) return result;
                }
                catch { }
            }

            // Search HKCU hive
            try
            {
                using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (hkcu != null)
                {
                    var result = SearchUninstallHive(hkcu, appName);
                    if (result != null) return result;
                }
            }
            catch { }

            return null;
        }

        private static string? SearchUninstallHive(Microsoft.Win32.RegistryKey hive, string appName)
        {
            foreach (var subKeyName in hive.GetSubKeyNames())
            {
                try
                {
                    using var sub = hive.OpenSubKey(subKeyName);
                    if (sub == null) continue;

                    var displayName = sub.GetValue("DisplayName") as string ?? "";
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    // Fuzzy match — app name contained in DisplayName or vice versa
                    if (displayName.IndexOf(appName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        appName.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var uninstallString = sub.GetValue("UninstallString") as string;
                        if (!string.IsNullOrWhiteSpace(uninstallString))
                            return uninstallString;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Runs a silent uninstall. If uninstallCmd is empty or just switches,
        /// looks up the actual uninstaller path from the Windows registry first.
        /// </summary>
        public void RunSilentUninstall(string uninstallCmd, string appName = "")
        {
            try
            {
                // Try to find the real uninstall path from the registry
                string? registryUninstallPath = FindUninstallStringFromRegistry(appName);

                string exePath;
                string exeArgs;

                if (!string.IsNullOrWhiteSpace(registryUninstallPath))
                {
                    // Registry found — use the uninstaller path directly
                    // Strip surrounding quotes if present
                    string cleanPath = registryUninstallPath.Trim().Trim('"');

                    exePath = cleanPath;
                    exeArgs = uninstallCmd.Trim(); // silent switches from JSON e.g. /S
                }
                else if (!string.IsNullOrWhiteSpace(uninstallCmd))
                {
                    // No registry entry — treat the whole uninstallCmd as path + args
                    var (fp, args) = SplitPathAndArgs(uninstallCmd);
                    exePath = fp;
                    exeArgs = args;
                }
                else
                {
                    return; // nothing to run
                }

                if (string.IsNullOrWhiteSpace(exePath)) return;

                // Run the uninstaller directly — no cmd.exe wrapper
                // UAC prompt will show the actual uninstaller path, not cmd.exe
                var psi = new System.Diagnostics.ProcessStartInfo(exePath, exeArgs)
                {
                    UseShellExecute = true,
                    Verb            = "runas",
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }

        public void LoadSnapItems(string snapPath)
        {
            if (!File.Exists(snapPath)) return;
            try
            {
                string rawJson = File.ReadAllText(snapPath);
                var snap = JsonSerializer.Deserialize<QaSnapshot>(
                    rawJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (snap == null) return;

                // A .svr saved by Create MSI records which items were unchecked in an
                // "ExcludedPaths" array (keys: "Category|DisplayPath|Value"). Read it directly so
                // the QaSnapshot model needn't change. Original capture .svrs have no such
                // field, so every item defaults to included (unchanged behaviour).
                var excludedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Manually-added file map: install-destination (DisplayPath) → SourceOnDisk.
                var manualFileSrc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // Manually-added registry keys ("Category|DisplayPath|Value") to flag IsManual.
                var manualRegKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                    if (doc.RootElement.TryGetProperty("ExcludedPaths", out var ep)
                        && ep.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var e in ep.EnumerateArray())
                        {
                            string? k = e.GetString();
                            if (!string.IsNullOrEmpty(k)) excludedKeys.Add(k);
                        }
                    }
                    if (doc.RootElement.TryGetProperty("ManualFiles", out var mf)
                        && mf.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var e in mf.EnumerateArray())
                        {
                            string dest = e.TryGetProperty("Dest", out var d) ? (d.GetString() ?? "") : "";
                            string srcv = e.TryGetProperty("Source", out var sv2) ? (sv2.GetString() ?? "") : "";
                            if (!string.IsNullOrEmpty(dest)) manualFileSrc[dest] = srcv;
                        }
                    }
                    if (doc.RootElement.TryGetProperty("ManualRegistry", out var mr)
                        && mr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var e in mr.EnumerateArray())
                        {
                            string kk = e.TryGetProperty("Key",   out var k2) ? (k2.GetString() ?? "") : "";
                            string vv = e.TryGetProperty("Value", out var v2) ? (v2.GetString() ?? "") : "";
                            if (!string.IsNullOrEmpty(kk)) manualRegKeys.Add("Registry|" + kk + "|" + vv);
                        }
                    }
                    // Restore the package app info (App Name / Vendor / Version) saved in the qasnap,
                    // so the Summary shows it again after re-import.
                    {
                        string an = doc.RootElement.TryGetProperty("AppName",    out var a1) ? (a1.GetString() ?? "") : "";
                        string av = doc.RootElement.TryGetProperty("AppVendor",  out var a2) ? (a2.GetString() ?? "") : "";
                        string ar = doc.RootElement.TryGetProperty("AppVersion", out var a3) ? (a3.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(an) || !string.IsNullOrWhiteSpace(av) || !string.IsNullOrWhiteSpace(ar))
                            _packageAppInfo = (an, av, ar);
                    }
                }
                catch { }

                void Add(SnapItem item)
                {
                    // Is this a manually-added item? Those were deliberately chosen by the user and
                    // must NOT be dropped by the capture-noise exclusion list on re-import.
                    bool isManualFile = item.Category == "File" && manualFileSrc.ContainsKey(item.DisplayPath);
                    string keyEarly0  = item.Category + "|" + item.DisplayPath + "|" + item.Value;
                    bool isManualReg  = item.Category == "Registry" && manualRegKeys.Contains(keyEarly0);
                    bool isManual     = isManualFile || isManualReg;

                    if (isManual || !IsExcluded(item))
                    {
                        // Manually-added files: restore SourceOnDisk and skip EnrichFromDisk (their
                        // DisplayPath is the install destination, not a real on-disk path).
                        if (isManualFile)
                        {
                            item.SourceOnDisk = manualFileSrc[item.DisplayPath];
                            item.IsManual     = true;
                        }
                        else
                        {
                            item.EnrichFromDisk();
                        }
                        if (isManualReg) item.IsManual = true;

                        // Restore saved include/exclude state.
                        string key = item.Category + "|" + item.DisplayPath + "|" + item.Value;
                        if (excludedKeys.Contains(key)) item.IsIncluded = false;
                        _allItems[key] = item;
                    }
                }

                foreach (var f in snap.Files ?? new())
                    if (!f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        Add(new SnapItem { Category = "File", DisplayPath = f });
                foreach (var f in snap.Folders ?? new())
                    Add(new SnapItem { Category = "Folder",   DisplayPath = f });
                foreach (var sc in snap.Shortcuts ?? new())
                    Add(new SnapItem { Category = "Shortcut", DisplayPath = sc });
                foreach (var r in snap.Registry ?? new())
                    Add(new SnapItem { Category = "Registry", DisplayPath = r.Key,
                        Value = r.Value, Data = r.Data, DataType = r.Type });
                foreach (var sv in snap.Services ?? new())
                    Add(new SnapItem { Category = "Service", DisplayPath = sv.Name,
                        Value = sv.DisplayName, Data = sv.StartType,
                        DataType = sv.Binary,
                        ServiceStatus = GetServiceStatus(sv.Name) });
                foreach (var c in snap.Certificates ?? new())
                    Add(new SnapItem { Category = "Certificate", DisplayPath = c });
            }
            catch { }
        }

        private static string GetServiceStatus(string name)
        {
            try { using var sc = new ServiceController(name); return sc.Status.ToString(); }
            catch { return "Unknown"; }
        }

        // ── Data-bound registry tree: apply a node's check state to the underlying SnapItems ──
        // Called ONCE per user toggle (from RegTreeNode.IsChecked). Updates IsIncluded on every
        // captured item under the node's path in a single pass, then refreshes the summary
        // (debounced). No visual-tree walking — this is why large trees stay fast. Works for any
        // path-based category (registry keys, file/folder paths, shortcuts) since matching is by
        // path prefix.
        public void ApplyRegNodeCheck(RegTreeNode node, bool included)
        {
            string nodeKey = node.FullPath;            // already upper-cased
            if (!string.IsNullOrEmpty(nodeKey))
            {
                // Normalise to single backslashes (file tree keys can carry a trailing '\').
                string baseKey = nodeKey.TrimEnd('\\');
                while (baseKey.Contains("\\\\")) baseKey = baseKey.Replace("\\\\", "\\");
                string prefix = baseKey + "\\";
                foreach (var si in _allItems.Values)
                {
                    string dp = si.DisplayPath.ToUpperInvariant();
                    while (dp.Contains("\\\\")) dp = dp.Replace("\\\\", "\\");
                    dp = dp.TrimEnd('\\');
                    if (dp == baseKey || dp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        si.IsIncluded = included;
                }
            }
            foreach (var it in node.Items) it.IsIncluded = included;

            LogAction($"{(included ? "Include" : "Exclude")}: {nodeKey}");
            ScheduleSummaryRefresh();
            AutoSaveQasnap();
        }

        // Debounced summary refresh so a cascade that toggles many model nodes recomputes the
        // summary once, not per node.
        private System.Windows.Threading.DispatcherTimer? _summaryTimer;
        private void ScheduleSummaryRefresh()
        {
            if (_summaryTimer == null)
            {
                _summaryTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(60) };
                _summaryTimer.Tick += (_, _) => { _summaryTimer!.Stop(); UpdateSummary(); };
            }
            _summaryTimer.Stop();
            _summaryTimer.Start();
        }

        // ═══════════════════════════════════════════════════════════
        //  CASCADE include/exclude on a tree node
        //  Updates BOTH the SnapItem data AND the checkbox UI in every child
        // ═══════════════════════════════════════════════════════════
        private void SetIncludedCascade(TreeViewItem node, bool included)
        {
            // Guard the ENTIRE cascade (data + every child checkbox) as one atomic operation.
            // Setting a child checkbox raises its own Checked/Unchecked event; without holding the
            // guard for the whole operation, each child would re-enter SetIncludedCascade and
            // re-scan all items — an avalanche that froze the app for ~20s on large keys. Holding
            // the guard means those re-entrant events return immediately.
            bool outer = !_cascading;
            _cascading = true;
            try
            {
                // Data propagation across ALL tabs runs ONCE for this node's subtree (by path
                // prefix), not per descendant.
                if (node.Tag is string nodeKeyRaw && !string.IsNullOrEmpty(nodeKeyRaw))
                {
                    static string Norm(string p)
                    {
                        while (p.Contains("\\\\")) p = p.Replace("\\\\", "\\");
                        return p.TrimEnd('\\').ToUpperInvariant();
                    }
                    string nodeKey = Norm(nodeKeyRaw);
                    string prefix  = nodeKey + "\\";
                    foreach (var si in _allItems.Values)
                    {
                        string dp = Norm(si.DisplayPath);
                        if (dp == nodeKey || dp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            si.IsIncluded = included;
                    }
                }
                // Update this node's own SnapItem (leaf nodes have no Tag prefix above).
                if (node.DataContext is SnapItem ownItem)
                    ownItem.IsIncluded = included;

                LogAction($"{(included ? "Include" : "Exclude")}: {(node.Tag as string) ?? (node.DataContext as SnapItem)?.DisplayPath ?? ""}");

                // Cascade the checkbox UI through the subtree (no per-node data scan, and the
                // guard above suppresses the resulting child events).
                SetCheckboxCascadeUiOnly(node, included);
            }
            finally
            {
                if (outer) _cascading = false;
            }
        }

        // Cascades only the checkbox UI state down a subtree. Does NOT scan _allItems — the data
        // was already propagated by prefix in SetIncludedCascade. Assumes _cascading is already
        // held true by the caller, so child checkbox events are suppressed.
        private void SetCheckboxCascadeUiOnly(TreeViewItem node, bool included)
        {
            if (node.DataContext is SnapItem item)
                item.IsIncluded = included;

            if (node.Header is StackPanel sp)
            {
                foreach (UIElement el in sp.Children)
                {
                    if (el is CheckBox cb)
                    {
                        cb.IsChecked = included;
                        break;
                    }
                }
            }

            foreach (TreeViewItem child in node.Items)
                SetCheckboxCascadeUiOnly(child, included);
        }

        // ── Walk UP the tree and check all ancestor nodes ──────────
        // Called when a child is checked — ensures all parent folders/keys
        // are also checked so the path is fully included.
        private void CheckParentsUpward(TreeViewItem node)
        {
            bool outer = !_cascading;
            _cascading = true;
            try
            {
                var parent = node.Parent as TreeViewItem;
                while (parent != null)
                {
                    // Update the parent SnapItem data
                    if (parent.DataContext is SnapItem parentItem)
                        parentItem.IsIncluded = true;

                    // Update the parent checkbox UI
                    if (parent.Header is System.Windows.Controls.StackPanel sp)
                    {
                        foreach (UIElement el in sp.Children)
                        {
                            if (el is CheckBox cb)
                            {
                                cb.IsChecked = true;
                                break;
                            }
                        }
                    }
                    parent = parent.Parent as TreeViewItem;
                }
            }
            finally
            {
                if (outer) _cascading = false;
            }
        }

        // ── Tab switching ─────────────────────────────────────────
        private void Tab_Click(object s, RoutedEventArgs e)
        {
            if (s is not Button btn) return;
            _currentTab = btn.Tag?.ToString() ?? "All";
            _summaryFilter = ""; // clear category filter when switching tabs manually

            // Clear search when switching tabs
            if (!string.IsNullOrEmpty(_searchText))
            {
                TxtSearch.Text  = "";
                _searchText     = "";
                _searchMatchIdx = -1;
                _searchMatches.Clear();
                _treeMatchTbs.Clear();
                TxtSearchCount.Text = "";
                ClearAllWordHighlights();
            }

            foreach (Button tb in new[] { TabAll, TabFiles, TabShortcuts, TabRegistry,
                                          TabEnv, TabCerts, TabServices, TabFirewall, TabDrivers, TabOdbc, TabTasks, TabProcess, TabApps,
                                          TabTemp, TabEvents })
                tb.Style = (Style)FindResource("TabBtnStyle");
            btn.Style = (Style)FindResource("TabBtnActiveStyle");
            _selectedTreeNode = null;
            _selectedListItem  = null;
            _selectedRegNode   = null;
            UpdateIncludeExcludeButtons();
            RefreshCurrentTab();
        }

        // Programmatically switch to a tab (e.g. after Add Registry, show the Registry tab).
        private void SwitchToTab(string tab)
        {
            var map = new Dictionary<string, Button>
            {
                ["All"] = TabAll, ["Files"] = TabFiles, ["Shortcuts"] = TabShortcuts,
                ["Registry"] = TabRegistry, ["Environment"] = TabEnv, ["Certificates"] = TabCerts,
                ["Services"] = TabServices, ["Firewall"] = TabFirewall, ["Drivers"] = TabDrivers, ["Odbc"] = TabOdbc, ["SchedTask"] = TabTasks, ["Process"] = TabProcess, ["InstalledApp"] = TabApps,
                ["Temp"] = TabTemp, ["Events"] = TabEvents,
            };
            _currentTab = tab;
            _summaryFilter = "";
            foreach (Button tb in new[] { TabAll, TabFiles, TabShortcuts, TabRegistry,
                                          TabEnv, TabCerts, TabServices, TabFirewall, TabDrivers, TabOdbc, TabTasks, TabProcess, TabApps,
                                          TabTemp, TabEvents })
                tb.Style = (Style)FindResource("TabBtnStyle");
            if (map.TryGetValue(tab, out var active) && active != null)
                active.Style = (Style)FindResource("TabBtnActiveStyle");
            _selectedTreeNode = null; _selectedListItem = null; _selectedRegNode = null;
            UpdateIncludeExcludeButtons();
            RefreshCurrentTab();
        }
        /// File/Folder/Shortcut: hide Value and Data columns.
        /// Registry: hide Date and Size columns.
        /// No filter (show all): show all columns.
        /// Column width=0 is the WPF way to hide a GridViewColumn (no Visibility property).
        /// </summary>
        /// <summary>
        /// Gives one column in each of the other tabs whatever width the rest leave over, so every
        /// grid fills the window instead of stopping short. This is what UpdateAllTabColumns already
        /// does for the All tab (wPath = lvWidth - fixedTotal); the other tabs never got it.
        ///
        /// The absorbing column is the long one, not simply the last: stretching "State" or "Size"
        /// would look wrong. So it is named per grid rather than picked by index.
        ///
        /// Must be done in code. GridViewColumn derives from DependencyObject, NOT FrameworkElement,
        /// so it sits outside the visual tree and a {RelativeSource AncestorType=ListView} binding on
        /// its Width cannot resolve - it fails silently and the column just auto-sizes.
        /// </summary>
        private void FitTabColumns()
        {
            // Deferred so ActualWidth is valid: a tab that has just been switched to has not been
            // laid out yet, and a collapsed one measures 0.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                Fit(LvEnv,      ColEnvValue);
                Fit(LvCerts,    ColCertName);
                Fit(LvServices, ColSvcDisplay);
                Fit(LvFirewall, ColFwRule);
                Fit(LvDrivers,  ColDrvInf);
                Fit(LvOdbc,     ColOdbcDriver);
                Fit(LvTasks,    ColTaskAuthor);
                Fit(LvProcess,  ColProcCmdLine);   // command lines are the long ones, so they absorb
                Fit(LvApps,     ColAppVersion);
                Fit(LvTemp,     ColTempPath);
                Fit(LvEvents,   ColEvtMessage);
            }));

            static void Fit(ListView lv, GridViewColumn absorb)
            {
                if (lv?.View is not GridView gv || absorb == null) return;
                double lvWidth = lv.ActualWidth;
                if (lvWidth <= 50) return;          // collapsed tab - nothing to measure against

                double used = 0;
                foreach (var c in gv.Columns)
                    if (!ReferenceEquals(c, absorb)) used += c.ActualWidth;

                double remaining = lvWidth - used - ScrollBarAllowance(lv);
                if (remaining >= 80) absorb.Width = remaining;
            }
        }

        /// <summary>
        /// Width to keep back for the vertical scrollbar - but ONLY when one is actually on screen.
        /// Reserving it unconditionally is what left a dead strip at the right edge of every grid
        /// whose rows fit without scrolling. Also allows 2px for the grid's own border.
        /// </summary>
        private static double ScrollBarAllowance(DependencyObject listView)
        {
            var sv = FindScrollViewer(listView);
            bool showing = sv != null
                        && sv.ComputedVerticalScrollBarVisibility == Visibility.Visible;
            return (showing ? SystemParameters.VerticalScrollBarWidth : 0) + 2;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv) return sv;
                if (FindScrollViewer(child) is ScrollViewer found) return found;
            }
            return null;
        }

        private void UpdateAllTabColumns()
        {
            // Defer to ensure LvAll.ActualWidth is valid after layout
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                const double W_CB     = 28;
                const double W_VALUE  = 120;
                const double W_DATA   = 160;
                const double W_TYPE   = 140;
                const double W_DATE   = 130;
                const double W_SIZE   = 75;
                const double W_ITEMS  = 75;
                const double W_OPEN   = 40;

                bool isFileLike     = _summaryFilter is "File" or "Folder" or "Shortcut";
                bool isRegistryOnly = _summaryFilter == "Registry";
                bool isServiceOnly  = _summaryFilter == "Service";
                bool isOther        = !string.IsNullOrEmpty(_summaryFilter)
                                      && !isFileLike && !isRegistryOnly && !isServiceOnly;

                double wValue = isFileLike ? 0 : W_VALUE;
                double wData  = isFileLike ? 0 : W_DATA;
                double wType  = W_TYPE;
                double wDate  = (isRegistryOnly || isServiceOnly || isOther) ? 0 : W_DATE;
                double wSize  = (isRegistryOnly || isServiceOnly || isOther) ? 0 : W_SIZE;
                double wItems = W_ITEMS;
                double wOpen  = W_OPEN;

                double lvWidth    = LvAll.ActualWidth > 50 ? LvAll.ActualWidth : 900;
                // ScrollBarAllowance instead of a flat W_SCROLL: with few enough rows to fit without
                // scrolling, the fixed 20px reserve just left a dead strip at the right edge.
                double fixedTotal = W_CB + wValue + wData + wType + wDate + wSize + wItems + wOpen
                                  + ScrollBarAllowance(LvAll);
                double wPath      = Math.Max(180, lvWidth - fixedTotal);

                if (ColPath  != null) ColPath.Width  = wPath;
                if (ColValue != null) ColValue.Width = wValue;
                if (ColData  != null) ColData.Width  = wData;
                if (ColType  != null) ColType.Width  = wType;
                if (ColDate  != null) ColDate.Width  = wDate;
                if (ColSize  != null) ColSize.Width  = wSize;
                if (ColItems != null) ColItems.Width = wItems;
                if (ColOpen  != null) ColOpen.Width  = wOpen;
            }));
        }

        private void RefreshCurrentTab()
        {
            LvAll.Visibility       = Visibility.Collapsed;
            TvFiles.Visibility     = Visibility.Collapsed;
            TvShortcuts.Visibility = Visibility.Collapsed;
            TvRegistry.Visibility  = Visibility.Collapsed;
            LvEnv.Visibility       = Visibility.Collapsed;
            LvCerts.Visibility     = Visibility.Collapsed;
            LvServices.Visibility  = Visibility.Collapsed;
            LvFirewall.Visibility  = Visibility.Collapsed;
            LvDrivers.Visibility   = Visibility.Collapsed;
            LvOdbc.Visibility      = Visibility.Collapsed;
            LvTasks.Visibility     = Visibility.Collapsed;
            LvProcess.Visibility   = Visibility.Collapsed;
            LvApps.Visibility      = Visibility.Collapsed;
            LvTemp.Visibility      = Visibility.Collapsed;
            LvEvents.Visibility    = Visibility.Collapsed;

            string search = _searchText.ToLowerInvariant();

            // Update column visibility for All tab based on active filter
            UpdateAllTabColumns();
            // ...and fit the absorbing column of whichever other tab is now visible.
            FitTabColumns();

            switch (_currentTab)
            {
                case "All":
                    LvAll.Visibility = Visibility.Visible;
                    PopulateFlat(LvAll, null, search);
                    break;

                case "Files":
                    TvFiles.Visibility = Visibility.Visible;
                    // Always rebuild when switching to Files — ensures checkboxes match IsIncluded
                    // (user may have checked/unchecked items in the All tab)
                    PopulateFileTree(TvFiles, search);
                    break;

                case "Shortcuts":
                    TvShortcuts.Visibility = Visibility.Visible;
                    PopulateShortcutTree(TvShortcuts, search);
                    break;

                case "Registry":
                    TvRegistry.Visibility = Visibility.Visible;
                    PopulateRegistryTree(TvRegistry, search);
                    break;

                case "Environment":  LvEnv.Visibility      = Visibility.Visible; PopulateFlat(LvEnv, "EnvVar", search);        break;
                case "Certificates": LvCerts.Visibility    = Visibility.Visible; PopulateFlat(LvCerts, "Certificate", search); break;
                case "Services":     LvServices.Visibility = Visibility.Visible; PopulateServices(search);                    break;
                case "Firewall":     LvFirewall.Visibility = Visibility.Visible; PopulateFlat(LvFirewall, "Firewall", search); break;
                case "Drivers":      LvDrivers.Visibility  = Visibility.Visible; PopulateFlat(LvDrivers, "Driver", search);   break;
                case "Odbc":         LvOdbc.Visibility     = Visibility.Visible; PopulateFlat(LvOdbc, "Odbc", search);        break;
                case "SchedTask":    LvTasks.Visibility    = Visibility.Visible; PopulateFlat(LvTasks, "SchedTask", search);  break;
                case "Process":      LvProcess.Visibility  = Visibility.Visible; PopulateFlat(LvProcess, "Process", search);  break;
                case "InstalledApp": LvApps.Visibility     = Visibility.Visible; PopulateFlat(LvApps, "InstalledApp", search); break;

                // VIEW ONLY. Reads _tempItems, never _allItems, and no SelectionChanged handler is
                // wired - so nothing here can feed _selectedListItem or the Include/Exclude buttons.
                case "Temp":         LvTemp.Visibility     = Visibility.Visible; PopulateTemp(search);                        break;

                // VIEW ONLY. Reads _eventItems (EventLogRow, not SnapItem) - nothing here can be
                // included, saved or packaged.
                case "Events":       LvEvents.Visibility   = Visibility.Visible; PopulateEvents(search);                      break;
            }
            if (_searchText.Length > 0)
            {
                // BuildSearchMatches populates _treeMatchTbs for tree tabs
                // Must run AFTER Populate*Tree so _treeLabelTbs is populated
                BuildSearchMatches();
                UpdateSearchCount();
                // Apply tree highlights after rebuild (deferred so nodes are rendered)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(ApplyTreeHighlights));
            }
            else
            {
                ClearTreeHighlights();
            }
        }

        // ── Column sorting ────────────────────────────────────────────────────────────────
        // Clicking a column header sorts by that column and toggles direction. State is held per
        // ListView because PopulateFlat re-runs on every 800ms refresh tick during a capture - the
        // chosen order has to survive that, or the list would snap back to its default while the
        // source is still installing.
        //
        // The sort key is taken from the column's own DisplayMemberBinding, so no column needs to
        // declare anything: any column bound to a SnapItem property is sortable, and template
        // columns (the checkbox, the Open icon) are simply ignored.
        private readonly Dictionary<ListView, (string Prop, bool Ascending)> _sortState = new();

        private void GridHeader_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not System.Windows.Controls.GridViewColumnHeader h) return;
                if (h.Role == System.Windows.Controls.GridViewColumnHeaderRole.Padding) return;
                if (h.Column?.DisplayMemberBinding is not System.Windows.Data.Binding b) return;

                string prop = b.Path?.Path ?? "";
                if (prop.Length == 0) return;

                // Walk up to the owning ListView.
                System.Windows.DependencyObject? d = h;
                while (d != null && d is not ListView) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
                if (d is not ListView lv) return;

                bool asc = true;
                if (_sortState.TryGetValue(lv, out var cur) && cur.Prop == prop) asc = !cur.Ascending;
                _sortState[lv] = (prop, asc);

                RefreshCurrentTab();
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[Sort] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
            }
        }

        /// <summary>
        /// Applies the chosen column order, or the default (category then name) when none is set.
        ///
        /// Values are compared numerically when every value in the column parses as a number, so
        /// PID sorts 2, 10, 1888 rather than "10" before "2". Everything else compares as text,
        /// case-insensitively. That also handles the Started column: it is written as HH:mm:ss, so
        /// text order is chronological.
        /// </summary>
        private IEnumerable<SnapItem> ApplySort(ListView lv, IEnumerable<SnapItem> rows)
        {
            if (!_sortState.TryGetValue(lv, out var st))
                return rows.OrderBy(i => i.Category).ThenBy(i => i.DisplayPath);

            var pi = typeof(SnapItem).GetProperty(st.Prop);
            if (pi == null)
                return rows.OrderBy(i => i.Category).ThenBy(i => i.DisplayPath);

            var list = rows.ToList();
            string Key(SnapItem i) { try { return pi.GetValue(i)?.ToString() ?? ""; } catch { return ""; } }

            bool numeric = list.Count > 0 && list.All(i =>
            {
                string v = Key(i);
                return v.Length == 0 || double.TryParse(v, out _);
            });

            if (numeric)
            {
                double Num(SnapItem i) => double.TryParse(Key(i), out var d) ? d : double.MinValue;
                return st.Ascending ? list.OrderBy(Num) : list.OrderByDescending(Num);
            }
            return st.Ascending
                ? list.OrderBy(Key, StringComparer.OrdinalIgnoreCase)
                : list.OrderByDescending(Key, StringComparer.OrdinalIgnoreCase);
        }

        private void PopulateFlat(ListView lv, string? category, string search)
        {
            // When category is null (All tab), apply optional summary panel filter
            string? effectiveCategory = category ?? (_summaryFilter.Length > 0 ? _summaryFilter : null);
            // Reset path cell TextBlock map — cells are recreated when ItemsSource changes
            if (lv == LvAll)
            {
                _pathCellTbs.Clear(); _valueCellTbs.Clear(); _dataCellTbs.Clear();
                _typeCellTbs.Clear(); _dateCellTbs.Clear();  _sizeCellTbs.Clear();
                _itemsCellTbs.Clear();
            }
            lv.ItemsSource = ApplySort(lv, _allItems.Values
                .Where(i => (effectiveCategory == null || i.Category == effectiveCategory)
                         // Folders are not shown as flat rows in the All tab — the folder
                         // hierarchy is visible only in the Files tab tree.
                         && (lv != LvAll || i.Category != "Folder")
                         // Process and InstalledApp belong to view-only tabs, like Temp and
                         // Events (which never reach here because they live in their own
                         // stores). Rows with checkboxes in All implied they were packagable
                         // and confused people, so the unfiltered All hides them. A click on
                         // the Summary panel's Process / InstalledApp row is an explicit ask
                         // (effectiveCategory is set) and still shows them.
                         && (lv != LvAll || effectiveCategory != null
                             || (i.Category != "Process" && i.Category != "InstalledApp"))
                         && MatchesSearch(i, search)))
                .ToList();
            // Wire SelectionChanged so Include/Exclude buttons enable only when a row is selected
            lv.SelectionChanged -= ListView_SelectionChanged;
            lv.SelectionChanged += ListView_SelectionChanged;
        }

        private void PopulateServices(string search)
        {
            LvServices.ItemsSource = _allItems.Values
                .Where(i => i.Category == "Service" && MatchesSearch(i, search))
                .OrderBy(i => i.DisplayPath).ToList();
            LvServices.SelectionChanged -= ListView_SelectionChanged;
            LvServices.SelectionChanged += ListView_SelectionChanged;
        }

        /// <summary>
        /// Temp tab - VIEW ONLY. Lists what the installer wrote under
        /// C:\Users\&lt;user&gt;\AppData\Local\Temp during the capture.
        ///
        /// Sourced from _tempItems, never _allItems, so these rows cannot be included, cannot be
        /// saved to the .svr, and cannot reach the MSI. SelectionChanged is deliberately NOT wired
        /// (unlike every other flat tab): that handler is what sets _selectedListItem and enables
        /// Include / Exclude, so leaving it off keeps those buttons dead on this tab.
        ///
        /// The search box still filters the list. Only a live capture fills this - an imported .svr
        /// has no temp data, because temp items are never written to it.
        /// </summary>
        private void PopulateTemp(string search)
        {
            LvTemp.ItemsSource = _tempItems.Values
                .Where(i => MatchesSearch(i, search))
                .OrderBy(i => i.DisplayPath).ToList();
        }

        /// <summary>
        /// Event Monitor tab - VIEW ONLY. Application-log entries written while the source ran.
        /// Sourced from _eventItems (EventLogRow, not SnapItem), so nothing here can be included,
        /// saved to the .svr or packaged. SelectionChanged is deliberately not wired, and
        /// UpdateIncludeExcludeButtons hard-locks Include/Exclude on this tab.
        /// Newest first, since that is what you look at after a failed install.
        /// </summary>
        private void PopulateEvents(string search)
        {
            IEnumerable<EventLogRow> rows = _eventItems;
            if (!string.IsNullOrEmpty(search))
                rows = rows.Where(r =>
                    r.Source.Contains(search, StringComparison.OrdinalIgnoreCase)
                 || r.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                 || r.Level.Contains(search, StringComparison.OrdinalIgnoreCase)
                 || r.EventId.Contains(search, StringComparison.OrdinalIgnoreCase));
            LvEvents.ItemsSource = rows.Reverse().ToList();
        }

        /// <summary>
        /// Shared SelectionChanged handler for all flat ListView tabs.
        /// Updates _selectedListItem and enables/disables Include/Exclude buttons.
        /// </summary>
        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListView lv)
                _selectedListItem = lv.SelectedItem as SnapItem;
            UpdateIncludeExcludeButtons();
        }

        private static bool MatchesSearch(SnapItem i, string search) =>
            search.Length == 0
            || i.DisplayPath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
            || i.Value.IndexOf(search,       StringComparison.OrdinalIgnoreCase) >= 0
            || i.Data.IndexOf(search,        StringComparison.OrdinalIgnoreCase) >= 0;

        // ── File tree ─────────────────────────────────────────────
        private void PopulateFileTree(TreeView tv, string search)
        {
            // DATA-BOUND build (same approach as the registry tree) so the Files tab scales to
            // tens of thousands of items without freezing: build lightweight RegTreeNode objects
            // and bind via ItemsSource; the virtualized TreeView realizes only visible rows, and
            // check/uncheck mutates the model (+ a single prefix scan of _allItems), never the
            // visual tree.
            var items = _allItems.Values
                .Where(i => (i.Category == "File" || i.Category == "Shortcut" || i.Category == "Folder")
                         && MatchesSearch(i, search))
                .OrderBy(i => i.DisplayPath).ToList();

            // Precompute key prefixes that have an included descendant (for initial check state).
            var includedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fi in _allItems.Values)
            {
                if (!fi.IsIncluded) continue;
                if (fi.Category != "File" && fi.Category != "Shortcut" && fi.Category != "Folder") continue;
                string[] fp = fi.DisplayPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                string acc = "";
                for (int k = 0; k < fp.Length; k++)
                {
                    acc = k == 0 ? fp[0] : acc + "\\" + fp[k];
                    includedPrefixes.Add(acc.ToUpperInvariant());
                }
            }

            var roots   = new System.Collections.ObjectModel.ObservableCollection<RegTreeNode>();
            var nodeMap = new Dictionary<string, RegTreeNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                string[] parts = item.DisplayPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                string pathSoFar = "";
                RegTreeNode? parent = null;

                for (int i = 0; i < parts.Length; i++)
                {
                    pathSoFar = i == 0 ? parts[0] : pathSoFar + "\\" + parts[i];
                    bool isLeaf = (i == parts.Length - 1);
                    string nodeKey = pathSoFar.ToUpperInvariant();

                    if (!nodeMap.TryGetValue(nodeKey, out var node))
                    {
                        bool isFileLeaf     = isLeaf && item.Category == "File";
                        bool isShortcutLeaf = isLeaf && item.Category == "Shortcut";
                        string icon = isFileLeaf ? "📄" : isShortcutLeaf ? "🔗" : "📁";

                        node = new RegTreeNode
                        {
                            Name     = parts[i],
                            Icon     = icon,
                            FullPath = nodeKey,
                            OriginalPath = pathSoFar,   // real-case path for Add-dialog prefill
                            Owner    = this,
                            Parent   = parent,
                            IsLeaf   = isLeaf && item.Category != "Folder",
                            IsExpanded = false,
                        };
                        // Leaf nodes track their SnapItem so toggling updates it directly and the
                        // checkbox reflects the item's own state; folder/intermediate nodes use the
                        // precomputed prefix set.
                        if (isLeaf && item.Category != "Folder")
                        {
                            node.Items.Add(item);
                            node.SetCheckedCascade(item.IsIncluded);
                        }
                        else
                        {
                            node.SetCheckedCascade(includedPrefixes.Contains(nodeKey));
                        }

                        nodeMap[nodeKey] = node;
                        if (parent == null) roots.Add(node);
                        else parent.Children.Add(node);
                    }
                    parent = node;
                }
            }

            // Expand root nodes by default (drive roots / top folders).
            foreach (var r in roots) r.IsExpanded = true;

            tv.ItemsSource = roots;
            tv.SelectedItemChanged -= BoundTree_SelectedItemChanged;
            tv.SelectedItemChanged += BoundTree_SelectedItemChanged;
        }

        // ── Folders tab — empty folders captured during install ───────
        private void PopulateFolderTree(TreeView tv, string search)
        {
            var expandedKeys = GetExpandedKeys(tv);
            bool firstBuild  = tv.Items.Count == 0;
            tv.Items.Clear();
            _treeLabelTbs.Clear();  _treeLabelNodes.Clear(); // reset tree highlight registry on rebuild
            var folders = _allItems.Values
                .Where(i => i.Category == "Folder" && MatchesSearch(i, search))
                .OrderBy(i => i.DisplayPath)
                .ToList();

            if (folders.Count == 0)
            {
                var emptyNode = new TreeViewItem
                {
                    Header = new TextBlock
                    {
                        Text = "No folders captured.",
                        FontStyle = FontStyles.Italic,
                        Foreground = (Brush)(FindResource("TextMutedBrush") ?? Brushes.Gray),
                        FontSize = 11,
                        Margin = new Thickness(4)
                    }
                };
                tv.Items.Add(emptyNode);
                return;
            }

            var nodeMap = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in folders)
            {
                string[] parts = item.DisplayPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                string pathSoFar = "";
                TreeViewItem? parent = null;
                for (int i = 0; i < parts.Length; i++)
                {
                    pathSoFar = i == 0 ? parts[0] + "\\" : pathSoFar + "\\" + parts[i];
                    bool isLeaf = (i == parts.Length - 1);
                    string nodeKey = pathSoFar.ToUpperInvariant();
                    SnapItem? nodeItem = isLeaf ? item : null;
                    if (!nodeMap.TryGetValue(nodeKey, out var node))
                    {
                        string label = parts[i] + (i < parts.Length - 1 ? "\\" : "");
                        node = MakeTreeNode(label, nodeItem, "📁", isFolder: true,
                            registerHighlight: isLeaf && !string.IsNullOrEmpty(search),
                            parentPathForCheck: nodeItem == null ? pathSoFar : null);
                        node.Tag = nodeKey;
                        nodeMap[nodeKey] = node;
                        if (parent == null) tv.Items.Add(node);
                        else parent.Items.Add(node);
                    }
                    parent = node;
                }
            }

            tv.SelectedItemChanged -= TreeView_SelectedItemChanged;
            tv.SelectedItemChanged += TreeView_SelectedItemChanged;
            if (firstBuild || expandedKeys.Count == 0)
                foreach (TreeViewItem root in tv.Items) root.IsExpanded = true;
            else
                RestoreExpandedKeys(tv, expandedKeys, defaultExpand: false);
        }

        // ── Shortcut tree ─────────────────────────────────────────
        private void PopulateShortcutTree(TreeView tv, string search)
        {
            tv.Items.Clear();
            _treeLabelTbs.Clear();  _treeLabelNodes.Clear(); // reset tree highlight registry on rebuild
            var items = _allItems.Values
                .Where(i => i.Category == "Shortcut" && MatchesSearch(i, search)).ToList();
            foreach (var g in items.GroupBy(i => IOPath.GetDirectoryName(i.DisplayPath) ?? "")
                                    .OrderBy(g => g.Key))
            {
                var folderNode = MakeTreeNode(g.Key, null, "📁", isFolder: true,
                    parentPathForCheck: g.Key);
                foreach (var item in g.OrderBy(i => i.DisplayPath))
                    folderNode.Items.Add(MakeTreeNode(IOPath.GetFileName(item.DisplayPath), item, "🔗",
                        isFolder: false, registerHighlight: !string.IsNullOrEmpty(search)));
                folderNode.IsExpanded = true;
                tv.Items.Add(folderNode);
            }

            tv.SelectedItemChanged -= TreeView_SelectedItemChanged;
            tv.SelectedItemChanged += TreeView_SelectedItemChanged;
        }

        // ── Registry tree ─────────────────────────────────────────
        // ── Helper: collect all currently expanded node keys from a TreeView ──
        private static HashSet<string> GetExpandedKeys(TreeView tv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(System.Windows.Controls.ItemCollection items)
            {
                foreach (TreeViewItem n in items)
                {
                    if (n.IsExpanded && n.Tag is string key) set.Add(key);
                    Walk(n.Items);
                }
            }
            Walk(tv.Items);
            return set;
        }

        // ── Helper: restore expanded state on rebuilt tree ───────────────────
        private static void RestoreExpandedKeys(TreeView tv, HashSet<string> expanded, bool defaultExpand)
        {
            void Walk(System.Windows.Controls.ItemCollection items)
            {
                foreach (TreeViewItem n in items)
                {
                    if (n.Tag is string key)
                        n.IsExpanded = expanded.Contains(key) || defaultExpand;
                    Walk(n.Items);
                }
            }
            Walk(tv.Items);
        }

        private void PopulateRegistryTree(TreeView tv, string search)
        {
            // DATA-BOUND build: construct a tree of lightweight RegTreeNode objects and assign as
            // ItemsSource. The TreeView (with HierarchicalDataTemplate + virtualization) realizes
            // visual containers only for visible rows, and checking/unchecking mutates the model —
            // not the visual tree — so large hives (thousands of keys) stay responsive.
            var items = _allItems.Values
                .Where(i => i.Category == "Registry" && MatchesSearch(i, search))
                .OrderBy(i => i.DisplayPath).ToList();

            // Precompute which key prefixes have at least one included item (for initial check state).
            var includedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ri in _allItems.Values)
            {
                if (ri.Category != "Registry" || !ri.IsIncluded) continue;
                string[] rp = ri.DisplayPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                string acc = "";
                for (int k = 0; k < rp.Length; k++)
                {
                    acc = k == 0 ? rp[0] : acc + "\\" + rp[k];
                    includedPrefixes.Add(acc.ToUpperInvariant());
                }
            }

            var roots  = new System.Collections.ObjectModel.ObservableCollection<RegTreeNode>();
            var keyMap = new Dictionary<string, RegTreeNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                string[] parts = item.DisplayPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                string pathSoFar = "";
                RegTreeNode? parent = null;

                for (int i = 0; i < parts.Length; i++)
                {
                    pathSoFar = i == 0 ? parts[0] : pathSoFar + "\\" + parts[i];
                    string nodeKey = pathSoFar.ToUpperInvariant();
                    if (!keyMap.TryGetValue(nodeKey, out var keyNode))
                    {
                        keyNode = new RegTreeNode
                        {
                            Name     = parts[i],
                            Icon     = "🗝",
                            FullPath = nodeKey,
                            OriginalPath = pathSoFar,   // real-case path for Add-dialog prefill
                            Owner    = this,
                            Parent   = parent,
                            IsExpanded = false,
                        };
                        // Set initial checked WITHOUT triggering propagation (field via cascade
                        // helper which only fires PropertyChanged).
                        keyNode.SetCheckedCascade(includedPrefixes.Contains(nodeKey));
                        keyMap[nodeKey] = keyNode;
                        if (parent == null) roots.Add(keyNode);
                        else parent.Children.Add(keyNode);
                    }
                    parent = keyNode;
                }

                // Attach the value as a leaf node under its key (only if it has a value name).
                if (parent != null && !string.IsNullOrEmpty(item.Value))
                {
                    // Show "ValueName = Data [REG_TYPE]" (like regedit) so the value data and its
                    // type are visible, not just the name. Falls back gracefully when data or type
                    // is missing.
                    string leafText = string.IsNullOrEmpty(item.Data)
                        ? item.Value
                        : $"{item.Value} = {item.Data}";
                    if (!string.IsNullOrEmpty(item.DataType))
                        leafText += $" [{item.DataType}]";
                    var leaf = new RegTreeNode
                    {
                        Name     = leafText,
                        Icon     = "📄",
                        FullPath = (parent.FullPath + "\\" + item.Value).ToUpperInvariant(),
                        OriginalPath = parent.OriginalPath + "\\" + item.Value,  // real-case path; parent key used for Add prefill
                        IsLeaf   = true,
                        Owner    = this,
                        Parent   = parent,
                    };
                    leaf.Items.Add(item);
                    leaf.SetCheckedCascade(item.IsIncluded);
                    parent.Children.Add(leaf);
                }
                else if (parent != null)
                {
                    parent.Items.Add(item);  // key node directly maps to a (Default) value item
                }
            }

            // Expand the root nodes by default (top-level hives).
            foreach (var r in roots) r.IsExpanded = true;

            tv.ItemsSource = roots;
            tv.SelectedItemChanged -= BoundTree_SelectedItemChanged;
            tv.SelectedItemChanged += BoundTree_SelectedItemChanged;
        }

        // ── Tree node builders ────────────────────────────────────
        /// <summary>
        /// Computes the initial checkbox state for a synthetic parent folder/key node (item == null).
        /// Returns true only if at least one child SnapItem whose DisplayPath starts with
        /// <paramref name="pathPrefix"/> is currently included.
        /// Works for both file paths (C:\Foo\) and registry paths (HKEY_LOCAL_MACHINE\Software).
        /// Returns false when all children are excluded (e.g. after a bulk-Exclude).
        /// Falls back to true when no matching children are found (shouldn't happen in practice).
        /// </summary>
        private bool AnyChildIncluded(string pathPrefix)
        {
            // Normalise: collapse any doubled backslashes (tree keys can contain them) and
            // ensure exactly one trailing backslash for prefix matching, so "C:\Foo" doesn't
            // accidentally match "C:\FooBar\...".
            static string Norm(string p)
            {
                while (p.Contains("\\\\")) p = p.Replace("\\\\", "\\");
                return p.TrimEnd('\\').ToUpperInvariant();
            }
            string prefix = Norm(pathPrefix) + "\\";
            bool anyFound = false;
            foreach (var si in _allItems.Values)
            {
                if (Norm(si.DisplayPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    anyFound = true;
                    if (si.IsIncluded) return true;
                }
            }
            return !anyFound; // no children found → default checked
        }

        private TreeViewItem MakeTreeNode(string label, SnapItem? item, string icon, bool isFolder,
                                          bool registerHighlight = false,
                                          string? parentPathForCheck = null,
                                          bool? initialCheckedOverride = null)
        {
            var node = new TreeViewItem { IsExpanded = false, DataContext = item };
            var sp   = new StackPanel { Orientation = Orientation.Horizontal };

            // For leaf nodes: use item.IsIncluded directly.
            // For synthetic parent folder nodes (item == null): use a precomputed override when
            // supplied (fast path for large trees), else check whether any descendant is included
            // so that bulk Include/Exclude is correctly reflected after a tree rebuild.
            bool initialChecked = item != null
                ? item.IsIncluded
                : (initialCheckedOverride ??
                   (parentPathForCheck != null ? AnyChildIncluded(parentPathForCheck) : true));

            // Checkbox first
            var cb = new CheckBox
            {
                IsChecked = initialChecked,
                Margin    = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            cb.Checked += (_, _) =>
            {
                if (_cascading) return;  // already being set by cascade — skip
                var _sw = System.Diagnostics.Stopwatch.StartNew();
                if (item != null) item.IsIncluded = true;
                SetIncludedCascade(node, true);   // cascade DOWN to children
                long t1 = _sw.ElapsedMilliseconds;
                CheckParentsUpward(node);          // cascade UP to parents
                long t2 = _sw.ElapsedMilliseconds;
                UpdateSummary();
                long t3 = _sw.ElapsedMilliseconds;
                AutoSaveQasnap();
                long t4 = _sw.ElapsedMilliseconds;
                LogPerf($"CHECK   '{(node.Tag as string) ?? label}': cascade={t1}ms parents={t2-t1}ms summary={t3-t2}ms autosave={t4-t3}ms total={t4}ms");
            };
            cb.Unchecked += (_, _) =>
            {
                if (_cascading) return;  // already being set by cascade — skip
                var _sw = System.Diagnostics.Stopwatch.StartNew();
                if (item != null) item.IsIncluded = false;
                SetIncludedCascade(node, false);
                long t1 = _sw.ElapsedMilliseconds;
                UpdateSummary();
                long t2 = _sw.ElapsedMilliseconds;
                AutoSaveQasnap();
                long t3 = _sw.ElapsedMilliseconds;
                LogPerf($"UNCHECK '{(node.Tag as string) ?? label}': cascade={t1}ms summary={t2-t1}ms autosave={t3-t2}ms total={t3}ms");
            };
            sp.Children.Add(cb);
            sp.Children.Add(new TextBlock { Text = icon, Margin = new Thickness(0,0,4,0), FontSize = 11 });
            var labelTb = new TextBlock
            {
                Text       = label,
                FontSize   = 11,
                Tag        = "TextPrimaryBrush",
            };
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            // Only register leaf/match nodes for highlight — not intermediate parent folders
            if (registerHighlight)
            {
                _treeLabelTbs[labelTb]   = label;
                _treeLabelNodes[labelTb] = node;
            }
            sp.Children.Add(labelTb);

            node.Header = sp;

            // Build context menu once and assign to node
            var nodeMenu = MakeContextMenu(item, node);
            node.ContextMenu = nodeMenu;

            // Right-click: highlight blue + open menu
            node.MouseRightButtonDown += (sender2, e) =>
            {
                // Clear previous highlight
                if (_selectedTreeNode != null && _selectedTreeNode != node)
                    SetNodeHighlight(_selectedTreeNode, false);

                _selectedTreeNode = node;
                node.IsSelected   = true;
                node.Focus();
                SetNodeHighlight(node, true);

                nodeMenu.PlacementTarget = node;
                nodeMenu.IsOpen = true;
                e.Handled = true;
            };

            // When menu closes, keep highlight on selected node
            nodeMenu.Closed += (_, _) =>
            {
                if (_selectedTreeNode == node)
                    SetNodeHighlight(node, true);
            };

            return node;
        }


        private TreeViewItem MakeRegValueNode(SnapItem item)
        {
            var node = new TreeViewItem { DataContext = item };
            var sp   = new StackPanel { Orientation = Orientation.Horizontal };

            var cb = new CheckBox
            {
                IsChecked = item.IsIncluded,
                Margin    = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            cb.Checked   += (_, _) =>
            {
                if (_cascading) return;
                item.IsIncluded = true;
                LogAction($"Include: {item.DisplayPath} [{item.Value}]");
                CheckParentsUpward(node);   // check parent registry key upward
                UpdateSummary();
                AutoSaveQasnap();
            };
            cb.Unchecked += (_, _) => { item.IsIncluded = false; LogAction($"Exclude: {item.DisplayPath} [{item.Value}]"); UpdateSummary(); AutoSaveQasnap(); };
            sp.Children.Add(cb);
            sp.Children.Add(new TextBlock { Text = "📋", Margin = new Thickness(0,0,4,0), FontSize = 11 });
            string regValLabel = $"{item.Value}  =  {item.Data}  ({item.DataType})";
            var regValTb = new TextBlock
            {
                Text       = regValLabel,
                FontSize   = 10,
                Tag        = "TextSecondaryBrush",
            };
            regValTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            // Register for search highlighting (Value, Data, Type columns)
            _treeLabelTbs[regValTb]   = regValLabel;
            _treeLabelNodes[regValTb] = node;
            sp.Children.Add(regValTb);

            node.Header = sp;

            var nodeMenu = MakeContextMenu(item, node);
            node.ContextMenu = nodeMenu;

            node.MouseRightButtonDown += (sender2, e) =>
            {
                if (_selectedTreeNode != null && _selectedTreeNode != node)
                    SetNodeHighlight(_selectedTreeNode, false);

                _selectedTreeNode = node;
                node.IsSelected   = true;
                node.Focus();
                SetNodeHighlight(node, true);

                nodeMenu.PlacementTarget = node;
                nodeMenu.IsOpen = true;
                e.Handled = true;
            };

            nodeMenu.Closed += (_, _) =>
            {
                if (_selectedTreeNode == node)
                    SetNodeHighlight(node, true);
            };

            return node;
        }

        // ── TreeView selection — tracks the exact clicked node ────
        private void TreeView_SelectedItemChanged(object sender,
            RoutedPropertyChangedEventArgs<object> e)
        {
            // Clear previous node highlight
            if (_selectedTreeNode != null)
                SetNodeHighlight(_selectedTreeNode, false);

            if (sender is TreeView tv)
                _selectedTreeNode = tv.SelectedItem as TreeViewItem;

            // Highlight new selection
            if (_selectedTreeNode != null)
                SetNodeHighlight(_selectedTreeNode, true);

            UpdateIncludeExcludeButtons();
        }

        // Selection handler for the DATA-BOUND trees (Files/Registry). The selected item is a
        // RegTreeNode (not a TreeViewItem), so it's tracked separately and drives the same
        // Include/Exclude/Expand/Collapse toolbar buttons.
        private void BoundTree_SelectedItemChanged(object sender,
            RoutedPropertyChangedEventArgs<object> e)
        {
            _selectedRegNode  = (sender as TreeView)?.SelectedItem as RegTreeNode;
            // A data-bound tree node is selected, so clear any legacy TreeViewItem selection.
            if (_selectedRegNode != null) { _selectedTreeNode = null; _selectedListItem = null; }
            UpdateIncludeExcludeButtons();
        }

        // ── Manually set/clear blue highlight on a tree node ─────────
        private static readonly SolidColorBrush HighlightBrush =
            new SolidColorBrush(Color.FromRgb(0x33, 0x90, 0xC5));

        private static void SetNodeHighlight(TreeViewItem node, bool highlight)
        {
            // Walk the visual tree to find the "Bd" Border in the template
            var bd = FindTemplateBorder(node, "Bd");
            if (bd != null)
            {
                bd.Background = highlight ? HighlightBrush : Brushes.Transparent;
            }
            // Also update text foreground
            SetNodeForeground(node, highlight);
        }

        private static System.Windows.Controls.Border? FindTemplateBorder(
            System.Windows.FrameworkElement element, string name)
        {
            try
            {
                // Template is on Control, not FrameworkElement — cast first
                var result = (element as System.Windows.Controls.Control)
                    ?.Template?.FindName(name, element)
                    as System.Windows.Controls.Border;
                if (result != null) return result;

                // Walk visual children
                int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < count; i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(element, i);
                    if (child is System.Windows.Controls.Border b && b.Name == name)
                        return b;
                    if (child is System.Windows.FrameworkElement fe)
                    {
                        var found = FindTemplateBorder(fe, name);
                        if (found != null) return found;
                    }
                }
            }
            catch { }
            return null;
        }

        private static void SetNodeForeground(TreeViewItem node, bool highlight)
        {
            if (node.Header is System.Windows.Controls.StackPanel sp)
            {
                foreach (var child in sp.Children)
                {
                    if (child is System.Windows.Controls.TextBlock tb)
                    {
                        if (highlight)
                        {
                            tb.Foreground = Brushes.White;
                        }
                        else
                        {
                            // Restore dynamic resource so theme toggling works correctly
                            string resourceKey = tb.Tag as string ?? "TextPrimaryBrush";
                            tb.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);
                        }
                    }
                }
            }
        }

        // ── Right-click context menu ──────────────────────────────
        private ContextMenu MakeContextMenu(SnapItem? item, TreeViewItem node)
        {
            var cm = new ContextMenu();

            var miInclude = new MenuItem { Header = "✓  Include" };
            miInclude.Click += (_, _) =>
            {
                SetIncludedCascade(node, true);
                CheckParentsUpward(node);
                UpdateSummary();
                AutoSaveQasnap();
            };

            var miExclude = new MenuItem { Header = "✕  Exclude" };
            miExclude.Click += (_, _) =>
            {
                SetIncludedCascade(node, false);
                UpdateSummary();
                AutoSaveQasnap();
            };

            var miExpand = new MenuItem { Header = "▼  Expand" };
            miExpand.Click += (_, _) => ExpandCollapseNode(node, expand: true);

            var miCollapse = new MenuItem { Header = "▲  Collapse" };
            miCollapse.Click += (_, _) => ExpandCollapseNode(node, expand: false);

            cm.Items.Add(miInclude);
            cm.Items.Add(miExclude);
            cm.Items.Add(new Separator());
            cm.Items.Add(miExpand);
            cm.Items.Add(miCollapse);

            // Re-select node when menu closes so blue highlight stays
            cm.Closed += (_, _) =>
            {
                if (_selectedTreeNode == node)
                {
                    node.IsSelected = true;
                    node.Focus();
                }
            };

            return cm;
        }

        // ── Footer Include / Exclude buttons ─────────────────────
        // Require an explicit selection — either a tree node or a list row.
        // Buttons are disabled when nothing is selected (see UpdateIncludeExcludeButtons).
        private void BtnIncludeAll_Click(object s, RoutedEventArgs e)
        {
            if (_selectedRegNode != null)
            {
                _selectedRegNode.IsChecked = true;   // model cascade handles items + summary
                LogAction("Include: selected node cascade");
            }
            else if (_selectedTreeNode != null)
            {
                SetIncludedCascade(_selectedTreeNode, true);
                CheckParentsUpward(_selectedTreeNode);
                LogAction($"Include: selected node cascade");
                UpdateSummary();
                AutoSaveQasnap();
            }
            else if (_selectedListItem != null)
            {
                _selectedListItem.IsIncluded = true;
                LogAction($"Include: {_selectedListItem.DisplayPath}");
                UpdateSummary();
                AutoSaveQasnap();
            }
        }

        private void BtnExcludeAll_Click(object s, RoutedEventArgs e)
        {
            if (_selectedRegNode != null)
            {
                _selectedRegNode.IsChecked = false;  // model cascade handles items + summary
                LogAction("Exclude: selected node cascade");
            }
            else if (_selectedTreeNode != null)
            {
                SetIncludedCascade(_selectedTreeNode, false);
                LogAction($"Exclude: selected node cascade");
                UpdateSummary();
                AutoSaveQasnap();
            }
            else if (_selectedListItem != null)
            {
                _selectedListItem.IsIncluded = false;
                LogAction($"Exclude: {_selectedListItem.DisplayPath}");
                UpdateSummary();
                AutoSaveQasnap();
            }
        }

        /// <summary>
        /// Enables Include/Exclude toolbar buttons only when an item is selected.
        /// Called on tab switch, tree selection change, and list selection change.
        /// </summary>
        private void UpdateIncludeExcludeButtons()
        {
            // The Temp tab is view-only: nothing on it can be included or excluded, so hard-lock
            // both buttons regardless of selection state rather than relying on the (deliberately
            // unwired) SelectionChanged handler leaving _selectedListItem null.
            bool hasSelection = _currentTab is not ("Temp" or "Events")
                             && (_selectedTreeNode != null || _selectedListItem != null || _selectedRegNode != null);
            if (BtnIncludeAll  != null) BtnIncludeAll.IsEnabled  = hasSelection;
            if (BtnExcludeAll  != null) BtnExcludeAll.IsEnabled  = hasSelection;
            // Expand/Collapse only make sense when a tree node is selected (legacy or data-bound).
            bool hasTreeNode = _selectedTreeNode != null || _selectedRegNode != null;
            if (BtnExpandNode   != null) BtnExpandNode.IsEnabled   = hasTreeNode;
            if (BtnCollapseNode != null) BtnCollapseNode.IsEnabled = hasTreeNode;
        }

        /// <summary>
        /// Returns only the items that belong to the currently active tab.
        /// If the active tab is "All", returns every item.
        /// </summary>
        private IEnumerable<SnapItem> GetItemsForCurrentTab()
        {
            var all = _allItems.Values;
            return _currentTab switch
            {
                "All"          => all,
                // The Files tab (PopulateFileTree) renders both File AND Folder items together,
                // so Include/Exclude on the Files tab must operate on both categories.
                "Files"        => all.Where(i => i.Category == "File" || i.Category == "Folder"),
                // The Folders tab (PopulateFolderTree) shows only empty captured folders.
                "Folders"      => all.Where(i => i.Category == "Folder"),
                "Shortcuts"    => all.Where(i => i.Category == "Shortcut"),
                "Registry"     => all.Where(i => i.Category == "Registry"),
                "Environment"  => all.Where(i => i.Category == "EnvVar"),
                "Certificates" => all.Where(i => i.Category == "Certificate"),
                "Services"     => all.Where(i => i.Category == "Service"),
                "Firewall"     => all.Where(i => i.Category == "Firewall"),
                "Drivers"      => all.Where(i => i.Category == "Driver"),
                "Odbc"         => all.Where(i => i.Category == "Odbc"),
                "SchedTask"    => all.Where(i => i.Category == "SchedTask"),
                "Process"      => all.Where(i => i.Category == "Process"),
                "InstalledApp" => all.Where(i => i.Category == "InstalledApp"),
                _              => all
            };
        }

        // ── Expand / Collapse selected node ───────────────────────
        private void BtnExpandNode_Click(object s, RoutedEventArgs e)
        {
            if (_selectedRegNode != null)
                ExpandCollapseRegNode(_selectedRegNode, expand: true);
            else if (_selectedTreeNode != null)
                ExpandCollapseNode(_selectedTreeNode, expand: true);
            // If nothing selected, expand the entire active tree
            else
                ExpandCollapseActiveTree(expand: true);
        }

        private void BtnCollapseNode_Click(object s, RoutedEventArgs e)
        {
            if (_selectedRegNode != null)
                ExpandCollapseRegNode(_selectedRegNode, expand: false);
            else if (_selectedTreeNode != null)
                ExpandCollapseNode(_selectedTreeNode, expand: false);
            else
                ExpandCollapseActiveTree(expand: false);
        }

        // Expand/collapse a data-bound node and all its descendants (model-driven).
        private static void ExpandCollapseRegNode(RegTreeNode node, bool expand)
        {
            node.IsExpanded = expand;
            foreach (var c in node.Children) ExpandCollapseRegNode(c, expand);
        }

        // ── Right-click context-menu handlers for the data-bound (Files/Registry) trees ──
        // A ContextMenu inherits DataContext from its placement target (the node's StackPanel),
        // so the MenuItem's DataContext is the RegTreeNode.
        private static RegTreeNode? CtxNode(object sender) =>
            (sender as MenuItem)?.DataContext as RegTreeNode;

        // Returns the folder path to pre-fill as the Add File/Folder destination, based on the
        // right-clicked Files-tree node. A folder node → its own path; a file leaf → its parent
        // folder. Empty when invoked from the toolbar (no node context).
        private string NodeFolderPathFromSender(object sender)
        {
            var node = CtxNode(sender);
            if (node == null) return "";
            string p = node.OriginalPath ?? "";
            if (string.IsNullOrEmpty(p)) return "";
            if (p.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)) return "";   // not a file path
            if (node.IsLeaf)
            {
                int slash = p.LastIndexOf('\\');
                return slash > 0 ? p.Substring(0, slash) : p;
            }
            return p;   // folder node → its own path
        }

        // Returns (hive, keyPath) to pre-fill the Add Registry dialog from the right-clicked
        // Registry-tree node. e.g. node "HKEY_CURRENT_USER\software\ab" →
        // ("HKEY_CURRENT_USER", "software\ab"). Empty when there is no registry node.
        private (string Hive, string KeyPath) NodeRegistryPathFromSender(object sender)
        {
            var node = CtxNode(sender);
            string p = node?.OriginalPath ?? "";
            if (string.IsNullOrEmpty(p) || !p.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
                return ("", "");
            if (node!.IsLeaf)
            {
                int slash = p.LastIndexOf('\\');
                if (slash > 0) p = p.Substring(0, slash);
            }
            int first = p.IndexOf('\\');
            if (first < 0) return (p, "");                       // just a hive
            return (p.Substring(0, first), p.Substring(first + 1));
        }

        private void CtxInclude_Click(object s, RoutedEventArgs e)
        {
            if (CtxNode(s) is RegTreeNode n) { n.IsChecked = true; }
        }
        private void CtxExclude_Click(object s, RoutedEventArgs e)
        {
            if (CtxNode(s) is RegTreeNode n) { n.IsChecked = false; }
        }
        private void CtxExpand_Click(object s, RoutedEventArgs e)
        {
            if (CtxNode(s) is RegTreeNode n) ExpandCollapseRegNode(n, true);
        }
        private void CtxCollapse_Click(object s, RoutedEventArgs e)
        {
            if (CtxNode(s) is RegTreeNode n) ExpandCollapseRegNode(n, false);
        }

        // ── Clear all captured data ───────────────────────────────
        // The toolbar "Add" button opens its dropdown menu (File/Folder/Registry) on left-click.
        private void BtnAdd_Click(object s, RoutedEventArgs e)
        {
            if (s is Button b && b.ContextMenu != null)
            {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.IsOpen = true;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ADD ITEMS — manually inject files / folders / registry into a capture
        // ════════════════════════════════════════════════════════════

        // Inserts a SnapItem into _allItems (included, manual), refreshes the active tab + summary.
        private int _sysSkippedCount = 0;   // user-context items rewritten/handled during a System run

        // In System context, an HKCU write actually lands in SYSTEM's profile, i.e.
        // HKEY_USERS\.DEFAULT, and a user-profile file write lands under SYSTEM's profile folder
        // (C:\Windows\System32\config\systemprofile\...). Rewrites the item's DisplayPath to where it
        // truly landed so the GUI is accurate. Returns true if a rewrite happened (and logs it).
        // Derives the app keyword from the source file name, matching EtwQaCapture's own logic:
        // "FileCompare Setup 1.0.exe" -> "filecompare". Returns "" when too short to be useful (e.g.
        // for many .bat sources), which disables the Uninstall-noise filter for that run.
        private static string DeriveSourceKeyword(string exeName)
        {
            try
            {
                string baseName = IOPath.GetFileNameWithoutExtension(exeName ?? "");
                // Split on spaces, hyphens, underscores and dots; take the first token that is
                // alphabetic and at least 3 chars (e.g. "filecompare-1.0-win64" -> "filecompare",
                // "FileCompare Setup 1" -> "FileCompare", "fc2401-x64" -> "" since no alpha>=3 token).
                var tokens = baseName.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var tok in tokens)
                {
                    string alpha = System.Text.RegularExpressions.Regex.Replace(tok, @"[\d]+$", "").Trim();
                    // Skip generic installer words that aren't app names.
                    if (alpha.Length >= 3 &&
                        !IsGenericInstallerWord(alpha))
                        return alpha.ToLowerInvariant();
                }
                return "";
            }
            catch { return ""; }
        }

        private static bool IsGenericInstallerWord(string w) =>
            w.Equals("setup",     StringComparison.OrdinalIgnoreCase) ||
            w.Equals("install",   StringComparison.OrdinalIgnoreCase) ||
            w.Equals("installer", StringComparison.OrdinalIgnoreCase) ||
            w.Equals("win",       StringComparison.OrdinalIgnoreCase) ||
            w.Equals("x64",       StringComparison.OrdinalIgnoreCase) ||
            w.Equals("x86",       StringComparison.OrdinalIgnoreCase) ||
            w.Equals("update",    StringComparison.OrdinalIgnoreCase);

        // True when a captured Registry item is an Uninstall-hive entry for a DIFFERENT app than the
        // source (i.e. background noise from other apps' ARP keys, e.g. MementoSection). Only fires
        // when a usable source keyword exists; otherwise nothing is dropped on this basis.
        private bool IsOtherAppUninstallNoise(SnapItem item)
        {
            if (item.Category != "Registry") return false;
            if (string.IsNullOrEmpty(_sourceKeyword)) return false;   // no keyword -> don't filter

            string p = item.DisplayPath ?? "";
            int idx = p.IndexOf(@"\Uninstall\", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;   // not an Uninstall-hive entry

            // The app segment after \Uninstall\.
            string after  = p.Substring(idx + @"\Uninstall\".Length);
            string appSeg = after.Split('\\')[0];

            // Keep if the source keyword appears in the app key segment OR in the value data
            // (DisplayName often holds the friendly name, e.g. "Smekal FileCompare").
            string hay = (appSeg + " " + (item.Data ?? "")).ToLowerInvariant();
            bool matchesSource = hay.Contains(_sourceKeyword);
            return !matchesSource;   // noise = an Uninstall entry that does NOT match the source
        }

        // True when the item lives in the CURRENT user's profile (HKCU, or C:\Users\<currentuser>\).
        // Used in System context to drop background noise the source (running as SYSTEM) could not
        // have produced. Note: items rewritten to a SYSTEM location are checked before this, so a
        // genuine SYSTEM write is never treated as noise.
        private static bool IsCurrentUserProfileItem(SnapItem item)
        {
            string p = item.DisplayPath ?? "";
            if (item.Category == "Registry")
                return p.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase);

            if (item.Category == "File" || item.Category == "Folder" || item.Category == "Shortcut")
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); // C:\Users\<me>
                if (!string.IsNullOrEmpty(userProfile)
                    && p.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool RewriteForSystemContext(SnapItem item)
        {
            if (!_runSystemContext) return false;

            if (item.Category == "Registry")
            {
                string p = item.DisplayPath ?? "";
                string? newPath = null;
                if (p.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
                    newPath = @"HKEY_USERS\.DEFAULT\" + p.Substring("HKEY_CURRENT_USER\\".Length);
                else if (p.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
                    newPath = @"HKEY_USERS\.DEFAULT";
                else if (p.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
                    newPath = @"HKEY_USERS\.DEFAULT\" + p.Substring("HKCU\\".Length);

                if (newPath != null)
                {
                    LogAction($"System context: registry {p} -> {newPath}");
                    item.DisplayPath = newPath;
                    _sysSkippedCount++;
                    return true;
                }
                return false;
            }

            if (item.Category == "File" || item.Category == "Folder" || item.Category == "Shortcut")
            {
                string p = item.DisplayPath ?? "";
                // C:\Users\<name>\<rest>  ->  systemprofile\<rest>
                int usersIdx = p.IndexOf(@"\Users\", StringComparison.OrdinalIgnoreCase);
                if (usersIdx >= 0)
                {
                    string afterUsers = p.Substring(usersIdx + @"\Users\".Length);   // <name>\<rest>
                    int slash = afterUsers.IndexOf('\\');
                    string rest = slash >= 0 ? afterUsers.Substring(slash + 1) : "";  // <rest>
                    string sysProfile = Environment.ExpandEnvironmentVariables(
                        @"%SystemRoot%\System32\config\systemprofile");
                    string newPath = string.IsNullOrEmpty(rest) ? sysProfile : IOPath.Combine(sysProfile, rest);
                    LogAction($"System context: {item.Category} {p} -> {newPath}");
                    item.DisplayPath = newPath;
                    if (!string.IsNullOrEmpty(item.SourceOnDisk)) item.SourceOnDisk = newPath;
                    _sysSkippedCount++;
                    return true;
                }
                return false;
            }
            return false;
        }

        private void AddManualItem(SnapItem item)
        {
            // In System context, rewrite user-profile items (HKCU, C:\Users\...) to where they
            // actually landed under the SYSTEM account, so the GUI reflects the real machine state.
            RewriteForSystemContext(item);
            item.IsManual   = true;
            item.IsIncluded = true;
            string key = item.Category + "|" + item.DisplayPath + "|" + item.Value;
            _allItems[key] = item;
        }

        // Standard install destinations offered in the Add-File/Folder dialog (absolute paths the
        // MSI builder maps to MSI directories). The captured app's own install folder is prepended
        // when known.
        private List<string> StandardDestinations()
        {
            var list = new List<string>();
            string? appDir = TryGetCapturedInstallDir();
            if (!string.IsNullOrEmpty(appDir)) list.Add(appDir!);
            void Add(Environment.SpecialFolder sf)
            {
                try { string p = Environment.GetFolderPath(sf); if (!string.IsNullOrEmpty(p) && !list.Contains(p)) list.Add(p); }
                catch { }
            }
            Add(Environment.SpecialFolder.ProgramFiles);
            Add(Environment.SpecialFolder.ProgramFilesX86);
            Add(Environment.SpecialFolder.CommonApplicationData);
            Add(Environment.SpecialFolder.ApplicationData);
            Add(Environment.SpecialFolder.LocalApplicationData);
            Add(Environment.SpecialFolder.CommonDesktopDirectory);
            Add(Environment.SpecialFolder.Windows);
            Add(Environment.SpecialFolder.System);
            return list;
        }

        // Best-effort: the install folder of the captured app (most common File item's directory).
        private string? TryGetCapturedInstallDir()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var grp = _allItems.Values
                .Where(i => i.Category == "File" && !string.IsNullOrEmpty(i.DisplayPath)
                            && i.DisplayPath.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
                .Select(i => IOPath.GetDirectoryName(i.DisplayPath) ?? "")
                .Where(d => d.Length > 0)
                .GroupBy(d => d).OrderByDescending(g => g.Count())
                .FirstOrDefault();
            return grp?.Key;
        }

        // ── Add File ──
        private void BtnAddFile_Click(object s, RoutedEventArgs e)
        {
            string defaultDest = NodeFolderPathFromSender(s);
            var r = ShowSourceDestDialog("Source Validation - Add File", isFolder: false, defaultDest: defaultDest);
            if (r == null) return;
            var (src, dest) = r.Value;

            // dest is the install destination FOLDER; the file keeps its name.
            string destPath = IOPath.Combine(dest, IOPath.GetFileName(src));
            try
            {
                Directory.CreateDirectory(dest);
                File.Copy(src, destPath, overwrite: true);
            }
            catch (UnauthorizedAccessException)
            {
                SvShowMessage("Add File", $"Access denied copying to:\n{destPath}\n\nRun the app as administrator to write to this location.");
                return;
            }
            catch (Exception ex)
            {
                SvShowMessage("Add File", $"Could not copy the file:\n{ex.Message}");
                return;
            }

            // Record it. Source-on-disk is now the destination copy (the real installed location),
            // so the MSI stages from there and re-import keeps working.
            AddManualItem(new SnapItem
            {
                Category     = "File",
                DisplayPath  = destPath,
                SourceOnDisk = destPath,
            });
            FinishAdd($"Copied and added: {IOPath.GetFileName(src)} → {dest}", "Files");
        }

        // ── Add Folder (enumerate all files inside, preserving structure) ──
        private void BtnAddFolder_Click(object s, RoutedEventArgs e)
        {
            string defaultDest = NodeFolderPathFromSender(s);
            var r = ShowSourceDestDialog("Source Validation - Add Folder", isFolder: true, defaultDest: defaultDest);
            if (r == null) return;
            var (srcFolder, destParent) = r.Value;

            // Recreate the picked folder by name under the destination, preserving subfolders.
            string folderName = new DirectoryInfo(srcFolder).Name;
            string destRoot   = IOPath.Combine(destParent, folderName);

            int added = 0; int failed = 0; string? firstError = null;
            try
            {
                // Always create the destination folder itself (so an EMPTY folder is still created
                // on disk and shown in the tree, not silently dropped).
                Directory.CreateDirectory(destRoot);
                AddManualItem(new SnapItem
                {
                    Category     = "Folder",
                    DisplayPath  = destRoot,
                    SourceOnDisk = destRoot,
                });

                // Recreate any empty subfolders too, so the structure is faithful.
                foreach (var dir in Directory.EnumerateDirectories(srcFolder, "*", SearchOption.AllDirectories))
                {
                    string relDir  = IOPath.GetRelativePath(srcFolder, dir);
                    string destDir = IOPath.Combine(destRoot, relDir);
                    try
                    {
                        Directory.CreateDirectory(destDir);
                        AddManualItem(new SnapItem
                        {
                            Category     = "Folder",
                            DisplayPath  = destDir,
                            SourceOnDisk = destDir,
                        });
                    }
                    catch { /* non-fatal: a protected subfolder */ }
                }

                foreach (var file in Directory.EnumerateFiles(srcFolder, "*", SearchOption.AllDirectories))
                {
                    string rel      = IOPath.GetRelativePath(srcFolder, file);
                    string destPath = IOPath.Combine(destRoot, rel);
                    try
                    {
                        Directory.CreateDirectory(IOPath.GetDirectoryName(destPath)!);
                        File.Copy(file, destPath, overwrite: true);
                        AddManualItem(new SnapItem
                        {
                            Category     = "File",
                            DisplayPath  = destPath,
                            SourceOnDisk = destPath,   // the copied (installed) location
                        });
                        added++;
                    }
                    catch (Exception exFile)
                    {
                        failed++;
                        firstError ??= exFile.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                SvShowMessage("Add Folder", $"Could not read folder: {ex.Message}");
                return;
            }

            if (failed > 0)
                SvShowMessage("Add Folder", $"Copied {added} file(s); {failed} failed.\nFirst error: {firstError}\n\nIf this is a protected location, run the app as administrator.");

            FinishAdd(added > 0
                ? $"Copied and added folder '{folderName}' ({added} file(s))"
                : $"Added empty folder '{folderName}'", "Files");
        }

        // ── Add Registry ──
        private void BtnAddRegistry_Click(object s, RoutedEventArgs e)
        {
            var (defHive, defKeyPath) = NodeRegistryPathFromSender(s);
            var result = ShowAddRegistryDialog(defHive, defKeyPath);
            if (result == null) return;
            var (hive, keyPath, valueName, valueData, regType) = result.Value;

            // If the user typed the hive into the key path too (e.g. "HKEY_LOCAL_MACHINE\SOFTWARE\X"
            // or "HKLM\SOFTWARE\X"), strip it so we don't double it.
            keyPath = StripHivePrefix(keyPath).Trim('\\');

            string fullKey = hive + (string.IsNullOrEmpty(keyPath) ? "" : "\\" + keyPath);

            // Write the value to the LIVE Windows registry now.
            if (!WriteLiveRegistry(hive, keyPath, valueName, valueData, regType, out string err))
            {
                SvShowMessage("Add Registry", $"Could not write to the registry:\n{err}\n\nWriting to HKEY_LOCAL_MACHINE requires running the app as administrator.");
                return;
            }

            AddManualItem(new SnapItem
            {
                Category    = "Registry",
                DisplayPath = fullKey,
                Value       = valueName,   // value name ("" → default value)
                Data        = valueData,
                DataType    = regType,
            });

            // Switch to the Registry tab so the newly-added value is visible immediately.
            FinishAdd($"Wrote and added registry value under {fullKey}", "Registry");
        }

        // Removes a leading registry hive (full name or common abbreviation) from a key path.
        private static string StripHivePrefix(string keyPath)
        {
            string kp = (keyPath ?? "").Trim().TrimStart('\\');
            (string Prefix, string _)[] hives =
            {
                ("HKEY_LOCAL_MACHINE\\", ""), ("HKLM\\", ""),
                ("HKEY_CURRENT_USER\\",  ""), ("HKCU\\", ""),
                ("HKEY_CLASSES_ROOT\\",  ""), ("HKCR\\", ""),
                ("HKEY_USERS\\",         ""), ("HKU\\",  ""),
            };
            foreach (var (prefix, _) in hives)
                if (kp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return kp.Substring(prefix.Length);
            return kp;
        }

        // Writes a value to the live Windows registry. Returns false + error on failure.
        private static bool WriteLiveRegistry(string hive, string subKey, string valueName,
                                              string data, string regType, out string error)
        {
            error = "";
            try
            {
                Microsoft.Win32.RegistryKey root = hive switch
                {
                    "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
                    "HKEY_CURRENT_USER"  => Microsoft.Win32.Registry.CurrentUser,
                    "HKEY_CLASSES_ROOT"  => Microsoft.Win32.Registry.ClassesRoot,
                    "HKEY_USERS"         => Microsoft.Win32.Registry.Users,
                    _                    => Microsoft.Win32.Registry.LocalMachine,
                };
                using var key = root.CreateSubKey(subKey, writable: true);
                if (key == null) { error = "Could not open or create the key."; return false; }

                // Empty value name → the key's (Default) value.
                string name = valueName ?? "";

                switch (regType)
                {
                    case "REG_DWORD":
                        key.SetValue(name, unchecked((int)ParseLongFlexible(data)), Microsoft.Win32.RegistryValueKind.DWord);
                        break;
                    case "REG_QWORD":
                        key.SetValue(name, ParseLongFlexible(data), Microsoft.Win32.RegistryValueKind.QWord);
                        break;
                    case "REG_EXPAND_SZ":
                        key.SetValue(name, data ?? "", Microsoft.Win32.RegistryValueKind.ExpandString);
                        break;
                    case "REG_MULTI_SZ":
                        // Split on newlines or ';' for multi-string entry.
                        var parts = (data ?? "").Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(p => p.Trim()).ToArray();
                        key.SetValue(name, parts, Microsoft.Win32.RegistryValueKind.MultiString);
                        break;
                    case "REG_BINARY":
                        key.SetValue(name, HexToBytes(data ?? ""), Microsoft.Win32.RegistryValueKind.Binary);
                        break;
                    default: // REG_SZ
                        key.SetValue(name, data ?? "", Microsoft.Win32.RegistryValueKind.String);
                        break;
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // Parses a number that may be decimal or 0x-prefixed hex.
        private static long ParseLongFlexible(string s)
        {
            s = (s ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return 0;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt64(s.Substring(2), 16);
            return long.TryParse(s, out var v) ? v : 0;
        }

        // Parses hex bytes ("00 1A FF" or "001AFF") into a byte array.
        private static byte[] HexToBytes(string s)
        {
            s = new string((s ?? "").Where(Uri.IsHexDigit).ToArray());
            if (s.Length % 2 != 0) s = "0" + s;
            var bytes = new byte[s.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return bytes;
        }

        private void FinishAdd(string status, string? navigateToTab = null)
        {
            // Navigate to the relevant tab so the user sees where the item was added.
            if (!string.IsNullOrEmpty(navigateToTab))
                SwitchToTab(navigateToTab!);
            RefreshCurrentTab();
            UpdateSummary();
            AutoSaveQasnap();
            SetFooterStatus(status);
            LogAction(status);
        }

        // Combined Source + Destination dialog. Two rows, each a TextBox with a 📂 browse button
        // inside (right side). For files the source browse is a file picker; for folders it is a
        // folder picker. The destination row also offers the standard install locations via a
        // dropdown button. Returns (source, destination) or null if cancelled. Both are validated
        // non-empty before OK closes.
        private (string Source, string Dest)? ShowSourceDestDialog(string title, bool isFolder, string defaultDest = "")
        {
            var dlg = SvBuildDialog(title);
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(dlg, title, isFolder ? "📁" : "📄");

            int r = 0;

            // Builds a row: label + (TextBox with an inline browse button).
            TextBox AddBrowseRow(string label, Action<TextBox> onBrowse, bool showStdMenu)
            {
                cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var lbl = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2, 8, 2) };
                Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0); cg.Children.Add(lbl);

                var rowGrid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var tb = SvRoundedTextBox();
                tb.MinWidth = 300;
                Grid.SetColumn(tb, 0); rowGrid.Children.Add(tb);

                var browse = SvRoundedIconButton("📂", "Browse");
                browse.Click += (_, _) => onBrowse(tb);
                Grid.SetColumn(browse, 1); rowGrid.Children.Add(browse);

                Grid.SetRow(rowGrid, r); Grid.SetColumn(rowGrid, 1); cg.Children.Add(rowGrid);
                r++;
                return tb;
            }

            // Source row.
            var srcTb = AddBrowseRow("Source", tb =>
            {
                if (isFolder)
                {
                    var picked = PickFolderRaw(isFolder ? "Select source folder" : "Select source file");
                    if (!string.IsNullOrEmpty(picked)) tb.Text = picked;
                }
                else
                {
                    var f = new OpenFileDialog { Title = "Select source file" };
                    if (f.ShowDialog() == true) tb.Text = f.FileName;
                }
            }, showStdMenu: false);

            // Destination row (browse = folder picker; also prefilled with the app install dir).
            var destTb = AddBrowseRow("Destination", tb =>
            {
                var picked = PickFolderRaw("Select destination folder");
                if (!string.IsNullOrEmpty(picked)) tb.Text = picked;
            }, showStdMenu: true);

            // Pre-fill the destination with the right-clicked node's folder (e.g. C:\ProgramData)
            // when invoked from a tree context menu; otherwise it starts empty.
            if (!string.IsNullOrEmpty(defaultDest))
                destTb.Text = defaultDest;

            SvNdAddSpacer(cg, r++, 6);

            (string, string)? result = null;
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = SvNdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = SvNdMakeButtons(sec,
                ("✔ OK", () =>
                {
                    string src  = (srcTb.Text  ?? "").Trim();
                    string dest = (destTb.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dest))
                    {
                        SvShowMessage("Add", "Please select both a Source and a Destination.");
                        return;
                    }
                    if (isFolder && !Directory.Exists(src))
                    {
                        SvShowMessage("Add Folder", "The source folder does not exist.");
                        return;
                    }
                    if (!isFolder && !File.Exists(src))
                    {
                        SvShowMessage("Add File", "The source file does not exist.");
                        return;
                    }
                    result = (src, dest);
                    dlg.Close();
                }),
                ("✕ Cancel", () => { result = null; dlg.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            dlg.ShowDialog();
            return result;
        }

        // Raw folder browser (no fallback dialog text); returns the folder or null.
        private string? PickFolderRaw(string title)
        {
            try
            {
                var ofd = new Microsoft.Win32.OpenFolderDialog { Title = title };
                return ofd.ShowDialog() == true ? ofd.FolderName : null;
            }
            catch
            {
                var f = new OpenFileDialog { Title = title + " (select any file inside the folder)" };
                return f.ShowDialog() == true ? IOPath.GetDirectoryName(f.FileName) : null;
            }
        }

        // Add-Registry dialog: hive dropdown, key path, value name, value data, type.
        private (string Hive, string KeyPath, string ValueName, string ValueData, string RegType)?
            ShowAddRegistryDialog(string defaultHive = "", string defaultKeyPath = "")
        {
            var dlg = SvBuildDialog("Source Validation - Add Registry");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(dlg, "Source Validation - Add Registry", "🗝");

            int r = 0;
            TextBox MakeTb()
            {
                var tb = SvRoundedTextBox();
                tb.Margin = new Thickness(0, 2, 0, 2);
                cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(tb, r); Grid.SetColumn(tb, 1);
                cg.Children.Add(tb);
                return tb;
            }
            void Label(string text)
            {
                var lbl = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0);
                cg.Children.Add(lbl);
            }

            Label("Hive");
            var (hiveHost, hive) = SvRoundedComboBox();
            hiveHost.Margin = new Thickness(0, 2, 0, 2);
            foreach (var h in new[] { "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER", "HKEY_CLASSES_ROOT", "HKEY_USERS" })
                hive.Items.Add(h);
            // Pre-select the hive from the right-clicked node, else default to HKLM.
            int hiveIdx = 0;
            if (!string.IsNullOrEmpty(defaultHive))
            {
                for (int i = 0; i < hive.Items.Count; i++)
                    if (string.Equals(hive.Items[i]?.ToString(), defaultHive, StringComparison.OrdinalIgnoreCase))
                    { hiveIdx = i; break; }
            }
            hive.SelectedIndex = hiveIdx;
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(hiveHost, r); Grid.SetColumn(hiveHost, 1); cg.Children.Add(hiveHost); r++;

            Label("Key path");      var keyTb  = MakeTb(); r++;
            // Pre-fill the key path from the right-clicked node (e.g. SOFTWARE\Microsoft).
            if (!string.IsNullOrEmpty(defaultKeyPath)) keyTb.Text = defaultKeyPath;
            Label("Value name");    var nameTb = MakeTb(); r++;
            Label("Value data");    var dataTb = MakeTb(); r++;

            Label("Type");
            var (typeHost, typeCombo) = SvRoundedComboBox();
            typeHost.Margin = new Thickness(0, 2, 0, 2);
            foreach (var t in new[] { "REG_SZ", "REG_EXPAND_SZ", "REG_DWORD", "REG_QWORD", "REG_MULTI_SZ", "REG_BINARY" })
                typeCombo.Items.Add(t);
            typeCombo.SelectedIndex = 0;
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(typeHost, r); Grid.SetColumn(typeHost, 1); cg.Children.Add(typeHost); r++;

            SvNdAddSpacer(cg, r++, 4);

            (string, string, string, string, string)? result = null;
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = SvNdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = SvNdMakeButtons(sec,
                ("✔ Add", () =>
                {
                    string kp = (keyTb.Text ?? "").Trim();
                    if (string.IsNullOrEmpty(kp))
                    {
                        SvShowMessage("Add Registry", "Key path is required.");
                        return;
                    }
                    result = (hive.SelectedItem?.ToString() ?? "HKEY_LOCAL_MACHINE",
                              kp, (nameTb.Text ?? "").Trim(), (dataTb.Text ?? "").Trim(),
                              typeCombo.SelectedItem?.ToString() ?? "REG_SZ");
                    dlg.Close();
                }),
                ("✕ Cancel", () => { result = null; dlg.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            dlg.ShowDialog();
            return result;
        }

        private void BtnClearAll_Click(object s, RoutedEventArgs e)
        {
            // Allow Clear even when there are no captured items: a System-context .bat run (or a
            // run that added nothing) leaves 0 items but the Source Path and other state should
            // still be reset. Only skip if there's truly nothing to clear at all.
            bool nothingToClear = _allItems.Count == 0
                && string.IsNullOrEmpty(TxtSourceFile?.Text)
                && string.IsNullOrEmpty(_lastSnapPath);
            if (nothingToClear) return;

            // Styled confirmation dialog (matches the Capture Complete / Create MSI dialogs).
            bool confirmed = false;
            var dlg = SvBuildDialog("Source Validation - Clear Capture");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(dlg, "Source Validation - Clear Capture", "🗑");

            int r = 0;
            SvNdAddRow(cg, r++, "Clear",
                "Clear all captured data?\nThis removes all files, registry, services and other captured items.",
                lblFg, boxBg, boxBdr, valFg, false);
            SvNdAddSpacer(cg, r++, 4);

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var icon = SvNdDecisionIcon();
            Grid.SetRow(icon, r); Grid.SetColumn(icon, 0);
            cg.Children.Add(icon);

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = SvNdMakeButtons(sec,
                ("✔ Clear",  () => { confirmed = true;  dlg.Close(); }),
                ("✕ Cancel", () => { confirmed = false; dlg.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            dlg.ShowDialog();
            if (!confirmed) return;

            // Clear source file text box
            TxtSourceFile.Text = "";

            // Clear all data
            _allItems.Clear();
            _tempItems.Clear();
            _eventItems.Clear(); _eventRecordIds.Clear(); _eventBaselineUtc = DateTime.MinValue;
            StopProcessPoller();
            _seenProcs.Clear();
            _knownPids.Clear();
            _appBaseline      = null;         // forget app baseline; next capture re-snapshots it
            _taskBaseline     = null;         // forget task baseline; next capture re-snapshots it
            _odbcBaseline     = null;         // forget ODBC baseline; next capture re-snapshots it
            _driverBaseline   = null;         // forget driver baseline; next capture re-snapshots it
            _tempBaseline     = null;         // ditto for the %TEMP% baseline
            _driverPayloadDone.Clear();        // and forget which driver payloads were listed
            _envBaseline      = null;         // forget env-var baseline; next capture re-snapshots it
            _firewallBaseline = null;         // forget firewall baseline; next capture re-snapshots it
            _firewallRegDone.Clear();          // and forget which firewall registry values were listed
            _serviceRegDone.Clear();           // and forget which service registry keys were listed
            _selectedTreeNode = null;
            _selectedListItem = null;
            _selectedRegNode  = null;
            _searchMatches.Clear();
            _searchMatchIdx = -1;
            _packageAppInfo  = null;          // start fresh — next capture re-reads the first app
            _appInfoCacheKey = "\0";
            _lastSnapPath    = "";            // no qasnap associated after clear
            _sysSkippedCount = 0;
            _lastCapturedSource = "";         // fresh session: no prior source to warn about
            _captureRunning  = false;

            // Reset dirty flags so trees rebuild fresh

            // Clear tree views and list views
            TvFiles.ItemsSource = null;      // files tree is data-bound (ItemsSource), not Items
            TvShortcuts.Items.Clear();
            TvRegistry.ItemsSource = null;   // registry is data-bound (ItemsSource), not Items
            LvAll.ItemsSource      = null;
            LvEnv.ItemsSource      = null;
            LvCerts.ItemsSource    = null;
            LvServices.ItemsSource = null;
            LvFirewall.ItemsSource = null;
            LvDrivers.ItemsSource  = null;
            LvOdbc.ItemsSource     = null;
            LvTasks.ItemsSource    = null;
            LvProcess.ItemsSource  = null;
            LvApps.ItemsSource     = null;

            // Clear search box
            TxtSearch.Text = "";
            TxtSearchCount.Text = "";

            UpdateIncludeExcludeButtons();

            // Clear summary
            SummaryPanel.Children.Clear();
            TxtCounts.Text = "";

            // Disable item-dependent actions now that the GUI is empty.
            if (BtnCreateQasnap != null) BtnCreateQasnap.IsEnabled = false;
            if (BtnCreateMsi != null)    BtnCreateMsi.IsEnabled    = false;
            if (BtnSaveQasnap != null)   BtnSaveQasnap.IsEnabled   = false;

            SetFooterStatus("Cleared - browse a source file and click Execute to begin capture");
            LogAction("All captured data cleared by user");
        }

        // ── Settings: edit ValidateSource.json ─────────────────────────────
        // Modal editor over ValidateSource.json (the same file the page reads for the SVR
        // output folder and capture exclusions). Loads current values, lets the user edit the
        // SVR folder and add / remove / edit each exclusion list, then writes the changes back
        // with every other key (_comment, _description, anything else) preserved via a JsonNode
        // round-trip. Mirrors the Auto Package Settings dialog.
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSourceValidationSettings();

        // Converts a browsed absolute path so the same rule works everywhere, matching the
        // convention of the config: exclusions are matched as a case-insensitive substring of
        // the captured absolute path.
        //   - inside the running user's profile -> profile-relative, dropping the user name
        //     (C:\Users\JohnDoe\Desktop\test.bat  ->  \Desktop\test.bat), so the rule applies to
        //     every user, not just the one who set it.
        //   - any other path -> kept as its full absolute path (it is the same on every
        //     machine, e.g. C:\Windows\Temp), so it stays explicit and readable.
        // Non-rooted input (already a fragment) is returned unchanged.
        private static string MakePortableExclusionPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                string full = path.Trim();
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                                    .TrimEnd('\\', '/');
                if (!string.IsNullOrEmpty(profile) &&
                    full.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase))
                    return full.Substring(profile.Length);   // keeps the leading backslash

                return full;   // non-profile path: keep the full absolute path
            }
            catch { return path; }
        }

        private void OpenSourceValidationSettings()
        {
            System.Text.Json.Nodes.JsonObject root;
            try
            {
                var docOpts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
                root = (File.Exists(SourceValidationConfigPath)
                    ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SourceValidationConfigPath), null, docOpts)?.AsObject()
                    : null) ?? new System.Text.Json.Nodes.JsonObject();
            }
            catch
            {
                SvShowMessage("Validate Source - Settings",
                    "ValidateSource.json is missing or is not valid JSON. Fix the file, then try again.\n\n" +
                    SourceValidationConfigPath, "⚠️");
                return;
            }

            // Theme brushes (same resource keys the page's other dialogs use).
            var winBg      = Application.Current.TryFindResource("WindowBgBrush")     as Brush ?? Brushes.White;
            var borderClr  = Application.Current.TryFindResource("BorderMainBrush")   as Brush ?? Brushes.LightGray;
            var boxBg      = Application.Current.TryFindResource("InputBgBrush")      as Brush ?? new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF8));
            var boxBdr     = Application.Current.TryFindResource("BorderMainBrush")   as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg      = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var valFg      = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? Brushes.Black;
            var sec        = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));

            var cwin = new Window
            {
                Width = 560, SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 48),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), ShowInTaskbar = false
            };
            // Hand focus back to the main window on close (a capture can steal foreground).
            cwin.Closed += (_, __) => { try { cwin.Owner?.Activate(); } catch { } };

            var outer = new Border { Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black } };
            cwin.Content = outer;
            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            // Title bar
            var tb = new Border { Background = titleBarBg, CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(16, 6, 12, 6) };
            tb.MouseLeftButtonDown += (_, _) => cwin.DragMove();
            Grid.SetRow(tb, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti = new TextBlock { Text = "⚙", FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt = new TextBlock { Text = "Validate Source - Settings", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
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

            void SectionHeader(string title, bool first = false)
                => form.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = valFg, Margin = new Thickness(0, first ? 0 : 14, 0, 6) });

            // ── SVR output folder (SVRPath) ────────────────────────────────
            SectionHeader("Output", true);
            var svrGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            svrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            svrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var svrLbl = new TextBlock { Text = "SVR Output Folder", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(svrLbl, 0); svrGrid.Children.Add(svrLbl);
            var svrTx = new TextBox { Text = ReadStr(root["SVRPath"]), FontSize = 11, MinHeight = 26, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = valFg, VerticalContentAlignment = VerticalAlignment.Center };
            var svrBrowse = new Button { Content = "📁", FontSize = 14, Foreground = valFg, Width = 28, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Browse for folder" };
            svrBrowse.Click += (_, _) =>
            {
                try
                {
                    var fbd = new System.Windows.Forms.FolderBrowserDialog { Description = "Select the SVR output folder", UseDescriptionForTitle = true };
                    try { if (!string.IsNullOrWhiteSpace(svrTx.Text)) fbd.SelectedPath = svrTx.Text; } catch { }
                    if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK) svrTx.Text = fbd.SelectedPath;
                }
                catch { }
            };
            var svrInner = new Grid();
            svrInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            svrInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(svrTx, 0); Grid.SetColumn(svrBrowse, 1);
            svrInner.Children.Add(svrTx); svrInner.Children.Add(svrBrowse);
            var svrWrap = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 6, 2), Child = svrInner };
            Grid.SetColumn(svrWrap, 1); svrGrid.Children.Add(svrWrap);
            form.Children.Add(svrGrid);

            // ── Editable string-list sections (order preserved) ────────────
            var listSpecs = new (string key, string header)[]
            {
                ("FilePaths",      "File Path Exclusions"),
                ("FolderPaths",    "Folder Path Exclusions"),
                ("FileExtensions", "File Extension Exclusions"),
                ("RegistryKeys",   "Registry Key Exclusions"),
                ("ShortcutNames",  "Shortcut Name Exclusions"),
                ("EnvVars",        "Environment Variable Exclusions"),
                ("Services",       "Service Exclusions"),
            };
            // key -> live list of the row textboxes (save reads .Text from each, in order)
            var listEditors = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<TextBox>>();

            foreach (var (key, header) in listSpecs)
            {
                SectionHeader(header);
                var rowsPanel = new StackPanel();
                form.Children.Add(rowsPanel);
                var boxes = new System.Collections.Generic.List<TextBox>();
                listEditors[key] = boxes;

                void AddListRow(string value)
                {
                    var rg = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var tx = new TextBox { Text = value, FontSize = 11, MinHeight = 26, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = valFg, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Consolas") };

                    // File Path and Folder Path Exclusions get a browse icon inside the box
                    // (matches the SVR folder field): FilePaths picks a file, FolderPaths picks
                    // a folder. The picked path drops into the box; entries are matched as a
                    // substring, so the user can trim the browsed path to the meaningful fragment.
                    UIElement boxInner = tx;
                    Button? browseBtn = null;
                    if (key == "FilePaths")
                    {
                        browseBtn = new Button { Content = "📄", FontSize = 13, Foreground = valFg, Width = 26, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Browse for a file (stored without the user name so it applies to all users)" };
                        browseBtn.Click += (_, _) =>
                        {
                            try
                            {
                                var ofd = new Microsoft.Win32.OpenFileDialog { Title = "Select a file to exclude", Filter = "All files (*.*)|*.*", CheckFileExists = false };
                                try { if (!string.IsNullOrWhiteSpace(tx.Text)) ofd.FileName = tx.Text; } catch { }
                                if (ofd.ShowDialog() == true) tx.Text = MakePortableExclusionPath(ofd.FileName);
                            }
                            catch { }
                        };
                    }
                    else if (key == "FolderPaths")
                    {
                        browseBtn = new Button { Content = "📁", FontSize = 13, Foreground = valFg, Width = 26, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Browse for a folder (stored without the user name so it applies to all users)" };
                        browseBtn.Click += (_, _) =>
                        {
                            try
                            {
                                var fbd = new System.Windows.Forms.FolderBrowserDialog { Description = "Select a folder to exclude", UseDescriptionForTitle = true };
                                try { if (!string.IsNullOrWhiteSpace(tx.Text)) fbd.SelectedPath = tx.Text; } catch { }
                                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK) tx.Text = MakePortableExclusionPath(fbd.SelectedPath);
                            }
                            catch { }
                        };
                    }
                    if (browseBtn != null)
                    {
                        var ig = new Grid();
                        ig.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        ig.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        Grid.SetColumn(tx, 0); Grid.SetColumn(browseBtn, 1);
                        ig.Children.Add(tx); ig.Children.Add(browseBtn);
                        boxInner = ig;
                    }

                    var wrap = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 6, 2), Margin = new Thickness(0, 0, 6, 0), Child = boxInner };
                    var del = new Button { Content = "✕", FontSize = 11, Width = 26, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = lblFg, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Remove this entry" };
                    del.Click += (_, _) => { rowsPanel.Children.Remove(rg); boxes.Remove(tx); };
                    Grid.SetColumn(wrap, 0); Grid.SetColumn(del, 1);
                    rg.Children.Add(wrap); rg.Children.Add(del);
                    rowsPanel.Children.Add(rg);
                    boxes.Add(tx);
                }

                if (root[key] is System.Text.Json.Nodes.JsonArray arr)
                    foreach (var item in arr)
                    {
                        string v = ReadStr(item);
                        if (v.Length > 0) AddListRow(v);
                    }

                var addBtn = new Button { Content = "＋  Add", Style = sec, Height = 26, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
                addBtn.Click += (_, _) => AddListRow("");
                form.Children.Add(addBtn);
            }

            // ── Button bar ─────────────────────────────────────────────────
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 4, 16, 14) };
            Grid.SetRow(btnBar, 2);
            var bCancel = new Button { Content = "✕  Cancel", Style = sec, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            bCancel.Click += (_, _) => cwin.Close();
            var bSave = new Button { Content = "💾  Save", Style = sec, Height = 30, Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            SvNdApplyPrimaryLook(bSave);
            bSave.Click += (_, _) =>
            {
                try
                {
                    // SVR folder (blank is allowed - the page falls back to its default).
                    root["SVRPath"] = svrTx.Text.Trim();

                    // Write each list back as a JSON array of the non-empty row values, in order.
                    foreach (var (key, _) in listSpecs)
                    {
                        var outArr = new System.Text.Json.Nodes.JsonArray();
                        foreach (var tx in listEditors[key])
                        {
                            string v = tx.Text.Trim();
                            if (v.Length > 0) outArr.Add(v);
                        }
                        root[key] = outArr;
                    }

                    // Serialise the node tree with a Utf8JsonWriter. Passing a bare
                    // JsonSerializerOptions to JsonNode.ToJsonString throws in .NET 8
                    // ("must specify a TypeInfoResolver ...") because the node write path does
                    // not auto-populate a resolver; WriteTo(writer) does not need one. Build the
                    // bytes fully in memory first so a mid-write failure cannot corrupt the file.
                    byte[] jsonBytes;
                    using (var ms = new MemoryStream())
                    {
                        using (var jw = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                            root.WriteTo(jw);
                        jsonBytes = ms.ToArray();
                    }
                    File.WriteAllBytes(SourceValidationConfigPath, jsonBytes);

                    // Refresh in-memory config + exclusions so changes apply without a restart.
                    LoadExclusions();
                    LoadSvConfig();

                    cwin.Close();
                    SvShowMessage("Validate Source - Settings",
                        "Settings written to ValidateSource.json.\nExclusion changes apply to the next capture.", "✅");
                }
                catch (Exception ex)
                {
                    SvShowMessage("Validate Source - Settings", "Save failed:\n" + ex.Message + "\n\n" + SourceValidationConfigPath, "⚠️");
                }
            };
            btnBar.Children.Add(bCancel); btnBar.Children.Add(bSave);
            og.Children.Add(btnBar);

            cwin.ShowDialog();
        }

        /// <summary>Recursively expand or collapse a node and all its descendants.</summary>
        private static void ExpandCollapseNode(TreeViewItem node, bool expand)
        {
            node.IsExpanded = expand;
            foreach (TreeViewItem child in node.Items)
                ExpandCollapseNode(child, expand);
        }

        /// <summary>Expand or collapse the entire active tree (no node selected).</summary>
        private void ExpandCollapseActiveTree(bool expand)
        {
            // Registry and Files are data-bound (RegTreeNode model) — toggle IsExpanded on the
            // model so the bound ItemContainerStyle updates the visual tree without walking
            // TreeViewItems.
            TreeView? boundTree = _currentTab == "Registry" ? TvRegistry
                                : _currentTab == "Files"    ? TvFiles
                                : null;
            if (boundTree != null && boundTree.ItemsSource is System.Collections.IEnumerable boundRoots)
            {
                void WalkReg(RegTreeNode n)
                {
                    n.IsExpanded = expand;
                    foreach (var c in n.Children) WalkReg(c);
                }
                foreach (var obj in boundRoots)
                    if (obj is RegTreeNode rn) WalkReg(rn);
                return;
            }

            TreeView? activeTree = _currentTab switch
            {
                "Shortcuts" => TvShortcuts,
                _           => null
            };
            if (activeTree == null) return;
            foreach (TreeViewItem root in activeTree.Items)
                ExpandCollapseNode(root, expand);
        }

        // ── Search ────────────────────────────────────────────────
        private void TxtSearch_TextChanged(object s, TextChangedEventArgs e)
        {
            _searchText     = TxtSearch.Text;
            _searchMatchIdx = -1;
            _searchMatches.Clear();
            _treeMatchTbs.Clear();
            RefreshCurrentTab();
            if (_searchText.Length > 0)
            {
                BuildSearchMatches();
                int total = _treeMatchTbs.Count > 0 ? _treeMatchTbs.Count : _searchMatches.Count;
                if (total > 0)
                {
                    _searchMatchIdx = 0;  // start at first match → shows 1/N
                    ScrollToSearchMatch();
                }
                else
                {
                    ApplyAllWordHighlights();
                    TxtSearchCount.Text = "0/0";
                }
            }
            else
            {
                ClearAllWordHighlights();
                TxtSearchCount.Text = "";
            }
        }
        private void BtnClearSearch_Click(object s, RoutedEventArgs e)
        {
            TxtSearch.Text  = "";
            _searchText     = "";
            _searchMatchIdx = -1;
            _searchMatches.Clear();
            _treeMatchTbs.Clear();
            TxtSearchCount.Text = "";
            ClearAllWordHighlights();
            RefreshCurrentTab();
        }
        private void BtnSearchPrev_Click(object s, RoutedEventArgs e)
        {
            int total = _treeMatchTbs.Count > 0 ? _treeMatchTbs.Count : _searchMatches.Count;
            if (total == 0) { BuildSearchMatches(); total = _treeMatchTbs.Count > 0 ? _treeMatchTbs.Count : _searchMatches.Count; }
            if (total == 0) return;
            // Wrapping from first → last = search complete (going backwards)
            bool wrapped = _searchMatchIdx == 0;
            _searchMatchIdx = (_searchMatchIdx - 1 + total) % total;
            ScrollToSearchMatch();
            ApplyAllWordHighlights();
            if (wrapped) PromptSearchAgain();
        }
        private void BtnSearchNext_Click(object s, RoutedEventArgs e)
        {
            int total = _treeMatchTbs.Count > 0 ? _treeMatchTbs.Count : _searchMatches.Count;
            if (total == 0) { BuildSearchMatches(); total = _treeMatchTbs.Count > 0 ? _treeMatchTbs.Count : _searchMatches.Count; }
            if (total == 0) return;
            // Wrapping from last → first = search complete (going forwards)
            bool wrapped = _searchMatchIdx == total - 1;
            _searchMatchIdx = (_searchMatchIdx + 1) % total;
            ScrollToSearchMatch();
            ApplyAllWordHighlights();
            if (wrapped) PromptSearchAgain();
        }

        /// <summary>
        /// Called when search wraps around (end→start or start→end).
        /// Asks the user if they want to search again.
        /// If No: clears the search box.
        /// </summary>
        private void PromptSearchAgain()
        {
            int matchCount = _treeMatchTbs.Count > 0 ? _treeMatchTbs.Count : _searchMatches.Count;
            string msg = "Search completed. Do you want to search again?";

            bool searchAgain = SvShowSearchWrapDialog(msg);

            if (!searchAgain)
            {
                TxtSearch.Text  = "";
                _searchText     = "";
                _searchMatchIdx = -1;
                _searchMatches.Clear();
                _treeMatchTbs.Clear();
                TxtSearchCount.Text = "";
                ClearAllWordHighlights();
                RefreshCurrentTab();
            }
        }

        private void BuildSearchMatches()
        {
            _searchMatches.Clear();
            if (_searchText.Length == 0) { TxtSearchCount.Text = ""; return; }
            string lower = _searchText.ToLowerInvariant();

            ListView? lv = _currentTab switch {
                "All"          => LvAll,
                "Environment"  => LvEnv,
                "Certificates" => LvCerts,
                "Services"     => LvServices,
                "Firewall"     => LvFirewall,
                "Drivers"      => LvDrivers,
                "Odbc"         => LvOdbc,
                "SchedTask"    => LvTasks,
                "Process"      => LvProcess,
                "InstalledApp" => LvApps,
                _              => null
            };
            // Build one entry per occurrence (not per row) so Next/Prev steps
            // through every individual highlight hit, including multiple hits per row.
            if (lv?.ItemsSource is List<SnapItem> flat)
            {
                // ListView tab: scan ALL visible columns for occurrences
                foreach (var item in flat)
                {
                    // Scan all visible columns — track field name so orange highlight lands on the right column
                    var fields = new (string text, string name)[]
                    {
                        (item.DisplayPath,  "path"),
                        (item.Value,        "value"),
                        (item.RegistryData, "data"),
                        (item.RegType,      "type"),
                        (item.FileDate,     "date"),
                        (item.FileSize,     "size"),
                        (item.Category,     "items"),
                    };
                    foreach (var (text, name) in fields)
                    {
                        if (string.IsNullOrEmpty(text)) continue;
                        int pos = 0, occIdx = 0;
                        while (pos <= text.Length)
                        {
                            int hit = text.IndexOf(lower, pos, StringComparison.OrdinalIgnoreCase);
                            if (hit < 0) break;
                            _searchMatches.Add((item, occIdx, name));
                            occIdx++;
                            pos = hit + lower.Length;
                        }
                    }
                }
            }
            else if (_treeLabelTbs.Count > 0)
            {
                // Tree tab: scan each registered label TextBlock for occurrences.
                // We reuse the (SnapItem, int) tuple but store the TextBlock index
                // as a fake SnapItem key via _treeMatchTbs parallel list.
                _treeMatchTbs.Clear();
                foreach (var kv in _treeLabelTbs)
                {
                    string text = kv.Value;
                    int pos = 0, occIdx = 0;
                    while (pos <= text.Length)
                    {
                        int hit = text.IndexOf(lower, pos, StringComparison.OrdinalIgnoreCase);
                        if (hit < 0) break;
                        // Store TextBlock + occurrence index
                        _treeMatchTbs.Add((kv.Key, occIdx));
                        occIdx++;
                        pos = hit + lower.Length;
                    }
                }
                // Also scan Value/Data/Type fields for Registry items via their SnapItem
            }

            if (_searchMatchIdx < 0 && _searchMatches.Count > 0) _searchMatchIdx = 0;
            UpdateSearchCount();
        }

        private void ScrollToSearchMatch()
        {
            // Tree tab navigation
            if (_treeMatchTbs.Count > 0)
            {
                if (_searchMatchIdx < 0 || _searchMatchIdx >= _treeMatchTbs.Count) return;
                var (matchTb, _) = _treeMatchTbs[_searchMatchIdx];
                if (_treeLabelNodes.TryGetValue(matchTb, out var matchNode))
                {
                    matchNode.IsSelected = true;
                    matchNode.BringIntoView();
                }
                UpdateSearchCount();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(ApplyTreeHighlights));
                return;
            }
            // ListView tab navigation
            if (_searchMatchIdx < 0 || _searchMatchIdx >= _searchMatches.Count) return;
            var (matchItem, _, _) = _searchMatches[_searchMatchIdx];
            ListView? lv = _currentTab switch {
                "All"          => LvAll,
                "Environment"  => LvEnv,
                "Certificates" => LvCerts,
                "Services"     => LvServices,
                "Firewall"     => LvFirewall,
                "Drivers"      => LvDrivers,
                "Odbc"         => LvOdbc,
                "SchedTask"    => LvTasks,
                "Process"      => LvProcess,
                "InstalledApp" => LvApps,
                _              => null
            };
            if (lv != null) { lv.SelectedItem = matchItem; lv.ScrollIntoView(matchItem); }
            UpdateSearchCount();
            // Defer highlight re-apply until after virtualized cells have rendered
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(ApplyAllWordHighlights));
        }

        private void UpdateSearchCount()
        {
            int total   = _treeMatchTbs.Count > 0 ? _treeMatchTbs.Count : _searchMatches.Count;
            int current = total == 0 ? 0 : _searchMatchIdx + 1;
            TxtSearchCount.Text = total > 0 ? $"{current}/{total}" : "";
        }

        // ── Word-level search highlighting in Path column ────────────
        private void PathCell_Loaded(object s, RoutedEventArgs e)
        {
            if (s is TextBlock tb && tb.Tag is SnapItem item)
            {
                _pathCellTbs[item] = tb;
                if (_searchText.Length > 0) ApplyCellHighlight(tb, item.DisplayPath,  item, "path");
            }
        }

        // ── Cell Loaded handlers for Value, Data, Type, Date, Size, Items columns ──
        private void ValueCell_Loaded(object s, RoutedEventArgs e)
        {
            if (s is TextBlock tb && tb.Tag is SnapItem item)
            { _valueCellTbs[item] = tb; if (_searchText.Length > 0) ApplyCellHighlight(tb, item.Value,        item, "value"); }
        }
        private void DataCell_Loaded(object s, RoutedEventArgs e)
        {
            if (s is TextBlock tb && tb.Tag is SnapItem item)
            { _dataCellTbs[item] = tb; if (_searchText.Length > 0) ApplyCellHighlight(tb, item.RegistryData, item, "data"); }
        }
        private void TypeCell_Loaded(object s, RoutedEventArgs e)
        {
            if (s is TextBlock tb && tb.Tag is SnapItem item)
            { _typeCellTbs[item] = tb; if (_searchText.Length > 0) ApplyCellHighlight(tb, item.RegType,      item, "type"); }
        }
        private void DateCell_Loaded(object s, RoutedEventArgs e)
        {
            if (s is TextBlock tb && tb.Tag is SnapItem item)
            { _dateCellTbs[item] = tb; if (_searchText.Length > 0) ApplyCellHighlight(tb, item.FileDate,     item, "date"); }
        }
        private void SizeCell_Loaded(object s, RoutedEventArgs e)
        {
            if (s is TextBlock tb && tb.Tag is SnapItem item)
            { _sizeCellTbs[item] = tb; if (_searchText.Length > 0) ApplyCellHighlight(tb, item.FileSize,     item, "size"); }
        }
        private void ItemsCell_Loaded(object s, RoutedEventArgs e)
        {
            if (s is TextBlock tb && tb.Tag is SnapItem item)
            { _itemsCellTbs[item] = tb; if (_searchText.Length > 0) ApplyCellHighlight(tb, item.Category,   item, "items"); }
        }

        private void ApplyAllWordHighlights()
        {
            foreach (var kv in _pathCellTbs)  ApplyCellHighlight(kv.Value, kv.Key.DisplayPath,  kv.Key, "path");
            foreach (var kv in _valueCellTbs) ApplyCellHighlight(kv.Value, kv.Key.Value,         kv.Key, "value");
            foreach (var kv in _dataCellTbs)  ApplyCellHighlight(kv.Value, kv.Key.RegistryData,  kv.Key, "data");
            foreach (var kv in _typeCellTbs)  ApplyCellHighlight(kv.Value, kv.Key.RegType,       kv.Key, "type");
            foreach (var kv in _dateCellTbs)  ApplyCellHighlight(kv.Value, kv.Key.FileDate,      kv.Key, "date");
            foreach (var kv in _sizeCellTbs)  ApplyCellHighlight(kv.Value, kv.Key.FileSize,      kv.Key, "size");
            foreach (var kv in _itemsCellTbs) ApplyCellHighlight(kv.Value, kv.Key.Category,      kv.Key, "items");
            ApplyTreeHighlights();
        }

        /// <summary>Applies search highlight to all registered tree node label TextBlocks.</summary>
        private void ApplyTreeHighlights()
        {
            string query = _searchText;
            // Determine which TextBlock + occurrence is currently active (orange)
            TextBlock? currentTb  = null;
            int        currentOcc = -1;
            if (_treeMatchTbs.Count > 0 && _searchMatchIdx >= 0 && _searchMatchIdx < _treeMatchTbs.Count)
            {
                (currentTb, currentOcc) = _treeMatchTbs[_searchMatchIdx];
            }
            foreach (var kv in _treeLabelTbs)
                ApplyTreeLabelHighlight(kv.Key, kv.Value, query,
                    isCurrentTb: kv.Key == currentTb, currentOccIdx: currentOcc);
        }

        private void ClearTreeHighlights()
        {
            foreach (var kv in _treeLabelTbs)
            {
                var tb = kv.Key;
                tb.Inlines.Clear();
                tb.Inlines.Add(new Run(kv.Value));
                // Restore dynamic resource so theme toggling keeps text visible
                string resourceKey = tb.Tag as string ?? "TextPrimaryBrush";
                tb.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);
            }
        }

        private void ApplyTreeLabelHighlight(TextBlock tb, string fullText, string query,
            bool isCurrentTb = false, int currentOccIdx = -1)
        {
            tb.Inlines.Clear();
            if (query.Length == 0 || fullText.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                tb.Inlines.Add(new Run(fullText));
                return;
            }
            int pos = 0, occIdx = 0;
            while (pos <= fullText.Length)
            {
                int hit = fullText.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    if (pos < fullText.Length) tb.Inlines.Add(new Run(fullText[pos..]));
                    break;
                }
                if (hit > pos) tb.Inlines.Add(new Run(fullText[pos..hit]));
                // Orange = this specific occurrence is the current navigated match
                Brush bg = (isCurrentTb && occIdx == currentOccIdx) ? _hlOrange : _hlYellow;
                tb.Inlines.Add(new Run(fullText.Substring(hit, query.Length))
                {
                    Background = bg,
                    Foreground = Brushes.Black,
                });
                occIdx++;
                pos = hit + query.Length;
            }
        }

        private void ClearAllWordHighlights()
        {
            void ClearCol(Dictionary<SnapItem, TextBlock> dict, Func<SnapItem, string> getText)
            {
                foreach (var kv in dict)
                {
                    kv.Value.Inlines.Clear();
                    kv.Value.Inlines.Add(new Run(getText(kv.Key)));
                    kv.Value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                }
            }
            ClearCol(_pathCellTbs,  i => i.DisplayPath);
            ClearCol(_valueCellTbs, i => i.Value);
            ClearCol(_dataCellTbs,  i => i.RegistryData);
            ClearCol(_typeCellTbs,  i => i.RegType);
            ClearCol(_dateCellTbs,  i => i.FileDate);
            ClearCol(_sizeCellTbs,  i => i.FileSize);
            ClearCol(_itemsCellTbs, i => i.Category);
            ClearTreeHighlights();
        }

        /// <summary>
        /// Applies search highlight to a single cell TextBlock for the given text content.
        /// Yellow = any match on this item; Orange = the currently navigated match occurrence.
        /// </summary>
        /// <summary>
        /// Applies search highlight to a single cell TextBlock.
        /// fieldName identifies which column this TextBlock belongs to (path/value/data/type/date/size/items).
        /// Yellow = any match; Orange = currently navigated match (must match both item AND field).
        /// </summary>
        private void ApplyCellHighlight(TextBlock tb, string text, SnapItem item, string fieldName = "path")
        {
            string query = _searchText;
            tb.Inlines.Clear();
            if (query.Length == 0 || string.IsNullOrEmpty(text))
            {
                tb.Inlines.Add(new Run(text ?? ""));
                return;
            }

            bool anyMatch = _searchMatches.Any(m => m.Item == item && m.Field == fieldName);
            if (!anyMatch)
            {
                tb.Inlines.Add(new Run(text));
                return;
            }

            // Which occurrence on this item+field is currently active (orange)?
            int currentOccIdx = -1;
            if (_searchMatchIdx >= 0 && _searchMatchIdx < _searchMatches.Count)
            {
                var (curItem, curOcc, curField) = _searchMatches[_searchMatchIdx];
                if (curItem == item && curField == fieldName) currentOccIdx = curOcc;
            }

            int pos = 0, occIdx = 0;
            while (pos <= text.Length)
            {
                int hit = text.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
                    break;
                }
                if (hit > pos) tb.Inlines.Add(new Run(text[pos..hit]));
                Brush bg = (occIdx == currentOccIdx) ? _hlOrange : _hlYellow;
                tb.Inlines.Add(new Run(text.Substring(hit, query.Length))
                {
                    Background = bg,
                    Foreground = Brushes.Black,
                });
                occIdx++;
                pos = hit + query.Length;
            }
        }

        private void SnapItem_CheckChanged(object s, RoutedEventArgs e)
        {
            // A list/All-tab checkbox changed IsIncluded via binding. Refresh the summary AND
            // trigger auto-save — without this, toggles on the list-based tabs never persisted.
            UpdateSummary();
            AutoSaveQasnap();
        }

        // ── Open icon click (inline Open column in All tab) ──────────────
        private void OpenIcon_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Source may be Grid, Image, TextBlock, or Run inline — walk up to find DataContext.
            // Guard: VisualTreeHelper.GetParent throws on non-Visual (e.g. Run).
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null)
            {
                if (dep is FrameworkElement fe && fe.DataContext is SnapItem item)
                {
                    ExecuteSnapItemOpen(item);
                    return;
                }
                if (dep is System.Windows.Media.Visual || dep is System.Windows.Media.Media3D.Visual3D)
                    dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
                else if (dep is System.Windows.FrameworkContentElement fce)
                    dep = fce.Parent;
                else
                    break;
            }
        }

        // ── Shared open logic for context menu and icon click ─────────────
        private void ExecuteSnapItemOpen(SnapItem item)
        {
            try
            {
                string path = item.OpenPath;
                if (path.StartsWith("reg://", StringComparison.OrdinalIgnoreCase))
                {
                    string regKey = path.Substring(6);
                    using (var applets = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                        applets?.SetValue("LastKey", regKey);
                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName("regedit"))
                        try { proc.Kill(); proc.WaitForExit(1500); } catch { }
                    System.Threading.Thread.Sleep(300);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("regedit.exe") { UseShellExecute = true });
                }
                else if (path.StartsWith("envvar://",   StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables") { UseShellExecute = true });
                else if (path.StartsWith("service://",  StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("services.msc") { UseShellExecute = true });
                else if (path.StartsWith("cert://",     StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("certmgr.msc") { UseShellExecute = true });
                else if (path.StartsWith("firewall://", StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wf.msc") { UseShellExecute = true });
                else if (System.IO.Directory.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", path);
                else if (System.IO.File.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                else
                    System.Diagnostics.Process.Start("explorer.exe",
                        System.IO.Path.GetDirectoryName(path) ?? path);
            }
            catch { }
        }

        // ── ListView right-click context menu ─────────────────────────────
        /// <summary>Called from XAML MouseRightButtonUp on LvAll rows; shows context menu.</summary>
        private void LvAll_MouseRightButtonUp(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Walk up visual tree to find the ListViewItem.
            // Guard: VisualTreeHelper.GetParent throws on non-Visual objects (e.g. Run inlines).
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not ListViewItem)
            {
                if (dep is System.Windows.Media.Visual || dep is System.Windows.Media.Media3D.Visual3D)
                    dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
                else if (dep is System.Windows.FrameworkContentElement fce)
                    dep = fce.Parent;
                else
                    break;
            }
            if (dep is not ListViewItem lvi || lvi.DataContext is not SnapItem item) return;

            // Detect which column was clicked by X position relative to the ListView
            string clickedHeader = GetClickedColumnHeader(e.GetPosition(LvAll).X);

            var cm = BuildSnapItemContextMenu(item, lvi, clickedHeader);
            lvi.ContextMenu = cm;
            cm.IsOpen = true;
            e.Handled = true;
        }

        private string GetClickedColumnHeader(double xPos) => GetClickedColumnHeader(LvAll, xPos);

        /// <summary>Which column an X position falls in, for any grid on the page.</summary>
        private static string GetClickedColumnHeader(ListView lv, double xPos)
        {
            // Walk through GridView columns in order, accumulating widths
            if (lv?.View is not GridView gv) return "";
            double x = 0;
            foreach (var col in gv.Columns)
            {
                x += col.ActualWidth;
                if (xPos < x)
                    return col.Header?.ToString() ?? "";
            }
            return "";
        }

        /// <summary>
        /// Right-click on the Process tab: copy, and nothing else. A process row records what ran;
        /// it cannot be included in a package, opened or excluded, so the menu the All tab shows
        /// would be mostly disabled items.
        ///
        /// Copying matters more here than elsewhere because the Command Line column is the one
        /// thing that will not fit on screen - an msiexec line with its /i, its /l*v and two full
        /// paths runs well past any sensible column width.
        /// </summary>
        private void LvProcess_MouseRightButtonUp(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not ListViewItem)
            {
                if (dep is System.Windows.Media.Visual || dep is System.Windows.Media.Media3D.Visual3D)
                    dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
                else if (dep is System.Windows.FrameworkContentElement fce)
                    dep = fce.Parent;
                else
                    break;
            }
            if (dep is not ListViewItem lvi || lvi.DataContext is not SnapItem item) return;

            string header = GetClickedColumnHeader(LvProcess, e.GetPosition(LvProcess).X);

            var cm = new ContextMenu();

            var miRow = new MenuItem { Header = "📋  Copy Row" };
            miRow.Click += (_, _) =>
            {
                string row = string.Join("\t", item.DisplayPath, item.Value, item.Data, item.DataType, item.CommandLine);
                try { System.Windows.Clipboard.SetText(row); } catch { }
            };
            cm.Items.Add(miRow);

            string cell = header switch
            {
                "Process"      => item.DisplayPath,
                "PID"          => item.Value,
                "Started"      => item.Data,
                "Path"         => item.DataType,
                "Command Line" => item.CommandLine,
                _              => item.CommandLine,   // the long one is the likeliest intent
            };
            var miCell = new MenuItem
            {
                Header    = string.IsNullOrEmpty(header) ? "📄  Copy" : $"📄  Copy  \"{header}\"",
                IsEnabled = !string.IsNullOrEmpty(cell),
            };
            miCell.Click += (_, _) => { try { System.Windows.Clipboard.SetText(cell); } catch { } };
            cm.Items.Add(miCell);

            // Always offered, wherever the click landed: it is why anyone right-clicks this grid.
            if (!string.IsNullOrEmpty(item.CommandLine) && header != "Command Line")
            {
                var miCmd = new MenuItem { Header = "📄  Copy Command Line" };
                miCmd.Click += (_, _) => { try { System.Windows.Clipboard.SetText(item.CommandLine); } catch { } };
                cm.Items.Add(miCmd);
            }

            lvi.ContextMenu = cm;
            cm.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu BuildSnapItemContextMenu(SnapItem item, ListViewItem lvi, string clickedHeader = "")
        {
            var cm = new ContextMenu();

            // ── Copy Row ──
            var miCopyRow = new MenuItem { Header = "📋  Copy Row" };
            miCopyRow.Click += (_, _) =>
            {
                string row = $"{item.DisplayPath}\t{item.Value}\t{item.RegistryData}\t{item.RegType}\t{item.FileDate}\t{item.FileSize}\t{item.Category}";
                try { System.Windows.Clipboard.SetText(row); } catch { }
            };
            cm.Items.Add(miCopyRow);

            // ── Copy — copies the cell matching the right-clicked column ──
            string cellValue = clickedHeader switch
            {
                "Path / Key / Service Name / Environment" => item.DisplayPath,
                "Value"  => item.Value,
                "Data"   => item.RegistryData,
                "Type"   => item.RegType,
                "Date"   => item.FileDate,
                "Size"   => item.FileSize,
                "Items"  => item.Category,
                _        => item.DisplayPath,   // fallback
            };
            string copyLabel = string.IsNullOrEmpty(clickedHeader) ? "📄  Copy" : $"📄  Copy  \"{clickedHeader}\"";
            var miCopy = new MenuItem { Header = copyLabel };
            miCopy.Click += (_, _) =>
            {
                try { System.Windows.Clipboard.SetText(cellValue); } catch { }
            };
            cm.Items.Add(miCopy);

            cm.Items.Add(new Separator());

            // ── Open ──
            string openLabel = item.Category switch
            {
                "Registry"    => "🗝  Open in Registry Editor",
                "EnvVar"      => "⚙  Open Environment Variables",
                "Service"     => "⚙  Open Services",
                "Certificate" => "🔒  Open Certificate Manager",
                "Firewall"    => "🛡  Open Windows Firewall",
                "Folder"      => "📁  Open Folder",
                _             => "📄  Open in Explorer",
            };
            var miOpen = new MenuItem { Header = openLabel };
            miOpen.Click += (_, _) => ExecuteSnapItemOpen(item);
            cm.Items.Add(miOpen);

            return cm;
        }

        // ── Summary ───────────────────────────────────────────────
        private void UpdateSummary()
        {
            SummaryPanel.Children.Clear();
            int total    = _allItems.Count;
            int included = _allItems.Values.Count(i => i.IsIncluded);

            // The Save button is usable once a .svr exists on disk (captured, imported, or
            // created) and there is something to save. Disabled while a capture is running.
            bool hasSnapFile = !string.IsNullOrEmpty(_lastSnapPath) && total > 0;
            if (BtnSaveQasnap != null)   BtnSaveQasnap.IsEnabled   = hasSnapFile && !_captureRunning;

            // Create SVR and Create MSI require captured/added data in the GUI. Disabled on a
            // fresh page (and after Clear), and while a capture is running; enabled the moment
            // there is at least one item and no capture is in progress.
            bool hasItems = total > 0 && !_captureRunning;
            if (BtnCreateQasnap != null) BtnCreateQasnap.IsEnabled = hasItems;
            if (BtnCreateMsi != null)    BtnCreateMsi.IsEnabled    = hasItems;

            // App info shown in the Summary: the package's app info. It is captured from the FIRST
            // installed app and then held (so a merged second install doesn't change it), and can be
            // overridden by the user in the Create MSI dialog. Fall back to reading the current
            // source only until the first app info is known.
            string curSrc = TxtSourceFile?.Text?.Trim() ?? "";
            if (_packageAppInfo == null)
            {
                // The captured registry is authoritative (see ResolveAppInfo) and is re-derived on
                // every pass, because the Uninstall hive is still filling in while the source runs.
                var fromCapture = AppInfoFromCapturedRegistry();
                if (!string.IsNullOrWhiteSpace(fromCapture.Name))
                {
                    _appInfoCache = fromCapture;
                }
                else if (_appInfoCacheKey != curSrc)
                {
                    // Nothing captured yet, or a source that registers no Uninstall key at all
                    // (script / .reg / .cer / font). Fall back to the live-hive lookup, cached per
                    // source because it is expensive (registry scan + MSI COM + FileVersionInfo).
                    _appInfoCache    = ReadUninstallInfo(curSrc);
                    _appInfoCacheKey = curSrc;
                }

                // Freeze ONLY once the capture has finished.
                //
                // The old rule was "lock in the first app's info the moment we can read a name".
                // A bundled sub-installer routinely registers BEFORE the app itself - a bundled
                // updater's Uninstall key can appear tens of seconds before the app's own - so
                // ANY mid-capture latch can freeze the wrong app, even a complete-looking one. Wait
                // until the hive has stopped changing.
                if (!_capturing && !string.IsNullOrEmpty(_appInfoCache.Name))
                    _packageAppInfo = _appInfoCache;
            }
            var (appName, appVendor, appVersion) = _packageAppInfo ?? _appInfoCache;

            // App info section — label above value for long text
            if (!string.IsNullOrEmpty(appName))
            {
                AddAppInfoRow("App Name",    appName);
                if (!string.IsNullOrEmpty(appVendor))  AddAppInfoRow("App Vendor",  appVendor);
                if (!string.IsNullOrEmpty(appVersion)) AddAppInfoRow("App Version", appVersion);
                SummaryPanel.Children.Add(new Separator { Margin = new Thickness(0,8,0,6) });
            }
            else if (total > 0)
            {
                // Install still in progress — app not yet registered in uninstall hive
                AddAppInfoRow("App Name", "-");
                SummaryPanel.Children.Add(new Separator { Margin = new Thickness(0,8,0,6) });
            }

            // Counts
            AddSummaryRow("Total",    total.ToString());
            AddSummaryRow("Included", included.ToString());
            AddSummaryRow("Excluded", (total - included).ToString());
            SummaryPanel.Children.Add(new Separator { Margin = new Thickness(0,6,0,6) });

            foreach (var g in _allItems.Values.GroupBy(i => i.Category).OrderBy(g => g.Key))
            {
                // Display labels for the summary rows. The filter category passed to the
                // click handler stays the raw Category value, so these are free to differ:
                // per request, the File row reads "All" and the Folder row reads "Files"
                // (clicking it opens the Files tab tree, which the name now matches).
                // Folders are shown here (so the summary counts add up to Total) even though they
                // are not a separate tab — the folder hierarchy itself lives in the Files tab tree.
                string label = g.Key switch
                {
                    "File"     => "All",
                    "Folder"   => "Files",
                    "Shortcut" => "Shortcuts",
                    "Driver"   => "Drivers",
                    "Odbc"     => "Odbc",
                    "SchedTask" => "SchedTask",
                    "Process"  => "Process",
                    "InstalledApp" => "InstalledApp",
                    "Certificate" => "Certificates",
                    "EnvVar"   => "Environment",
                    _          => g.Key
                };
                AddClickableSummaryRow(label, $"{g.Count(i => i.IsIncluded)} / {g.Count()}", g.Key);
            }

            TxtCounts.Text = $"{included} of {total} included";
        }

        /// <summary>
        /// Reads DisplayName, Publisher and DisplayVersion from the Windows Uninstall hive.
        /// For MSI files, reads the product name from the MSI database for accurate matching.
        /// Falls back to most-recently-installed entry when no name match found.
        /// </summary>
        /// <summary>
        /// Best available identity for the captured package.
        ///
        /// The CAPTURED registry wins over the live-hive lookup, because it contains ONLY the
        /// Uninstall keys this source actually wrote, and AppInfoFromCapturedRegistry breaks ties by
        /// how specifically a DisplayName matches the source keyword - so it can tell "FileCompare" from
        /// the bundled "FileCompare Update Helper" that ships inside the same installer.
        ///
        /// ReadUninstallInfo cannot: it fuzzy-matches the LIVE hive on the source's file name, and
        /// both entries match "filecompare". It returned the helper, complete with the helper's own
        /// version, which then looked like a perfectly good answer and suppressed the captured-
        /// registry path entirely.
        ///
        /// ReadUninstallInfo stays as the fallback for sources that register no Uninstall key at all
        /// (.reg / .cer / .inf / scripts / archives) - which is what its own comments say it is for.
        /// </summary>
        private (string Name, string Vendor, string Version) ResolveAppInfo(string srcPath)
        {
            var fromCapture = AppInfoFromCapturedRegistry();
            if (!string.IsNullOrWhiteSpace(fromCapture.Name)) return fromCapture;
            return ReadUninstallInfo(srcPath);
        }

        private static (string Name, string Vendor, string Version) ReadUninstallInfo(string srcPath)
        {
            string exeName = IOPath.GetFileNameWithoutExtension(srcPath).ToLowerInvariant();
            string srcExt  = IOPath.GetExtension(srcPath).ToLowerInvariant();

            // .reg / .cer / .inf sources are not applications — they apply registry/cert/INF data
            // directly. There is no Add/Remove Programs entry to detect, so don't fuzzy-match the
            // live registry (which would surface an unrelated app). The captured data fallback in
            // UpdateSummary still fills app info if the change happens to register one.
            //
            // Scripts and archives (.bat/.cmd/.ps1/.vbs/.js/.zip/...) are likewise not installers:
            // they don't create their own Uninstall key, and fuzzy-matching their filename keyword
            // (e.g. "test" from test.bat) wrongly surfaces an unrelated app such as
            // "Microsoft TestPlatform SDK". Skip the registry guess for them too — if a script
            // actually installs something, the captured Uninstall key fills app info via the
            // AppInfoFromCapturedRegistry fallback in UpdateSummary.
            if (srcExt == ".reg" || srcExt == ".cer" || srcExt == ".inf"
                || srcExt == ".bat" || srcExt == ".cmd" || srcExt == ".ps1"
                || srcExt == ".vbs" || srcExt == ".js"  || srcExt == ".zip"
                || srcExt == ".7z"  || srcExt == ".rar")
                return ("", "", "");

            bool isMsi = srcPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                      || srcPath.EndsWith(".msp", StringComparison.OrdinalIgnoreCase);

            // For MSI: read ProductName, Manufacturer, ProductVersion directly from MSI database
            string msiProductName    = "";
            string msiManufacturer   = "";
            string msiProductVersion = "";
            string msiProductCode    = "";
            if (isMsi && File.Exists(srcPath))
            {
                try
                {
                    Type? t = Type.GetTypeFromProgID("WindowsInstaller.Installer");
                    if (t != null)
                    {
                        dynamic installer = Activator.CreateInstance(t)!;
                        dynamic db = installer.OpenDatabase(srcPath, 0);
                        string QueryProp(string prop)
                        {
                            try
                            {
                                dynamic v = db.OpenView($"SELECT `Value` FROM `Property` WHERE `Property`='{prop}'");
                                v.Execute();
                                dynamic? r = v.Fetch();
                                return r?.StringData[1] as string ?? "";
                            }
                            catch { return ""; }
                        }
                        msiProductName    = QueryProp("ProductName");
                        msiManufacturer   = QueryProp("Manufacturer");
                        msiProductVersion = QueryProp("ProductVersion");
                        msiProductCode    = QueryProp("ProductCode").Trim('{', '}').ToUpperInvariant();
                    }
                }
                catch { }
            }

            // For EXE: read FileVersionInfo to get a better hint than just the filename
            string fviProductName = "";
            string fviCompany     = "";
            string fviVersion     = "";
            if (!isMsi && File.Exists(srcPath))
            {
                try
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(srcPath);
                    fviProductName = vi.ProductName?.Trim()    ?? "";
                    fviCompany     = vi.CompanyName?.Trim()    ?? "";
                    fviVersion     = vi.ProductVersion?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(fviProductName)) fviProductName = vi.FileDescription?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(fviVersion))     fviVersion     = vi.FileVersion?.Trim()     ?? "";
                }
                catch { }
            }

            // For NSIS/stripped EXEs with no version resource (e.g. filecompare-1.0-x64.exe),
            // derive a short normalised token from the filename to use as a registry hint.
            // Strips version numbers and arch suffixes: "filecompare-1.0-x64" → "filecompare"
            string exeNormHint = "";
            if (!isMsi && string.IsNullOrWhiteSpace(fviProductName))
            {
                exeNormHint = System.Text.RegularExpressions.Regex.Replace(
                    exeName,
                    @"[-_.]?(setup|install|installer|win\d+|x64|x86|\d[\d.\-]*).*$",
                    "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim().TrimEnd('-','_','.');
            }

            var uninstallPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            var candidates = new List<(string Name, string Vendor, string Version, int Score, string InstallDate)>();

            // Pre-compute significant words from the EXE's product name (≥4 chars, not version/setup words)
            static string[] SigWords(string s) => s
                .Split(new[] { ' ', '-', '_', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4
                    && !new[] { "setup", "install", "installer", "free", "trial", "version", "portable", "update" }
                        .Contains(w.ToLowerInvariant())
                    && !System.Text.RegularExpressions.Regex.IsMatch(w, @"^\d[\d.]*$"))
                .Select(w => w.ToLowerInvariant())
                .ToArray();

            string[] hintWords = SigWords(!string.IsNullOrWhiteSpace(fviProductName) ? fviProductName
                : !string.IsNullOrWhiteSpace(exeNormHint) ? exeNormHint : exeName);

            foreach (var hivePath in uninstallPaths)
            {
                try
                {
                    using var hive = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(hivePath);
                    if (hive == null) continue;

                    foreach (var subKeyName in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = hive.OpenSubKey(subKeyName);
                            if (sub == null) continue;

                            string name        = sub.GetValue("DisplayName")    as string ?? "";
                            string vendor      = sub.GetValue("Publisher")      as string ?? "";
                            string version     = sub.GetValue("DisplayVersion") as string ?? "";
                            string installDate = sub.GetValue("InstallDate")    as string ?? "";

                            if (string.IsNullOrWhiteSpace(name)) continue;

                            int score = 0;
                            string nameLower = name.ToLowerInvariant();
                            string keyLower  = subKeyName.ToLowerInvariant();

                            // MSI: match by ProductCode in registry key name (most reliable)
                            if (!string.IsNullOrEmpty(msiProductCode))
                            {
                                string keyClean  = keyLower.Trim('{', '}').Replace("-", "").ToUpperInvariant();
                                string codeClean = msiProductCode.Replace("-", "");
                                if (keyClean == codeClean || keyLower.Contains(msiProductCode.ToLowerInvariant()))
                                    score += 100;
                            }

                            // MSI: match by ProductName
                            if (!string.IsNullOrEmpty(msiProductName))
                            {
                                if (name.Equals(msiProductName, StringComparison.OrdinalIgnoreCase)) score += 50;
                                else if (nameLower.Contains(msiProductName.ToLowerInvariant()))       score += 20;
                            }

                            // EXE: Score 20 — exact match between FVI ProductName and DisplayName
                            if (!isMsi && !string.IsNullOrEmpty(fviProductName))
                            {
                                if (name.Equals(fviProductName, StringComparison.OrdinalIgnoreCase))
                                    score += 20;
                                else if (nameLower.Contains(fviProductName.ToLowerInvariant()) ||
                                         fviProductName.ToLowerInvariant().Contains(nameLower))
                                    score += 10;
                            }

                            // EXE: Score via significant word overlap — both sides must be ≥4 chars
                            // This prevents "windows" in the EXE name from matching "R for Windows"
                            if (!isMsi && hintWords.Length > 0 && score == 0)
                            {
                                string[] nameWords = SigWords(name);
                                if (nameWords.Length > 0)
                                {
                                    int shared = hintWords.Count(hw =>
                                        nameWords.Any(nw => nw.Contains(hw) || hw.Contains(nw)));
                                    // Require at least 1 shared word AND it covers ≥ half the hint words
                                    if (shared >= 1 && shared * 2 >= hintWords.Length)
                                        score += shared * 5;
                                }
                            }

                            // EXE: short filename token match (e.g. "fc" from fc-1.0-x64.exe)
                            // Uses word-boundary regex so "fc" matches "FC file compare" but not "effective"
                            if (!isMsi && !string.IsNullOrWhiteSpace(exeNormHint) && score == 0)
                            {
                                string dispLower = name.ToLowerInvariant();
                                bool wordMatch = System.Text.RegularExpressions.Regex.IsMatch(
                                    dispLower,
                                    @"(^|[\s\-_(])" + System.Text.RegularExpressions.Regex.Escape(exeNormHint) + @"($|[\s\-_)])",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (wordMatch) score += 10;
                            }

                            // Skip system / Microsoft entries unless they scored meaningfully
                            if (score < 5 && (vendor.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                                || vendor.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))) continue;

                            if (score > 0)
                                candidates.Add((name, vendor, version, score, installDate));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // If MSI and we got product info directly, use it (most accurate)
            if (isMsi && !string.IsNullOrEmpty(msiProductName))
            {
                var msiMatch = candidates.FirstOrDefault(c => c.Score >= 50);
                string vendor  = msiMatch.Name != null ? msiMatch.Vendor  : msiManufacturer;
                string version = msiMatch.Name != null ? msiMatch.Version : msiProductVersion;
                if (string.IsNullOrEmpty(version)) version = msiProductVersion;
                return (msiProductName, vendor, version);
            }

            if (candidates.Count == 0)
                return ("", "", "");

            // Pick best: highest score, then most recently installed
            var best = candidates
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.InstallDate)
                .ThenBy(c => c.Name.ToLowerInvariant().Contains("service") ? 1 : 0)
                .First();

            if (best.Score == 0) return ("", "", "");

            return (best.Name, best.Vendor, best.Version);
        }

        private void AddSummaryRow(string label, string value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,2,0,2) };
            var labelTb = new TextBlock { Text = label, Width = 80, FontSize = 11 };
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            var valueTb = new TextBlock { Text = value, FontSize = 11, FontWeight = FontWeights.Medium };
            valueTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(labelTb);
            sp.Children.Add(valueTb);
            SummaryPanel.Children.Add(sp);
        }

        /// <summary>
        /// Adds a clickable summary row. Clicking it filters the All tab to show only
        /// items of the given category. Clicking the active filter clears it (shows all).
        /// </summary>
        private void AddClickableSummaryRow(string label, string value, string filterCategory)
        {
            bool isActive = _summaryFilter == filterCategory;

            // Active = light blue tint background; hover = lighter tint
            var activeBg = new SolidColorBrush(Color.FromArgb(0x28, 0x19, 0x76, 0xD2));  // blue tint
            var hoverBg  = new SolidColorBrush(Color.FromArgb(0x14, 0x19, 0x76, 0xD2));  // lighter tint

            var sp = new StackPanel
            {
                Orientation  = Orientation.Horizontal,
                Margin       = new Thickness(0, 1, 0, 1),
                Background   = Brushes.Transparent, // set on border wrapper below
                Cursor       = System.Windows.Input.Cursors.Hand,
            };

            var border = new Border
            {
                Margin      = new Thickness(-4, 0, -4, 0),
                Padding     = new Thickness(4, 3, 4, 3),
                Background  = isActive ? activeBg : Brushes.Transparent,
                Cursor      = System.Windows.Input.Cursors.Hand,
                ToolTip     = $"Click to filter All tab by {label}. Click again to clear.",
                Child       = sp,
            };

            var labelTb = new TextBlock
            {
                Text       = label,
                Width      = 80,
                FontSize   = 11,
                FontWeight = FontWeights.Normal,
            };
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            var valueTb = new TextBlock
            {
                Text       = value,
                FontSize   = 11,
                FontWeight = FontWeights.Medium,
            };
            valueTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(labelTb);
            sp.Children.Add(valueTb);

            // Hover: only show hover tint when not already active
            border.MouseEnter += (_, _) =>
            {
                if (_summaryFilter != filterCategory)
                    border.Background = hoverBg;
            };
            border.MouseLeave += (_, _) =>
            {
                border.Background = _summaryFilter == filterCategory ? activeBg : Brushes.Transparent;
            };

            border.MouseLeftButtonUp += (_, _) =>
            {
                Focus();   // arrows now route through the page's PreviewKeyDown
                ApplySummaryFilter(filterCategory, toggle: true);
            };

            border.Tag = filterCategory;   // keyboard navigation reads the rows in visual order
            SummaryPanel.Children.Add(border);
        }

        // ── Keyboard navigation: Summary rows (Up/Down), category tabs (Left/Right) ──
        // Both columns are code-built Borders / plain Buttons rather than selectors, so
        // arrow keys did nothing in either. Clicking a summary row focuses the page (a
        // Border cannot take focus itself); clicking a tab focuses that Button. From then
        // on the arrows below move the selection, doing exactly what a click does.
        private int _summaryNavIndex = -1;

        private Button[] AllTabButtons() => new[]
        {
            TabAll, TabFiles, TabShortcuts, TabRegistry, TabEnv, TabCerts, TabServices,
            TabFirewall, TabDrivers, TabOdbc, TabTasks, TabProcess, TabApps, TabTemp, TabEvents,
        };

        /// <summary>
        /// The summary row click, as a method, so the keyboard does not grow its own
        /// variant that can drift. toggle:true is the mouse contract (clicking the active
        /// row clears it); the keyboard passes false, because stepping onto a row should
        /// select it, never deselect it.
        /// </summary>
        private void ApplySummaryFilter(string filterCategory, bool toggle)
        {
            _summaryNavIndex = SummaryFilterKeys().IndexOf(filterCategory);

            _summaryFilter = (toggle && _summaryFilter == filterCategory) ? "" : filterCategory;

            // Folders are not listed in the All tab (only in the Files tab tree). So the
            // Folders summary row navigates to the Files tab; everything else uses All.
            bool toFiles = filterCategory == "Folder" && _summaryFilter == "Folder";
            string targetTab = toFiles ? "Files" : "All";
            if (toFiles) _summaryFilter = "";   // Files tab is a tree, not a flat category filter

            _currentTab = targetTab;
            // Full tab list, Temp and Events included: the old inline version omitted them,
            // so a summary click while either was active left it still styled as active.
            foreach (Button tb in AllTabButtons())
                tb.Style = (Style)FindResource("TabBtnStyle");
            (toFiles ? TabFiles : TabAll).Style = (Style)FindResource("TabBtnActiveStyle");

            RefreshCurrentTab();
            UpdateSummary(); // re-render to update background on all rows
        }

        /// <summary>The clickable summary rows' categories, in the order they appear.</summary>
        private List<string> SummaryFilterKeys()
        {
            var keys = new List<string>();
            if (SummaryPanel == null) return keys;
            foreach (var child in SummaryPanel.Children)
                if (child is Border b && b.Tag is string cat && cat.Length > 0)
                    keys.Add(cat);
            return keys;
        }

        private void NavigateSummary(int dir)
        {
            var keys = SummaryFilterKeys();
            if (keys.Count == 0) return;
            int i = _summaryNavIndex >= 0 && _summaryNavIndex < keys.Count
                ? _summaryNavIndex
                : keys.IndexOf(_summaryFilter);          // fall back to whatever is highlighted
            int ni = Math.Clamp(i + dir, 0, keys.Count - 1);
            if (ni == i && i >= 0) return;               // already at the end; stay put
            ApplySummaryFilter(keys[ni], toggle: false);
        }

        private void NavigateTab(int dir)
        {
            var tabs = AllTabButtons()
                .Where(b => b != null && b.Visibility == Visibility.Visible && b.IsEnabled)
                .ToList();
            if (tabs.Count == 0) return;
            int i = tabs.FindIndex(b => string.Equals(b.Tag?.ToString(), _currentTab, StringComparison.OrdinalIgnoreCase));
            int ni = Math.Clamp(i + dir, 0, tabs.Count - 1);
            if (ni == i && i >= 0) return;
            Tab_Click(tabs[ni], new RoutedEventArgs());
            tabs[ni].Focus();   // keep the strip focused so further arrows keep stepping
        }

        /// <summary>True when the arrows belong to whatever is focused - a list being
        /// browsed, a tree being expanded, a field being edited - and must not be stolen.</summary>
        private static bool IsInsideItemsBrowser(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is ListBox or TreeView or DataGrid or ComboBox
                    or System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or Slider)
                    return true;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OnValidateKeyNav(object sender, System.Windows.Input.KeyEventArgs e)
        {
            bool vertical   = e.Key is System.Windows.Input.Key.Down  or System.Windows.Input.Key.Up;
            bool horizontal = e.Key is System.Windows.Input.Key.Right or System.Windows.Input.Key.Left;
            if (!vertical && !horizontal) return;
            if (System.Windows.Input.Keyboard.FocusedElement is DependencyObject f && IsInsideItemsBrowser(f)) return;

            if (vertical) NavigateSummary(e.Key == System.Windows.Input.Key.Down  ? +1 : -1);
            else          NavigateTab(    e.Key == System.Windows.Input.Key.Right ? +1 : -1);
            e.Handled = true;
        }

        // Label on one line, value on next line — for long values like App Name
        private void AddAppInfoRow(string label, string value)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0,4,0,2) };
            var labelTb = new TextBlock
            {
                Text       = label,
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0,0,0,1),
            };
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            var valueTb = new TextBlock
            {
                Text         = value,
                FontSize     = 11,
                FontWeight   = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            valueTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(labelTb);
            panel.Children.Add(valueTb);
            SummaryPanel.Children.Add(panel);
        }

        // ── Import: open a .svr to edit and rebuild ──
        private void BtnImport_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Import .svr",
                Filter = "QA Snapshot|*.svr|All files|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            LoadQasnapForEdit(dlg.FileName);
        }

        // ── Create MSI button: confirm/edit metadata, pick output, then build ──
        // ════════════════════════════════════════════════════════════
        //  CREATE QASNAP / SAVE — persist the capture as a .svr (no MSI)
        // ════════════════════════════════════════════════════════════

        private bool _qasnapDirty = false;   // unsaved check/uncheck or add since last save
        private bool _runSystemContext = false;  // captured at Execute time: run via PsExec -s
        private string _sourceKeyword = "";       // app keyword for filtering other-app Uninstall noise

        // Marks the capture dirty (called on check/uncheck and manual add) and enables Save once a
        // qasnap exists.
        private void MarkQasnapDirty()
        {
            _qasnapDirty = true;
            if (BtnSaveQasnap != null && !string.IsNullOrEmpty(_lastSnapPath))
                BtnSaveQasnap.IsEnabled = true;
        }

        // Resolves the folder where .svr (and MSI) files are saved: SVRPath from
        // ValidateSource.json, else the default C:\Temp\Validate Source. Creates it if needed.
        private string ResolveQasnapOutputDir()
        {
            const string DefaultDir = @"C:\Temp\Validate Source";
            string dir = "";
            try
            {
                string qp = Environment.ExpandEnvironmentVariables(_svCfg?.QasnapPath?.Trim() ?? "");
                if (!string.IsNullOrEmpty(qp)) { Directory.CreateDirectory(qp); dir = qp; }
            }
            catch { }
            // Empty/missing SVRPath → default to C:\Temp\Validate Source (matches EtwQaCapture
            // and Validate Package, so captures land where Validate Package looks for them).
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(DefaultDir); dir = DefaultDir; }
                catch { }
            }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                try { dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } catch { }
            }
            return dir;
        }

        // ── Create Qasnap: metadata dialog → write .svr to the configured folder ──
        private void BtnCreateQasnap_Click(object s, RoutedEventArgs e)
        {
            if (_allItems.Count == 0)
            {
                SvShowMessage("Create SVR", "Nothing to save yet. Capture a source or add items first.", "ℹ️");
                return;
            }

            // Pre-fill with the package app info (first app) or the current source.
            string srcPath = TxtSourceFile?.Text?.Trim() ?? "";
            var (appName, appVendor, appVersion) = _packageAppInfo ?? ResolveAppInfo(srcPath);

            var edited = ShowMsiMetadataDialog(appName, appVendor, appVersion);
            if (edited == null) return;
            (appName, appVendor, appVersion) = edited.Value;

            // Persist as the package app info (so the Summary + later actions match) and refresh.
            _packageAppInfo = (appName, appVendor, appVersion);
            UpdateSummary();

            string outDir = ResolveQasnapOutputDir();
            if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir))
            {
                SvShowMessage("Create SVR", "Could not determine where to save the .svr. Check your Validate Source (SVRPath) folder.");
                return;
            }

            string baseName   = BuildStandardBaseName(appVendor, appName, appVersion);
            string qasnapPath = IOPath.Combine(outDir, baseName + ".svr");

            // If the target already exists, let the user overwrite, pick a new name, or cancel.
            if (File.Exists(qasnapPath))
            {
                string? target = ResolveSvrTarget(qasnapPath);
                if (target == null) return;   // Cancel: nothing is written
                qasnapPath = target;
            }

            try
            {
                // If renaming from an old snapshot, drop the stale one to keep a single file.
                if (!string.IsNullOrEmpty(_lastSnapPath)
                    && !string.Equals(_lastSnapPath, qasnapPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(_lastSnapPath))
                {
                    try { File.Delete(_lastSnapPath); } catch { }
                }
                // Auto Package stands down while the .svr is written: it is the same
                // snapshot data an auto capture would touch. The scope releases on every
                // exit, exception included.
                using (AppBusy.Manual("Validate Source (Create SVR)"))
                    WriteQasnap(qasnapPath, onlyIncluded: true);
                _lastSnapPath  = qasnapPath;
                _qasnapDirty   = false;
                if (BtnSaveQasnap != null) BtnSaveQasnap.IsEnabled = true;
                SetFooterStatus($"Saved: {qasnapPath}");
                SvShowMessage("Create SVR", $"Saved:\n{qasnapPath}", "💾");
            }
            catch (Exception ex)
            {
                SvShowMessage("Create SVR", $"Could not save the .svr:\n{ex.Message}");
            }
        }

        // ── Save: re-save the existing .svr (no dialog) ──
        private void BtnSaveQasnap_Click(object s, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastSnapPath))
            {
                // No qasnap yet — fall back to Create Qasnap.
                BtnCreateQasnap_Click(s, e);
                return;
            }
            try
            {
                WriteQasnap(_lastSnapPath);
                _qasnapDirty = false;
                SetFooterStatus($"Saved: {_lastSnapPath}");
            }
            catch (Exception ex)
            {
                SvShowMessage("Save", $"Could not save the .svr:\n{ex.Message}");
            }
        }

        // Overwrite confirmation (returns true to proceed). Uses the branded confirm dialog.
        // Shown when the target .svr already exists. Returns the path to write to:
        //   • the original path  (Overwrite),
        //   • a user-chosen path (Browse / Save As, pre-filled with the standard name),
        //   • or null            (Cancel — nothing is written).
        private string? ResolveSvrTarget(string defaultPath)
        {
            string? chosen = null;
            var dlg = SvBuildDialog("Source Validation - Overwrite");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(dlg, "Source Validation - Overwrite", "⚠️");
            int r = 0;
            SvNdAddRow(cg, r++, "Overwrite",
                $"A file already exists:\n{defaultPath}\n\nOverwrite it, choose a new name with Browse, or cancel?",
                lblFg, boxBg, boxBdr, valFg, false);
            SvNdAddSpacer(cg, r++, 4);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = SvNdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = SvNdMakeButtons(sec,
                ("✔ Overwrite", () => { chosen = defaultPath; dlg.Close(); }),
                ("📂 Browse", () =>
                {
                    var sf = new Microsoft.Win32.SaveFileDialog
                    {
                        Title           = "Save SVR As",
                        Filter          = "SVR files (*.svr)|*.svr|All files (*.*)|*.*",
                        DefaultExt      = ".svr",
                        FileName        = IOPath.GetFileNameWithoutExtension(defaultPath),   // e.g. VideoLAN_VLCmediaplayer_3.0.23
                        AddExtension    = true,
                        OverwritePrompt = true,
                    };
                    try
                    {
                        string d = IOPath.GetDirectoryName(defaultPath) ?? "";
                        if (Directory.Exists(d)) sf.InitialDirectory = d;
                    }
                    catch { }
                    if (sf.ShowDialog() == true) { chosen = sf.FileName; dlg.Close(); }
                }),
                ("✕ Cancel", () => { chosen = null; dlg.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            dlg.ShowDialog();
            return chosen;
        }

        // Prompt to save unsaved changes when leaving the page (switching to another feature).
        private void ValidateSourcePage_Unloaded(object sender, RoutedEventArgs e)
        {
            PromptSaveIfDirty();
        }

        // Public so the shell (MainWindow) can call it just before navigating away from this page,
        // since pages are cached and the Unloaded event may not fire on navigation.
        public void PromptSaveIfDirty()
        {
            if (!_qasnapDirty || _allItems.Count == 0) return;

            bool save = false;
            var dlg = SvBuildDialog("Source Validation - Unsaved Changes");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(dlg, "Source Validation - Unsaved Changes", "💾");
            int r = 0;
            SvNdAddRow(cg, r++, "Save",
                "You have unsaved changes to the capture. Save them to the .svr?",
                lblFg, boxBg, boxBdr, valFg, false);
            SvNdAddSpacer(cg, r++, 4);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = SvNdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = SvNdMakeButtons(sec,
                ("✔ Save",       () => { save = true;  dlg.Close(); }),
                ("✕ Don't Save", () => { save = false; dlg.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            dlg.ShowDialog();

            if (save)
            {
                if (!string.IsNullOrEmpty(_lastSnapPath))
                {
                    try { WriteQasnap(_lastSnapPath); _qasnapDirty = false; } catch { }
                }
                else
                {
                    // No existing file — create one in the configured folder with the standard name.
                    try
                    {
                        var (n, v, ver) = _packageAppInfo ?? ResolveAppInfo(TxtSourceFile?.Text?.Trim() ?? "");
                        string outDir = ResolveQasnapOutputDir();
                        if (!string.IsNullOrEmpty(outDir))
                        {
                            string qp = IOPath.Combine(outDir, BuildStandardBaseName(v, n, ver) + ".svr");
                            WriteQasnap(qp); _lastSnapPath = qp; _qasnapDirty = false;
                        }
                    }
                    catch { }
                }
            }
        }

        private void BtnCreateMsi_Click(object s, RoutedEventArgs e)
        {
            if (_allItems.Count == 0)
            {
                SvShowMessage("Create MSI", "Nothing captured yet. Run a capture or open a .svr first.", "ℹ️");
                return;
            }

            string srcPath = TxtSourceFile?.Text?.Trim() ?? "";
            // Pre-fill with the package's app info (the first app's, as shown in the Summary). Fall
            // back to reading the current source if it isn't known yet.
            var (appName, appVendor, appVersion) = _packageAppInfo ?? ResolveAppInfo(srcPath);

            // Show the editable metadata dialog. Returns null if the user cancels.
            var edited = ShowMsiMetadataDialog(appName, appVendor, appVersion);
            if (edited == null) return;
            (appName, appVendor, appVersion) = edited.Value;

            // Persist the (possibly edited) info as THE package app info so the Summary reflects it
            // and future MSI builds reuse it. Refresh the summary to show the change immediately.
            _packageAppInfo = (appName, appVendor, appVersion);
            UpdateSummary();

            // The MSI is saved automatically next to the .svr (same folder, same standard name),
            // so the optional MSI and its source snapshot always live together — no "where to save"
            // prompt. Standard name: AppVendor_AppName_AppVersion (matches the .svr).
            string msiBaseName = BuildStandardBaseName(appVendor, appName, appVersion);

            // Output folder: the folder of the current .svr (its canonical home — the same
            // place the capture wrote it, e.g. the Validate Source folder from ValidateSource.json).
            // Falls back through the source file's folder, the configured Validate Source folder,
            // and finally the Desktop — so Create MSI works even when only manually-added items
            // exist (no capture was run, so there's no snapshot or source file yet).
            string msiOutDir = "";
            if (!string.IsNullOrEmpty(_lastSnapPath))
            {
                try { msiOutDir = IOPath.GetDirectoryName(_lastSnapPath) ?? ""; } catch { }
            }
            if (string.IsNullOrEmpty(msiOutDir) || !Directory.Exists(msiOutDir))
            {
                try { msiOutDir = IOPath.GetDirectoryName(srcPath) ?? ""; } catch { msiOutDir = ""; }
            }
            if (string.IsNullOrEmpty(msiOutDir) || !Directory.Exists(msiOutDir))
            {
                // Configured output folder from ValidateSource.json. QasnapPath is the folder where
                // .svr files are saved, so the MSI belongs there too. Fall back to SourcePath
                // (which may be a file or a folder) if QasnapPath isn't set.
                try
                {
                    string qp = Environment.ExpandEnvironmentVariables(_svCfg?.QasnapPath?.Trim() ?? "");
                    if (!string.IsNullOrEmpty(qp))
                    {
                        Directory.CreateDirectory(qp);   // create it if it doesn't exist yet
                        if (Directory.Exists(qp)) msiOutDir = qp;
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(msiOutDir) || !Directory.Exists(msiOutDir))
            {
                try
                {
                    string cfg = Environment.ExpandEnvironmentVariables(_svCfg?.SourcePath?.Trim() ?? "");
                    if (!string.IsNullOrEmpty(cfg))
                        msiOutDir = Directory.Exists(cfg) ? cfg : (IOPath.GetDirectoryName(cfg) ?? "");
                }
                catch { msiOutDir = ""; }
            }
            if (string.IsNullOrEmpty(msiOutDir) || !Directory.Exists(msiOutDir))
            {
                // Last resort: the Desktop, so a manual-only package still has somewhere to land.
                try { msiOutDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
                catch { msiOutDir = ""; }
            }
            if (string.IsNullOrEmpty(msiOutDir) || !Directory.Exists(msiOutDir))
            {
                SvShowMessage("Source Validation", "Could not determine where to save the MSI. Capture a source first, or check your Validate Source folder.");
                return;
            }

            var progressWin = ShowProcessingWindow();
            SetFooterStatus("Building MSI…");
            // Claimed before the build thread starts and released in its finally below, so
            // the lock spans the whole background build rather than just this method.
            AppBusy.SetManual("Validate Source (Create MSI)", true);

            var staThread = new Thread(() =>
            {
                // Shell icon APIs (SHGetFileInfo with SHGFI_ICON) require OLE to be initialized on
                // the calling thread, not just STA COM. Initialize it here so per-shortcut icon
                // extraction works on this background build thread.
                try { OleInitialize(IntPtr.Zero); } catch { }
                try { CreateMsi(srcPath, msiOutDir, msiBaseName, appName, appVendor, appVersion, progressWin); }
                finally
                {
                    try { OleUninitialize(); } catch { }
                    AppBusy.SetManual("Validate Source (Create MSI)", false);
                    Dispatcher.Invoke(() =>
                    {
                        try { progressWin.Close(); } catch { }
                        SetFooterStatus("MSI build finished");
                    });
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();
        }

        /// <summary>
        /// Modal dialog with three editable text boxes (App Name / Vendor / Version),
        /// pre-filled from the resolved metadata. Returns the edited values, or null if
        /// the user cancels. Uses the same styled shell as the other Source Validation dialogs.
        /// </summary>
        private (string Name, string Vendor, string Version)? ShowMsiMetadataDialog(
            string name, string vendor, string version)
        {
            (string, string, string)? result = null;

            var win = SvBuildDialog("Source Validation - Create MSI");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(win, "Source Validation - Create MSI", "📦");

            // Helper: add a labelled editable textbox row, return the TextBox.
            TextBox AddEditRow(int row, string label, string value)
            {
                cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var lbl = new TextBlock
                {
                    Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 0)
                };
                var box = new Border
                {
                    Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 3, 0, 3)
                };
                var tb = new TextBox
                {
                    Text = value ?? "", FontSize = 11, BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent, Foreground = valFg,
                    VerticalAlignment = VerticalAlignment.Center
                };
                box.Child = tb;
                Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
                Grid.SetRow(box, row); Grid.SetColumn(box, 1);
                cg.Children.Add(lbl); cg.Children.Add(box);
                return tb;
            }

            int r = 0;
            var tbName    = AddEditRow(r++, "App Name",    name);
            var tbVendor  = AddEditRow(r++, "App Vendor",  vendor);
            var tbVersion = AddEditRow(r++, "App Version", version);
            SvNdAddSpacer(cg, r++, 4);

            // Buttons row
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var icon = SvNdDecisionIcon();
            Grid.SetRow(icon, r); Grid.SetColumn(icon, 0);
            cg.Children.Add(icon);

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = SvNdMakeButtons(sec,
                ("✔ Create", () =>
                {
                    result = (tbName.Text.Trim(), tbVendor.Text.Trim(), tbVersion.Text.Trim());
                    win.Close();
                }),
                ("✕ Cancel", () => { result = null; win.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            win.ShowDialog();
            return result.HasValue ? (result.Value.Item1, result.Value.Item2, result.Value.Item3)
                                   : ((string, string, string)?)null;
        }

        /// <summary>
        /// Triggers a debounced auto-save of the current .svr file whenever the user
        /// includes, excludes, checks, or unchecks any item in the tree/list.
        /// Only fires if a capture has already produced a valid _lastSnapPath.
        /// Uses a 500 ms debounce so rapid checkbox changes coalesce into one write.
        /// </summary>
        private void AutoSaveQasnap()
        {
            // DIAGNOSTIC: record every call + the current snapshot path so we can confirm the
            // toggle chain reaches here and whether a target file is set.
            try
            {
                File.AppendAllText(
                    IOPath.Combine(IOPath.GetTempPath(), "SwiftPack_AutoSave.log"),
                    $"[{DateTime.Now:HH:mm:ss}] CALL  _lastSnapPath='{_lastSnapPath}'{Environment.NewLine}");
            }
            catch { }

            // Any call here means the capture changed (toggle/add) — mark dirty and enable Save.
            MarkQasnapDirty();

            if (string.IsNullOrEmpty(_lastSnapPath)) return; // nothing to save yet

            if (_autoSaveTimer == null)
            {
                _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _autoSaveTimer.Tick += (_, _) =>
                {
                    _autoSaveTimer.Stop();
                    if (string.IsNullOrEmpty(_lastSnapPath)) return;
                    string diag = IOPath.Combine(IOPath.GetTempPath(), "SwiftPack_AutoSave.log");
                    try
                    {
                        WriteQasnap(_lastSnapPath);
                        _qasnapDirty = false;   // persisted
                        LogAction($"Auto-saved: {_lastSnapPath}");
                        // Flush a line straight to disk so auto-save activity is visible immediately
                        // (the in-memory action log isn't written until capture stops).
                        try { File.AppendAllText(diag, $"[{DateTime.Now:HH:mm:ss}] OK  {_lastSnapPath}{Environment.NewLine}"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        LogAction($"Auto-save FAILED for {_lastSnapPath}: {ex.Message}");
                        try { File.AppendAllText(diag, $"[{DateTime.Now:HH:mm:ss}] FAIL {_lastSnapPath} :: {ex.Message}{Environment.NewLine}"); } catch { }
                    }
                };
            }

            // Restart the timer — debounces rapid successive changes
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        private void WriteQasnap(string path, bool onlyIncluded = false)
        {
            // Save ALL captured items (included AND excluded) so the .svr is a faithful,
            // re-editable source. An ExcludedPaths list records which items were unchecked, so
            // a re-import restores the exact include/exclude state and the user can re-include
            // anything they previously excluded. (Earlier this saved only included items, which
            // permanently dropped excluded files from the snapshot.)
            //
            // onlyIncluded=true produces a clean package definition (Create SVR): unchecked items
            // are omitted entirely - the .svr then contains only what will actually be packaged.
            var all = onlyIncluded
                ? _allItems.Values.Where(i => i.IsIncluded).ToList()
                : _allItems.Values.ToList();
            var excludedKeys = onlyIncluded
                ? new List<string>()
                : all.Where(i => !i.IsIncluded)
                     .Select(i => i.Category + "|" + i.DisplayPath + "|" + i.Value)
                     .ToList();
            var snap = new
            {
                SnapVersion  = "1.0",
                FileName      = IOPath.GetFileName(TxtSourceFile.Text.Trim()),
                FilePath      = TxtSourceFile.Text.Trim(),
                Arguments    = "",
                CaptureTime  = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                Files        = all.Where(i => i.Category == "File")     .Select(i => i.DisplayPath).ToList(),
                Folders      = all.Where(i => i.Category == "Folder")   .Select(i => i.DisplayPath).ToList(),
                Shortcuts    = all.Where(i => i.Category == "Shortcut") .Select(i => i.DisplayPath).ToList(),
                Registry     = all.Where(i => i.Category == "Registry") .Select(i => new { Key = i.DisplayPath, Value = i.Value, Data = i.Data, Type = i.DataType }).ToList(),
                EnvVars      = all.Where(i => i.Category == "EnvVar")   .Select(i => new { Name = i.DisplayPath, Value = i.Value }).ToList(),
                Services     = all.Where(i => i.Category == "Service")  .Select(i => new { Name = i.DisplayPath, DisplayName = i.Value, Binary = i.DataType, StartType = i.Data }).ToList(),
                Certificates = all.Where(i => i.Category == "Certificate").Select(i => i.DisplayPath).ToList(),
                ExcludedPaths = excludedKeys,
                // Manually-added files carry a SourceOnDisk (where to read content from) distinct
                // from DisplayPath (install destination). Persist these so re-import restores them.
                ManualFiles  = all.Where(i => i.Category == "File" && i.IsManual && !string.IsNullOrEmpty(i.SourceOnDisk))
                                  .Select(i => new { Dest = i.DisplayPath, Source = i.SourceOnDisk }).ToList(),
                // Manually-added registry values (so their Value/Data/Type round-trip).
                ManualRegistry = all.Where(i => i.Category == "Registry" && i.IsManual)
                                  .Select(i => new { Key = i.DisplayPath, Value = i.Value, Data = i.Data, Type = i.DataType }).ToList(),
                // Package app info shown in the Summary, so re-import restores it.
                AppName    = _packageAppInfo?.Name    ?? "",
                AppVendor  = _packageAppInfo?.Vendor  ?? "",
                AppVersion = _packageAppInfo?.Version ?? "",
            };
            File.WriteAllText(path, JsonSerializer.Serialize(snap,
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        // ── Create MSI ────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════
        //  CreateMsi — WixToolset.Dtf.WindowsInstaller (managed wrapper over msi.dll)
        //
        //  The full table schema, all INSERT shapes, the x64;1033 summary template,
        //  and the cab-stream embed in this method were validated end-to-end against
        //  the libmsi engine (msibuild/msiinfo) before shipping — the produced MSI
        //  opens, its Property/File/Registry/Shortcut rows persist, and the embedded
        //  cab extracts back intact.
        //
        //  Requires NuGet: WixToolset.Dtf.WindowsInstaller (v6.0.2+).
        //  Commercial use over US$10k/yr revenue → pay the OSMF via GitHub Sponsors.
        // ════════════════════════════════════════════════════════════════════
        private void CreateMsi(string srcPath, string outputDir, string productName,
                               string appName = "", string appVendor = "", string appVersion = "",
                               Window? progressWin = null)
        {
            // Log file named per app to match the MSI/qasnap standard name:
            // CreateMSI_<Vendor>_<Name>_<Version>.log in %TEMP%.
            string logBase = BuildStandardBaseName(appVendor, appName, appVersion);
            string _logPath = IOPath.Combine(IOPath.GetTempPath(), $"CreateMSI_{logBase}.log");
            void Log(string s)
            {
                try { File.AppendAllText(_logPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {s}{Environment.NewLine}", new UTF8Encoding(false)); }
                catch { }
            }
            try { File.WriteAllText(_logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] === CreateMsi START  out={outputDir}  name={productName} ==={Environment.NewLine}",
                new UTF8Encoding(false)); } catch { }

            try
            {
                Log("collect included items");
                _pmDirIdByPath.Clear();
                _pmDirCounter = 0;
                var inc = _allItems.Values.Where(i => i.IsIncluded).ToList();

                // ── Metadata: prefer passed-in values, fall back to captured registry ──
                string prodName = !string.IsNullOrWhiteSpace(appName) ? appName
                    : (inc.FirstOrDefault(i => i.Category == "Registry" && i.Value == "DisplayName")?.Data ?? productName);
                if (string.IsNullOrWhiteSpace(prodName)) prodName = productName;

                string ver = !string.IsNullOrWhiteSpace(appVersion) ? appVersion
                    : (inc.FirstOrDefault(i => i.Category == "Registry" && i.Value == "DisplayVersion")?.Data ?? "1.0.0");
                ver = SanitiseMsiVersion(ver);

                string mfr = !string.IsNullOrWhiteSpace(appVendor) ? appVendor
                    : (inc.FirstOrDefault(i => i.Category == "Registry" && i.Value == "Publisher")?.Data ?? "Unknown");
                if (string.IsNullOrWhiteSpace(mfr)) mfr = "Unknown";

                Log($"metadata: name='{prodName}' ver='{ver}' mfr='{mfr}'");

                string pguid       = NewGuidBraced();   // ProductCode: new each version (major-upgrade model)
                string uguid       = StableUpgradeGuid(prodName, mfr);  // UpgradeCode: STABLE per app
                string packageCode = NewGuidBraced();
                Log($"UpgradeCode (stable for '{prodName}'/'{mfr}'): {uguid}");

                string msiPath = IOPath.Combine(outputDir, productName + ".msi");
                if (File.Exists(msiPath)) { try { File.Delete(msiPath); } catch { } }

                // ── 1. Stage payload (flat short keys f0,f1,…) ──
                Log("stage payload");
                string payloadDir = IOPath.Combine(outputDir, productName + "_payload");
                if (Directory.Exists(payloadDir)) { try { Directory.Delete(payloadDir, true); } catch { } }
                Directory.CreateDirectory(payloadDir);

                var fileRows = new List<MsiFileRow>();
                int fkey = 0;
                var skipped = new List<string>();   // "path  — reason" for each skipped file
                var exeKeys = new List<(string Key, string ExePath)>();

                foreach (var f in inc.Where(i => i.Category == "File"))
                {
                    try
                    {
                        // Captured files read content from DisplayPath (where they were installed).
                        // Manually-added files read from SourceOnDisk (where the user picked them);
                        // DisplayPath then holds the desired install destination on the target.
                        string srcOnDisk = !string.IsNullOrEmpty(f.SourceOnDisk) ? f.SourceOnDisk : f.DisplayPath;
                        if (!File.Exists(srcOnDisk)) { skipped.Add($"{srcOnDisk}  — not found on disk"); continue; }
                        string key = "f" + (fkey++);
                        string dst = IOPath.Combine(payloadDir, key);
                        File.Copy(srcOnDisk, dst, overwrite: true);
                        var fi = new FileInfo(dst);
                        fileRows.Add(new MsiFileRow
                        {
                            Key       = key,
                            SourceAbs = dst,
                            FileName  = IOPath.GetFileName(f.DisplayPath),
                            Folder    = IOPath.GetDirectoryName(f.DisplayPath) ?? "",
                            SizeBytes = (int)Math.Min(fi.Length, int.MaxValue),
                        });
                        if (f.DisplayPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            exeKeys.Add((key, srcOnDisk));
                    }
                    catch (Exception ex) { skipped.Add($"{f.DisplayPath}  — {ex.Message}"); }
                }
                if (skipped.Count > 0)
                {
                    Log($"skipped {skipped.Count} file(s):");
                    foreach (var sk in skipped) Log("  SKIP " + sk);
                }

                if (fileRows.Count == 0)
                {
                    Dispatcher.Invoke(() => { try { progressWin?.Close(); } catch { } });
                    Dispatcher.Invoke(() => VsStyledMessage(null, "Create MSI",
                        "No files were available to stage (source may already be uninstalled).\nMSI not created.", "⚠"));
                    return;
                }

                // ── 2. Build cabinet via makecab ──
                Log($"build cab from {fileRows.Count} files");
                string cabPath = IOPath.Combine(payloadDir, "payload.cab");
                if (!BuildCab(payloadDir, fileRows, cabPath, _logPath))
                {
                    Dispatcher.Invoke(() => { try { progressWin?.Close(); } catch { } });
                    Dispatcher.Invoke(() => VsStyledMessage(null, "Create MSI",
                        "Failed to build payload cabinet (makecab).\nMSI not created.", "⚠"));
                    return;
                }

                // ── 3. Author the MSI database via DTF ──
                Log("create DTF database");
                using (var db = new WixToolset.Dtf.WindowsInstaller.Database(
                                    msiPath, WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode.Create))
                {
                    Log("schema");
                    foreach (var ddl in MsiSchemaDdl())
                        db.Execute(ddl);

                    // ── Standard Windows Installer tables, created EMPTY ──────────────────────
                    // The 15 tables above are the ones this builder actually populates. A vendor
                    // MSI shows 80+ tables in Orca not because it uses them, but because its build
                    // tool seeds the database from the full Windows Installer schema and leaves
                    // most of it empty. Do the same, for two reasons:
                    //   1. The generated MSI reads as a normal MSI in Orca / InstEd / Edit MSI.
                    //   2. A packager can add a CustomAction, Environment, RemoveFile, ... row in
                    //      Edit MSI without having to CREATE TABLE first.
                    //
                    // This does NOT change what gets installed. No rows are inserted into any of
                    // them and no sequenced action reads an empty table, so the deployed result is
                    // identical to before. Each statement is executed under its own try/catch so a
                    // single CREATE TABLE the engine dislikes is logged and skipped instead of
                    // aborting MSI creation.
                    int stdOk = 0, stdFail = 0;
                    foreach (var ddl in MsiStandardEmptyTablesDdl())
                    {
                        try { db.Execute(ddl); stdOk++; }
                        catch (Exception tex) { stdFail++; Log($"  standard table skipped ({tex.Message}): {ddl}"); }
                    }
                    Log($"standard tables: {stdOk} created" + (stdFail > 0 ? $", {stdFail} skipped" : ""));

                    Log("property table");
                    InsertProperty(db, "ProductName",     prodName);
                    InsertProperty(db, "ProductVersion",  ver);
                    InsertProperty(db, "ProductCode",     pguid);
                    InsertProperty(db, "UpgradeCode",     uguid);
                    InsertProperty(db, "Manufacturer",    mfr);
                    InsertProperty(db, "ProductLanguage", "1033");
                    InsertProperty(db, "ALLUSERS",        "1");

                    Log("directory roots");
                    InsertDirectory(db, "TARGETDIR", null, "SourceDir");
                    InsertDirectory(db, "ProgramFiles64Folder", "TARGETDIR", ".");
                    InsertDirectory(db, "ProgramFilesFolder",   "TARGETDIR", ".");
                    InsertDirectory(db, "LocalAppDataFolder",   "TARGETDIR", ".");
                    InsertDirectory(db, "AppDataFolder",        "TARGETDIR", ".");
                    InsertDirectory(db, "CommonAppDataFolder",  "TARGETDIR", ".");
                    InsertDirectory(db, "ProgramMenuFolder",    "TARGETDIR", ".");
                    InsertDirectory(db, "DesktopFolder",        "TARGETDIR", ".");

                    var dirIdByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int dirCounter  = 0;
                    string EnsureDir(string absoluteFolder)
                    {
                        absoluteFolder = absoluteFolder.TrimEnd('\\');
                        if (string.IsNullOrEmpty(absoluteFolder)) return "ProgramFiles64Folder";
                        if (dirIdByPath.TryGetValue(absoluteFolder, out var existing)) return existing;

                        var (rootId, rootPath) = MapToStandardDir(absoluteFolder);

                        string remainder;
                        string parentId;
                        string accum;

                        if (rootPath == null)
                        {
                            // NON-STANDARD absolute path (e.g. C:\Temp\MSI, D:\Tools). Anchor it at
                            // the system drive via MSI's built-in WindowsVolume property instead of
                            // hardcoding the drive letter, so the package installs to the equivalent
                            // path on whatever drive Windows lives on (portable across machines).
                            const string wvKey = "WINVOL_ROOT";
                            if (!dirIdByPath.TryGetValue(wvKey, out var wvId))
                            {
                                // "WindowsVolume" is a standard MSI public property resolving to the
                                // Windows drive root (e.g. C:\). A Directory row with this id +
                                // DefaultDir "." roots subsequent folders at that volume.
                                wvId = "WindowsVolume";
                                InsertDirectory(db, wvId, "TARGETDIR", ".");
                                dirIdByPath[wvKey] = wvId;
                            }
                            parentId  = wvId;
                            accum     = wvKey;
                            // Drop the drive root ("C:\") and keep the rest of the path.
                            string root = IOPath.GetPathRoot(absoluteFolder) ?? "";
                            remainder = root.Length > 0 && absoluteFolder.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                                ? absoluteFolder.Substring(root.Length).Trim('\\')
                                : absoluteFolder.Trim('\\');
                        }
                        else
                        {
                            parentId  = rootId!;
                            accum     = rootPath ?? "";
                            remainder = rootPath.Length > 0 &&
                                        absoluteFolder.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                                ? absoluteFolder.Substring(rootPath.Length).Trim('\\')
                                : IOPath.GetFileName(absoluteFolder);
                        }

                        if (!string.IsNullOrEmpty(remainder))
                        {
                            foreach (var part in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                            {
                                accum = accum.Length == 0 ? part : accum + "\\" + part;
                                if (!dirIdByPath.TryGetValue(accum, out var dId))
                                {
                                    dId = "d" + (dirCounter++);
                                    InsertDirectory(db, dId, parentId, MsiSafeDefaultDir(part));
                                    dirIdByPath[accum] = dId;
                                }
                                parentId = dId;
                            }
                        }
                        dirIdByPath[absoluteFolder] = parentId;
                        return parentId;
                    }

                    InsertFeature(db, "MainFeature", prodName);

                    Log("components/files");
                    int seq = 1, compCounter = 0;
                    var compByFileKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var fr in fileRows)
                    {
                        string dirId  = EnsureDir(fr.Folder);
                        string compId = "c" + (compCounter++);
                        compByFileKey[fr.Key] = compId;
                        InsertComponent(db, compId, NewGuidBraced(), dirId, fr.Key);
                        InsertFeatureComponent(db, "MainFeature", compId);
                        InsertFile(db, fr.Key, compId, MsiSafeFileName(fr.FileName), fr.SizeBytes, seq++);
                    }

                    // ── Icon: point the Icon table at the main exe itself ──
                    // Windows extracts the icon from a PE file natively, so an Icon row whose
                    // Name ends in ".exe" and whose Data is the exe stream gives both ARP
                    // (ARPPRODUCTICON) and shortcuts (Shortcut.Icon_) the real app icon —
                    // far more reliable than extracting a .ico (which often yields an icon
                    // ARP won't render). Pick the "main" exe: best name match, else largest.
                    Log("icon");
                    string? iconId = null;
                    try
                    {
                        (string Key, string ExePath)? mainExe = null;
                        if (exeKeys.Count > 0)
                        {
                            string prodKey = new string(prodName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                            mainExe = exeKeys
                                .OrderByDescending(e =>
                                {
                                    string en = new string(IOPath.GetFileNameWithoutExtension(e.ExePath)
                                                    .Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                                    int score = 0;
                                    // Strongly prefer an exe that actually contains an icon — many apps
                                    // ship an iconless console exe (e.g. a command-line build) alongside the
                                    // real GUI exe (7zFM.exe). Without this the ARP icon comes out blank.
                                    if (ExeHasIcon(e.ExePath)) score += 100000;
                                    if (prodKey.Length > 0 && (en.Contains(prodKey) || prodKey.Contains(en))) score += 1000;
                                    try { score += (int)Math.Min(new FileInfo(e.ExePath).Length / 1024, 90000); } catch { }
                                    return score;
                                })
                                .First();
                        }

                        if (mainExe != null && File.Exists(mainExe.Value.ExePath))
                        {
                            iconId = "appicon.exe";   // Name MUST end in .exe so Windows reads the PE icon
                            InsertIcon(db, iconId, mainExe.Value.ExePath);
                            InsertProperty(db, "ARPPRODUCTICON", iconId);
                            Log($"  icon from {IOPath.GetFileName(mainExe.Value.ExePath)} → {iconId}");
                        }
                        else { iconId = null; Log("  no exe available for icon"); }
                    }
                    catch (Exception icoEx) { iconId = null; Log($"  icon skipped: {icoEx.Message}"); }

                    Log("registry");
                    // Windows Installer creates its OWN Add/Remove Programs (ARP) entry for THIS
                    // MSI (via ProductCode + RegisterProduct). If we also wrote the captured
                    // "...\CurrentVersion\Uninstall\<ThisApp>" values, the install would produce a
                    // SECOND, duplicate ARP entry pointing at the original (now-removed) uninstaller.
                    // So we skip ONLY the Uninstall key that matches THIS MSI's own app name — other
                    // apps' Uninstall keys (from a merged multi-installer capture) are legitimate
                    // captured data the user chose to include and are kept.
                    string ownAppNorm = new string((prodName ?? "").ToLowerInvariant()
                        .Where(c => char.IsLetterOrDigit(c)).ToArray());
                    bool IsOwnArpKey(string path)
                    {
                        int idx = path.IndexOf(@"\Uninstall\", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) return false;
                        if (string.IsNullOrEmpty(ownAppNorm)) return false;
                        // The ARP subkey is the segment right after "...\Uninstall\".
                        string after = path.Substring(idx + @"\Uninstall\".Length);
                        string seg   = after.Split('\\')[0];
                        string segNorm = new string(seg.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                        // Match if the ARP subkey corresponds to this MSI's own app (contains the
                        // app name, ignoring GUID-style keys and punctuation/case).
                        return segNorm.Length > 0 &&
                               (segNorm.Contains(ownAppNorm) || ownAppNorm.Contains(segNorm));
                    }

                    var regItems = inc.Where(i => i.Category == "Registry"
                                              && !string.IsNullOrEmpty(i.Value) && i.Value != "(Default)"
                                              && !IsOwnArpKey(i.DisplayPath)).ToList();
                    int arpSkipped = inc.Count(i => i.Category == "Registry" && IsOwnArpKey(i.DisplayPath));
                    if (arpSkipped > 0) Log($"  skipped {arpSkipped} ARP/Uninstall registry value(s) for this app to avoid a duplicate Add/Remove entry");
                    if (regItems.Count > 0)
                    {
                        InsertComponent(db, "cReg", NewGuidBraced(), "ProgramFiles64Folder", null);
                        InsertFeatureComponent(db, "MainFeature", "cReg");
                        int rIdx = 0;
                        foreach (var r in regItems)
                        {
                            int root = r.DisplayPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) ? 2
                                     : r.DisplayPath.StartsWith("HKEY_CLASSES_ROOT",  StringComparison.OrdinalIgnoreCase) ? 0
                                     : r.DisplayPath.StartsWith("HKEY_USERS",         StringComparison.OrdinalIgnoreCase) ? 3
                                     : 1;
                            string subKey = string.Join("\\", r.DisplayPath.Split('\\').Skip(1));
                            InsertRegistry(db, "r" + (rIdx++), root, subKey, r.Value, r.Data, "cReg");
                        }
                    }

                    Log("shortcuts");
                    var shortcutItems = inc.Where(i => i.Category == "Shortcut").ToList();
                    if (shortcutItems.Count > 0)
                    {
                        // Map a resolved target's absolute path to a staged file key, so the
                        // shortcut points at the real file (not a guessed exe).
                        var fileKeyByAbsPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var fr in fileRows)
                            fileKeyByAbsPath[IOPath.Combine(fr.Folder, fr.FileName)] = fr.Key;

                        // Each Start-menu subfolder needs its own component (advertised shortcuts
                        // require a component whose directory is the shortcut's directory).
                        var shortcutCompByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        int scCompCounter = 0;
                        int sIdx = 0;

                        // Per-build icon cache: maps an absolute icon-source file → (IconId, IconIndex),
                        // so each distinct icon source is streamed into the Icon table only once.
                        var iconBySource = new Dictionary<string, (string Id, int Index)>(StringComparer.OrdinalIgnoreCase);
                        int scIconCounter = 0;

                        // Resolve a .lnk's IconLocation ("path,index") to an Icon-table id + index.
                        // Streams the icon source into the Icon table on first use. Returns the
                        // product icon (iconId/0) as a fallback when the .lnk has no usable icon.
                        (string? Id, int Index) ResolveShortcutIcon(string iconLocation, string targetAbsForFallback, string targetRealName)
                        {
                            string srcPath = ""; int srcIndex = 0;
                            if (!string.IsNullOrWhiteSpace(iconLocation))
                            {
                                // IconLocation is "path,index"; index defaults to 0.
                                int comma = iconLocation.LastIndexOf(',');
                                if (comma > 0)
                                {
                                    srcPath = iconLocation.Substring(0, comma).Trim().Trim('"');
                                    int.TryParse(iconLocation.Substring(comma + 1).Trim(), out srcIndex);
                                }
                                else srcPath = iconLocation.Trim().Trim('"');
                            }

                            // Expand environment variables (e.g. %SystemRoot%\System32\SHELL32.dll).
                            // Without this, File.Exists fails and the icon wrongly falls back.
                            if (!string.IsNullOrWhiteSpace(srcPath))
                                srcPath = Environment.ExpandEnvironmentVariables(srcPath);

                            // Internet Shortcuts (.url) and .html/.htm targets have NO icon stored in
                            // the .lnk — Windows shows a standard document/Internet icon derived from
                            // the target's file type. Neither WScript.Shell nor IShellLink return one.
                            // So when no explicit icon was found and the target is such a type, use the
                            // standard Windows icon from SHELL32.dll (index 13 = generic Internet/HTML
                            // document) so the shortcut matches what the source displays.
                            string tExt = IOPath.GetExtension(targetRealName).ToLowerInvariant();
                            bool targetIsWebDoc = tExt is ".url" or ".html" or ".htm" or ".mht";
                            bool targetIsHelp   = tExt is ".chm" or ".hlp";
                            if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
                            {
                                if (targetIsWebDoc)
                                {
                                    string shell32 = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\SHELL32.dll");
                                    if (File.Exists(shell32)) { srcPath = shell32; srcIndex = 13; }
                                }
                                else if (targetIsHelp)
                                {
                                    // .chm/.hlp use the HTML Help viewer's icon (hh.exe). That is the
                                    // icon Windows shows for help files in the source Start Menu.
                                    string hh = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\hh.exe");
                                    if (File.Exists(hh)) { srcPath = hh; srcIndex = 0; }
                                }
                            }

                            // If still nothing, use the actual target file (so e.g. the target exe's
                            // icon is used).
                            if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
                            {
                                if (!string.IsNullOrWhiteSpace(targetAbsForFallback) && File.Exists(targetAbsForFallback))
                                { srcPath = targetAbsForFallback; srcIndex = 0; }
                            }
                            if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
                                return (iconId, 0);   // product-exe fallback

                            string ext = IOPath.GetExtension(srcPath).ToLowerInvariant();
                            if (ext != ".exe" && ext != ".dll" && ext != ".ico")
                                return (iconId, 0);   // unsupported icon container → fallback

                            string cacheKey = srcPath + "|" + srcIndex;
                            if (iconBySource.TryGetValue(cacheKey, out var cached))
                                return (cached.Id, cached.Index);

                            // For a small .ico/.exe/.dll, embed the file directly (Windows reads the
                            // icon at IconIndex). For a LARGE source (e.g. SHELL32.dll ~9 MB), don't
                            // embed the whole binary — extract just the one icon at srcIndex to a tiny
                            // .ico and embed that instead. Index 0 then refers to the extracted icon.
                            long size = 0;
                            try { size = new FileInfo(srcPath).Length; } catch { return (iconId, 0); }

                            string embedPath; int embedIndex;
                            if (size <= 2 * 1024 * 1024)
                            {
                                embedPath = srcPath; embedIndex = srcIndex;
                            }
                            else
                            {
                                string outIco = IOPath.Combine(payloadDir, $"scico{scIconCounter}.ico");
                                if (ExtractIconIndexToFile(srcPath, srcIndex, outIco))
                                { embedPath = outIco; embedIndex = 0; ext = ".ico"; }
                                else
                                {
                                    Log($"    icon extract failed ({IOPath.GetFileName(srcPath)},{srcIndex}) → product fallback");
                                    return (iconId, 0);
                                }
                            }

                            string id = "ico" + (scIconCounter++) + ext;
                            try
                            {
                                InsertIcon(db, id, embedPath);
                                cached = (id, embedIndex);
                                iconBySource[cacheKey] = cached;
                                Log($"    icon source {IOPath.GetFileName(srcPath)},{srcIndex} → {id} (idx {embedIndex})");
                                return (cached.Id, cached.Index);
                            }
                            catch (Exception iex)
                            {
                                Log($"    icon source failed ({IOPath.GetFileName(srcPath)}): {iex.Message}");
                                return (iconId, 0);
                            }
                        }

                        foreach (var sc in shortcutItems)
                        {
                            // 1. Resolve the .lnk's real target, args, and icon from disk (COM).
                            var (targetAbs, lnkArgs, _, lnkIconLoc) = ResolveLnk(sc.DisplayPath);
                            string scBaseName = IOPath.GetFileNameWithoutExtension(sc.DisplayPath);
                            string? targetKey = null;

                            // 2a. Standard .lnk with a resolvable file target → match to a staged file.
                            if (!string.IsNullOrEmpty(targetAbs))
                            {
                                if (!fileKeyByAbsPath.TryGetValue(targetAbs, out targetKey))
                                {
                                    string tgtName = IOPath.GetFileName(targetAbs);
                                    var byName = fileRows.FirstOrDefault(fr =>
                                        fr.FileName.Equals(tgtName, StringComparison.OrdinalIgnoreCase));
                                    if (byName != null) targetKey = byName.Key;
                                }
                            }

                            // 2b. Internet Shortcut (.url) or unresolved target: WScript.Shell returns
                            //     an empty TargetPath for Internet Shortcuts. The Start-menu .lnk almost
                            //     always shares its base name with the staged target file
                            //     (e.g. "Documentation.lnk" → "Documentation.url"). Match by base name
                            //     against any staged file so .url/.html/.txt shortcuts still resolve.
                            if (string.IsNullOrEmpty(targetKey))
                            {
                                var byBase = fileRows.FirstOrDefault(fr =>
                                    IOPath.GetFileNameWithoutExtension(fr.FileName)
                                          .Equals(scBaseName, StringComparison.OrdinalIgnoreCase));
                                if (byBase != null)
                                {
                                    targetKey = byBase.Key;
                                    Log($"  shortcut '{scBaseName}' matched staged file by base name → {byBase.FileName}");
                                }
                            }

                            if (string.IsNullOrEmpty(targetKey))
                            {
                                Log($"  shortcut SKIP (no staged target): {sc.DisplayPath}  (resolved target='{targetAbs}')");
                                continue;
                            }

                            // 3. Place the shortcut in its REAL captured subfolder under the Start Menu.
                            //    e.g. ...\Start Menu\Programs\Smekal\Documentation.lnk
                            //         → ProgramMenuFolder\Smekal
                            string lnkDir = IOPath.GetDirectoryName(sc.DisplayPath) ?? "";
                            string shortcutDirId = EnsureProgramMenuSubDir(db, lnkDir);

                            // 4. Component for this shortcut's directory (one per dir).
                            if (!shortcutCompByDir.TryGetValue(shortcutDirId, out var compId))
                            {
                                compId = "cSc" + (scCompCounter++);
                                InsertComponent(db, compId, NewGuidBraced(), shortcutDirId, null);
                                InsertFeatureComponent(db, "MainFeature", compId);
                                // Registry keypath anchors the advertised-shortcut component.
                                InsertRegistry(db, "rk" + compId, 1,
                                    $"Software\\{mfr}\\{prodName}\\Shortcuts", compId, "1", compId);
                                shortcutCompByDir[shortcutDirId] = compId;
                            }

                            // Find the real staged file behind targetKey so the icon resolver can
                            // tell whether the shortcut points at a .url / Internet shortcut (which
                            // has no stored icon and should use the Windows default document icon).
                            var targetRow = fileRows.FirstOrDefault(fr => fr.Key == targetKey);
                            string targetRealName = targetRow?.FileName ?? "";

                            // PRIMARY icon source: ask the shell for the icon it actually DISPLAYS for
                            // this shortcut (same call the UI's Open column uses). This is correct for
                            // every target type (.lnk/.url/.chm/...) without guessing an icon location.
                            string? scIconId = null; int scIconIndex = 0;
                            try
                            {
                                string shellIco = IOPath.Combine(payloadDir, $"scico{scIconCounter}.ico");
                                if (ExtractShellIconToIco(sc.DisplayPath, shellIco))
                                {
                                    string sid = "ico" + (scIconCounter++) + ".ico";
                                    InsertIcon(db, sid, shellIco);
                                    scIconId = sid; scIconIndex = 0;
                                    Log($"    icon source SHELL(displayed) → {sid}");
                                }
                            }
                            catch (Exception sex) { Log($"    shell icon failed: {sex.Message}"); }

                            // Fallback: the icon-location resolver (handles cases where the shell call
                            // could not produce an icon).
                            if (scIconId == null)
                            {
                                var (rid, ridx) = ResolveShortcutIcon(lnkIconLoc, targetAbs, targetRealName);
                                scIconId = rid; scIconIndex = ridx;
                            }

                            InsertShortcut(db, "s" + (sIdx++), shortcutDirId, scBaseName, compId, "[#" + targetKey + "]",
                                           arguments: lnkArgs, iconId: scIconId, iconIndex: scIconIndex);
                            Log($"  shortcut: '{scBaseName}' in {shortcutDirId} → [#{targetKey}]"
                                + (string.IsNullOrWhiteSpace(lnkArgs) ? "" : $" args='{lnkArgs}'")
                                + (string.IsNullOrEmpty(scIconId) ? "" : $" icon={scIconId},{scIconIndex}"));
                        }
                    }

                    Log("services");
                    var serviceItems = inc.Where(i => i.Category == "Service").ToList();
                    int svcIdx = 0;
                    foreach (var sv in serviceItems)
                    {
                        string binary = sv.DataType ?? "";
                        if (string.IsNullOrWhiteSpace(binary)) continue;
                        var fr = fileRows.FirstOrDefault(x =>
                            string.Equals(IOPath.Combine(x.Folder, x.FileName), binary, StringComparison.OrdinalIgnoreCase));
                        if (fr == null) continue;
                        if (!compByFileKey.TryGetValue(fr.Key, out var ownerComp)) continue;
                        InsertServiceInstall(db, "svc" + svcIdx, sv.DisplayPath, sv.Value, MapServiceStart(sv.Data), ownerComp);
                        InsertServiceControl(db, "svcc" + svcIdx, sv.DisplayPath, ownerComp);
                        svcIdx++;
                    }

                    Log("media + sequences");
                    InsertMedia(db, 1, seq - 1, "#payload.cab");

                    // ── Upgrade table: detect and remove any prior install of this app ──
                    // One row covering every version BELOW the current one (VersionMax = ver,
                    // exclusive). Attributes: 256 = msidbUpgradeAttributesVersionMaxInclusive is
                    // NOT set (so the current version itself isn't matched) plus 1 = migrate
                    // feature states. ActionProperty receives the found ProductCode; pairing it
                    // with RemoveExistingProducts (sequenced after InstallInitialize) makes the
                    // new install replace the old one instead of installing side-by-side.
                    InsertUpgrade(db, uguid, "", ver, attributes: 1, actionProperty: "SWP_OLDERFOUND");
                    // Also detect a SAME-OR-NEWER version already installed (attr 2 = OnlyDetect,
                    // 256 = VersionMinInclusive) so a downgrade is detected; property set, no remove.
                    InsertUpgrade(db, uguid, ver, "", attributes: 2 + 256, actionProperty: "SWP_NEWERFOUND");
                    InsertProperty(db, "SecureCustomProperties", "SWP_OLDERFOUND;SWP_NEWERFOUND");
                    Log("upgrade table authored");

                    foreach (var (action, cond, sequence) in InstallExecuteSequenceRows())
                        InsertSequence(db, "InstallExecuteSequence", action, cond, sequence);
                    foreach (var (action, cond, sequence) in InstallUISequenceRows())
                        InsertSequence(db, "InstallUISequence", action, cond, sequence);

                    // Minimal install UI so a double-click shows a progress bar and a finish
                    // dialog instead of nothing. Safe to fail: if it throws, the dialogs are never
                    // sequenced and the package installs silently exactly as it did before.
                    Log(AuthorMinimalUi(db, Log)
                        ? "minimal UI authored (ProgressDlg / ExitDialog / FatalError / UserExit)"
                        : "minimal UI NOT authored - package installs silently");

                    // ── Summary information (typed) — set after all table writes ──
                    Log("summary information");
                    var si = db.SummaryInfo;
                    si.CodePage       = 1252;
                    si.Title          = prodName;
                    si.Subject        = prodName;
                    si.Author         = mfr;
                    si.Keywords       = "Installer";
                    si.Comments       = prodName;
                    si.Template       = "x64;1033";
                    si.RevisionNumber = packageCode;
                    si.PageCount      = 200;   // min installer version
                    si.WordCount      = 2;     // 2 = files compressed in cab
                    // NOTE: do NOT call si.Persist() here. DTF's Database.Commit() persists
                    // the summary info itself, and SummaryInfo.Persist may only be called once —
                    // calling it explicitly causes Commit to throw "Function failed during execution".
                    Log("summary set");

                    Log("import cab stream");
                    ImportCabStream(db, cabPath, "payload.cab");

                    Log("commit");
                    db.Commit();
                    Log("commit done");
                }

                // ── Write the .svr alongside the .msi (same base name) ──
                // This is the editable source the user re-imports later to rebuild.
                // WriteQasnap touches a UI element, so marshal it to the UI thread.
                try
                {
                    string qasnapPath = IOPath.Combine(outputDir, productName + ".svr");
                    Dispatcher.Invoke(() => WriteQasnap(qasnapPath));
                    Log($"qasnap written: {qasnapPath}");

                    // If the user changed the app name in the Create MSI dialog, the capture-time
                    // qasnap (saved under the old standard name) is now stale and would leave a
                    // duplicate. Remove it so only the MSI's qasnap (same name as the .msi) remains.
                    try
                    {
                        if (!string.IsNullOrEmpty(_lastSnapPath)
                            && !string.Equals(_lastSnapPath, qasnapPath, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(_lastSnapPath))
                        {
                            File.Delete(_lastSnapPath);
                            Log($"removed stale capture qasnap: {_lastSnapPath}");
                        }
                    }
                    catch (Exception delEx) { Log($"stale qasnap cleanup skipped: {delEx.Message}"); }
                    _lastSnapPath = qasnapPath;
                }
                catch (Exception qex) { Log($"qasnap write skipped: {qex.Message}"); }

                // ── Clean up the temporary payload folder ──
                // The staged file copies and the cab are now inside the MSI's embedded
                // _Streams; the payload folder is build scratch and is no longer needed.
                // Only the .msi (and the user's .svr) are kept.
                try
                {
                    if (Directory.Exists(payloadDir))
                    {
                        Directory.Delete(payloadDir, true);
                        Log("payload folder deleted");
                    }
                }
                catch (Exception cleanupEx) { Log($"payload cleanup skipped: {cleanupEx.Message}"); }

                Dispatcher.Invoke(() => { try { progressWin?.Close(); } catch { } });

                if (File.Exists(msiPath))
                {
                    SetFooterStatus($"MSI created: {msiPath}");
                    var skippedCopy = skipped.ToList();
                    Dispatcher.Invoke(() => ShowMsiResultDialog(msiPath, _logPath, skippedCopy, error: null));
                }
                else
                    Dispatcher.Invoke(() => ShowMsiResultDialog(msiPath, _logPath, skipped.ToList(),
                        error: "MSI authoring completed but file not found on disk."));
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                Log($"STACK: {ex.StackTrace}");
                Dispatcher.Invoke(() => { try { progressWin?.Close(); } catch { } });
                Dispatcher.Invoke(() => ShowMsiResultDialog("", _logPath, new List<string>(),
                    error: ex.Message));
            }
        }

        // ── Styled MSI result dialog — same shell as the qasnap-complete dialog ──
        private void ShowMsiResultDialog(string msiPath, string logPath, List<string> skipped, string? error)
        {
            bool ok = error == null && File.Exists(msiPath);
            string titleText = "Source Validation - " + (ok ? "MSI Created" : "MSI Error");
            var win = SvBuildDialog(titleText);
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(win, titleText, "📦");

            int r = 0;

            if (ok)
            {
                SvNdAddRow(cg, r++, "MSI", msiPath, lblFg, boxBg, boxBdr, valFg, true);
                SvNdAddSpacer(cg, r++);

                if (skipped.Count == 0)
                {
                    SvNdAddRow(cg, r++, "Files", "All included files were packaged.", lblFg, boxBg, boxBdr, valFg, false);
                }
                else
                {
                    // List the skipped file paths so the user knows exactly what was left out.
                    // A blank line between entries keeps them readable when there are several.
                    string skippedText = string.Join("\n\n", skipped);
                    SvNdAddRow(cg, r++, $"Skipped ({skipped.Count})", skippedText, lblFg, boxBg, boxBdr, valFg, true,
                               scrollable: true, maxHeight: 220, selectable: true);
                }
                SvNdAddSpacer(cg, r++);
            }
            else
            {
                SvNdAddRow(cg, r++, "Error", error ?? "Unknown error.", lblFg, boxBg, boxBdr, valFg, false);
                SvNdAddSpacer(cg, r++);
            }

            // Log row
            SvNdAddRow(cg, r++, "Log", string.IsNullOrEmpty(logPath) ? "-" : logPath, lblFg, boxBg, boxBdr, valFg, false);
            SvNdAddSpacer(cg, r++, 4);

            // Icon + buttons row (mirrors the qasnap dialog)
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = SvNdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var buttonList = new List<(string label, Action onClick)>();
            if (ok)
                buttonList.Add(("📂 Open Path", () =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe",
                        $"/select,\"{msiPath}\"") { UseShellExecute = true }); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ValidateSource] open path failed: " + ex.Message); }
                }));
            if (File.Exists(logPath))
                buttonList.Add(("📄 Open Log", () =>
                {
                    try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ValidateSource] open log failed: " + ex.Message); }
                }));
            buttonList.Add(("✕ Close", () => win.Close()));

            var btns = SvNdMakeButtons(sec, buttonList.ToArray());
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            win.ShowDialog();
        }

        private static string NewGuidBraced() => $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}";

        /// <summary>
        /// Produces a STABLE, deterministic UpgradeCode for a product, derived from its
        /// name + vendor. The same app always yields the same UpgradeCode across versions,
        /// so a newer MSI's RemoveExistingProducts can find and replace the older install
        /// (instead of leaving two side-by-side entries in Add/Remove Programs).
        /// Uses a fixed namespace so the value is reproducible and unique to SwiftPack output.
        /// </summary>
        private static string StableUpgradeGuid(string name, string vendor)
        {
            // Namespace prefix keeps these GUIDs distinct from any other tool's scheme.
            string seed = "SwiftPackStudio|UpgradeCode|" + (vendor ?? "").Trim().ToLowerInvariant()
                        + "|" + (name ?? "").Trim().ToLowerInvariant();
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
            // 16 bytes → GUID. Set version (5) and variant bits so it's a well-formed GUID.
            hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            var g = new Guid(hash);
            return $"{{{g.ToString().ToUpperInvariant()}}}";
        }

        /// <summary>
        /// Reads a .lnk shortcut's real target path and working directory from disk using
        /// the WScript.Shell COM object (the standard Windows shortcut API). Returns empty
        /// strings if the file is missing or cannot be read.
        /// </summary>
        private static (string TargetPath, string Arguments, string WorkingDir, string IconLocation) ResolveLnk(string lnkPath)
        {
            if (string.IsNullOrEmpty(lnkPath) || !File.Exists(lnkPath))
                return ("", "", "", "");
            object? shell = null;
            try
            {
                Type? t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return ("", "", "", "");
                shell = Activator.CreateInstance(t);
                dynamic sh = shell!;
                dynamic lnk = sh.CreateShortcut(lnkPath);
                string target = (string)(lnk.TargetPath ?? "");
                string args   = (string)(lnk.Arguments ?? "");
                string wdir   = (string)(lnk.WorkingDirectory ?? "");
                string icon   = "";
                try { icon = (string)(lnk.IconLocation ?? ""); } catch { }   // "path,index"

                // WScript.Shell's IconLocation is often empty (or ",0") even when the shortcut
                // shows a custom icon. Always also read it from the .lnk binary via IShellLink
                // and prefer that when it yields a real path. Diagnostic logging records both.
                string wscriptIcon = icon;
                string slIcon = ReadLnkIconViaShellLink(lnkPath);
                bool wscriptUseless = string.IsNullOrWhiteSpace(icon) || icon.StartsWith(",") || icon.Trim() == ",0";
                if (wscriptUseless && !string.IsNullOrWhiteSpace(slIcon))
                    icon = slIcon;
                return (target, args, wdir, icon);
            }
            catch { return ("", "", "", ""); }
            finally
            {
                if (shell != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
                    catch { }
                }
            }
        }

        // ── IShellLinkW / IPersistFile COM interop to read a .lnk's icon location ──
        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFile);
        }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("0000010b-0000-0000-C000-000000000046")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [System.Runtime.InteropServices.PreserveSig] int IsDirty();
            void Load([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        /// <summary>
        /// Returns the .lnk's icon location as "path,index" read straight from the shortcut
        /// binary via IShellLink (authoritative — matches the Properties dialog). Empty if none.
        /// </summary>
        private static string ReadLnkIconViaShellLink(string lnkPath)
        {
            IShellLinkW? sl = null;
            try
            {
                sl = (IShellLinkW)new ShellLink();
                ((IPersistFile)sl).Load(lnkPath, 0);
                var sb = new System.Text.StringBuilder(1024);
                sl.GetIconLocation(sb, sb.Capacity, out int idx);
                string path = sb.ToString();
                return string.IsNullOrWhiteSpace(path) ? "" : $"{path},{idx}";
            }
            catch { return ""; }
            finally
            {
                if (sl != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(sl); } catch { }
                }
            }
        }

        /// <summary>
        /// Maps a captured Start-menu folder (the parent of a .lnk) to an MSI Directory id under
        /// ProgramMenuFolder, creating the nested Directory rows as needed. For example a .lnk in
        /// "...\Start Menu\Programs\Smekal" yields ProgramMenuFolder\Smekal. A .lnk directly in
        /// "...\Programs" (or on the Desktop) yields ProgramMenuFolder itself.
        /// </summary>
        private readonly Dictionary<string, string> _pmDirIdByPath =
            new(StringComparer.OrdinalIgnoreCase);
        private int _pmDirCounter;
        private string EnsureProgramMenuSubDir(WixToolset.Dtf.WindowsInstaller.Database db,
            string lnkFolderAbs)
        {
            if (string.IsNullOrEmpty(lnkFolderAbs)) return "ProgramMenuFolder";

            // Desktop shortcuts (Public Desktop or a user's Desktop) install to DesktopFolder.
            if (lnkFolderAbs.IndexOf(@"\Desktop", StringComparison.OrdinalIgnoreCase) >= 0)
                return "DesktopFolder";

            // Find the portion after "...\Start Menu\Programs".
            const string marker = @"\Start Menu\Programs";
            int idx = lnkFolderAbs.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            string remainder;
            if (idx >= 0)
                remainder = lnkFolderAbs.Substring(idx + marker.Length).Trim('\\');
            else
                // Unknown location → drop directly under ProgramMenuFolder.
                remainder = "";

            if (string.IsNullOrEmpty(remainder))
                return "ProgramMenuFolder";

            string parentId = "ProgramMenuFolder";
            string accum    = "";
            foreach (var part in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                accum = accum.Length == 0 ? part : accum + "\\" + part;
                if (!_pmDirIdByPath.TryGetValue(accum, out var dId))
                {
                    dId = "pm" + (_pmDirCounter++);
                    InsertDirectory(db, dId, parentId, MsiSafeDefaultDir(part));
                    _pmDirIdByPath[accum] = dId;
                }
                parentId = dId;
            }
            return parentId;
        }

        private sealed class MsiFileRow
        {
            public string Key = "", SourceAbs = "", FileName = "", Folder = "";
            public int SizeBytes;
        }

        // ── makecab: build one cabinet from staged files ──
        private bool BuildCab(string payloadDir, List<MsiFileRow> rows, string cabPath, string logPath)
        {
            void Log(string m) { try { File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}]   cab: {m}{Environment.NewLine}", new UTF8Encoding(false)); } catch { } }
            try
            {
                string ddf = IOPath.Combine(payloadDir, "payload.ddf");
                var sb = new StringBuilder();
                sb.AppendLine(".OPTION EXPLICIT");
                sb.AppendLine($".Set CabinetNameTemplate=\"{IOPath.GetFileName(cabPath)}\"");
                sb.AppendLine($".Set DiskDirectory1=\"{payloadDir}\"");
                sb.AppendLine(".Set Cabinet=on");
                sb.AppendLine(".Set Compress=on");
                sb.AppendLine(".Set CompressionType=MSZIP");
                sb.AppendLine(".Set MaxDiskSize=0");
                sb.AppendLine(".Set MaxCabinetSize=0");
                sb.AppendLine(".Set FolderSizeThreshold=0");
                sb.AppendLine(".Set UniqueFiles=off");
                sb.AppendLine(".Set InfFileName=payload.inf");
                sb.AppendLine(".Set RptFileName=payload.rpt");
                foreach (var r in rows)
                    sb.AppendLine($"\"{r.SourceAbs}\" {r.Key}");
                File.WriteAllText(ddf, sb.ToString(), new UTF8Encoding(false));

                Log($"makecab start ({rows.Count} files)");
                bool ok = RunProcess("makecab.exe", $"/f \"{ddf}\"", payloadDir, 600_000);
                bool exists = File.Exists(cabPath);
                Log($"makecab done ok={ok} cabExists={exists}");
                return exists;
            }
            catch (Exception ex) { Log($"EXCEPTION {ex.Message}"); return false; }
        }

        // ════════════════════════════════════════════════════════════════════
        //  DTF insert helpers — parameter markers (?) bound via a typed Record.
        //  (Validated: this INSERT shape parses and persists on the libmsi engine.)
        // ════════════════════════════════════════════════════════════════════
        private static void DtfInsert(WixToolset.Dtf.WindowsInstaller.Database db,
            string table, (string Col, object? Val)[] cols)
        {
            var colList = string.Join(", ", cols.Select(c => "`" + c.Col + "`"));
            var qList   = string.Join(", ", cols.Select(_ => "?"));
            string sql  = $"INSERT INTO `{table}` ({colList}) VALUES ({qList})";
            using var view = db.OpenView(sql);
            using var rec  = new WixToolset.Dtf.WindowsInstaller.Record(cols.Length);
            for (int i = 0; i < cols.Length; i++)
            {
                object? v = cols[i].Val;
                if (v == null)        continue;
                else if (v is int iv) rec.SetInteger(i + 1, iv);
                else                  rec.SetString(i + 1, v.ToString());
            }
            view.Execute(rec);
        }

        private static void InsertProperty(WixToolset.Dtf.WindowsInstaller.Database db, string name, string value) =>
            DtfInsert(db, "Property", new (string, object?)[] { ("Property", name), ("Value", value) });

        private static void InsertDirectory(WixToolset.Dtf.WindowsInstaller.Database db, string dir, string? parent, string defaultDir) =>
            DtfInsert(db, "Directory", new (string, object?)[]
            { ("Directory", dir), ("Directory_Parent", parent), ("DefaultDir", defaultDir) });

        private static void InsertFeature(WixToolset.Dtf.WindowsInstaller.Database db, string feature, string title) =>
            DtfInsert(db, "Feature", new (string, object?)[]
            { ("Feature", feature), ("Feature_Parent", null), ("Title", title),
              ("Description", null), ("Display", 1), ("Level", 1), ("Directory_", null), ("Attributes", 0) });

        private static void InsertComponent(WixToolset.Dtf.WindowsInstaller.Database db, string comp, string guid, string dir, string? keyPathFileKey) =>
            DtfInsert(db, "Component", new (string, object?)[]
            { ("Component", comp), ("ComponentId", guid), ("Directory_", dir),
              ("Attributes", 256 /* 64-bit */), ("Condition", null),
              ("KeyPath", string.IsNullOrEmpty(keyPathFileKey) ? (object?)null : keyPathFileKey) });

        private static void InsertFeatureComponent(WixToolset.Dtf.WindowsInstaller.Database db, string feature, string comp) =>
            DtfInsert(db, "FeatureComponents", new (string, object?)[] { ("Feature_", feature), ("Component_", comp) });

        private static void InsertFile(WixToolset.Dtf.WindowsInstaller.Database db, string fileKey, string comp, string fileName, int size, int seq) =>
            DtfInsert(db, "File", new (string, object?)[]
            { ("File", fileKey), ("Component_", comp), ("FileName", fileName),
              ("FileSize", size), ("Version", null), ("Language", null),
              ("Attributes", 512 /* Compressed */), ("Sequence", seq) });

        private static void InsertMedia(WixToolset.Dtf.WindowsInstaller.Database db, int disk, int lastSeq, string cabinet) =>
            DtfInsert(db, "Media", new (string, object?)[]
            { ("DiskId", disk), ("LastSequence", lastSeq), ("DiskPrompt", null),
              ("Cabinet", cabinet), ("VolumeLabel", null), ("Source", null) });

        private static void InsertUpgrade(WixToolset.Dtf.WindowsInstaller.Database db,
            string upgradeCode, string versionMin, string versionMax, int attributes, string actionProperty) =>
            DtfInsert(db, "Upgrade", new (string, object?)[]
            { ("UpgradeCode", upgradeCode),
              ("VersionMin", string.IsNullOrEmpty(versionMin) ? (object?)null : versionMin),
              ("VersionMax", string.IsNullOrEmpty(versionMax) ? (object?)null : versionMax),
              ("Language", null), ("Attributes", attributes),
              ("Remove", null), ("ActionProperty", actionProperty) });

        private static void InsertRegistry(WixToolset.Dtf.WindowsInstaller.Database db, string key, int root, string subKey, string name, string value, string comp) =>
            DtfInsert(db, "Registry", new (string, object?)[]
            { ("Registry", key), ("Root", root), ("Key", subKey),
              ("Name", name), ("Value", value), ("Component_", comp) });

        private static void InsertShortcut(WixToolset.Dtf.WindowsInstaller.Database db, string key, string dir, string name, string comp, string target,
                                           string? arguments = null, string? wkDir = null, string? iconId = null, int iconIndex = 0) =>
            DtfInsert(db, "Shortcut", new (string, object?)[]
            { ("Shortcut", key), ("Directory_", dir), ("Name", name),
              ("Component_", comp), ("Target", target),
              ("Arguments", string.IsNullOrWhiteSpace(arguments) ? (object?)null : arguments),
              ("Description", null), ("Hotkey", null),
              ("Icon_", string.IsNullOrWhiteSpace(iconId) ? (object?)null : iconId),
              ("IconIndex", string.IsNullOrWhiteSpace(iconId) ? (object?)null : iconIndex),
              ("ShowCmd", null),
              ("WkDir", string.IsNullOrWhiteSpace(wkDir) ? (object?)null : wkDir) });

        private static void InsertServiceInstall(WixToolset.Dtf.WindowsInstaller.Database db, string key, string name, string display, int startType, string comp) =>
            DtfInsert(db, "ServiceInstall", new (string, object?)[]
            { ("ServiceInstall", key), ("Name", name), ("DisplayName", display),
              ("ServiceType", 0x10), ("StartType", startType), ("ErrorControl", 1),
              ("LoadOrderGroup", null), ("Dependencies", null), ("StartName", null),
              ("Password", null), ("Arguments", null), ("Component_", comp), ("Description", null) });

        private static void InsertServiceControl(WixToolset.Dtf.WindowsInstaller.Database db, string key, string name, string comp) =>
            DtfInsert(db, "ServiceControl", new (string, object?)[]
            { ("ServiceControl", key), ("Name", name), ("Event", 0xA0),
              ("Arguments", null), ("Wait", 1), ("Component_", comp) });

        private static void InsertSequence(WixToolset.Dtf.WindowsInstaller.Database db, string table, string action, string? condition, int seq) =>
            DtfInsert(db, table, new (string, object?)[] { ("Action", action), ("Condition", condition), ("Sequence", seq) });

        // ══════════════════════════════════════════════════════════════════════════════════
        //  MINIMAL INSTALL UI
        // ══════════════════════════════════════════════════════════════════════════════════
        private static void InsertTextStyle(WixToolset.Dtf.WindowsInstaller.Database db, string name, string face, int size, int styleBits) =>
            DtfInsert(db, "TextStyle", new (string, object?)[] {
                ("TextStyle", name), ("FaceName", face), ("Size", size), ("Color", null), ("StyleBits", styleBits) });

        // All four dialogs use the conventional 370x270 MSI dialog canvas.
        private static void InsertDialog(WixToolset.Dtf.WindowsInstaller.Database db, string dlg,
            int attrs, string title, string first, string? def, string? cancel) =>
            DtfInsert(db, "Dialog", new (string, object?)[] {
                ("Dialog", dlg), ("HCentering", 50), ("VCentering", 50), ("Width", 370), ("Height", 270),
                ("Attributes", attrs), ("Title", title), ("Control_First", first),
                ("Control_Default", def), ("Control_Cancel", cancel) });

        private static void InsertControl(WixToolset.Dtf.WindowsInstaller.Database db, string dlg, string ctl,
            string type, int x, int y, int w, int h, int attrs, string? text) =>
            DtfInsert(db, "Control", new (string, object?)[] {
                ("Dialog_", dlg), ("Control", ctl), ("Type", type), ("X", x), ("Y", y),
                ("Width", w), ("Height", h), ("Attributes", attrs), ("Property", null),
                ("Text", text), ("Control_Next", null), ("Help", null) });

        // ControlEvent.Condition is part of the primary key and NOT NULL, so an unconditional
        // event is authored with the condition "1" rather than a null.
        private static void InsertControlEvent(WixToolset.Dtf.WindowsInstaller.Database db,
            string dlg, string ctl, string evt, string arg) =>
            DtfInsert(db, "ControlEvent", new (string, object?)[] {
                ("Dialog_", dlg), ("Control_", ctl), ("Event", evt), ("Argument", arg),
                ("Condition", "1"), ("Ordering", 1) });

        private static void InsertEventMapping(WixToolset.Dtf.WindowsInstaller.Database db,
            string dlg, string ctl, string evt, string attr) =>
            DtfInsert(db, "EventMapping", new (string, object?)[] {
                ("Dialog_", dlg), ("Control_", ctl), ("Event", evt), ("Attribute", attr) });

        private static void InsertActionText(WixToolset.Dtf.WindowsInstaller.Database db,
            string action, string desc, string? template) =>
            DtfInsert(db, "ActionText", new (string, object?)[] {
                ("Action", action), ("Description", desc), ("Template", template) });

        private static void InsertError(WixToolset.Dtf.WindowsInstaller.Database db, int err, string msg) =>
            DtfInsert(db, "Error", new (string, object?)[] { ("Error", err), ("Message", msg) });

        /// <summary>
        /// Authors a minimal install UI: ProgressDlg (progress bar + live action text), ExitDialog,
        /// FatalError and UserExit, plus the ActionText the progress dialog displays and the Error
        /// message templates.
        ///
        /// Double-clicking an MSI requests full UI level, so Windows Installer runs the
        /// InstallUISequence. That sequence previously reached ExecuteAction without ever showing a
        /// dialog, which is why a double-click produced a UAC prompt and then nothing at all.
        /// Intune is unaffected: it deploys with /qn and never runs the UI sequence.
        ///
        /// FAIL-SAFE ORDER. Every dialog / control / event / text row is written FIRST, and the four
        /// dialogs are added to InstallUISequence only after all of it has succeeded. If any part
        /// throws, the dialogs are never sequenced, so the package cannot end up referencing a
        /// half-authored dialog - it simply installs silently, exactly as it did before. Returns
        /// true when the UI was fully authored and sequenced.
        /// </summary>
        private static bool AuthorMinimalUi(WixToolset.Dtf.WindowsInstaller.Database db, Action<string> log)
        {
            try
            {
                // ── Fonts. DefaultUIFont must name a TextStyle row or text renders unstyled. ──
                InsertTextStyle(db, "DefaultFont",  "Tahoma",  8, 0);
                InsertTextStyle(db, "TahomaBold10", "Tahoma", 10, 1);   // StyleBits 1 = bold
                InsertProperty(db, "DefaultUIFont", "DefaultFont");

                // Control attribute bits used below:
                //   3     = Visible + Enabled                      (buttons)
                //   1     = Visible                                (lines)
                //   65539 = Visible + Enabled + Transparent        (static text)
                //   65537 = Visible + Progress95                   (progress bar)
                const int CTL_BUTTON = 3, CTL_LINE = 1, CTL_TEXT = 65539, CTL_PROGRESS = 65537;
                // Dialog attribute bits:
                //   5 = Visible + Minimize          (ProgressDlg MUST be modeless or it would block
                //                                    ExecuteAction from ever running)
                //   3 = Visible + Modal             (the three terminal dialogs)
                const int DLG_MODELESS = 5, DLG_MODAL = 3;

                // ── ProgressDlg ──────────────────────────────────────────────────────────────
                InsertDialog(db, "ProgressDlg", DLG_MODELESS, "[ProductName] Setup", "Cancel", null, "Cancel");
                InsertControl(db, "ProgressDlg", "Title",       "Text",        20,  15, 330, 15, CTL_TEXT,     "{\\TahomaBold10}[ProductName]");
                InsertControl(db, "ProgressDlg", "Description", "Text",        20,  35, 330, 20, CTL_TEXT,     "Please wait while [ProductName] is installed.");
                InsertControl(db, "ProgressDlg", "StatusLabel", "Text",        20, 100,  50, 10, CTL_TEXT,     "Status:");
                InsertControl(db, "ProgressDlg", "ActionText",  "Text",        75, 100, 275, 10, CTL_TEXT,     null);
                InsertControl(db, "ProgressDlg", "ProgressBar", "ProgressBar", 20, 115, 330, 10, CTL_PROGRESS, "Progress done");
                InsertControl(db, "ProgressDlg", "ActionData",  "Text",        20, 130, 330, 10, CTL_TEXT,     null);
                InsertControl(db, "ProgressDlg", "BottomLine",  "Line",         0, 234, 370,  0, CTL_LINE,     null);
                InsertControl(db, "ProgressDlg", "Cancel",      "PushButton", 304, 243,  56, 17, CTL_BUTTON,   "Cancel");
                InsertControlEvent(db, "ProgressDlg", "Cancel", "EndDialog", "Exit");
                // These three are what make the dialog live: Windows Installer pushes the current
                // action, its data and the progress ticks into the mapped controls as it runs.
                InsertEventMapping(db, "ProgressDlg", "ActionText",  "ActionText",  "Text");
                InsertEventMapping(db, "ProgressDlg", "ActionData",  "ActionData",  "Text");
                InsertEventMapping(db, "ProgressDlg", "ProgressBar", "SetProgress", "Progress");

                // ── ExitDialog (success) ─────────────────────────────────────────────────────
                InsertDialog(db, "ExitDialog", DLG_MODAL, "[ProductName] Setup", "Finish", "Finish", "Finish");
                InsertControl(db, "ExitDialog", "Title",       "Text",        20,  15, 330, 15, CTL_TEXT,   "{\\TahomaBold10}Installation Complete");
                InsertControl(db, "ExitDialog", "Description", "Text",        20,  40, 330, 40, CTL_TEXT,   "[ProductName] has been installed successfully.\n\nClick Finish to exit.");
                InsertControl(db, "ExitDialog", "BottomLine",  "Line",         0, 234, 370,  0, CTL_LINE,   null);
                InsertControl(db, "ExitDialog", "Finish",      "PushButton", 236, 243,  56, 17, CTL_BUTTON, "Finish");
                InsertControlEvent(db, "ExitDialog", "Finish", "EndDialog", "Return");

                // ── FatalError (install failed) ──────────────────────────────────────────────
                InsertDialog(db, "FatalError", DLG_MODAL, "[ProductName] Setup", "Finish", "Finish", "Finish");
                InsertControl(db, "FatalError", "Title",       "Text",        20,  15, 330, 15, CTL_TEXT,   "{\\TahomaBold10}Installation Failed");
                InsertControl(db, "FatalError", "Description", "Text",        20,  40, 330, 40, CTL_TEXT,   "[ProductName] setup ended prematurely. Your system has not been modified.\n\nClick Finish to exit.");
                InsertControl(db, "FatalError", "BottomLine",  "Line",         0, 234, 370,  0, CTL_LINE,   null);
                InsertControl(db, "FatalError", "Finish",      "PushButton", 236, 243,  56, 17, CTL_BUTTON, "Finish");
                InsertControlEvent(db, "FatalError", "Finish", "EndDialog", "Exit");

                // ── UserExit (cancelled) ─────────────────────────────────────────────────────
                InsertDialog(db, "UserExit", DLG_MODAL, "[ProductName] Setup", "Finish", "Finish", "Finish");
                InsertControl(db, "UserExit", "Title",       "Text",        20,  15, 330, 15, CTL_TEXT,   "{\\TahomaBold10}Installation Cancelled");
                InsertControl(db, "UserExit", "Description", "Text",        20,  40, 330, 40, CTL_TEXT,   "[ProductName] setup was cancelled. Your system has not been modified.\n\nClick Finish to exit.");
                InsertControl(db, "UserExit", "BottomLine",  "Line",         0, 234, 370,  0, CTL_LINE,   null);
                InsertControl(db, "UserExit", "Finish",      "PushButton", 236, 243,  56, 17, CTL_BUTTON, "Finish");
                InsertControlEvent(db, "UserExit", "Finish", "EndDialog", "Exit");

                foreach (var (action, desc, template) in ActionTextRows())
                    InsertActionText(db, action, desc, template);
                foreach (var (code, message) in ErrorRows())
                    InsertError(db, code, message);

                // ── Sequence the dialogs LAST ────────────────────────────────────────────────
                // Negative sequence numbers are the Windows Installer terminal-dialog slots:
                //   -3 = FatalError   -2 = UserExit   -1 = ExitDialog
                // ProgressDlg goes at 1299, immediately before ExecuteAction (1300), so it is on
                // screen while the execute sequence runs.
                InsertSequence(db, "InstallUISequence", "FatalError",  null, -3);
                InsertSequence(db, "InstallUISequence", "UserExit",    null, -2);
                InsertSequence(db, "InstallUISequence", "ExitDialog",  null, -1);
                InsertSequence(db, "InstallUISequence", "ProgressDlg", null, 1299);
                return true;
            }
            catch (Exception ex)
            {
                // Nothing was sequenced, so the package is still perfectly valid - it just has no UI.
                log($"minimal UI skipped: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Text shown by ProgressDlg for each standard action. Without these rows the progress
        /// dialog runs with a blank status line. Description is the status text; Template formats
        /// the detail line from the action's record fields.
        /// </summary>
        private static IEnumerable<(string Action, string Desc, string? Template)> ActionTextRows()
        {
            yield return ("AllocateRegistrySpace",  "Allocating registry space", "Free space: [1]");
            yield return ("CostFinalize",           "Computing space requirements", null);
            yield return ("CostInitialize",         "Computing space requirements", null);
            yield return ("CreateFolders",          "Creating folders", "Folder: [1]");
            yield return ("CreateShortcuts",        "Creating shortcuts", "Shortcut: [1]");
            yield return ("DeleteServices",         "Deleting services", "Service: [1]");
            yield return ("FileCost",               "Computing space requirements", null);
            yield return ("FindRelatedProducts",    "Searching for related applications", "Found application: [1]");
            yield return ("GenerateScript",         "Generating script operations for action:", "[1]");
            yield return ("InstallFiles",           "Copying new files", "File: [1],  Directory: [9],  Size: [6]");
            yield return ("InstallFinalize",        "Committing transaction", null);
            yield return ("InstallInitialize",      "Beginning transaction", null);
            yield return ("InstallServices",        "Installing new services", "Service: [2]");
            yield return ("InstallValidate",        "Validating install", null);
            yield return ("ProcessComponents",      "Updating component registration", null);
            yield return ("PublishFeatures",        "Publishing product features", "Feature: [1]");
            yield return ("PublishProduct",         "Publishing product information", null);
            yield return ("RegisterProduct",        "Registering product", "[1]");
            yield return ("RegisterUser",           "Registering user", "[1]");
            yield return ("RemoveExistingProducts", "Removing applications", "Application: [1], Command line: [2]");
            yield return ("RemoveFiles",            "Removing files", "File: [1], Directory: [9]");
            yield return ("RemoveFolders",          "Removing folders", "Folder: [1]");
            yield return ("RemoveRegistryValues",   "Removing system registry values", "Key: [1], Name: [2]");
            yield return ("RemoveShortcuts",        "Removing shortcuts", "Shortcut: [1]");
            yield return ("RollbackCleanup",        "Removing backup files", "File: [1]");
            yield return ("StartServices",          "Starting services", "Service: [1]");
            yield return ("StopServices",           "Stopping services", "Service: [1]");
            yield return ("UnpublishFeatures",      "Unpublishing product features", "Feature: [1]");
            yield return ("WriteRegistryValues",    "Writing system registry values", "Key: [1], Name: [2], Value: [3]");
        }

        /// <summary>
        /// Error message rows. Windows Installer has built-in English text for EVERY standard error
        /// code and falls back to it for any code absent from this table, so a partial table is safe
        /// and complete coverage buys nothing. What is authored here is the set that actually
        /// matters: the 0-33 formatting templates (they shape every message, including the progress
        /// dialog's "Time remaining" line) plus the runtime errors a package of this shape can
        /// really raise - file, registry, disk, privilege, shortcut and service. ODBC, COM+, patch
        /// and assembly errors are deliberately omitted: this builder never emits those components.
        /// </summary>
        private static IEnumerable<(int Code, string Message)> ErrorRows()
        {
            // Formatting templates
            yield return (0,  "{{Fatal error: }}");
            yield return (1,  "{{Error [1]. }}");
            yield return (2,  "Warning [1]. ");
            yield return (4,  "Info [1]. ");
            yield return (5,  "The installer has encountered an unexpected error installing this package. This may indicate a problem with this package. The error code is [1]. {{The arguments are: [2], [3], [4]}}");
            yield return (7,  "{{Disk full: }}");
            yield return (8,  "Action [Time]: [1]. [2]");
            yield return (9,  "[ProductName]");
            yield return (10, "{[2]}{, [3]}{, [4]}");
            yield return (11, "Message type: [1], Argument: [2]");
            yield return (12, "=== Logging started: [Date]  [Time] ===");
            yield return (13, "=== Logging stopped: [Date]  [Time] ===");
            yield return (14, "Action start [Time]: [1].");
            yield return (15, "Action ended [Time]: [1]. Return value [2].");
            yield return (16, "Time remaining: {[1] minutes }{[2] seconds}");
            yield return (17, "Out of memory. Shut down other applications before retrying.");
            yield return (18, "Installer is no longer responding.");
            yield return (19, "Installer stopped prematurely.");
            yield return (20, "Please wait while Windows configures [ProductName]");
            yield return (21, "Gathering required information...");
            yield return (22, "Removing older versions of this application...");
            yield return (23, "Preparing to remove older versions of this application...");
            yield return (32, "{[ProductName] }Setup completed successfully.");
            yield return (33, "{[ProductName] }Setup failed.");

            // File / disk
            yield return (1301, "Cannot create the file '[2]'. A directory with this name already exists. Cancel the installation and try installing to a different location.");
            yield return (1303, "The installer has insufficient privileges to access this directory: [2]. The installation cannot continue. Log on as an administrator or contact your system administrator.");
            yield return (1304, "Error writing to file: [2]. Verify that you have access to that directory.");
            yield return (1305, "Error reading from file [2]. {{ System error [3].}} Verify that the file exists and that you can access it.");
            yield return (1306, "Another application has exclusive access to the file '[2]'. Please shut down all other applications, then click Retry.");
            yield return (1307, "There is not enough disk space to install this file: [2]. Free some disk space and click Retry, or click Cancel to exit.");
            yield return (1308, "Source file not found: [2]. Verify that the file exists and that you can access it.");
            yield return (1310, "Error writing to file: [3]. {{ System error [2].}} Verify that you have access to that directory.");
            yield return (1311, "Source file not found{{(cabinet)}}: [2]. Verify that the file exists and that you can access it.");
            yield return (1312, "Cannot create the directory '[2]'. A file with this name already exists. Please rename or remove the file and click Retry, or click Cancel to exit.");
            yield return (1320, "The specified path is too long: [2]");
            yield return (1321, "The installer has insufficient privileges to modify this file: [2].");
            yield return (1331, "Failed to correctly copy [2] file: CRC error.");
            yield return (1334, "The file '[2]' cannot be installed because the file cannot be found in cabinet file '[3]'. This could indicate a network error, an error reading from the CD-ROM, or a problem with this package.");
            yield return (1335, "The cabinet file '[2]' required for this installation is corrupt and cannot be used. This could indicate a network error, an error reading from the CD-ROM, or a problem with this package.");
            yield return (1336, "There was an error creating a temporary file that is needed to complete this installation.{{ Folder: [3]. System error code: [2]}}");

            // Registry
            yield return (1401, "Could not create key: [2]. {{ System error [3].}} Verify that you have sufficient access to that key, or contact your support personnel.");
            yield return (1402, "Could not open key: [2]. {{ System error [3].}} Verify that you have sufficient access to that key, or contact your support personnel.");
            yield return (1403, "Could not delete value [2] from key [3]. {{ System error [4].}} Verify that you have sufficient access to that key, or contact your support personnel.");
            yield return (1404, "Could not delete key [2]. {{ System error [3].}} Verify that you have sufficient access to that key, or contact your support personnel.");
            yield return (1405, "Could not read value [2] from key [3]. {{ System error [4].}} Verify that you have sufficient access to that key, or contact your support personnel.");
            yield return (1406, "Could not write value [2] to key [3]. {{ System error [4].}} Verify that you have sufficient access to that key, or contact your support personnel.");

            // Disk space / files in use / privileges
            yield return (1601, "Out of disk space -- Volume: '[2]'; required space: [3] KB; available space: [4] KB. Free some disk space and retry.");
            yield return (1602, "Are you sure you want to cancel?");
            yield return (1603, "The file [2][3] is being held in use{ by the following process: Name: [4], Id: [5], Window Title: [6]}. Close that application and retry.");
            yield return (1607, "The following applications should be closed before continuing the install:");
            yield return (1704, "An installation for [2] is currently suspended. You must undo the changes made by that installation to continue. Do you want to undo those changes?");
            yield return (1706, "An installation package for the product [2] cannot be found. Try the installation again using a valid copy of the installation package '[3]'.");
            yield return (1715, "Installed [2]");
            yield return (1716, "Configured [2]");
            yield return (1717, "Removed [2]");
            yield return (1719, "The Windows Installer service could not be accessed. This can occur if the Windows Installer is not correctly installed. Contact your support personnel for assistance.");
            yield return (1728, "Configuration completed successfully.");
            yield return (1729, "Configuration failed.");
            yield return (1730, "You must be an administrator to remove this application. To remove this application, you can log on as an administrator, or contact your technical support group for assistance.");

            // Shortcut / service / environment
            yield return (1909, "Could not create shortcut [2]. Verify that the destination folder exists and that you can access it.");
            yield return (1910, "Could not remove shortcut [2]. Verify that the shortcut file exists and that you can access it.");
            yield return (1920, "Service '[2]' ([3]) failed to start. Verify that you have sufficient privileges to start system services.");
            yield return (1921, "Service '[2]' ([3]) could not be stopped. Verify that you have sufficient privileges to stop system services.");
            yield return (1922, "Service '[2]' ([3]) could not be deleted. Verify that you have sufficient privileges to remove system services.");
            yield return (1923, "Service '[2]' ([3]) could not be installed. Verify that you have sufficient privileges to install system services.");
            yield return (1924, "Could not update environment variable '[2]'. Verify that you have sufficient privileges to modify environment variables.");
            yield return (1925, "You do not have sufficient privileges to complete this installation for all users of the machine. Log on as administrator and then retry this installation.");
            yield return (1930, "The description for service '[2]' ([3]) could not be changed.");
        }

        // ── Cab stream import (_Streams needs a Record for binary Data) ──
        private static void ImportCabStream(WixToolset.Dtf.WindowsInstaller.Database db, string cabPath, string streamName)
        {
            using var view = db.OpenView("INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)");
            using var rec  = new WixToolset.Dtf.WindowsInstaller.Record(2);
            rec.SetString(1, streamName);
            rec.SetStream(2, cabPath);
            view.Execute(rec);
        }

        // ── Icon table insert: Data is a binary stream (the exe, whose PE icon Windows reads) ──
        private static void InsertIcon(WixToolset.Dtf.WindowsInstaller.Database db, string iconName, string sourcePath)
        {
            using var view = db.OpenView("INSERT INTO `Icon` (`Name`, `Data`) VALUES (?, ?)");
            using var rec  = new WixToolset.Dtf.WindowsInstaller.Record(2);
            rec.SetString(1, iconName);
            rec.SetStream(2, sourcePath);
            view.Execute(rec);
        }

        // ── Extract a single icon (by index) from a PE file into a .ico, via Win32 only ──
        // Used for large system DLLs (e.g. SHELL32.dll) so the whole binary isn't embedded.
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIconVs(IntPtr hIcon);

        /// <summary>
        /// True if the given PE file (.exe/.dll) contains at least one icon. Used to prefer a
        /// GUI exe (which has an icon) over a console exe (which usually does not) when choosing
        /// the product / ARP icon. Passing null buffers to ExtractIconEx returns the icon count.
        /// </summary>
        private static bool ExeHasIcon(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                uint count = ExtractIconEx(path, -1, null, null, 0);
                return count > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Extracts the icon at <paramref name="index"/> from a PE file and writes a .ico.
        /// Pure Win32 (no System.Drawing). Wraps the device-independent icon image returned by
        /// CreateBitmapSourceFromHIcon and encodes it as a single-image .ico via PNG.
        /// Returns true on success.
        /// </summary>
        private static bool ExtractIconIndexToFile(string pePath, int index, string icoPath)
        {
            IntPtr[] big = new IntPtr[1];
            try
            {
                // Negative index in .lnk means "by resource ID"; ExtractIconEx wants a positional
                // index, but it also accepts a resource id when negative on most Windows builds.
                uint got = ExtractIconEx(pePath, index, big, null, 1);
                if (got == 0 || big[0] == IntPtr.Zero)
                {
                    // Retry at positional 0 as a last resort.
                    got = ExtractIconEx(pePath, 0, big, null, 1);
                    if (got == 0 || big[0] == IntPtr.Zero) return false;
                }

                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    big[0], System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

                // Encode the 32-bit image as PNG, then wrap in a minimal .ico container
                // (Vista+ icons support PNG-compressed images).
                using var ms = new MemoryStream();
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                enc.Save(ms);
                byte[] png = ms.ToArray();

                int w = src.PixelWidth  >= 256 ? 0 : src.PixelWidth;
                int h = src.PixelHeight >= 256 ? 0 : src.PixelHeight;
                using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs);
                // ICONDIR
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)1);
                // ICONDIRENTRY
                bw.Write((byte)w); bw.Write((byte)h); bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write((uint)png.Length); bw.Write((uint)(6 + 16));
                bw.Write(png);
                bw.Flush();
                return new FileInfo(icoPath).Length > 0;
            }
            catch { return false; }
            finally
            {
                try { if (big[0] != IntPtr.Zero) DestroyIconVs(big[0]); } catch { }
            }
        }

        /// <summary>
        /// Extracts the icon Windows actually DISPLAYS for a file/shortcut (via SHGetFileInfo on
        /// the real path) and writes it as a .ico. This is the same mechanism the UI's "Open"
        /// column uses, so it is correct for every target type — .lnk, .url, .chm, etc. — without
        /// having to resolve or guess an icon location. Returns true on success.
        /// </summary>
        private static bool ExtractShellIconToIco(string filePath, string icoPath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
            var shfi = new SHFILEINFO_VS();
            try
            {
                // SHGFI_ICON (0x100) | SHGFI_LARGEICON (0x0), reading the ACTUAL file (no
                // USEFILEATTRIBUTES) so the shell resolves the real displayed icon (e.g. a .url's
                // web icon, a .chm's help icon, or a .lnk's stored/derived icon).
                IntPtr res = SHGetFileInfoVs(filePath, 0, ref shfi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(shfi),
                    0x000000100 | 0x000000000);
                if (shfi.hIcon == IntPtr.Zero) return false;

                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

                using var ms = new MemoryStream();
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                enc.Save(ms);
                byte[] png = ms.ToArray();

                int w = src.PixelWidth  >= 256 ? 0 : src.PixelWidth;
                int h = src.PixelHeight >= 256 ? 0 : src.PixelHeight;
                using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs);
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)1);
                bw.Write((byte)w); bw.Write((byte)h); bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write((uint)png.Length); bw.Write((uint)(6 + 16));
                bw.Write(png);
                bw.Flush();
                return new FileInfo(icoPath).Length > 0;
            }
            catch { return false; }
            finally
            {
                try { if (shfi.hIcon != IntPtr.Zero) DestroyIconVs(shfi.hIcon); } catch { }
            }
        }

        // SHGetFileInfo for ExtractShellIconToIco (ValidateSourcePage-scoped copy).
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SHGetFileInfoVs(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO_VS psfi, uint cbSizeFileInfo, uint uFlags);

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        private static extern int OleInitialize(IntPtr pvReserved);

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        private static extern void OleUninitialize();

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct SHFILEINFO_VS
        {
            public IntPtr hIcon;
            public int    iIcon;
            public uint   dwAttributes;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        // ── Schema DDL (every statement validated on the libmsi engine) ──
        private static IEnumerable<string> MsiSchemaDdl()
        {
            yield return "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL PRIMARY KEY `Property`)";
            yield return "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)";
            yield return "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` INT, `Level` INT NOT NULL, `Directory_` CHAR(72), `Attributes` INT NOT NULL PRIMARY KEY `Feature`)";
            yield return "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` INT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)";
            yield return "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)";
            yield return "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` INT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)";
            yield return "CREATE TABLE `Media` (`DiskId` INT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)";
            yield return "CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` INT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)";
            yield return "CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` INT, `Icon_` CHAR(72), `IconIndex` INT, `ShowCmd` INT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)";
            yield return "CREATE TABLE `ServiceInstall` (`ServiceInstall` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL, `DisplayName` CHAR(255) LOCALIZABLE, `ServiceType` LONG NOT NULL, `StartType` LONG NOT NULL, `ErrorControl` LONG NOT NULL, `LoadOrderGroup` CHAR(255), `Dependencies` CHAR(255), `StartName` CHAR(255), `Password` CHAR(255), `Arguments` CHAR(255), `Component_` CHAR(72) NOT NULL, `Description` CHAR(255) LOCALIZABLE PRIMARY KEY `ServiceInstall`)";
            yield return "CREATE TABLE `ServiceControl` (`ServiceControl` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL LOCALIZABLE, `Event` LONG NOT NULL, `Arguments` CHAR(255) LOCALIZABLE, `Wait` INT, `Component_` CHAR(72) NOT NULL PRIMARY KEY `ServiceControl`)";
            yield return "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` INT PRIMARY KEY `Action`)";
            yield return "CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` INT PRIMARY KEY `Action`)";
            yield return "CREATE TABLE `Icon` (`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)";
            yield return "CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)";
        }

        // ── Standard Windows Installer tables, created EMPTY ──
        // Kept deliberately separate from MsiSchemaDdl(): nothing here is ever populated by this
        // builder. They exist so the generated package carries the same table set as any other
        // MSI (Orca / InstEd / Edit MSI show the familiar list) and so a packager can add a row to
        // a standard table without having to create it first. Column widths follow the Windows
        // Installer schema; INT is used for 2-byte columns to match the statements above that are
        // already proven on this engine. Executed under a per-statement try/catch by the caller.
        //
        // _Validation is intentionally NOT created: an empty _Validation is worse than none, since
        // ICE/validation would then treat every column as undefined. Populating it correctly is a
        // separate job (one row per column of every table, with Category/Nullable/KeyTable).
        private static IEnumerable<string> MsiStandardEmptyTablesDdl()
        {
            // Sequences
            yield return "CREATE TABLE `AdminExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` INT PRIMARY KEY `Action`)";
            yield return "CREATE TABLE `AdminUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` INT PRIMARY KEY `Action`)";
            yield return "CREATE TABLE `AdvtExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` INT PRIMARY KEY `Action`)";
            yield return "CREATE TABLE `AdvtUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` INT PRIMARY KEY `Action`)";

            // Custom actions / binaries / conditions
            yield return "CREATE TABLE `Binary` (`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)";
            yield return "CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` INT NOT NULL, `Source` CHAR(72), `Target` CHAR(255), `ExtendedType` LONG PRIMARY KEY `Action`)";
            yield return "CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)";
            yield return "CREATE TABLE `Condition` (`Feature_` CHAR(38) NOT NULL, `Level` INT NOT NULL, `Condition` CHAR(255) PRIMARY KEY `Feature_`, `Level`)";

            // Search / locators
            yield return "CREATE TABLE `AppSearch` (`Property` CHAR(72) NOT NULL, `Signature_` CHAR(72) NOT NULL PRIMARY KEY `Property`, `Signature_`)";
            yield return "CREATE TABLE `CCPSearch` (`Signature_` CHAR(72) NOT NULL PRIMARY KEY `Signature_`)";
            yield return "CREATE TABLE `Signature` (`Signature` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `MinVersion` CHAR(20), `MaxVersion` CHAR(20), `MinSize` LONG, `MaxSize` LONG, `MinDate` LONG, `MaxDate` LONG, `Languages` CHAR(255) PRIMARY KEY `Signature`)";
            yield return "CREATE TABLE `RegLocator` (`Signature_` CHAR(72) NOT NULL, `Root` INT NOT NULL, `Key` CHAR(255) NOT NULL, `Name` CHAR(255), `Type` INT PRIMARY KEY `Signature_`)";
            yield return "CREATE TABLE `CompLocator` (`Signature_` CHAR(72) NOT NULL, `ComponentId` CHAR(38) NOT NULL, `Type` INT PRIMARY KEY `Signature_`)";
            yield return "CREATE TABLE `DrLocator` (`Signature_` CHAR(72) NOT NULL, `Parent` CHAR(72), `Path` CHAR(255), `Depth` INT PRIMARY KEY `Signature_`, `Parent`, `Path`)";
            yield return "CREATE TABLE `IniLocator` (`Signature_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `Section` CHAR(96) NOT NULL, `Key` CHAR(128) NOT NULL, `Field` INT, `Type` INT PRIMARY KEY `Signature_`)";

            // File / folder operations
            yield return "CREATE TABLE `CreateFolder` (`Directory_` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Directory_`, `Component_`)";
            yield return "CREATE TABLE `RemoveFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) LOCALIZABLE, `DirProperty` CHAR(72) NOT NULL, `InstallMode` INT NOT NULL PRIMARY KEY `FileKey`)";
            yield return "CREATE TABLE `MoveFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `SourceName` CHAR(255) LOCALIZABLE, `DestName` CHAR(255) LOCALIZABLE, `SourceFolder` CHAR(72), `DestFolder` CHAR(72) NOT NULL, `Options` INT NOT NULL PRIMARY KEY `FileKey`)";
            yield return "CREATE TABLE `DuplicateFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `File_` CHAR(72) NOT NULL, `DestName` CHAR(255) LOCALIZABLE, `DestFolder` CHAR(72) PRIMARY KEY `FileKey`)";
            yield return "CREATE TABLE `ReserveCost` (`ReserveKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `ReserveFolder` CHAR(72), `ReserveLocal` LONG NOT NULL, `ReserveSource` LONG NOT NULL PRIMARY KEY `ReserveKey`)";
            yield return "CREATE TABLE `BindImage` (`File_` CHAR(72) NOT NULL, `Path` CHAR(255) PRIMARY KEY `File_`)";
            yield return "CREATE TABLE `Font` (`File_` CHAR(72) NOT NULL, `FontTitle` CHAR(128) PRIMARY KEY `File_`)";
            yield return "CREATE TABLE `SelfReg` (`File_` CHAR(72) NOT NULL, `Cost` INT PRIMARY KEY `File_`)";
            yield return "CREATE TABLE `IsolatedComponent` (`Component_Shared` CHAR(72) NOT NULL, `Component_Application` CHAR(72) NOT NULL PRIMARY KEY `Component_Shared`, `Component_Application`)";

            // Registry / INI / environment removal
            yield return "CREATE TABLE `RemoveRegistry` (`RemoveRegistry` CHAR(72) NOT NULL, `Root` INT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `RemoveRegistry`)";
            yield return "CREATE TABLE `IniFile` (`IniFile` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `DirProperty` CHAR(72), `Section` CHAR(96) NOT NULL LOCALIZABLE, `Key` CHAR(128) NOT NULL LOCALIZABLE, `Value` CHAR(255) NOT NULL LOCALIZABLE, `Action` INT NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `IniFile`)";
            yield return "CREATE TABLE `RemoveIniFile` (`RemoveIniFile` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `DirProperty` CHAR(72), `Section` CHAR(96) NOT NULL LOCALIZABLE, `Key` CHAR(128) NOT NULL LOCALIZABLE, `Value` CHAR(255) LOCALIZABLE, `Action` INT NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `RemoveIniFile`)";
            yield return "CREATE TABLE `Environment` (`Environment` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL LOCALIZABLE, `Value` CHAR(255) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Environment`)";
            yield return "CREATE TABLE `LockPermissions` (`LockObject` CHAR(72) NOT NULL, `Table` CHAR(32) NOT NULL, `Domain` CHAR(255) LOCALIZABLE, `User` CHAR(255) NOT NULL LOCALIZABLE, `Permission` LONG PRIMARY KEY `LockObject`, `Table`, `Domain`, `User`)";
            // Do NOT also create MsiLockPermissionsEx. The two permission tables are mutually
            // exclusive: Windows Installer refuses to open a package that contains both and fails
            // with error 1941 ("Only one of them should be present"), even when both are empty.
            // LockPermissions is the classic table every authoring tool emits, so that is the one
            // kept here. Note this is an INSTALL-time validation rule, not a CREATE TABLE error -
            // the per-statement guard around this DDL cannot catch it.

            // COM / class registration
            yield return "CREATE TABLE `AppId` (`AppId` CHAR(38) NOT NULL, `RemoteServerName` CHAR(255), `LocalService` CHAR(255), `ServiceParameters` CHAR(255), `DllSurrogate` CHAR(255), `ActivateAtStorage` INT, `RunAsInteractiveUser` INT PRIMARY KEY `AppId`)";
            yield return "CREATE TABLE `Class` (`CLSID` CHAR(38) NOT NULL, `Context` CHAR(32) NOT NULL, `Component_` CHAR(72) NOT NULL, `ProgId_Default` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `AppId_` CHAR(38), `FileTypeMask` CHAR(255), `Icon_` CHAR(72), `IconIndex` INT, `DefInprocHandler` CHAR(32), `Argument` CHAR(255), `Feature_` CHAR(38) NOT NULL, `Attributes` INT PRIMARY KEY `CLSID`, `Context`, `Component_`)";
            yield return "CREATE TABLE `ProgId` (`ProgId` CHAR(255) NOT NULL, `ProgId_Parent` CHAR(255), `Class_` CHAR(38), `Description` CHAR(255) LOCALIZABLE, `Icon_` CHAR(72), `IconIndex` INT PRIMARY KEY `ProgId`)";
            yield return "CREATE TABLE `Extension` (`Extension` CHAR(255) NOT NULL, `Component_` CHAR(72) NOT NULL, `ProgId_` CHAR(255), `MIME_` CHAR(64), `Feature_` CHAR(38) NOT NULL PRIMARY KEY `Extension`, `Component_`)";
            yield return "CREATE TABLE `Verb` (`Extension_` CHAR(255) NOT NULL, `Verb` CHAR(32) NOT NULL, `Sequence` INT, `Command` CHAR(255) LOCALIZABLE, `Argument` CHAR(255) LOCALIZABLE PRIMARY KEY `Extension_`, `Verb`)";
            yield return "CREATE TABLE `MIME` (`ContentType` CHAR(64) NOT NULL, `Extension_` CHAR(255) NOT NULL, `CLSID` CHAR(38) PRIMARY KEY `ContentType`)";
            yield return "CREATE TABLE `TypeLib` (`LibID` CHAR(38) NOT NULL, `Language` INT NOT NULL, `Component_` CHAR(72) NOT NULL, `Version` LONG, `Description` CHAR(128) LOCALIZABLE, `Directory_` CHAR(72), `Feature_` CHAR(38) NOT NULL, `Cost` LONG PRIMARY KEY `LibID`, `Language`, `Component_`)";
            yield return "CREATE TABLE `Complus` (`Component_` CHAR(72) NOT NULL, `ExpType` INT PRIMARY KEY `Component_`)";
            yield return "CREATE TABLE `PublishComponent` (`ComponentId` CHAR(38) NOT NULL, `Qualifier` CHAR(255) NOT NULL, `Component_` CHAR(72) NOT NULL, `AppData` CHAR(255) LOCALIZABLE, `Feature_` CHAR(38) NOT NULL PRIMARY KEY `ComponentId`, `Qualifier`, `Component_`)";

            // .NET assemblies
            yield return "CREATE TABLE `MsiAssembly` (`Component_` CHAR(72) NOT NULL, `Feature_` CHAR(38) NOT NULL, `File_Manifest` CHAR(72), `File_Application` CHAR(72), `Attributes` INT PRIMARY KEY `Component_`)";
            yield return "CREATE TABLE `MsiAssemblyName` (`Component_` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL, `Value` CHAR(255) NOT NULL PRIMARY KEY `Component_`, `Name`)";

            // Services (extended) / SFC
            yield return "CREATE TABLE `MsiServiceConfig` (`MsiServiceConfig` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL LOCALIZABLE, `Event` INT NOT NULL, `ConfigType` INT NOT NULL, `Argument` CHAR(255) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `MsiServiceConfig`)";
            yield return "CREATE TABLE `MsiServiceConfigFailureActions` (`MsiServiceConfigFailureActions` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL LOCALIZABLE, `Event` INT NOT NULL, `ResetPeriod` LONG, `RebootMessage` CHAR(255) LOCALIZABLE, `Command` CHAR(255) LOCALIZABLE, `Actions` CHAR(255), `DelayActions` CHAR(255), `Component_` CHAR(72) NOT NULL PRIMARY KEY `MsiServiceConfigFailureActions`)";
            yield return "CREATE TABLE `MsiSFCBypass` (`File_` CHAR(72) NOT NULL PRIMARY KEY `File_`)";
            yield return "CREATE TABLE `SFPCatalog` (`SFPCatalog` CHAR(255) NOT NULL, `Catalog` OBJECT NOT NULL, `Dependency` CHAR(0) PRIMARY KEY `SFPCatalog`)";
            yield return "CREATE TABLE `FileSFPCatalog` (`File_` CHAR(72) NOT NULL, `SFPCatalog_` CHAR(255) NOT NULL PRIMARY KEY `File_`, `SFPCatalog_`)";

            // ODBC
            yield return "CREATE TABLE `ODBCDriver` (`Driver` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `Description` CHAR(255) NOT NULL, `File_` CHAR(72) NOT NULL, `File_Setup` CHAR(72) PRIMARY KEY `Driver`)";
            yield return "CREATE TABLE `ODBCTranslator` (`Translator` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `Description` CHAR(255) NOT NULL, `File_` CHAR(72) NOT NULL, `File_Setup` CHAR(72) PRIMARY KEY `Translator`)";
            yield return "CREATE TABLE `ODBCDataSource` (`DataSource` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `Description` CHAR(255) NOT NULL, `DriverDescription` CHAR(255) NOT NULL, `Registration` INT NOT NULL PRIMARY KEY `DataSource`)";
            yield return "CREATE TABLE `ODBCAttribute` (`Driver_` CHAR(72) NOT NULL, `Attribute` CHAR(40) NOT NULL, `Value` CHAR(255) LOCALIZABLE PRIMARY KEY `Driver_`, `Attribute`)";
            yield return "CREATE TABLE `ODBCSourceAttribute` (`DataSource_` CHAR(72) NOT NULL, `Attribute` CHAR(32) NOT NULL, `Value` CHAR(255) LOCALIZABLE PRIMARY KEY `DataSource_`, `Attribute`)";

            // Shortcut extras / digital signatures / embedded UI
            yield return "CREATE TABLE `MsiShortcutProperty` (`MsiShortcutProperty` CHAR(72) NOT NULL, `Shortcut_` CHAR(72) NOT NULL, `PropertyKey` CHAR(255) NOT NULL, `PropVariantValue` CHAR(255) NOT NULL PRIMARY KEY `MsiShortcutProperty`)";
            yield return "CREATE TABLE `MsiDigitalCertificate` (`DigitalCertificate` CHAR(72) NOT NULL, `CertData` OBJECT NOT NULL PRIMARY KEY `DigitalCertificate`)";
            yield return "CREATE TABLE `MsiDigitalSignature` (`Table` CHAR(32) NOT NULL, `SignObject` CHAR(72) NOT NULL, `DigitalCertificate_` CHAR(72) NOT NULL, `Hash` OBJECT PRIMARY KEY `Table`, `SignObject`)";
            yield return "CREATE TABLE `MsiPackageCertificate` (`PackageCertificate` CHAR(72) NOT NULL, `DigitalCertificate_` CHAR(72) NOT NULL PRIMARY KEY `PackageCertificate`)";
            yield return "CREATE TABLE `MsiPatchCertificate` (`PatchCertificate` CHAR(72) NOT NULL, `DigitalCertificate_` CHAR(72) NOT NULL PRIMARY KEY `PatchCertificate`)";
            yield return "CREATE TABLE `MsiEmbeddedChainer` (`MsiEmbeddedChainer` CHAR(72) NOT NULL, `Condition` CHAR(255), `CommandLine` CHAR(255), `Source` CHAR(72) NOT NULL, `Type` INT NOT NULL PRIMARY KEY `MsiEmbeddedChainer`)";
            yield return "CREATE TABLE `MsiEmbeddedUI` (`MsiEmbeddedUI` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `Attributes` INT NOT NULL, `MessageFilter` LONG, `Data` OBJECT PRIMARY KEY `MsiEmbeddedUI`)";
            yield return "CREATE TABLE `MsiFileHash` (`File_` CHAR(72) NOT NULL, `Options` INT NOT NULL, `HashPart1` LONG NOT NULL, `HashPart2` LONG NOT NULL, `HashPart3` LONG NOT NULL, `HashPart4` LONG NOT NULL PRIMARY KEY `File_`)";

            // Patching
            yield return "CREATE TABLE `Patch` (`File_` CHAR(72) NOT NULL, `Sequence` INT NOT NULL, `PatchSize` LONG NOT NULL, `Attributes` INT NOT NULL, `Header` OBJECT, `StreamRef_` CHAR(72) PRIMARY KEY `File_`, `Sequence`)";
            yield return "CREATE TABLE `PatchPackage` (`PatchId` CHAR(38) NOT NULL, `Media_` INT NOT NULL PRIMARY KEY `PatchId`)";

            // UI
            yield return "CREATE TABLE `Dialog` (`Dialog` CHAR(72) NOT NULL, `HCentering` INT NOT NULL, `VCentering` INT NOT NULL, `Width` INT NOT NULL, `Height` INT NOT NULL, `Attributes` LONG, `Title` CHAR(128) LOCALIZABLE, `Control_First` CHAR(50) NOT NULL, `Control_Default` CHAR(50), `Control_Cancel` CHAR(50) PRIMARY KEY `Dialog`)";
            yield return "CREATE TABLE `Control` (`Dialog_` CHAR(72) NOT NULL, `Control` CHAR(50) NOT NULL, `Type` CHAR(20) NOT NULL, `X` INT NOT NULL, `Y` INT NOT NULL, `Width` INT NOT NULL, `Height` INT NOT NULL, `Attributes` LONG, `Property` CHAR(72), `Text` CHAR(0) LOCALIZABLE, `Control_Next` CHAR(50), `Help` CHAR(50) LOCALIZABLE PRIMARY KEY `Dialog_`, `Control`)";
            yield return "CREATE TABLE `ControlCondition` (`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Action` CHAR(50) NOT NULL, `Condition` CHAR(255) NOT NULL PRIMARY KEY `Dialog_`, `Control_`, `Action`, `Condition`)";
            yield return "CREATE TABLE `ControlEvent` (`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Event` CHAR(50) NOT NULL, `Argument` CHAR(255) NOT NULL, `Condition` CHAR(255) NOT NULL, `Ordering` INT PRIMARY KEY `Dialog_`, `Control_`, `Event`, `Argument`, `Condition`)";
            yield return "CREATE TABLE `EventMapping` (`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Event` CHAR(50) NOT NULL, `Attribute` CHAR(50) NOT NULL PRIMARY KEY `Dialog_`, `Control_`, `Event`)";
            yield return "CREATE TABLE `CheckBox` (`Property` CHAR(72) NOT NULL, `Value` CHAR(64) PRIMARY KEY `Property`)";
            yield return "CREATE TABLE `ComboBox` (`Property` CHAR(72) NOT NULL, `Order` INT NOT NULL, `Value` CHAR(64) NOT NULL, `Text` CHAR(64) LOCALIZABLE PRIMARY KEY `Property`, `Order`)";
            yield return "CREATE TABLE `ListBox` (`Property` CHAR(72) NOT NULL, `Order` INT NOT NULL, `Value` CHAR(64) NOT NULL, `Text` CHAR(64) LOCALIZABLE PRIMARY KEY `Property`, `Order`)";
            yield return "CREATE TABLE `ListView` (`Property` CHAR(72) NOT NULL, `Order` INT NOT NULL, `Value` CHAR(64) NOT NULL, `Text` CHAR(64) LOCALIZABLE, `Binary_` CHAR(72) PRIMARY KEY `Property`, `Order`)";
            yield return "CREATE TABLE `RadioButton` (`Property` CHAR(72) NOT NULL, `Order` INT NOT NULL, `Value` CHAR(64) NOT NULL, `X` INT NOT NULL, `Y` INT NOT NULL, `Width` INT NOT NULL, `Height` INT NOT NULL, `Text` CHAR(64) LOCALIZABLE, `Help` CHAR(50) LOCALIZABLE PRIMARY KEY `Property`, `Order`)";
            yield return "CREATE TABLE `Billboard` (`Billboard` CHAR(50) NOT NULL, `Feature_` CHAR(38) NOT NULL, `Action` CHAR(50), `Ordering` INT PRIMARY KEY `Billboard`)";
            yield return "CREATE TABLE `BBControl` (`Billboard_` CHAR(50) NOT NULL, `BBControl` CHAR(50) NOT NULL, `Type` CHAR(50) NOT NULL, `X` INT NOT NULL, `Y` INT NOT NULL, `Width` INT NOT NULL, `Height` INT NOT NULL, `Attributes` LONG, `Text` CHAR(0) LOCALIZABLE PRIMARY KEY `Billboard_`, `BBControl`)";
            yield return "CREATE TABLE `TextStyle` (`TextStyle` CHAR(72) NOT NULL, `FaceName` CHAR(32) NOT NULL, `Size` INT NOT NULL, `Color` LONG, `StyleBits` INT PRIMARY KEY `TextStyle`)";
            yield return "CREATE TABLE `UIText` (`Key` CHAR(72) NOT NULL, `Text` CHAR(255) LOCALIZABLE PRIMARY KEY `Key`)";
            yield return "CREATE TABLE `ActionText` (`Action` CHAR(72) NOT NULL, `Description` CHAR(64) LOCALIZABLE, `Template` CHAR(128) LOCALIZABLE PRIMARY KEY `Action`)";
            yield return "CREATE TABLE `Error` (`Error` INT NOT NULL, `Message` CHAR(0) LOCALIZABLE PRIMARY KEY `Error`)";
        }

        private static IEnumerable<(string Action, string? Cond, int Seq)> InstallExecuteSequenceRows()
        {
            yield return ("FindRelatedProducts",   null, 200);   // detect an older install (Upgrade table)
            yield return ("CostInitialize",       null, 800);
            yield return ("FileCost",             null, 900);
            yield return ("CostFinalize",         null, 1000);
            yield return ("InstallValidate",      null, 1400);
            yield return ("InstallInitialize",    null, 1500);
            yield return ("RemoveExistingProducts", null, 1525); // remove the older install before laying down the new one
            yield return ("ProcessComponents",    null, 1600);
            yield return ("UnpublishFeatures",    null, 1800);
            yield return ("StopServices",         null, 1900);
            yield return ("DeleteServices",       null, 2000);
            yield return ("RemoveRegistryValues", null, 2600);
            yield return ("RemoveShortcuts",      null, 3200);
            yield return ("RemoveFiles",          null, 3500);
            yield return ("InstallFiles",         null, 4000);
            yield return ("CreateShortcuts",      null, 4500);
            yield return ("WriteRegistryValues",  null, 5000);
            yield return ("InstallServices",      null, 5800);
            yield return ("StartServices",        null, 5900);
            yield return ("RegisterProduct",      null, 6100);
            yield return ("PublishFeatures",      null, 6300);
            yield return ("PublishProduct",       null, 6400);
            yield return ("InstallFinalize",      null, 6600);
        }

        private static IEnumerable<(string Action, string? Cond, int Seq)> InstallUISequenceRows()
        {
            yield return ("FindRelatedProducts", null, 200);
            yield return ("CostInitialize", null, 800);
            yield return ("FileCost",       null, 900);
            yield return ("CostFinalize",   null, 1000);
            yield return ("ExecuteAction",  null, 1300);
        }

        private static string MsiSafeDefaultDir(string longName)
        {
            string clean = new string(longName.Where(c => c != '?' && c != '<' && c != '>' && c != '|' && c != '*' && c != ':').ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "DIR" : clean;
        }

        private static string MsiSafeFileName(string longName)
        {
            string clean = new string(longName.Where(c => c != '?' && c != '<' && c != '>' && c != '|' && c != '*').ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "file.dat" : clean;
        }

        private static int MapServiceStart(string startType) => (startType ?? "").ToLowerInvariant() switch
        {
            "automatic"                 => 2,
            "automatic (delayed start)" => 2,
            "boot"                      => 0,
            "system"                    => 1,
            "disabled"                  => 4,
            _                           => 3,
        };

        private static (string? Id, string? Path) MapToStandardDir(string absoluteFolder)
        {
            string pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string lad  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ad   = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string cad  = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var map = new (string Path, string Id)[]
            {
                (lad,  "LocalAppDataFolder"),
                (ad,   "AppDataFolder"),
                (cad,  "CommonAppDataFolder"),
                (pf86, "ProgramFilesFolder"),
                (pf64, "ProgramFiles64Folder"),
            };
            foreach (var (path, id) in map)
                if (!string.IsNullOrEmpty(path) &&
                    absoluteFolder.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    return (id, path);
            return (null, null);
        }

        private static string SanitiseMsiVersion(string raw)
        {
            var nums = System.Text.RegularExpressions.Regex.Matches(raw ?? "", @"\d+")
                .Select(m => m.Value).Take(4).ToArray();
            return nums.Length > 0 ? string.Join(".", nums) : "1.0.0";
        }

        private static bool RunProcess(string exe, string args, string workDir, int timeoutMs = 300_000)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute        = false,
                    WorkingDirectory       = workDir,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.OutputDataReceived += (_, _) => { };
                p.ErrorDataReceived  += (_, _) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        // ── Status helpers ────────────────────────────────────────
        private void SetFooterStatus(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                if (TxtFooterStatus != null) TxtFooterStatus.Text = msg;
            });
        }

        // ══════════════════════════════════════════════════════════════
        // THEMED DIALOG HELPERS  (mirrors CreatePackagePage Nd* pattern)
        // ══════════════════════════════════════════════════════════════

        private Window SvBuildDialog(string title)
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
                ShowInTaskbar = false
            };
            var outer = new Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35,
                    Color = System.Windows.Media.Colors.Black
                }
            };
            win.Content = outer;
            // When the dialog closes, hand focus back to SwiftPack Studio. During a capture
            // another app can grab the foreground, and without this the main window can be
            // skipped on close (focus landing on Word/Excel/etc. instead).
            win.Closed += (_, __) =>
            {
                try { win.Owner?.Activate(); } catch { }
            };
            return win;
        }

        private (Grid outerGrid, Grid contentGrid,
                 Brush boxBg, Brush boxBdr, Brush lblFg, Brush valFg, Style? sec)
            SvBuildDialogShell(Window win, string titleText, string titleIcon = "📦")
        {
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));
            Brush titleTxtBrush = Brushes.White;
            Brush titleSecBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            var outer = (Border)win.Content;
            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;
            var tb = new Border
            {
                Background = titleBarBg,
                BorderBrush = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 6, 12, 6)
            };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti  = new TextBlock { Text = titleIcon, FontSize = 14, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt  = new TextBlock { Text = titleText,  FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button   { Content = "✕", FontSize = 11, Foreground = titleSecBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => win.Close();
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg; og.Children.Add(tb);
            var cg = new Grid { Margin = new Thickness(8), Background = Brushes.Transparent };
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cg, 1); og.Children.Add(cg);
            var boxBg  = Application.Current.TryFindResource("WindowBgBrush")       as Brush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")     as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextPrimaryBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));
            var sec    = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            return (og, cg, boxBg, boxBdr, lblFg, valFg, sec);
        }

        private static void SvNdAddRow(Grid g, int row, string label, string value,
            Brush lblFg, Brush boxBg, Brush boxBdr, Brush valFg, bool mono = true,
            bool scrollable = false, double maxHeight = 0, bool selectable = false)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
            var box = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };

            // selectable: render the value as a read-only TextBox instead of a TextBlock so the
            // text can be highlighted and copied (Ctrl+A / Ctrl+C / right-click Copy). Border,
            // background and padding are stripped from the TextBox so the surrounding Border still
            // supplies the look - visually identical to the TextBlock, just selectable.
            FrameworkElement valueCtl;
            if (selectable)
            {
                valueCtl = new TextBox
                {
                    Text                          = value,
                    FontSize                      = 11,
                    TextWrapping                  = TextWrapping.Wrap,
                    Foreground                    = valFg,
                    FontFamily                    = new System.Windows.Media.FontFamily(mono ? "Consolas" : "Segoe UI"),
                    IsReadOnly                    = true,
                    IsReadOnlyCaretVisible        = false,
                    AcceptsReturn                 = true,   // keep the blank-line separators as real breaks
                    Background                    = System.Windows.Media.Brushes.Transparent,
                    BorderThickness               = new Thickness(0),
                    Padding                       = new Thickness(0),
                    // The outer ScrollViewer (when scrollable) owns the scrolling; the TextBox must
                    // not compete with it or the wheel would stall inside the box.
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
            }
            else
            {
                valueCtl = new TextBlock { Text = value, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg, FontFamily = new System.Windows.Media.FontFamily(mono ? "Consolas" : "Segoe UI") };
            }

            if (scrollable && maxHeight > 0)
            {
                // Wrap in a scroll viewer so a long list (e.g. many skipped files) grows up to
                // maxHeight then shows a vertical scrollbar instead of pushing the window huge.
                var sv = new ScrollViewer
                {
                    MaxHeight = maxHeight,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = valueCtl,
                };
                box.Child = sv;
            }
            else
            {
                box.Child = valueCtl;
            }

            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            g.Children.Add(lbl); g.Children.Add(box);
        }

        // A branded info/warning dialog matching the app's window style (replaces plain MessageBox
        // in the Add flows). Single OK button.
        private void SvShowMessage(string title, string message, string icon = "⚠️")
        {
            var dlg = SvBuildDialog(title);
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = SvBuildDialogShell(dlg, title, icon);

            int r = 0;
            // Message box spans BOTH columns (no empty label column on the left) so a plain
            // message dialog has no large blank gap.
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var box = new Border
            {
                Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8)
            };
            box.Child = new TextBlock
            {
                Text = message, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = valFg, FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            Grid.SetRow(box, r); Grid.SetColumn(box, 0); Grid.SetColumnSpan(box, 2);
            cg.Children.Add(box);
            r++;
            SvNdAddSpacer(cg, r++, 4);

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = SvNdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = SvNdMakeButtons(sec, ("✔ OK", () => dlg.Close()));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            dlg.ShowDialog();
        }

        // Two-button confirmation (Cancel / Continue). Returns true only if Continue is chosen.
        private bool SvConfirm(string title, string message,
                               string continueLabel = "✔ Continue", string cancelLabel = "✖ Cancel",
                               string icon = "⚠️")
        {
            bool result = false;
            var dlg = SvBuildDialog(title);
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = SvBuildDialogShell(dlg, title, icon);

            int r = 0;
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var box = new Border
            {
                Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8)
            };
            box.Child = new TextBlock
            {
                Text = message, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = valFg, FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };
            Grid.SetRow(box, r); Grid.SetColumn(box, 0); Grid.SetColumnSpan(box, 2);
            cg.Children.Add(box);
            r++;
            SvNdAddSpacer(cg, r++, 4);

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = SvNdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = SvNdMakeButtons(sec,
                (cancelLabel,   () => { result = false; dlg.Close(); }),
                (continueLabel, () => { result = true;  dlg.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            dlg.ShowDialog();
            return result;
        }

        private static void SvNdAddSpacer(Grid g, int row, double h = 8)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        }

        // ── Rounded input controls for the Add dialogs (match the app's rounded styling) ──

        // A TextBox with a rounded border (CornerRadius 6) via a minimal ControlTemplate.
        private static TextBox SvRoundedTextBox()
        {
            var tb = new TextBox
            {
                FontSize = 12,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0xC6, 0xD6)),
            };
            var tpl = new ControlTemplate(typeof(TextBox));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BackgroundProperty,      new System.Windows.Data.Binding("Background")  { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.BorderBrushProperty,     new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.CornerRadiusProperty,    new CornerRadius(6));
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(ScrollViewer.MarginProperty, new Thickness(2, 0, 2, 0));
            host.SetValue(ScrollViewer.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(host);
            tpl.VisualTree = bd;
            tb.Template = tpl;
            return tb;
        }

        // A rounded browse button ("📂") used inside the Add dialogs.
        private static Button SvRoundedIconButton(string glyph, string tip)
        {
            var btn = new Button
            {
                Content = glyph, FontSize = 13, Width = 32, Height = 30,
                Margin = new Thickness(4, 0, 0, 0), ToolTip = tip,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF7)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0xC6, 0xD6)),
                BorderThickness = new Thickness(1),
            };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BackgroundProperty,      new System.Windows.Data.Binding("Background")  { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.BorderBrushProperty,     new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.CornerRadiusProperty,    new CornerRadius(6));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;
            return btn;
        }

        // A ComboBox wrapped in a rounded border. The border provides the rounded chrome the user
        // sees in the closed state; the dropdown list renders in its own popup (unaffected).
        private static (Border Host, ComboBox Combo) SvRoundedComboBox()
        {
            var cb = new ComboBox
            {
                FontSize = 12, Height = 28,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var host = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0xC6, 0xD6)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2, 0, 2, 0),
                Child = cb,
            };
            return (host, cb);
        }

        private static System.Windows.Controls.Primitives.UniformGrid SvNdMakeButtons(
            Style? sec, params (string label, Action onClick)[] buttons)
        {
            // Highlight one button as the default action so every dialog has a
            // clear, consistent look (matching the Publish Package dialog). The
            // affirmative button is the one carrying the check glyph; when none
            // does, fall back to the first button so exactly one is highlighted.
            int primary = 0;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].label.IndexOf('\u2714') >= 0) { primary = i; break; }
            }
            var ug = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = buttons.Length };
            for (int i = 0; i < buttons.Length; i++)
            {
                var margin = i == 0 ? new Thickness(0, 0, 4, 0)
                           : i == buttons.Length - 1 ? new Thickness(4, 0, 0, 0)
                           : new Thickness(4, 0, 4, 0);
                var b = new Button { Content = buttons[i].label, Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6, 0, 6, 0), Margin = margin };
                if (i == primary) SvNdApplyPrimaryLook(b);
                var action = buttons[i].onClick;
                b.Click += (_, _) => action();
                ug.Children.Add(b);
            }
            return ug;
        }

        // Give a dialog button the highlighted "primary action" look: a filled
        // accent background, rounded corners and a subtle hover. Theme resources
        // keep it readable in both light and dark modes, mirroring the Publish
        // Package dialog. Applied on top of any existing Style (local values win
        // in WPF), so callers keep their own sizing.
        private static void SvNdApplyPrimaryLook(Button b)
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
        // and size) so every popup looks the same. Each dialog's title bar still
        // carries its own context icon. A monochrome glyph with an explicit
        // Foreground is used so the blue tint always applies in both themes.
        private static TextBlock SvNdDecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };

        private bool SvShowSearchWrapDialog(string message)
        {
            bool result = false;
            var win = SvBuildDialog("Source Validation - Search");
            win.Width = 360; win.MaxWidth = 360;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                SvBuildDialogShell(win, "Source Validation - Search", "🔬");
            int r = 0;
            SvNdAddRow(cg, r++, "Status", message, lblFg, boxBg, boxBdr, valFg, false);
            SvNdAddSpacer(cg, r++);
            // ❓ icon in col 0, buttons in col 1 — same row (mirrors CreatePackagePage exactly)
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = SvNdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = SvNdMakeButtons(sec,
                ("✔ Yes", () => { result = true;  win.Close(); }),
                ("✕ No",  () => { result = false; win.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
            return result;
        }

        // Themed single-OK message dialog, replacing raw MessageBox.Show. Safe to call from any
        // thread (marshals to the UI thread if needed).
        private void VsStyledMessage(Window? owner, string title, string message, string icon)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => VsStyledMessage(owner, title, message, icon)); return; }

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
            var tcl = new Button { Content = "\u2715", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
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
            var dIcon = SvNdDecisionIcon();
            Grid.SetColumn(dIcon, 0);
            actionRow.Children.Add(dIcon);
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(btnBar, 1);
            var bOk = new Button { Content = "\u2714  OK", Style = sec, Height = 30, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsDefault = true };
            SvNdApplyPrimaryLook(bOk);
            bOk.Click += (_, _) => win.Close();
            btnBar.Children.Add(bOk);
            actionRow.Children.Add(btnBar);
            og.Children.Add(actionRow);

            win.ShowDialog();
        }

    }

    /// <summary>Settings from ValidateSource.json.</summary>
    public class SourceValidationConfig
    {
        /// <summary>Default source file or folder path pre-filled in the Source Path box.</summary>
        public string? SourcePath  { get; set; }
        /// <summary>Folder where .svr files are saved. Empty → default C:\Temp\Validate Source.</summary>
        [JsonPropertyName("SVRPath")]
        public string? QasnapPath  { get; set; }
    }

    // Exclusion fields are read from the consolidated ValidateSource.json (the separate
    // ValidateSourceExclusionList.json is no longer used).
    public class ExclusionConfig
    {
        public List<string>? FilePaths       { get; set; }
        public List<string>? FileExtensions  { get; set; }
        public List<string>? RegistryKeys    { get; set; }
        public List<string>? ShortcutNames   { get; set; }
        public List<string>? EnvVars         { get; set; }
        public List<string>? FolderPaths     { get; set; }
        public List<string>? Services        { get; set; }
        /// <summary>Unused here — SVRPath is read via SourceValidationConfig.</summary>
        [JsonPropertyName("SVRPath")]
        public string?       QAsnapPath      { get; set; }
    }

    /// <summary>Converts null → Collapsed, non-null → Visible (for ShellIcon Image).</summary>
    public class NullToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
            => value != null ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
            => throw new NotImplementedException();
    }

    /// <summary>Converts empty/null string → Collapsed, non-empty → Visible (for emoji TextBlock).</summary>
    public class EmptyToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
            => !string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
            => throw new NotImplementedException();
    }
}
