using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SwiftPack.Views
{
    // ═══════════════════════════════════════════════════════════════
    // DATA MODELS
    // ═══════════════════════════════════════════════════════════════
    public class AdtFunction
    {
        public string Name      { get; set; } = "";
        public string Synopsis  { get; set; } = "";
        public List<AdtParam> Parameters { get; set; } = new();
        public bool HasReturnValue   { get; set; } = false;
    }

    public class AdtParam
    {
        public string Name        { get; set; } = "";
        public bool   Mandatory   { get; set; }
        public bool   IsSwitch    { get; set; }
        public List<string> ValidSet { get; set; } = new();
        public string Description { get; set; } = "";
        public string Type        { get; set; } = "";
        public string Default     { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════
    // PAGE
    // ═══════════════════════════════════════════════════════════════
    public partial class CreatePackagePage : Page
    {
        // ── Info-tab state ──────────────────────────────────────────
        private bool _suppressNameUpdate = false;
        private string? _createdScriptPath;   // set after Create completes
        private bool _modifyMode = false;     // true while modifying an existing package
        private System.Threading.CancellationTokenSource? _deployCts;
        private Process? _deployProcess;
        private string?  _deployCancelFile;
        private string   _currentDeploymentType = "";
        private System.Threading.CancellationTokenSource? _createCts; // cancels BtnCreate_Click background work
        private readonly System.Threading.ManualResetEventSlim _createPause = new(true); // set=running, reset=paused
        private DateTime _pkgCreateStartTime = DateTime.MinValue; // set when Create begins
        private int _arrowAnchorLine  = -1; // anchor line for Shift+Arrow multi-line selection in script editor
        private int _dragLine          = -1; // current drag end line for Shift+Arrow selection
        private int _savedSelStartLine = -1; // persisted selection start line - survives focus loss
        private int _savedSelEndLine   = -1; // persisted selection end line   - survives focus loss

        // ── Drag-and-drop state ──
        private bool   _isDragging        = false; // true once drag threshold exceeded
        // Set by WPF drop handler so WM_DROPFILES path skips the same drop
        private bool   _wpfLnkDropHandled  = false;
        private bool   _dragPending       = false; // mouse down - waiting for threshold
        private Point  _dragStartPoint;            // mouse-down position in TxtScript space
        private int    _dragSourceStart   = -1;    // first line being dragged
        private int    _dragSourceEnd     = -1;    // last line being dragged
        private int    _dragTargetLine    = -1;    // current drop target line
        private System.Windows.Shapes.Line? _dragIndicator = null; // horizontal drop indicator

        /// <summary>Exposed so MainWindow can pre-fill ExecutePackagePage.</summary>
        public string? LastCreatedScriptPath => _createdScriptPath;
        public string? LastMainFilePath      => TxtMainFile?.Text.Trim();
        public string? LastAppName           => TxtAppName?.Text.Trim();
        public string? LastAppVendor         => TxtAppVendor?.Text.Trim();
        public string? LastAppVersion        => TxtAppVersion?.Text.Trim();

        /// <summary>
        /// Fired immediately when a package is successfully created and the script path is known.
        /// MainWindow subscribes to this to pre-fill ExecutePackagePage without waiting for navigation.
        /// </summary>
        public Action<string>? OnScriptPathCreated { get; set; }

        // ── Script-tab state ────────────────────────────────────────
        private bool              _scriptDirty;
        private AdtFunction?      _selectedFunc;
        private List<AdtFunction> _allFunctions  = new();
        private List<AdtFunction> _filteredFuncs = new();
        private readonly Dictionary<string, FrameworkElement> _paramControls = new();
        private TextBox? _varNameControl = null;  // optional $var = prefix for output-returning functions
        // ── Param ↔ Script sync state ───────────────────────────────
        // _syncingParams: true while populating controls from the script line
        //   prevents live-sync handlers firing and writing back mid-fill.
        // _syncLineIndex: 0-based index of the script line owned by the param panel.
        //   -1 means panel was not opened from a clicked line.
        private bool _syncingParams     = false;
        private int  _syncLineIndex     = -1;
        // True while FlushSyncToScript is writing to TxtScript.Text.
        // Blocks TrySelectCommandFromCurrentLine from re-parsing and
        // overwriting the param controls during the write.
        private bool _flushingToScript      = false;
        // After Add to Script, reset params to blank instead of fully clearing.
        private bool _resetPanelOnNextBlank = false;
        private ScrollViewer? _col2ScrollViewer = null; // cached reference for Add-PSScript restore
        // Debounce timer: fires 300ms after the last param change.
        // Delays the script-line write until the user has stopped typing,
        // so TxtScript.Select() never runs while a keystroke is in flight.
        private System.Windows.Threading.DispatcherTimer? _syncTimer;

        // ── Undo / Redo stacks ──────────────────────────────────────
        // Snapshot = script text + the caret position at the moment it was taken. The caret must
        // travel with the text: assigning TxtScript.Text resets the caret to 0, so restoring a
        // snapshot without it sent the caret (and the view, and the command panel via
        // SelectionChanged) to line 1 on every Undo/Redo.
        private readonly Stack<(string Text, int Caret)> _undoStack = new();
        private readonly Stack<(string Text, int Caret)> _redoStack = new();
        private bool _suppressUndoCapture = false;

        // ── Help cache: funcName → formatted help text ───────────────
        private readonly Dictionary<string, string> _helpCache = new();
        private string? _psm1SourcePath;   // path to PSAppDeployToolkit.psm1 for offline help
        private int _currentHighlightLine = -1;

        // ── Script search state ──────────────────────────────────────
        private List<int> _searchMatches = new List<int>();   // start indices of all matches
        private int       _searchIndex   = -1;                // current match index
        private string    _lastSearchTerm = "";

        // ── CreatePackage.json config ───────────────────────────────
        private CreatePackageConfig _cfg = new();
        private static string GetCreatePackageConfigPath()
        {
            const string p = @"C:\Program Files\Smekal\SwiftPack Studio\Templates\CreatePackage.json";
            if (File.Exists(p)) return p;
            return System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                "Resources", "CreatePackage.json");
        }

        internal void ApplyCreatePackageConfig()
        {
            var path = GetCreatePackageConfigPath();
            try
            {
                if (!File.Exists(path)) return;
                string raw = File.ReadAllText(path);

                // Tolerate unescaped backslashes in Windows paths (e.g. "C:\aPSApp").
                // Scan inside JSON string values and properly escape any lone backslash.
                var fixed2 = new System.Text.StringBuilder(raw.Length + 16);
                bool inStr = false;
                for (int i = 0; i < raw.Length; i++)
                {
                    char c = raw[i];
                    if (c == '"' && (i == 0 || raw[i - 1] != '\\')) inStr = !inStr;
                    if (inStr && c == '\\' && i + 1 < raw.Length)
                    {
                        char nx = raw[i + 1];
                        bool valid = nx == '"' || nx == '\\' || nx == '/' ||
                                     nx == 'b' || nx == 'f'  || nx == 'n'  ||
                                     nx == 'r' || nx == 't'  || nx == 'u';
                        if (valid)
                        {
                            fixed2.Append(c);
                            fixed2.Append(nx);
                            i++;
                            continue;
                        }
                        else
                        {
                            fixed2.Append('\\');
                            fixed2.Append('\\');
                            continue;
                        }
                    }
                    fixed2.Append(c);
                }

                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cfg  = JsonSerializer.Deserialize<CreatePackageConfig>(fixed2.ToString(), opts);
                if (cfg == null) return;
                _cfg = cfg;

                // Support both new "PSADTTemplateFolder" key and legacy "TemplateFolder" key
                string? tmplCfg = !string.IsNullOrWhiteSpace(cfg.PSADTTemplateFolder)
                    ? cfg.PSADTTemplateFolder : cfg.TemplateFolder;
                if (!string.IsNullOrWhiteSpace(tmplCfg))
                    TxtTemplatePath.Text = tmplCfg.Trim();

                if (!string.IsNullOrWhiteSpace(cfg.PackageOutputFolder))
                    TxtOutputPath.Text = cfg.PackageOutputFolder.Trim();

                // Support both new "SourceFolder" key and legacy "SourcePath" key
                string? srcCfg = !string.IsNullOrWhiteSpace(cfg.SourceFolder)
                    ? cfg.SourceFolder : cfg.SourcePath;
                if (!string.IsNullOrWhiteSpace(srcCfg))
                {
                    string src = srcCfg.Trim();
                    TxtSourcePath.Text = src;

                    // If the source path is a folder, check for a single .exe or .msi
                    if (Directory.Exists(src))
                    {
                        var installers = Directory.GetFiles(src, "*.*", SearchOption.TopDirectoryOnly)
                            .Where(f => { var ext = Path.GetExtension(f).ToLowerInvariant(); return ext == ".exe" || ext == ".msi"; })
                            .ToArray();
                        if (installers.Length == 1)
                        {
                            TxtMainFile.Text = installers[0];
                            FetchAndApplyFileInfo(installers[0]);
                        }
                    }
                    else if (File.Exists(src))
                    {
                        // SourcePath itself is a file - use it as main file if it is an installer
                        var ext = Path.GetExtension(src).ToLowerInvariant();
                        if (ext == ".exe" || ext == ".msi")
                        {
                            TxtMainFile.Text = src;
                            FetchAndApplyFileInfo(src);
                        }
                    }
                }
            }
            catch { }
        }

        public CreatePackagePage()
        {
            InitializeComponent();
            TxtScriptDate.Text = string.Empty;
            LoadAdtFunctions();
            // Script tab and Save are inactive until a package has been successfully created
            SetScriptTabEnabled(false);
            if (BtnInfoSave != null) BtnInfoSave.IsEnabled = false;
            // Attach smart caret handler to every TextBox on this page
            this.Loaded += (_, _) =>
            {
                AttachCaretHandlerToAllTextBoxes(this);
                this.PreviewMouseLeftButtonDown += Page_PreviewMouseLeftButtonDown;
                // Rebuild the param panel when the page is re-shown so any dynamically-created
                // controls pick up the current theme (Light/Dark) colours.
                this.IsVisibleChanged += (_, ev) =>
                {
                    if (this.IsVisible && _selectedFunc != null)
                        try { BuildParamPanel(_selectedFunc); } catch { }
                };
                // Rename label by scanning visual tree text content
                RenameLabelByText(this, "Template or Package Folder", "PSADT Template Folder");
                RenameLabelByText(this, "Template or Package Folder *", "PSADT Template Folder *");
                // Wire X clear buttons defined in XAML
                WireNamedButton("BtnClearTemplate", ClearTemplatePath_Click);
                WireNamedButton("BtnClearSource",   ClearSourcePath_Click);
                WireNamedButton("BtnClearMainFile", ClearMainFile_Click);
                WireNamedButton("BtnClearOutput",   ClearOutputPath_Click);

                // IsReadOnly on TxtScript causes WPF to mark the Drop routed event as
                // handled before our handler sees it. AddHandler with handledEventsToo:true
                // bypasses that - we receive Drop regardless of the IsReadOnly state.
                if (TxtScript != null)
                    TxtScript.AddHandler(UIElement.DropEvent,
                        new DragEventHandler(TxtScript_LnkDrop), handledEventsToo: true);

                // Wire PreviewDragOver at the Page level so the "copy" cursor is shown
                // even if an intermediate element has no AllowDrop. This must be done
                // via AddHandler (handledEventsToo:true) so it fires unconditionally.
                this.AddHandler(UIElement.PreviewDragOverEvent,
                    new DragEventHandler(Page_PreviewDragOver), handledEventsToo: true);
            };
        }

        /// <summary>Finds a Button by name in the visual tree and wires its Click.</summary>
        private void WireNamedButton(string name, RoutedEventHandler handler)
        {
            if (FindName(name) is System.Windows.Controls.Button btn)
                btn.Click += handler;
            else
            {
                var b = FindVisualChild<System.Windows.Controls.Button>(this, name);
                if (b != null) b.Click += handler;
            }
        }


        /// <summary>
        /// Walks the visual tree and renames every TextBlock/Label whose text
        /// exactly matches <paramref name="oldText"/> to <paramref name="newText"/>.
        /// Works even when the element has no x:Name set in XAML.
        /// </summary>
        private static void RenameLabelByText(
            System.Windows.DependencyObject root, string oldText, string newText)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb &&
                    string.Equals(tb.Text.Trim(), oldText.Trim(),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    tb.Text = newText;
                    return; // rename first match only
                }
                if (child is System.Windows.Controls.Label lbl &&
                    lbl.Content is string lc &&
                    string.Equals(lc.Trim(), oldText.Trim(),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    lbl.Content = newText;
                    return;
                }
                RenameLabelByText(child, oldText, newText);
            }
        }

        private static T? FindVisualChild<T>(System.Windows.DependencyObject parent, string name)
            where T : System.Windows.FrameworkElement
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t && t.Name == name) return t;
                var result = FindVisualChild<T>(child, name);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Page-level handler: when the user clicks anywhere on the page, find any
        /// TextBox that currently has a selection and clear it - unless the click
        /// landed inside that same TextBox (in which case the TextBox's own handler runs).
        /// </summary>
        private void Page_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Find the TextBox that currently has keyboard focus (if any)
            if (System.Windows.Input.Keyboard.FocusedElement is not TextBox focused) return;
            if (focused.SelectionLength == 0) return; // nothing selected - nothing to do

            // Check if the click landed inside the focused TextBox
            var clickPos = e.GetPosition(focused);
            bool clickedInsideFocused =
                clickPos.X >= 0 && clickPos.X <= focused.ActualWidth &&
                clickPos.Y >= 0 && clickPos.Y <= focused.ActualHeight;

            if (!clickedInsideFocused)
            {
                // Click was outside - clear selection by moving caret to end
                focused.SelectionLength = 0;
            }
        }

        /// <summary>
        /// Walks the visual tree and attaches PathBox_PreviewMouseLeftButtonDown
        /// and a custom context menu to every TextBox on the page.
        /// </summary>
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

        /// <summary>
        /// Builds a themed context menu with Select All, Delete, Undo, Redo
        /// bound to the given TextBox's state.
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

            // Cut - ✂ Ctrl+X
            menu.Items.Add(MakeItem("Cut",        "✂",  "Ctrl+X",
                () => tb.Cut(),
                () => !tb.IsReadOnly && tb.SelectionLength > 0));

            // Copy - ⧉ Ctrl+C
            menu.Items.Add(MakeItem("Copy",       "⧉", "Ctrl+C",
                () => tb.Copy(),
                () => tb.SelectionLength > 0));

            // Paste - 📋 Ctrl+V
            menu.Items.Add(MakeItem("Paste",      "📋", "Ctrl+V",
                () => ScriptPaste(),
                () => !tb.IsReadOnly &&
                      (!string.IsNullOrEmpty(ClipboardGetTextSafe()) ||
                       TryGetLnkFromClipboard() != null)));

            menu.Items.Add(MakeSep());

            // Select All - ⬛ Ctrl+A
            menu.Items.Add(MakeItem("Select All", "⬛", "Ctrl+A",
                () => tb.SelectAll(),
                () => !string.IsNullOrEmpty(tb.Text)));

            // Delete - 🗑 Del
            menu.Items.Add(MakeItem("Delete",     "🗑",  "Del",
                () =>
                {
                    if (tb.SelectionLength > 0)
                        tb.SelectedText = "";
                    else if (tb.CaretIndex < tb.Text.Length)
                        tb.Text = tb.Text.Remove(tb.CaretIndex, 1);
                },
                () => !tb.IsReadOnly && (tb.SelectionLength > 0 || tb.CaretIndex < tb.Text.Length)));

            menu.Items.Add(MakeSep());

            // Undo - ↩ Ctrl+Z
            menu.Items.Add(MakeItem("Undo",       "↩",  "Ctrl+Z",
                () => tb.Undo(),
                () => !tb.IsReadOnly && tb.CanUndo));

            // Redo - ↪ Ctrl+Y
            menu.Items.Add(MakeItem("Redo",       "↪",  "Ctrl+Y",
                () => tb.Redo(),
                () => !tb.IsReadOnly && tb.CanRedo));

            return menu;
        }

        // ══════════════════════════════════════════════════════════════
        // TAB / BUTTON STATE HELPERS
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Enables or disables the Script tab button.
        /// When disabled the tab appears greyed out and cannot be clicked.
        /// </summary>
        private void SetScriptTabEnabled(bool enabled)
        {
            if (BtnTabScript == null) return;
            BtnTabScript.IsEnabled = enabled;
            BtnTabScript.Opacity   = enabled ? 1.0 : 0.4;
        }

        /// <summary>
        /// Enables or disables the Info-toolbar action buttons and all Browse buttons.
        /// Called when Create starts (disable) and when it completes or fails (enable).
        /// </summary>
        private void SetInfoButtonsEnabled(bool enabled)
        {
            if (BtnRefreshConfig  != null) BtnRefreshConfig.IsEnabled  = enabled;
            if (BtnBrowseTemplate != null) BtnBrowseTemplate.IsEnabled = enabled;
            if (BtnBrowseSource   != null) BtnBrowseSource.IsEnabled   = enabled;
            if (BtnBrowseMainFile != null) BtnBrowseMainFile.IsEnabled = enabled;
            if (BtnBrowseOutput   != null) BtnBrowseOutput.IsEnabled   = enabled;
            // Cancel is the inverse - only active while creation is running
            if (BtnCancelCreate   != null) BtnCancelCreate.IsEnabled   = !enabled;

            // Disable / re-enable all Info-tab TextBoxes during Create
            var boxes = new TextBox?[]
            {
                TxtTemplatePath, TxtSourcePath, TxtMainFile, TxtOutputPath,
                TxtAppVendor, TxtAppName, TxtAppVersion, TxtAppArch,
                TxtAppLang, TxtAppRevision, TxtSuccessExitCodes, TxtRebootExitCodes,
                TxtAppProcesses, TxtScriptVersion, TxtScriptDate, TxtScriptAuthor,
                TxtRequireAdmin, TxtInstallName, TxtInstallTitle, TxtPackageName
            };
            foreach (var tb in boxes)
                if (tb != null) tb.IsEnabled = enabled;

            // Show / hide header progress bar
            if (PrgHeaderProgress != null)
                PrgHeaderProgress.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        // ══════════════════════════════════════════════════════════════
        // TAB SWITCHING
        // ══════════════════════════════════════════════════════════════
        private void BtnTabInfo_Click(object sender, RoutedEventArgs e)    => ActivateTab(false);
        private void BtnTabScript_Click(object sender, RoutedEventArgs e)  => ActivateTab(true);

        private void ActivateTab(bool showScript)
        {
            TabInfoContent.Visibility    = showScript ? Visibility.Collapsed : Visibility.Visible;
            TabScriptContent.Visibility  = showScript ? Visibility.Visible   : Visibility.Collapsed;

            // Update underline indicator (live theme refs so a Light/Dark switch re-colors
            // the active tab instead of keeping the previous theme's pill colour).
            if (showScript)
            {
                TabInfoIndicator.BorderBrush = new SolidColorBrush(Colors.Transparent);
                TabScriptIndicator.SetResourceReference(Border.BorderBrushProperty, "AccentBlueBrush");
            }
            else
            {
                TabInfoIndicator.SetResourceReference(Border.BorderBrushProperty, "AccentBlueBrush");
                TabScriptIndicator.BorderBrush = new SolidColorBrush(Colors.Transparent);
            }

            if (showScript)
            {
                BtnTabInfo.SetResourceReference(Control.ForegroundProperty,   "TextSecondaryBrush");
                BtnTabScript.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            }
            else
            {
                BtnTabInfo.SetResourceReference(Control.ForegroundProperty,   "TextPrimaryBrush");
                BtnTabScript.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            }

            // Active tab gets a subtle background pill; inactive is transparent
            if (showScript)
            {
                TabInfoIndicator.Background = new SolidColorBrush(Colors.Transparent);
                TabScriptIndicator.SetResourceReference(Border.BackgroundProperty, "TabScriptBgBrush");
            }
            else
            {
                TabInfoIndicator.SetResourceReference(Border.BackgroundProperty, "TabInfoBgBrush");
                TabScriptIndicator.Background = new SolidColorBrush(Colors.Transparent);
            }

            // Show/hide toolbars
            if (ScriptToolbarScroll != null)
                ScriptToolbarScroll.Visibility = showScript ? Visibility.Visible : Visibility.Collapsed;
            if (InfoToolbar != null)
                InfoToolbar.Visibility = showScript ? Visibility.Collapsed : Visibility.Visible;
        }

        // ══════════════════════════════════════════════════════════════
        // LOAD ADT FUNCTIONS FROM JSON
        // ══════════════════════════════════════════════════════════════
        private void LoadAdtFunctions()
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // 1) Preferred: read the command catalog from an embedded resource compiled into the
            //    assembly, so no loose Commands.json file ships in the Resources folder.
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                // Match the resource regardless of the exact root namespace prefix.
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("Commands.json", StringComparison.OrdinalIgnoreCase));
                if (resName != null)
                {
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        string json = reader.ReadToEnd();
                        _allFunctions = JsonSerializer.Deserialize<List<AdtFunction>>(json, opts)
                                        ?? new List<AdtFunction>();
                    }
                }
            }
            catch { }

            // 2) Fallback: read from disk (Resources\Commands.json) if the embedded resource
            //    was not found (e.g. during development before the embed is set up).
            if (_allFunctions.Count == 0)
            {
                try
                {
                    string[] candidates = {
                        Path.Combine(Path.GetDirectoryName(
                            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "Resources", "Commands.json"),
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Commands.json"),
                    };
                    foreach (string p in candidates)
                    {
                        if (!File.Exists(p)) continue;
                        _allFunctions = JsonSerializer.Deserialize<List<AdtFunction>>(File.ReadAllText(p), opts)
                                        ?? new List<AdtFunction>();
                        break;
                    }
                }
                catch { }
            }

            // 3) Last resort: the small compiled-in built-in set.
            if (_allFunctions.Count == 0)
                _allFunctions = BuiltInFunctions();

            _allFunctions = _allFunctions.OrderBy(f => f.Name).ToList();
            ApplyCmdFilter("");
        }

        private static List<AdtFunction> BuiltInFunctions() => new()
        {
            new AdtFunction {
                Name = "Copy-ADTFile",
                Synopsis = "Copies files and directories from a source to a destination.",
                Parameters = new() {
                    new() { Name="Path",        Mandatory=true,  IsSwitch=false, Description="Path of the file to copy. Multiple paths can be specified.", Type="System.String[]" },
                    new() { Name="Destination", Mandatory=true,  IsSwitch=false, Description="Destination Path of the file to copy.", Type="System.String" },
                    new() { Name="Recurse",     Mandatory=false, IsSwitch=true,  Description="Copy files in subdirectories.", Type="System.Management.Automation.SwitchParameter" },
                    new() { Name="Flatten",     Mandatory=false, IsSwitch=true,  Description="Flattens the files into the root destination directory.", Type="System.Management.Automation.SwitchParameter" },
                    new() { Name="ContinueFileCopyOnError", Mandatory=false, IsSwitch=true, Description="Continue copying files if an error is encountered.", Type="System.Management.Automation.SwitchParameter" },
                    new() { Name="FileCopyMode", Mandatory=false, ValidSet=new(){"Native","Robocopy"}, Description="Select from 'Native' or 'Robocopy'. Default is configured in config.psd1.", Type="System.String" },
                    new() { Name="RobocopyParams", Mandatory=false, IsSwitch=false, Description="Override the default Robocopy parameters.", Type="System.String", Default="/NJH /NJS /NS /NC /NP /NDL /FP /IA:RASHCNETO /IS /IT /IM /XX /MT:4 /R:1 /W:1" },
                    new() { Name="RobocopyAdditionalParams", Mandatory=false, IsSwitch=false, Description="Append to the default Robocopy parameters.", Type="System.String" },
                }
            }
        };

        // ══════════════════════════════════════════════════════════════
        // HELPERS - shared folder dialog
        // ══════════════════════════════════════════════════════════════
        private static string GetSystemDrive() =>
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";

        #region Win32 IFileOpenDialog COM interop
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"), ClassInterface(ClassInterfaceType.None)]
        private class FileOpenDialogClass { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr hwndOwner);
            void SetFileTypes(uint c, IntPtr r);  void SetFileTypeIndex(uint i); void GetFileTypeIndex(out uint p);
            void Advise(IntPtr p, out uint c);    void Unadvise(uint c);
            void SetOptions(uint f);              void GetOptions(out uint f);
            void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] IShellItem p);
            void SetFolder([MarshalAs(UnmanagedType.Interface)] IShellItem p);
            void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem p);
            void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem p);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string n);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string n);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem p);
            void AddPlace([MarshalAs(UnmanagedType.Interface)] IShellItem p, int f);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string e);
            void Close([MarshalAs(UnmanagedType.Error)] int hr);
            void SetClientGuid(ref Guid g); void ClearClientData(); void SetFilter(IntPtr f);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr p, ref Guid b, ref Guid r, out IntPtr v);
            void GetParent(out IShellItem p);
            void GetDisplayName(uint s, [MarshalAs(UnmanagedType.LPWStr)] out string n);
            void GetAttributes(uint m, out uint a);
            void Compare(IShellItem p, uint h, out int o);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string p, IntPtr b, [In] ref Guid r,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem v);

        private const uint FOS_PICKFOLDERS      = 0x20,
                           FOS_FORCEFILESYSTEM  = 0x40,
                           FOS_NOCHANGEDIR      = 0x08,
                           FOS_ALLOWMULTISELECT = 0x200,
                           SFGAO_FOLDER         = 0x20000000,
                           SIGDN_FILESYSPATH    = 0x80028000;
        #endregion

        private static string? PickFolder(string title)
        {
            try
            {
                var dlg = (IFileDialog)new FileOpenDialogClass();
                dlg.SetTitle(title);
                dlg.GetOptions(out uint opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                string sysDrive = GetSystemDrive().TrimEnd('\\') + "\\";
                var iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(sysDrive, IntPtr.Zero, ref iid, out IShellItem sf);
                dlg.SetFolder(sf);
                if (dlg.Show(IntPtr.Zero) != 0) return null;
                dlg.GetResult(out IShellItem res);
                res.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                return path;
            }
            catch
            {
                using var fb = new System.Windows.Forms.FolderBrowserDialog
                { Description = title, UseDescriptionForTitle = true, SelectedPath = GetSystemDrive() };
                return fb.ShowDialog() == System.Windows.Forms.DialogResult.OK ? fb.SelectedPath : null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // BROWSE HANDLERS (Info tab)
        // ══════════════════════════════════════════════════════════════
        private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
        { var p = PickFolder(_modifyMode ? "Select Package Folder" : "Select PSADT Template Folder"); if (p != null) TxtTemplatePath.Text = p; }

        private void ClearTemplatePath_Click(object sender, RoutedEventArgs e)
            => TxtTemplatePath.Clear();

        private void BrowseSource_Click(object sender, RoutedEventArgs e)
        {
            var p = PickFolder("Select Source Folder");
            if (p == null) return;
            TxtSourcePath.Text = p;
            AutoPopulateMainSourceFile(p);
        }

        private void ClearSourcePath_Click(object sender, RoutedEventArgs e)
        {
            TxtSourcePath.Clear();
            TxtMainFile.Clear();
        }

        /// <summary>
        /// Searches the given folder (and subfolders) for .exe and .msi files.
        /// If exactly one installer type is found (only .exe OR only .msi), auto-fills
        /// Main Source File and fetches AppVendor/AppName/AppVersion from it.
        /// If both types are found, leaves Main Source File empty.
        /// </summary>
        private void AutoPopulateMainSourceFile(string folder)
        {
            if (!Directory.Exists(folder)) return;

            // Clear all fields immediately before background search
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtMainFile.Text     = "";
                TxtAppVendor.Text    = "";
                TxtAppName.Text      = "";
                TxtAppVersion.Text   = "";
                TxtAppProcesses.Text = "";
            }));

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string? chosen = PickMainSourceFile(folder);
                    if (chosen == null) return;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TxtMainFile.Text = chosen;
                        FetchAndApplyFileInfo(chosen);
                    }));
                }
                catch { }
            });
        }

        /// <summary>
        /// Picks the best main source file from a folder using this priority:
        /// 1. Root files only (prefer root-level before diving into subfolders)
        ///    a. Both MSI and EXE at root → first MSI (alphabetical)
        ///    b. Only MSI at root → first MSI
        ///    c. Only EXE at root → first EXE
        /// 2. If root has no installers, scan one level of subdirectories and
        ///    apply the same priority within each subfolder (alphabetical subfolder order).
        /// Returns null if nothing suitable is found.
        /// </summary>
        private static string? PickMainSourceFile(string folder)
        {
            // Search in a given directory (root-only, no recursion)
            static string? BestIn(string dir)
            {
                var msi = Directory.GetFiles(dir, "*.msi", SearchOption.TopDirectoryOnly)
                              .OrderBy(f => f).ToArray();
                var exe = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                              .OrderBy(f => f).ToArray();
                // MSI beats EXE; if both present → first MSI
                if (msi.Length > 0) return msi[0];
                if (exe.Length > 0) return exe[0];
                return null;
            }

            // Step 1: try root of the selected folder
            string? result = BestIn(folder);
            if (result != null) return result;

            // Step 2: no installers at root - try immediate subfolders (one level)
            foreach (string sub in Directory.GetDirectories(folder)
                                             .OrderBy(d => d))
            {
                result = BestIn(sub);
                if (result != null) return result;
            }

            return null;
        }


        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var p = PickFolder("Select Output Folder");
            if (p != null) TxtOutputPath.Text = p;
        }

        private void ClearOutputPath_Click(object sender, RoutedEventArgs e)
            => TxtOutputPath.Clear();

        /// <summary>
        /// Shows a small inline prompt - Folder button opens native folder picker,
        /// File button opens native file picker. No intermediate dialog shown; both
        /// are direct native Windows dialogs. Returns selected path or null.
        /// </summary>
        /// <summary>
        /// Shows a File / Folder / Cancel dialog.
        /// <paramref name="initDir"/> seeds the Windows file/folder picker to that directory
        /// (used so Add-ADTFont opens directly inside the package's Files\ folder).
        /// </summary>
        private string? PickFileOrFolder(string title, string? initDir = null)
        {
            string? chosen = null;

            // Resolve a usable seed directory: prefer the supplied initDir, fall back to system drive
            string seedDir = (initDir != null && Directory.Exists(initDir))
                             ? initDir
                             : GetSystemDrive();

            bool genericMode = title.Equals("Select Source File or Folder", StringComparison.OrdinalIgnoreCase);
            string bodyText = genericMode
                ? "Select a source file (any type) or a source folder."
                : "Select a font file (.ttf / .otf / .fon / .ttc) or a font folder.";

            // Standard themed dialog (matches the app's other dialogs)
            var win = BuildNewDialog("Create Package - " + title);
            win.Width = 460; win.MaxWidth = 460;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, title);
            int r = 0;
            NdAddRow(cg, r++, "Source", bodyText, lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);

            void PickFile()
            {
                win.Close();
                var dlg = new OpenFileDialog
                {
                    Title = genericMode ? "Select Source File" : "Select Font File",
                    Filter = genericMode
                        ? "All Files (*.*)|*.*"
                        : "Font Files (*.ttf;*.otf;*.fon;*.ttc)|*.ttf;*.otf;*.fon;*.ttc|All Files (*.*)|*.*",
                    InitialDirectory = seedDir
                };
                if (dlg.ShowDialog() == true) { chosen = dlg.FileName; }
            }

            void PickFolder()
            {
                win.Close();
                try
                {
                    var folderDlg = (IFileDialog)new FileOpenDialogClass();
                    folderDlg.SetTitle(genericMode ? "Select Source Folder" : "Select Font Folder");
                    folderDlg.GetOptions(out uint opts);
                    folderDlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                    var iid = typeof(IShellItem).GUID;
                    SHCreateItemFromParsingName(seedDir, IntPtr.Zero, ref iid, out IShellItem sf);
                    folderDlg.SetFolder(sf);
                    if (folderDlg.Show(IntPtr.Zero) == 0)
                    {
                        folderDlg.GetResult(out IShellItem res);
                        res.GetDisplayName(SIGDN_FILESYSPATH, out string p);
                        if (p != null) { chosen = p; }
                    }
                }
                catch
                {
                    using var fb = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description = genericMode ? "Select Source Folder" : "Select Font Folder",
                        UseDescriptionForTitle = true,
                        SelectedPath = seedDir
                    };
                    if (fb.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    { chosen = fb.SelectedPath; }
                }
            }

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = NdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = NdMakeButtons(sec,
                ("📄 File",   PickFile),
                ("📁 Folder", PickFolder),
                ("Cancel",    () => win.Close())
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            win.ShowDialog();
            return chosen;
        }

        private void BrowseMainFile_Click(object sender, RoutedEventArgs e)
        {
            // Seed dialog to where Source File or Folder lives
            string srcVal = TxtSourcePath.Text.Trim();
            string init;
            if (!string.IsNullOrWhiteSpace(srcVal) && Directory.Exists(srcVal))
                init = srcVal;
            else if (!string.IsNullOrWhiteSpace(srcVal) && File.Exists(srcVal))
                init = Path.GetDirectoryName(srcVal) ?? GetSystemDrive();
            else
                init = GetSystemDrive();
            var dlg = new OpenFileDialog { Title = "Select Source File", Filter = "Installers (*.msi;*.exe)|*.msi;*.exe|All Files (*.*)|*.*", InitialDirectory = init };
            if (dlg.ShowDialog() == true)
            {
                TxtMainFile.Text      = dlg.FileName;
                // Clear all stale values immediately before background fetch
                TxtAppVendor.Text     = "";
                TxtAppName.Text       = "";
                TxtAppVersion.Text    = "";
                TxtAppProcesses.Text  = "";
                FetchAndApplyFileInfo(dlg.FileName);
            }
        }

        private void ClearMainFile_Click(object sender, RoutedEventArgs e)
            => TxtMainFile.Clear();

        // ══════════════════════════════════════════════════════════════
        // DRAG-DROP
        // ══════════════════════════════════════════════════════════════
        private void PathBox_DragOver(object sender, DragEventArgs e)
        { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }

        private void PathBox_Drop(object sender, DragEventArgs e)
        {
            if (sender is not TextBox tb || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;
            string dropped = files[0];
            if (tb == TxtMainFile)
            {
                if (File.Exists(dropped))
                {
                    tb.Text               = dropped;
                    TxtAppVendor.Text     = "";
                    TxtAppName.Text       = "";
                    TxtAppVersion.Text    = "";
                    TxtAppProcesses.Text  = "";
                    FetchAndApplyFileInfo(dropped);
                }
            }
            else if (tb == TxtSourcePath)
            {
                // Source is now folder-only
                string f = Directory.Exists(dropped) ? dropped : Path.GetDirectoryName(dropped) ?? "";
                if (!string.IsNullOrEmpty(f))
                {
                    tb.Text = f;
                    AutoPopulateMainSourceFile(f);
                }
            }
            else { string f = Directory.Exists(dropped) ? dropped : Path.GetDirectoryName(dropped) ?? ""; if (!string.IsNullOrEmpty(f)) tb.Text = f; }
        }

        // ── .lnk drag-and-drop onto the Script editor ────────────────────────────
        // Allows the user to drag a desktop shortcut (.lnk) file onto any line of
        // the script.  Two lines are inserted at the drop position:
        //   # Remove Desktop shortcut - <ShortcutName>
        //   Remove-ADTFile -Path "<envVar>\<folder>\<ShortcutName>.lnk"
        // Known folder paths are mapped to their PowerShell $env* equivalents.

        private void TxtScript_LnkDragOver(object sender, DragEventArgs e)
        {
            // If the window is minimized (e.g. user dragged .lnk onto the taskbar icon),
            // restore it so the user can see the script and complete the drop.
            var win = Window.GetWindow(this);
            if (win != null && win.WindowState == WindowState.Minimized)
            {
                win.WindowState = WindowState.Normal;
                win.Activate();
            }

            // Accept ALL FileDrop types so the WPF Drop event fires directly on TxtScript.
            // This gives us e.GetPosition(TxtScript) - accurate coords with no DPI
            // conversion needed - for all file types (.exe, .msi, .lnk, etc.).
            // Previously only .lnk was accepted here, forcing all other types through
            // the WM_DROPFILES shell path which uses different coordinate conventions.
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// Called by MainWindow.HandleShellDrop when a .lnk is dropped via the
        /// Windows Shell drag-drop path (WM_DROPFILES), e.g. after restoring
        /// from the taskbar. Converts the drop point from window client coords
        /// to TxtScript-relative coords and delegates to the shared insert logic.
        /// </summary>
        internal void HandleLnkShellDrop(List<string> paths, System.Windows.Point windowClientPt)
        {
            if (_wpfLnkDropHandled) { _wpfLnkDropHandled = false; return; }
            if (TxtScript == null || string.IsNullOrEmpty(TxtScript.Text)) return;
            if (paths == null || paths.Count == 0) return;

            // PointToScreen/PointFromScreen require the element to be in a live
            // PresentationSource. When called via WM_DROPFILES (shell path) after
            // a restore the Page may not be connected yet - guard with try/catch
            // and fall back to the last caret line or line 0.
            int dropLine = 0;
            try
            {
                var win = Window.GetWindow(this);
                System.Windows.Point screenPt = win != null
                    ? win.PointToScreen(windowClientPt)
                    : PointToScreen(windowClientPt);
                var scriptPt = TxtScript.PointFromScreen(screenPt);
                // Robust on empty lines - see LineIndexFromPoint. Dropping a file onto a blank
                // line used to compute the LAST line and insert the snippet at the end.
                dropLine = LineIndexFromPoint(scriptPt);
            }
            catch
            {
                // Visual not yet connected - insert after current caret line
                try
                {
                    int ci = TxtScript.CaretIndex;
                    dropLine = TxtScript.GetLineIndexFromCharacterIndex(ci);
                }
                catch { dropLine = 0; }
            }
            dropLine = Math.Max(0, Math.Min(dropLine, TxtScript.LineCount - 1));

            ProcessDroppedFiles(paths.ToArray(), dropLine);
        }
        private void TxtScript_LnkDrop(object sender, DragEventArgs e)
        {
            // Receives Drop even when TxtScript.IsReadOnly=True because it is wired
            // via AddHandler(..., handledEventsToo: true) in the Loaded handler.
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            // ── Mark handled FIRST so the WM_DROPFILES shell path (HandleLnkShellDrop)
            // skips this drop even if a modal dialog is shown during processing below.
            // Without this, WM_DROPFILES arrives while ShowDialog() is spinning its own
            // message loop and _wpfLnkDropHandled is still false → double processing.
            _wpfLnkDropHandled = true;
            e.Handled = true;

            // ── Determine drop target line using direct TxtScript coords ───────
            // e.GetPosition(TxtScript) is in TxtScript's own coordinate space -
            // accurate at any DPI with no conversion required.
            if (TxtScript == null || string.IsNullOrEmpty(TxtScript.Text)) return;

            var dropPt   = e.GetPosition(TxtScript);
            // Robust on empty lines - see LineIndexFromPoint.
            int dropLine = LineIndexFromPoint(dropPt);
            dropLine = Math.Max(0, Math.Min(dropLine, TxtScript.LineCount - 1));

            // ── Route all files through the shared handler ─────────────────────
            // .lnk files use shortcut-specific logic; all others (exe/msi/ps1/etc.)
            // go through ProcessDroppedFiles - same path as copy-paste.
            var lnkFiles   = files.Where(f => f != null &&
                                  Path.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase)).ToList();
            var otherFiles = files.Where(f => f != null &&
                                  !Path.GetExtension(f).Equals(".lnk", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (string lnkPath in lnkFiles)
            {
                string shortcutName = Path.GetFileNameWithoutExtension(lnkPath);
                string shortcutDir  = Path.GetDirectoryName(lnkPath) ?? "";
                var (commentLine, cmdLine) = BuildShortcutLines(shortcutDir, shortcutName);
                dropLine = InsertShortcutLines(commentLine, cmdLine, dropLine);
            }

            if (otherFiles.Count > 0)
                ProcessDroppedFiles(otherFiles.ToArray(), dropLine);
        }

        /// <summary>
        /// Shared insertion logic: inserts comment + command at the given line,
        /// ensuring exactly one blank line before the comment and after the command.
        /// </summary>
        /// <summary>
        /// Inserts a shortcut comment+command pair after dropLine with blank-line
        /// padding. Returns the line index of the blank line after the command
        /// so callers can chain multiple insertions sequentially.
        /// </summary>
        /// <summary>
        /// If the exact command is already present in the script (editor or the on-disk
        /// copy), shows a styled prompt asking whether to add it again. Returns true to
        /// proceed with the insertion, false to skip it.
        /// </summary>
        private bool ConfirmDuplicateCommand(string cmdLine)
        {
            string needle = (cmdLine ?? "").Trim();
            if (needle.Length == 0) return true;

            bool exists = TxtScript != null
                && TxtScript.Text.Replace("\r\n", "\n").Replace("\r", "\n")
                       .Split('\n').Any(l => l.Trim() == needle);
            if (!exists && _createdScriptPath != null && File.Exists(_createdScriptPath))
            {
                try
                {
                    string disk = File.ReadAllText(_createdScriptPath, new UTF8Encoding(false))
                                      .Replace("\r\n", "\n").Replace("\r", "\n");
                    exists = disk.Split('\n').Any(l => l.Trim() == needle);
                }
                catch { }
            }
            if (!exists) return true;

            bool addAgain = false;
            var win = BuildNewDialog("Create Package - Add Command");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                BuildNewDialogShell(win, "Create Package - Add Command");
            int r = 0;

            NdAddRow(cg, r++, "Command", needle, lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            NdAddRow(cg, r++, "Action",
                "This command already exists in the script. Add it again?",
                lblFg, boxBg, boxBdr, valFg, false);
            NdAddSpacer(cg, r++);

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var questionIcon = NdDecisionIcon();
            Grid.SetRow(questionIcon, r); Grid.SetColumn(questionIcon, 0);
            cg.Children.Add(questionIcon);

            var btns = NdMakeButtons(sec,
                ("✔ Yes", () => { addAgain = true;  win.Close(); }),
                ("✕ No",  () => { addAgain = false; win.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            win.ShowDialog();
            return addAgain;
        }

        private int InsertShortcutLines(string commentLine, string cmdLine, int dropLine)
        {
            // If this exact command is already in the script, ask before adding a duplicate.
            if (!ConfirmDuplicateCommand(cmdLine)) return dropLine;

            CaptureUndoState();

            // In a batch drop, Apply* methods write to disk but BeginInvoke hasn't yet
            // updated TxtScript.Text. Always read from disk (the accumulated truth) so
            // we don't overwrite other files' contributions.
            string sourceText = TxtScript.Text;
            if (_createdScriptPath != null && File.Exists(_createdScriptPath))
            {
                try
                {
                    string diskText = File.ReadAllText(_createdScriptPath, new UTF8Encoding(false))
                                         .Replace("\r\n", "\n").Replace("\r", "\n");
                    // Use disk content only if it has MORE content than the editor
                    // (i.e. Apply* has written new stuff that BeginInvoke hasn't shown yet)
                    if (diskText.Length > sourceText.Length)
                        sourceText = diskText;
                }
                catch { }
            }

            var lines = sourceText.Split('\n').ToList();

            // Clamp dropLine to valid range (disk content may have more lines)
            if (dropLine >= lines.Count) dropLine = lines.Count - 1;
            if (dropLine < 0) dropLine = 0;

            // Check neighbours BEFORE any insertion (indices are stable here)
            bool dropLineIsBlank = lines[dropLine].Trim() == "";
            bool nextLineIsBlank = (dropLine + 1 < lines.Count) && lines[dropLine + 1].Trim() == "";

            // Build the block: [blank?] comment cmdLine [blank?]
            var block = new System.Collections.Generic.List<string>();
            if (!dropLineIsBlank) block.Add("");    // blank before comment
            block.Add(commentLine);
            block.Add(cmdLine);
            if (!nextLineIsBlank) block.Add("");    // blank after command

            lines.InsertRange(dropLine + 1, block);

            int insertedAt = dropLineIsBlank ? dropLine + 1 : dropLine + 2;
            ReplaceScriptLines(lines.ToArray(), targetLine: insertedAt);
            _savedSelStartLine = insertedAt;
            _savedSelEndLine   = insertedAt + 1;
            HighlightCurrentLine();
            MarkScriptDirty();

            // Write merged result back to disk for the next file in the batch
            if (_createdScriptPath != null)
            {
                try { File.WriteAllText(_createdScriptPath, TxtScript.Text, new UTF8Encoding(true)); }
                catch { }
            }

            // Return the line of the trailing blank so the next insertion chains after it
            return insertedAt + 1; // cmdLine is at insertedAt; blank is at insertedAt+1
        }

        // ── Extensions that get the Install/Remove dialog when outside package Files folder ──
        private static readonly HashSet<string> _scriptFileExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".mst", ".reg", ".bat", ".cmd", ".inf", ".iss", ".cer", ".msp", ".ps1"
        };

        /// <summary>
        /// Returns the package "Files" folder path by locating Invoke-AppDeployToolkit.ps1
        /// in the same directory as the Files folder. Checks (in order):
        ///   1. TemplatePath sibling  2. OutputPath\PackageName sibling
        /// Returns null if not determinable.
        /// </summary>
        private string? GetPackageFilesFolder()
        {
            // Helper: given a candidate root dir, check if it contains both
            // "Files" subfolder and "Invoke-AppDeployToolkit.ps1"
            static string? Check(string dir)
            {
                if (!Directory.Exists(dir)) return null;
                bool hasScript = Directory.GetFiles(dir, "Invoke-AppDeployToolkit.ps1",
                                     SearchOption.TopDirectoryOnly).Length > 0
                              || Directory.GetFiles(dir, "Invoke-AppDeployToolkit.ps1",
                                     SearchOption.AllDirectories).Any(f =>
                                         string.Equals(Path.GetDirectoryName(f), dir,
                                             StringComparison.OrdinalIgnoreCase));
                string filesDir = Path.Combine(dir, "Files");
                return (hasScript && Directory.Exists(filesDir)) ? filesDir : null;
            }

            // 0. Currently open/created package - most authoritative, since this is
            //    the exact script loaded in the editor (the one being dropped/pasted
            //    into). Template/Output/PackageName fields can point elsewhere, so
            //    derive Files\ straight from _createdScriptPath's folder first.
            if (!string.IsNullOrEmpty(_createdScriptPath))
            {
                string? pkgDir = Path.GetDirectoryName(_createdScriptPath);
                if (!string.IsNullOrEmpty(pkgDir))
                {
                    var r0 = Check(pkgDir);
                    if (r0 != null) return r0;
                }
            }

            // 1. Template folder
            string tmpl = TxtTemplatePath?.Text.Trim() ?? "";
            if (!string.IsNullOrEmpty(tmpl))
            {
                var r = Check(tmpl);
                if (r != null) return r;
            }

            // 2. OutputPath\PackageName
            string output  = TxtOutputPath?.Text.Trim() ?? "";
            string pkgName = TxtPackageName?.Text.Trim() ?? "";
            if (!string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(pkgName))
            {
                // Walk subdirs of pkgRoot to find one containing Invoke-AppDeployToolkit.ps1
                string pkgRoot = Path.Combine(output, pkgName);
                if (Directory.Exists(pkgRoot))
                {
                    foreach (string sub in Directory.GetDirectories(pkgRoot, "*",
                                             SearchOption.AllDirectories))
                    {
                        var r = Check(sub);
                        if (r != null) return r;
                    }
                    var r2 = Check(pkgRoot);
                    if (r2 != null) return r2;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns true if filePath is inside the package Files folder (or its subfolders).
        /// </summary>
        private bool IsInsidePackageFilesFolder(string filePath)
        {
            string? filesFolder = GetPackageFilesFolder();
            if (filesFolder == null) return false;
            return filePath.StartsWith(filesFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                                       StringComparison.OrdinalIgnoreCase)
                || filePath.Equals(filesFolder, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Core routing method called by all drop/paste entry points.
        /// For each file:
        ///   - .lnk  → always insert Remove-ADT* shortcut lines
        ///   - Script extensions (.exe/.msi/etc.) inside Files folder → insert Start-ADTProcess snippet with $($adtSession.DirFiles)\relative\path
        ///   - Script extensions outside Files folder → show Install/Remove dialog
        ///   - All other extensions (.txt/.dll/etc.) → insert Remove-ADTFile line
        /// Returns the last dropLine so callers can chain.
        /// </summary>
        private int ProcessDroppedFiles(string[] files, int dropLine)
        {
            // Sync the editor's current text to disk first. The insertion and Apply*
            // helpers below treat the on-disk script as the accumulating source of truth
            // during a batch; if the user edited or deleted lines in the editor since the
            // last drop/paste, the file would be stale and those deletions would reappear.
            if (_createdScriptPath != null && TxtScript != null)
            {
                try { File.WriteAllText(_createdScriptPath, TxtScript.Text, new UTF8Encoding(true)); }
                catch { }
            }

            // Pre-compute a set of all .mst base names in this batch so we can
            // skip .msi files that have a paired .mst (mst takes priority).
            var mstBaseNames = new HashSet<string>(
                files.Where(f => !string.IsNullOrEmpty(f) &&
                            Path.GetExtension(f).Equals(".mst", StringComparison.OrdinalIgnoreCase))
                     .Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            // Tracks whether any disallowed item (a folder, or a non-whitelisted
            // extension) was dropped/pasted from inside the package Files folder, so a
            // single advisory message box can be shown after the batch.
            bool notAllowed = false;

            foreach (string filePath in files)
            {
                if (string.IsNullOrEmpty(filePath)) continue;

                // Skip the .msi only when its matching .mst was pasted in THIS batch. That
                // .mst path adds -Transforms and handles the install. A .mst that merely
                // exists on disk next to the .msi but was not pasted must NOT skip the .msi,
                // otherwise pasting the .msi by itself would silently add nothing.
                if (Path.GetExtension(filePath).Equals(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    string msiBase = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
                    if (mstBaseNames.Contains(msiBase))
                        continue; // paired .mst in this batch handles this msi
                }

                // ── Inside the package Files folder ───────────────────────────
                // Only whitelisted file types add a command (built with a
                // $($adtSession.DirFiles)\relative\path). Folders and any other
                // extension are payload, not install/run targets, so they are
                // skipped and an advisory message is shown after the batch.
                if (IsInsidePackageFilesFolder(filePath))
                {
                    bool isDir = Directory.Exists(filePath) && !File.Exists(filePath);
                    if (!isDir && _scriptFileExts.Contains(Path.GetExtension(filePath)))
                    {
                        if (ConfirmAddInstallCommand(filePath, files.Length > 1))
                            dropLine = InsertFilesInstallLine(filePath, dropLine);
                    }
                    else
                        notAllowed = true;
                    continue;
                }

                // ── Folder (outside Files) → confirm, then Remove-ADTFolder ────
                if (Directory.Exists(filePath) && !File.Exists(filePath))
                {
                    var (addFolder, stopFolder) = ConfirmAddRemoveCommand(filePath, files.Length > 1);
                    if (!addFolder)
                    {
                        if (stopFolder) return dropLine; // Cancel → stop the batch
                        continue;                        // No → skip this item
                    }
                    var (comment, cmd) = BuildFolderRemoveLines(filePath);
                    dropLine = InsertShortcutLines(comment, cmd, dropLine);
                    continue;
                }

                string ext = Path.GetExtension(filePath);

                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var (addLnk, stopLnk) = ConfirmAddRemoveCommand(filePath, files.Length > 1);
                    if (!addLnk)
                    {
                        if (stopLnk) return dropLine; // Cancel → stop the batch
                        continue;                     // No → skip this item
                    }

                    string lnkDir  = Path.GetDirectoryName(filePath) ?? "";
                    string lnkName = Path.GetFileNameWithoutExtension(filePath);
                    string lnkFile = Path.GetFileName(filePath);

                    // Determine if this .lnk is under C:\Users\Public\*
                    // If so, use BuildFileRemoveLines (produces $envPublic\... path)
                    // rather than BuildShortcutLines which uses the per-user profile path.
                    string publicRoot = Path.GetDirectoryName(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                            .TrimEnd(Path.DirectorySeparatorChar))
                        ?? @"C:\Users\Public";
                    bool isPublic = lnkDir.StartsWith(
                        publicRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)
                        || lnkDir.Equals(publicRoot, StringComparison.OrdinalIgnoreCase);

                    if (isPublic)
                    {
                        // Route through BuildFileRemoveLines to get $envPublic\... path
                        var (comment, cmd) = BuildFileRemoveLines(lnkDir, lnkFile);
                        dropLine = InsertShortcutLines(comment, cmd, dropLine);
                    }
                    else
                    {
                        // Normal shortcut logic for personal user profile / system paths
                        var (comment, cmd) = BuildShortcutLines(lnkDir, lnkName);
                        dropLine = InsertShortcutLines(comment, cmd, dropLine);
                    }
                }
                else if (_scriptFileExts.Contains(ext))
                {
                    // Script-type file - only show dialog if outside the package Files folder
                    if (!IsInsidePackageFilesFolder(filePath))
                    {
                        var (newLine, action) = ShowFileActionDialog(filePath, dropLine, files.Length > 1);
                        dropLine = newLine;
                        // Yes adds the command inside the dialog; No and Cancel both skip
                        // this file and continue to the next one in the batch.
                    }
                    else
                    {
                        // Inside Files folder: insert Start-ADTProcess snippet at the drop line
                        // using $($adtSession.DirFiles)\relative\path so the script is portable.
                        dropLine = InsertFilesInstallLine(filePath, dropLine);
                    }
                }
                else
                {
                    // Any other file type (outside Files) → confirm, then Remove-ADTFile.
                    // (Inside-Files items were already handled at the top of the loop.)
                    var (addOther, stopOther) = ConfirmAddRemoveCommand(filePath, files.Length > 1);
                    if (addOther)
                        dropLine = InsertGenericRemoveLine(filePath, dropLine);
                    else if (stopOther)
                        return dropLine; // Cancel → stop the batch
                    // No → skip this item
                }
            }
            if (notAllowed)
                ShowThemedInfoDialog(
                    "Not Allowed",
                    "Allowed types",
                    "Only these file types can be added from the package Files folder:\n\n" +
                    ".exe, .msi, .mst, .ps1, .bat, .cmd, .msp, .reg, .iss, .cer, .inf\n\n" +
                    "Folders and any other file types are skipped.");

            return dropLine;
        }

        /// <summary>
        /// Styled confirmation before adding a Remove command for an item pasted/dropped
        /// from outside the package Files folder. A single-item paste shows Yes/Cancel;
        /// a multi-item paste shows Yes/No/Cancel (No and Cancel both skip this one and
        /// continue to the next). Returns (add, stop): add = insert the Remove command.
        /// </summary>
        private (bool add, bool stop) ConfirmAddRemoveCommand(string path, bool multiple)
        {
            bool isFolder = Directory.Exists(path) && !File.Exists(path);
            string kind   = isFolder ? "folder" : "file";

            bool add = false, stop = false;

            var win = BuildNewDialog("Create Package - Add Command");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                BuildNewDialogShell(win, "Create Package - Add Command");
            int r = 0;

            // Row 1: full path
            NdAddRow(cg, r++, "Path", path, lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            // Row 2: action message
            NdAddRow(cg, r++, "Action",
                $"Add a command to remove this {kind}?",
                lblFg, boxBg, boxBdr, valFg, false);
            NdAddSpacer(cg, r++);
            // Row 3: review note
            NdAddRow(cg, r++, "Note",
                "A command will be added automatically. Please review it and make any necessary changes before deploying the application.",
                lblFg, boxBg, boxBdr, valFg, false);
            NdAddSpacer(cg, r++);

            // Button row - question icon left, buttons right (matches other dialogs)
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var questionIcon = NdDecisionIcon();
            Grid.SetRow(questionIcon, r); Grid.SetColumn(questionIcon, 0);
            cg.Children.Add(questionIcon);

            // Single-item paste → Yes/Cancel (No has nothing to skip to).
            // Multi-item paste  → Yes/No/Cancel (No skips this one, Cancel stops the batch).
            var btns = multiple
                ? NdMakeButtons(sec,
                    ("✔ Yes",    () => { add = true;  stop = false; win.Close(); }),
                    ("✕ No",     () => { add = false; stop = false; win.Close(); }),
                    ("✕ Cancel", () => { add = false; stop = false; win.Close(); }))
                : NdMakeButtons(sec,
                    ("✔ Yes",    () => { add = true;  stop = false; win.Close(); }),
                    ("✕ Cancel", () => { add = false; stop = false; win.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            win.ShowDialog();
            return (add, stop);
        }

        /// <summary>
        /// Styled confirmation before adding an install/run command for a script-type file
        /// pasted/dropped from inside the package Files folder. There is no Remove option here
        /// (the file is package payload), so no radio buttons are shown - just a confirmation.
        /// A single-item paste shows Yes/Cancel; a multi-item paste shows Yes/No/Cancel (No and
        /// Cancel both skip this one and continue to the next). Returns true to add the command.
        /// </summary>
        private bool ConfirmAddInstallCommand(string path, bool multiple)
        {
            bool add = false;

            var win = BuildNewDialog("Create Package - Add Command");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                BuildNewDialogShell(win, "Create Package - Add Command");
            int r = 0;

            // Row 1: full path
            NdAddRow(cg, r++, "Path", path, lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            // Row 2: action message
            NdAddRow(cg, r++, "Action", "Add a command to execute this file?", lblFg, boxBg, boxBdr, valFg, false);
            NdAddSpacer(cg, r++);
            // Row 3: review note
            NdAddRow(cg, r++, "Note",
                "A command will be added automatically. Please review it and make any necessary changes before deploying the application.",
                lblFg, boxBg, boxBdr, valFg, false);
            NdAddSpacer(cg, r++);

            // Button row - question icon left, buttons right (matches other dialogs)
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var questionIcon = NdDecisionIcon();
            Grid.SetRow(questionIcon, r); Grid.SetColumn(questionIcon, 0);
            cg.Children.Add(questionIcon);

            // Single-item paste → Yes/Cancel. Multi-item paste → Yes/No/Cancel
            // (No and Cancel both skip this one and continue to the next).
            var btns = multiple
                ? NdMakeButtons(sec,
                    ("✔ Yes",    () => { add = true;  win.Close(); }),
                    ("✕ No",     () => { add = false; win.Close(); }),
                    ("✕ Cancel", () => { add = false; win.Close(); }))
                : NdMakeButtons(sec,
                    ("✔ Yes",    () => { add = true;  win.Close(); }),
                    ("✕ Cancel", () => { add = false; win.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            win.ShowDialog();
            return add;
        }

        // Return values for ShowFileActionDialog
        private enum FileActionResult { Yes, No, Cancel }

        /// <summary>
        /// Shows the File Action dialog using the same visual style as other app dialogs.
        /// Returns (dropLine, FileActionResult):
        ///   Yes    = process this file (Insert/Remove), continue queue
        ///   No     = skip this file, continue queue
        ///   Cancel = stop entire queue
        /// </summary>
        private (int dropLine, FileActionResult action) ShowFileActionDialog(
            string filePath, int dropLine, bool multiple)
        {
            FileActionResult result = FileActionResult.Cancel;
            bool rbRemoveChecked = true;

            var win = BuildNewDialog("Create Package - Add Command");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                BuildNewDialogShell(win, "Create Package - Add Command");
            int r = 0;

            // Row 1: full file path
            NdAddRow(cg, r++, "Path",   filePath,                              lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            // Row 2: action label
            NdAddRow(cg, r++, "Action", "Add a command to remove or execute this file?", lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);

            // Row 3: radio buttons - value column, icon column left
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var radioIcon = new TextBlock
            {
                Text = "📄", FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            Grid.SetRow(radioIcon, r); Grid.SetColumn(radioIcon, 0);
            cg.Children.Add(radioIcon);

            var radioPanel = new StackPanel
            {
                Orientation       = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 4, 0, 4),
            };
            var rbRemove = new RadioButton
            {
                Content    = "Remove file",
                IsChecked  = true,
                Foreground = valFg,
                FontSize   = 12,
                Margin     = new Thickness(0, 0, 0, 6),
            };
            var rbInstall = new RadioButton
            {
                Content    = "Execute file",
                IsChecked  = false,
                Foreground = valFg,
                FontSize   = 12,
            };
            rbRemove.Checked  += (_, _) => rbRemoveChecked = true;
            rbInstall.Checked += (_, _) => rbRemoveChecked = false;
            radioPanel.Children.Add(rbRemove);
            radioPanel.Children.Add(rbInstall);

            var radioBorder = new Border
            {
                Background      = boxBg,
                BorderBrush     = boxBdr,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(8, 6, 8, 6),
                Child           = radioPanel,
            };
            Grid.SetRow(radioBorder, r); Grid.SetColumn(radioBorder, 1);
            cg.Children.Add(radioBorder);
            r++;

            // Review note
            NdAddRow(cg, r++, "Note",
                "A command will be added automatically. Please review it and make any necessary changes before deploying the application.",
                lblFg, boxBg, boxBdr, valFg, false);

            // Spacer before button row
            NdAddSpacer(cg, r++);

            // Button row - same row for icon (?) and buttons
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var questionIcon = NdDecisionIcon();
            Grid.SetRow(questionIcon, r); Grid.SetColumn(questionIcon, 0);
            cg.Children.Add(questionIcon);

            var btns = multiple
                ? NdMakeButtons(sec,
                    ("✔ Yes",    () => { result = FileActionResult.Yes;    win.Close(); }),
                    ("✕ No",     () => { result = FileActionResult.No;     win.Close(); }),
                    ("✕ Cancel", () => { result = FileActionResult.Cancel; win.Close(); }))
                : NdMakeButtons(sec,
                    ("✔ Yes",    () => { result = FileActionResult.Yes;    win.Close(); }),
                    ("✕ Cancel", () => { result = FileActionResult.Cancel; win.Close(); }));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            win.ShowDialog();

            if (result == FileActionResult.Yes)
            {
                dropLine = rbRemoveChecked
                    ? InsertGenericRemoveLine(filePath, dropLine)
                    : InsertInstallLines(filePath, dropLine);
            }

            return (dropLine, result);
        }

        /// <summary>
        /// Builds and inserts an install comment + command for a script-type file
        /// dropped/pasted when "Install file" is selected.
        /// Reuses ExeInfoCP logic: registry lookup first, then FileVersionInfo.
        /// Uses MatchBuiltInSwitches for known silent install switches.
        /// Replaces the drive letter with $envSystemDrive for portability.
        /// </summary>
        private int InsertInstallLines(string filePath, int dropLine)
        {
            string ext      = Path.GetExtension(filePath).ToLowerInvariant();
            string fileName = Path.GetFileName(filePath);
            string fileBase = Path.GetFileNameWithoutExtension(filePath);
            string fileDir  = Path.GetDirectoryName(filePath) ?? "";

            // ── Classify path: user-profile vs system ────────────────────────────
            string usersRoot = Path.GetDirectoryName(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    .TrimEnd(Path.DirectorySeparatorChar)) ?? @"C:\Users";
            string norm = filePath.TrimEnd(Path.DirectorySeparatorChar);

            bool isUserProfile = norm.StartsWith(
                usersRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

            // Build portable path ($envUserProfile\... or $envSystemDrive\...)
            string portable;
            if (isUserProfile)
            {
                string afterUsers = norm.Substring(usersRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                int sep = afterUsers.IndexOf(Path.DirectorySeparatorChar);
                string rel = sep >= 0 ? afterUsers.Substring(sep + 1) : "";
                portable = string.IsNullOrEmpty(rel)
                    ? $@"$envUserProfile\{fileName}"
                    : $@"$envUserProfile\{rel}";
            }
            else
            {
                string pathRoot = Path.GetPathRoot(filePath) ?? "";
                portable = pathRoot.Length > 0
                    ? "$envSystemDrive" + filePath.Substring(pathRoot.Length - 1)
                    : filePath;
            }

            // ps1/certutil always use full $envSystemDrive path for the executable
            string psExe = @"$envSystemDrive\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

            string adtProc    = isUserProfile ? "Start-ADTProcessAsUser" : "Start-ADTProcess";
            string adtMsiProc = isUserProfile ? "Start-ADTMsiProcessAsUser" : "Start-ADTMsiProcess";

            string comment;
            string cmd;

            switch (ext)
            {
                // ── .msi ─────────────────────────────────────────────────────────
                case ".msi":
                {
                    var (pc, mfr, pn, pv) = ReadMsiAllProperties(filePath);
                    // Registry lookup to get installed version info if app is installed
                    CpInstalledInfo? regInfo = null;
                    if (!string.IsNullOrWhiteSpace(pc))
                        regInfo = LookupInstalledByProductCode(pc);
                    string body;
                    if (regInfo != null)
                        body = BuildAppComment("", regInfo.Publisher, regInfo.DisplayName, regInfo.DisplayVersion)
                               .TrimStart('#', ' ').Trim();
                    else
                        body = BuildAppComment(pc, mfr, pn, pv).TrimStart('#', ' ').Trim();
                    if (string.IsNullOrWhiteSpace(body)) body = fileName;

                    // .msi alone - no transforms (drop .mst separately to get transforms)
                    comment = $"    # Install {body}";
                    cmd     = $"    {adtMsiProc} -Action 'Install' -FilePath \"{portable}\"";
                    break;
                }

                // ── .mst (standalone drop - look for matching .msi) ───────────────
                case ".mst":
                {
                    string msiPath = Path.Combine(fileDir, fileBase + ".msi");
                    if (!File.Exists(msiPath))
                        return dropLine; // no matching .msi - skip per spec

                    var (pc, mfr, pn, pv) = ReadMsiAllProperties(msiPath);
                    CpInstalledInfo? regInfo = null;
                    if (!string.IsNullOrWhiteSpace(pc))
                        regInfo = LookupInstalledByProductCode(pc);
                    string body;
                    if (regInfo != null)
                        body = BuildAppComment("", regInfo.Publisher, regInfo.DisplayName, regInfo.DisplayVersion)
                               .TrimStart('#', ' ').Trim();
                    else
                        body = BuildAppComment(pc, mfr, pn, pv).TrimStart('#', ' ').Trim();
                    if (string.IsNullOrWhiteSpace(body)) body = fileBase + ".msi";

                    // Portable path for the .msi sibling
                    string msiPortable;
                    if (isUserProfile)
                    {
                        string afterUsers = Path.GetDirectoryName(norm)!
                            .Substring(usersRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                        int sep = afterUsers.IndexOf(Path.DirectorySeparatorChar);
                        string rel = sep >= 0 ? afterUsers.Substring(sep + 1) : "";
                        msiPortable = string.IsNullOrEmpty(rel)
                            ? $@"$envUserProfile\{fileBase}.msi"
                            : $@"$envUserProfile\{rel}\{fileBase}.msi";
                    }
                    else
                    {
                        string pathRoot = Path.GetPathRoot(msiPath) ?? "";
                        msiPortable = pathRoot.Length > 0
                            ? "$envSystemDrive" + msiPath.Substring(pathRoot.Length - 1)
                            : msiPath;
                    }

                    string mstPortable = msiPortable.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                        ? msiPortable[..^4] + ".mst"
                        : msiPortable + ".mst";
                    comment = $"    # Install {body}";
                    cmd     = $"    {adtMsiProc} -FilePath \"{msiPortable}\" -Transforms \"{mstPortable}\"";
                    break;
                }

                // ── .iss (look for matching .exe in same folder) ──────────────────
                case ".iss":
                {
                    string exePath = Path.Combine(fileDir, fileBase + ".exe");
                    if (!File.Exists(exePath))
                        return dropLine; // no matching .exe - skip per spec

                    // Get app info from exe
                    string fviPub = "", fviName = "", fviVer = "";
                    try
                    {
                        var vi  = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                        fviPub  = vi.CompanyName?.Trim()    ?? "";
                        fviName = vi.ProductName?.Trim()    ?? "";
                        fviVer  = vi.ProductVersion?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviName)) fviName = vi.FileDescription?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviVer))  fviVer  = vi.FileVersion?.Trim()     ?? "";
                    }
                    catch { }

                    var regI = LookupInstalledByExe(exePath,
                        !string.IsNullOrWhiteSpace(fviName) ? fviName : fileBase);
                    string body = regI != null
                        ? BuildAppComment("", regI.Publisher, regI.DisplayName,
                              !string.IsNullOrWhiteSpace(regI.DisplayVersion) ? regI.DisplayVersion : fviVer)
                        : BuildAppComment("", fviPub, fviName, fviVer);
                    body = body.TrimStart('#', ' ').Trim();
                    if (string.IsNullOrWhiteSpace(body)) body = fileBase + ".exe";

                    // Portable paths for both .exe and .iss
                    string exePortable, issPortable;
                    if (isUserProfile)
                    {
                        string afterUsers = Path.GetDirectoryName(norm)!
                            .Substring(usersRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                        int sep = afterUsers.IndexOf(Path.DirectorySeparatorChar);
                        string rel = sep >= 0 ? afterUsers.Substring(sep + 1) : "";
                        string prefix = string.IsNullOrEmpty(rel) ? "$envUserProfile" : $@"$envUserProfile\{rel}";
                        exePortable = $@"{prefix}\{fileBase}.exe";
                        issPortable = $@"{prefix}\{fileName}";
                    }
                    else
                    {
                        string pathRoot = Path.GetPathRoot(exePath) ?? "";
                        exePortable = pathRoot.Length > 0
                            ? "$envSystemDrive" + exePath.Substring(pathRoot.Length - 1) : exePath;
                        pathRoot    = Path.GetPathRoot(filePath) ?? "";
                        issPortable = pathRoot.Length > 0
                            ? "$envSystemDrive" + filePath.Substring(pathRoot.Length - 1) : filePath;
                    }

                    comment = $"    # Install {body}";
                    cmd     = $"    {adtProc} -FilePath \"{exePortable}\" -ArgumentList \"-s -f1`\"{issPortable}`\"\" -WindowStyle \"Hidden\"";
                    break;
                }

                // ── .msp ─────────────────────────────────────────────────────────
                // System path : Start-ADTMsiProcess -Action 'Patch'
                // User profile: Start-ADTMspProcessAsUser -Action "Install"
                case ".msp":
                {
                    comment = $"    # Install {fileName}";
                    cmd = isUserProfile
                        ? $"    Start-ADTMspProcessAsUser -Action \"Install\" -FilePath \"{portable}\""
                        : $"    Start-ADTMsiProcess -Action 'Patch' -FilePath \"{portable}\"";
                    break;
                }

                // ── .cer ─────────────────────────────────────────────────────────
                case ".cer":
                {
                    // Try to read installed certificate Subject from the cert store
                    string certName = "";
                    try
                    {
                        var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(filePath);
                        certName = cert.GetNameInfo(
                            System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,
                            false)?.Trim() ?? "";
                    }
                    catch { }

                    string label = string.IsNullOrWhiteSpace(certName) ? fileName : certName;
                    comment = $"    # Install {label}";
                    cmd     = $"    {adtProc} -FilePath \"certutil.exe\" -ArgumentList \"-addstore TrustedPublisher `\"{portable}`\"\" -WindowStyle \"Hidden\"";
                    break;
                }

                // ── .inf ─────────────────────────────────────────────────────────
                case ".inf":
                {
                    comment = $"    # Install {fileName}";
                    cmd     = $"    {adtProc} -FilePath \"pnputil.exe\" -ArgumentList \"/add-driver `\"{portable}`\" /install /force\" -WindowStyle \"Hidden\" -IgnoreExitCodes '512'";
                    break;
                }

                // ── .reg ─────────────────────────────────────────────────────────
                case ".reg":
                {
                    comment = $"    # Run {fileName}";
                    cmd     = $"    {adtProc} -FilePath \"regedit.exe\" -ArgumentList \"/s `\"{portable}`\"\" -WindowStyle \"Hidden\"";
                    break;
                }

                // ── .bat / .cmd ───────────────────────────────────────────────────
                case ".bat":
                case ".cmd":
                {
                    comment = $"    # Run {fileName}";
                    cmd     = $"    {adtProc} -FilePath \"{portable}\" -WindowStyle \"Hidden\"";
                    break;
                }

                // ── .ps1 ─────────────────────────────────────────────────────────
                case ".ps1":
                {
                    comment = $"    # Run {fileName}";
                    cmd     = $"    {adtProc} -FilePath \"{psExe}\" -ArgumentList \"-ExecutionPolicy Bypass -NoProfile -File `\"{portable}`\"\" -WindowStyle \"Hidden\"";
                    break;
                }

                // ── .exe (default - includes ISS-less EXE) ────────────────────────
                case ".exe":
                default:
                {
                    string fviPub = "", fviName = "", fviVer = "";
                    try
                    {
                        var vi  = System.Diagnostics.FileVersionInfo.GetVersionInfo(filePath);
                        fviPub  = vi.CompanyName?.Trim()    ?? "";
                        fviName = vi.ProductName?.Trim()    ?? "";
                        fviVer  = vi.ProductVersion?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviName)) fviName = vi.FileDescription?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviVer))  fviVer  = vi.FileVersion?.Trim()     ?? "";
                    }
                    catch { }

                    string hint = !string.IsNullOrWhiteSpace(fviName) ? fviName
                        : Regex.Replace(fileBase,
                            @"[-_\.\s]?(setup|install|installer|win\d+|x64|x86|\d[\d\.\-]*).*$",
                            "", RegexOptions.IgnoreCase).Trim();
                    if (hint.Length < 3) hint = fileBase;

                    var reg = LookupInstalledByExe(filePath, hint);
                    string appComment = reg != null
                        ? BuildAppComment("", reg.Publisher, reg.DisplayName,
                              !string.IsNullOrWhiteSpace(reg.DisplayVersion) ? reg.DisplayVersion : fviVer)
                        : BuildAppComment("", fviPub, fviName, fviVer);
                    if (string.IsNullOrWhiteSpace(appComment)) appComment = "# " + fileName;

                    string regPub  = reg?.Publisher   ?? fviPub;
                    string regName = reg?.DisplayName ?? fviName;
                    string combinedKey = $"{fileName} {regName} {regPub}".ToLowerInvariant();
                    var (instSwitch, _) = MatchBuiltInSwitches(combinedKey);
                    if (string.IsNullOrEmpty(instSwitch)) instSwitch = "/S";

                    comment = $"    # Install {appComment.TrimStart('#', ' ').Trim()}";
                    cmd     = $"    {adtProc} -FilePath \"{portable}\" -ArgumentList '{instSwitch}' -WindowStyle \"Hidden\"";
                    break;
                }
            }

            return InsertShortcutLines(comment, cmd, dropLine);
        }

        /// <summary>
        /// Called when a script-type file that lives inside the package Files folder
        /// (or one of its sub-folders) is dropped or pasted onto the Script editor.
        ///
        /// Builds a portable Start-ADTProcess snippet using $($adtSession.DirFiles)
        /// and inserts it at <paramref name="dropLine"/>. The relative sub-path under
        /// Files\ is preserved so that both flat drops and sub-folder drops work:
        ///
        ///   …\Files\vlc-3.0.23-win64.exe
        ///       → Start-ADTProcess -FilePath "$($adtSession.DirFiles)\vlc-3.0.23-win64.exe"
        ///
        ///   …\Files\Source\vlc-3.0.23-win64.exe
        ///       → Start-ADTProcess -FilePath "$($adtSession.DirFiles)\Source\vlc-3.0.23-win64.exe"
        ///
        /// App metadata priority: registry (installed) → FileVersionInfo (PE) → filename.
        /// Silent-install switch: MatchBuiltInSwitches → fallback "/S".
        /// Only .exe is fully handled for now; remaining extensions use a generic
        /// Start-ADTProcess line without an ArgumentList so they are never silently ignored.
        /// </summary>
        /// <summary>
        /// Patches the live Invoke-AppDeployToolkit.ps1 with three blocks:
        ///   1. Install snippet at dropLine
        ///   2. ARP flags after Post-Installation marker (if config says Yes and not already present)
        ///   3. Uninstall line after Uninstallation marker (if app found in registry)
        /// Used by both .exe and .iss drops from the Files folder.
        /// Returns true when the file was written (so InsertShortcutLines is skipped).
        /// </summary>
        private bool ApplyFilesExeSnippet(
            string appCommentBody, string comment, string cmd,
            string adtPath, int dropLine, string uninstSwitch,
            CpInstalledInfo? reg)
        {
            if (_createdScriptPath == null || !File.Exists(_createdScriptPath))
                return false;

            try
            {
                // Flush the live editor to disk first so that any user edits (including
                // deletions) are on disk before we read it. Without this, a command deleted
                // in the editor but not yet saved would still appear on disk, causing the
                // "already exists" check to incorrectly skip re-insertion.
                if (_scriptDirty && TxtScript != null)
                {
                    try { File.WriteAllText(_createdScriptPath, TxtScript.Text, new UTF8Encoding(true)); }
                    catch { /* best-effort; continue with disk content */ }
                }

                // Read from disk so multi-file drops accumulate correctly.
                // TxtScript.Text is only updated asynchronously via BeginInvoke and would
                // be stale for the 2nd+ file in a batch drop.
                string ps1Text = File.ReadAllText(_createdScriptPath, new UTF8Encoding(false));
                ps1Text = ps1Text.Replace("\r\n", "\n").Replace("\r", "\n");
                bool ps1Changed = false;

                const string INSTALL_MARKER   = "## <Perform Installation tasks here>";
                const string POST_MARKER      = "## <Perform Post-Installation tasks here>";
                const string UNINSTALL_MARKER = "## <Perform Uninstallation tasks here>";

                // ── Install snippet ──────────────────────────────────────────────
                bool installAlready = ps1Text.Contains(adtPath, StringComparison.OrdinalIgnoreCase);
                if (!installAlready)
                {
                    ps1Text    = InjectAtDropLine(ps1Text, dropLine, INSTALL_MARKER, comment, cmd);
                    ps1Changed = true;
                }

                // ── ARP flags ────────────────────────────────────────────────────
                var latestCfg   = ReadCreatePackageConfigFresh() ?? _cfg;
                bool doNoModify = CreatePackageConfig.FlagStrict(latestCfg.AddNoModify);
                bool doNoRepair = CreatePackageConfig.FlagStrict(latestCfg.AddNoRepair);
                bool doNoRemove = CreatePackageConfig.FlagStrict(latestCfg.AddNoRemove);

                if ((doNoModify || doNoRepair || doNoRemove) && reg != null
                    && !string.IsNullOrEmpty(reg.RegKeyPath))
                {
                    string litPath = reg.RegKeyPath
                        .Replace("HKLM:\\", "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                        .Replace("HKCU:\\", "HKEY_CURRENT_USER\\",  StringComparison.OrdinalIgnoreCase)
                        .Replace("HKLM\\",  "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                        .Replace("HKCU\\",  "HKEY_CURRENT_USER\\",  StringComparison.OrdinalIgnoreCase);

                    Microsoft.Win32.RegistryKey? liveKey = null;
                    try
                    {
                        string[] pts  = litPath.Split('\\');
                        string   subK = string.Join("\\", pts.Skip(1));
                        var hive = pts[0].Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
                                   ? Microsoft.Win32.Registry.LocalMachine
                                   : Microsoft.Win32.Registry.CurrentUser;
                        liveKey = hive.OpenSubKey(subK);
                    }
                    catch { }

                    var arpLines    = new List<string>();
                    var appArpLines = new List<string>();

                    if (doNoModify && liveKey?.GetValue("NoModify") == null)
                        appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoModify\" -Value \"1\" -Type \"DWord\"");
                    if (doNoRepair && liveKey?.GetValue("NoRepair") == null)
                        appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoRepair\" -Value \"1\" -Type \"DWord\"");
                    if (doNoRemove && liveKey?.GetValue("NoRemove") == null)
                        appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoRemove\" -Value \"1\" -Type \"DWord\"");

                    liveKey?.Dispose();

                    if (appArpLines.Count > 0)
                    {
                        arpLines.Add($"    # ARP flags - {appCommentBody}");
                        arpLines.AddRange(appArpLines);
                        arpLines.Add("");

                        bool arpAlready = ps1Text.Contains(litPath, StringComparison.OrdinalIgnoreCase);
                        if (!arpAlready)
                        {
                            ps1Text    = InjectLinesAfterMarker(ps1Text, POST_MARKER, arpLines);
                            ps1Changed = true;
                        }
                    }
                }

                // ── Uninstall snippet ─────────────────────────────────────────────
                if (reg != null)
                {
                    string rawUninstall = !string.IsNullOrWhiteSpace(reg.QuietUninstallString)
                        ? reg.QuietUninstallString : reg.UninstallString ?? "";

                    if (!string.IsNullOrWhiteSpace(rawUninstall) &&
                        !rawUninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                    {
                        string uExe = rawUninstall;
                        string uArg = "";

                        if (rawUninstall.StartsWith("\""))
                        {
                            int closeQuote = rawUninstall.IndexOf('"', 1);
                            if (closeQuote > 1)
                            {
                                uExe = rawUninstall[1..closeQuote];
                                uArg = rawUninstall[(closeQuote + 1)..].Trim();
                            }
                        }
                        else
                        {
                            int exeEnd = rawUninstall.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                            if (exeEnd >= 0)
                            {
                                uExe = rawUninstall[..(exeEnd + 4)];
                                uArg = rawUninstall[(exeEnd + 4)..].Trim();
                            }
                        }

                        // Priority: SilentSwitches.json uninstall (uninstSwitch, from
                        // MatchBuiltInSwitches) → argument parsed from the registry uninstall string
                        // → /S fallback. The curated JSON wins so a known app's uninstall matches its
                        // install (e.g. KeePass "/VERYSILENT /NORESTART") instead of whatever partial
                        // switch the ARP UninstallString happens to carry (e.g. "/SILENT").
                        string finalArg =
                            !string.IsNullOrWhiteSpace(uninstSwitch) ? uninstSwitch :
                            !string.IsNullOrWhiteSpace(uArg)         ? uArg :
                            "/S";
                        string uArgPart = string.IsNullOrWhiteSpace(finalArg)
                            ? "" : $" -ArgumentList '{finalArg.Trim()}'";
                        string psUPath  = ConvertToEnvSystemDrivePath(uExe);

                        var uninstLines = new List<string>
                        {
                            $"    # Uninstall {appCommentBody}",
                            $"    Start-ADTProcess -FilePath \"{psUPath}\"{uArgPart} -WindowStyle \"Hidden\"",
                            ""
                        };

                        bool uninstAlready = ps1Text.Contains(psUPath, StringComparison.OrdinalIgnoreCase);
                        if (!uninstAlready)
                        {
                            ps1Text    = InjectLinesAfterMarker(ps1Text, UNINSTALL_MARKER, uninstLines);
                            ps1Changed = true;
                        }
                    }
                }

                // ── Write + refresh editor ────────────────────────────────────────
                if (ps1Changed)
                {
                    RefreshEditorFromText(ps1Text, comment, dropLine);
                }


                // Append process name to AppProcessesToClose (only if config allows)
                var cfgClose2 = ReadCreatePackageConfigFresh() ?? _cfg;
                if (CreatePackageConfig.Flag(cfgClose2.AddCloseAppsCommandToPs1))
                {
                    string procNameExe = !string.IsNullOrWhiteSpace(reg?.MainExePath)
                        ? Path.GetFileNameWithoutExtension(reg.MainExePath)
                        : Path.GetFileNameWithoutExtension(adtPath);
                    AppendAppProcessesToClose(procNameExe);
                }
                return true; // handled - skip InsertShortcutLines
            }
            catch { return false; }
        }


        // ══════════════════════════════════════════════════════════════
        //  .inf - install pnputil + uninstall pnputil /delete-driver
        // ══════════════════════════════════════════════════════════════
        private bool ApplyInfSnippet(
            string fileName, string comment, string cmd,
            string adtPath, int dropLine)
        {
            if (_createdScriptPath == null || !File.Exists(_createdScriptPath))
                return false;
            try
            {
                // Always read from disk so multi-file drops accumulate correctly.
                // TxtScript.Text is only updated asynchronously via BeginInvoke and would
                // be stale for the 2nd+ file in a batch drop.
                string ps1Text = File.ReadAllText(_createdScriptPath, new UTF8Encoding(false));
                ps1Text = ps1Text.Replace("\r\n", "\n").Replace("\r", "\n");
                bool ps1Changed = false;

                const string INSTALL_MARKER   = "## <Perform Installation tasks here>";
                const string UNINSTALL_MARKER = "## <Perform Uninstallation tasks here>";

                // ── Install ──────────────────────────────────────────────────────
                if (!ps1Text.Contains(adtPath, StringComparison.OrdinalIgnoreCase))
                {
                    ps1Text    = InjectAtDropLine(ps1Text, dropLine, INSTALL_MARKER, comment, cmd);
                    ps1Changed = true;
                }

                // ── Uninstall - pnputil /delete-driver ───────────────────────────
                // Use the same adtPath so the driver path is consistent
                string uComment = $"    # Uninstall {fileName}";
                string uCmd     = $"    Start-ADTProcess -FilePath \"pnputil.exe\" -ArgumentList \"/delete-driver `\"{adtPath}`\" /uninstall /force\" -WindowStyle \"Hidden\"";

                bool uninstAlready = ps1Text.Contains(adtPath, StringComparison.OrdinalIgnoreCase)
                                     && ps1Text.Contains("/delete-driver", StringComparison.OrdinalIgnoreCase);
                if (!uninstAlready)
                {
                    var uninstLines = new List<string> { uComment, uCmd, "" };
                    ps1Text    = InjectLinesAfterMarker(ps1Text, UNINSTALL_MARKER, uninstLines);
                    ps1Changed = true;
                }

                if (ps1Changed)
                    RefreshEditorFromText(ps1Text, comment, dropLine);

                return true;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════
        //  .cer - install certutil -addstore + uninstall -delstore
        //  Uninstall only injected when cert is already in the store
        // ══════════════════════════════════════════════════════════════
        private bool ApplyCerSnippet(
            string certLabel, string certSerial,
            string comment, string cmd,
            string adtPath, int dropLine)
        {
            if (_createdScriptPath == null || !File.Exists(_createdScriptPath))
                return false;
            try
            {
                // Always read from disk so multi-file drops accumulate correctly.
                // TxtScript.Text is only updated asynchronously via BeginInvoke and would
                // be stale for the 2nd+ file in a batch drop.
                string ps1Text = File.ReadAllText(_createdScriptPath, new UTF8Encoding(false));
                ps1Text = ps1Text.Replace("\r\n", "\n").Replace("\r", "\n");
                bool ps1Changed = false;

                const string INSTALL_MARKER   = "## <Perform Installation tasks here>";
                const string UNINSTALL_MARKER = "## <Perform Uninstallation tasks here>";

                // ── Install ──────────────────────────────────────────────────────
                if (!ps1Text.Contains(adtPath, StringComparison.OrdinalIgnoreCase))
                {
                    ps1Text    = InjectAtDropLine(ps1Text, dropLine, INSTALL_MARKER, comment, cmd);
                    ps1Changed = true;
                }

                // ── Uninstall - only if cert is already installed in machine store ─
                // Check TrustedPublisher + Root + CA stores (certlm.msc = LocalMachine)
                string? installedSerial  = null;
                string? installedSubject = null;
                try
                {
                    var storesToSearch = new[]
                    {
                        ("TrustedPublisher", System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine),
                        ("Root",             System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine),
                        ("CA",               System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine),
                        ("TrustedPublisher", System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser),
                    };

                    foreach (var (storeName, loc) in storesToSearch)
                    {
                        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                            storeName, loc);
                        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);

                        // Match by serial number (most reliable) or by subject CN
                        foreach (var c in store.Certificates)
                        {
                            bool matchSerial  = !string.IsNullOrEmpty(certSerial)
                                && c.GetSerialNumberString().Equals(certSerial, StringComparison.OrdinalIgnoreCase);
                            bool matchSubject = !string.IsNullOrEmpty(certLabel)
                                && c.GetNameInfo(
                                    System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,
                                    false)?.Equals(certLabel, StringComparison.OrdinalIgnoreCase) == true;

                            if (matchSerial || matchSubject)
                            {
                                installedSerial  = c.GetSerialNumberString();
                                installedSubject = c.GetNameInfo(
                                    System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,
                                    false) ?? certLabel;
                                break;
                            }
                        }
                        if (installedSerial != null) break;
                    }
                }
                catch { }

                // Only add uninstall command if cert IS installed
                if (installedSerial != null)
                {
                    string uLabel   = installedSubject ?? certLabel;
                    string uComment = $"    # Uninstall {uLabel}";
                    string uCmd     = $"    Start-ADTProcess -FilePath \"certutil.exe\" -ArgumentList \"-delstore TrustedPublisher {installedSerial}\" -WindowStyle \"Hidden\"";

                    bool uninstAlready = ps1Text.Contains(installedSerial, StringComparison.OrdinalIgnoreCase);
                    if (!uninstAlready)
                    {
                        var uninstLines = new List<string> { uComment, uCmd, "" };
                        ps1Text    = InjectLinesAfterMarker(ps1Text, UNINSTALL_MARKER, uninstLines);
                        ps1Changed = true;
                    }
                }

                if (ps1Changed)
                    RefreshEditorFromText(ps1Text, comment, dropLine);

                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Shared editor-refresh logic used by ApplyInfSnippet and ApplyCerSnippet.
        /// Writes ps1Text to disk, then updates TxtScript and restores caret/scroll.
        /// </summary>

        /// <summary>
        /// Appends <paramref name="processName"/> to the AppProcessesToClose array
        /// in the live PS1 file (and TxtAppProcesses UI field) if not already present.
        /// Process name = filename without extension, lower-case, no path.
        /// </summary>
        private void AppendAppProcessesToClose(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;
            processName = Path.GetFileNameWithoutExtension(processName).Trim();
            if (string.IsNullOrWhiteSpace(processName)) return;

            // ── Update TxtAppProcesses UI field ──────────────────────────────
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (TxtAppProcesses != null)
                {
                    var existing = TxtAppProcesses.Text
                        .Split(',')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();
                    if (!existing.Any(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase)))
                    {
                        existing.Add(processName);
                        TxtAppProcesses.Text = string.Join(", ", existing);
                    }
                }
            }));

            // ── Patch the live PS1 file ───────────────────────────────────────
            if (_createdScriptPath == null || !File.Exists(_createdScriptPath)) return;
            try
            {
                string ps1 = File.ReadAllText(_createdScriptPath, new System.Text.UTF8Encoding(true));

                // Find existing AppProcessesToClose = @('a', 'b') line
                var m = System.Text.RegularExpressions.Regex.Match(
                    ps1,
                    @"(?m)^(?<indent>\s*)AppProcessesToClose\s*=\s*@\((?<vals>[^)]*)\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!m.Success) return;

                // Parse existing process names
                var procs = System.Text.RegularExpressions.Regex.Matches(
                    m.Groups["vals"].Value, @"'([^']+)'")
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Select(x => x.Groups[1].Value.Trim())
                    .ToList();

                // Skip if already present
                if (procs.Any(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase)))
                    return;

                procs.Add(processName);
                string newVal = m.Groups["indent"].Value +
                                "AppProcessesToClose = @('" +
                                string.Join("', '", procs) + "')";

                ps1 = ps1[..m.Index] + newVal + ps1[(m.Index + m.Length)..];
                File.WriteAllText(_createdScriptPath, ps1, new System.Text.UTF8Encoding(true));
            }
            catch { }
        }

        private void RefreshEditorFromText(string ps1Text, string comment, int dropLine)
        {
            File.WriteAllText(_createdScriptPath!, ps1Text, new UTF8Encoding(true));

            int targetScrollLine = dropLine;
            var ps1Lines = ps1Text.Split('\n');
            for (int li = 0; li < ps1Lines.Length; li++)
            {
                if (ps1Lines[li].Contains(comment.Trim(), StringComparison.OrdinalIgnoreCase))
                { targetScrollLine = li; break; }
            }
            int capturedLine = targetScrollLine;
            string scriptPath = _createdScriptPath!;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (TxtScript == null) return;
                // Always read from disk so the editor shows the FINAL accumulated state
                // after all files in a batch drop have been processed and written to disk.
                string latestText;
                try { latestText = File.ReadAllText(scriptPath, new UTF8Encoding(false)); }
                catch { return; }
                latestText = latestText.Replace("\r\n", "\n").Replace("\r", "\n");
                _flushingToScript = true;
                TxtScript.IsReadOnly = false;
                TxtScript.Text = latestText;
                TxtScript.IsReadOnly = true;
                _flushingToScript = false;
                UpdateLineNumbers();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                {
                    if (TxtScript == null) return;
                    int lineCount  = TxtScript.LineCount;
                    int targetLine = Math.Min(capturedLine, Math.Max(0, lineCount - 1));
                    int charIdx    = TxtScript.GetCharacterIndexFromLineIndex(targetLine);
                    TxtScript.CaretIndex    = charIdx;
                    _savedSelStartLine = targetLine;
                    _savedSelEndLine   = targetLine;
                    if (ScriptScrollViewer != null)
                    {
                        TxtScript.UpdateLayout();
                        Rect r = TxtScript.GetRectFromCharacterIndex(charIdx);
                        if (r != Rect.Empty)
                        {
                            Point pt      = TxtScript.TranslatePoint(new Point(0, r.Top), ScriptScrollViewer);
                            double target = ScriptScrollViewer.VerticalOffset + pt.Y
                                            - ScriptScrollViewer.ViewportHeight / 4.0;
                            ScriptScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
                        }
                    }
                    else { TxtScript.ScrollToLine(targetLine); }
                    HighlightCurrentLine();
                }));
            }));
        }


        // ══════════════════════════════════════════════════════════════
        //  .msi - install Start-ADTMsiProcess + uninstall by ProductCode
        // ══════════════════════════════════════════════════════════════
        /// <summary>
        /// Patches the live PS1 with:
        ///   Install : Start-ADTMsiProcess -Action 'Install' -FilePath "..." at dropLine
        ///   Uninstall: Start-ADTMsiProcess -Action 'Uninstall' -ProductCode '{...}'
        ///              after ## <Perform Uninstallation tasks here>
        /// Product code is taken from:
        ///   1. regInfo (installed - from uninstall registry hive)
        ///   2. pc read from the MSI file itself (not installed)
        /// </summary>
        private bool ApplyMsiSnippet(
            string appCommentBody, string comment, string cmd,
            string adtPath, int dropLine,
            string msiProductCode, CpInstalledInfo? regInfo)
        {
            if (_createdScriptPath == null || !File.Exists(_createdScriptPath))
                return false;
            try
            {
                // Flush the live editor to disk first so that any user edits (including
                // deletions) are on disk before we read it. Without this, a command deleted
                // in the editor but not yet saved would still appear on disk, causing the
                // "already exists" check to incorrectly skip re-insertion.
                if (_scriptDirty && TxtScript != null)
                {
                    try { File.WriteAllText(_createdScriptPath, TxtScript.Text, new UTF8Encoding(true)); }
                    catch { /* best-effort; continue with disk content */ }
                }
                string ps1Text = File.ReadAllText(_createdScriptPath, new UTF8Encoding(false));
                ps1Text = ps1Text.Replace("\r\n", "\n").Replace("\r", "\n");
                bool ps1Changed = false;

                const string INSTALL_MARKER   = "## <Perform Installation tasks here>";
                const string UNINSTALL_MARKER = "## <Perform Uninstallation tasks here>";

                // ── Install ──────────────────────────────────────────────────────
                if (!ps1Text.Contains(adtPath, StringComparison.OrdinalIgnoreCase))
                {
                    ps1Text    = InjectAtDropLine(ps1Text, dropLine, INSTALL_MARKER, comment, cmd);
                    ps1Changed = true;
                }

                // ── Resolve ProductCode ───────────────────────────────────────────
                // Priority: registry (installed) → MSI file property
                string productCode = "";

                if (regInfo != null && !string.IsNullOrEmpty(regInfo.RegKeyPath))
                {
                    // RegKeyPath = "HKEY_LOCAL_MACHINE\SOFTWARE\...\{GUID}"
                    // The last segment after the final backslash IS the product code
                    string rk = regInfo.RegKeyPath;
                    int lastSep = rk.LastIndexOf('\\');
                    if (lastSep >= 0)
                    {
                        string candidate = rk[(lastSep + 1)..].Trim();
                        if (candidate.StartsWith("{") && candidate.EndsWith("}"))
                            productCode = candidate;
                    }
                    // Fallback: try to parse it from UninstallString "MsiExec.exe /I{GUID}"
                    if (string.IsNullOrEmpty(productCode) && !string.IsNullOrEmpty(regInfo.UninstallString))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            regInfo.UninstallString,
                            @"\{[0-9A-Fa-f\-]+\}",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) productCode = m.Value;
                    }
                }

                // Not in registry - use what was read from the MSI file
                if (string.IsNullOrEmpty(productCode) && !string.IsNullOrEmpty(msiProductCode))
                {
                    string pc = msiProductCode.Trim();
                    if (!pc.StartsWith("{")) pc = "{" + pc;
                    if (!pc.EndsWith("}"))   pc += "}";
                    productCode = pc;
                }

                // ── Uninstall ─────────────────────────────────────────────────────
                if (!string.IsNullOrEmpty(productCode))
                {
                    string uComment = $"    # Uninstall {appCommentBody}";
                    string uCmd     = $"    Start-ADTMsiProcess -Action 'Uninstall' -ProductCode \"{productCode}\"";

                    bool uninstAlready = ps1Text.Contains(productCode, StringComparison.OrdinalIgnoreCase);
                    if (!uninstAlready)
                    {
                        var uninstLines = new List<string> { uComment, uCmd, "" };
                        ps1Text    = InjectLinesAfterMarker(ps1Text, UNINSTALL_MARKER, uninstLines);
                        ps1Changed = true;
                    }
                }

                if (ps1Changed)
                    RefreshEditorFromText(ps1Text, comment, dropLine);


                // Append process name to AppProcessesToClose (only if config allows)
                {
                    var cfgCloseM = ReadCreatePackageConfigFresh() ?? _cfg;
                    if (CreatePackageConfig.Flag(cfgCloseM.AddCloseAppsCommandToPs1)
                        && !string.IsNullOrWhiteSpace(regInfo?.MainExePath))
                        AppendAppProcessesToClose(Path.GetFileNameWithoutExtension(regInfo.MainExePath));
                }
                return true;
            }
            catch { return false; }
        }

        private int InsertFilesInstallLine(string filePath, int dropLine)
        {
            string ext      = Path.GetExtension(filePath).ToLowerInvariant();
            string fileName = Path.GetFileName(filePath);
            string fileBase = Path.GetFileNameWithoutExtension(filePath);

            // ── Resolve relative path under Files\ ───────────────────────────────
            string? filesFolder = GetPackageFilesFolder();
            string  relPath     = filesFolder != null
                ? Path.GetRelativePath(filesFolder, filePath)
                : fileName;
            // Guard: Path.GetRelativePath returns the full path when filesFolder is null
            // or when the file isn't actually under it - fall back to filename only.
            if (relPath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relPath))
                relPath = fileName;

            string adtPath = $"$($adtSession.DirFiles)\\{relPath}";

            // Read config flags - gate each file type on its Yes/No config value
            var cfg2 = ReadCreatePackageConfigFresh() ?? _cfg;
            bool cfgExe  = CreatePackageConfig.Flag(cfg2.AddExeCommandToPs1);
            bool cfgMsi  = CreatePackageConfig.Flag(cfg2.AddMsiCommandToPs1);
            bool cfgMst  = CreatePackageConfig.Flag(cfg2.AddMstCommandToPs1);
            bool cfgMsp  = CreatePackageConfig.Flag(cfg2.AddMspCommandToPs1);
            bool cfgIss  = CreatePackageConfig.Flag(cfg2.AddIssCommandToPs1);
            bool cfgCer  = CreatePackageConfig.Flag(cfg2.AddCerCommandToPs1);
            bool cfgInf  = CreatePackageConfig.Flag(cfg2.AddInfCommandToPs1);
            bool cfgReg  = CreatePackageConfig.Flag(cfg2.AddRegCommandToPs1);
            bool cfgBat  = CreatePackageConfig.Flag(cfg2.AddBatCommandToPs1);
            bool cfgPs1  = CreatePackageConfig.Flag(cfg2.AddPS1CommandToPs1);
            bool cfgClose= CreatePackageConfig.Flag(cfg2.AddCloseAppsCommandToPs1);

            string comment;
            string cmd;
            // Set to true by the .exe case when Step 5 writes everything to the PS1 file directly,
            // so the final InsertShortcutLines call is skipped (avoids overwrite by BeginInvoke).
            bool handledByStep5 = false;

            switch (ext)
            {
                // ── .exe ─────────────────────────────────────────────────────────
                case ".exe":
                {
                    if (!cfgExe) return dropLine;
                    // Step 1: FileVersionInfo
                    string fviPub = "", fviName = "", fviVer = "";
                    try
                    {
                        var vi  = System.Diagnostics.FileVersionInfo.GetVersionInfo(filePath);
                        fviPub  = vi.CompanyName?.Trim()    ?? "";
                        fviName = vi.ProductName?.Trim()    ?? "";
                        fviVer  = vi.ProductVersion?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviName)) fviName = vi.FileDescription?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviVer))  fviVer  = vi.FileVersion?.Trim()     ?? "";
                    }
                    catch { }

                    // Step 2: Registry lookup for installed app info
                    string hint = !string.IsNullOrWhiteSpace(fviName) ? fviName
                        : Regex.Replace(fileBase,
                            @"[-_\.\s]?(setup|install|installer|win\d+|x64|x86|\d[\d\.\-]*).*$",
                            "", RegexOptions.IgnoreCase).Trim();
                    if (hint.Length < 3) hint = fileBase;

                    var reg = LookupInstalledByExe(filePath, hint);

                    // Step 3: Build comment - registry → FVI → filename
                    string appComment = reg != null
                        ? BuildAppComment("", reg.Publisher, reg.DisplayName,
                              !string.IsNullOrWhiteSpace(reg.DisplayVersion) ? reg.DisplayVersion : fviVer)
                        : BuildAppComment("", fviPub, fviName, fviVer);
                    if (string.IsNullOrWhiteSpace(appComment))
                        appComment = "# " + fileName;
                    string appCommentBody = appComment.TrimStart('#', ' ').Trim();

                    // Step 4: Silent-install switch
                    string regPub  = reg?.Publisher   ?? fviPub;
                    string regName = reg?.DisplayName ?? fviName;
                    string combinedKey = $"{fileName} {regName} {regPub}".ToLowerInvariant();
                    var (instSwitch, uninstSwitch) = MatchBuiltInSwitches(combinedKey);
                    if (string.IsNullOrEmpty(instSwitch)) instSwitch = "/S";

                    comment = $"    # Install {appCommentBody}";
                    cmd     = $"    Start-ADTProcess -FilePath \"{adtPath}\" -ArgumentList '{instSwitch}' -WindowStyle \"Hidden\"";

                    // ── Step 5: Patch live PS1 (install + ARP flags + uninstall) ──
                    handledByStep5 = ApplyFilesExeSnippet(
                        appCommentBody, comment, cmd, adtPath, dropLine,
                        uninstSwitch, reg);
                    break;
                }

                // ── .msi ─────────────────────────────────────────────────────────
                case ".msi":
                {
                    // A .msi whose matching .mst is pasted in the same batch is already
                    // skipped at the top of ProcessDroppedFiles (the .mst handles both). A
                    // .mst that only exists on disk but was not pasted must NOT skip the
                    // .msi here, or pasting the .msi by itself would silently add nothing.
                    var (pc, mfr, pn, pv) = ReadMsiAllProperties(filePath);
                    CpInstalledInfo? regInfo = !string.IsNullOrWhiteSpace(pc)
                        ? LookupInstalledByProductCode(pc) : null;
                    string body = regInfo != null
                        ? BuildAppComment("", regInfo.Publisher, regInfo.DisplayName, regInfo.DisplayVersion)
                              .TrimStart('#', ' ').Trim()
                        : BuildAppComment(pc, mfr, pn, pv).TrimStart('#', ' ').Trim();
                    if (string.IsNullOrWhiteSpace(body)) body = fileName;
                    comment = $"    # Install {body}";
                    cmd     = $"    Start-ADTMsiProcess -Action 'Install' -FilePath \"{adtPath}\"";
                    // Inject uninstall into live PS1
                    handledByStep5 = ApplyMsiSnippet(body, comment, cmd, adtPath, dropLine, pc, regInfo);
                    break;
                }

                // ── .msp ─────────────────────────────────────────────────────────
                case ".msp":
                    if (!cfgMsp) return dropLine;
                    comment = $"    # Install {fileName}";
                    cmd     = $"    Start-ADTMsiProcess -Action 'Patch' -FilePath \"{adtPath}\"";
                    break;

                // ── .mst (needs same-name paired .msi e.g. setup.msi + setup.mst) ─
                case ".mst":
                {
                    if (!cfgMst) return dropLine;
                    // Rule: .msi and .mst MUST share the same base name.
                    // e.g. SharedView.mst requires SharedView.msi in the same folder.
                    string msiPath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", fileBase + ".msi");
                    if (!File.Exists(msiPath)) return dropLine; // no same-name .msi - skip silently
                    var (pc, mfr, pn, pv) = ReadMsiAllProperties(msiPath);
                    CpInstalledInfo? regInfo = !string.IsNullOrWhiteSpace(pc)
                        ? LookupInstalledByProductCode(pc) : null;
                    string body = regInfo != null
                        ? BuildAppComment("", regInfo.Publisher, regInfo.DisplayName, regInfo.DisplayVersion)
                              .TrimStart('#', ' ').Trim()
                        : BuildAppComment(pc, mfr, pn, pv).TrimStart('#', ' ').Trim();
                    if (string.IsNullOrWhiteSpace(body)) body = fileBase + ".msi";
                    // Build adtPath for the paired .msi (may be in a sub-folder)
                    string msiRel = filesFolder != null
                        ? Path.GetRelativePath(filesFolder, msiPath)
                        : Path.GetFileName(msiPath);
                    // Guard: GetRelativePath returns full path when not under filesFolder
                    if (msiRel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(msiRel))
                        msiRel = Path.GetFileName(msiPath);
                    string msiAdtPath = $"$($adtSession.DirFiles)\\{msiRel}";
                    // FilePath -> the .msi, Transforms -> the .mst, both as $($adtSession.DirFiles)\..
                    // portable paths (adtPath was built for the dropped .mst at the top of this method).
                    comment = $"    # Install {body}";
                    cmd     = $"    Start-ADTMsiProcess -Action 'Install' -FilePath \"{msiAdtPath}\" -Transforms \"{adtPath}\"";
                    // Inject install + uninstall into live PS1 (same ProductCode logic as .msi)
                    handledByStep5 = ApplyMsiSnippet(body, comment, cmd, msiAdtPath, dropLine, pc, regInfo);
                    break;
                }

                // ── .iss (needs paired .exe) ──────────────────────────────────────
                case ".iss":
                {
                    if (!cfgIss) return dropLine;
                    string exePath2 = Path.Combine(Path.GetDirectoryName(filePath) ?? "", fileBase + ".exe");
                    if (!File.Exists(exePath2)) return dropLine; // no matching .exe - skip
                    string fviPub2 = "", fviName2 = "", fviVer2 = "";
                    try
                    {
                        var vi2  = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath2);
                        fviPub2  = vi2.CompanyName?.Trim()    ?? "";
                        fviName2 = vi2.ProductName?.Trim()    ?? "";
                        fviVer2  = vi2.ProductVersion?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviName2)) fviName2 = vi2.FileDescription?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviVer2))  fviVer2  = vi2.FileVersion?.Trim()     ?? "";
                    }
                    catch { }
                    // Registry lookup via paired EXE - provides metadata + uninstall info
                    var regI2 = LookupInstalledByExe(exePath2,
                        !string.IsNullOrWhiteSpace(fviName2) ? fviName2 : fileBase);
                    string body2 = regI2 != null
                        ? BuildAppComment("", regI2.Publisher, regI2.DisplayName,
                              !string.IsNullOrWhiteSpace(regI2.DisplayVersion) ? regI2.DisplayVersion : fviVer2)
                        : BuildAppComment("", fviPub2, fviName2, fviVer2);
                    body2 = body2.TrimStart('#', ' ').Trim();
                    if (string.IsNullOrWhiteSpace(body2)) body2 = fileBase + ".exe";
                    string exeRel2 = filesFolder != null
                        ? Path.GetRelativePath(filesFolder, exePath2)
                        : Path.GetFileName(exePath2);
                    string exeAdtPath = $"$($adtSession.DirFiles)\\{exeRel2}";
                    comment = $"    # Install {body2}";
                    cmd     = $"    Start-ADTProcess -FilePath \"{exeAdtPath}\" -ArgumentList \"-s -f1`\"{adtPath}`\"\" -WindowStyle \"Hidden\"";
                    // Inject ARP flags + uninstall into live PS1 (same as .exe case)
                    // Takes the uninstall element: this value is passed as ApplyFilesExeSnippet's
                    // uninstSwitch and ends up on the Uninstall line. It previously read the
                    // install element, which was harmless only while MatchBuiltInSwitches was
                    // stubbed to return nothing - both were empty, so the caller fell back to "/S".
                    // With the switch table live, it would have written the install switch into the
                    // uninstall command. The equivalent call in the non-Files path already takes
                    // the second element.
                    var (_, issUninstSwitch) = MatchBuiltInSwitches(
                        $"{Path.GetFileName(exePath2)} {body2}".ToLowerInvariant());
                    handledByStep5 = ApplyFilesExeSnippet(
                        body2, comment, cmd, adtPath, dropLine,
                        issUninstSwitch, regI2);
                    break;
                }

                // ── .cer ─────────────────────────────────────────────────────────
                case ".cer":
                {
                    // Read cert metadata from the file
                    string certLabel = fileName;
                    string certSerial = "";
                    try
                    {
                        var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(filePath);
                        string sn = cert.GetNameInfo(
                            System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,
                            false)?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(sn)) certLabel = sn;
                        certSerial = cert.GetSerialNumberString()?.Trim() ?? "";
                    }
                    catch { }
                    comment = $"    # Install {certLabel}";
                    cmd     = $"    Start-ADTProcess -FilePath \"certutil.exe\" -ArgumentList \"-addstore TrustedPublisher `\"{adtPath}`\"\" -WindowStyle \"Hidden\"";
                    handledByStep5 = ApplyCerSnippet(
                        certLabel, certSerial, comment, cmd, adtPath, dropLine);
                    break;
                }

                // ── .inf ─────────────────────────────────────────────────────────
                case ".inf":
                    if (!cfgInf) return dropLine;
                    comment = $"    # Install {fileName}";
                    cmd     = $"    Start-ADTProcess -FilePath \"pnputil.exe\" -ArgumentList \"/add-driver `\"{adtPath}`\" /install /force\" -WindowStyle \"Hidden\" -IgnoreExitCodes '512'";
                    handledByStep5 = ApplyInfSnippet(fileName, comment, cmd, adtPath, dropLine);
                    break;

                // ── .reg ─────────────────────────────────────────────────────────
                case ".reg":
                    if (!cfgReg) return dropLine;
                    comment = $"    # Run {fileName}";
                    cmd     = $"    Start-ADTProcess -FilePath \"regedit.exe\" -ArgumentList \"/s `\"{adtPath}`\"\" -WindowStyle \"Hidden\"";
                    break;

                // ── .bat / .cmd ───────────────────────────────────────────────────
                case ".bat":
                case ".cmd":
                    if (!cfgBat) return dropLine;
                    comment = $"    # Run {fileName}";
                    cmd     = $"    Start-ADTProcess -FilePath \"{adtPath}\" -WindowStyle \"Hidden\"";
                    break;

                // ── .ps1 ─────────────────────────────────────────────────────────
                case ".ps1":
                    if (!cfgPs1) return dropLine;
                    comment = $"    # Run {fileName}";
                    cmd     = $"    Start-ADTProcess -FilePath \"$envSystemDrive\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -ArgumentList \"-ExecutionPolicy Bypass -NoProfile -File `\"{adtPath}`\"\" -WindowStyle \"Hidden\"";
                    break;

                // ── fallback ──────────────────────────────────────────────────────
                default:
                    comment = $"    # Install {fileName}";
                    cmd     = $"    Start-ADTProcess -FilePath \"{adtPath}\" -WindowStyle \"Hidden\"";
                    break;
            }

            // For .exe files with a live PS1, Step 5 wrote everything to the file and
            // will refresh the editor via BeginInvoke - skip the caret insertion to avoid
            // the async overwrite erasing the install line.
            if (handledByStep5) return dropLine;

            return InsertShortcutLines(comment, cmd, dropLine);
        }

        private int InsertGenericRemoveLine(string filePath, int dropLine)
        {
            string fileName = Path.GetFileName(filePath);
            string folder   = Path.GetDirectoryName(filePath) ?? "";

            // Reuse the same path classification as BuildShortcutLines so
            // user-profile paths get Remove-ADTFileFromUserProfiles and
            // known env-var folders get the correct $env* prefix.
            var (comment, cmd) = BuildFileRemoveLines(folder, fileName);
            return InsertShortcutLines(comment, cmd, dropLine);
        }

        /// <summary>
        /// Classifies a shortcut by its containing folder and returns the correct
        /// comment line and PowerShell command line pair.
        ///
        /// Cases:
        ///   1. All-users Public Desktop
        ///        C:\Users\Public\Desktop\name.lnk
        ///        → # Remove Desktop shortcut - name
        ///        → Remove-ADTFile -Path "$envPublic\Desktop\name.lnk"
        ///
        ///   2. All-users system path (ProgramData / Common*)
        ///        C:\ProgramData\Microsoft\Windows\Start Menu\Programs\...\name.lnk
        ///        → # Remove Start Menu shortcut - name
        ///        → Remove-ADTFile -Path "$envProgramData\Microsoft\Windows\Start Menu\Programs\...\name.lnk"
        ///        (drive letter replaced with $envSystemDrive)
        ///
        ///   3. Per-user profile - file directly in profile root
        ///        C:\Users\<user>\name.lnk
        ///        → # Remove User Profile shortcut - name
        ///        → Remove-ADTFileFromUserProfiles -Path "name.lnk"
        ///
        ///   4. Per-user profile - file in a sub-path under the profile
        ///        C:\Users\<user>\AppData\Roaming\...\name.lnk
        ///        → # Remove User Profile shortcut - name
        ///        → Remove-ADTFileFromUserProfiles -Path "AppData\Roaming\...\name.lnk"
        ///
        ///   5. Unknown - falls back to literal path with Remove-ADTFile
        /// </summary>
        private static (string comment, string command) BuildShortcutLines(string folder, string name)
        {
            string fileName = name + ".lnk";
            string norm     = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // ── Helper: case-insensitive StartsWith for paths ──────────────────
            bool PathStartsWith(string path, string prefix) =>
                path.StartsWith(prefix.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

            // ── Resolve well-known system root paths ───────────────────────────
            string publicDesktop    = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                                        .TrimEnd('\\', '/');
            string programData      = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
            programData             = programData.TrimEnd('\\', '/');
            string commonPrograms   = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                                        .TrimEnd('\\', '/');
            string commonStartMenu  = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                                        .TrimEnd('\\', '/');
            string usersRoot        = Path.GetDirectoryName(
                                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                                            .TrimEnd('\\', '/')) ?? @"C:\Users";

            // Drive letter of the system drive (e.g. "C:")
            string sysDrive = Path.GetPathRoot(Environment.GetFolderPath(
                                  Environment.SpecialFolder.System)) ?? "C:\\";
            sysDrive = sysDrive.TrimEnd('\\', '/'); // → "C:"

            // ── Case 1: All-users Public Desktop ──────────────────────────────
            if (norm.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    $"    # Remove Desktop shortcut - {name}",
                    $"    Remove-ADTFile -Path \"$envPublic\\Desktop\\{fileName}\""
                );
            }

            // ── Case 2: Any all-users system path (ProgramData / Common folders)
            //    Replace the drive letter with $envSystemDrive so the path is portable.
            if (PathStartsWith(norm, programData)     ||
                PathStartsWith(norm, commonPrograms)  ||
                PathStartsWith(norm, commonStartMenu))
            {
                // Build relative path from drive root: strip "C:" prefix → "\ProgramData\..."
                string relFromDrive = norm.Length > sysDrive.Length
                    ? norm.Substring(sysDrive.Length)   // keeps leading backslash
                    : "\\" + norm;
                // Append filename
                string fullRel = relFromDrive.TrimEnd('\\') + "\\" + fileName;

                // Choose comment label
                string label = (PathStartsWith(norm, commonPrograms) || PathStartsWith(norm, commonStartMenu))
                    ? "Start Menu shortcut"
                    : "shortcut";

                return (
                    $"    # Remove {label} - {name}",
                    $"    Remove-ADTFile -Path \"$envSystemDrive{fullRel}\""
                );
            }

            // ── Cases 3 & 4: Per-user profile path (C:\Users\<user>\...) ────────
            if (PathStartsWith(norm, usersRoot))
            {
                // Strip the users root + one component (the username) to get relative path
                // e.g. C:\Users\JohnDoe\AppData\... → after usersRoot = C:\Users → \JohnDoe\AppData\...
                string afterUsers = norm.Substring(usersRoot.Length).TrimStart('\\', '/');
                // afterUsers now = "JohnDoe\AppData\Roaming\..." or "JohnDoe" (profile root)

                // Strip username component
                int sep = afterUsers.IndexOf('\\');
                string relUnderProfile = sep >= 0
                    ? afterUsers.Substring(sep + 1)   // "AppData\Roaming\..."
                    : "";                              // file is at profile root

                string psRelPath = string.IsNullOrEmpty(relUnderProfile)
                    ? fileName                                           // "name.lnk"
                    : relUnderProfile.TrimEnd('\\') + "\\" + fileName;  // "AppData\...\name.lnk"

                return (
                    $"    # Remove User Profile shortcut - {name}",
                    $"    Remove-ADTFileFromUserProfiles -Path \"{psRelPath}\""
                );
            }

            // ── Case 5: Unknown / unmapped - use $envSystemDrive for portability ──
            // Replace the drive letter (e.g. "C:") with $envSystemDrive so the
            // generated script is not hardcoded to this machine's drive letter.
            string fullPath  = Path.Combine(folder, fileName);
            string pathRoot  = Path.GetPathRoot(fullPath) ?? "";
            string portable  = pathRoot.Length > 0
                ? "$envSystemDrive" + fullPath.Substring(pathRoot.Length - 1)
                : fullPath;
            return (
                $"    # Remove shortcut - {name}",
                $"    Remove-ADTFile -Path \"{portable}\""
            );
        }


        private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
        { if (sender is TextBox tb) { tb.CaretIndex = tb.Text.Length; tb.ScrollToEnd(); } }

        /// <summary>
        /// Fixes caret placement AND drag-selection when clicking in the empty area of any TextBox.
        /// Click in empty space before text → caret at beginning; drag right selects forward.
        /// Click in empty space after text  → caret at end; drag left selects backward.
        /// </summary>
        private void PathBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not TextBox tb || string.IsNullOrEmpty(tb.Text)) return;

            var clickPoint = e.GetPosition(tb);

            // snapToText: false → -1 if click is in empty space (not on a character)
            int exactIndex = tb.GetCharacterIndexFromPoint(clickPoint, snapToText: false);
            if (exactIndex != -1) return; // clicked on actual text - WPF handles normally

            // Click is in empty space - determine left or right side.
            // Use the X position of the last character's right edge as the boundary.
            // If the text is shorter than the TextBox, the last char rect right edge
            // tells us where text ends. Clicks past that point → go to end.
            var lastRect  = tb.GetRectFromCharacterIndex(tb.Text.Length);
            var firstRect = tb.GetRectFromCharacterIndex(0);

            // Determine anchor: end if click is to the right of text end, start otherwise
            int anchor = (clickPoint.X >= lastRect.Left) ? tb.Text.Length : 0;

            // Focus and place caret at anchor with no selection
            if (!tb.IsFocused) tb.Focus();
            tb.Select(anchor, 0);
            if (anchor == 0) tb.ScrollToHome(); else tb.ScrollToEnd();

            e.Handled = true;
            tb.CaptureMouse();

            MouseEventHandler?       moveHandler = null;
            MouseButtonEventHandler? upHandler   = null;

            moveHandler = (s, me) =>
            {
                if (s is not TextBox t) return;
                var pos = me.GetPosition(t);

                // Find character index under mouse during drag
                int dragIndex = t.GetCharacterIndexFromPoint(pos, snapToText: true);

                // If mouse moves past the last character to the right, snap to end
                var lastR = t.GetRectFromCharacterIndex(t.Text.Length);
                if (pos.X >= lastR.Left) dragIndex = t.Text.Length;

                // If mouse moves past the first character to the left, snap to 0
                var firstR = t.GetRectFromCharacterIndex(0);
                if (pos.X <= firstR.Left) dragIndex = 0;

                // Extend selection from anchor to current drag position
                int selStart  = Math.Min(anchor, dragIndex);
                int selLength = Math.Abs(dragIndex - anchor);
                t.Select(selStart, selLength);
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

        private void TemplatePath_TextChanged(object sender, TextChangedEventArgs e)
        {
            PathBox_TextChanged(sender, e);
            string path = TxtTemplatePath.Text.Trim();
            if (!Directory.Exists(path)) return;

            TxtScriptDate.Text = DateTime.Now.ToString("yyyy-MM-dd");

            var dispatcher = Dispatcher;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Read Info tab defaults from the script
                var v = ReadPsadtDefaults(path);

                // Find the ps1 script to load into the Script tab
                string? ps1 = Directory.GetFiles(path, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault();

                dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyPsadtDefaults(v);

                    // Load script into Script tab editor
                    if (ps1 != null && File.Exists(ps1))
                    {
                        _createdScriptPath = ps1;
                        LoadScriptIntoEditor(ps1);

                        // In modify mode, browsing to an existing package folder unlocks the
                        // Script tab so the user can edit the existing package's script.
                        if (_modifyMode)
                        {
                            SetScriptTabEnabled(true);
                            if (BtnInfoSave != null) BtnInfoSave.IsEnabled = true;
                        }
                    }
                }));
            });
        }

        // ══════════════════════════════════════════════════════════════
        // READ / APPLY PS1 DEFAULTS
        // ══════════════════════════════════════════════════════════════
        private static Dictionary<string, string> ReadPsadtDefaults(string folder)
        {
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string? ps1 = Directory.GetFiles(folder, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault();
                if (ps1 == null) return r;
                string txt = File.ReadAllText(ps1);
                foreach (string k in new[] { "AppVendor","AppName","AppVersion","AppArch","AppLang","AppRevision","AppScriptVersion","InstallName","InstallTitle" })
                { var m = Regex.Match(txt, @"(?m)^\s+" + Regex.Escape(k) + @"\s*=\s*'([^']*)'", RegexOptions.IgnoreCase); if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value)) r[k] = m.Groups[1].Value.Trim(); }
                foreach (string k in new[] { "AppSuccessExitCodes","AppRebootExitCodes" })
                { var m = Regex.Match(txt, @"(?m)^\s+" + Regex.Escape(k) + @"\s*=\s*@\(([^)]*)\)", RegexOptions.IgnoreCase); if (m.Success) r[k] = string.Join(", ", Regex.Matches(m.Groups[1].Value, @"\d+").Cast<Match>().Select(x => x.Value)); }
                { var m = Regex.Match(txt, @"(?m)^\s+AppProcessesToClose\s*=\s*@\(([^)]*)\)", RegexOptions.IgnoreCase); if (m.Success) r["AppProcessesToClose"] = string.Join(", ", Regex.Matches(m.Groups[1].Value, @"'([^']+)'").Cast<Match>().Select(x => x.Groups[1].Value)); }
                { var m = Regex.Match(txt, @"(?m)^\s+RequireAdmin\s*=\s*(\$\w+)", RegexOptions.IgnoreCase); if (m.Success) r["RequireAdmin"] = m.Groups[1].Value.Trim(); }
            }
            catch { }
            return r;
        }

        private void ApplyPsadtDefaults(Dictionary<string, string> v)
        {
            if (v.TryGetValue("AppVendor",       out var s) && !string.IsNullOrWhiteSpace(s)) TxtAppVendor.Text     = s;
            if (v.TryGetValue("AppName",          out s)    && !string.IsNullOrWhiteSpace(s)) TxtAppName.Text       = s;
            if (v.TryGetValue("AppVersion",       out s)    && !string.IsNullOrWhiteSpace(s)) TxtAppVersion.Text    = s;
            if (v.TryGetValue("InstallName",      out s)    && !string.IsNullOrWhiteSpace(s)) TxtInstallName.Text   = s;
            if (v.TryGetValue("InstallTitle",     out s)    && !string.IsNullOrWhiteSpace(s)) TxtInstallTitle.Text  = s;
            if (v.TryGetValue("AppArch",          out s))   TxtAppArch.Text       = s;
            if (v.TryGetValue("AppLang",          out s))   TxtAppLang.Text       = s;
            if (v.TryGetValue("AppRevision",      out s))   TxtAppRevision.Text   = s;
            if (v.TryGetValue("AppScriptVersion", out s))   TxtScriptVersion.Text = s;
            if (v.TryGetValue("AppSuccessExitCodes", out s)) TxtSuccessExitCodes.Text = s;
            if (v.TryGetValue("AppRebootExitCodes",  out s)) TxtRebootExitCodes.Text  = s;
            if (v.TryGetValue("AppProcessesToClose", out s)) TxtAppProcesses.Text     = s;
            if (v.TryGetValue("RequireAdmin",        out s)) TxtRequireAdmin.Text      = s;
        }

        private void FetchAndApplyFileInfo(string filePath)
        {
            var d = Dispatcher;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var (ven, nam, ver) = ReadInstallerInfo(filePath);
                string closeApps = "";

                // Registry enrichment - same logic as AutoPackage
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".msi")
                {
                    // Single COM session reads ProductCode + MSI properties together
                    var (productCode, msiMfr, msiProd, msiVer) = ReadMsiAllProperties(filePath);
                    CpInstalledInfo? reg = null;
                    if (!string.IsNullOrWhiteSpace(productCode))
                        reg = LookupInstalledByProductCode(productCode);
                    if (reg != null)
                    {
                        // App is installed - registry values take priority
                        if (!string.IsNullOrWhiteSpace(reg.Publisher))      ven = reg.Publisher;
                        if (!string.IsNullOrWhiteSpace(reg.DisplayName))    nam = reg.DisplayName;
                        if (!string.IsNullOrWhiteSpace(reg.DisplayVersion)) ver = reg.DisplayVersion;
                        closeApps = DeriveCloseApps(reg);
                    }
                    else
                    {
                        // App not installed - fall back to MSI Property table values
                        if (!string.IsNullOrWhiteSpace(msiMfr))  ven = msiMfr;
                        if (!string.IsNullOrWhiteSpace(msiProd)) nam = msiProd;
                        if (!string.IsNullOrWhiteSpace(msiVer))  ver = msiVer;
                    }
                }
                else if (ext == ".exe" && File.Exists(filePath))
                {
                    // Mirror AutoPackage: use filename as hint when ProductName is empty
                    // (e.g. NSIS stubs like vlc-3.0.23-win64.exe have no version resource)
                    string exeHint = !string.IsNullOrWhiteSpace(nam)
                        ? nam
                        : Path.GetFileNameWithoutExtension(filePath);
                    var reg = LookupInstalledByExe(filePath, exeHint);
                    if (reg != null)
                    {
                        if (!string.IsNullOrWhiteSpace(reg.Publisher))      ven = reg.Publisher;
                        if (!string.IsNullOrWhiteSpace(reg.DisplayName))    nam = reg.DisplayName;
                        // Use DisplayVersion from registry only; if absent, leave AppVersion blank
                        if (!string.IsNullOrWhiteSpace(reg.DisplayVersion)) ver = reg.DisplayVersion;
                        else ver = "";
                        closeApps = DeriveCloseApps(reg);
                    }
                    // Last-resort: if still no name, use filename (mirrors AutoPackage)
                    if (string.IsNullOrWhiteSpace(nam))
                        nam = Path.GetFileNameWithoutExtension(filePath);
                }

                d.BeginInvoke(new Action(() =>
                {
                    TxtAppVendor.Text    = ven;
                    TxtAppName.Text      = nam;
                    TxtAppVersion.Text   = ver;
                    TxtAppProcesses.Text = closeApps; // always set - clears stale value if empty
                    // Always reset AppRevision to 01 when a new source file is loaded.
                    // The previous package's revision (e.g. 02) must not carry over.
                    TxtAppRevision.Text  = "01";
                }));
            });
        }

        // ── Registry record (mirrors AutoPackage) ──────────────────────────
        private record CpInstalledInfo(
            string DisplayName, string Publisher, string DisplayVersion,
            string InstallLocation, string MainExePath, string MainExeVersion,
            string UninstallString, string QuietUninstallString,
            string RegKeyPath, bool Is32Bit);

        private static CpInstalledInfo? LookupInstalledByExe(string exePath, string productNameHint)
        {
            var hives = new (Microsoft.Win32.RegistryKey root, string sub)[]
            {
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            CpInstalledInfo? bestMatch = null;
            int bestScore = 0;

            // Pre-compute normalised tokens from the EXE filename (strip version/setup suffixes)
            string exeBaseName = Path.GetFileNameWithoutExtension(exePath);
            string exeStripped = System.Text.RegularExpressions.Regex.Replace(
                exeBaseName,
                @"[-_.\s]?(setup|install|installer|win\d+|x64|x86|\d[\d.\-]*).*$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            string exeNorm = System.Text.RegularExpressions.Regex.Replace(exeStripped, @"[\s\-_.]", "").ToLowerInvariant();

            // Pre-compute significant words from productNameHint (words with 3+ chars, strip trailing version/setup words).
            // Also splits on camelCase boundaries so "DuplicateCleaner" -> ["duplicate","cleaner"].
            static string[] SigWords(string s)
            {
                // Insert space before each uppercase letter that follows a lowercase letter or digit
                // so camelCase/PascalCase tokens split: "DuplicateCleaner" -> "Duplicate Cleaner"
                string spaced = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[a-z\d])(?=[A-Z])", " ");
                return spaced.Split(new[]{' ','-','_'}, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= 3
                             && !System.Text.RegularExpressions.Regex.IsMatch(w, @"^\d[\d.]*$")
                             && !new[]{"setup","install","installer","free","trial","version","portable"}.Contains(w.ToLowerInvariant()))
                    .Select(w => w.ToLowerInvariant())
                    .ToArray();
            }

            string[] hintWords = SigWords(productNameHint);

            foreach (var (root, subPath) in hives)
            {
                try
                {
                    using var hive = root.OpenSubKey(subPath);
                    if (hive == null) continue;

                    foreach (string keyName in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = hive.OpenSubKey(keyName);
                            if (key == null) continue;

                            string dispName = key.GetValue("DisplayName") as string ?? "";
                            if (string.IsNullOrWhiteSpace(dispName)) continue;

                            int score = 0;
                            string dispIcon = (key.GetValue("DisplayIcon") as string ?? "").Split(',')[0].Trim().Trim('"');

                            // Score 10: DisplayIcon path exactly matches the browsed EXE
                            if (!string.IsNullOrWhiteSpace(exePath) && !string.IsNullOrWhiteSpace(dispIcon))
                            {
                                if (string.Equals(dispIcon, exePath, StringComparison.OrdinalIgnoreCase))
                                    score = 10;
                                else if (string.Equals(Path.GetDirectoryName(dispIcon),
                                             Path.GetDirectoryName(exePath),
                                             StringComparison.OrdinalIgnoreCase))
                                    score = 6;
                            }

                            // Score 8: exact DisplayName == productNameHint
                            // Score 5: significant word overlap between productNameHint and DisplayName
                            //          e.g. "Duplicate Cleaner Setup" vs "Duplicate Cleaner 1.2" → 2 shared words
                            // Score 4: one name contains the other (original logic)
                            if (score == 0 && !string.IsNullOrWhiteSpace(productNameHint))
                            {
                                if (string.Equals(dispName, productNameHint, StringComparison.OrdinalIgnoreCase))
                                    score = 8;
                                else if (dispName.Contains(productNameHint, StringComparison.OrdinalIgnoreCase) ||
                                         productNameHint.Contains(dispName, StringComparison.OrdinalIgnoreCase))
                                    score = 4;
                                else if (hintWords.Length >= 2)
                                {
                                    // Count how many significant hint words appear in the dispName
                                    string dispNameLow = dispName.ToLowerInvariant();
                                    int shared = hintWords.Count(w => dispNameLow.Contains(w));
                                    // Require at least 2 shared words AND shared ≥ half the hint words
                                    if (shared >= 2 && shared >= hintWords.Length / 2)
                                        score = 5;
                                }
                                else if (hintWords.Length == 1 && hintWords[0].Length >= 2)
                                {
                                    // Single-word hint (e.g. "vlc", "zoom") - match if dispName starts with or contains it
                                    string dispNameLow = dispName.ToLowerInvariant();
                                    if (dispNameLow.StartsWith(hintWords[0]) || dispNameLow.Contains(" " + hintWords[0]))
                                        score = 4;
                                }
                            }

                            // Score 4: EXE filename (normalised) matches DisplayName
                            // Requires >=3 chars. For short names (3 chars, e.g. "vlc") uses
                            // word-boundary regex to avoid false hits like "eville".
                            if (score == 0 && exeNorm.Length >= 3)
                            {
                                string dispNameNorm  = System.Text.RegularExpressions.Regex.Replace(dispName, @"[\s\-_.]", "").ToLowerInvariant();
                                string dispFirstNorm = System.Text.RegularExpressions.Regex.Replace(
                                    dispName.Split(' ')[0], @"[\s\-_.]", "").ToLowerInvariant();

                                bool nameMatch = false;
                                if (exeNorm.Length >= 4)
                                {
                                    nameMatch = dispFirstNorm.Length >= 3 &&
                                        (dispNameNorm.Contains(exeNorm, StringComparison.OrdinalIgnoreCase) ||
                                         exeNorm.Contains(dispFirstNorm, StringComparison.OrdinalIgnoreCase));
                                }
                                else
                                {
                                    // Short name (3 chars): require whole-word match in DisplayName
                                    string dispNameLower = dispName.ToLowerInvariant();
                                    nameMatch = System.Text.RegularExpressions.Regex.IsMatch(
                                        dispNameLower,
                                        @"(^|[\s\-_(])" + System.Text.RegularExpressions.Regex.Escape(exeNorm) + @"($|[\s\-_)])",
                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                }
                                if (nameMatch) score = 4;
                            }

                            if (score <= bestScore) continue;

                            string dispVer    = key.GetValue("DisplayVersion")      as string ?? "";
                            string publisher  = key.GetValue("Publisher")            as string ?? "";
                            string instLoc    = (key.GetValue("InstallLocation")    as string ?? "").TrimEnd(Path.DirectorySeparatorChar);
                            string uninstStr  = key.GetValue("UninstallString")      as string ?? "";
                            string quietUninst= key.GetValue("QuietUninstallString") as string ?? "";

                            if (string.IsNullOrWhiteSpace(instLoc) && dispIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                instLoc = (Path.GetDirectoryName(dispIcon) ?? "").TrimEnd(Path.DirectorySeparatorChar);

                            // Derive UninstallString install location as last resort
                            if (string.IsNullOrWhiteSpace(instLoc) && !string.IsNullOrWhiteSpace(uninstStr))
                            {
                                string uPath = uninstStr.Trim().Trim('"');
                                if (uPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(uPath))
                                    instLoc = (Path.GetDirectoryName(uPath) ?? "").TrimEnd(Path.DirectorySeparatorChar);
                            }

                            // Find the main application EXE from InstallLocation:
                            // Prefer the EXE whose name best matches DisplayName significant words,
                            // then fall back to the largest EXE (excludes uninstall helpers like unins*.exe).
                            string mainExePath = "";
                            if (!string.IsNullOrWhiteSpace(dispIcon) && dispIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                // Use dispIcon for process-name derivation even if not on disk
                                mainExePath = dispIcon;
                            }
                            else if (!string.IsNullOrWhiteSpace(instLoc) && Directory.Exists(instLoc))
                            {
                                string[] dispWords = SigWords(dispName);
                                var exes = Directory.GetFiles(instLoc, "*.exe", SearchOption.TopDirectoryOnly)
                                    .Where(f => !Path.GetFileNameWithoutExtension(f)
                                        .StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                                    .ToArray();

                                if (exes.Length > 0)
                                {
                                    // Score each EXE by how many DisplayName significant words appear in its filename
                                    mainExePath = exes
                                        .OrderByDescending(f =>
                                        {
                                            string fn = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                                            return dispWords.Count(w => fn.Contains(w));
                                        })
                                        .ThenByDescending(f => new FileInfo(f).Length)
                                        .First();
                                }
                            }
                            if (string.IsNullOrWhiteSpace(mainExePath)) mainExePath = exePath;
                            // Last resort: scan Start Menu shortcuts for an exe matching the app name
                            if (string.IsNullOrWhiteSpace(mainExePath) || !File.Exists(mainExePath))
                            {
                                string shortcutExe = ResolveMainExeFromShortcuts(dispName);
                                if (!string.IsNullOrWhiteSpace(shortcutExe)) mainExePath = shortcutExe;
                            }

                            // Read version from the main EXE if DisplayVersion is empty
                            string mainExeVer = "";
                            if (!string.IsNullOrWhiteSpace(mainExePath) && File.Exists(mainExePath))
                            {
                                try
                                {
                                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(mainExePath);
                                    mainExeVer = (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                                        ? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}"
                                        : fvi.FileVersion?.Trim() ?? "";
                                }
                                catch { }
                            }

                            string hiveRoot   = root == Microsoft.Win32.Registry.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                            bestScore  = score;
                            bestMatch  = new CpInstalledInfo(dispName, publisher, dispVer, instLoc,
                                             mainExePath, mainExeVer, uninstStr, quietUninst,
                                             hiveRoot + "\\" + subPath + "\\" + keyName, false);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return bestScore >= 4 ? bestMatch : null;
        }

        // ── Resolve main exe from Start Menu / Desktop shortcuts ────────────
        // Copied from AutoPackagePage - scans .lnk files whose name matches the app DisplayName.
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
                    .Split(new[]{' ','-','_'}, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= 3 &&
                        !new[]{"the","and","for","com","inc","ltd","llc","s.l.","s.a.","microsoft"}.Contains(w.ToLowerInvariant()))
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

                foreach (var folder in folders.Where(f => Directory.Exists(f)))
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
                            Marshal.ReleaseComObject(sc);

                            if (string.IsNullOrWhiteSpace(target)) continue;
                            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!File.Exists(target)) continue;

                            string exeBaseName = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
                            if (browserExes.Contains(exeBaseName)) continue;
                            if (sigWords.Any(w => exeBaseName.Contains(w))) matchScore += 5;

                            candidates.Add((matchScore, target));
                        }
                        catch { }
                    }
                }

                if (shell != null) try { Marshal.ReleaseComObject(shell); } catch { }
                if (candidates.Count > 0)
                    return candidates.OrderByDescending(c => c.score).First().path;
            }
            catch { }
            return "";
        }

                private static string DeriveCloseApps(CpInstalledInfo? info)
        {
            if (info == null) return "";

            // Priority 1: MainExePath - use filename even if file not on disk (path from DisplayIcon)
            if (!string.IsNullOrWhiteSpace(info.MainExePath) &&
                info.MainExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(info.MainExePath);

            // Priority 2: Scan InstallLocation for largest non-uninstaller exe (mirrors AutoPackage)
            if (!string.IsNullOrWhiteSpace(info.InstallLocation) &&
                Directory.Exists(info.InstallLocation))
            {
                try
                {
                    var best = Directory.GetFiles(info.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(f => !Path.GetFileNameWithoutExtension(f)
                            .StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();
                    if (best != null) return Path.GetFileNameWithoutExtension(best);
                }
                catch { }
            }

            return "";
        }

        // ── Download Silent Switches ─────────────────────────────────────────────────────────
        //
        // Builds Templates\SilentSwitches.json on THIS machine from the winget community manifests,
        // rather than the product shipping a compiled copy of somebody else's dataset.
        //
        // The switches themselves are facts - /S is NSIS, /qn is Microsoft's documented MSI
        // parameter - but a 7,000-row compilation of them is a different thing from a fact, and the
        // cleanest answer to that is not to redistribute one: the tool reads the same public source
        // winget itself reads, on the user's own machine, when the user asks it to.
        //
        // Cost is one manifest per package, roughly 14,000 of them. raw.githubusercontent.com is a
        // CDN with no API quota (unlike api.github.com at 60 requests an hour), which is what makes
        // a sweep this size possible at all.

        private static readonly System.Net.Http.HttpClient _swHttp = CreateSwitchHttpClient();

        // Both generated files open with a "//" header explaining where the data came from and
        // what may be edited. JSON has no comments, so every read of these two files goes
        // through these options; without Skip, the first regenerated file would fail to parse.
        // AllowTrailingCommas is for the same audience: SilentSwitches.json is expected to be
        // corrected by hand, and a stray trailing comma should not cost anyone their table.
        private static readonly JsonSerializerOptions _swReadOptions = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        private static readonly JsonDocumentOptions _swDocOptions = new JsonDocumentOptions
        {
            CommentHandling     = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static System.Net.Http.HttpClient CreateSwitchHttpClient()
        {
            var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // GitHub raw rejects requests with no user agent.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SwiftPackStudio");
            return c;
        }

        /// <summary>How many manifests are fetched at once. Twelve keeps a sweep brisk without
        /// looking like an attack to the CDN.</summary>
        private const int SwitchFetchParallel = 12;

        /// <summary>
        /// Whether an uninstall switch may be filled in from the installer FRAMEWORK.
        ///
        /// Winget's manifest schema has no uninstall switch at all - Silent, SilentWithProgress,
        /// Interactive and Custom are all about installing - so taken literally this column would be
        /// empty for every entry. What is filled in instead is not a guess: NSIS "/S" and Inno
        /// "/VERYSILENT /NORESTART" are symmetric by those frameworks' own design, which is where the
        /// values in the existing file come from. Anything else is still left blank.
        /// Set to false for a strictly empty column.
        /// </summary>
        private const bool SwitchInferUninstall = true;

        /// <summary>One app as the file stores it. Property names ARE the JSON names, verbatim.</summary>
        private sealed class SwitchFileEntry
        {
            public string ApplicationName { get; set; } = "";
            public string install { get; set; } = "";
            public string uninstall { get; set; } = "";
        }

        /// <summary>
        /// Package ids already read and found to have NO silent switch - a zip, a portable exe, an
        /// MSIX. Without this a second run re-reads all ~6,800 of them to learn nothing, which is
        /// most of the cost of a sweep and the reason a re-run looked like a full download.
        ///
        /// Derived state, not something to edit: delete it to force every package to be read again.
        /// </summary>
        private static string SilentSwitchesSkipPath() =>
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(SilentSwitchesWritePath()) ?? "",
                "SilentSwitches.skip.json");

        private static HashSet<string> LoadSwitchSkipList()
        {
            try
            {
                string path = SilentSwitchesSkipPath();
                if (File.Exists(path))
                {
                    var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), _swReadOptions);
                    return new HashSet<string>(ids ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[SilentSwitches] skip list: " + exSwallow.Message);
            }
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveSwitchSkipList(HashSet<string> ids)
        {
            try
            {
                string path = SilentSwitchesSkipPath();
                string? dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                string nl = Environment.NewLine;
                string header = string.Join(nl, new[]
                {
                    "// SilentSwitches.skip.json - bookkeeping for the \"Download Silent Switches\" button",
                    "// on the Create Package page (the table itself is SilentSwitches.json in this folder).",
                    "//",
                    "// These are winget package ids whose manifests were read from the winget community",
                    "// repository (https://raw.githubusercontent.com/microsoft/winget-pkgs) and found to",
                    "// have NO silent install switch - zips, portable exes, MSIX packages and the like.",
                    "// They are listed here so the next run can skip re-reading thousands of manifests",
                    "// that yield nothing.",
                    "//",
                    "// Derived state, not a file to edit: nothing is packaged from it, and deleting it",
                    "// only makes the next run read every package again.",
                }) + nl;
                File.WriteAllText(path, header + JsonSerializer.Serialize(
                    ids.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[SilentSwitches] skip list save: " + exSwallow.Message);
            }
        }

        /// <summary>Every "vendor|app" already in the file, so those packages are not read again.</summary>
        private static HashSet<string> LoadCoveredKeys()
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string existing = GetSilentSwitchesPath();
                if (!File.Exists(existing)) return covered;
                var d = JsonSerializer.Deserialize<Dictionary<string, List<SwitchFileEntry>>>(File.ReadAllText(existing), _swReadOptions);
                foreach (var kv in d ?? new Dictionary<string, List<SwitchFileEntry>>())
                    foreach (var e in kv.Value ?? new List<SwitchFileEntry>())
                        covered.Add(kv.Key + "|" + e.ApplicationName);
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[SilentSwitches] covered keys: " + exSwallow.Message);
            }
            return covered;
        }

        /// <summary>Where a rebuilt table is written: the same Templates copy the reader prefers,
        /// falling back to the Resources copy beside the exe.</summary>
        private static string SilentSwitchesWritePath()
        {
            const string templates = @"C:\Program Files\Smekal\SwiftPack Studio\Templates\SilentSwitches.json";
            string dir = System.IO.Path.GetDirectoryName(templates) ?? "";
            if (Directory.Exists(dir)) return templates;
            return System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                "Resources", "SilentSwitches.json");
        }

        /// <summary>
        /// One plain YAML scalar, cleaned. Taking the rest of the line is not enough: in YAML an
        /// unquoted "#" preceded by whitespace, or starting the value, begins a COMMENT. Manifests
        /// use that, and the raw line therefore produced switches like
        ///
        ///     Silent: /S # /Silent          ->  "/S # /Silent"     (should be "/S")
        ///     Silent: ##silence=1           ->  "##silence=1"      (the value is empty)
        ///     Silent: '#null'               ->  "#null"            (winget's explicit no-value)
        ///
        /// Inside quotes a "#" is part of the value, so "/S /v#q" survives intact.
        /// </summary>
        private static string CleanYamlScalar(string raw)
        {
            string v = (raw ?? "").Trim();
            if (v.Length == 0) return "";

            bool quoted = v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0];
            if (quoted)
            {
                v = v.Substring(1, v.Length - 2).Trim();
            }
            else
            {
                if (v[0] == '#') return "";              // the whole value is a comment
                int hash = v.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) v = v.Substring(0, hash).Trim();
            }

            // Sentinels meaning "there is deliberately no value here".
            string low = v.ToLowerInvariant();
            return (low == "#null" || low == "null" || low == "~" || low == "none") ? "" : v;
        }

        /// <summary>
        /// Install and uninstall switches out of one installer manifest, or blanks when the package
        /// has none - a zip, a portable exe or an MSIX, which cannot be installed silently because
        /// they are not installed at all.
        ///
        /// MSI and WiX packages are excluded as well, for the opposite reason: their switch is /qn
        /// for every MSI ever made, manual packaging already defaults to exactly that, and several
        /// thousand rows repeating it would bury the EXE switches this table exists to carry.
        ///
        /// Deliberately narrow: it reads InstallerType and Silent and nothing else. A manifest can
        /// list InstallModes before InstallerSwitches, so this takes the first Silent value found
        /// anywhere rather than assuming it sits in a particular block.
        /// </summary>
        private static (string install, string uninstall) SwitchesFromManifest(string yaml)
        {
            string type = "", silent = "";
            foreach (string raw in yaml.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (type.Length == 0)
                {
                    var mt = Regex.Match(line, @"^\s*-?\s*InstallerType:\s*(\S+)", RegexOptions.IgnoreCase);
                    if (mt.Success) type = mt.Groups[1].Value.Trim().ToLowerInvariant();
                }
                if (silent.Length == 0)
                {
                    var ms = Regex.Match(line, @"^\s*-?\s*Silent(?:WithProgress)?:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                    if (ms.Success) silent = CleanYamlScalar(ms.Groups[1].Value);
                }
                if (type.Length > 0 && silent.Length > 0) break;
            }

            // Excluded outright, whatever the manifest says: /qn is not information anyone needs
            // looked up. Burn stays - it is a WiX BOOTSTRAPPER, an .exe with its own switch.
            if (type == "msi" || type == "wix") return ("", "");

            // Framework defaults, used when the manifest states no Silent switch of its own.
            (string i, string u) = type switch
            {
                "nullsoft" => ("/S",                     "/S"),
                "inno"     => ("/VERYSILENT /NORESTART", "/VERYSILENT /NORESTART"),
                "burn"     => ("/quiet /norestart",      ""),
                _          => ("",                       ""),
            };

            string install = silent.Length > 0 ? silent : i;
            if (install.Length == 0) return ("", "");   // zip / portable / msix, or a manifest we cannot read

            string uninstall = SwitchInferUninstall ? u : "";
            return (install, uninstall);
        }

        /// <summary>
        /// ADD-ONLY merge, then write. An app already in the file is left exactly as it is and
        /// nothing is ever removed, so hand corrections survive every rebuild - which is the whole
        /// point of the file being editable. The cost is that a switch corrected upstream will not
        /// reach an entry that already exists.
        /// Returns (apps added, path written).
        /// </summary>
        private static (int added, string path) MergeSilentSwitchesFile(
            List<(string Vendor, string App, string Install, string Uninstall)> found)
        {
            var table = new Dictionary<string, List<SwitchFileEntry>>(StringComparer.OrdinalIgnoreCase);

            // Read what is already there, through the same path the reader uses.
            try
            {
                string existing = GetSilentSwitchesPath();
                if (File.Exists(existing))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, List<SwitchFileEntry>>>(
                        File.ReadAllText(existing), _swReadOptions);
                    foreach (var kv in d ?? new Dictionary<string, List<SwitchFileEntry>>())
                        table[kv.Key] = kv.Value ?? new List<SwitchFileEntry>();
                }
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[SilentSwitches] read for merge: " + exSwallow.Message);
            }

            int added = 0;
            foreach (var f in found)
            {
                string vendor = (f.Vendor ?? "").Trim(), app = (f.App ?? "").Trim();
                string inst   = (f.Install ?? "").Trim();
                if (vendor.Length == 0 || app.Length == 0 || inst.Length == 0) continue;

                if (!table.TryGetValue(vendor, out var list)) { list = new List<SwitchFileEntry>(); table[vendor] = list; }
                if (list.Any(x => string.Equals(x.ApplicationName, app, StringComparison.OrdinalIgnoreCase))) continue;

                list.Add(new SwitchFileEntry { ApplicationName = app, install = inst, uninstall = (f.Uninstall ?? "").Trim() });
                added++;
            }

            // Vendors and apps in case-insensitive order, which is the order the file is already in,
            // so new entries slot into place instead of churning every line.
            var ordered = table
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.OrderBy(x => x.ApplicationName, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            // Two-space indent and the relaxed encoder: the shape the file already has. The default
            // encoder would write "ChuyuTeam.DISM\u002B\u002B" and turn every CJK app name into
            // escapes, which is unreadable in a file people are expected to correct by hand.
            string json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // The header the person hand-editing this file will actually read: where the data
            // came from, why a value can be wrong, and the guarantee that makes editing safe
            // (the merge above is add-only, so a corrected entry is never overwritten).
            string nl = Environment.NewLine;
            string header = string.Join(nl, new[]
            {
                "// SilentSwitches.json - silent install / uninstall switches used by manual",
                "// packaging: Create Package fills AppInstallParameters / AppUninstallParameters",
                "// from this table when it recognises the vendor and application name.",
                "//",
                "// Built on this machine by the \"Download Silent Switches\" button on the Create",
                "// Package page. Source: the winget community manifests, one manifest per package,",
                "// read from https://raw.githubusercontent.com/microsoft/winget-pkgs - the same",
                "// public data the winget client itself reads.",
                "//",
                "// \"install\" comes from each manifest's silent install switches. Winget's schema",
                "// has no uninstall switch at all, so \"uninstall\" is filled in only where the",
                "// installer framework makes it symmetric by design (NSIS \"/S\", Inno",
                "// \"/VERYSILENT /NORESTART\") and is left blank otherwise.",
                "//",
                "// Community data is not guaranteed correct for every app or version: verify the",
                "// install and uninstall commands and edit them here if they are wrong. Edits are",
                "// safe - the download button only ADDS missing apps, it never overwrites an",
                "// existing entry - and the file is re-read the next time it changes on disk, no",
                "// restart needed.",
            }) + nl;

            string path = SilentSwitchesWritePath();
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, header + json);
            return (added, path);
        }

        /// <summary>
        /// The sweep. Every package in the local winget index, one manifest each, twelve at a time.
        /// Cancelling keeps what it has: the merge is add-only, so a later run simply fills the rest.
        /// </summary>
        private static async System.Threading.Tasks.Task<(int added, int scanned, int total, string path)>
            SweepSilentSwitchesAsync(IProgress<(int done, int total)> progress, CancellationToken ct)
        {
            // The index may not exist yet on a fresh machine - Create Package can be the first page
            // anyone opens - so make sure it is there before asking it for 14,000 package names.
            await WingetCatalog.EnsureAsync(_swHttp).ConfigureAwait(false);

            var catalog = WingetCatalog.SearchPackages("");

            // ── What actually needs reading ─────────────────────────────────────────────────
            // A package is skipped when it is already in the file, or when a previous run read it
            // and found no silent switch at all. What is left is exactly the gap: packages published
            // since the last run, and entries deleted from the file by hand - so removing an app and
            // pressing the button again restores just that app, in seconds rather than minutes.
            var covered = LoadCoveredKeys();
            var skip    = LoadSwitchSkipList();

            var pkgs = catalog.Where(p =>
            {
                int dot = p.Id.IndexOf('.');
                if (dot < 0 || p.Version.Length == 0) return false;
                if (skip.Contains(p.Id)) return false;
                return !covered.Contains(p.Id.Substring(0, dot) + "|" + p.Name);
            }).ToList();

            int total = pkgs.Count, done = 0;
            var found   = new System.Collections.Concurrent.ConcurrentBag<(string, string, string, string)>();
            var noSwitch = new System.Collections.Concurrent.ConcurrentBag<string>();
            progress.Report((0, total));

            if (total == 0)
            {
                var (_, upToDate) = MergeSilentSwitchesFile(new List<(string, string, string, string)>());
                return (0, 0, 0, upToDate);
            }

            try
            {
                using var gate = new SemaphoreSlim(SwitchFetchParallel);
                var tasks = pkgs.Select(async p =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (p.Id.IndexOf('.') < 0 || p.Version.Length == 0) return;
                        string relPath = "manifests/" + char.ToLowerInvariant(p.Id[0]) + "/" + p.Id.Replace('.', '/');
                        string url = $"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/{relPath}/{Uri.EscapeDataString(p.Version)}/{p.Id}.installer.yaml";
                        string yaml = await _swHttp.GetStringAsync(url, ct).ConfigureAwait(false);

                        var (inst, uninst) = SwitchesFromManifest(yaml);
                        if (inst.Length == 0)
                        {
                            // Read, and there is genuinely nothing here. Remembered so the next run
                            // does not spend a minute rediscovering it.
                            noSwitch.Add(p.Id);
                            return;
                        }

                        int dot = p.Id.IndexOf('.');
                        found.Add((p.Id.Substring(0, dot), p.Name, inst, uninst));
                    }
                    catch (Exception exSwallow)
                    {
                        System.Diagnostics.Debug.WriteLine("[SilentSwitches] " + p.Id + ": " + exSwallow.Message);
                    }
                    finally
                    {
                        gate.Release();
                        progress.Report((Interlocked.Increment(ref done), total));
                    }
                });
                await System.Threading.Tasks.Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* keep whatever was found */ }

            if (!noSwitch.IsEmpty)
            {
                foreach (string id in noSwitch) skip.Add(id);
                SaveSwitchSkipList(skip);
            }

            var (added, path) = MergeSilentSwitchesFile(found.ToList());
            return (added, done, total, path);
        }

        /// <summary>
        /// Toolbar action. Opens a progress window, runs the sweep, and ends on the path of the file
        /// it wrote - which is the thing anyone actually wants at the end of a five minute job.
        /// </summary>
        private void BtnDownloadSwitches_Click(object sender, RoutedEventArgs e)
        {
            if (_swBusy) return;
            _swBusy = true;
            BtnDownloadSwitches.IsEnabled = false;
            try { ShowSilentSwitchDownload(); }
            finally { _swBusy = false; BtnDownloadSwitches.IsEnabled = true; }
        }

        private bool _swBusy;

        private void ShowSilentSwitchDownload()
        {
            var winBg      = Application.Current.TryFindResource("WindowBgBrush")      as Brush ?? Brushes.White;
            var borderClr  = Application.Current.TryFindResource("BorderMainBrush")    as Brush ?? Brushes.LightGray;
            var lblFg      = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var valFg      = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? Brushes.Black;
            var accent     = Application.Current.TryFindResource("AccentBlueBrush")    as Brush ?? Brushes.DodgerBlue;
            var sec        = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));

            var cwin = new Window
            {
                Width = 560, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this), ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None, AllowsTransparency = true,
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false,
            };
            var outer = new Border
            {
                Background = winBg, BorderBrush = borderClr, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black },
            };
            cwin.Content = outer;

            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            var tb = new Border { Background = titleBarBg, CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(16, 6, 12, 6) };
            tb.MouseLeftButtonDown += (_, _) => cwin.DragMove();
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tt = new TextBlock { Text = "Download Silent Switches", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button { Content = "✕", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            Grid.SetColumn(tt, 0); Grid.SetColumn(tcl, 1);
            tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg; Grid.SetRow(tb, 0); og.Children.Add(tb);

            var form = new StackPanel { Margin = new Thickness(18, 14, 18, 6) };
            Grid.SetRow(form, 1); og.Children.Add(form);

            form.Children.Add(new TextBlock
            {
                Text = "The silent install and uninstall switches used by manual packaging are read from "
                     + "Templates\\SilentSwitches.json. This builds that file on this machine from the "
                     + "winget community manifests.",
                FontSize = 11, Foreground = lblFg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var status = new TextBlock
            {
                Text = "Preparing the winget catalog. The first build reads about 14,000 manifests and "
                     + "takes 5 to 10 minutes; after that only what is missing is read.",
                FontSize = 11.5, Foreground = valFg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            form.Children.Add(status);

            var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Value = 0, Foreground = accent, BorderThickness = new Thickness(0), IsIndeterminate = true };
            form.Children.Add(bar);

            var pathBox = new TextBox
            {
                FontSize = 11, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed,
                Background = Application.Current.TryFindResource("InputBgBrush") as Brush ?? Brushes.WhiteSmoke,
                Foreground = valFg, BorderBrush = borderClr, BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
            };
            form.Children.Add(pathBox);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 4, 18, 14) };
            var openBtn  = new Button { Content = "📂  Open Folder", Style = sec, FontSize = 11, Height = 28, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
            var closeBtn = new Button { Content = "✕  Cancel", Style = sec, FontSize = 11, Height = 28, MinWidth = 96 };
            btnRow.Children.Add(openBtn); btnRow.Children.Add(closeBtn);
            Grid.SetRow(btnRow, 2); og.Children.Add(btnRow);

            // NOT disposed here on purpose. The sweep can still be unwinding after the window has
            // gone, and a token whose source has been disposed throws ObjectDisposedException out of
            // the semaphore wait instead of the cancellation the callers are written for.
            var cts = new CancellationTokenSource();
            bool finished = false;

            // The X always closes. Cancel stops the sweep and stays open, so the partial result and
            // its path are still shown - the file is written either way.
            tcl.Click += (_, _) => { cts.Cancel(); cwin.Close(); };
            closeBtn.Click += (_, _) =>
            {
                if (finished) { cwin.Close(); return; }
                cts.Cancel();
                closeBtn.IsEnabled = false;
                closeBtn.Content   = "Stopping…";
            };

            // Progress<T> marshals to the thread that created it, so these run on the UI thread
            // without a single Dispatcher call - and cannot arrive after the window is gone.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<(int done, int total)>(pr =>
            {
                if (pr.total <= 0) return;
                bar.IsIndeterminate = false;
                bar.Value = 100.0 * pr.done / pr.total;

                string eta = "";
                if (pr.done >= 50 && pr.done < pr.total)
                {
                    double left = clock.Elapsed.TotalSeconds * (pr.total - pr.done) / pr.done;
                    eta = left < 60 ? "  -  less than a minute left"
                                    : $"  -  about {Math.Ceiling(left / 60):0} minute(s) left";
                }
                status.Text = $"Reading manifests  {pr.done:N0} of {pr.total:N0}{eta}";
            });

            cwin.Loaded += async (_, _) =>
            {
                try
                {
                    var (added, scanned, total, path) = await SweepSilentSwitchesAsync(progress, cts.Token);
                    finished = true;
                    bar.IsIndeterminate = false;
                    bar.Value = 100;
                    status.Text = cts.IsCancellationRequested
                        ? $"Stopped after {scanned:N0} of {total:N0}. {added:N0} app(s) added - run it again to finish the rest."
                        : total == 0
                            ? "Nothing to read. Every package in the catalog is already in the file."
                            : $"Done. {scanned:N0} package(s) read, {added:N0} app(s) added.";
                    pathBox.Text       = path;
                    pathBox.Visibility = Visibility.Visible;
                    openBtn.Visibility = Visibility.Visible;
                    closeBtn.IsEnabled = true;
                    closeBtn.Content   = "✔  Close";
                    cwin.SizeToContent = SizeToContent.Height;

                    openBtn.Click += (_, _) =>
                    {
                        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
                        catch (Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[SilentSwitches] open folder: " + exSwallow.Message); }
                    };
                }
                catch (Exception ex)
                {
                    finished = true;
                    bar.IsIndeterminate = false;
                    status.Text = "Could not build the switch table: " + ex.Message;
                    closeBtn.Content = "✔  Close";
                }
            };

            cwin.ShowDialog();
            cts.Cancel();   // closing the window stops a sweep that is somehow still running
        }

        // ── Silent switches from Templates\SilentSwitches.json ───────────────────────────────
        //
        // Built-in silent-switch matching used to be a hardcoded table, then was removed entirely
        // and this method stubbed out to return nothing, leaving every caller on its "/S" fallback.
        // It is now backed by Templates\SilentSwitches.json, so the table is data rather than code
        // and can be corrected without a rebuild.
        //
        // Shape of the file is vendor -> array of applications:
        //
        //   { "Abelssoft": [ { "ApplicationName": "SSD Fresh",
        //                      "install": "/silent",
        //                      "uninstall": "/VERYSILENT /NORESTART" } ] }
        //
        // Callers pass either a bare exe name or "<file> <DisplayName> <Publisher>", so matching
        // works off whichever parts are present: an app that is already installed matches on
        // vendor and product name, and one that is not still matches on the exe name alone
        // (ssdfreshsetup.exe contains "ssdfresh").
        private sealed class SilentSwitchEntry
        {
            public string Vendor { get; init; } = "";
            public string App    { get; init; } = "";   // squashed to letters and digits
            public string Install   { get; init; } = "";
            public string Uninstall { get; init; } = "";
        }

        private static List<SilentSwitchEntry>? _silentSwitches;
        private static DateTime _silentSwitchesStamp = DateTime.MinValue;
        private static readonly object _silentSwitchesLock = new object();

        private static string GetSilentSwitchesPath()
        {
            const string p = @"C:\Program Files\Smekal\SwiftPack Studio\Templates\SilentSwitches.json";
            if (File.Exists(p)) return p;
            return System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                "Resources", "SilentSwitches.json");
        }

        /// <summary>Reduces a name to lowercase letters and digits, so "SSD Fresh" becomes "ssdfresh".</summary>
        private static string SquashName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// Loads and indexes the switch table, re-reading it when the file changes on disk so an
        /// edit takes effect without restarting. Returns an empty list on any failure, which puts
        /// every caller back on its own default.
        /// </summary>
        private static List<SilentSwitchEntry> LoadSilentSwitches()
        {
            try
            {
                string path = GetSilentSwitchesPath();
                if (!File.Exists(path)) return _silentSwitches ?? new List<SilentSwitchEntry>();

                DateTime stamp = File.GetLastWriteTimeUtc(path);
                lock (_silentSwitchesLock)
                {
                    if (_silentSwitches != null && stamp == _silentSwitchesStamp) return _silentSwitches;

                    var list = new List<SilentSwitchEntry>();
                    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), _swDocOptions))
                    {
                        foreach (var vendor in doc.RootElement.EnumerateObject())
                        {
                            if (vendor.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                            string vendorSquashed = SquashName(vendor.Name);

                            foreach (var app in vendor.Value.EnumerateArray())
                            {
                                if (app.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                                string appName = app.TryGetProperty("ApplicationName", out var an) ? (an.GetString() ?? "") : "";
                                string inst    = app.TryGetProperty("install",   out var i1) ? (i1.GetString() ?? "") : "";
                                string uninst  = app.TryGetProperty("uninstall", out var u1) ? (u1.GetString() ?? "") : "";
                                if (string.IsNullOrWhiteSpace(appName)) continue;

                                list.Add(new SilentSwitchEntry
                                {
                                    Vendor    = vendorSquashed,
                                    App       = SquashName(appName),
                                    Install   = inst.Trim(),
                                    Uninstall = uninst.Trim(),
                                });
                            }
                        }
                    }

                    _silentSwitches      = list;
                    _silentSwitchesStamp = stamp;
                    return list;
                }
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[SilentSwitches] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
                return _silentSwitches ?? new List<SilentSwitchEntry>();
            }
        }

        /// <summary>
        /// Application names that describe an installer rather than a product. The table contains a
        /// few (ChrisLong publishes one literally called "Setup", TruckersMP one called "Launcher"),
        /// and matching on those alone would attach their switches to any setup.exe or launcher.exe
        /// that happened to come along. They are only accepted when the vendor matches too.
        /// </summary>
        private static readonly HashSet<string> GenericAppNames = new(StringComparer.Ordinal)
        {
            "setup", "install", "installer", "update", "updater", "upgrade",
            "launcher", "client", "tools", "toolkit", "driver", "drivers",
            "app", "application", "software", "program", "package", "main",
        };

        /// <summary>
        /// Finds the silent install/uninstall switches for a source, or ("", "") when nothing
        /// matches so the caller keeps its own default.
        ///
        /// <paramref name="key"/> is a bare exe name or "&lt;file&gt; &lt;DisplayName&gt; &lt;Publisher&gt;".
        /// A candidate matches when its squashed application name appears anywhere in the squashed
        /// key. Names shorter than four characters are ignored: entries like "Go" or "7" would
        /// otherwise match almost any file name.
        ///
        /// Where several entries match, the one whose vendor also appears in the key wins, then the
        /// longest application name, so "SSD Fresh" beats a hypothetical "SSD" for ssdfreshsetup.exe.
        /// </summary>
        private static (string install, string uninstall) MatchBuiltInSwitches(string key)
        {
            try
            {
                string squashedKey = SquashName(key);
                if (squashedKey.Length < 4) return ("", "");

                SilentSwitchEntry? best = null;
                bool bestVendorMatch = false;

                foreach (var e in LoadSilentSwitches())
                {
                    if (e.App.Length < 4 || !squashedKey.Contains(e.App, StringComparison.Ordinal)) continue;

                    bool vendorMatch = e.Vendor.Length >= 3
                                    && squashedKey.Contains(e.Vendor, StringComparison.Ordinal);

                    // A generic name proves nothing on its own; the vendor has to confirm it.
                    if (!vendorMatch && GenericAppNames.Contains(e.App)) continue;

                    if (best == null
                        || (vendorMatch && !bestVendorMatch)
                        || (vendorMatch == bestVendorMatch && e.App.Length > best.App.Length))
                    {
                        best = e;
                        bestVendorMatch = vendorMatch;
                    }
                }

                return best == null ? ("", "") : (best.Install, best.Uninstall);
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[SilentSwitches] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
                return ("", "");
            }
        }

        // ── Wrap a machine-path uninstall command in an existence guard ───
        // The uninstall commands built from an installed app's ARP entry point at the
        // uninstaller ON THE PACKAGING MACHINE (e.g. $env:SystemDrive\Program Files\7-Zip\
        // Uninstall.exe). On a target device that never had the app, that file is absent and
        // Start-ADTProcess fails the whole uninstall. Wrapping it in If(Test-Path ...) makes the
        // command a no-op there instead. Paths under $($adtSession.DirFiles) ship inside the
        // package and always exist, so those are emitted unguarded.
        private static void AddGuardedUninstall(List<string> lines, string guardPath, string commandLine)
        {
            if (string.IsNullOrWhiteSpace(guardPath))
            {
                lines.Add(commandLine);
                return;
            }
            lines.Add($"    If(Test-Path \"{guardPath}\"){{");
            lines.Add(commandLine);
            lines.Add("    }");
        }

        // ── Convert absolute path to use $env:SystemDrive in PS script ────
        private static string ConvertToEnvSystemDrivePath(string fullPath)
        {
            if (fullPath.Length >= 3 && fullPath[1] == ':')
            {
                char driveLetter = char.ToUpperInvariant(fullPath[0]);
                string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
                char sysDriveLetter = char.ToUpperInvariant(systemDrive[0]);
                if (driveLetter == sysDriveLetter)
                    return "$env:SystemDrive" + fullPath[2..].Replace("\\", "\\");
            }
            return fullPath;
        }

        private static CpInstalledInfo? LookupInstalledByProductCode(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode)) return null;
            string pc = productCode.Trim();
            if (!pc.StartsWith("{")) pc = "{" + pc;
            if (!pc.EndsWith("}"))   pc += "}";

            var hives = new (Microsoft.Win32.RegistryKey root, string sub)[]
            {
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            foreach (var (root, subPath) in hives)
            {
                try
                {
                    using var hive = root.OpenSubKey(subPath);
                    if (hive == null) continue;
                    using var key  = hive.OpenSubKey(pc);
                    if (key == null) continue;

                    string dispName   = key.GetValue("DisplayName")          as string ?? "";
                    string publisher  = key.GetValue("Publisher")            as string ?? "";
                    string dispVer    = key.GetValue("DisplayVersion")       as string ?? "";
                    string instLoc    = (key.GetValue("InstallLocation")     as string ?? "").TrimEnd(Path.DirectorySeparatorChar);
                    string dispIcon   = (key.GetValue("DisplayIcon")         as string ?? "").Split(',')[0].Trim().Trim('"');
                    string uninstStr  = key.GetValue("UninstallString")      as string ?? "";
                    string quietUninst= key.GetValue("QuietUninstallString") as string ?? "";

                    if (string.IsNullOrWhiteSpace(instLoc) && dispIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        instLoc = (Path.GetDirectoryName(dispIcon) ?? "").TrimEnd(Path.DirectorySeparatorChar);

                    // Find main exe: DisplayIcon if .exe > largest exe in install folder > Start Menu shortcuts
                    string mainExePath = "";
                    if (!string.IsNullOrWhiteSpace(dispIcon) &&
                        dispIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(dispIcon))
                        mainExePath = dispIcon;
                    if (string.IsNullOrWhiteSpace(mainExePath) && Directory.Exists(instLoc))
                    {
                        var exes = Directory.GetFiles(instLoc, "*.exe", SearchOption.TopDirectoryOnly);
                        if (exes.Length > 0)
                            mainExePath = exes.OrderByDescending(e => new FileInfo(e).Length).First();
                    }
                    // Fallback: scan Start Menu shortcuts (e.g. SharedView.lnk → SharedView.exe)
                    if (string.IsNullOrWhiteSpace(mainExePath))
                        mainExePath = ResolveMainExeFromShortcuts(dispName);

                    string mainExeVer = "";
                    if (!string.IsNullOrWhiteSpace(mainExePath) && File.Exists(mainExePath))
                    {
                        try
                        {
                            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(mainExePath);
                            mainExeVer = (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                                ? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}"
                                : fvi.FileVersion?.Trim() ?? "";
                        }
                        catch { }
                    }

                    string hiveRoot = root == Microsoft.Win32.Registry.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                    return new CpInstalledInfo(dispName, publisher, dispVer, instLoc,
                                              mainExePath, mainExeVer, uninstStr, quietUninst,
                                              hiveRoot + "\\" + subPath + "\\" + pc, false);
                }
                catch { }
            }
            return null;
        }

        private static (string, string, string) ReadInstallerInfo(string f)
        {
            string ven="",nam="",ver=""; string ext=Path.GetExtension(f).ToLowerInvariant();
            if (ext==".msi") { object? inst=null,db=null,vw=null; try { Type t=Type.GetTypeFromProgID("WindowsInstaller.Installer")!; inst=Activator.CreateInstance(t)!; db=t.InvokeMember("OpenDatabase",BindingFlags.InvokeMethod,null,inst,new object[]{f,0}); var dt=db!.GetType(); vw=dt.InvokeMember("OpenView",BindingFlags.InvokeMethod,null,db,new object[]{"SELECT Property,Value FROM Property"}); var vt=vw!.GetType(); vt.InvokeMember("Execute",BindingFlags.InvokeMethod,null,vw,null); while(true){object? rec=vt.InvokeMember("Fetch",BindingFlags.InvokeMethod,null,vw,null); if(rec==null)break; var rt=rec.GetType(); string k=rt.InvokeMember("StringData",BindingFlags.GetProperty,null,rec,new object[]{1}) as string??""; string v2=rt.InvokeMember("StringData",BindingFlags.GetProperty,null,rec,new object[]{2}) as string??""; if(k=="Manufacturer"&&ven=="")ven=v2.Trim(); if(k=="ProductName"&&nam=="")nam=v2.Trim(); if(k=="ProductVersion"&&ver=="")ver=v2.Trim(); Marshal.ReleaseComObject(rec);} } catch{} finally{if(vw!=null)try{Marshal.ReleaseComObject(vw);}catch{} if(db!=null)try{Marshal.ReleaseComObject(db);}catch{} if(inst!=null)try{Marshal.ReleaseComObject(inst);}catch{}} }
            else if(ext==".exe"){try{var vi=FileVersionInfo.GetVersionInfo(f);ven=vi.CompanyName??"";nam=vi.ProductName??"";ver=vi.ProductVersion??""; // Fallback: some EXEs (e.g. installers) leave ProductName/CompanyName blank but populate FileDescription/FileVersion
if(string.IsNullOrWhiteSpace(nam))nam=(vi.FileDescription??"").Trim(); if(string.IsNullOrWhiteSpace(ver))ver=(vi.FileVersion??"").Trim();}catch{}}
            return (ven.Trim(),nam.Trim(),ver.Trim());
        }

        private void EditBox_GotFocus(object sender, RoutedEventArgs e)
        { if (sender is TextBox tb) tb.CaretIndex = tb.Text.Length; }

        private void ClearAppName_Click(object sender, RoutedEventArgs e)      => TxtAppName.Clear();
        private void ClearInstallName_Click(object sender, RoutedEventArgs e)  => TxtInstallName.Clear();
        private void ClearInstallTitle_Click(object sender, RoutedEventArgs e) => TxtInstallTitle.Clear();

        // Generic clear handler - reads TextBox name from Tag, finds it by name
        private void ClearField_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string name &&
                btn.Parent is Grid g)
            {
                foreach (UIElement child in g.Children)
                    if (child is TextBox tb) { tb.Clear(); tb.Focus(); return; }
            }
        }

        private void AppFields_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressNameUpdate) return;
            if (TxtAppVendor==null||TxtAppName==null||TxtAppVersion==null||TxtAppRevision==null) return;
            if (string.IsNullOrWhiteSpace(TxtAppVendor.Text)&&string.IsNullOrWhiteSpace(TxtAppName.Text)) return;
            string computed = string.Join("_", new[]{TxtAppVendor.Text,TxtAppName.Text,TxtAppVersion.Text,TxtAppRevision.Text}.Select(s=>s.Trim().Replace(" ","")).Where(s=>s.Length>0));
            // Re-read the JSON config fresh every time so changes made while the app
            // is running (e.g. setting AddInstallTitle = No) take effect immediately.
            var latestCfg = ReadCreatePackageConfigFresh();
            _suppressNameUpdate=true;
            if (CreatePackageConfig.Flag(latestCfg?.AddInstallName))  TxtInstallName.Text  = computed;
            if (CreatePackageConfig.Flag(latestCfg?.AddInstallTitle)) TxtInstallTitle.Text = computed;
            TxtPackageName.Text = computed;
            _suppressNameUpdate=false;

            // Live-sync the Info field values into the Script tab's $adtSession app-variables block.
            LivePatchAppVariablesInEditor();
        }

        // Lightweight handler for App Variable fields that do not feed the PackageName computation
        // (AppArch, AppLang, exit codes, processes, script version/date/author, RequireAdmin,
        // InstallName/Title). It only live-syncs values into the Script tab's $adtSession block.
        private void AppVarField_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressNameUpdate) return;
            LivePatchAppVariablesInEditor();
        }

        // Patches the App Variable values into the Script editor's $adtSession = @{ ... } block live,
        // so edits on the Info tab immediately reflect in the Script tab. Operates on the in-memory
        // editor text (not disk); the Info Save button still writes the file. Indentation is preserved
        // by PatchAppVariables. Guarded so it does not fight the live-sync / flush machinery.
        private void LivePatchAppVariablesInEditor()
        {
            if (TxtScript == null || string.IsNullOrEmpty(TxtScript.Text)) return;
            if (_flushingToScript) return;
            try
            {
                string current = TxtScript.Text;
                string patched = PatchAppVariables(current);
                if (patched == current) return;  // nothing to change

                _flushingToScript    = true;
                _suppressUndoCapture = true;
                bool wasReadOnly = TxtScript.IsReadOnly;
                int caret = TxtScript.CaretIndex;
                TxtScript.IsReadOnly = false;
                TxtScript.Text = patched;
                TxtScript.CaretIndex = Math.Min(caret, TxtScript.Text.Length);
                TxtScript.IsReadOnly = wasReadOnly;
                _suppressUndoCapture = false;
                _flushingToScript    = false;
            }
            catch
            {
                _suppressUndoCapture = false;
                _flushingToScript    = false;
            }
        }

        /// <summary>Reads CreatePackage.json fresh from disk without updating UI fields.
        /// Returns null if the file cannot be parsed.</summary>
        private static CreatePackageConfig? ReadCreatePackageConfigFresh()
        {
            try
            {
                var path = GetCreatePackageConfigPath();
                if (!File.Exists(path)) return null;
                string raw = File.ReadAllText(path);
                // Same backslash-escape tolerance as ApplyCreatePackageConfig
                var sb = new System.Text.StringBuilder(raw.Length + 16);
                bool inStr = false;
                for (int i = 0; i < raw.Length; i++)
                {
                    char c = raw[i];
                    if (c == '"' && (i == 0 || raw[i-1] != '\\')) inStr = !inStr;
                    if (inStr && c == '\\' && i + 1 < raw.Length)
                    {
                        char nx = raw[i+1];
                        bool valid = nx=='"'||nx=='\\'||nx=='/'||nx=='b'||nx=='f'||nx=='n'||nx=='r'||nx=='t'||nx=='u';
                        if (valid) { sb.Append(c); sb.Append(nx); i++; continue; }
                        else       { sb.Append('\\'); sb.Append('\\'); continue; }
                    }
                    sb.Append(c);
                }
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<CreatePackageConfig>(sb.ToString(), opts);
            }
            catch { return null; }
        }

        private void BtnInfoSave_Click(object sender, RoutedEventArgs e)
        {
            if (_createdScriptPath == null || !File.Exists(_createdScriptPath))
            {
                FlashStatus("⚠ No script loaded", "#F7B84F");
                return;
            }

            try
            {
                // Patch the Info app-variable values into the CURRENT editor text (not the file on
                // disk), so any unsaved manual edits made in the Script tab are preserved. This makes
                // Info Save consider both the Info values and the Script tab edits, matching Script Save.
                string current = TxtScript.Text;
                string patched = PatchAppVariables(current);

                // Write the combined result to disk
                File.WriteAllText(_createdScriptPath, patched, new UTF8Encoding(true));

                // Refresh the Script tab editor so it shows the updated values
                TxtScript.Text = patched;
                _scriptDirty = false;
                TxtScriptDirty.Visibility = Visibility.Collapsed;

                FlashStatus("💾 Saved to script", "#3ECFA0");
            }
            catch (Exception ex)
            {
                PkgStyledMessage(Window.GetWindow(this), "Error", "Save failed:\n" + ex.Message, "⛔");
            }
        }

        // ── Settings: edit CreatePackage.json ──────────────────────────────
        // Modal editor over CreatePackage.json (the flat key/value settings this page reads:
        // the PSADT template + output folders and the per-file-type / ARP injection flags).
        // Lets the user add / edit / delete any field, and writes the changes back via a
        // JsonNode round-trip. Mirrors the Auto Package / Validate Source / Edit MSI dialogs.
        private void BtnPkgSettings_Click(object sender, RoutedEventArgs e) => OpenCreatePackageSettings();

        private void OpenCreatePackageSettings()
        {
            string cfgPath = GetCreatePackageConfigPath();

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
                PkgStyledMessage(Window.GetWindow(this), "Create Package - Settings",
                    "CreatePackage.json is not valid JSON. Fix the file, then try again.\n\n" + cfgPath, "⚠");
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

            // Title bar
            var tb = new Border { Background = titleBarBg, CornerRadius = new CornerRadius(10, 10, 0, 0), Padding = new Thickness(16, 6, 12, 6) };
            tb.MouseLeftButtonDown += (_, _) => cwin.DragMove();
            Grid.SetRow(tb, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti = new TextBlock { Text = "⚙", FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt = new TextBlock { Text = "Create Package - Settings", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
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

            form.Children.Add(new TextBlock { Text = "The PSADT template + output folders and the per-file-type / ARP command-injection flags used when building a package.", FontSize = 10.5, Foreground = lblFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

            // (section, JSON key, label, kind)  kind: p = folder path (browse + required *), yn = Yes/No
            var fields = new (string section, string key, string label, string kind)[]
            {
                ("Paths",             "PSADTTemplateFolder",      "PSADT Template Folder",         "p"),
                ("Paths",             "PackageOutputFolder",      "Package Output Folder",         "p"),
                ("Command Injection", "AddMsiCommandToPs1",       "Add MSI Command To PS1",        "yn"),
                ("Command Injection", "AddMstCommandToPs1",       "Add MST Command To PS1",        "yn"),
                ("Command Injection", "AddMspCommandToPs1",       "Add MSP Command To PS1",        "yn"),
                ("Command Injection", "AddExeCommandToPs1",       "Add EXE Command To PS1",        "yn"),
                ("Command Injection", "AddBatCommandToPs1",       "Add BAT Command To PS1",        "yn"),
                ("Command Injection", "AddIssCommandToPs1",       "Add ISS Command To PS1",        "yn"),
                ("Command Injection", "AddRegCommandToPs1",       "Add REG Command To PS1",        "yn"),
                ("Command Injection", "AddCerCommandToPs1",       "Add CER Command To PS1",        "yn"),
                ("Command Injection", "AddInfCommandToPs1",       "Add INF Command To PS1",        "yn"),
                ("Command Injection", "AddPS1CommandToPs1",       "Add PS1 Command To PS1",        "yn"),
                ("Command Injection", "AddcmdCommandToPs1",       "Add CMD Command To PS1",        "yn"),
                ("Command Injection", "AddCloseAppsCommandToPs1", "Add Close Apps Command To PS1", "yn"),
                ("Options",               "AddInstallName",  "Add Install Name",  "yn"),
                ("Options",               "AddInstallTitle", "Add Install Title", "yn"),
                ("ARP Settings for EXE", "AddNoModify",     "Add No Modify",     "yn"),
                ("ARP Settings for EXE", "AddNoRepair",     "Add No Repair",     "yn"),
                ("ARP Settings for EXE", "AddNoRemove",     "Add No Remove",     "yn"),
            };

            var editors = new System.Collections.Generic.Dictionary<string, Control>();
            string curSection = "";
            foreach (var f in fields)
            {
                if (f.section != curSection)
                {
                    curSection = f.section;
                    form.Children.Add(new TextBlock { Text = curSection, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = valFg, Margin = new Thickness(0, form.Children.Count == 1 ? 0 : 12, 0, 6) });
                }

                string cur = ReadStr(root[f.key]);

                var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 6), MinHeight = 30 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
                lbl.Inlines.Add(new System.Windows.Documents.Run(f.label));
                if (f.kind == "p")
                    lbl.Inlines.Add(new System.Windows.Documents.Run(" *") { Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), FontWeight = FontWeights.Bold });
                Grid.SetColumn(lbl, 0); rowGrid.Children.Add(lbl);

                if (f.kind == "yn")
                {
                    bool isYes = cur.Length == 0 || CreatePackageConfig.Flag(cur);
                    var rbY = new RadioButton { Content = "Yes", FontSize = 11, IsChecked = isYes,  Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Cursor = Cursors.Hand };
                    var rbN = new RadioButton { Content = "No",  FontSize = 11, IsChecked = !isYes, Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
                    var rbPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    rbPanel.Children.Add(rbY); rbPanel.Children.Add(rbN);   // grouped by their parent panel
                    Grid.SetColumn(rbPanel, 1); rowGrid.Children.Add(rbPanel);
                    editors[f.key] = rbY;   // on save: rbY.IsChecked -> "Yes"/"No"
                }
                else // "p" - folder path with browse button in the box
                {
                    var txv = new TextBox { Text = cur, FontSize = 11, MinHeight = 26, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = valFg, VerticalContentAlignment = VerticalAlignment.Center };
                    var browse = new Button { Content = "📁", FontSize = 14, Foreground = valFg, Width = 28, Height = 26, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Browse for a folder" };
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
                form.Children.Add(rowGrid);
            }

            // Button bar
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 4, 16, 14) };
            Grid.SetRow(btnBar, 2);
            var bCancel = new Button { Content = "✕  Cancel", Style = sec, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            bCancel.Click += (_, _) => cwin.Close();
            var bSave = new Button { Content = "💾  Save", Style = sec, Height = 30, Padding = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            NdApplyPrimaryLook(bSave);
            bSave.Click += (_, _) =>
            {
                try
                {
                    // Required folder paths must be set before saving.
                    var missing = new System.Collections.Generic.List<string>();
                    foreach (var f in fields)
                        if (f.kind == "p" && string.IsNullOrWhiteSpace((editors[f.key] as TextBox)?.Text))
                            missing.Add(f.label);
                    if (missing.Count > 0)
                    {
                        PkgStyledMessage(cwin, "Create Package - Settings",
                            "These folder(s) must be set before saving:\n\n" + string.Join("\n", missing), "⚠");
                        return;
                    }

                    // Write the edited fields back into the JSON (any other keys are preserved).
                    foreach (var f in fields)
                    {
                        string val = f.kind == "yn"
                            ? ((editors[f.key] as RadioButton)?.IsChecked == true ? "Yes" : "No")
                            : ((editors[f.key] as TextBox)?.Text ?? "").Trim();
                        root[f.key] = val;
                    }

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

                    // Refresh the cached config (flags are read fresh on use, so they apply
                    // immediately) and reflect the two folder defaults in the main fields.
                    var fresh = ReadCreatePackageConfigFresh();
                    if (fresh != null)
                    {
                        _cfg = fresh;
                        string? tmpl = !string.IsNullOrWhiteSpace(fresh.PSADTTemplateFolder) ? fresh.PSADTTemplateFolder : fresh.TemplateFolder;
                        if (!string.IsNullOrWhiteSpace(tmpl)) TxtTemplatePath.Text = tmpl.Trim();
                        if (!string.IsNullOrWhiteSpace(fresh.PackageOutputFolder)) TxtOutputPath.Text = fresh.PackageOutputFolder.Trim();
                    }

                    cwin.Close();
                    PkgStyledMessage(Window.GetWindow(this), "Create Package - Settings",
                        "Settings written to CreatePackage.json.", "✅");
                }
                catch (Exception ex)
                {
                    PkgStyledMessage(cwin, "Create Package - Settings",
                        "Save failed:\n" + ex.Message + "\n\n" + cfgPath, "⚠");
                }
            };
            btnBar.Children.Add(bCancel); btnBar.Children.Add(bSave);
            og.Children.Add(btnBar);

            cwin.ShowDialog();
        }

        // Small branded message dialog matching the app's window style (replaces the plain
        // MessageBox in the Settings flow). Single OK button.
        private void PkgStyledMessage(Window? owner, string title, string message, string icon)
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
            var dIcon = NdDecisionIcon();
            Grid.SetColumn(dIcon, 0);
            actionRow.Children.Add(dIcon);
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(btnBar, 1);
            var bOk = new Button { Content = "✔  OK", Style = sec, Height = 30, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsDefault = true };
            NdApplyPrimaryLook(bOk);
            bOk.Click += (_, _) => win.Close();
            btnBar.Children.Add(bOk);
            actionRow.Children.Add(btnBar);
            og.Children.Add(actionRow);

            win.ShowDialog();
        }

        // Themed confirm dialog for the New button (matches the Publish Package
        // "New" dialog): asks before clearing all fields and re-reading defaults.
        private bool ShowNewConfirmDialog()
        {
            bool confirmed = false;
            var win = BuildNewDialog("Create Package - New");
            win.Width = 440; win.MaxWidth = 440;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - New");
            int r = 0;
            NdAddRow(cg, r++, "Status",
                "This will clear all fields and start a new package, restoring default settings from CreatePackage.json. Continue?",
                lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = NdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = NdMakeButtons(sec,
                ("✔ Yes", () => { confirmed = true;  win.Close(); }),
                ("✕ No",  () => { confirmed = false; win.Close(); })
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
            return confirmed;
        }

        private void BtnRefreshConfig_Click(object sender, RoutedEventArgs e)
        {
            // Confirm first: New clears every field, so give the user a way out.
            if (!ShowNewConfirmDialog()) return;

            // Clear the config-driven path fields and the App Variables, then re-apply
            // the config so everything reflects a fresh read of CreatePackage.json.
            TxtTemplatePath.Text = string.Empty;
            TxtOutputPath.Text   = string.Empty;
            TxtSourcePath.Text   = string.Empty;
            TxtMainFile.Text     = string.Empty;

            // App Variables
            TxtAppVendor.Clear(); TxtAppName.Clear(); TxtAppVersion.Clear(); TxtAppArch.Clear();
            TxtAppLang.Clear(); TxtAppRevision.Clear(); TxtSuccessExitCodes.Clear();
            TxtRebootExitCodes.Clear(); TxtAppProcesses.Clear(); TxtScriptVersion.Clear();
            TxtScriptDate.Clear(); TxtScriptAuthor.Clear(); TxtRequireAdmin.Clear();
            TxtInstallName.Clear(); TxtInstallTitle.Clear(); TxtPackageName.Clear();

            ApplyCreatePackageConfig();
            ExitModifyMode();   // leave modify mode if it was active
        }

        // ══════════════════════════════════════════════════════════════
        // CREATE PACKAGE
        // ══════════════════════════════════════════════════════════════
        // ── Modify existing package ─────────────────────────────────────────────
        // Switches the page into "modify" mode: the user points the Package Folder field at an
        // existing package and edits its script. Create is disabled; the source/main-file/output
        // fields are disabled (they only apply to creating a new package). Browsing the package
        // folder enables the Script tab. Modify stays active until New or Clear resets it.
        private void BtnModify_Click(object sender, RoutedEventArgs e)
        {
            EnterModifyMode();
        }

        private void EnterModifyMode()
        {
            _modifyMode = true;

            // Clear existing Package Paths and App Variables so the user starts fresh, then browses
            // to the package folder (which reloads the values from the existing script).
            TxtTemplatePath.Clear(); TxtSourcePath.Clear(); TxtMainFile.Clear(); TxtOutputPath.Clear();
            TxtAppVendor.Clear(); TxtAppName.Clear(); TxtAppVersion.Clear(); TxtAppArch.Clear();
            TxtAppLang.Clear(); TxtAppRevision.Clear(); TxtSuccessExitCodes.Clear();
            TxtRebootExitCodes.Clear(); TxtAppProcesses.Clear(); TxtScriptVersion.Clear();
            TxtScriptDate.Clear(); TxtScriptAuthor.Clear(); TxtRequireAdmin.Clear();
            TxtInstallName.Clear(); TxtInstallTitle.Clear(); TxtPackageName.Clear();

            // Disable Create; keep Modify highlighted as active by disabling it too
            if (BtnCreate != null) BtnCreate.IsEnabled = false;
            if (BtnModify != null) BtnModify.IsEnabled = false;

            // Rename the template-folder label to "Package Folder"; its browse stays enabled
            if (LblTemplateFolder != null) LblTemplateFolder.Text = "Package Folder";

            // Disable source / main-file / output fields and their browse buttons
            SetModifyFieldsDisabled(true);

            FlashStatus("✏ Modify mode - browse to an existing package folder", "#5AA9E6");
        }

        private void ExitModifyMode()
        {
            _modifyMode = false;

            if (BtnCreate != null) BtnCreate.IsEnabled = true;
            if (BtnModify != null) BtnModify.IsEnabled = true;

            if (LblTemplateFolder != null) LblTemplateFolder.Text = "PSADT Template Folder";

            SetModifyFieldsDisabled(false);

            // Return to the fresh app-open state: unload any loaded script and lock the Script tab,
            // so New/Clear after a modify+browse does not leave the Script tab enabled.
            _createdScriptPath = null;
            _scriptDirty = false;
            if (TxtScript != null) { TxtScript.IsReadOnly = false; TxtScript.Clear(); TxtScript.IsReadOnly = true; }
            if (TxtScriptDirty != null) TxtScriptDirty.Visibility = Visibility.Collapsed;
            if (PanelNoScript  != null) PanelNoScript.Visibility  = Visibility.Visible;
            if (TxtScriptFileName != null) TxtScriptFileName.Text = "Invoke-AppDeployToolkit.ps1";
            SetScriptTabEnabled(false);
            if (BtnInfoSave != null) BtnInfoSave.IsEnabled = false;
            ActivateTab(false); // ensure we are on the Info tab
        }

        // Enable/disable the new-package-only fields (Source Folder, Main Source File, Package
        // Output Folder) and their Browse buttons. Disabled while modifying an existing package.
        private void SetModifyFieldsDisabled(bool disabled)
        {
            bool en = !disabled;
            if (TxtSourcePath     != null) TxtSourcePath.IsEnabled     = en;
            if (BtnBrowseSource   != null) BtnBrowseSource.IsEnabled   = en;
            if (TxtMainFile       != null) TxtMainFile.IsEnabled       = en;
            if (BtnBrowseMainFile != null) BtnBrowseMainFile.IsEnabled = en;
            if (TxtOutputPath     != null) TxtOutputPath.IsEnabled     = en;
            if (BtnBrowseOutput   != null) BtnBrowseOutput.IsEnabled   = en;
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            var miss = new List<string>();
            if (string.IsNullOrWhiteSpace(TxtTemplatePath.Text)) miss.Add("PSADT Template Folder");
            if (string.IsNullOrWhiteSpace(TxtOutputPath.Text))   miss.Add("Package Output Folder");
            if (string.IsNullOrWhiteSpace(TxtPackageName.Text))  miss.Add("PackageName");
            if (miss.Count > 0) { ShowValidationError(miss); return; }

            // ── PSADT template pre-flight ────────────────────────────────────────────────
            // The folder exists but carries no deployment script anywhere under it: the copy
            // in Step 2 would succeed, Step 3 would log a WARNING nobody reads mid-progress,
            // and the "created" package would have no entry point - the same silent-success
            // trap the Auto Package flow closes with its create-time abort. Same test as
            // there: FindDeploymentPs1, which ignores the decoy copy the toolkit ships
            // inside its own PSAppDeployToolkit module folder, so a template that holds
            // ONLY the module still counts as missing the script. A non-existent folder is
            // left to the copy step's own error - this guard is about a folder that looks
            // right and is not.
            if (Directory.Exists(TxtTemplatePath.Text.Trim())
                && FindDeploymentPs1(TxtTemplatePath.Text.Trim()) == null)
            {
                ShowTemplateScriptMissingDialog(TxtTemplatePath.Text.Trim());
                return;
            }

            string templatePath = TxtTemplatePath.Text.Trim();
            string sourcePath   = TxtSourcePath.Text.Trim();
            string mainFile     = TxtMainFile.Text.Trim();
            string outputBase   = TxtOutputPath.Text.Trim();
            string packageName  = Regex.Replace(TxtPackageName.Text.Trim(), @"[^\w\-_.()\[\]]", "_");
            // Brackets "()" and "[]" are valid folder-name characters and are kept so the
            // package folder matches the PackageName exactly (e.g. "...7-Zip26.02(x64edition)...").
            // Only truly invalid characters become "_"; collapse any runs of "_" into a single "_".
            packageName = Regex.Replace(packageName, @"_+", "_").Trim('_');
            var    patches      = ReadPatches();

            // Capture install/uninstall overrides from the linked Auto Package page if available,
            // otherwise pass empty strings - PatchInstallCommands will fall back to registry lookup.
            // These are resolved on the UI thread so the background thread can safely read them.
            string cpInstallArgs   = "";   // no dedicated field on this page - auto-derived
            string cpUninstallSrc  = "";   // no dedicated field on this page - registry or comment
            string cpUninstallArgs = "";   // no dedicated field on this page - auto-derived

            // Capture app identity fields on the UI thread for use in the background thread
            string cpAppVendor  = TxtAppVendor?.Text.Trim()  ?? "";
            string cpAppName    = TxtAppName?.Text.Trim()    ?? "";
            string cpAppVersion = TxtAppVersion?.Text.Trim() ?? "";

            // Output folder\PackageName\ is the package root
            string pkgRoot = Path.Combine(outputBase, packageName);

            if (Directory.Exists(pkgRoot))
            {
                if (!ShowFolderExistsDialog(pkgRoot, packageName)) return;
                string ps1InDest = Directory.GetFiles(pkgRoot, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault() ?? "";
                if (File.Exists(ps1InDest) && IsFileLocked(ps1InDest))
                { if (!ShowPs1LockedDialog(ps1InDest)) return; TryClosePs1InEditors(ps1InDest); Thread.Sleep(800); }
            }

            AppBusy.SetManual("Create Package", true);   // Auto Package waits until this build ends
            BtnCreate.IsEnabled=false; BtnCreate.Content="Creating...";
            SetInfoButtonsEnabled(false);
            if (BtnInfoSave != null) BtnInfoSave.IsEnabled = false;
            TxtStatus.Visibility=Visibility.Collapsed; PanelProgress.Visibility=Visibility.Visible; PkgProgressBar.Value=0;
            TxtProgressLabel.Text="Starting...";

            _createCts = new System.Threading.CancellationTokenSource();
            var token = _createCts.Token;
            _createPause.Set(); // ensure not paused from any previous run
            _pkgCreateStartTime = DateTime.Now;

            var disp = Dispatcher;
            Action<int,string> rep = (v,l) => disp.BeginInvoke(new Action(()=>{PkgProgressBar.Value=v;TxtProgressLabel.Text=l;}));

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string? err=null; string? scriptPath=null;
                var actionLog = new System.Collections.Generic.List<string>();
                actionLog.Add("Package Creation Log -- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                actionLog.Add("Package Name     : " + packageName);
                actionLog.Add("PSADT Template Folder : " + templatePath);
                actionLog.Add("Output Location  : " + pkgRoot);
                if (!string.IsNullOrWhiteSpace(sourcePath)) actionLog.Add("Source           : " + sourcePath);
                if (!string.IsNullOrWhiteSpace(mainFile))   actionLog.Add("Main Source File : " + mainFile + (string.IsNullOrWhiteSpace(sourcePath) ? " (will be copied to Files\\)" : " (reference only)"));
                actionLog.Add(new string('-', 60));
                try
                {
                    // Step 1: Create Output\PackageName\
                    token.ThrowIfCancellationRequested();
                    rep(5, "Creating package folder...");
                    Directory.CreateDirectory(pkgRoot);
                    actionLog.Add("[STEP 1] Package folder created: " + pkgRoot);

                    // Step 2: Copy PSADT template into Output\PackageName\
                    // Position the copied template so the folder containing
                    // Invoke-AppDeployToolkit.ps1 sits ONE level below the package root:
                    //   • ps1 at template root        → wrap into <pkgRoot>\Package
                    //   • ps1 in a template subfolder → copy as-is (structure preserved,
                    //                                    e.g. AppPackage or Output\AppPackage)
                    // Intune / Documents are then created beside that folder (its parent).
                    token.ThrowIfCancellationRequested();
                    rep(20, "Copying template...");
                    string? tplPs1 = FindDeploymentPs1(templatePath);
                    string tplPs1Dir = tplPs1 != null ? (Path.GetDirectoryName(tplPs1) ?? templatePath) : templatePath;
                    string templateCopyDest = (tplPs1 != null && SamePath(tplPs1Dir, templatePath))
                        ? Path.Combine(pkgRoot, "Package") : pkgRoot;
                    Directory.CreateDirectory(templateCopyDest);
                    CopyDir(templatePath, templateCopyDest, token, _createPause);
                    int tmplFiles = Directory.GetFiles(pkgRoot, "*", SearchOption.AllDirectories).Length;
                    actionLog.Add("[STEP 2] Template copied: " + templatePath + " → " + templateCopyDest + " (" + tmplFiles + " files)");

                    // The package keeps the PSADT template contents exactly as copied -
                    // nothing that came from the template folder is removed during creation.

                    // Step 3: Find Invoke-AppDeployToolkit.ps1 in Output\PackageName\ (any depth)
                    token.ThrowIfCancellationRequested();
                    rep(50, "Locating Invoke-AppDeployToolkit.ps1...");
                    scriptPath = FindDeploymentPs1(pkgRoot);
                    if (scriptPath != null)
                        actionLog.Add("[STEP 3] Script located: " + scriptPath);
                    else
                        actionLog.Add("[STEP 3] WARNING: Invoke-AppDeployToolkit.ps1 not found in package folder");

                    // NOTE: Intune and Documents folders are intentionally NOT created here.
                    //   • Intune    is created by the Create Intunewin page when Create is clicked.
                    //   • Documents is created by the Create Document page per its JSON settings.
                    //   • Logs      are copied by the Execute Package page on install/uninstall.
                    // Create Package only lays down the app-package folder (the template copy).

                    // Step 4: If found, copy Source Folder / Main File into the sibling Files\ folder
                    if (scriptPath != null)
                    {
                        string ps1Dir  = Path.GetDirectoryName(scriptPath)!;
                        string filesDir = Path.Combine(ps1Dir, "Files");
                        Directory.CreateDirectory(filesDir);
                        actionLog.Add("[STEP 4] Files folder: " + filesDir);

                        // Clear any files the template may have placed in Files\ (e.g. leftover
                        // installer files from a previous run). Prevents stale files from mixing
                        // with the real source files - same fix as AutoPackagePage.
                        if (Directory.Exists(filesDir))
                        {
                            foreach (string staleFile in Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories))
                                try { File.Delete(staleFile); } catch { }
                            foreach (string staleDir in Directory.GetDirectories(filesDir)
                                         .OrderByDescending(x => x.Length))
                                try { Directory.Delete(staleDir, recursive: true); } catch { }
                            actionLog.Add("[STEP 4] Files\\ cleared (template leftovers removed)");
                        }

                        token.ThrowIfCancellationRequested();
                        rep(65, "Copying source files...");
                        if (!string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath))
                        {
                            CopyDir(sourcePath, filesDir, token, _createPause);
                            int copied = Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories).Length;
                            actionLog.Add("[STEP 4] Source folder copied: " + sourcePath + " → " + filesDir + " (" + copied + " files)");
                        }
                        else if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                        {
                            File.Copy(sourcePath, Path.Combine(filesDir, Path.GetFileName(sourcePath)), true);
                            actionLog.Add("[STEP 4] Source file copied: " + Path.GetFileName(sourcePath) + " → Files\\");
                        }
                        else if (!string.IsNullOrWhiteSpace(sourcePath))
                            actionLog.Add("[STEP 4] Source path not found (skipped): " + sourcePath);

                        // If Source Folder is empty but Main Source File is specified, copy it to Files\
                        if (string.IsNullOrWhiteSpace(sourcePath) &&
                            !string.IsNullOrWhiteSpace(mainFile) && File.Exists(mainFile))
                        {
                            token.ThrowIfCancellationRequested();
                            string destFile = Path.Combine(filesDir, Path.GetFileName(mainFile));
                            File.Copy(mainFile, destFile, true);
                            actionLog.Add("[STEP 4] Main Source File copied to Files\\: " + Path.GetFileName(mainFile));
                        }
                    }

                    // Step 5: Patch script variables
                    token.ThrowIfCancellationRequested();
                    rep(85, "Patching script...");
                    if (scriptPath != null)
                    {
                        PatchScript(Path.GetDirectoryName(scriptPath)!, patches);
                        var appliedVars = patches.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => kv.Key + " = " + kv.Value).ToList();
                        actionLog.Add("[STEP 5] Script variables patched (" + appliedVars.Count + " values):");
                        foreach (var v in appliedVars) actionLog.Add("          " + v);

                        // Step 5b: Auto-inject Install/Uninstall commands based on Files folder contents
                        // Read Install/Uninstall overrides from the UI (captured before thread start via closure)
                        token.ThrowIfCancellationRequested();
                        string filesDir2 = Path.Combine(Path.GetDirectoryName(scriptPath)!, "Files");
                        // Pass app identity into cfg so PatchInstallCommands can build a meaningful ARP comment
                        _cfg.AppVendor  = cpAppVendor;
                        _cfg.AppName    = cpAppName;
                        _cfg.AppVersion = cpAppVersion;

                        var injected = PatchInstallCommands(scriptPath, filesDir2,
                            manualInstallArgs:    cpInstallArgs,
                            manualUninstallSource: cpUninstallSrc,
                            manualUninstallArgs:  cpUninstallArgs,
                            addMsi: CreatePackageConfig.Flag(_cfg.AddMsiCommandToPs1),
                            addMst: CreatePackageConfig.Flag(_cfg.AddMstCommandToPs1),
                            addMsp: CreatePackageConfig.Flag(_cfg.AddMspCommandToPs1),
                            addExe: CreatePackageConfig.Flag(_cfg.AddExeCommandToPs1),
                            addBat: CreatePackageConfig.Flag(_cfg.AddBatCommandToPs1),
                            addIss: CreatePackageConfig.Flag(_cfg.AddIssCommandToPs1),
                            addReg: CreatePackageConfig.Flag(_cfg.AddRegCommandToPs1),
                            addCer: CreatePackageConfig.Flag(_cfg.AddCerCommandToPs1),
                            addInf: CreatePackageConfig.Flag(_cfg.AddInfCommandToPs1),
                            addPs1: CreatePackageConfig.Flag(_cfg.AddPS1CommandToPs1),
                            cfg: _cfg);
                        foreach (var msg in injected) actionLog.Add("[STEP 5b] " + msg);
                    }

                    actionLog.Add(new string('-', 60));
                    actionLog.Add("Package creation completed successfully at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    rep(100, "Done!"); Thread.Sleep(300);
                }
                catch (OperationCanceledException)
                {
                    err = null; // cancelled - not an error
                    actionLog.Add("[CANCELLED] Package creation was cancelled by the user.");
                }
                catch(Exception ex)
                {
                    err=ex.Message;
                    actionLog.Add("[ERROR] " + ex.Message);
                }

                disp.BeginInvoke(new Action(()=>
                {
                    _createCts?.Dispose();
                    _createCts = null;
                    _createPause.Set(); // ensure unpaused for next run
                    AppBusy.SetManual("Create Package", false);
                    BtnCreate.IsEnabled=true; BtnCreate.Content="  Create  ";
                    SetInfoButtonsEnabled(true);
                    PanelProgress.Visibility=Visibility.Collapsed; TxtStatus.Visibility=Visibility.Visible;
                    if (token.IsCancellationRequested)
                    {
                        if (BtnInfoSave != null) BtnInfoSave.IsEnabled = false;
                        // Package may be partially created - inform user
                        ShowThemedInfoDialog("Cancelled", "Status", "Package creation was cancelled.");
                    }
                    else if (err!=null)
                    {
                        if (BtnInfoSave != null) BtnInfoSave.IsEnabled = false;
                        ShowThemedInfoDialog("Failed", "Error", err);
                    }
                    else
                    {
                        _createdScriptPath = scriptPath;
                        if (scriptPath != null) LoadScriptIntoEditor(scriptPath);
                        SetScriptTabEnabled(true);
                        if (BtnInfoSave != null) BtnInfoSave.IsEnabled = true;
                        // Notify MainWindow so ExecutePackagePage is pre-filled immediately
                        // with the newly created package's script path.
                        if (scriptPath != null) OnScriptPathCreated?.Invoke(scriptPath);
                        ShowSuccessDialogWithScript(pkgRoot, packageName, actionLog);
                    }
                }));
            });
        }


        private void BtnCancelCreate_Click(object sender, RoutedEventArgs e)
        {
            if (_createCts == null || _createCts.IsCancellationRequested) return;

            // Pause the background copy immediately so no more files are copied while dialog is open
            _createPause.Reset();

            if (ShowCancelCreationDialog())
            {
                // User chose Yes - cancel (pause gate will be set in the completion block)
                _createCts.Cancel();
            }
            else
            {
                // User chose No (or closed dialog) - resume copy
                _createPause.Set();
            }
        }

        private bool ShowCancelCreationDialog()
        {
            bool confirmed = false;
            var win = BuildNewDialog("Create Package - Cancel Creation");
            win.Width = 360; win.MaxWidth = 360;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - Cancel Creation");
            int r = 0;
            NdAddRow(cg, r++, "Status", "Package creation is in progress.", lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            NdAddRow(cg, r++, "Action", "Do you want to cancel it?",        lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cancelIcon = NdDecisionIcon();
            Grid.SetRow(cancelIcon, r); Grid.SetColumn(cancelIcon, 0);
            cg.Children.Add(cancelIcon);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = NdMakeButtons(sec,
                ("✔ Yes", () => { confirmed = true;  win.Close(); }),
                ("✕ No",  () => { confirmed = false; win.Close(); })
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
            return confirmed;
        }

        private Dictionary<string, string> ReadPatches()
        {
            var p = new Dictionary<string, string> {
                ["AppVendor"]=""+TxtAppVendor.Text.Trim(), ["AppName"]=""+TxtAppName.Text.Trim(),
                ["AppVersion"]=""+TxtAppVersion.Text.Trim(), ["AppArch"]=""+TxtAppArch.Text.Trim(),
                ["AppLang"]=""+TxtAppLang.Text.Trim(), ["AppRevision"]=""+TxtAppRevision.Text.Trim(),
                ["AppScriptVersion"]=""+TxtScriptVersion.Text.Trim(), ["AppScriptDate"]=""+TxtScriptDate.Text.Trim(),
                ["AppScriptAuthor"]=""+TxtScriptAuthor.Text.Trim(), ["InstallName"]=""+TxtInstallName.Text.Trim(),
                ["InstallTitle"]=""+TxtInstallTitle.Text.Trim(), ["RequireAdmin"]=""+TxtRequireAdmin.Text.Trim()
            };
            string sec=TxtSuccessExitCodes.Text.Trim(); if(!string.IsNullOrWhiteSpace(sec)) p["AppSuccessExitCodes"]="@("+sec+")";
            string rec=TxtRebootExitCodes.Text.Trim();  if(!string.IsNullOrWhiteSpace(rec)) p["AppRebootExitCodes"]="@("+rec+")";
            string pr=TxtAppProcesses.Text.Trim();
            if(!string.IsNullOrWhiteSpace(pr)){var it=pr.Split(',').Select(s=>s.Trim()).Where(s=>s.Length>0); p["AppProcessesToClose"]="@('"+string.Join("', '",it)+"')";}
            return p;
        }

        /// <summary>
        /// Finds the REAL deployment Invoke-AppDeployToolkit.ps1 under <paramref name="root"/>,
        /// EXCLUDING the copy bundled inside the PSAppDeployToolkit module folder
        /// (…\PSAppDeployToolkit\Frontend\v4\Invoke-AppDeployToolkit.ps1). Excluded, not merely
        /// sorted last: every v4 template carries that module folder, so a template with NO
        /// deployment script still produced one match - the pre-flight guard passed on a broken
        /// template, and Step 3 then patched the module's internal file with the app variables.
        /// The decoy is never the deployment script, so it is never a valid answer, not even as
        /// a last resort. Among real candidates the shallowest path wins; returns null if none.
        /// </summary>
        private static string? FindDeploymentPs1(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                return Directory.GetFiles(root, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories)
                    .Where(p => p.IndexOf(@"\PSAppDeployToolkit\", StringComparison.OrdinalIgnoreCase) < 0)
                    .OrderBy(p => p.Count(c => c == '\\' || c == '/'))
                    .ThenBy(p => p.Length)
                    .FirstOrDefault();
            }
            catch { }
            return null;
        }

        /// <summary>Case-insensitive, separator-tolerant full-path comparison.</summary>
        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\', '/'),
                    Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a?.TrimEnd('\\', '/'), b?.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void CopyDir(string src, string dst,
            System.Threading.CancellationToken token = default,
            System.Threading.ManualResetEventSlim? pause = null)
        {
            // Normalize paths for comparison - prevents infinite loop when dst is inside src
            string srcFull = Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string dstFull = Path.GetFullPath(dst).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            Directory.CreateDirectory(dst);

            foreach (string f in Directory.GetFiles(src))
            {
                // Wait if paused (blocks until resumed); throws if cancelled
                pause?.Wait(token);
                token.ThrowIfCancellationRequested();
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }

            foreach (string d in Directory.GetDirectories(src))
            {
                // Wait if paused (blocks until resumed); throws if cancelled
                pause?.Wait(token);
                token.ThrowIfCancellationRequested();
                string subDstFull = Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                // Skip if this subdirectory IS the destination or contains it - avoids infinite recursion
                if (dstFull.StartsWith(subDstFull, StringComparison.OrdinalIgnoreCase)) continue;
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)), token, pause);
            }
        }

        private static void PatchScript(string dest, Dictionary<string, string> patches)
        {
            string? ps1=Directory.GetFiles(dest,"Invoke-AppDeployToolkit.ps1",SearchOption.AllDirectories).FirstOrDefault();
            if(ps1==null)return;
            string txt=File.ReadAllText(ps1);
            var raw=new HashSet<string>{"RequireAdmin","AppProcessesToClose","AppSuccessExitCodes","AppRebootExitCodes"};
            foreach(var kv in patches){if(string.IsNullOrEmpty(kv.Value))continue; string k=Regex.Escape(kv.Key); txt=raw.Contains(kv.Key)?Regex.Replace(txt,@"(?m)(^\s+"+k+@"\s*=\s*)([^\r\n#]+)","${1}"+kv.Value):Regex.Replace(txt,@"(?m)(^\s+"+k+@"\s*=\s*')[^']*('|\Z)","${1}"+kv.Value+"'");}
            File.WriteAllText(ps1,txt,new UTF8Encoding(true));
        }

        /// <summary>
        /// Scans the Files folder for .msi and .exe files and injects
        /// Start-ADTMsiProcess / Start-ADTProcess commands after the
        /// ## &lt;Perform Installation/Uninstallation tasks here&gt; markers.
        /// Handles both MSI-only, EXE-only, and MSI+EXE combinations.
        /// When manualUninstallSource is provided it is used verbatim (mirrors
        /// AutoPackagePage.PatchExeScript logic). Otherwise registry lookup is
        /// attempted; if that also fails a TODO comment is written instead of
        /// incorrectly reusing the install EXE.
        /// </summary>
        private static List<string> PatchInstallCommands(
            string scriptPath,
            string filesDir,
            string manualInstallArgs     = "",
            string manualUninstallSource = "",
            string manualUninstallArgs   = "",
            bool addMsi = true, bool addMst = true, bool addMsp = true,
            bool addExe = true, bool addBat = true, bool addIss = true,
            bool addReg = true, bool addCer = true, bool addInf = true,
            bool addPs1 = true,
            CreatePackageConfig? cfg = null)
        {
            var log = new List<string>();
            if (!File.Exists(scriptPath))   { log.Add("Script not found"); return log; }
            if (!Directory.Exists(filesDir)){ log.Add(@"Files\ folder not found - no commands injected"); return log; }

            string txt = File.ReadAllText(scriptPath, Encoding.UTF8);
            const string INSTALL_MARKER   = "## <Perform Installation tasks here>";
            const string UNINSTALL_MARKER = "## <Perform Uninstallation tasks here>";
            const string POST_MARKER      = "## <Perform Post-Installation tasks here>";

            var installLines   = new List<string>();
            var uninstallLines = new List<string>();
            var closeAppsList  = new List<string>(); // process names collected from installed MSI/EXE apps

            // ── Auto-collect AppProcessesToClose - only when AddCloseAppsCommandToPs1 = Yes ─
            bool addCloseApps = CreatePackageConfig.Flag(cfg?.AddCloseAppsCommandToPs1);
            if (addCloseApps)
            {
                // EXEs
                foreach (string ef in Directory.GetFiles(filesDir, "*.exe", SearchOption.AllDirectories))
                {
                    string hint = "";
                    try { var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(ef); hint = vi.ProductName?.Trim() ?? ""; } catch { }
                    if (string.IsNullOrWhiteSpace(hint)) hint = Path.GetFileNameWithoutExtension(ef);
                    var regE = LookupInstalledByExe(ef, hint);
                    string procName = DeriveCloseApps(regE);
                    if (string.IsNullOrWhiteSpace(procName)) procName = Path.GetFileNameWithoutExtension(ef);
                    if (!string.IsNullOrWhiteSpace(procName) && !closeAppsList.Contains(procName, StringComparer.OrdinalIgnoreCase))
                        closeAppsList.Add(procName);
                }
                // MSIs
                foreach (string mf in Directory.GetFiles(filesDir, "*.msi", SearchOption.AllDirectories))
                {
                    var (msiPc, _, msiPn, _) = ReadMsiAllProperties(mf);
                    CpInstalledInfo? regM = !string.IsNullOrWhiteSpace(msiPn)
                        ? LookupInstalledByExe("", msiPn) : null;
                    if (regM == null && !string.IsNullOrWhiteSpace(msiPc))
                        regM = LookupInstalledByProductCode(msiPc);
                    string msiProc = DeriveCloseApps(regM);
                    if (!string.IsNullOrWhiteSpace(msiProc) && !closeAppsList.Contains(msiProc, StringComparer.OrdinalIgnoreCase))
                        closeAppsList.Add(msiProc);
                }
            }
            // ──────────────────────────────────────────────────────────────────────

            // Relative path from filesDir
            string Rel(string full) => Path.GetRelativePath(filesDir, full);

            // Produce "$($adtSession.DirFiles)\rel\path" - used inside PowerShell strings
            string DR(string rel) => "$($adtSession.DirFiles)\\" + rel;

            // Comment from registry (via product code) then MSI properties - single COM session
            string MsiComment(string msiPath, string pc, string mfr, string pn, string pv)
                => BuildAppComment(pc, mfr, pn, pv);

            // Comment + uninstall path + registry record from registry then FileVersionInfo
            (string comment, string uninstPath, string uninstArg, CpInstalledInfo? regInfo) ExeInfoCP(string exePath)
            {
                // Step 1: Read the EXE's own version info first - use ProductName as registry hint
                // (mirrors ReadInstallerInfo + the FileDescription fallback from FetchAndApplyFileInfo)
                string fviPublisher = "", fviName = "", fviVersion = "";
                if (File.Exists(exePath))
                {
                    try
                    {
                        var vi   = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                        fviPublisher = vi.CompanyName?.Trim()  ?? "";
                        fviName      = vi.ProductName?.Trim()  ?? "";
                        fviVersion   = vi.ProductVersion?.Trim() ?? "";
                        // Fallback within EXE info: FileDescription / FileVersion
                        if (string.IsNullOrWhiteSpace(fviName))    fviName    = vi.FileDescription?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviVersion)) fviVersion = vi.FileVersion?.Trim()     ?? "";
                    }
                    catch { }
                }

                // Step 2: Use the best available name as hint for registry lookup.
                // Priority: EXE ProductName/FileDescription → stripped filename
                string hint = !string.IsNullOrWhiteSpace(fviName) ? fviName
                    : Regex.Replace(
                        Path.GetFileNameWithoutExtension(exePath),
                        @"[-_.\s]?(setup|install|installer|win\d+|x64|x86|\d[\d.\-]*).*$",
                        "", RegexOptions.IgnoreCase).Trim();
                if (hint.Length < 3) hint = Path.GetFileNameWithoutExtension(exePath);

                // Step 3: Registry lookup - if installed, registry values take priority
                var reg = LookupInstalledByExe(exePath, hint);
                string comment;
                if (reg != null)
                {
                    comment = BuildAppComment("", reg.Publisher, reg.DisplayName, reg.DisplayVersion);
                    // If registry DisplayVersion is empty, fall back to EXE file version
                    if (!string.IsNullOrWhiteSpace(comment) && string.IsNullOrWhiteSpace(reg.DisplayVersion)
                        && !string.IsNullOrWhiteSpace(fviVersion))
                        comment = BuildAppComment("", reg.Publisher, reg.DisplayName, fviVersion);
                }
                else
                {
                    // Not installed - use EXE's own version info
                    comment = BuildAppComment("", fviPublisher, fviName, fviVersion);
                }

                // Step 4: Final fallback - use the filename so comment is never blank
                if (string.IsNullOrWhiteSpace(comment))
                    comment = "# " + Path.GetFileName(exePath);

                // Step 5: Derive uninstall path + argument from registry
                string uPath = "", uArg = "";
                if (reg != null)
                {
                    string rawU = !string.IsNullOrWhiteSpace(reg.QuietUninstallString)
                        ? reg.QuietUninstallString : reg.UninstallString ?? "";
                    if (!rawU.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                    {
                        if (rawU.StartsWith("\""))
                        {
                            int end = rawU.IndexOf('"', 1);
                            if (end > 1)
                            {
                                uPath = rawU[1..end];
                                uArg  = rawU[(end + 1)..].Trim();
                            }
                        }
                        else
                        {
                            int idx = rawU.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                uPath = rawU[..(idx + 4)];
                                uArg  = rawU[(idx + 4)..].Trim();
                            }
                        }
                    }
                }
                return (comment, uPath, uArg, reg);
            }

            string[] Collect(string pattern) =>
                Directory.GetFiles(filesDir, pattern, SearchOption.AllDirectories)
                    .OrderBy(f => Path.GetDirectoryName(f)!.Length).ThenBy(f => f).ToArray();

            // ── MSI ──────────────────────────────────────────────────────
            if (addMsi)
            {
                foreach (var msiPath in Collect("*.msi"))
                {
                    string msiRel    = Rel(msiPath);
                    string msiFolder = Path.GetDirectoryName(msiPath)!;
                    string baseName  = Path.GetFileNameWithoutExtension(msiPath);
                    string msiName   = Path.GetFileName(msiPath);

                    // Single COM session - reads ProductCode + Manufacturer + ProductName + ProductVersion
                    var (pc, mfr, pn, pv) = ReadMsiAllProperties(msiPath);

                    // Build comment: registry (via product code) → MSI properties → filename fallback
                    string rawComment  = MsiComment(msiPath, pc, mfr, pn, pv);
                    string commentBody = string.IsNullOrWhiteSpace(rawComment)
                        ? msiName
                        : rawComment.TrimStart('#', ' ').Trim();

                    // MST: same base name only (e.g. setup.msi ↔ setup.mst)
                    string mstRel = "";
                    if (addMst)
                    {
                        string mstSame = Path.Combine(msiFolder, baseName + ".mst");
                        if (File.Exists(mstSame)) mstRel = Rel(mstSame);
                    }

                    // ── Install ──
                    installLines.Add($"    # Install {commentBody}");
                    if (string.IsNullOrEmpty(mstRel))
                        installLines.Add($"    Start-ADTMsiProcess -Action 'Install' -FilePath \"{DR(msiRel)}\"");
                    else
                        installLines.Add($"    Start-ADTMsiProcess -Action 'Install' -FilePath \"{DR(msiRel)}\" -Transforms \"{DR(mstRel)}\"");
                    installLines.Add("");

                    // ── Uninstall ──
                    uninstallLines.Add($"    # Uninstall {commentBody}");
                    string uninstallCmd = string.IsNullOrEmpty(pc)
                        ? $"    Start-ADTMsiProcess -Action 'Uninstall' -FilePath \"{DR(msiRel)}\""
                        : $"    Start-ADTMsiProcess -Action 'Uninstall' -ProductCode \"{pc}\"";
                    uninstallLines.Add(uninstallCmd);
                    uninstallLines.Add("");

                    log.Add($"MSI: {msiRel}{(string.IsNullOrEmpty(mstRel) ? "" : $" +MST:{mstRel}")}");
                }
            }

            // ── MSP ──────────────────────────────────────────────────────
            if (addMsp)
            {
                foreach (var mspPath in Collect("*.msp"))
                {
                    string mspRel  = Rel(mspPath);
                    string mspName = Path.GetFileName(mspPath);
                    installLines.Add($"    # Install {mspName}");
                    installLines.Add($"    Start-ADTMsiProcess -Action 'Patch' -FilePath \"{DR(mspRel)}\"");
                    installLines.Add("");
                    log.Add($"MSP: {mspRel}");
                }
            }

            // ── EXE ──────────────────────────────────────────────────────
            if (addExe)
            {
                var issBaseNames = new HashSet<string>(
                    Collect("*.iss").Select(f => Path.GetFileNameWithoutExtension(f)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var exePath in Collect("*.exe"))
                {
                    string exeBase = Path.GetFileNameWithoutExtension(exePath);
                    if (addIss && issBaseNames.Contains(exeBase)) continue;

                    string exeRel  = Rel(exePath);
                    string exeName = Path.GetFileName(exePath);
                    var (appComment, uPath, uRegistryArg, exeReg) = ExeInfoCP(exePath);

                    var (instSwitch, uninstSwitch) = MatchBuiltInSwitches(exeName.ToLowerInvariant());
                    // Priority: manual args → MatchBuiltInSwitches → /S fallback
                    string resolvedInstArgs = !string.IsNullOrWhiteSpace(manualInstallArgs)
                        ? manualInstallArgs
                        : !string.IsNullOrEmpty(instSwitch) ? instSwitch : "/S";
                    string instArgsPart = $" -ArgumentList '{resolvedInstArgs}'";

                    if (!string.IsNullOrEmpty(appComment)) installLines.Add($"    # Install {appComment.TrimStart('#', ' ').Trim()}");
                    else installLines.Add($"    # Install {exeName}");
                    installLines.Add($"    Start-ADTProcess -FilePath \"{DR(exeRel)}\"{instArgsPart} -WindowStyle \"Hidden\"");
                    installLines.Add("");

                    string uninstallLine;
                    // Non-empty when the command targets a file on the machine rather than one
                    // shipped in Files\, i.e. when it needs the Test-Path guard.
                    string uninstallGuardPath = "";
                    if (!string.IsNullOrWhiteSpace(manualUninstallSource))
                    {
                        string uName3  = Path.GetFileName(manualUninstallSource);
                        bool inFiles   = manualUninstallSource.StartsWith(filesDir, StringComparison.OrdinalIgnoreCase)
                                      || Directory.GetFiles(filesDir, uName3, SearchOption.TopDirectoryOnly).Any();
                        string uArgs3  = !string.IsNullOrWhiteSpace(manualUninstallArgs) ? manualUninstallArgs : uninstSwitch;
                        string uPart3  = string.IsNullOrEmpty(uArgs3) ? "" : $" -ArgumentList '{uArgs3}'";
                        if (inFiles)
                        {
                            uninstallLine = $"    Start-ADTProcess -FilePath \"{DR(uName3)}\"{uPart3} -WindowStyle \"Hidden\"";
                        }
                        else
                        {
                            uninstallGuardPath = ConvertToEnvSystemDrivePath(manualUninstallSource);
                            uninstallLine      = $"    Start-ADTProcess -FilePath \"{uninstallGuardPath}\"{uPart3} -WindowStyle \"Hidden\"";
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(uPath))
                    {
                        string psPath  = ConvertToEnvSystemDrivePath(uPath);
                        // Priority: SilentSwitches.json uninstall (uninstSwitch) → registry uninstall
                        // argument → /S. Curated JSON wins so a known app's uninstall matches its
                        // install rather than the partial switch the ARP UninstallString carries.
                        string finalUArg =
                            !string.IsNullOrWhiteSpace(uninstSwitch)  ? uninstSwitch :
                            !string.IsNullOrWhiteSpace(uRegistryArg)  ? uRegistryArg :
                            "/S";
                        string uArgPt  = string.IsNullOrEmpty(finalUArg) ? "" : $" -ArgumentList '{finalUArg.Trim()}'";
                        uninstallGuardPath = psPath;
                        uninstallLine  = $"    Start-ADTProcess -FilePath \"{psPath}\"{uArgPt} -WindowStyle \"Hidden\"";
                    }
                    else
                    {
                        uninstallLine = ""; // no uninstall found - skip
                    }
                    if (!string.IsNullOrEmpty(uninstallLine))
                    {
                        string uComment = !string.IsNullOrEmpty(appComment)
                            ? $"    # Uninstall {appComment.TrimStart('#', ' ').Trim()}"
                            : $"    # Uninstall {exeName}";
                        uninstallLines.Add(uComment);
                        AddGuardedUninstall(uninstallLines, uninstallGuardPath, uninstallLine);
                        uninstallLines.Add("");
                    }
                    log.Add($"EXE: {exeRel}");
                }
            }

            // ── ISS (InstallShield - paired EXE must have same base name) ─
            if (addIss)
            {
                foreach (var issPath in Collect("*.iss"))
                {
                    string issDir2   = Path.GetDirectoryName(issPath)!;
                    string issBase   = Path.GetFileNameWithoutExtension(issPath);
                    string pairedExe = Path.Combine(issDir2, issBase + ".exe");
                    if (!File.Exists(pairedExe)) continue;
                    string issRel  = Rel(issPath);
                    string exeRel2 = Rel(pairedExe);
                    string exeName2 = Path.GetFileName(pairedExe);

                    // Get metadata + uninstall info from the paired EXE
                    var (issComment, uPathIss, uArgIss, issReg) = ExeInfoCP(pairedExe);

                    // ── Install ───────────────────────────────────────────
                    string issBody = !string.IsNullOrEmpty(issComment)
                        ? issComment.TrimStart('#', ' ').Trim() : exeName2;
                    installLines.Add($"    # Install {issBody}");
                    installLines.Add($"    Start-ADTProcess -FilePath \"{DR(exeRel2)}\" -ArgumentList \"-s -f1`\"{DR(issRel)}`\"\" -WindowStyle \"Hidden\"");
                    installLines.Add("");

                    // ── Uninstall ─────────────────────────────────────────
                    // Only add when uninstall path was found in registry
                    if (!string.IsNullOrWhiteSpace(uPathIss))
                    {
                        var (_, issUninstSwitch) = MatchBuiltInSwitches(exeName2.ToLowerInvariant());
                        string finalUArgIss = !string.IsNullOrWhiteSpace(uArgIss) ? uArgIss : issUninstSwitch;
                        string uArgPartIss  = string.IsNullOrEmpty(finalUArgIss)
                            ? "" : $" -ArgumentList '{finalUArgIss.Trim()}'";
                        string psPathIss = ConvertToEnvSystemDrivePath(uPathIss);
                        uninstallLines.Add($"    # Uninstall {issBody}");
                        AddGuardedUninstall(uninstallLines, psPathIss,
                            $"    Start-ADTProcess -FilePath \"{psPathIss}\"{uArgPartIss} -WindowStyle \"Hidden\"");
                        uninstallLines.Add("");
                    }

                    log.Add($"ISS: {exeRel2} + {issRel}");
                }
            }

            // ── BAT ──────────────────────────────────────────────────────
            if (addBat)
            {
                foreach (var batPath in Collect("*.bat").Concat(Collect("*.cmd")))
                {
                    string batRel  = Rel(batPath);
                    string batName = Path.GetFileName(batPath);
                    installLines.Add($"    # Run {batName}");
                    installLines.Add($"    Start-ADTProcess -FilePath \"{DR(batRel)}\" -WindowStyle \"Hidden\"");
                    installLines.Add("");
                    log.Add($"BAT: {batRel}");
                }
            }

            // ── PS1 ──────────────────────────────────────────────────────
            if (addPs1)
            {
                foreach (var ps1Path in Collect("*.ps1"))
                {
                    string ps1Rel  = Rel(ps1Path);
                    string ps1Name = Path.GetFileName(ps1Path);
                    installLines.Add($"    # Run {ps1Name}");
                    installLines.Add($"    Start-ADTProcess -FilePath \"$envSystemDrive\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -ArgumentList \"-ExecutionPolicy Bypass -NoProfile -File `\"{DR(ps1Rel)}`\"\" -WindowStyle \"Hidden\"");
                    installLines.Add("");
                    log.Add($"PS1: {ps1Rel}");
                }
            }

            // ── REG ──────────────────────────────────────────────────────
            if (addReg)
            {
                foreach (var regPath in Collect("*.reg"))
                {
                    string regRel  = Rel(regPath);
                    string regName = Path.GetFileName(regPath);
                    installLines.Add($"    # Run {regName}");
                    installLines.Add($"    Start-ADTProcess -FilePath \"regedit.exe\" -ArgumentList \"/s `\"$($adtSession.DirFiles)\\{regRel}`\"\" -WindowStyle \"Hidden\"");
                    installLines.Add("");
                    log.Add($"REG: {regRel}");
                }
            }

            // ── CER ──────────────────────────────────────────────────────
            if (addCer)
            {
                foreach (var cerPath in Collect("*.cer"))
                {
                    string cerRel     = Rel(cerPath);
                    string cerName    = Path.GetFileName(cerPath);
                    string certLabel  = cerName;   // default: filename
                    string certSerial = "";
                    bool   certInstalledInStore = false;

                    try
                    {
                        var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(cerPath);
                        string cn = cert.GetNameInfo(
                            System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                        certSerial = cert.SerialNumber ?? "";

                        // Check if cert is actually installed in the machine TrustedPublisher store
                        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                            "TrustedPublisher",
                            System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                        var found = store.Certificates.Find(
                            System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                            cert.Thumbprint, false);

                        certInstalledInStore = found.Count > 0;

                        // Use CN as label only when cert is installed (then we know the real name)
                        if (certInstalledInStore && !string.IsNullOrWhiteSpace(cn))
                            certLabel = cn;
                        else if (!string.IsNullOrWhiteSpace(cn))
                            certLabel = cn; // use CN from file even if not installed
                    }
                    catch { }

                    // Install: comment uses cert name or filename; path has NO backtick-quotes per spec
                    installLines.Add($"    # Install {certLabel}");
                    installLines.Add($"    Start-ADTProcess -FilePath \"certutil.exe\" -ArgumentList \"-addstore TrustedPublisher `\"$($adtSession.DirFiles)\\{cerRel}`\"\" -WindowStyle \"Hidden\"");
                    installLines.Add("");

                    // Uninstall: ONLY if cert is installed in the machine store AND serial is known
                    if (certInstalledInStore && !string.IsNullOrWhiteSpace(certSerial))
                    {
                        uninstallLines.Add($"    # Uninstall {certLabel}");
                        uninstallLines.Add($"    Start-ADTProcess -FilePath \"certutil.exe\" -ArgumentList \"-delstore TrustedPublisher {certSerial}\" -WindowStyle \"Hidden\"");
                        uninstallLines.Add("");
                    }
                    // If cert is NOT installed on this machine, no uninstall command is added

                    log.Add($"CER: {cerRel}{(certInstalledInStore ? " (installed - uninstall added)" : " (not installed - uninstall skipped)")}");
                }
            }

            // ── INF ──────────────────────────────────────────────────────
            if (addInf)
            {
                foreach (var infPath in Collect("*.inf"))
                {
                    string infRel  = Rel(infPath);
                    string infName = Path.GetFileName(infPath);
                    installLines.Add($"    # Install {infName}");
                    installLines.Add($"    Start-ADTProcess -FilePath \"pnputil.exe\" -ArgumentList \"/add-driver `\"{DR(infRel)}`\" /install /force\" -WindowStyle \"Hidden\" -IgnoreExitCodes '512'");
                    installLines.Add("");
                    uninstallLines.Add($"    # Uninstall {infName}");
                    uninstallLines.Add($"    Start-ADTProcess -FilePath \"pnputil.exe\" -ArgumentList \"/delete-driver `\"{DR(infRel)}`\" /uninstall /force\" -WindowStyle \"Hidden\"");
                    uninstallLines.Add("");
                    log.Add($"INF: {infRel}");
                }
            }

            // ── Collect uninstall registry key paths (for ARP flag injection) ──────
            // Key = registry path, Value = per-app display label for the comment line
            //
            // EXE ONLY. MSI files are deliberately NOT harvested here: msiexec owns its own
            // Add/Remove Programs entry and already exposes the correct Modify/Repair/Remove
            // buttons, so stamping NoModify/NoRepair/NoRemove onto an MSI's uninstall key is
            // both unnecessary and wrong. Previously this loop also scanned *.msi, so whenever
            // an MSI app happened to be installed on the build machine its uninstall key was
            // picked up and got ARP flags alongside the EXE app.
            // The live drag/drop path already behaves this way: ApplyFilesExeSnippet (.exe/.iss)
            // injects ARP flags, ApplyMsiSnippet (.msi/.mst) does not.
            var uninstallRegKeyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Collect from EXE files - use FileVersionInfo as hint for better registry matching
            foreach (string ef2 in Directory.GetFiles(filesDir, "*.exe", SearchOption.AllDirectories))
            {
                try
                {
                    // Build productNameHint from FileVersionInfo (same priority as InsertFilesInstallLine)
                    string fviHint = "";
                    try
                    {
                        var vi2 = System.Diagnostics.FileVersionInfo.GetVersionInfo(ef2);
                        fviHint = vi2.ProductName?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(fviHint)) fviHint = vi2.FileDescription?.Trim() ?? "";
                    }
                    catch { }
                    // Fall back to camelCase-split filename when FVI is empty
                    if (string.IsNullOrWhiteSpace(fviHint))
                        fviHint = Path.GetFileNameWithoutExtension(ef2);

                    var regInfo = LookupInstalledByExe(ef2, fviHint);
                    if (regInfo != null && !string.IsNullOrEmpty(regInfo.RegKeyPath)
                        && !uninstallRegKeyMap.ContainsKey(regInfo.RegKeyPath))
                    {
                        string label = string.Join(" ", new[] { regInfo.Publisher, regInfo.DisplayName, regInfo.DisplayVersion }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        uninstallRegKeyMap[regInfo.RegKeyPath] = label;
                    }
                }
                catch { }
            }

            // ── Fallback: extract paths from script text when app not installed on this machine ────
            // Covers all three hive variants:
            //   HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\
            //   HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\
            //   HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\
            // Always scan the existing script for uninstall registry key paths so that
            // apps already patched into the script (MSI or EXE) are tracked, enabling
            // the per-app ARP guard to correctly skip only already-present apps.
            {
                var hkeyMatches = System.Text.RegularExpressions.Regex.Matches(
                    txt,
                    @"(?:HKEY_LOCAL_MACHINE\\SOFTWARE(?:\\WOW6432Node)?|HKEY_CURRENT_USER\\Software)\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\[^\x22\r\n]+",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in hkeyMatches)
                {
                    string kp = m.Value.Trim();
                    if (!uninstallRegKeyMap.ContainsKey(kp))
                        uninstallRegKeyMap[kp] = ""; // path tracked; no label from script text
                }
            }

            // Back-compat: keep a flat list for any code that still references uninstallRegKeyPaths
            var uninstallRegKeyPaths = uninstallRegKeyMap.Keys.ToList();

            // ── Inject using section-aware guards ────────────────────
            bool changed = false;

            // Trim trailing blank lines then keep exactly one, so each injected block ends cleanly
            static void NormaliseTrailingBlank(List<string> lst)
            {
                while (lst.Count > 0 && lst[lst.Count - 1] == "") lst.RemoveAt(lst.Count - 1);
                lst.Add("");
            }
            NormaliseTrailingBlank(installLines);
            NormaliseTrailingBlank(uninstallLines);

            bool installHasCmd = SectionContains(txt, INSTALL_MARKER, "Start-ADTMsiProcess -Action 'Install'")
                              || SectionContains(txt, INSTALL_MARKER, "Start-ADTProcess -FilePath");
            if (!installHasCmd && installLines.Count > 0)
            {
                txt = InjectLinesAfterMarker(txt, INSTALL_MARKER, installLines);
                changed = true;
                log.Add($"Injected {installLines.Count} Install line(s)");
            }
            else
                log.Add("Install commands already present - skipped");

            bool uninstallHasCmd = SectionContains(txt, UNINSTALL_MARKER, "Start-ADTMsiProcess -Action 'Uninstall'")
                                || SectionContains(txt, UNINSTALL_MARKER, "Start-ADTProcess -FilePath");
            if (!uninstallHasCmd && uninstallLines.Count > 0)
            {
                txt = InjectLinesAfterMarker(txt, UNINSTALL_MARKER, uninstallLines);
                changed = true;
                log.Add($"Injected {uninstallLines.Count} Uninstall line(s)");
            }
            else
                log.Add("Uninstall commands already present - skipped");

            // ── ARP Flags (NoModify / NoRepair / NoRemove) ────────────────
            // Only inject when the JSON config explicitly sets each flag to "Yes" or "true".
            bool addNoModify = CreatePackageConfig.FlagStrict(cfg?.AddNoModify);
            bool addNoRepair = CreatePackageConfig.FlagStrict(cfg?.AddNoRepair);
            bool addNoRemove = CreatePackageConfig.FlagStrict(cfg?.AddNoRemove);

            if ((addNoModify || addNoRepair || addNoRemove) && uninstallRegKeyPaths.Count > 0)
            {
                // Fallback label from cfg app identity (used when registry lookup has no label)
                string arpLabel = string.Join(" ", new[] { cfg?.AppVendor, cfg?.AppName, cfg?.AppVersion }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                var arpLines = new List<string>();

                foreach (var kvp in uninstallRegKeyMap)
                {
                    string regKeyPath = kvp.Key;

                    // Normalise to full HKEY_... form
                    string litPath = regKeyPath
                        .Replace("HKLM:\\", "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                        .Replace("HKCU:\\", "HKEY_CURRENT_USER\\",  StringComparison.OrdinalIgnoreCase)
                        .Replace("HKLM\\",  "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                        .Replace("HKCU\\",  "HKEY_CURRENT_USER\\",  StringComparison.OrdinalIgnoreCase);

                    // Try to open the live registry key to check existing values.
                    // If the app is not yet installed on THIS machine, liveKey == null - that is fine;
                    // we still inject the commands so they run on the TARGET machine after install.
                    Microsoft.Win32.RegistryKey? liveKey = null;
                    try
                    {
                        string[] pts  = litPath.Split('\\');
                        string   subK = string.Join("\\", pts.Skip(1));
                        var hive = pts[0].Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
                                   ? Microsoft.Win32.Registry.LocalMachine
                                   : Microsoft.Win32.Registry.CurrentUser;
                        liveKey = hive.OpenSubKey(subK);
                    }
                    catch { }

                    // Collect Set-ADTRegistryKey lines for this app first;
                    // only prepend the comment if at least one line was actually produced.
                    var appArpLines = new List<string>();

                    if (addNoModify)
                    {
                        bool alreadySet = liveKey?.GetValue("NoModify") != null;
                        if (!alreadySet)
                            appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoModify\" -Value \"1\" -Type \"DWord\"");
                        else
                            log.Add($"NoModify already set in registry for {litPath} - skipped");
                    }
                    if (addNoRepair)
                    {
                        bool alreadySet = liveKey?.GetValue("NoRepair") != null;
                        if (!alreadySet)
                            appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoRepair\" -Value \"1\" -Type \"DWord\"");
                        else
                            log.Add($"NoRepair already set in registry for {litPath} - skipped");
                    }
                    if (addNoRemove)
                    {
                        bool alreadySet = liveKey?.GetValue("NoRemove") != null;
                        if (!alreadySet)
                            appArpLines.Add($"    Set-ADTRegistryKey -LiteralPath \"{litPath}\" -Name \"NoRemove\" -Value \"1\" -Type \"DWord\"");
                        else
                            log.Add($"NoRemove already set in registry for {litPath} - skipped");
                    }

                    liveKey?.Dispose();

                    // Only add comment + lines if there is something to inject for this app
                    if (appArpLines.Count > 0)
                    {
                        string perAppLabel = kvp.Value;
                        if (string.IsNullOrWhiteSpace(perAppLabel))
                            perAppLabel = arpLabel;
                        string perAppComment = string.IsNullOrWhiteSpace(perAppLabel)
                            ? "# ARP flags - prevent Modify/Repair/Remove from Add/Remove Programs"
                            : $"# ARP flags - {perAppLabel}";
                        arpLines.Add("    " + perAppComment);
                        arpLines.AddRange(appArpLines);
                        arpLines.Add(""); // blank line between apps
                    }
                }

                // Remove any trailing blank lines, then add exactly one
                while (arpLines.Count > 0 && arpLines[arpLines.Count - 1] == "")
                    arpLines.RemoveAt(arpLines.Count - 1);
                arpLines.Add("");

                // Only inject if we have at least one Set-ADTRegistryKey line.
                // Filter out lines for registry key paths already present in the script -
                // this allows partial injection (e.g. MSI ARP already there, EXE still needs it).
                if (arpLines.Count > 1)
                {
                    // Remove per-app blocks whose registry key path is already in the script,
                    // keeping blocks for apps not yet present.
                    var filteredArpLines = new List<string>();
                    var blockComment  = new List<string>();
                    var blockSetLines = new List<string>();

                    void FlushBlock()
                    {
                        if (blockSetLines.Count == 0) { blockComment.Clear(); return; }
                        // Check if ANY Set-ADTRegistryKey in this block references a path already in script
                        bool allPresent = blockSetLines.All(l =>
                        {
                            // Extract the LiteralPath value from the Set-ADTRegistryKey line
                            var m = System.Text.RegularExpressions.Regex.Match(
                                l, @"-LiteralPath\s+""([^""]+)""",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            return m.Success && txt.Contains(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
                        });
                        if (!allPresent)
                        {
                            filteredArpLines.AddRange(blockComment);
                            filteredArpLines.AddRange(blockSetLines);
                            filteredArpLines.Add("");
                        }
                        blockComment.Clear();
                        blockSetLines.Clear();
                    }

                    foreach (var line in arpLines)
                    {
                        if (line.TrimStart().StartsWith("# ARP flags", StringComparison.OrdinalIgnoreCase))
                        {
                            FlushBlock();
                            blockComment.Add(line);
                        }
                        else if (line.TrimStart().StartsWith("Set-ADTRegistryKey", StringComparison.OrdinalIgnoreCase))
                        {
                            blockSetLines.Add(line);
                        }
                        // blank lines between blocks are re-added by FlushBlock
                    }
                    FlushBlock();

                    // Trim trailing blanks then add exactly one
                    while (filteredArpLines.Count > 0 && filteredArpLines[filteredArpLines.Count - 1] == "")
                        filteredArpLines.RemoveAt(filteredArpLines.Count - 1);
                    if (filteredArpLines.Count > 0)
                    {
                        filteredArpLines.Add("");
                        txt = InjectLinesAfterMarker(txt, POST_MARKER, filteredArpLines);
                        changed = true;
                        log.Add($"Injected ARP flag command(s) after Post-Installation marker");
                    }
                    else
                        log.Add("ARP flags already present in script for all apps - skipped");
                }
            }
            // ── AppProcessesToClose ───────────────────────────────────────
            // Patch AppProcessesToClose whenever installed apps were detected,
            // regardless of whether install/uninstall commands were also injected.
            if (addCloseApps && closeAppsList.Count > 0)
            {
                string procsVal = "@('" + string.Join("', '", closeAppsList) + "')";
                // Replace ANY existing AppProcessesToClose value - mirrors AutoPackage PatchSessionParam:
                // matches the whole line regardless of current value (@(), @('x'), etc.)
                string patched = Regex.Replace(
                    txt,
                    @"(?m)^(\s*AppProcessesToClose\s*=\s*).*$",
                    m => m.Groups[1].Value + procsVal,
                    RegexOptions.IgnoreCase);
                if (patched != txt)
                {
                    txt = patched;
                    changed = true;
                    log.Add($"AppProcessesToClose = {procsVal}");
                }
            }

            if (changed)
                File.WriteAllText(scriptPath, txt, new UTF8Encoding(true));

            return log;
        }

        private static bool SectionContains(string content, string marker, string searchText)
        {
            int idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            int end = Math.Min(idx + 500, content.Length);
            return content.Substring(idx, end - idx)
                          .Contains(searchText, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Injects <paramref name="lineToInject"/> on the line immediately after
        /// the first line that contains <paramref name="marker"/>.
        /// Uses line-by-line split to handle any line ending (same as AutoPackage).
        /// </summary>
        private static string InjectAfterMarker(string content, string marker, string lineToInject)
        {
            var lines = content.Split('\n').ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Insert(i + 1, lineToInject);
                    return string.Join("\n", lines);
                }
            }
            return content; // marker not found - return unchanged
        }

        /// <summary>
        /// Inserts all <paramref name="linesToInject"/> immediately after the first line
        /// that contains <paramref name="marker"/>, preserving their original order.
        /// Calling this once for the whole block avoids the reversal that occurs when
        /// InjectAfterMarker is called in a loop (each call re-inserts at position i+1).
        /// </summary>

        /// <summary>
        /// Inserts the install comment + command at <paramref name="dropLine"/> in the script,
        /// adding a blank line before the comment (if the preceding line is not blank) and
        /// after the command (if the following line is not blank).
        /// If <paramref name="dropLine"/> is before or on the install marker line, falls back
        /// to inserting immediately after the marker (same as InjectLinesAfterMarker).
        /// </summary>
        private static string InjectAtDropLine(
            string content, int dropLine, string installMarker,
            string comment, string cmd)
        {
            var lines = content.Split('\n').ToList();

            // Always insert at the exact line the user dropped/pasted on.
            // Only fall back to the Install marker when dropLine is out of range.
            int insertAfter = (dropLine >= 0 && dropLine < lines.Count)
                              ? dropLine
                              : lines.Count - 1;

            // If we land exactly on the Install marker line, remove the blank
            // template lines immediately after it so spacing is clean.
            if (lines[insertAfter].Contains(installMarker, StringComparison.OrdinalIgnoreCase))
            {
                while (insertAfter + 1 < lines.Count && lines[insertAfter + 1].Trim() == "")
                    lines.RemoveAt(insertAfter + 1);
            }

            // ── Collapse consecutive blank lines above the drop point ─────────
            // If the user drops on a blank line and the line before it is also blank,
            // step back so the existing single blank serves as spacing - prevents
            // double-blank above the comment (e.g. lines 144=blank, 145=blank → step to 144).
            while (insertAfter > 0
                   && lines[insertAfter].Trim() == ""
                   && lines[insertAfter - 1].Trim() == "")
            {
                insertAfter--;
            }

            // Build snippet: only add blank lines where none already exist.
            // blank-before → drop line is non-blank (no existing gap above comment)
            // blank-after  → line after drop is non-blank (no existing gap below command)
            var snippet = new List<string>();

            bool dropLineIsBlank = lines[insertAfter].Trim() == "";
            bool nextLineIsBlank = (insertAfter + 1 < lines.Count)
                                   && lines[insertAfter + 1].Trim() == "";

            if (!dropLineIsBlank) snippet.Add("");   // blank before comment
            snippet.Add(comment);
            snippet.Add(cmd);
            if (!nextLineIsBlank) snippet.Add("");   // blank after command

            // Insert in reverse order so indices stay correct
            for (int j = snippet.Count - 1; j >= 0; j--)
                lines.Insert(insertAfter + 1, snippet[j]);

            return string.Join("\n", lines);
        }

        private static string InjectLinesAfterMarker(string content, string marker, IList<string> linesToInject)
        {
            if (linesToInject.Count == 0) return content;
            var lines = content.Split('\n').ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    // Remove blank lines that immediately follow the marker (template spacing)
                    while (i + 1 < lines.Count && lines[i + 1].Trim() == "")
                        lines.RemoveAt(i + 1);

                    // Always insert one blank line between the marker and the injected content.
                    // linesToInject already ends with a trailing "" for spacing after the block.
                    var toInsert = new List<string> { "" };
                    toInsert.AddRange(linesToInject);

                    // Insert in reverse so list order is preserved
                    for (int j = toInsert.Count - 1; j >= 0; j--)
                        lines.Insert(i + 1, toInsert[j]);
                    return string.Join("\n", lines);
                }
            }
            return content; // marker not found - return unchanged
        }

                private static string GetRelativePath(string basePath, string filePath)
        {
            string dir = Path.GetDirectoryName(filePath) ?? basePath;
            if (string.Equals(dir, basePath, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(filePath);
            try
            {
                string rel = System.IO.Path.GetRelativePath(basePath, filePath);
                return rel;
            }
            catch { return Path.GetFileName(filePath); }
        }

        /// <summary>Reads ProductCode from an MSI file using Windows Installer COM (STA thread).</summary>
        private static string ReadMsiProductCode(string msiPath)
        {
            string code = "";
            var thread = new Thread(() =>
            {
                object? inst = null, db = null, vw = null;
                try
                {
                    Type t = Type.GetTypeFromProgID("WindowsInstaller.Installer")!;
                    inst = Activator.CreateInstance(t)!;
                    db   = t.InvokeMember("OpenDatabase", BindingFlags.InvokeMethod, null, inst, new object[] { msiPath, 0 });
                    var dt = db!.GetType();
                    vw   = dt.InvokeMember("OpenView", BindingFlags.InvokeMethod, null, db,
                               new object[] { "SELECT Value FROM Property WHERE Property='ProductCode'" });
                    var vt = vw!.GetType();
                    vt.InvokeMember("Execute", BindingFlags.InvokeMethod, null, vw, null);
                    object? rec = vt.InvokeMember("Fetch", BindingFlags.InvokeMethod, null, vw, null);
                    if (rec != null)
                    {
                        var rt = rec.GetType();
                        code = rt.InvokeMember("StringData", BindingFlags.GetProperty, null, rec,
                                          new object[] { 1 }) as string ?? "";
                        code = code.Trim();
                        Marshal.ReleaseComObject(rec);
                    }
                }
                catch { }
                finally
                {
                    if (vw   != null) try { Marshal.ReleaseComObject(vw);   } catch { }
                    if (db   != null) try { Marshal.ReleaseComObject(db);   } catch { }
                    if (inst != null) try { Marshal.ReleaseComObject(inst); } catch { }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            return code;
        }

        /// <summary>
        /// Reads ProductCode, Manufacturer, ProductName, ProductVersion from an MSI
        /// in a SINGLE Windows Installer COM session to avoid the double-open lock issue.
        /// Returns empty strings on any failure.
        /// </summary>
        private static (string productCode, string manufacturer, string productName, string productVersion)
            ReadMsiAllProperties(string msiPath)
        {
            // Windows Installer COM requires STA apartment threading.
            // PatchInstallCommands runs on a ThreadPool (MTA) thread, so we spin up
            // a dedicated STA thread to do all COM work and block until it finishes.
            string pc = "", mfr = "", name = "", ver = "";
            var thread = new Thread(() =>
            {
                object? inst = null, db = null, vw = null;
                try
                {
                    Type t = Type.GetTypeFromProgID("WindowsInstaller.Installer")!;
                    inst = Activator.CreateInstance(t)!;
                    db   = t.InvokeMember("OpenDatabase", BindingFlags.InvokeMethod, null, inst,
                               new object[] { msiPath, 0 });
                    var dt = db!.GetType();
                    vw = dt.InvokeMember("OpenView", BindingFlags.InvokeMethod, null, db,
                             new object[] { "SELECT Property,Value FROM Property" });
                    var vt = vw!.GetType();
                    vt.InvokeMember("Execute", BindingFlags.InvokeMethod, null, vw, null);
                    while (true)
                    {
                        object? rec = vt.InvokeMember("Fetch", BindingFlags.InvokeMethod, null, vw, null);
                        if (rec == null) break;
                        var rt = rec.GetType();
                        string k = rt.InvokeMember("StringData", BindingFlags.GetProperty, null, rec, new object[] { 1 }) as string ?? "";
                        string v = rt.InvokeMember("StringData", BindingFlags.GetProperty, null, rec, new object[] { 2 }) as string ?? "";
                        Marshal.ReleaseComObject(rec);
                        // Filter the four properties we need in C# - MSI SQL has no IN operator
                        if (k == "ProductCode"    && pc   == "") pc   = v.Trim();
                        if (k == "Manufacturer"   && mfr  == "") mfr  = v.Trim();
                        if (k == "ProductName"    && name == "") name = v.Trim();
                        if (k == "ProductVersion" && ver  == "") ver  = v.Trim();
                        // Early exit once all four found
                        if (pc != "" && mfr != "" && name != "" && ver != "") break;
                    }
                }
                catch { }
                finally
                {
                    if (vw   != null) try { Marshal.ReleaseComObject(vw);   } catch { }
                    if (db   != null) try { Marshal.ReleaseComObject(db);   } catch { }
                    if (inst != null) try { Marshal.ReleaseComObject(inst); } catch { }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            return (pc, mfr, name, ver);
        }

        /// <summary>
        /// Reads Manufacturer, ProductName, ProductVersion from an MSI Property table.
        /// Returns ("","","") on any failure.
        /// </summary>
        private static (string manufacturer, string productName, string productVersion) ReadMsiProperties(string msiPath)
        {
            string mfr = "", name = "", ver = "";
            object? inst = null, db = null, vw = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WindowsInstaller.Installer")!;
                inst = Activator.CreateInstance(t)!;
                db   = t.InvokeMember("OpenDatabase", BindingFlags.InvokeMethod, null, inst, new object[] { msiPath, 0 });
                var dt = db!.GetType();
                vw   = dt.InvokeMember("OpenView", BindingFlags.InvokeMethod, null, db,
                           new object[] { "SELECT Property,Value FROM Property" });
                var vt = vw!.GetType();
                vt.InvokeMember("Execute", BindingFlags.InvokeMethod, null, vw, null);
                while (true)
                {
                    object? rec = vt.InvokeMember("Fetch", BindingFlags.InvokeMethod, null, vw, null);
                    if (rec == null) break;
                    var rt  = rec.GetType();
                    string k = rt.InvokeMember("StringData", BindingFlags.GetProperty, null, rec, new object[] { 1 }) as string ?? "";
                    string v = rt.InvokeMember("StringData", BindingFlags.GetProperty, null, rec, new object[] { 2 }) as string ?? "";
                    Marshal.ReleaseComObject(rec);
                    if (k == "Manufacturer"   && mfr  == "") mfr  = v.Trim();
                    if (k == "ProductName"    && name == "") name = v.Trim();
                    if (k == "ProductVersion" && ver  == "") ver  = v.Trim();
                    if (mfr != "" && name != "" && ver != "") break;
                }
            }
            catch { }
            finally
            {
                if (vw   != null) try { Marshal.ReleaseComObject(vw);   } catch { }
                if (db   != null) try { Marshal.ReleaseComObject(db);   } catch { }
                if (inst != null) try { Marshal.ReleaseComObject(inst); } catch { }
            }
            return (mfr, name, ver);
        }

        /// <summary>
        /// Builds a PS comment string "# Publisher ProductName Version".
        /// Registry values take priority over MSI-table values.
        /// If the productCode is set, registry lookup is attempted first.
        /// Returns "" if no useful info is available.
        /// </summary>
        private static string BuildAppComment(
            string productCode,
            string msiMfr, string msiProd, string msiVer)
        {
            string publisher = "", displayName = "", displayVersion = "";

            // Priority 1: look up the installed app from the registry using the ProductCode
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                var hives = new (Microsoft.Win32.RegistryKey root, string sub)[]
                {
                    (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                    (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                    (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                };
                foreach (var (root, sub) in hives)
                {
                    try
                    {
                        using var key = root.OpenSubKey(sub + "\\" + productCode);
                        if (key == null) continue;
                        publisher      = key.GetValue("Publisher")      as string ?? "";
                        displayName    = key.GetValue("DisplayName")    as string ?? "";
                        displayVersion = key.GetValue("DisplayVersion") as string ?? "";
                        if (!string.IsNullOrWhiteSpace(displayName)) break;
                    }
                    catch { }
                }
            }

            // Priority 2: fall back to MSI Property table values
            if (string.IsNullOrWhiteSpace(publisher))    publisher      = msiMfr;
            if (string.IsNullOrWhiteSpace(displayName))  displayName    = msiProd;
            if (string.IsNullOrWhiteSpace(displayVersion)) displayVersion = msiVer;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(publisher))      parts.Add(publisher);
            if (!string.IsNullOrWhiteSpace(displayName))    parts.Add(displayName);
            if (!string.IsNullOrWhiteSpace(displayVersion)) parts.Add(displayVersion);

            return parts.Count > 0 ? "# " + string.Join(" ", parts) : "";
        }

        // ══════════════════════════════════════════════════════════════
        // SCRIPT TAB - LOAD SCRIPT
        // ══════════════════════════════════════════════════════════════
        private void LoadScriptIntoEditor(string scriptPath)
        {
            if (!File.Exists(scriptPath)) return;
            string text = File.ReadAllText(scriptPath, Encoding.UTF8)
                              .Replace("\r\n", "\n").Replace("\r", "\n"); // normalise to \n
            _flushingToScript = true;
            try
            {
                TxtScript.IsReadOnly = false;
                TxtScript.Text       = text;
                TxtScript.IsReadOnly = true;
            }
            finally { _flushingToScript = false; }
            TxtScriptPath.Text    = scriptPath;
            TxtScriptFileName.Text = Path.GetFileName(scriptPath);
            _scriptDirty = false;
            TxtScriptDirty.Visibility = Visibility.Collapsed;
            PanelNoScript.Visibility  = Visibility.Collapsed;
            _savedSelStartLine = 0; _savedSelEndLine = 0;
            _arrowAnchorLine   = -1; _dragLine       = -1;
            UpdateLineNumbers();
            HighlightCurrentLine();
        }

        // ══════════════════════════════════════════════════════════════
        // SCRIPT TAB - COMMAND SEARCH
        // ══════════════════════════════════════════════════════════════
        private void TxtCmdSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = TxtCmdSearch.Text;
            BtnClearCmdSearch.Visibility = string.IsNullOrEmpty(q) ? Visibility.Collapsed : Visibility.Visible;
            if (TxtCmdSearchWatermark != null)
                TxtCmdSearchWatermark.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;
            ApplyCmdFilter(q);
        }

        private void TxtCmdSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyCmdFilter(TxtCmdSearch.Text);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                TxtCmdSearch.Clear();
                e.Handled = true;
            }
        }

        private void BtnClearCmdSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtCmdSearch.Clear();
            BtnClearCmdSearch.Visibility = Visibility.Collapsed;
            if (TxtCmdSearchWatermark != null) TxtCmdSearchWatermark.Visibility = Visibility.Visible;
        }

        private void ApplyCmdFilter(string q)
        {
            q = q.Trim();
            _filteredFuncs = string.IsNullOrEmpty(q)
                ? _allFunctions.ToList()
                : _allFunctions.Where(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                           f.Synopsis.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            LstCommands.ItemsSource = null;
            LstCommands.ItemsSource = _filteredFuncs;
        }

        // ══════════════════════════════════════════════════════════════
        // SCRIPT TAB - COMMAND SELECTION → BUILD PARAM PANEL
        // ══════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════
        // AUTO-SELECT COMMAND FROM CLICKED SCRIPT LINE
        // ══════════════════════════════════════════════════════════════
        /// <summary>
        /// Reads the line at the current caret position, extracts the first token,
        /// and if it matches a known ADT function name, selects it in LstCommands
        /// and rebuilds the parameter panel - without triggering a recursive loop.
        /// </summary>
        private void SelectAddPSScript(int lineIdx, string content)
        {
            var psFn = _allFunctions.FirstOrDefault(
                f => string.Equals(f.Name, "Add-PSScript", StringComparison.OrdinalIgnoreCase));
            if (psFn == null) { ClearParamPanel(); return; }

            // Only sync back to the script line if the line already has content.
            // For empty lines, _syncLineIndex = -1 so typing in the panel does NOT
            // immediately write to the script - the user must click Add explicitly.
            _syncLineIndex = string.IsNullOrWhiteSpace(content) ? -1 : lineIdx;

            bool alreadySel = LstCommands.SelectedItem is AdtFunction af &&
                string.Equals(af.Name, "Add-PSScript", StringComparison.OrdinalIgnoreCase);
            string capturedContent = content;
            void PopulateScriptBlock()
            {
                if (_paramControls.TryGetValue("ScriptBlock", out var ctrl) && ctrl is TextBox sbtb)
                {
                    _syncingParams = true;
                    sbtb.Text = capturedContent;
                    _syncingParams = false;
                }
            }
            if (!alreadySel)
            {
                LstCommands.SelectedItem = psFn;
                LstCommands.ScrollIntoView(psFn);
                Dispatcher.InvokeAsync(PopulateScriptBlock, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                TxtParamHeader.Text = psFn.Name;
                TxtParamHeader.SetResourceReference(TextBlock.ForegroundProperty, "NavIconIntuneFgBrush");
                TxtParamPlaceholder.Visibility = Visibility.Collapsed;
                PopulateScriptBlock();
            }
        }

        private void TrySelectCommandFromCurrentLine()
        {
            // Don't re-parse while we are writing to TxtScript ourselves -
            // that would overwrite the param controls the user is editing.
            if (_flushingToScript) return;
            if (TxtScript == null || LstCommands == null) return;

            try
            {
                int caretIdx = TxtScript.CaretIndex;
                string text  = TxtScript.Text;
                if (string.IsNullOrEmpty(text)) return;

                // Get the full text of the line containing the caret
                int lineIdx   = TxtScript.GetLineIndexFromCharacterIndex(caretIdx);
                int lineStart = TxtScript.GetCharacterIndexFromLineIndex(lineIdx);
                int lineEnd   = (lineIdx + 1 < TxtScript.LineCount)
                                ? TxtScript.GetCharacterIndexFromLineIndex(lineIdx + 1)
                                : text.Length;
                string line = text.Substring(lineStart, Math.Max(0, lineEnd - lineStart)).Trim();

                // Strip leading comment markers and whitespace
                line = line.TrimStart('#', ' ', '\t');
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (_resetPanelOnNextBlank && _selectedFunc != null)
                    {
                        _resetPanelOnNextBlank = false;
                        BuildParamPanel(_selectedFunc);  // reset to blank, keep command
                        return;
                    }
                    // 4. Blank line → select Add-PSScript with empty text box
                    SelectAddPSScript(lineIdx, "");
                    return;
                }

                // Save the raw line (with any $var= prefix) for ParseAndPopulateParamsFromLine
                // which needs it to correctly populate the variable name TextBox.
                string rawLine = line;

                // Strip leading variable assignment: $varName = CommandName ...
                var varPfxMatch = System.Text.RegularExpressions.Regex.Match(
                    line, @"^\$[\w]+\s*=\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (varPfxMatch.Success)
                    line = line.Substring(varPfxMatch.Length).TrimStart();

                // The first whitespace-delimited token is the command name
                // (handles both "Add-ADTFont -Path ..." and "$app = Add-ADTFont ...")
                string token = line.Split(new[] { ' ', '\t', '\r', '\n' },
                                          StringSplitOptions.RemoveEmptyEntries)[0].Trim('"', '\'');

                // Look for a case-insensitive match in the full function list
                var match = _allFunctions.FirstOrDefault(
                    f => string.Equals(f.Name, token, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    // 5. Non-command text line → select Add-PSScript and populate TextBox with the raw line
                    SelectAddPSScript(lineIdx, rawLine);
                    return;
                }

                // If the command list is currently filtered and the match isn't visible,
                // clear the search filter first so it becomes visible
                if (!_filteredFuncs.Any(f => string.Equals(f.Name, match.Name,
                                             StringComparison.OrdinalIgnoreCase)))
                {
                    if (TxtCmdSearch != null)
                        TxtCmdSearch.Clear();       // triggers ApplyCmdFilter("") via TextChanged
                }

                // Always update the sync line index - the user may have clicked the
                // same command on a DIFFERENT line with different param values.
                _syncLineIndex = lineIdx;

                // Select in list (if already selected, LstCommands_SelectionChanged
                // won't fire, so we always call ParseAndPopulateParamsFromLine directly)
                bool alreadySelected = LstCommands.SelectedItem is AdtFunction already &&
                    string.Equals(already.Name, match.Name, StringComparison.OrdinalIgnoreCase);

                if (!alreadySelected)
                {
                    LstCommands.SelectedItem = match;   // fires SelectionChanged → BuildParamPanel
                    LstCommands.ScrollIntoView(match);
                }

                // Always populate - pass rawLine so variable name box gets correctly filled
                ParseAndPopulateParamsFromLine(rawLine);
            }
            catch { /* best-effort - never crash on a click */ }
        }

        /// <summary>
        /// Clears the param panel header, controls, and resets sync state.
        /// Called whenever the caret moves to a line that is NOT a known command.
        /// </summary>
        private void ClearParamPanel()
        {
            _syncingParams  = true;   // suppress any lingering change handlers
            _syncLineIndex  = -1;
            _selectedFunc   = null;
            LstCommands.SelectedItem = null;
            _paramControls.Clear();
            // Restore ScrollViewer if Add-PSScript had replaced its content
            if (_col2ScrollViewer != null && !ReferenceEquals(_col2ScrollViewer.Content, PanelParams))
            {
                _col2ScrollViewer.Content = PanelParams;
                _col2ScrollViewer.VerticalScrollBarVisibility   = ScrollBarVisibility.Auto;
                _col2ScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            _varNameControl = null;
            // Keep TxtParamHeader and TxtParamPlaceholder (first 2 children) - remove param controls only
            if (PanelParams != null)
            {
                while (PanelParams.Children.Count > 2)
                    PanelParams.Children.RemoveAt(PanelParams.Children.Count - 1);
            }
            if (TxtParamHeader != null)
            {
                TxtParamHeader.Text       = "← Select a command from the left panel.";
                TxtParamHeader.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            }
            if (TxtParamPlaceholder != null)
                TxtParamPlaceholder.Visibility = Visibility.Collapsed;
            if (BtnAddToScript != null) BtnAddToScript.IsEnabled = false;
            _syncingParams = false;
        }

        private void LstCommands_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // null selection comes from ClearParamPanel - ignore it here
            if (LstCommands.SelectedItem is not AdtFunction func) return;

            // If the user manually picks a command from the list that is DIFFERENT
            // from the one currently synced to a script line, detach the sync link.
            // This prevents OnParamControlChanged from overwriting the old script
            // line with the new command's values while the user is filling params.
            // TrySelectCommandFromCurrentLine sets _syncLineIndex BEFORE setting
            // SelectedItem, so when that path is used _selectedFunc is still the
            // old value here - the names will match, and we keep the link.
            if (_syncLineIndex >= 0 &&
                _selectedFunc != null &&
                !string.Equals(_selectedFunc.Name, func.Name, StringComparison.OrdinalIgnoreCase))
            {
                _syncLineIndex = -1;  // different command chosen - break the sync link
            }

            _selectedFunc = func;
            TxtParamHeader.Text = func.Name;
            TxtParamHeader.SetResourceReference(TextBlock.ForegroundProperty, "NavIconIntuneFgBrush");
            TxtParamPlaceholder.Visibility = Visibility.Collapsed;
            BuildParamPanel(func);
            BtnAddToScript.IsEnabled = true;
        }

        private void BuildParamPanel(AdtFunction func)
        {
            _paramControls.Clear();
            // If previous command was Add-PSScript, restore the ScrollViewer → PanelParams link
            if (_col2ScrollViewer != null && !ReferenceEquals(_col2ScrollViewer.Content, PanelParams))
            {
                _col2ScrollViewer.Content = PanelParams;
                _col2ScrollViewer.VerticalScrollBarVisibility   = ScrollBarVisibility.Auto;
                _col2ScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
            // Keep TxtParamHeader (child[0]) and TxtParamPlaceholder (child[1]) - remove param rows only
            while (PanelParams.Children.Count > 2)
                PanelParams.Children.RemoveAt(PanelParams.Children.Count - 1);
            _varNameControl = null;

            // ── Special case: Add-PSScript - large editable multi-line script block ──
            if (string.Equals(func.Name, "Add-PSScript", StringComparison.OrdinalIgnoreCase))
            {
                // Full-height professional multi-line script editor
                // Remove the ScrollViewer constraint so the TextBox can fill the whole Col2
                // We build a Border-wrapped TextBox and add it directly to PanelParams parent
                var tb = new TextBox
                {
                    AcceptsReturn     = true,
                    AcceptsTab        = true,
                    TextWrapping                  = TextWrapping.Wrap,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    FontFamily        = new FontFamily("Consolas"),
                    FontSize          = 12,
                    BorderThickness   = new Thickness(0),   // border on wrapper, not TextBox
                    Padding           = new Thickness(10, 8, 10, 8),
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                // Live theme references so the box re-colors on Light/Dark switch (not a one-time snapshot)
                tb.SetResourceReference(Control.BackgroundProperty,  "InputBgBrush");
                tb.SetResourceReference(Control.ForegroundProperty,  "TextPrimaryBrush");
                tb.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty,     "AccentBlueBrush");
                tb.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "AccentBlueBrush");
                tb.TextChanged += (_, _) => OnParamControlChanged();
                _paramControls["ScriptBlock"] = tb;

                // Professional wrapper: rounded border with accent focus ring
                var wrapper = new Border
                {
                    BorderThickness = new Thickness(0),
                    CornerRadius    = new CornerRadius(6),
                    Margin          = new Thickness(8, 4, 8, 8),
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = tb
                };
                wrapper.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
                wrapper.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");
                // Accent border on focus
                tb.GotKeyboardFocus  += (_, _) => wrapper.SetResourceReference(Border.BorderBrushProperty, "AccentBlueBrush");
                tb.LostKeyboardFocus += (_, _) => wrapper.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");

                // Replace the ScrollViewer+PanelParams with direct Grid row stretch
                // Find the ScrollViewer parent and replace its content
                if (PanelParams.Parent is ScrollViewer sv)
                {
                    _col2ScrollViewer = sv; // cache for restore later
                    sv.Content = wrapper;
                    sv.VerticalScrollBarVisibility   = ScrollBarVisibility.Hidden;
                    sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
                    // ScrollViewer provides infinite width → TextWrapping never triggers.
                    // Constrain TextBox width to the ScrollViewer's actual rendered width.
                    void ConstrainWidth()
                    {
                        double avail = sv.ViewportWidth > 0 ? sv.ViewportWidth
                                     : sv.ActualWidth;
                        if (avail > 0)
                        {
                            tb.MaxWidth  = avail - wrapper.Margin.Left - wrapper.Margin.Right - 2;
                            tb.MinWidth  = tb.MaxWidth;
                        }
                    }
                    sv.SizeChanged += (_, _) => ConstrainWidth();
                    // Also run immediately in case already laid out
                    sv.Loaded       += (_, _) => ConstrainWidth();
                    Dispatcher.InvokeAsync(ConstrainWidth, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    // Fallback: add to PanelParams with stretch
                    wrapper.MinHeight = 300;
                    PanelParams.Children.Add(wrapper);
                }
                return;
            }

            // Variable assignment row (for output-returning functions)
            if (func.HasReturnValue)
            {
                var varRow = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

                // Label + "optional" badge
                var varLrow = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                varLrow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                varLrow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var varLbl = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                varLbl.Inlines.Add(new System.Windows.Documents.Run("Variable")
                    { FontWeight = FontWeights.SemiBold, Foreground = TC("TextPrimaryBrush") });
                Grid.SetColumn(varLbl, 0);
                varLrow.Children.Add(varLbl);
                varRow.Children.Add(varLrow);

                // $ prefix label + TextBox
                var varGrid = new Grid();
                varGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                varGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var dollarLbl = new TextBlock {
                    Text = "$", FontSize = 11, FontWeight = FontWeights.Normal,
                    Foreground = TC("TextPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0,0,2,0)
                };
                Grid.SetColumn(dollarLbl, 0);
                varGrid.Children.Add(dollarLbl);
                var varTb = MakeParamTextBox();
                varTb.ToolTip = "Optional variable name to capture the return value. Leave blank to discard.";
                Grid.SetColumn(varTb, 1);
                varGrid.Children.Add(varTb);
                varRow.Children.Add(varGrid);

                PanelParams.Children.Add(varRow);
                _varNameControl = varTb;
                // Live-sync: rebuilds the script line when var name changes
                varTb.TextChanged += (_, _) => OnParamControlChanged();
            }

            foreach (var param in func.Parameters)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

                // Label row
                var lrow = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                lrow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                lrow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new TextBlock { FontSize=11, VerticalAlignment=VerticalAlignment.Center };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                lbl.Inlines.Add(new System.Windows.Documents.Run(param.Name) { FontWeight=FontWeights.SemiBold });
                if (param.Mandatory)
                {
                    var star = new System.Windows.Documents.Run(" *");
                    star.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentRedBrush");
                    lbl.Inlines.Add(star);
                }

                // Mark mutually-exclusive params with a small badge
                bool isMutuallyExclusive = param.Name is "AccountName" or "SID" or "WellKnownSIDName";
                if (isMutuallyExclusive)
                    lbl.ToolTip = "Mutually exclusive - use only one of: AccountName, SID, or WellKnownSIDName";

                Grid.SetColumn(lbl, 0); lrow.Children.Add(lbl);

                // "one-of" badge removed per user request - the mutual-exclusivity hint remains
                // available via the label's tooltip above.

                // Description column removed per user requirement
                row.Children.Add(lrow);

                // Input control
                FrameworkElement ctrl;
                if (param.IsSwitch)
                {
                    var enableLbl = new TextBlock { Text="Enable", FontSize=11 };
                    enableLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    var cb = new CheckBox {
                        Margin=new Thickness(2,2,0,0),
                        Style = this.TryFindResource("RoundedParamCheckBox") as Style,
                        Content=enableLbl
                    };
                    ctrl = cb; row.Children.Add(ctrl);
                }
                else if (param.ValidSet.Count > 0)
                {
                    var cmb = new ComboBox {
                        FontSize=11,
                        Style = this.TryFindResource("RoundedParamCombo") as Style
                    };
                    cmb.Items.Add(new ComboBoxItem { Content="(default)", IsSelected=true });
                    foreach (var v in param.ValidSet) cmb.Items.Add(new ComboBoxItem { Content=v });
                    cmb.SelectedIndex = 0;
                    ctrl = cmb; row.Children.Add(ctrl);
                }
                else
                {
                    bool hasBrowse = (IsPathParam(param.Name) || IsTransformsLikeParam(param.Name))
                                     && !IsSuppressedBrowseParam(param.Name);
                    bool isArray   = IsArrayParam(param);

                    if (hasBrowse)
                    {
                        // Determine icon and tooltip based on function/param context
                        bool isReg       = IsRegistryBrowseParam(param.Name);
                        bool isFolderOnly = IsFolderOnlyBrowseParam(param.Name);
                        string btnIcon    = isReg ? "🗂" : "📁";
                        string btnTip     = isReg ? "Open Registry Editor" : "Browse";

                        var g = new Grid();
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                        var tb = MakeParamTextBox();
                        Grid.SetColumn(tb, 0);
                        var btn = new Button {
                            Content=btnIcon, Height=32, Width=34, Margin=new Thickness(4,0,0,0),
                            FontSize=13,
                            Style = this.TryFindResource("RoundedIconButton") as Style,
                            ToolTip=btnTip,
                            Tag=tb, DataContext=param.Name
                        };
                        btn.Click += BrowseParamPath_Click;
                        Grid.SetColumn(btn, 1);
                        g.Children.Add(tb); g.Children.Add(btn);
                        row.Children.Add(g); ctrl = tb;


                    }
                    else
                    {
                        var tb = MakeParamTextBox();
                        row.Children.Add(tb);
                        ctrl = tb;
                    }
                }

                _paramControls[param.Name] = ctrl;
                PanelParams.Children.Add(row);

                // Attach live-sync: any change rebuilds the script line in-place
                switch (ctrl)
                {
                    case TextBox tbSync:
                        tbSync.TextChanged += (_, _) => OnParamControlChanged();
                        break;
                    case CheckBox cbSync:
                        cbSync.Checked   += (_, _) => OnParamControlChanged();
                        cbSync.Unchecked += (_, _) => OnParamControlChanged();
                        break;
                    case ComboBox cmSync:
                        cmSync.SelectionChanged += (_, _) => OnParamControlChanged();
                        break;
                }
            }
        }

        // ── Theme-aware color helper ─────────────────────────────────
        private static SolidColorBrush TC(string key)
        {
            var res = Application.Current.TryFindResource(key);
            return res is SolidColorBrush b ? b : new SolidColorBrush(Colors.Gray);
        }

        private TextBox MakeParamTextBox()
        {
            var tb = new TextBox {
                FontSize=11,
                Style = this.TryFindResource("RoundedParamTextBox") as Style
            };
            return tb;
        }


        // pathMode: 0 = no conversion, 1 = source (MakePortable), 2 = destination (MakePortableDestination).
        // Path values use double quotes so embedded $variables / $(...) expand at runtime.
        private string FormatArrayValue(string raw, int pathMode = 0)
        {
            var parts = raw.Split(',')
                           .Select(s => s.Trim())
                           .Where(s => !string.IsNullOrEmpty(s))
                           .Select(s => pathMode == 2 ? MakePortableDestination(s)
                                      : pathMode == 1 ? MakePortable(s)
                                      : s)
                           .Select(s => pathMode == 0 ? $"'{s}'" : $"\"{s}\"")
                           .ToList();
            // Single value: emit just the quoted value (no array parentheses) for cleaner output
            // like -Path "...". Multiple comma-separated values keep the (a,b,c) array syntax.
            if (parts.Count == 1) return parts[0];
            return $"({string.Join(",", parts)})";
        }

        private static bool IsPathParam(string name)
        { string n=name.ToLowerInvariant(); return n.Contains("path")||n.Contains("destination")||n.Contains("dir")||n.Contains("folder")||n.Contains("file")||n.Contains("source"); }

        // File-list params (Transforms, Patches) take .mst/.msp files that sit beside the .msi in the
        // package Files folder. Format them as portable double-quoted paths ($($adtSession.DirFiles)\..)
        // rather than bare single-quoted array literals, so an MSI+MST command emits
        // -Transforms "$($adtSession.DirFiles)\x.mst" to match the FilePath style.
        private static bool IsTransformsLikeParam(string name)
        { string n=name.ToLowerInvariant(); return n.Contains("transform")||n.Contains("patch"); }

        // Destination-type params point at a location ON THE TARGET MACHINE, so their drive root
        // should be made portable as $SystemDrive\... (e.g. Destination "C:\Intune" → "$SystemDrive\Intune").
        private static bool IsDestinationParam(string name)
        { string n=name.ToLowerInvariant(); return n.Contains("destination")||n.Contains("targetpath")||n.Contains("installpath"); }

        // Converts a destination/target path's drive root (C:\) to $SystemDrive\. If the path is
        // inside the package Files/SupportFiles folder it is still made portable to $($adtSession.DirFiles).
        // A user-profile path (C:\Users\<name>\...) has the literal username replaced with
        // $env:USERNAME so it is not hardcoded (e.g. C:\Users\JohnDoe\Desktop →
        // $SystemDrive\Users\$env:USERNAME\Desktop).
        private string MakePortableDestination(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            string portable = MakePortable(path);
            // If MakePortable already converted it to a package-relative path, keep that.
            if (portable.StartsWith("$(", StringComparison.Ordinal)) return portable;
            // Convert the drive root to $env:SystemDrive (note: PowerShell uses $env:SystemDrive,
            // NOT a bare $SystemDrive, which is undefined and expands to an empty string).
            // A MatchEvaluator is used so the replacement text is treated literally (no $-substitution).
            portable = Regex.Replace(portable, @"^[A-Za-z]:\\", _ => @"$env:SystemDrive\");
            // Replace a literal username in a user-profile path with $env:USERNAME.
            //   $env:SystemDrive\Users\<name>           → $env:SystemDrive\Users\$env:USERNAME
            //   $env:SystemDrive\Users\<name>\<rest>    → $env:SystemDrive\Users\$env:USERNAME\<rest>
            // Shared/system profiles (Public, Default, Default User, All Users) are left as-is.
            portable = Regex.Replace(
                portable,
                @"^(\$env:SystemDrive\\Users\\)([^\\]+)(\\|$)",
                m =>
                {
                    string name = m.Groups[2].Value;
                    bool shared = name.Equals("Public", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("Default User", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("All Users", StringComparison.OrdinalIgnoreCase);
                    return shared
                        ? m.Value
                        : m.Groups[1].Value + "$env:USERNAME" + m.Groups[3].Value;
                },
                RegexOptions.IgnoreCase);
            return portable;
        }

        // Returns true when the parameter type is an array (e.g. System.String[]).
        private static bool IsArrayParam(AdtParam param)
            => !string.IsNullOrEmpty(param.Type) && param.Type.EndsWith("[]", System.StringComparison.Ordinal);

        private static bool IsFolderParam(string name)
        { string n=name.ToLowerInvariant(); return n.Contains("folder")||n.Contains("dir")||n.Contains("destination")||n.Contains("directory"); }

        // Returns true for params that accept either a file OR a folder (user must choose).
        // -Path on these functions accepts either a file OR a folder as source.
        // All of them get the File / Folder / Cancel dialog when Browse is clicked.
        private static readonly HashSet<string> _fileOrFolderFunctions = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Add-ADTFont",
            "Copy-ADTFile",
            "Copy-ADTFileToUserProfiles",
            // File-or-folder browse for Path and LiteralPath
            "Remove-ADTFileFromUserProfiles",
            "Remove-ADTFolder",
        };

        // Functions whose Path/LiteralPath params need a folder-only browse icon (📁).
        // These replace the generic file browse with a folder picker.
        private static readonly HashSet<string> _folderOnlyBrowseFunctions = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Copy-ADTContentToCache",      // LiteralPath
            "Dismount-ADTWimFile",         // Path
            "Mount-ADTWimFile",            // Path
            "New-ADTFolder",               // LiteralPath (already folder-named but confirm)
            "New-ADTMsiTransform",         // NewTransformPath
            "Remove-ADTContentFromCache",  // LiteralPath (note: param name is "Path" in some builds)
        };

        // Functions whose Path/LiteralPath params open the registry editor (🗂).
        private static readonly HashSet<string> _registryBrowseFunctions = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "Get-ADTRegistryKey",
            "Remove-ADTRegistryKey",
            "Set-ADTRegistryKey",
        };

        // (func, param) pairs where the browse icon should NOT appear.
        private static readonly HashSet<(string func, string param)> _noBrowseParams = new(
            new TupleIgnoreCaseComparer())
        {
            ("Invoke-ADTAllUsersRegistryAction", "UserProfiles"),
            ("Start-ADTMsiProcess",              "LogFileName"),
            ("Start-ADTMsiProcessAsUser",         "LogFileName"),
        };

        // Case-insensitive comparer for (string, string) tuples
        private sealed class TupleIgnoreCaseComparer : IEqualityComparer<(string, string)>
        {
            public bool Equals((string, string) x, (string, string) y)
                => StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);
            public int GetHashCode((string, string) obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
        }

        private bool IsFileOrFolderParam(string paramName)
        {
            if (_selectedFunc == null) return false;
            // Path and LiteralPath both trigger the File/Folder dialog for the listed commands
            return (paramName.Equals("Path", StringComparison.OrdinalIgnoreCase)
                    || paramName.Equals("LiteralPath", StringComparison.OrdinalIgnoreCase))
                   && _fileOrFolderFunctions.Contains(_selectedFunc.Name);
        }

        private bool IsFolderOnlyBrowseParam(string paramName)
        {
            if (_selectedFunc == null) return false;
            string fn = _selectedFunc.Name;
            // Per-function overrides
            if (fn.Equals("Copy-ADTContentToCache", StringComparison.OrdinalIgnoreCase)
                && paramName.Equals("LiteralPath", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.Equals("Dismount-ADTWimFile", StringComparison.OrdinalIgnoreCase)
                && paramName.Equals("Path", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.Equals("Mount-ADTWimFile", StringComparison.OrdinalIgnoreCase)
                && paramName.Equals("Path", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.Equals("New-ADTFolder", StringComparison.OrdinalIgnoreCase)
                && paramName.Equals("LiteralPath", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.Equals("New-ADTMsiTransform", StringComparison.OrdinalIgnoreCase)
                && paramName.Equals("NewTransformPath", StringComparison.OrdinalIgnoreCase)) return true;
            if (fn.Equals("Remove-ADTContentFromCache", StringComparison.OrdinalIgnoreCase)
                && (paramName.Equals("LiteralPath", StringComparison.OrdinalIgnoreCase)
                    || paramName.Equals("Path", StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        private bool IsRegistryBrowseParam(string paramName)
        {
            if (_selectedFunc == null) return false;
            return _registryBrowseFunctions.Contains(_selectedFunc.Name)
                   && (paramName.Equals("Path", StringComparison.OrdinalIgnoreCase)
                       || paramName.Equals("LiteralPath", StringComparison.OrdinalIgnoreCase));
        }

        private bool IsSuppressedBrowseParam(string paramName)
        {
            if (_selectedFunc == null) return false;
            return _noBrowseParams.Contains((_selectedFunc.Name, paramName));
        }

        // ══════════════════════════════════════════════════════════════
        // BROWSE FOR PARAM PATH (file or folder - auto-detected by name)
        // ══════════════════════════════════════════════════════════════
        private void BrowseParamPath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not TextBox tb) return;
            string paramName = btn.DataContext as string ?? "";

            // Registry params: clicking the 🗂 icon opens regedit silently so the user
            // can navigate to the desired key and manually type/paste the path.
            if (IsRegistryBrowseParam(paramName))
            {
                try { System.Diagnostics.Process.Start("regedit.exe"); } catch { }
                return;
            }

            // Folder-only browse (overrides the generic IsFolderParam check for specific commands).
            if (IsFolderOnlyBrowseParam(paramName))
            {
                var p = PickFolder("Select Folder");
                if (p != null) tb.Text = p;
                return;
            }

            // For Path/LiteralPath on file-or-folder commands: offer File / Folder / Cancel,
            // seeded to the package's Files\ directory so the user lands there directly.
            if (IsFileOrFolderParam(paramName))
            {
                string? initDir = GetScriptFilesDir();
                string title = _selectedFunc?.Name switch
                {
                    "Copy-ADTFile"               => "Select Source File or Folder",
                    "Copy-ADTFileToUserProfiles"  => "Select Source File or Folder",
                    "Remove-ADTFileFromUserProfiles" => "Select File or Folder",
                    "Remove-ADTFolder"            => "Select File or Folder",
                    _                             => "Select Font File or Folder",
                };
                string? p = PickFileOrFolder(title, initDir);
                if (p != null) tb.Text = p;
                return;
            }

            // Transforms (.mst) / Patches (.msp): file picker filtered to the matching type,
            // seeded to the package Files\ folder so the user lands beside the .msi.
            if (IsTransformsLikeParam(paramName))
            {
                string init = GetScriptFilesDir() ?? GetSystemDrive();
                bool isPatch = paramName.IndexOf("patch", StringComparison.OrdinalIgnoreCase) >= 0;
                var d = new OpenFileDialog
                {
                    Title  = isPatch ? "Select Patch File" : "Select Transform File",
                    Filter = isPatch ? "Patch Files (*.msp)|*.msp|All Files (*.*)|*.*"
                                     : "Transform Files (*.mst)|*.mst|All Files (*.*)|*.*",
                    InitialDirectory = Directory.Exists(init) ? init : GetSystemDrive()
                };
                if (d.ShowDialog() == true) tb.Text = d.FileName;
                return;
            }

            if (IsFolderParam(paramName))
            {
                var p = PickFolder("Select Folder");
                if (p != null) tb.Text = p;
            }
            else
            {
                // Seed the dialog's initial directory to the package's Files folder when available
                string initDir = GetScriptFilesDir() ?? GetSystemDrive();
                var dlg = new OpenFileDialog {
                    Title = "Select File",
                    Filter = "All Files (*.*)|*.*",
                    InitialDirectory = Directory.Exists(initDir) ? initDir : GetSystemDrive()
                };
                if (dlg.ShowDialog() == true) tb.Text = dlg.FileName;
            }
        }

        /// <summary>
        /// Returns the Files\ directory that sits next to the currently loaded
        /// Invoke-AppDeployToolkit.ps1, or null when no script is loaded.
        /// </summary>
        private string? GetScriptFilesDir()
        {
            // Primary: derive from the created/loaded script path
            if (_createdScriptPath != null && File.Exists(_createdScriptPath))
            {
                string? scriptDir = Path.GetDirectoryName(_createdScriptPath);
                if (scriptDir != null)
                {
                    string filesDir = Path.Combine(scriptDir, "Files");
                    if (Directory.Exists(filesDir)) return filesDir;
                }
            }
            // Fallback: derive from the PSADT Template Folder field
            string templatePath = TxtTemplatePath.Text.Trim();
            if (!string.IsNullOrEmpty(templatePath))
            {
                string filesDir = Path.Combine(templatePath, "Files");
                if (Directory.Exists(filesDir)) return filesDir;
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        // PARSE SCRIPT LINE → POPULATE PARAM CONTROLS  (Feature 1)
        // ══════════════════════════════════════════════════════════════
        /// <summary>
        /// Parses a PowerShell command string and populates the param controls.
        /// Handles: -Param "value"  -Param 'value'  -SwitchParam
        /// </summary>
        private void ParseAndPopulateParamsFromLine(string line)
        {
            if (_selectedFunc == null || string.IsNullOrWhiteSpace(line)) return;

            _syncingParams = true;
            try
            {
                // Check for leading variable assignment: $varName = CommandName ...
                line = line.Trim();
                var varAssignMatch = System.Text.RegularExpressions.Regex.Match(
                    line, @"^\$(?<var>[\w]+)\s*=\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (varAssignMatch.Success)
                {
                    if (_varNameControl != null)
                        _varNameControl.Text = varAssignMatch.Groups["var"].Value;
                    line = line.Substring(varAssignMatch.Length).Trim();
                }
                else if (_varNameControl != null)
                {
                    _varNameControl.Clear();
                }

                // Drop the command name (first token) to get the arguments string
                int firstSpace = line.IndexOfAny(new[] { ' ', '\t' });
                string args = firstSpace >= 0 ? line.Substring(firstSpace).Trim() : "";

                var paramPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var switchSet  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Match: -ParamName [optional value]
                // value = "...", '...', or bare word (not starting with -)
                var rx = new System.Text.RegularExpressions.Regex(
                    @"-(?<n>\w[\w-]*)(?:\s+(?<v>""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|[^\s\-]\S*))?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match m in rx.Matches(args))
                {
                    string pName = m.Groups["n"].Value;
                    string pVal  = m.Groups["v"].Success ? m.Groups["v"].Value : "";
                    // Strip surrounding quotes
                    if (pVal.Length >= 2 &&
                        ((pVal[0] == '"' && pVal[pVal.Length-1] == '"') ||
                         (pVal[0] == '\'' && pVal[pVal.Length-1] == '\''))) 
                        pVal = pVal.Substring(1, pVal.Length - 2);

                    if (string.IsNullOrEmpty(pVal)) switchSet.Add(pName);
                    else paramPairs[pName] = pVal;
                }

                // Apply to controls
                foreach (var param in _selectedFunc.Parameters)
                {
                    if (!_paramControls.TryGetValue(param.Name, out var ctrl)) continue;
                    if (param.IsSwitch)
                    {
                        if (ctrl is CheckBox cb)
                            cb.IsChecked = switchSet.Contains(param.Name);
                    }
                    else if (paramPairs.TryGetValue(param.Name, out string? val))
                    {
                        // For array params, val may arrive as ('winword','excel') - convert back to CSV
                        if (IsArrayParam(param) && val.StartsWith("(") && val.EndsWith(")"))
                        {
                            val = val.Substring(1, val.Length - 2); // strip parens
                            // strip individual quotes and join with ", "
                            val = string.Join(", ",
                                val.Split(',')
                                   .Select(s => s.Trim().Trim('\'', '"'))
                                   .Where(s => !string.IsNullOrEmpty(s)));
                        }

                        if (ctrl is TextBox tb) tb.Text = val;
                        else if (ctrl is ComboBox cm)
                        {
                            foreach (ComboBoxItem item in cm.Items)
                                if (string.Equals(item.Content?.ToString(), val,
                                    StringComparison.OrdinalIgnoreCase))
                                { cm.SelectedItem = item; break; }
                        }
                    }
                    else
                    {
                        // Param absent in this line - clear control
                        if (ctrl is TextBox tb2) tb2.Clear();
                        else if (ctrl is CheckBox cb2) cb2.IsChecked = false;
                        else if (ctrl is ComboBox cm2) cm2.SelectedIndex = 0;
                    }
                }
            }
            catch { /* best-effort */ }
            finally { _syncingParams = false; }
        }

        // ══════════════════════════════════════════════════════════════
        // LIVE-SYNC PARAM CONTROLS → SCRIPT LINE  (Feature 2)
        // ══════════════════════════════════════════════════════════════
        /// <summary>
        /// Called on every param control change. Restarts a 300ms debounce timer.
        /// The actual script-line update happens in FlushSyncToScript() when the
        /// timer fires - safely after the user has stopped typing, so TxtScript
        /// operations never run while a keystroke is being processed in the param
        /// TextBox (which caused the cursor-jump / reversed-typing bug).
        /// </summary>
        private void OnParamControlChanged()
        {
            if (_syncingParams)     return;  // populating controls - no write-back
            if (_syncLineIndex < 0) return;  // list click, not script-line click
            if (_selectedFunc == null || TxtScript == null) return;

            // (Re)start the debounce timer - resets to 300ms on every keystroke
            if (_syncTimer == null)
            {
                _syncTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _syncTimer.Tick += (_, _) => FlushSyncToScript();
            }
            _syncTimer.Stop();
            _syncTimer.Start();
        }

        /// <summary>
        /// Fired by _syncTimer 300ms after the last param control change.
        /// Rebuilds the command string from current control values and replaces
        /// the tracked script line in-place using TxtScript.Text assignment
        /// (safe here because no keystroke is in flight).
        /// </summary>
        private void FlushSyncToScript()
        {
            _syncTimer?.Stop();
            if (_syncingParams)     return;
            if (_syncLineIndex < 0) return;
            if (_selectedFunc == null || TxtScript == null) return;

            try
            {
                // Special case: Add-PSScript inserts raw script block verbatim
                string newCmd;
                if (string.Equals(_selectedFunc.Name, "Add-PSScript", StringComparison.OrdinalIgnoreCase))
                {
                    newCmd = _paramControls.TryGetValue("ScriptBlock", out var scriptCtrl)
                        ? GetCtrlValue(scriptCtrl)
                        : "";
                }
                else
                {
                // Build new command string from current control values
                var sb = new StringBuilder(_selectedFunc.Name);
                foreach (var param in _selectedFunc.Parameters)
                {
                    if (!_paramControls.TryGetValue(param.Name, out var ctrl)) continue;
                    string val = GetCtrlValue(ctrl);
                    if (string.IsNullOrWhiteSpace(val)) continue;
                    if (param.IsSwitch) { if (val == "True") sb.Append($" -{param.Name}"); }
                    else if (IsArrayParam(param))
                    {
                        int mode = IsDestinationParam(param.Name) ? 2 : (IsPathParam(param.Name) || IsTransformsLikeParam(param.Name)) ? 1 : 0;
                        sb.Append($" -{param.Name} {FormatArrayValue(val, mode)}");
                    }
                    else if (IsDestinationParam(param.Name))
                        sb.Append($" -{param.Name} \"{MakePortableDestination(val)}\"");
                    else sb.Append($" -{param.Name} \"{MakePortable(val)}\"");
                }
                // Prepend variable assignment if user filled in the var name box
                string varPrefix = (_varNameControl != null && !string.IsNullOrWhiteSpace(_varNameControl.Text))
                    ? "$" + _varNameControl.Text.Trim().TrimStart('$') + " = "
                    : "";
                newCmd = varPrefix + sb.ToString();
                }

                string fullText = TxtScript.Text;
                int lineIdx = _syncLineIndex;
                if (lineIdx < 0 || lineIdx >= TxtScript.LineCount) return;

                int lineStart = TxtScript.GetCharacterIndexFromLineIndex(lineIdx);
                int lineEnd   = (lineIdx + 1 < TxtScript.LineCount)
                                ? TxtScript.GetCharacterIndexFromLineIndex(lineIdx + 1)
                                : fullText.Length;

                string origLine   = fullText.Substring(lineStart, lineEnd - lineStart);
                string lineEnding = origLine.EndsWith("\r\n") ? "\r\n"
                                  : origLine.EndsWith("\n")   ? "\n"
                                  : "";

                // Preserve the original line's leading indentation so an in-place edit doesn't
                // push the command back to column 1. Apply the same indent to every line of a
                // multi-line command (e.g. Add-PSScript).
                string origIndent = "";
                {
                    int wi = 0;
                    while (wi < origLine.Length && (origLine[wi] == ' ' || origLine[wi] == '\t')) wi++;
                    origIndent = origLine.Substring(0, wi);
                }
                if (!string.IsNullOrEmpty(origIndent) && !string.IsNullOrEmpty(newCmd))
                {
                    var ncLines = newCmd.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < ncLines.Length; i++)
                        if (ncLines[i].Length > 0) ncLines[i] = origIndent + ncLines[i];
                    newCmd = string.Join("\n", ncLines);
                }

                string replacement = newCmd + lineEnding;
                if (origLine == replacement) return;  // nothing changed

                // Assigning TxtScript.Text resets the caret, and this flush is fired by a 300ms
                // debounce timer - so it can land AFTER the user has clicked somewhere else and
                // yank the caret off the line they just clicked. Every other TxtScript.Text writer
                // in this file restores the caret; this one did not. Map the old caret position
                // through the edit so it stays visually put:
                //   before the edited line -> unchanged
                //   inside  the edited line -> clamped to the rewritten line
                //   after   the edited line -> shifted by the change in that line's length
                int caretBefore = TxtScript.CaretIndex;
                int caretAfter;
                if (caretBefore >= lineEnd)
                    caretAfter = caretBefore + (replacement.Length - origLine.Length);
                else if (caretBefore > lineStart)
                    caretAfter = lineStart + Math.Min(caretBefore - lineStart, replacement.Length);
                else
                    caretAfter = caretBefore;

                // Raise _flushingToScript BEFORE writing so that the
                // SelectionChanged → TrySelectCommandFromCurrentLine chain
                // that fires from TxtScript.Text assignment is blocked from
                // re-parsing the line and clearing the param controls.
                _flushingToScript    = true;
                _suppressUndoCapture = true;
                TxtScript.IsReadOnly = false;
                TxtScript.Text = fullText.Substring(0, lineStart)
                               + replacement
                               + fullText.Substring(lineEnd);
                TxtScript.CaretIndex  = Math.Max(0, Math.Min(caretAfter, TxtScript.Text.Length));
                TxtScript.IsReadOnly  = true;
                _suppressUndoCapture  = false;
                _flushingToScript     = false;

                MarkScriptDirty();
                UpdateLineNumbers();
            }
            catch { /* best-effort */ }
        }

        // ══════════════════════════════════════════════════════════════
        // ADD TO SCRIPT
        // ══════════════════════════════════════════════════════════════
        private void BtnAddToScript_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFunc == null) return;
            if (_createdScriptPath == null)
            { PkgStyledMessage(Window.GetWindow(this), "No Script", "Create a package first, then use the Script tab to add commands.", "ℹ"); return; }

            // Validate mandatory
            var missing = _selectedFunc.Parameters
                .Where(p => p.Mandatory && _paramControls.TryGetValue(p.Name, out var c) && string.IsNullOrWhiteSpace(GetCtrlValue(c)))
                .Select(p => p.Name).ToList();
            if (missing.Count > 0)
            { PkgStyledMessage(Window.GetWindow(this), "Required", "Fill in required fields:\n\n• "+string.Join("\n• ",missing), "⚠"); return; }

            // Build command string
            string cmd;
            if (string.Equals(_selectedFunc.Name, "Add-PSScript", StringComparison.OrdinalIgnoreCase))
            {
                // Verbatim multi-line script block
                cmd = _paramControls.TryGetValue("ScriptBlock", out var sc)
                    ? GetCtrlValue(sc) : "";
            }
            else
            {
            var sb = new StringBuilder(_selectedFunc.Name);
            foreach (var param in _selectedFunc.Parameters)
            {
                if (!_paramControls.TryGetValue(param.Name, out var ctrl)) continue;
                string val = GetCtrlValue(ctrl);
                if (string.IsNullOrWhiteSpace(val)) continue;
                if (param.IsSwitch) { if (val=="True") sb.Append($" -{param.Name}"); }
                else if (IsArrayParam(param))
                {
                    int mode = IsDestinationParam(param.Name) ? 2 : (IsPathParam(param.Name) || IsTransformsLikeParam(param.Name)) ? 1 : 0;
                    sb.Append($" -{param.Name} {FormatArrayValue(val, mode)}");
                }
                else if (IsDestinationParam(param.Name))
                    sb.Append($" -{param.Name} \"{MakePortableDestination(val)}\"");
                else sb.Append($" -{param.Name} \"{MakePortable(val)}\"");
            }
            // Prepend variable assignment if user filled in the var name box
            string varPrefix = (_varNameControl != null && !string.IsNullOrWhiteSpace(_varNameControl.Text))
                ? "$" + _varNameControl.Text.Trim().TrimStart('$') + " = "
                : "";
            cmd = varPrefix + sb.ToString();
            }

            // Indent the new command to match the surrounding script block. Scan upward from the
            // insertion point to find the nearest non-blank line that carries indentation (the
            // block's existing code lines, e.g. the Start-ADTProcess line) and reuse that indent,
            // so added commands line up with the block instead of sitting at column 1. This skips
            // blank lines and any just-added column-1 lines. Multi-line commands get each line indented.
            {
                int probeLine;
                if (_syncLineIndex >= 0 && _syncLineIndex < TxtScript.LineCount)
                    probeLine = _syncLineIndex;
                else
                {
                    int caretIdx = TxtScript.CaretIndex;
                    probeLine = (TxtScript.Text.Length > 0)
                        ? TxtScript.GetLineIndexFromCharacterIndex(Math.Max(0, Math.Min(caretIdx, TxtScript.Text.Length)))
                        : 0;
                }

                // Find the nearest indented, non-blank line at or above the probe line.
                string indent = "";
                for (int li = probeLine; li >= 0 && li > probeLine - 40; li--)
                {
                    string lineText = GetLineText(li);
                    if (string.IsNullOrWhiteSpace(lineText)) continue;       // skip blank lines
                    string ind = GetLineIndent(li);
                    if (!string.IsNullOrEmpty(ind)) { indent = ind; break; } // first indented line wins
                    // a non-blank line with no indent (e.g. a previously-added column-1 command):
                    // keep scanning up to find the block's real indentation.
                }

                if (!string.IsNullOrEmpty(indent) && !string.IsNullOrEmpty(cmd))
                {
                    var cmdLines = cmd.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < cmdLines.Length; i++)
                        if (cmdLines[i].Length > 0) cmdLines[i] = indent + cmdLines[i];
                    cmd = string.Join("\n", cmdLines);
                }
            }

            // Decide where to insert.
            // If the panel is still linked to an existing script line (_syncLineIndex >= 0)
            // AND the command in the panel matches that line's command, this is an
            // in-place update - live-sync already keeps the line current, so nothing
            // extra is needed and we just flash status.
            // If the panel command DIFFERS from the linked line (user switched command
            // in the list), insert as a brand-new line after the caret line and
            // detach the sync link so we don't corrupt the old line.
            if (_syncLineIndex >= 0)
            {
                // Check whether the live-sync line already contains this command
                string fullTxt = TxtScript.Text;
                int ls = (_syncLineIndex < TxtScript.LineCount)
                         ? TxtScript.GetCharacterIndexFromLineIndex(_syncLineIndex) : -1;
                int le = (ls >= 0 && _syncLineIndex + 1 < TxtScript.LineCount)
                         ? TxtScript.GetCharacterIndexFromLineIndex(_syncLineIndex + 1)
                         : (ls >= 0 ? fullTxt.Length : -1);
                string existingLine = (ls >= 0 && le > ls)
                    ? fullTxt.Substring(ls, le - ls).Trim() : "";
                string existingCmd = existingLine.Split(new[]{' ','\t'},
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

                bool sameCommand = string.Equals(existingCmd, _selectedFunc.Name,
                                                 StringComparison.OrdinalIgnoreCase);

                // Whether same or different command: insert a blank line after the
                // linked line, move caret there, and detach the sync link.
                // (Live-sync already kept the linked line up-to-date.)
                string addedFuncNameSync = _selectedFunc.Name;
                CaptureUndoState();
                _flushingToScript    = true;
                _suppressUndoCapture = true;
                TxtScript.IsReadOnly = false;
                string fullTxt2 = TxtScript.Text;
                // Find the end of the sync line
                int syncLineStart = (_syncLineIndex < TxtScript.LineCount)
                    ? TxtScript.GetCharacterIndexFromLineIndex(_syncLineIndex) : -1;
                int syncLineEnd = (syncLineStart >= 0 && _syncLineIndex + 1 < TxtScript.LineCount)
                    ? TxtScript.GetCharacterIndexFromLineIndex(_syncLineIndex + 1)
                    : (syncLineStart >= 0 ? fullTxt2.Length : -1);

                int insertAfterSync;
                if (!sameCommand && syncLineStart >= 0)
                {
                    // Different command: replace the sync line with the new cmd first
                    string lineEnding2 = (syncLineEnd < fullTxt2.Length) ? "\n" : "";
                    fullTxt2 = fullTxt2.Substring(0, syncLineStart)
                               + cmd + lineEnding2
                               + ((syncLineEnd < fullTxt2.Length) ? fullTxt2.Substring(syncLineEnd) : "");
                    insertAfterSync = syncLineStart + cmd.Length + lineEnding2.Length;
                }
                else
                {
                    // Same command: live-sync already updated it, just find end
                    insertAfterSync = syncLineEnd >= 0 ? syncLineEnd : fullTxt2.Length;
                }

                // Do NOT insert a blank line after the command - it accumulated one extra empty
                // line into the script on every Add. The caret still lands on the line that
                // follows the command (which in the PSADT template is already blank), so the
                // "click Add again to insert the next command" flow is unchanged.
                TxtScript.Text       = fullTxt2;
                TxtScript.IsReadOnly  = true;
                _suppressUndoCapture  = false;
                // Place caret on the line after the command - set suppress flag FIRST.
                _flushingToScript       = false;
                _syncLineIndex          = -1;  // detach sync - caret has left the command line
                _resetPanelOnNextBlank  = true;
                TxtScript.CaretIndex    = Math.Max(0, Math.Min(insertAfterSync, TxtScript.Text.Length));

                MarkScriptDirty();
                UpdateLineNumbers();
                FlashStatus("✓ " + (sameCommand ? "Updated" : "Added") + ": " + addedFuncNameSync, "#3ECFA0");
                return;
            }

            // No sync link - write to the saved selection line.
            // If the line is blank, replace it; otherwise insert after it.
            string addedFuncName = _selectedFunc.Name;
            CaptureUndoState();
            _flushingToScript    = true;
            _suppressUndoCapture = true;
            TxtScript.IsReadOnly = false;
            string text = TxtScript.Text;

            // Use _savedSelStartLine (reliable) instead of CaretIndex (unreliable after button click)
            int caretLineIdx   = _savedSelStartLine >= 0 ? _savedSelStartLine : GetCaretLine();
            int caretLineStart = TxtScript.GetCharacterIndexFromLineIndex(caretLineIdx);
            int caretLineEnd   = (caretLineIdx + 1 < TxtScript.LineCount)
                ? TxtScript.GetCharacterIndexFromLineIndex(caretLineIdx + 1)
                : text.Length;
            string caretLineText = text.Substring(caretLineStart,
                caretLineEnd - caretLineStart).Trim('\r', '\n', ' ', '\t');

            string newText;
            int blankLinePos;
            if (string.IsNullOrWhiteSpace(caretLineText))
            {
                // Blank line - replace it with cmd + blank line
                string lineEnding = (caretLineEnd < text.Length) ? Environment.NewLine : "";
                newText = text.Substring(0, caretLineStart)
                        + cmd + Environment.NewLine
                        + Environment.NewLine
                        + ((caretLineEnd < text.Length) ? text.Substring(caretLineEnd) : "");
                blankLinePos = caretLineStart + cmd.Length + Environment.NewLine.Length;
            }
            else
            {
                // Non-blank line - insert cmd after it. No trailing blank line: adding one here
                // was what grew the script by an extra empty line on every Add. blankLinePos is
                // unchanged - it still points at the start of the line following the command,
                // which is now the script's own next line rather than a freshly created blank.
                newText = text.Substring(0, caretLineEnd)
                        + cmd + Environment.NewLine
                        + ((caretLineEnd < text.Length) ? text.Substring(caretLineEnd) : "");
                blankLinePos = caretLineEnd + cmd.Length + Environment.NewLine.Length;
            }

            TxtScript.Text       = newText;
            TxtScript.IsReadOnly  = true;
            _suppressUndoCapture  = false;
            // Move caret to the blank line - set suppress flag FIRST so the
            // blank-line TrySelectCommandFromCurrentLine does not clear the panel.
            _flushingToScript       = false;
            _syncLineIndex          = -1;
            _resetPanelOnNextBlank  = true;
            TxtScript.CaretIndex    = blankLinePos;
            // Sync selection fields to the new blank line
            int newBlankLine = GetCaretLine();
            _savedSelStartLine = newBlankLine;
            _savedSelEndLine   = newBlankLine;
            _arrowAnchorLine   = -1; _dragLine = -1;

            MarkScriptDirty();
            UpdateLineNumbers();
            HighlightCurrentLine();
            FlashStatus("✓ Added: " + addedFuncName, "#3ECFA0");
        }

        private static string GetCtrlValue(FrameworkElement c) => c switch {
            TextBox tb  => tb.Text.Trim(),
            CheckBox cb => cb.IsChecked==true ? "True" : "",
            ComboBox cm => (cm.SelectedItem as ComboBoxItem)?.Content?.ToString() is "(default)" or null ? "" : (cm.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "",
            _ => ""
        };

        /// <summary>
        /// Converts an absolute path to a portable PowerShell expression:
        /// - Under Files\        → $($adtSession.DirFiles)\relative
        /// - Under SupportFiles\ → $($adtSession.DirSupportFiles)\relative
        /// - Otherwise           → $env:SystemDrive\rest
        /// </summary>
        // Returns the leading whitespace (indentation) of the given script line index.
        private string GetLineIndent(int lineIndex)
        {
            if (TxtScript == null || lineIndex < 0 || lineIndex >= TxtScript.LineCount) return "";
            int start = TxtScript.GetCharacterIndexFromLineIndex(lineIndex);
            if (start < 0) return "";
            string text = TxtScript.Text;
            int i = start;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(start, i - start);
        }

        // Returns the full text of the given script line index (without the trailing newline).
        private string GetLineText(int lineIndex)
        {
            if (TxtScript == null || lineIndex < 0 || lineIndex >= TxtScript.LineCount) return "";
            int start = TxtScript.GetCharacterIndexFromLineIndex(lineIndex);
            if (start < 0) return "";
            string text = TxtScript.Text;
            int end = (lineIndex + 1 < TxtScript.LineCount)
                ? TxtScript.GetCharacterIndexFromLineIndex(lineIndex + 1)
                : text.Length;
            if (end <= start) return "";
            return text.Substring(start, end - start).TrimEnd('\r', '\n');
        }

        private string MakePortable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            string? filesDir = GetScriptFilesDir();
            if (filesDir != null)
            {
                string normalPath = Path.GetFullPath(path);

                // ── Files\ ──────────────────────────────────────────────
                string normalFiles = Path.GetFullPath(filesDir).TrimEnd(Path.DirectorySeparatorChar)
                                     + Path.DirectorySeparatorChar;
                if (normalPath.StartsWith(normalFiles, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = normalPath.Substring(normalFiles.Length);
                    return string.IsNullOrEmpty(rel)
                        ? "$($adtSession.DirFiles)"
                        : "$($adtSession.DirFiles)\\" + rel;
                }
                if (string.Equals(normalPath.TrimEnd(Path.DirectorySeparatorChar),
                                  filesDir.TrimEnd(Path.DirectorySeparatorChar),
                                  StringComparison.OrdinalIgnoreCase))
                    return "$($adtSession.DirFiles)";

                // ── SupportFiles\ ───────────────────────────────────────
                string supportDir = Path.Combine(Path.GetDirectoryName(filesDir)!, "SupportFiles");
                string normalSupport = Path.GetFullPath(supportDir).TrimEnd(Path.DirectorySeparatorChar)
                                       + Path.DirectorySeparatorChar;
                if (normalPath.StartsWith(normalSupport, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = normalPath.Substring(normalSupport.Length);
                    return string.IsNullOrEmpty(rel)
                        ? "$($adtSession.DirSupportFiles)"
                        : "$($adtSession.DirSupportFiles)\\" + rel;
                }
                if (string.Equals(normalPath.TrimEnd(Path.DirectorySeparatorChar),
                                  supportDir.TrimEnd(Path.DirectorySeparatorChar),
                                  StringComparison.OrdinalIgnoreCase))
                    return "$($adtSession.DirSupportFiles)";
            }

            // Not inside the resolved package Files\ / SupportFiles\ folder (or the script dir
            // could not be resolved). As a robust fallback, detect a "\Files\" or "\SupportFiles\"
            // segment anywhere in the path and make it portable relative to that segment - this
            // covers paths under the package's Files folder even when the script directory cannot
            // be derived. Any path WITHOUT such a segment is returned exactly as typed.
            var mSupport = Regex.Match(path, @"[\\/]SupportFiles[\\/](.+)$", RegexOptions.IgnoreCase);
            if (mSupport.Success)
                return "$($adtSession.DirSupportFiles)\\" + mSupport.Groups[1].Value.Replace('/', '\\');
            if (Regex.IsMatch(path, @"[\\/]SupportFiles[\\/]?$", RegexOptions.IgnoreCase))
                return "$($adtSession.DirSupportFiles)";

            var mFiles = Regex.Match(path, @"[\\/]Files[\\/](.+)$", RegexOptions.IgnoreCase);
            if (mFiles.Success)
                return "$($adtSession.DirFiles)\\" + mFiles.Groups[1].Value.Replace('/', '\\');
            if (Regex.IsMatch(path, @"[\\/]Files[\\/]?$", RegexOptions.IgnoreCase))
                return "$($adtSession.DirFiles)";

            // No package-folder segment: convert the drive root (C:\) to $env:SystemDrive\ so the
            // path is portable across machines. If there is no drive root, the path is left as-is.
            return Regex.Replace(path, @"^[A-Za-z]:\\", _ => @"$env:SystemDrive\");
        }

        // ══════════════════════════════════════════════════════════════
        // UNDO / REDO
        // ══════════════════════════════════════════════════════════════
        private void BtnUndo_Click(object sender, RoutedEventArgs e) => ScriptUndo();
        private void BtnRedo_Click(object sender, RoutedEventArgs e) => ScriptRedo();

        private void CaptureUndoState()
        {
            if (_suppressUndoCapture) return;
            _undoStack.Push((TxtScript.Text, TxtScript.CaretIndex));
            _redoStack.Clear();
            if (_undoStack.Count > 100) { var tmp = _undoStack.ToArray(); _undoStack.Clear(); foreach (var s in tmp.Take(80)) _undoStack.Push(s); }
        }

        /// <summary>
        /// Applies an undo/redo snapshot and puts the caret back where it was when the snapshot
        /// was taken, then re-syncs the tracked selection lines, the line highlight and the
        /// command panel to that line. Raises _flushingToScript across the write so the
        /// SelectionChanged that fires from the Text assignment (caret momentarily at 0) cannot
        /// drag the command panel to line 1 before the caret has been put back.
        /// </summary>
        private void RestoreScriptSnapshot(string text, int caret)
        {
            _flushingToScript    = true;
            _suppressUndoCapture = true;
            TxtScript.IsReadOnly = false;
            TxtScript.Text       = text;
            TxtScript.CaretIndex = Math.Max(0, Math.Min(caret, TxtScript.Text.Length));
            TxtScript.IsReadOnly = true;
            _suppressUndoCapture = false;
            _flushingToScript    = false;

            int line = GetCaretLine();
            _savedSelStartLine = line;
            _savedSelEndLine   = line;
            _arrowAnchorLine   = -1;
            _dragLine          = -1;

            MarkScriptDirty();
            UpdateLineNumbers();
            EnsureScriptLineVisible(line);
            HighlightCurrentLine();
            TrySelectCommandFromCurrentLine();
        }

        private void ScriptUndo()
        {
            if (_undoStack.Count == 0) { FlashStatus("Nothing to undo", "#8B92A9"); return; }
            _redoStack.Push((TxtScript.Text, TxtScript.CaretIndex));
            var (text, caret) = _undoStack.Pop();
            RestoreScriptSnapshot(text, caret);
        }

        private void ScriptRedo()
        {
            if (_redoStack.Count == 0) { FlashStatus("Nothing to redo", "#8B92A9"); return; }
            _undoStack.Push((TxtScript.Text, TxtScript.CaretIndex));
            var (text, caret) = _redoStack.Pop();
            RestoreScriptSnapshot(text, caret);
        }

        // ══════════════════════════════════════════════════════════════
        // HELP BUTTON - reads from PSM1 (offline, one-time per session)
        // ══════════════════════════════════════════════════════════════
        /// <summary>
        /// Locates PSAppDeployToolkit.psm1 near the loaded script and extracts
        /// the comment-based help block for <paramref name="funcName"/>.
        /// </summary>
        private string ExtractHelpFromPsm1(string funcName)
        {
            // Find the PSM1: look relative to the loaded script path
            string? psm1 = FindPsm1();
            if (psm1 == null) return "";

            try
            {
                string src = File.ReadAllText(psm1);
                // Locate  "function FuncName"  then grab the <# ... #> block
                int fIdx = src.IndexOf("function " + funcName, StringComparison.OrdinalIgnoreCase);
                if (fIdx < 0) return "";

                // Walk forward to find <#
                int blockStart = src.IndexOf("<#", fIdx);
                if (blockStart < 0) return "";
                int blockEnd = src.IndexOf("#>", blockStart + 2);
                if (blockEnd < 0) return "";

                string block = src.Substring(blockStart + 2, blockEnd - blockStart - 2);

                // Parse sections: .SYNOPSIS .DESCRIPTION .PARAMETER .EXAMPLE .NOTES
                var sb = new StringBuilder();
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine("  " + funcName);
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine();

                string ParseSection(string tag)
                {
                    // Match  .TAG\r?\n  ... up to next  .UPPERCASE_WORD or end
                    var m = Regex.Match(block,
                        @"\." + tag + @"\s*\r?\n(.*?)(?=\s*\.[A-Z]|\z)",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (!m.Success) return "";
                    string t = m.Groups[1].Value;
                    t = Regex.Replace(t, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                    t = Regex.Replace(t, @"[ \t]{2,}", " ");
                    t = Regex.Replace(t, @"\r\n|\r", "\n");
                    t = Regex.Replace(t, @"\n{3,}", "\n\n");
                    return t.Trim();
                }

                IEnumerable<string> ParseAllSections(string tag)
                {
                    // Returns each occurrence of .TAG block (for PARAMETER, EXAMPLE which repeat)
                    var matches = Regex.Matches(block,
                        @"\." + tag + @"[ \t]+([^\r\n]*)\r?\n(.*?)(?=\s*\.[A-Z]|\z)",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        string header = m.Groups[1].Value.Trim();
                        string body   = m.Groups[2].Value;
                        body = Regex.Replace(body, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                        body = Regex.Replace(body, @"[ \t]{2,}", " ");
                        body = Regex.Replace(body, @"\r\n|\r", "\n");
                        body = Regex.Replace(body, @"\n{3,}", "\n\n");
                        yield return (header.Length > 0 ? header + "\n" : "") + body.Trim();
                    }
                }

                // SYNOPSIS
                string syn = ParseSection("SYNOPSIS");
                if (syn.Length > 0)
                { sb.AppendLine("SYNOPSIS"); sb.AppendLine("────────────────────────────────────"); sb.AppendLine(syn); sb.AppendLine(); }

                // DESCRIPTION
                string desc = ParseSection("DESCRIPTION");
                if (desc.Length > 0)
                { sb.AppendLine("DESCRIPTION"); sb.AppendLine("────────────────────────────────────"); sb.AppendLine(desc); sb.AppendLine(); }

                // PARAMETERS (each .PARAMETER <name>)
                var parameters = ParseAllSections("PARAMETER").ToList();
                if (parameters.Count > 0)
                {
                    sb.AppendLine("PARAMETERS");
                    sb.AppendLine("────────────────────────────────────");
                    foreach (string p in parameters) { sb.AppendLine(p); sb.AppendLine(); }
                }

                // EXAMPLES
                var exBlocks = Regex.Matches(block,
                    @"\.EXAMPLE\s*\r?\n(.*?)(?=\s*\.[A-Z]|\z)",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (exBlocks.Count > 0)
                {
                    sb.AppendLine("EXAMPLES");
                    sb.AppendLine("────────────────────────────────────");
                    int n = 1;
                    foreach (Match ex in exBlocks)
                    {
                        string exText = ex.Groups[1].Value;
                        exText = Regex.Replace(exText, @"\r\n|\r", "\n");
                        exText = Regex.Replace(exText, @"\n{3,}", "\n\n").Trim();
                        sb.AppendLine("Example " + n++ + ":");
                        sb.AppendLine(exText);
                        sb.AppendLine();
                    }
                }

                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                sb.AppendLine("  Source: " + psm1);
                sb.AppendLine("═══════════════════════════════════════════════════════════════");
                return sb.ToString().Trim();
            }
            catch { return ""; }
        }

        private string? FindPsm1()
        {
            // Use cached path from previous call
            if (_psm1SourcePath != null && File.Exists(_psm1SourcePath))
                return _psm1SourcePath;

            var searchRoots = new List<string?>();
            if (_createdScriptPath != null)
            {
                string? scriptDir = Path.GetDirectoryName(_createdScriptPath);
                searchRoots.Add(scriptDir);
                searchRoots.Add(scriptDir != null ? Path.GetDirectoryName(scriptDir) : null);
                searchRoots.Add(scriptDir != null ? Path.Combine(scriptDir, "AppDeployToolkit") : null);
            }

            foreach (string? root in searchRoots)
            {
                if (root == null || !Directory.Exists(root)) continue;
                try
                {
                    var found = Directory.GetFiles(root, "PSAppDeployToolkit.psm1",
                        SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) { _psm1SourcePath = found; return found; }
                }
                catch { }
            }
            return null;
        }

        private static string ExtractHelpText(string html, string funcName, string url)
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("  " + funcName);
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine();

            // ── SYNOPSIS ────────────────────────────────────────────────────
            // Try h2 Synopsis heading first, then lead paragraph
            var synM = Regex.Match(html,
                @"<h[23][^>]*>\s*Synopsis\s*</h[23]>\s*(.*?)(?=<h[23])",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!synM.Success)
                synM = Regex.Match(html,
                    @"<p[^>]*class=""[^""]*lead[^""]*""[^>]*>(.*?)</p>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (synM.Success)
            {
                string syn = CleanSection(synM.Groups[1].Value);
                if (syn.Length > 0)
                { sb.AppendLine("SYNOPSIS"); sb.AppendLine("────────────────────────────────────"); sb.AppendLine(syn); sb.AppendLine(); }
            }

            // ── SYNTAX ──────────────────────────────────────────────────────
            var synxM = Regex.Match(html,
                @"<h[23][^>]*>\s*Syntax\s*</h[23]>(.*?)(?=<h[23])",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!synxM.Success)
                synxM = Regex.Match(html, @"<pre[^>]*>(.*?)</pre>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (synxM.Success)
            {
                // Extract <code> blocks first for cleaner output
                string sec = synxM.Groups[1].Value;
                var codes = Regex.Matches(sec, @"<code[^>]*>(.*?)</code>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                string syntax = codes.Count > 0
                    ? string.Join("\n", codes.Cast<Match>()
                        .Select(c => System.Net.WebUtility.HtmlDecode(
                            Regex.Replace(c.Groups[1].Value, @"<[^>]+>", "")).Trim()))
                    : CleanSection(sec);
                if (syntax.Length > 0)
                { sb.AppendLine("SYNTAX"); sb.AppendLine("────────────────────────────────────"); sb.AppendLine(syntax); sb.AppendLine(); }
            }

            // ── PARAMETERS ──────────────────────────────────────────────────
            var paramM = Regex.Match(html,
                @"<h[23][^>]*>\s*Parameters?\s*</h[23]>(.*?)(?=<h[23][^>]*>\s*(?:Example|Return|Note|See|Remark|Output))",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!paramM.Success)
                paramM = Regex.Match(html,
                    @"<h[23][^>]*>\s*Parameters?\s*</h[23]>(.*?)(?=<h[23]|</article|</main)",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (paramM.Success)
            {
                string pt = CleanSection(paramM.Groups[1].Value);
                if (pt.Length > 0)
                { sb.AppendLine("PARAMETERS"); sb.AppendLine("────────────────────────────────────"); sb.AppendLine(pt); sb.AppendLine(); }
            }

            // ── EXAMPLES ────────────────────────────────────────────────────
            var exM = Regex.Match(html,
                @"<h[23][^>]*>\s*Examples?\s*</h[23]>(.*?)(?=<h[23][^>]*>|</article|</main|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (exM.Success)
            {
                string sec = exM.Groups[1].Value;
                // Split into individual example blocks (each .EXAMPLE section)
                var exBlocks = Regex.Split(sec, @"<h[34][^>]*>.*?</h[34]>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                var esb = new StringBuilder();
                int n = 1;
                foreach (string block in exBlocks)
                {
                    if (string.IsNullOrWhiteSpace(block)) continue;
                    // Get code
                    var codeM = Regex.Match(block, @"<code[^>]*>(.*?)</code>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    // Get description (text outside code blocks)
                    string desc = Regex.Replace(block, @"<code[^>]*>.*?</code>", "",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    desc = CleanSection(desc);

                    if (codeM.Success)
                    {
                        string code = System.Net.WebUtility.HtmlDecode(
                            Regex.Replace(codeM.Groups[1].Value, @"<[^>]+>", "")).Trim();
                        if (code.Length > 0)
                        {
                            esb.AppendLine($"Example {n++}:");
                            esb.AppendLine(code);
                            if (!string.IsNullOrWhiteSpace(desc))
                                esb.AppendLine("  " + desc.Replace("\n", "\n  "));
                            esb.AppendLine();
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(desc))
                    {
                        // No code block - plain text example
                        esb.AppendLine($"Example {n++}:");
                        esb.AppendLine(desc);
                        esb.AppendLine();
                    }
                }

                // Fallback: if block splitting found nothing, use full section
                if (n == 1)
                {
                    var codes = Regex.Matches(sec, @"<code[^>]*>(.*?)</code>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    foreach (Match c in codes)
                    {
                        string code = System.Net.WebUtility.HtmlDecode(
                            Regex.Replace(c.Groups[1].Value, @"<[^>]+>", "")).Trim();
                        if (code.Length > 0)
                        { esb.AppendLine($"Example {n++}:"); esb.AppendLine(code); esb.AppendLine(); }
                    }
                    if (n == 1) esb.AppendLine(CleanSection(sec));
                }

                string extext = esb.ToString().Trim();
                if (extext.Length > 0)
                { sb.AppendLine("EXAMPLES"); sb.AppendLine("────────────────────────────────────"); sb.AppendLine(extext); sb.AppendLine(); }
            }

            // Fallback: plain text strip from function name onward
            if (sb.Length < 250)
            {
                string plain = StripTags(html);
                plain = Regex.Replace(plain, @"[ \t]+", " ");
                plain = Regex.Replace(plain, @"\n{3,}", "\n\n");
                int fi = plain.IndexOf(funcName, StringComparison.OrdinalIgnoreCase);
                if (fi > 0) plain = plain[fi..];
                if (plain.Length > 6000) plain = plain[..6000] + "\n[…truncated]";
                sb.AppendLine(plain.Trim());
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("  Source: " + url);
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            return sb.ToString().Trim();
        }

        private static string CleanSection(string html)
        {
            string t = StripTags(html);
            t = Regex.Replace(t, @"[ \t]{2,}", " ");
            t = Regex.Replace(t, @"\n{3,}", "\n\n");
            return t.Trim();
        }

        private static string StripTags(string html)
        {
            html = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</li>|</h\d>|</tr>|</dt>|</dd>", "\n",
                RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", "");
            html = System.Net.WebUtility.HtmlDecode(html);
            return html;
        }

        private void ShowHelpWindow(string funcName, string content)
        {
            // Use a real WindowStyle so OS minimize/maximize work correctly
            var win = new Window {
                Width = 920, Height = 600,
                MinWidth = 640, MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.CanResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                ShowInTaskbar = true,
                Background = TC("WindowBgBrush"),
                Title = funcName + " - Help"
            };

            var root = new Border
            {
                Background = TC("DlgBodyBgBrush"),
                BorderBrush = TC("DlgBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            root.MouseLeftButtonDown += (_, ev) => { if (win.WindowState == WindowState.Normal) win.DragMove(); };

            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Header with Maximize + Close ─────────────────────────────
            var hdr = new Border
            {
                Background = TC("FooterBgBrush"),
                CornerRadius = new CornerRadius(12, 12, 0, 0),
                Padding = new Thickness(20, 12, 12, 12)
            };
            var hdrGrid = new Grid();
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconTb  = new TextBlock { Text = "❓", FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var titleTb = new TextBlock { Text = funcName, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = TC("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center };

            // Window control buttons
            var winBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,4,0) };

            Button MakeWinBtn(string txt, int w = 36) => new Button
            {
                Content = txt, Width = w, Height = 30, FontSize = 14,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = TC("TextSecondaryBrush"),
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.Bold
            };

            var btnMinimize = MakeWinBtn("-");
            btnMinimize.ToolTip = "Minimize";
            btnMinimize.Margin = new Thickness(0, 0, 2, 0);
            btnMinimize.Click += (_, _) => win.WindowState = WindowState.Minimized;
            winBtns.Children.Add(btnMinimize);

            var btnMaxRestore = MakeWinBtn("🗖");
            btnMaxRestore.ToolTip = "Maximize";
            btnMaxRestore.Click += (_, _) =>
            {
                if (win.WindowState == WindowState.Maximized)
                {
                    win.WindowState = WindowState.Normal;
                    btnMaxRestore.Content = "🗖";
                    btnMaxRestore.ToolTip = "Maximize";
                    root.CornerRadius = new CornerRadius(12);
                    hdr.CornerRadius  = new CornerRadius(12, 12, 0, 0);
                }
                else
                {
                    win.WindowState = WindowState.Maximized;
                    btnMaxRestore.Content = "🗗";
                    btnMaxRestore.ToolTip = "Restore";
                    root.CornerRadius = new CornerRadius(0);
                    hdr.CornerRadius  = new CornerRadius(0);
                }
            };

            var btnClose = MakeWinBtn("✕");
            btnClose.ToolTip = "Close";
            btnClose.Click += (_, _) => win.Close();

            btnMaxRestore.Margin = new Thickness(0, 0, 2, 0);
            winBtns.Children.Add(btnMaxRestore);
            winBtns.Children.Add(btnClose);

            Grid.SetColumn(iconTb,  0);
            Grid.SetColumn(titleTb, 1);
            Grid.SetColumn(winBtns, 2);
            hdrGrid.Children.Add(iconTb);
            hdrGrid.Children.Add(titleTb);
            hdrGrid.Children.Add(winBtns);
            hdr.Child = hdrGrid;
            Grid.SetRow(hdr, 0);

            // ── Body ────────────────────────────────────────────────────
            var body = new Border { Background = TC("ScriptBgBrush") };
            var sv = new ScrollViewer
            {
                Margin = new Thickness(20, 14, 4, 14),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            sv.Content = new TextBlock
            {
                Text = content,
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Foreground = TC("TextPrimaryBrush"),
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = 20
            };
            body.Child = sv;
            Grid.SetRow(body, 1);

            // ── Footer ───────────────────────────────────────────────────
            var ftr = new Border
            {
                BorderBrush = TC("DlgBorderBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 10, 20, 10),
                Background = TC("DlgFooterBgBrush"),
                CornerRadius = new CornerRadius(0, 0, 12, 12)
            };
            var fs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            string helpUrl = $"https://psappdeploytoolkit.com/docs/reference/functions/{funcName}";
            var bWeb = DlgBtnStyled("🌐 Open in Browser", "blue", 160);
            bWeb.Margin = new Thickness(0, 0, 10, 0);
            bWeb.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(helpUrl) { UseShellExecute = true }); } catch { } };
            var bClose = DlgBtnStyled("✕ Close", "green", 100);
            bClose.Margin = new Thickness(0);
            bClose.Click += (_, _) => win.Close();
            fs.Children.Add(bWeb); fs.Children.Add(bClose);
            ftr.Child = fs;
            Grid.SetRow(ftr, 2);

            outer.Children.Add(hdr);
            outer.Children.Add(body);
            outer.Children.Add(ftr);
            root.Child = outer;
            win.Content = root;
            win.Show();
        }

        // ══════════════════════════════════════════════════════════════
        // SCRIPT TOOLBAR ACTIONS
        // ══════════════════════════════════════════════════════════════
        private void BtnScriptSave_Click(object sender, RoutedEventArgs e)       => ScriptSave();

        // ── Close Script ─────────────────────────────────────────────
        private void BtnScriptClose_Click(object sender, RoutedEventArgs e)
        {
            // PromptSaveAndClose handles dirty-check and script unload.
            // Then close the application window (same as X button).
            if (PromptSaveAndClose())
                Application.Current.MainWindow?.Close();
        }

        /// <summary>Shows a themed Save/Discard/Cancel dialog if dirty, then clears the script.</summary>
        public bool PromptSaveAndClose()
        {
            if (_createdScriptPath == null) return true;  // nothing loaded
            if (_scriptDirty)
            {
                var result = ShowSaveChangesDialog(System.IO.Path.GetFileName(_createdScriptPath));
                if (result == SaveChangesResult.Cancel) return false;
                if (result == SaveChangesResult.Save)   ScriptSave();
            }
            // Unload script and reset all state
            _createdScriptPath = null;
            _scriptDirty = false;
            if (TxtScriptDirty  != null) TxtScriptDirty.Visibility = Visibility.Collapsed;
            if (TxtScript       != null) { TxtScript.IsReadOnly = false; TxtScript.Clear(); TxtScript.IsReadOnly = true; }
            if (PanelNoScript   != null) PanelNoScript.Visibility = Visibility.Visible;
            if (TxtStatus       != null) TxtStatus.Text = "* Required fields";
            ClearParamPanel();
            ActivateTab(false); // switch back to Info tab
            return true;
        }

        public enum SaveChangesResult { Save, Discard, Cancel }

        private SaveChangesResult ShowSaveChangesDialog(string fileName)
        {
            var result = SaveChangesResult.Cancel;

            var win = BuildNewDialog("Create Package - Save Changes");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) =
                BuildNewDialogShell(win, "Create Package - Save Changes");
            int r = 0;

            // Row 1: file name
            NdAddRow(cg, r++, "File",   fileName, lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            // Row 2: question
            NdAddRow(cg, r++, "Action", "Do you want to save the changes you made to this file?",
                     lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);

            // Button row with 💾 icon
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var saveIcon = NdDecisionIcon();
            Grid.SetRow(saveIcon, r); Grid.SetColumn(saveIcon, 0);
            cg.Children.Add(saveIcon);

            var btns = NdMakeButtons(sec,
                ("✔ Yes",    () => { result = SaveChangesResult.Save;    win.Close(); }),
                ("✕ No",     () => { result = SaveChangesResult.Discard; win.Close(); }),
                ("✕ Cancel", () => { result = SaveChangesResult.Cancel;  win.Close(); })
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);

            win.ShowDialog();
            return result;
        }

        private void BtnScriptOpen_Click(object sender, RoutedEventArgs e)       => ScriptOpenInEditor();
        private void BtnScriptDelete_Click(object sender, RoutedEventArgs e)     => ScriptDeleteLines();
        private void BtnScriptCut_Click(object sender, RoutedEventArgs e)        => ScriptCutLines();
        private void BtnScriptCopy_Click(object sender, RoutedEventArgs e)       => ScriptCopyLines();
        private void BtnScriptPaste_Click(object sender, RoutedEventArgs e)      => ScriptPaste();
        private void BtnScriptMoveUp_Click(object sender, RoutedEventArgs e)     => ScriptMoveLines(-1);
        private void BtnScriptMoveDown_Click(object sender, RoutedEventArgs e)   => ScriptMoveLines(1);
        private void BtnScriptComment_Click(object sender, RoutedEventArgs e)    => ScriptToggleComment(true);
        private void BtnScriptUncomment_Click(object sender, RoutedEventArgs e)  => ScriptToggleComment(false);
        private void BtnScriptInsert_Click(object sender, RoutedEventArgs e)
        {
            // Insert one empty line below the current selected line
            CaptureUndoState();
            var (s, _, lines) = GetSelectionLines();
            // Walk to end of selected start line
            int insertAt = 0;
            for (int i = 0; i <= s && i < lines.Length; i++)
                insertAt += lines[i].Length + (i < lines.Length - 1 ? 1 : 0);

            _flushingToScript = true;
            try
            {
                TxtScript.IsReadOnly = false;
                TxtScript.Text = TxtScript.Text.Insert(insertAt, "\n");
                TxtScript.CaretIndex = Math.Min(insertAt + 1, TxtScript.Text.Length);
                TxtScript.IsReadOnly = true;
            }
            finally
            {
                _flushingToScript = false;
            }
            int landingLine3 = s + 1;
            int total = TxtScript.Text.Split('\n').Length;
            _savedSelStartLine = Math.Min(landingLine3, total - 1);
            _savedSelEndLine   = _savedSelStartLine;
            _arrowAnchorLine   = -1;
            _dragLine          = -1;
            MarkScriptDirty(); UpdateLineNumbers();
            TrySelectCommandFromCurrentLine();
            HighlightCurrentLine();
        }

        // ── Install / Uninstall / Repair ─────────────────────────────


        /// <summary>
        /// Parses PSADT script header comments and AppDeployToolkitMain.ps1 defaults
        /// to populate the Info tab fields.
        /// </summary>
        private void ImportInfoFieldsFromScript(string content, string ps1Path)
        {
            try
            {
                // Try to read defaults from AppDeployToolkitConfig in same folder tree
                string scriptDir = Path.GetDirectoryName(ps1Path) ?? "";
                var defaults = ReadPsadtDefaults(scriptDir);

                // Parse key=value pairs from the script's variable assignments
                // e.g. $appName = 'WinZip'
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var varRx = new System.Text.RegularExpressions.Regex(
                    @"^\s*\$(?<name>app\w+)\s*=\s*['""](?<val>[^'""]*)['""]",
                    System.Text.RegularExpressions.RegexOptions.Multiline |
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in varRx.Matches(content))
                    vars[m.Groups["n"].Value] = m.Groups["val"].Value;

                void Set(System.Windows.Controls.TextBox tb, string key)
                {
                    if (vars.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v))
                        tb.Text = v;
                    else if (defaults.TryGetValue(key, out string? d) && !string.IsNullOrWhiteSpace(d))
                        tb.Text = d;
                }

                Set(TxtAppVendor,   "appVendor");
                Set(TxtAppName,     "appName");
                Set(TxtAppVersion,  "appVersion");
                Set(TxtAppArch,     "appArch");
                Set(TxtAppLang,     "appLang");
                Set(TxtAppRevision, "appRevision");

                // Set source/template paths from script location
                if (!string.IsNullOrEmpty(scriptDir))
                {
                    TxtTemplatePath.Text = scriptDir;
                    TxtSourcePath.Text   = scriptDir;
                }

                ApplyPsadtDefaults(defaults);
            }
            catch { /* best-effort - don't block import on parse failure */ }
        }

        private void BtnJumpInstall_Click(object sender, RoutedEventArgs e)   => RunDeployAction("Install");
        private void BtnJumpUninstall_Click(object sender, RoutedEventArgs e) => RunDeployAction("Uninstall");
        private void BtnJumpRepair_Click(object sender, RoutedEventArgs e)    => RunDeployAction("Repair");

        /// <summary>
        /// Reload - re-reads Invoke-AppDeployToolkit.ps1 from disk and refreshes
        /// both the Script tab editor and the Info tab fields.
        /// </summary>
        /// <summary>
        /// Expand/collapse the left panels (Commands + Params columns).
        /// When expanded: hides columns 0-3, script takes full width, icon changes to ➡.
        /// When collapsed: restores original widths, icon changes back to ⬅.
        /// </summary>
        private bool _scriptExpanded = false;

        private void BtnScriptExpand_Click(object sender, RoutedEventArgs e)
        {
            _scriptExpanded = !_scriptExpanded;

            if (_scriptExpanded)
            {
                // Hide left panels - collapse columns 0-3 to zero
                ColCommands.Width    = new GridLength(0);
                ColCommands.MinWidth = 0;
                ColSplit1.Width      = new GridLength(0);
                ColParams.Width      = new GridLength(0);
                ColParams.MinWidth   = 0;
                ColSplit2.Width      = new GridLength(0);

                // Show "restore" icon (right-pointing), hide "collapse" icon
                IconCollapse.Visibility      = Visibility.Collapsed;
                IconExpand.Visibility        = Visibility.Visible;
                BtnScriptExpand.ToolTip      = "Restore left panels";
            }
            else
            {
                // Restore original widths
                ColCommands.Width    = new GridLength(270);
                ColCommands.MinWidth = 270;
                ColSplit1.Width      = new GridLength(8);
                ColParams.Width      = new GridLength(270);
                ColParams.MinWidth   = 270;
                ColSplit2.Width      = new GridLength(8);

                // Show "collapse" icon (left-pointing), hide "restore" icon
                IconCollapse.Visibility      = Visibility.Visible;
                IconExpand.Visibility        = Visibility.Collapsed;
                BtnScriptExpand.ToolTip      = "Expand script editor (hide left panels)";
            }
        }

        private void BtnScriptReload_Click(object sender, RoutedEventArgs e)
        {
            string? path = _createdScriptPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                ShowReloadInfoDialog("No script is currently loaded. Create a package first.");
                return;
            }

            if (_scriptDirty)
            {
                if (!ShowReloadConfirmDialog()) return;
            }

            // Reload Script tab
            LoadScriptIntoEditor(path);

            // Reload Info tab fields from the script
            try
            {
                string content = File.ReadAllText(path, Encoding.UTF8);
                ImportInfoFieldsFromScript(content, path);
            }
            catch (Exception ex)
            {
                ShowReloadInfoDialog($"Reload failed:\n{ex.Message}");
            }
        }

        // Themed confirm dialog for reload-with-unsaved-changes (matches the app's other dialogs).
        private bool ShowReloadConfirmDialog()
        {
            bool confirmed = false;
            var win = BuildNewDialog("Create Package - Reload");
            win.Width = 380; win.MaxWidth = 380;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - Reload");
            int r = 0;
            NdAddRow(cg, r++, "Status", "The script has unsaved changes. Reloading will discard them.", lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            NdAddRow(cg, r++, "Action", "Do you want to continue?", lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = NdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = NdMakeButtons(sec,
                ("✔ Yes", () => { confirmed = true;  win.Close(); }),
                ("✕ No",  () => { confirmed = false; win.Close(); })
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
            return confirmed;
        }

        // Themed info dialog for reload messages (no script loaded / reload failed).
        private void ShowReloadInfoDialog(string message)
        {
            var win = BuildNewDialog("Create Package - Reload");
            win.Width = 380; win.MaxWidth = 380;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - Reload");
            int r = 0;
            NdAddRow(cg, r++, "Status", message, lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = NdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = NdMakeButtons(sec, ("✔ OK", () => win.Close()));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
        }

        // Generic themed info dialog (matches the app's other dialogs) - used for the
        // Create-package "Failed" / "Cancelled" messages instead of a plain MessageBox.
        private void ShowThemedInfoDialog(string title, string label, string message)
        {
            var win = BuildNewDialog("Create Package - " + title);
            win.Width = 460; win.MaxWidth = 460;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - " + title);
            int r = 0;
            NdAddRow(cg, r++, label, message, lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dIcon = NdDecisionIcon(); Grid.SetRow(dIcon, r); Grid.SetColumn(dIcon, 0); cg.Children.Add(dIcon);
            var btns = NdMakeButtons(sec, ("✔ OK", () => win.Close()));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
        }

        private void BtnScriptCancel_Click(object sender, RoutedEventArgs e)
        {
            // Show confirmation before cancelling
            if (!ShowCancelConfirmDialog(_currentDeploymentType)) return;

            // Write the cancel sentinel file immediately - the elevated wrapper polls for
            // this every 200ms and calls taskkill /T /F on the inner process itself.
            // This avoids any second UAC prompt.
            string? cf = _deployCancelFile;
            if (!string.IsNullOrEmpty(cf))
                try { File.WriteAllText(cf, "cancel"); } catch { }
            _deployCts?.Cancel();
        }

        private void ShowNoScriptDialog(string deploymentType)
        {
            string icon   = deploymentType == "Install"   ? "▶"
                          : deploymentType == "Uninstall" ? "◀"
                          : "🔧";
            string colour = deploymentType == "Install"   ? "green"
                          : deploymentType == "Uninstall" ? "amber"
                          : "blue";

            var win = MakeDialogWindow(460, 260);
            win.Topmost = true;
            var root  = MakeDialogRoot(win);
            var outer = MakeDialogGrid();

            var hdr = MakeDialogHeaderThemed(icon, deploymentType + " - No Script Loaded", colour, () => win.Close());
            Grid.SetRow(hdr, 0);

            var body = new StackPanel { Margin = new Thickness(26, 22, 26, 14) };

            var msgTitle = new TextBlock
            {
                Text       = "No package script has been created yet.",
                FontSize   = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TC("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var msgBody = new TextBlock
            {
                Text = "Go to the \u2018Info\u2019 tab, fill in the package details and click \u2018Create\u2019 to generate the script. "
                     + "Then return here to run " + deploymentType + ".",
                FontSize = 12,
                Foreground = TC("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            };

            // Coloured tip row
            var tipBorder = new Border
            {
                Background    = TC("DeployCard" + (deploymentType == "Install" ? "Install" : deploymentType == "Uninstall" ? "Uninstall" : "Repair") + "BgBrush"),
                BorderBrush   = TC("DeployCard" + (deploymentType == "Install" ? "Install" : deploymentType == "Uninstall" ? "Uninstall" : "Repair") + "BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius  = new CornerRadius(6),
                Padding       = new Thickness(12, 8, 12, 8),
                Margin        = new Thickness(0, 10, 0, 0)
            };
            tipBorder.Child = new TextBlock
            {
                Text = "\u2192  Info tab  \u203a  Fill fields  \u203a  Create  \u203a  Script tab  \u203a  " + deploymentType,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = TC("DeployCard" + (deploymentType == "Install" ? "Install" : deploymentType == "Uninstall" ? "Uninstall" : "Repair") + "FgBrush"),
                FontFamily = new FontFamily("Consolas")
            };

            body.Children.Add(msgTitle);
            body.Children.Add(msgBody);
            body.Children.Add(tipBorder);
            Grid.SetRow(body, 1);

            var ftr = new Border
            {
                BorderBrush = TC("DlgBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 12, 20, 12), Background = TC("DlgFooterBgBrush")
            };
            var btn = DlgBtnStyled("✔ Got it", colour, 120);
            btn.Click += (_, _) => win.Close();
            var fs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            fs.Children.Add(btn);
            ftr.Child = fs;
            Grid.SetRow(ftr, 2);

            outer.Children.Add(hdr);
            outer.Children.Add(body);
            outer.Children.Add(ftr);
            root.Child = outer;
            win.Content = root;
            win.ShowDialog();
        }

        private bool ShowCancelConfirmDialog(string deploymentType)
        {
            bool confirmed = false;
            var win = MakeDialogWindow(420, 220);   // owned by main window
            win.Topmost  = true;                    // stay above the PSADT install prompt
            win.ShowInTaskbar = false;
            var root = MakeDialogRoot(win);
            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string action = string.IsNullOrWhiteSpace(deploymentType) ? "operation" : deploymentType.ToLower();
            var hdr = MakeDialogHeaderThemed("⚠", "Cancel " + (string.IsNullOrWhiteSpace(deploymentType) ? "Operation" : deploymentType),
                "amber", () => win.Close());
            Grid.SetRow(hdr, 0);

            var body = new Border
            {
                Padding = new Thickness(24, 20, 24, 16),
                Background = TC("DlgBodyBgBrush")
            };
            var bodyStack = new StackPanel();
            bodyStack.Children.Add(new TextBlock
            {
                Text = $"This will cancel the {action}.",
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = TC("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            bodyStack.Children.Add(new TextBlock
            {
                Text = "Are you sure you want to cancel?",
                FontSize = 12,
                Foreground = TC("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            body.Child = bodyStack;
            Grid.SetRow(body, 1);

            var ftr = new Border
            {
                BorderBrush = TC("DlgBorderBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 12, 20, 12),
                Background = TC("DlgFooterBgBrush")
            };
            var fs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var bNo = DlgBtnStyled("✕ No", "neutral", 90);
            bNo.Click += (_, _) => win.Close();

            var bYes = DlgBtnStyled("✔ Yes", "amber", 90);
            bYes.Click += (_, _) => { confirmed = true; win.Close(); };

            fs.Children.Add(bNo);
            fs.Children.Add(bYes);
            ftr.Child = fs;
            Grid.SetRow(ftr, 2);

            outer.Children.Add(hdr);
            outer.Children.Add(body);
            outer.Children.Add(ftr);
            root.Child = outer;
            win.Content = root;
            win.ShowDialog();   // blocks UI thread - install process continues in background thread
            return confirmed;
        }

        private static void KillDeployProcessTree(int pid)
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                    { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(3000);
            }
            catch { }
        }

        private async void RunDeployAction(string deploymentType)
        {
            if (_createdScriptPath == null || !File.Exists(_createdScriptPath))
            {
                ShowNoScriptDialog(deploymentType);
                return;
            }

            // ── Professional confirm dialog ───────────────────────────
            bool confirmed = ShowDeployConfirmDialog(deploymentType, _createdScriptPath);
            if (!confirmed) return;

            // ── Show in-progress indicator + progress bar + cancel button ──
            string progressMsg = deploymentType + " in progress. Please wait.";
            _deployCts = new System.Threading.CancellationTokenSource();
            _currentDeploymentType = deploymentType;
            var ct = _deployCts.Token;
            bool cancelled = false;

            Dispatcher.Invoke(() =>
            {
                TxtScriptRunning.Text           = progressMsg;
                BorderScriptRunning.Visibility  = Visibility.Visible;
                ScriptProgressBar.Visibility    = Visibility.Visible;
            });


            string scriptDir = Path.GetDirectoryName(_createdScriptPath) ?? ".";
            string logDir    = ReadLogPathFromConfig(scriptDir);
            int    exitCode  = -1;
            string errorMsg  = "";

            string exitCodeFile = Path.Combine(Path.GetTempPath(),
                "SwiftPack_exit_"   + Guid.NewGuid().ToString("N") + ".txt");
            string cancelFile = Path.Combine(Path.GetTempPath(),
                "SwiftPack_cancel_" + Guid.NewGuid().ToString("N") + ".tmp");
            string wrapperPs1 = Path.Combine(Path.GetTempPath(),
                "SwiftPack_wrap_"   + Guid.NewGuid().ToString("N") + ".ps1");

            try
            {
                // Prefer .exe over .ps1
                string exePath = Path.Combine(scriptDir,
                    Path.GetFileNameWithoutExtension(_createdScriptPath) + ".exe");
                bool useExe = File.Exists(exePath);

                string scriptArg  = deploymentType == "Install" ? "" : deploymentType;
                string escapedExit   = exitCodeFile.Replace("'", "''");
                string escapedCancel = cancelFile.Replace("'", "''");

                // The wrapper runs elevated. It starts the inner process, then polls every 500ms
                // for a cancel sentinel file dropped by C#. If found, it kills the inner process
                // itself - no second UAC needed because the wrapper is already elevated.
                string innerLaunch = useExe
                    ? "Start-Process -FilePath '" + exePath.Replace("'","''") + "'" +
                      (scriptArg.Length > 0 ? " -ArgumentList @('" + scriptArg + "')" : "") +
                      " -PassThru"
                    : "Start-Process -FilePath 'powershell.exe'" +
                      " -ArgumentList @('-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File','" +
                      _createdScriptPath.Replace("'","''") + "'" +
                      (scriptArg.Length > 0 ? ",'" + scriptArg + "'" : "") + ")" +
                      " -PassThru -WindowStyle Hidden";

                string wrapperContent =
                    "$exitPath   = '" + escapedExit   + "'\r\n" +
                    "$cancelPath = '" + escapedCancel + "'\r\n" +
                    "try {\r\n" +
                    "    if (Test-Path $cancelPath) { -2 | Out-File $exitPath -Encoding ASCII -Force; exit }\r\n" +
                    "    $p = " + innerLaunch + "\r\n" +
                    "    while (-not $p.HasExited) {\r\n" +
                    "        if (Test-Path $cancelPath) {\r\n" +
                    "            & taskkill /PID $p.Id /T /F 2>$null\r\n" +
                    "            -2 | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                    "            exit\r\n" +
                    "        }\r\n" +
                    "        Start-Sleep -Milliseconds 200\r\n" +
                    "    }\r\n" +
                    "    $p.ExitCode | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                    "} catch {\r\n" +
                    "    -1 | Out-File $exitPath -Encoding ASCII -Force\r\n" +
                    "}\r\n";

                File.WriteAllText(wrapperPs1, wrapperContent, new UTF8Encoding(true));
                _deployCancelFile = cancelFile;

                await Task.Run(() =>
                {
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
                    elevProc.Start();
                    _deployProcess = elevProc;

                    // Poll for cancellation every 200ms - drop cancel sentinel file,
                    // the elevated wrapper detects it and runs taskkill /T /F itself.
                    while (!elevProc.WaitForExit(200))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            cancelled = true;
                            try { File.WriteAllText(cancelFile, "cancel"); } catch { }
                            // Wait for wrapper to finish killing and exit (max 10s)
                            elevProc.WaitForExit(10000);
                            return;
                        }
                    }

                    _deployProcess    = null;
                    _deployCancelFile = null;

                    if (File.Exists(exitCodeFile))
                    {
                        try
                        {
                            string raw = File.ReadAllText(exitCodeFile).Trim();
                            if (int.TryParse(raw, out int parsed)) exitCode = parsed;
                        }
                        catch { }
                    }
                }, ct);
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { errorMsg = ex.Message; }
            finally
            {
                _deployProcess    = null;
                _deployCts        = null;
                _deployCancelFile = null;
                _currentDeploymentType = "";
                Dispatcher.Invoke(() =>
                {
                    BorderScriptRunning.Visibility = Visibility.Collapsed;
                    ScriptProgressBar.Visibility   = Visibility.Collapsed;
                });
                try { if (File.Exists(wrapperPs1))  File.Delete(wrapperPs1);  } catch { }
                try { if (File.Exists(exitCodeFile)) File.Delete(exitCodeFile); } catch { }
                try { if (File.Exists(cancelFile))   File.Delete(cancelFile);   } catch { }
            }

            if (cancelled)
            {
                return;
            }

            bool success = exitCode == 0 || exitCode == 3010;
            Dispatcher.Invoke(() =>
            {
                FlashStatus(success ? "✓ " + deploymentType + " Done" : "✗ Failed (exit " + exitCode + ")",
                            success ? "#3ECFA0" : "#F75858");
                ShowDeployResultDialog(deploymentType, success, exitCode, errorMsg, logDir);
            });
        }

        private bool ShowDeployConfirmDialog(string deploymentType, string scriptPath)
        {
            bool result = false;
            string icon    = deploymentType == "Install"   ? "▶"  :
                             deploymentType == "Uninstall" ? "◀"  : "🔧";
            string btnStyle = deploymentType == "Install"   ? "green" :
                              deploymentType == "Uninstall" ? "amber" : "blue";
            string hdrStyle = btnStyle;

            var win  = MakeDialogWindow(520, 220);
            win.Title = deploymentType;
            var root = MakeDialogRoot(win);
            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var hdr = MakeDialogHeaderThemed(icon, deploymentType + " Package", hdrStyle, () => win.Close());
            Grid.SetRow(hdr, 0);

            // Body
            var body = new StackPanel { Margin = new Thickness(22, 14, 22, 10), VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(new TextBlock
            {
                Text = "Script:", FontSize = 10, Foreground = TC("TextSecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 2)
            });
            body.Children.Add(new TextBlock
            {
                Text = scriptPath, FontSize = 11, FontFamily = new FontFamily("Consolas"),
                Foreground = TC("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            body.Children.Add(new TextBlock
            {
                Text = "Click Run to start " + deploymentType.ToLower() + ". You will be prompted for administrator access.",
                FontSize = 11, Foreground = TC("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });

            // Footer
            var ftr = new Border
            {
                BorderBrush = TC("DlgBorderBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 12, 20, 12),
                Background = TC("DlgFooterBgBrush")
            };
            var fs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var bCancel = DlgBtnStyled("✕ Cancel", "neutral", 90);
            bCancel.Click += (_, _) => { result = false; win.Close(); };

            var bRun = DlgBtnStyled(icon + " Run " + deploymentType, btnStyle, 140);
            bRun.Click += (_, _) => { result = true; win.Close(); };

            fs.Children.Add(bCancel);
            fs.Children.Add(bRun);
            ftr.Child = fs;

            Grid.SetRow(hdr, 0);
            Grid.SetRow(body, 1);
            Grid.SetRow(ftr, 2);
            outer.Children.Add(hdr);
            outer.Children.Add(body);
            outer.Children.Add(ftr);
            root.Child = outer;
            win.Content = root;
            win.ShowDialog();
            return result;
        }

        private static string ReadLogPathFromConfig(string scriptDir)
        {
            string defaultLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "Software");
            var candidates = new[]
            {
                Path.Combine(scriptDir, "AppDeployToolkit", "AppDeployToolkitConfig.xml"),
                Path.Combine(scriptDir, "config.psd1"),
                Path.Combine(Path.GetDirectoryName(scriptDir) ?? scriptDir, "config.psd1"),
            };
            foreach (var cfg in candidates)
            {
                if (!File.Exists(cfg)) continue;
                try
                {
                    string txt = File.ReadAllText(cfg);
                    var m = Regex.Match(txt, @"LogPath\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string raw = m.Groups[1].Value.Trim();
                        raw = Regex.Replace(raw, @"\$env:windir|\$envWinDir|\$env:SystemRoot",
                            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            RegexOptions.IgnoreCase);
                        return raw;
                    }
                }
                catch { }
            }
            return defaultLog;
        }

        private void ShowDeployResultDialog(string deployType, bool success, int exitCode, string error, string logDir)
        {
            string headerIcon  = success ? "✅" : "❌";
            string headerTitle = success ? deployType + " Completed Successfully" : deployType + " Failed";
            string hdrStyle    = success ? (deployType == "Install" ? "green" : deployType == "Uninstall" ? "amber" : "blue") : "red";

            var win  = MakeDialogWindow(500, 260);
            win.Title = headerTitle;
            var root  = MakeDialogRoot(win);
            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var hdr = MakeDialogHeaderThemed(headerIcon, headerTitle, hdrStyle, () => win.Close());
            Grid.SetRow(hdr, 0);

            // Body
            var body = new StackPanel { Margin = new Thickness(22, 14, 22, 6) };

            // Exit code row
            string fgKey = hdrStyle switch { "green"=>"DlgHdrGreenFg","amber"=>"DlgHdrAmberFg","blue"=>"DlgHdrBlueFg","red"=>"DlgHdrRedFg",_=>"DlgHdrNeutralFg" };
            Color fgColor = Application.Current.TryFindResource(fgKey) is Color c ? c : Color.FromRgb(0xEA,0xED,0xF5);

            var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,6) };
            codeRow.Children.Add(new TextBlock
            {
                Text = "Exit Code:", FontSize = 11, Width = 80,
                Foreground = TC("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            codeRow.Children.Add(new TextBlock
            {
                Text = exitCode.ToString(),
                FontSize = 12, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fgColor),
                VerticalAlignment = VerticalAlignment.Center
            });
            body.Children.Add(codeRow);

            // Reboot note
            if (exitCode == 3010)
            {
                var rebootRow = new Border
                {
                    Background = TC("RebootWarnBgBrush"),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 0, 0)
                };
                rebootRow.Child = new TextBlock
                {
                    Text = "⚠  Reboot required to complete the installation.",
                    FontSize = 11, Foreground = TC("RebootWarnFgBrush"),
                    TextWrapping = TextWrapping.Wrap
                };
                body.Children.Add(rebootRow);
            }

            // Error message
            if (!success && !string.IsNullOrEmpty(error))
            {
                body.Children.Add(new TextBlock
                {
                    Text = error, FontSize = 10, FontFamily = new FontFamily("Consolas"),
                    Foreground = TC("AccentRedBrush"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                    MaxHeight = 60
                });
            }

            // Log dir info
            string? logFile = null;
            try
            {
                if (Directory.Exists(logDir))
                {
                    string suffix = "_PSAppDeployToolkit_" + deployType + ".log";
                    var all = Directory.GetFiles(logDir, "*.log");
                    logFile = all
                        .Where(f => Path.GetFileName(f).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault()
                        ?? all.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                }
            }
            catch { }

            body.Children.Add(new TextBlock
            {
                Text = logFile != null ? "Log: " + Path.GetFileName(logFile) : "Log folder: " + logDir,
                FontSize = 10, Foreground = TC("DlgInfoTextBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });

            // Footer
            var ftr = new Border
            {
                BorderBrush = TC("DlgBorderBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 12, 20, 12),
                Background = TC("DlgFooterBgBrush")
            };
            var fs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var bLog = DlgBtnStyled("📄 Open Log", "blue", 130);
            bLog.Click += (_, _) =>
            {
                try
                {
                    if (logFile != null && File.Exists(logFile))
                        Process.Start(new ProcessStartInfo(logFile) { UseShellExecute = true });
                    else if (Directory.Exists(logDir))
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + logDir + "\""));
                }
                catch { }
            };

            var bClose = DlgBtnStyled("✕ Close", "green", 90);
            bClose.Click += (_, _) => win.Close();

            fs.Children.Add(bLog); fs.Children.Add(bClose);
            ftr.Child = fs;

            Grid.SetRow(hdr, 0); Grid.SetRow(body, 1); Grid.SetRow(ftr, 2);
            outer.Children.Add(hdr); outer.Children.Add(body); outer.Children.Add(ftr);
            root.Child = outer; win.Content = root;
            win.ShowDialog();
        }

        private void ScriptSave()
        {
            if (_createdScriptPath == null) return;
            try
            {
                // Always patch app variables from Info tab - user may have changed
                // Info fields without touching the script text directly
                string patched = PatchAppVariables(TxtScript.Text);
                if (patched != TxtScript.Text)
                    TxtScript.Text = patched;

                // Always write the current editor text to disk. The editor is the source of truth
                // and may differ from the file (e.g. live Info auto-population updated the editor
                // without flagging it dirty). Comparing against the file avoids a needless write
                // while ensuring app-variable changes are never silently dropped.
                string onDisk = "";
                try { onDisk = File.ReadAllText(_createdScriptPath, new UTF8Encoding(false)); } catch { }
                if (!_scriptDirty && string.Equals(onDisk, TxtScript.Text, StringComparison.Ordinal))
                { FlashStatus("✓ No changes", "#8B92A9"); return; }

                File.WriteAllText(_createdScriptPath, TxtScript.Text, new UTF8Encoding(true));
                _scriptDirty = false;
                TxtScriptDirty.Visibility = Visibility.Collapsed;
                FlashStatus("💾 Saved", "#3ECFA0");
            }
            catch (Exception ex) { PkgStyledMessage(Window.GetWindow(this), "Error", "Save failed:\n" + ex.Message, "⛔"); }
        }

        /// <summary>
        /// Replaces $appVendor / $appName / $appVersion / $appArch / $appLang / $appRevision
        /// / $appScriptVersion assignments in the script with values from the Info tab.
        /// Only replaces lines that already have the assignment pattern; leaves everything else untouched.
        /// </summary>
        private string PatchAppVariables(string script)
        {
            // Simple string fields - patched with single-quote replacement
            var stringMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AppVendor"]        = TxtAppVendor.Text.Trim(),
                ["AppName"]          = TxtAppName.Text.Trim(),
                ["AppVersion"]       = TxtAppVersion.Text.Trim(),
                ["AppArch"]          = TxtAppArch.Text.Trim(),
                ["AppLang"]          = TxtAppLang.Text.Trim(),
                ["AppRevision"]      = TxtAppRevision.Text.Trim(),
                ["AppScriptVersion"] = TxtScriptVersion.Text.Trim(),
                ["AppScriptDate"]    = TxtScriptDate.Text.Trim(),
                ["AppScriptAuthor"]  = TxtScriptAuthor.Text.Trim(),
                ["InstallName"]      = TxtInstallName.Text.Trim(),
                ["InstallTitle"]     = TxtInstallTitle.Text.Trim(),
            };

            // Pattern 1: hashtable style  →  AppRevision = '13'
            script = Regex.Replace(script,
                @"^(?<indent>\s+)(?<var>App(?:Vendor|Name|Version|Arch|Lang|Revision|ScriptVersion|ScriptDate|ScriptAuthor)|InstallName|InstallTitle)\s*=\s*'(?<val>[^']*)'",
                m =>
                {
                    string varName = m.Groups["var"].Value;
                    if (stringMap.TryGetValue(varName, out string? newVal) && newVal != null)
                        return m.Groups["indent"].Value + varName + " = '" + newVal + "'";
                    return m.Value;
                },
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            // Pattern 2: standalone variable style  →  $appRevision = '13'
            script = Regex.Replace(script,
                @"^(?<indent>\s*)\$(?<var>app(?:Vendor|Name|Version|Arch|Lang|Revision|ScriptVersion|ScriptDate|ScriptAuthor)|InstallName|InstallTitle)\s*=\s*'(?<val>[^']*)'",
                m =>
                {
                    string varName = m.Groups["var"].Value;
                    if (stringMap.TryGetValue(varName, out string? newVal) && newVal != null)
                        return m.Groups["indent"].Value + "$" + varName + " = '" + newVal + "'";
                    return m.Value;
                },
                RegexOptions.Multiline | RegexOptions.IgnoreCase);

            // RequireAdmin - value is $true/$false, not quoted
            string reqAdmin = TxtRequireAdmin.Text.Trim();
            if (!string.IsNullOrWhiteSpace(reqAdmin))
            {
                script = Regex.Replace(script,
                    @"^(?<indent>\s+)(?<var>RequireAdmin)\s*=\s*(?<val>\$(?:true|false|\w+))",
                    m => m.Groups["indent"].Value + "RequireAdmin" + " = " + reqAdmin,
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }

            // AppSuccessExitCodes - @(0) or @(0, 1641)
            string sec = TxtSuccessExitCodes.Text.Trim();
            if (!string.IsNullOrWhiteSpace(sec))
            {
                string secVal = "@(" + sec + ")";
                script = Regex.Replace(script,
                    @"^(?<indent>\s+)AppSuccessExitCodes\s*=\s*@\([^)]*\)",
                    m => m.Groups["indent"].Value + "AppSuccessExitCodes = " + secVal,
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }

            // AppRebootExitCodes - @(1641, 3010)
            string rec = TxtRebootExitCodes.Text.Trim();
            if (!string.IsNullOrWhiteSpace(rec))
            {
                string recVal = "@(" + rec + ")";
                script = Regex.Replace(script,
                    @"^(?<indent>\s+)AppRebootExitCodes\s*=\s*@\([^)]*\)",
                    m => m.Groups["indent"].Value + "AppRebootExitCodes = " + recVal,
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }

            // AppProcessesToClose - @('excel', 'winword')
            string procs = TxtAppProcesses.Text.Trim();
            if (!string.IsNullOrWhiteSpace(procs))
            {
                var items = procs.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
                string procsVal = "@('" + string.Join("', '", items) + "')";
                script = Regex.Replace(script,
                    @"^(?<indent>\s+)AppProcessesToClose\s*=\s*@\([^)]*\)",
                    m => m.Groups["indent"].Value + "AppProcessesToClose = " + procsVal,
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }

            return script;
        }

        private void ScriptOpenInEditor()
        {
            if (_createdScriptPath == null || !File.Exists(_createdScriptPath)) return;
            try
            {
                // Try PowerShell ISE first (shows the script in a proper editor window)
                string isePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "PowerShell_ISE.exe");

                // Also check sysnative / syswow paths
                if (!File.Exists(isePath))
                {
                    string sys = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                    isePath = Path.Combine(sys, "System32", "WindowsPowerShell", "v1.0", "PowerShell_ISE.exe");
                }
                if (!File.Exists(isePath))
                {
                    string sys = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                    isePath = Path.Combine(sys, "SysWOW64", "WindowsPowerShell", "v1.0", "PowerShell_ISE.exe");
                }

                if (File.Exists(isePath))
                {
                    // Open script in PowerShell ISE
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = isePath,
                        Arguments       = "\"" + _createdScriptPath + "\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    // Fallback: open in a plain PowerShell console window (visible, stays open)
                    string args = "-NoExit -ExecutionPolicy Bypass -NoProfile -File \"" +
                                  _createdScriptPath + "\"";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = "powershell.exe",
                        Arguments       = args,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex) { PkgStyledMessage(Window.GetWindow(this), "Error opening script", ex.Message, "⛔"); }
        }

        // ── Line manipulation helpers ──
        /// <summary>Returns the 0-based line index of the current caret position.</summary>
        private int GetCaretLine()
        {
            if (TxtScript == null || string.IsNullOrEmpty(TxtScript.Text)) return 0;
            int idx = Math.Max(0, Math.Min(TxtScript.CaretIndex, TxtScript.Text.Length - 1));
            return TxtScript.GetLineIndexFromCharacterIndex(idx);
        }
        private (int startLine, int endLine, string[] lines) GetSelectionLines()
        {
            // Always read from our tracked fields - never from WPF SelectionStart/Length
            // which are unreliable (cleared on focus loss, corrupted by IsReadOnly changes).
            string text = TxtScript.Text;
            var lines = text.Split('\n');
            int total = lines.Length;
            int s = _savedSelStartLine >= 0 ? Math.Min(_savedSelStartLine, total - 1) : GetCaretLine();
            int e2 = _savedSelEndLine   >= 0 ? Math.Min(_savedSelEndLine,   total - 1) : s;
            if (s > e2) (s, e2) = (e2, s);
            return (s, e2, lines);
        }

        /// <summary>
        /// Moves the caret to startLine and records the selection range in fields.
        /// Does NOT touch SelectionStart/SelectionLength - those are unreliable.
        /// Visual highlight is drawn by HighlightCurrentLine() from the fields.
        /// </summary>
        private void SelectScriptLines(int startLine, int endLine)
        {
            if (TxtScript == null || string.IsNullOrEmpty(TxtScript.Text)) return;
            var lines = TxtScript.Text.Split('\n');
            int total = lines.Length;
            startLine = Math.Max(0, Math.Min(startLine, total - 1));
            endLine   = Math.Max(0, Math.Min(endLine,   total - 1));
            if (startLine > endLine) (startLine, endLine) = (endLine, startLine);

            // Place caret at the first character of startLine
            int caretPos = 0;
            for (int i = 0; i < startLine; i++) caretPos += lines[i].Length + 1;

            _flushingToScript = true;
            try { TxtScript.CaretIndex = Math.Min(caretPos, TxtScript.Text.Length); }
            finally { _flushingToScript = false; }

            _savedSelStartLine = startLine;
            _savedSelEndLine   = endLine;

            UpdateCursorPos();
            HighlightCurrentLine();
            TrySelectCommandFromCurrentLine();
        }

        /// <summary>Number of script lines that fit in one viewport (for PageUp/PageDown).</summary>
        private int ScriptPageLines()
        {
            double lineH = 16.0;
            try
            {
                int ci = TxtScript.GetCharacterIndexFromLineIndex(Math.Max(0, GetCaretLine()));
                Rect r = TxtScript.GetRectFromCharacterIndex(ci);
                if (r != Rect.Empty && r.Height > 1) lineH = r.Height;
            }
            catch { }
            double vh = ScriptScrollViewer?.ViewportHeight ?? 0;
            int n = vh > 0 ? (int)(vh / lineH) : 20;
            return Math.Max(1, n - 1);   // keep one line of context across a page jump
        }

        /// <summary>Scrolls the outer ScriptScrollViewer minimally so the given line is visible.</summary>
        private void EnsureScriptLineVisible(int line)
        {
            if (TxtScript == null || ScriptScrollViewer == null) return;
            try
            {
                TxtScript.UpdateLayout();
                int lc = TxtScript.LineCount;
                if (lc <= 0) return;
                line = Math.Max(0, Math.Min(line, lc - 1));
                int ci = TxtScript.GetCharacterIndexFromLineIndex(line);
                Rect r = TxtScript.GetRectFromCharacterIndex(ci);
                if (r == Rect.Empty || double.IsNaN(r.Top)) return;

                // r.Top is relative to the current viewport; add VerticalOffset for the absolute
                // content position, then scroll up/down only if the line falls outside the viewport.
                Point pt    = TxtScript.TranslatePoint(new Point(0, r.Top), ScriptScrollViewer);
                double absTop = ScriptScrollViewer.VerticalOffset + pt.Y;
                double lineH  = r.Height > 1 ? r.Height : 16.0;
                double vo     = ScriptScrollViewer.VerticalOffset;
                double vh     = ScriptScrollViewer.ViewportHeight;

                if (absTop < vo)
                    ScriptScrollViewer.ScrollToVerticalOffset(Math.Max(0, absTop));
                else if (absTop + lineH > vo + vh)
                    ScriptScrollViewer.ScrollToVerticalOffset(Math.Max(0, absTop + lineH - vh));
            }
            catch { }
        }

        private void ReplaceScriptLines(string[] lines, int targetLine = -1)
        {
            CaptureUndoState();

            // Suppress SelectionChanged → TrySelectCommandFromCurrentLine during the entire
            // replace so that setting Text (resets caret to 0) and IsReadOnly do not
            // cause the command panel or line highlight to jump to line 1.
            _flushingToScript = true;
            try
            {
                TxtScript.IsReadOnly = false;
                TxtScript.Text = string.Join("\n", lines);

                // Set caret to target line
                if (targetLine >= 0 && lines.Length > 0)
                {
                    int clampedLine = Math.Min(targetLine, lines.Length - 1);
                    int pos = 0;
                    for (int i = 0; i < clampedLine; i++)
                        pos += lines[i].Length + 1; // +1 for '\n'
                    TxtScript.CaretIndex = Math.Min(pos, TxtScript.Text.Length);
                }
                else
                {
                    TxtScript.CaretIndex = Math.Min(TxtScript.CaretIndex, TxtScript.Text.Length);
                }

                TxtScript.IsReadOnly = true;
            }
            finally
            {
                _flushingToScript = false;
            }

            // Set fields to the actual landing line so highlight and next arrow key are correct
            int landingLine = GetCaretLine();
            _savedSelStartLine = landingLine;
            _savedSelEndLine   = landingLine;
            _arrowAnchorLine   = -1;
            _dragLine          = -1;
            MarkScriptDirty(); UpdateLineNumbers();
            TrySelectCommandFromCurrentLine();
            HighlightCurrentLine();
        }

        /// <summary>Sets clipboard text with retry on OpenClipboard failure (race condition with other apps).</summary>
        private static void ClipboardSetTextSafe(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return; }
                catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(20); }
            }
        }

        /// <summary>Gets clipboard text with retry on OpenClipboard failure.</summary>
        private static string ClipboardGetTextSafe()
        {
            for (int i = 0; i < 5; i++)
            {
                try { return Clipboard.GetText(); }
                catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(20); }
            }
            return string.Empty;
        }

        private void ScriptDeleteLines()
        {
            var (s, e2, l) = GetSelectionLines();
            ReplaceScriptLines(l.Where((_, i) => i < s || i > e2).ToArray(), targetLine: s);
        }

        private void ScriptCutLines()
        {
            var (s, e2, l) = GetSelectionLines();
            ClipboardSetTextSafe(string.Join("\n", l.Skip(s).Take(e2 - s + 1)));
            ReplaceScriptLines(l.Where((_, i) => i < s || i > e2).ToArray(), targetLine: s);
        }

        private void ScriptCopyLines()
        {
            var (s, e2, l) = GetSelectionLines();
            ClipboardSetTextSafe(string.Join("\n", l.Skip(s).Take(e2 - s + 1)));
            // Keep single-line selection on the start line after copy
            _savedSelStartLine = s; _savedSelEndLine = s;
            _arrowAnchorLine = -1; _dragLine = -1;
            HighlightCurrentLine();
        }

        private void ScriptPaste()
        {
            // If clipboard holds files, route through ProcessDroppedFiles
            var clipFiles = GetAllFilesFromClipboard();
            if (clipFiles.Count > 0)
            {
                int dropLine = 0;
                if (TxtScript != null && !string.IsNullOrEmpty(TxtScript.Text))
                {
                    int ci = TxtScript.CaretIndex;
                    dropLine = TxtScript.GetLineIndexFromCharacterIndex(ci);
                    dropLine = Math.Max(0, Math.Min(dropLine, TxtScript.LineCount - 1));
                }
                ProcessDroppedFiles(clipFiles.ToArray(), dropLine);
                return;
            }

            // Default paste: insert clipboard text at caret
            string cl = ClipboardGetTextSafe();
            if (!string.IsNullOrEmpty(cl)) ScriptInsertAtCursor(cl);
        }

        /// <summary>
        /// Returns ALL file paths from the clipboard (supports multi-select, all extensions).
        /// Checks: CF_HDROP (FileDrop), "FileNameW"/"FileName" shell formats, plain text path.
        /// </summary>
        private static List<string> GetAllFilesFromClipboard()
        {
            var result = new List<string>();
            try
            {
                // Format 1: CF_HDROP / FileDrop - standard multi-file copy (includes folders)
                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string? f in Clipboard.GetFileDropList())
                        if (f != null
                            && (System.IO.File.Exists(f) || System.IO.Directory.Exists(f))
                            && !result.Contains(f, StringComparer.OrdinalIgnoreCase))
                            result.Add(f);
                    if (result.Count > 0) return result;
                }

                // Format 2: "FileNameW" / "FileName" shell extensions
                foreach (string fmt in new[] { "FileNameW", "FileName" })
                {
                    if (!Clipboard.ContainsData(fmt)) continue;
                    object? raw = Clipboard.GetData(fmt);
                    string? path = raw is byte[] bytes
                        ? (fmt == "FileNameW"
                            ? System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0')
                            : System.Text.Encoding.Default.GetString(bytes).TrimEnd('\0'))
                        : raw as string;
                    if (path != null && System.IO.File.Exists(path)
                        && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                        result.Add(path);
                    if (result.Count > 0) return result;
                }

                // Format 3: plain text that is a valid file or folder path
                if (Clipboard.ContainsText())
                {
                    string txt = Clipboard.GetText().Trim();
                    if ((System.IO.File.Exists(txt) || System.IO.Directory.Exists(txt))
                        && !result.Contains(txt, StringComparer.OrdinalIgnoreCase))
                        result.Add(txt);
                }
            }
            catch { /* clipboard access can fail transiently */ }
            return result;
        }

        // Compat shims for hasClip checks
        private static List<string> GetAllLnksFromClipboard()
            => GetAllFilesFromClipboard().Where(f => f.EndsWith(".lnk",
                   StringComparison.OrdinalIgnoreCase)).ToList();

        private static string? TryGetLnkFromClipboard()
            => GetAllFilesFromClipboard().FirstOrDefault(f => f.EndsWith(".lnk",
                   StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Same path classification as BuildShortcutLines but for generic files (non-.lnk).
        /// Uses "# Remove file" / "# Remove User Profile file" labels instead of "shortcut".
        /// All five cases mirror BuildShortcutLines exactly.
        /// </summary>
        private static (string comment, string command) BuildFileRemoveLines(string folder, string name)
        {
            string norm = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            bool PathStartsWith(string path, string prefix) =>
                path.StartsWith(prefix.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

            string publicDesktop   = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory).TrimEnd('\\', '/');
            string publicRoot      = Path.GetDirectoryName(publicDesktop) ?? @"C:\Users\Public";
            string programData     = (Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData").TrimEnd('\\', '/');
            string commonPrograms  = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms).TrimEnd('\\', '/');
            string commonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu).TrimEnd('\\', '/');
            string usersRoot       = Path.GetDirectoryName(
                                         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                                             .TrimEnd('\\', '/')) ?? @"C:\Users";
            string sysDrive        = (Path.GetPathRoot(
                                         Environment.GetFolderPath(Environment.SpecialFolder.System))
                                         ?? "C:\\").TrimEnd('\\', '/');

            // Case 1a: Exactly the Public Desktop folder
            if (norm.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase))
                return (
                    $"    # Remove Desktop file - {name}",
                    $"    Remove-ADTFile -Path \"$envPublic\\Desktop\\{name}\""
                );

            // Case 1b: Any path under C:\Users\Public\* -> $envPublic\<rel>\<file>
            if (PathStartsWith(norm, publicRoot + "\\") ||
                norm.Equals(publicRoot, StringComparison.OrdinalIgnoreCase))
            {
                string relFromPublic = norm.Length > publicRoot.Length
                    ? norm.Substring(publicRoot.Length).TrimStart('\\', '/')
                    : "";
                string psPath = string.IsNullOrEmpty(relFromPublic)
                    ? $"$envPublic\\{name}"
                    : $"$envPublic\\{relFromPublic}\\{name}";
                return (
                    $"    # Remove file - {name}",
                    $"    Remove-ADTFileFromUserProfiles -Path \"{psPath}\""
                );
            }

            // Case 2: All-users system path (ProgramData / Common Start Menu)
            if (PathStartsWith(norm, programData)    ||
                PathStartsWith(norm, commonPrograms) ||
                PathStartsWith(norm, commonStartMenu))
            {
                string relFromDrive = norm.Length > sysDrive.Length ? norm.Substring(sysDrive.Length) : "\\" + norm;
                string fullRel      = relFromDrive.TrimEnd('\\') + "\\" + name;
                string label        = (PathStartsWith(norm, commonPrograms) || PathStartsWith(norm, commonStartMenu))
                    ? "Start Menu file" : "file";
                return (
                    $"    # Remove {label} - {name}",
                    $"    Remove-ADTFile -Path \"$envSystemDrive{fullRel}\""
                );
            }

            // Cases 3 & 4: Per-user profile (non-Public)
            if (PathStartsWith(norm, usersRoot))
            {
                string afterUsers      = norm.Substring(usersRoot.Length).TrimStart('\\', '/');
                int    sep             = afterUsers.IndexOf('\\');
                string relUnderProfile = sep >= 0 ? afterUsers.Substring(sep + 1) : "";
                string psRelPath       = string.IsNullOrEmpty(relUnderProfile)
                    ? name
                    : relUnderProfile.TrimEnd('\\') + "\\" + name;
                return (
                    $"    # Remove User Profile file - {name}",
                    $"    Remove-ADTFileFromUserProfiles -Path \"{psRelPath}\""
                );
            }

            // Case 5: Unknown - replace drive letter with $envSystemDrive
            string fullPath = Path.Combine(folder, name);
            string root     = Path.GetPathRoot(fullPath) ?? "";
            string portable = root.Length > 0
                ? "$envSystemDrive" + fullPath.Substring(root.Length - 1)
                : fullPath;
            return (
                $"    # Remove file - {name}",
                $"    Remove-ADTFile -Path \"{portable}\""
            );
        }


        /// <summary>
        /// Builds Remove comment + command for a dropped/pasted folder.
        ///
        /// Cases (mirror BuildFileRemoveLines path classification):
        ///   Public Desktop subdir  → Remove-ADTFileFromUserProfiles -Path "$envPublic\Desktop\<rel>" -Recurse
        ///   All-users system path  → Remove-ADTFolder -Path "$envSystemDrive\<rel>"
        ///   Per-user profile       → Remove-ADTFileFromUserProfiles -Path "<rel>" -Recurse
        ///   Unknown                → Remove-ADTFolder -Path "$envSystemDrive\<rel>"
        /// </summary>
        private static (string comment, string command) BuildFolderRemoveLines(string folderPath)
        {
            string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar,
                                                  Path.AltDirectorySeparatorChar));
            string norm = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            bool PathStartsWith(string path, string prefix) =>
                path.StartsWith(prefix.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

            string publicDesktop   = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                                         .TrimEnd('\\', '/');
            string publicRoot      = Path.GetDirectoryName(publicDesktop) ?? // C:\Users\Public
                                     @"C:\Users\Public";
            string programData     = (Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData")
                                         .TrimEnd('\\', '/');
            string commonPrograms  = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                                         .TrimEnd('\\', '/');
            string commonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                                         .TrimEnd('\\', '/');
            string usersRoot       = Path.GetDirectoryName(
                                         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                                             .TrimEnd('\\', '/')) ?? @"C:\Users";
            string sysDrive        = (Path.GetPathRoot(
                                         Environment.GetFolderPath(Environment.SpecialFolder.System))
                                         ?? "C:\\").TrimEnd('\\', '/');

            string comment = $"    # Remove folder - {folderName}";

            // ── Case: Public Desktop or sub-path ──────────────────────────────
            if (norm.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase) ||
                PathStartsWith(norm, publicDesktop + "\\"))
            {
                // rel from publicDesktop parent (C:\Users\Public)
                string relFromPublic = norm.Length > publicRoot.Length
                    ? norm.Substring(publicRoot.Length).TrimStart('\\', '/')
                    : folderName;
                return (comment,
                    $"    Remove-ADTFileFromUserProfiles -Path \"$envPublic\\{relFromPublic}\" -Recurse");
            }

            // ── Case: All-users system (ProgramData / Common folders) ─────────
            if (PathStartsWith(norm, programData)    ||
                PathStartsWith(norm, commonPrograms) ||
                PathStartsWith(norm, commonStartMenu))
            {
                string relFromDrive = norm.Length > sysDrive.Length
                    ? norm.Substring(sysDrive.Length)
                    : "\\" + norm;
                return (comment,
                    $"    Remove-ADTFolder -Path \"$envSystemDrive{relFromDrive}\"");
            }

            // ── Cases: Per-user profile (C:\Users\<user>\...) ───────────────
            if (PathStartsWith(norm, usersRoot + "\\"))
            {
                string afterUsers = norm.Substring(usersRoot.Length).TrimStart('\\', '/');
                int sep = afterUsers.IndexOf('\\');
                string relUnderProfile = sep >= 0 ? afterUsers.Substring(sep + 1) : "";

                // If directly in profile root (no sub-path), use just the folder name
                string psRel = string.IsNullOrEmpty(relUnderProfile) ? folderName : relUnderProfile;
                return (comment,
                    $"    Remove-ADTFileFromUserProfiles -Path \"{psRel}\" -Recurse");
            }

            // ── Case: Unknown - replace drive with $envSystemDrive ────────────
            string pathRoot = Path.GetPathRoot(norm) ?? "";
            string portable = pathRoot.Length > 0
                ? "$envSystemDrive" + norm.Substring(pathRoot.Length - 1)
                : norm;
            return (comment,
                $"    Remove-ADTFolder -Path \"{portable}\"");
        }

        private void ScriptMoveLines(int dir)
        {
            var (s, e2, lines) = GetSelectionLines();
            if (dir == -1 && s == 0) return;
            if (dir ==  1 && e2 == lines.Length - 1) return;
            var l = lines.ToList();
            int t = dir == -1 ? s - 1 : e2 + 1;
            string mv = l[t]; l.RemoveAt(t); l.Insert(dir == -1 ? e2 : s, mv);
            int newStart = dir == -1 ? s - 1 : s + 1;
            int newEnd   = newStart + (e2 - s);      // selection range moves by 1
            ReplaceScriptLines(l.ToArray(), targetLine: newStart);
            // Restore the full selection on the moved lines
            int total = TxtScript.Text.Split('\n').Length;
            _savedSelStartLine = Math.Min(newStart, total - 1);
            _savedSelEndLine   = Math.Min(newEnd,   total - 1);
            _arrowAnchorLine   = -1;
            _dragLine          = -1;
            HighlightCurrentLine();
        }
        private void ScriptToggleComment(bool add)
        {
            var(s,e2,lines)=GetSelectionLines();
            for(int i=s;i<=e2;i++)
            {
                // Skip empty or whitespace-only lines - never comment/uncomment them
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                if (add)
                {
                    var m = Regex.Match(lines[i], @"^(\s*)(.*)$");
                    string indent  = m.Groups[1].Value;
                    string rest    = m.Groups[2].Value;
                    bool alreadyCommented = rest.StartsWith("#");
                    lines[i] = indent + (alreadyCommented ? "#" + rest : "# " + rest);
                }
                else
                {
                    lines[i] = Regex.Replace(lines[i], @"^(\s*)#+\s?", "$1");
                }
            }
            ReplaceScriptLines(lines, targetLine: s);
            // ReplaceScriptLines collapses selection to targetLine - restore the full range
            int total = TxtScript.Text.Split('\n').Length;
            _savedSelStartLine = Math.Min(s,  total - 1);
            _savedSelEndLine   = Math.Min(e2, total - 1);
            _arrowAnchorLine   = -1;
            _dragLine          = -1;
            HighlightCurrentLine();
        }
        private void ScriptInsertAtCursor(string text)
        {
            CaptureUndoState();
            // Insert AFTER the current selected line (after its trailing \n)
            var (s, _, existingLines) = GetSelectionLines();
            int insertAt = 0;
            for (int i = 0; i <= s && i < existingLines.Length; i++)
            {
                insertAt += existingLines[i].Length;
                if (i < existingLines.Length - 1) insertAt += 1; // +1 for \n
            }
            // Ensure the pasted text ends with \n so it becomes its own line(s)
            string insertText = text.EndsWith("\n") ? text : text + "\n";

            _flushingToScript = true;
            try
            {
                TxtScript.IsReadOnly = false;
                TxtScript.Text = TxtScript.Text.Insert(insertAt, insertText);
                // Place caret at start of first pasted line (s+1)
                int caretLine   = s + 1;
                var newLines    = TxtScript.Text.Split('\n');
                int caretPos    = 0;
                for (int i = 0; i < caretLine && i < newLines.Length; i++)
                    caretPos += newLines[i].Length + 1;
                TxtScript.CaretIndex = Math.Min(caretPos, TxtScript.Text.Length);
                TxtScript.IsReadOnly = true;
            }
            finally
            {
                _flushingToScript = false;
            }
            int landingLine2 = s + 1; // paste goes after line s, so land on s+1
            int totalAfter   = TxtScript.Text.Split('\n').Length;
            _savedSelStartLine = Math.Min(landingLine2, totalAfter - 1);
            _savedSelEndLine   = _savedSelStartLine;
            _arrowAnchorLine   = -1;
            _dragLine          = -1;
            MarkScriptDirty(); UpdateLineNumbers();
            TrySelectCommandFromCurrentLine();
            HighlightCurrentLine();
        }

        // ══════════════════════════════════════════════════════════════
        // LINE NUMBERS / CURSOR / DIRTY
        // ══════════════════════════════════════════════════════════════
        private void TxtScript_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _arrowAnchorLine = -1;
            _isDragging      = false;
            _dragPending     = false;

            if (TxtScript != null && !string.IsNullOrEmpty(TxtScript.Text))
            {
                var clickPt = e.GetPosition(TxtScript);
                TxtScript.UpdateLayout();
                // LineIndexFromPoint, not the raw snapToText call: the native call mis-snaps on
                // empty lines and sent the caret to the end of the script (the "click a blank
                // line and the cursor jumps to the bottom" bug).
                {
                    int clickedLine = LineIndexFromPoint(clickPt);

                    // Capture the drag source for a potential line-move drag. Only the existing
                    // multi-line selection is captured when the click lands INSIDE it; otherwise the
                    // drag source is just the clicked line - so a tiny mouse jiggle on a fresh click
                    // can never resurrect a previous multi-line block (the intermittent multi-select bug).
                    BeginDragPending(clickPt, clickedLine);

                    // Always collapse the highlight to the single clicked line. (Previously, clicking
                    // INSIDE an existing multi-line selection preserved it, which left many lines
                    // highlighted after block operations like Comment/MoveDown. A plain click should
                    // always place the caret on one line; if the user drags, the move uses the
                    // _dragSource* range captured above.)
                    _dragLine          = -1;
                    _savedSelStartLine = clickedLine;
                    _savedSelEndLine   = clickedLine;
                    int lineStart = TxtScript.GetCharacterIndexFromLineIndex(clickedLine);
                    _flushingToScript = true;
                    try { TxtScript.CaretIndex = lineStart; }
                    finally { _flushingToScript = false; }
                    // Update the highlight now so the old multi-line block disappears immediately.
                    HighlightCurrentLine();
                }
            }

            // Highlight is applied authoritatively on mouse-up (see TxtScript_PreviewMouseLeftButtonUp),
            // not here - a scheduled highlight on mouse-down can race with the up-handler and leave a
            // stale multi-line selection (clicking line N then N-1 showed BOTH lines highlighted).
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (TxtScript == null) return;
                UpdateCursorPos();
            }));
        }

        // ── Right-click context menu on the script editor ───────────────
        /// <summary>
        /// Allows file drags to pass through the ScrollViewer to TxtScript.
        /// Without AllowDrop + this handler on the ScrollViewer, WPF resets
        /// DragDropEffects to None as the tunnel event passes through, showing
        /// the "no-drop" cursor before it ever reaches TxtScript's PreviewDragOver.
        /// </summary>
        /// <summary>
        /// Page-level PreviewDragOver - fires before any child element can block the drag.
        /// If the mouse is over TxtScript (or its scroll viewer), show Copy cursor.
        /// This ensures the "no-drop" cursor never appears while dragging over the editor.
        /// </summary>
        private void Page_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            // Only accept drags that are physically over the script editor area
            if (TxtScript == null || ScriptScrollViewer == null) return;
            var pos = e.GetPosition(ScriptScrollViewer);
            if (pos.X >= 0 && pos.Y >= 0
                && pos.X <= ScriptScrollViewer.ActualWidth
                && pos.Y <= ScriptScrollViewer.ActualHeight)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void ScriptScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void ScriptScrollViewer_RightClick(object sender, MouseButtonEventArgs e)
        {
            // If multiple lines are already selected, preserve the selection -
            // don't move the caret just because the user right-clicked.
            var (selStart, selEnd, _) = GetSelectionLines();
            bool hasMultiLineSelection = selEnd > selStart && TxtScript.SelectionLength > 0;

            if (!hasMultiLineSelection)
            {
                // Single line or no selection: move caret to the clicked LINE. The line comes
                // from LineIndexFromPoint (robust on empty lines); the raw character index from
                // the native call sent right-clicks on blank lines to the end of the script.
                Point ptInTextBox = e.GetPosition(TxtScript);
                int rcLine = LineIndexFromPoint(ptInTextBox);
                TxtScript.CaretIndex = TxtScript.GetCharacterIndexFromLineIndex(rcLine);
            }

            TxtScript.Focus();
            // If no multi-line selection, sync fields to caret line
            if (!(_savedSelStartLine >= 0 && _savedSelEndLine > _savedSelStartLine))
            {
                int line = GetCaretLine();
                _savedSelStartLine = line; _savedSelEndLine = line;
            }
            UpdateCursorPos();
            HighlightCurrentLine();

            var menu = new ContextMenu();
            if (TryFindResource("TxtBoxContextMenuStyle") is Style ctxStyle)
                menu.Style = ctxStyle;

            Style? itemStyle = TryFindResource("TxtBoxMenuItemStyle") as Style;

            MenuItem MakeItem(string header, string icon, string gesture, Action action, bool enabled = true)
            {
                var mi = new MenuItem { Header = $"  {icon}  {header}", InputGestureText = gesture, IsEnabled = enabled };
                if (itemStyle != null) mi.Style = itemStyle;
                if (enabled) mi.Click += (_, _) => action();
                return mi;
            }

            Separator MakeSep() => new Separator { Margin = new Thickness(0, 2, 0, 2) };

            bool hasScript    = !string.IsNullOrEmpty(TxtScript?.Text);
            bool hasClipboard = !string.IsNullOrEmpty(ClipboardGetTextSafe())
                             || GetAllFilesFromClipboard().Count > 0;

            menu.Items.Add(MakeItem("Delete",      "🗑",  "Del",      ScriptDeleteLines,              hasScript));
            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("Cut",         "✂",  "Ctrl+X",   ScriptCutLines,                 hasScript));
            menu.Items.Add(MakeItem("Copy",        "📋", "Ctrl+C",   ScriptCopyLines,                hasScript));
            menu.Items.Add(MakeItem("Paste",       "📄", "Ctrl+V",   ScriptPaste,                    hasClipboard));
            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("Move Up",     "⬆",  "Alt+↑",    () => ScriptMoveLines(-1),      hasScript));
            menu.Items.Add(MakeItem("Move Down",   "⬇",  "Alt+↓",    () => ScriptMoveLines(1),       hasScript));
            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("Comment",     "#",  "Ctrl+K",   () => ScriptToggleComment(true),  hasScript));
            menu.Items.Add(MakeItem("Uncomment",   "○",  "Ctrl+U",   () => ScriptToggleComment(false), hasScript));
            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("Insert Line", "＋", "Ctrl+I",   () => BtnScriptInsert_Click(sender, e), hasScript));

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen          = true;
            e.Handled            = true;
        }

        /// <summary>
        /// Keyboard shortcut handler for the read-only TxtScript editor.
        /// Since TxtScript is IsReadOnly=True, standard WPF shortcuts don't fire -
        /// we intercept them here and call the same underlying methods the toolbar buttons use.
        /// </summary>
        private void TxtScript_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var ctrl  = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
            var alt   = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt);
            var shift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            bool hasScript = !string.IsNullOrEmpty(TxtScript?.Text);
            bool hasClip   = !string.IsNullOrEmpty(ClipboardGetTextSafe())
                          || GetAllFilesFromClipboard().Count > 0;

            // Alt+Arrow keys are reported as Key.System in WPF - handle before the switch
            if (alt && hasScript)
            {
                var sysKey = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                if (sysKey == System.Windows.Input.Key.Up)
                    { ScriptMoveLines(-1); e.Handled = true; return; }
                if (sysKey == System.Windows.Input.Key.Down)
                    { ScriptMoveLines(1);  e.Handled = true; return; }
            }

            // Arrow Up/Down - line navigation and Shift+Arrow multi-line selection
            if (!ctrl && !alt && hasScript &&
                (e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down))
            {
                int totalLines = TxtScript.Text.Split('\n').Length;
                bool goDown    = e.Key == System.Windows.Input.Key.Down;

                if (shift)
                {
                    // First Shift+Arrow: set anchor = current tracked line, drag = anchor
                    if (_arrowAnchorLine < 0)
                    {
                        _arrowAnchorLine = _savedSelStartLine >= 0 ? _savedSelStartLine : GetCaretLine();
                        _dragLine        = _arrowAnchorLine;
                    }
                    _dragLine = goDown
                        ? Math.Min(totalLines - 1, _dragLine + 1)
                        : Math.Max(0, _dragLine - 1);
                    SelectScriptLines(_arrowAnchorLine, _dragLine);
                }
                else
                {
                    // Plain arrow: current = tracked line, move one step
                    int cur = _savedSelStartLine >= 0 ? _savedSelStartLine : GetCaretLine();
                    int target = goDown
                        ? Math.Min(totalLines - 1, cur + 1)
                        : Math.Max(0, cur - 1);
                    _arrowAnchorLine = -1;
                    _dragLine        = -1;
                    SelectScriptLines(target, target);
                }
                e.Handled = true;
                return;
            }

            // Home / End / PageUp / PageDown - caret and line navigation with scrolling.
            // The script TextBox has its own scrollbars disabled (the outer ScriptScrollViewer
            // scrolls), so the native keys cannot move the view; handle them here and scroll
            // the outer viewer to follow the caret.
            if (!alt && hasScript &&
                (e.Key == System.Windows.Input.Key.Home || e.Key == System.Windows.Input.Key.End ||
                 e.Key == System.Windows.Input.Key.PageUp || e.Key == System.Windows.Input.Key.PageDown))
            {
                var lines      = TxtScript.Text.Split('\n');
                int totalLines = lines.Length;
                int curLine    = _savedSelStartLine >= 0 ? _savedSelStartLine : GetCaretLine();
                curLine        = Math.Max(0, Math.Min(curLine, totalLines - 1));

                if (e.Key == System.Windows.Input.Key.Home || e.Key == System.Windows.Input.Key.End)
                {
                    if (ctrl)
                    {
                        // Ctrl+Home / Ctrl+End -> first / last line of the script.
                        int target = e.Key == System.Windows.Input.Key.Home ? 0 : totalLines - 1;
                        _arrowAnchorLine = -1;
                        _dragLine        = -1;
                        SelectScriptLines(target, target);
                        EnsureScriptLineVisible(target);
                    }
                    else
                    {
                        // Home / End -> start / end of the current line (caret column move;
                        // the whole-line highlight stays on the current line).
                        int lineStart = 0;
                        for (int i = 0; i < curLine && i < totalLines; i++) lineStart += lines[i].Length + 1;
                        int caret = e.Key == System.Windows.Input.Key.Home
                            ? lineStart
                            : lineStart + (curLine < totalLines ? lines[curLine].Length : 0);
                        _flushingToScript = true;
                        try { TxtScript.CaretIndex = Math.Min(caret, TxtScript.Text.Length); TxtScript.SelectionLength = 0; }
                        finally { _flushingToScript = false; }
                        _savedSelStartLine = curLine;
                        _savedSelEndLine   = curLine;
                        _arrowAnchorLine   = -1;
                        _dragLine          = -1;
                        UpdateCursorPos();
                        HighlightCurrentLine();
                        EnsureScriptLineVisible(curLine);
                    }
                }
                else
                {
                    // PageUp / PageDown -> move by one viewport of lines, scrolling to follow.
                    int page   = ScriptPageLines();
                    int target = e.Key == System.Windows.Input.Key.PageUp
                        ? Math.Max(0, curLine - page)
                        : Math.Min(totalLines - 1, curLine + page);

                    if (shift)
                    {
                        // Shift+Page extends the selection, like Shift+Arrow.
                        if (_arrowAnchorLine < 0)
                        {
                            _arrowAnchorLine = _savedSelStartLine >= 0 ? _savedSelStartLine : GetCaretLine();
                            _dragLine        = _arrowAnchorLine;
                        }
                        _dragLine = target;
                        SelectScriptLines(_arrowAnchorLine, _dragLine);
                    }
                    else
                    {
                        _arrowAnchorLine = -1;
                        _dragLine        = -1;
                        SelectScriptLines(target, target);
                    }
                    EnsureScriptLineVisible(target);
                }

                e.Handled = true;
                return;
            }
            if (!shift) { _arrowAnchorLine = -1; _dragLine = -1; }

            switch (e.Key)
            {
                // Delete - Del
                case System.Windows.Input.Key.Delete when !ctrl && !alt && hasScript:
                    ScriptDeleteLines(); e.Handled = true; break;

                // Cut - Ctrl+X
                case System.Windows.Input.Key.X when ctrl && hasScript:
                    ScriptCutLines(); e.Handled = true; break;

                // Copy - Ctrl+C
                case System.Windows.Input.Key.C when ctrl && hasScript:
                    ScriptCopyLines(); e.Handled = true; break;

                // Paste - Ctrl+V
                case System.Windows.Input.Key.V when ctrl && hasClip:
                    ScriptPaste(); e.Handled = true; break;

                // Undo - Ctrl+Z
                case System.Windows.Input.Key.Z when ctrl && !shift:
                    BtnUndo_Click(sender, e); e.Handled = true; break;

                // Redo - Ctrl+Y
                case System.Windows.Input.Key.Y when ctrl:
                    BtnRedo_Click(sender, e); e.Handled = true; break;

                // Comment - Ctrl+K
                case System.Windows.Input.Key.K when ctrl && hasScript:
                    ScriptToggleComment(true); e.Handled = true; break;

                // Uncomment - Ctrl+U
                case System.Windows.Input.Key.U when ctrl && hasScript:
                    ScriptToggleComment(false); e.Handled = true; break;

                // Insert Line - Ctrl+I
                case System.Windows.Input.Key.I when ctrl && hasScript:
                    BtnScriptInsert_Click(sender, e); e.Handled = true; break;
            }
        }

        // Click on a line number → highlight that line and move caret there
        private void TxtLineNumbers_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TxtLineNumbers == null || TxtScript == null) return;

            // Calculate the clicked line directly from the Y position and line height.
            // TranslatePoint between TextBlock (TxtLineNumbers) and TextBox (TxtScript) is
            // unreliable because TextBox has internal template padding that TextBlock does not,
            // causing a consistent Y offset that shifts the selected line (e.g. clicking line 299
            // selects line 321).
            //
            // Instead: use GetRectFromCharacterIndex on TxtScript to find the pixel-top of known
            // lines, then binary-search for the line whose top <= click Y <= bottom.

            Point ptInLineNum = e.GetPosition(TxtLineNumbers);

            // The line numbers TextBlock has Padding.Top = 5 and Consolas 12pt gives ~16px lines.
            // ScriptScrollViewer.VerticalOffset gives the scroll position.
            // ptInLineNum.Y is relative to TxtLineNumbers (which is inside LineNumScroll).
            // Since LineNumScroll.ScrollToVerticalOffset mirrors ScriptScrollViewer exactly,
            // the absolute Y of the click in script-content space is:
            double scrollY = ScriptScrollViewer?.VerticalOffset ?? 0;
            double absoluteY = ptInLineNum.Y + scrollY;

            // Find the line by scanning TxtScript line tops via GetRectFromCharacterIndex.
            // This is accurate because it uses WPF's own text layout metrics.
            int bestLine = 0;
            int lineCount = TxtScript.LineCount;
            if (lineCount <= 0) return;

            for (int li = 0; li < lineCount; li++)
            {
                try
                {
                    int ci = TxtScript.GetCharacterIndexFromLineIndex(li);
                    Rect r  = TxtScript.GetRectFromCharacterIndex(ci);
                    if (r == Rect.Empty || double.IsNaN(r.Top)) continue;

                    // r.Top is in TxtScript content space (not scrolled); add scroll offset
                    double lineTop = r.Top;
                    double lineBot = lineTop + (r.Height > 0 ? r.Height : 16.0);

                    if (absoluteY >= lineTop && absoluteY < lineBot)
                    { bestLine = li; break; }
                    if (absoluteY < lineTop)
                    { bestLine = Math.Max(0, li - 1); break; }

                    bestLine = li; // keep updating so last line is selected if click is below all
                }
                catch { }
            }

            bestLine = Math.Max(0, Math.Min(bestLine, lineCount - 1));

            _arrowAnchorLine   = -1;
            _dragLine          = -1;
            _savedSelStartLine = bestLine;
            _savedSelEndLine   = bestLine;

            int lineStartChar = TxtScript.GetCharacterIndexFromLineIndex(bestLine);
            _flushingToScript = true;
            try { TxtScript.CaretIndex = lineStartChar; }
            finally { _flushingToScript = false; }
            TxtScript.Focus();

            UpdateCursorPos();
            HighlightCurrentLine();
            TrySelectCommandFromCurrentLine();
        }

        private void HighlightCurrentLine()
        {
            if (TxtScript == null || LineHighlightRect == null || LineHighlightCanvas == null) return;
            try
            {
                // Read selection purely from our tracked fields - never from WPF selection state
                int selStart = _savedSelStartLine >= 0 ? _savedSelStartLine : GetCaretLine();
                int selEnd   = _savedSelEndLine   >= 0 ? _savedSelEndLine   : selStart;
                if (selStart > selEnd) (selStart, selEnd) = (selEnd, selStart);
                selStart = Math.Max(0, Math.Min(selStart, TxtScript.LineCount - 1));
                selEnd   = Math.Max(0, Math.Min(selEnd,   TxtScript.LineCount - 1));
                bool multiLine = selEnd > selStart;

                // Always clear BOTH highlight types up-front so no stale highlight can survive
                // from a previous selection (prevents two lines appearing highlighted at once).
                var toRemove = LineHighlightCanvas.Children.OfType<System.Windows.Shapes.Rectangle>()
                    .Where(r => r.Tag is string s && s == "multi").ToList();
                foreach (var r in toRemove) LineHighlightCanvas.Children.Remove(r);
                LineHighlightRect.Visibility = Visibility.Collapsed;

                if (multiLine)
                {
                    LineHighlightRect.Visibility = Visibility.Collapsed;
                    for (int li = selStart; li <= selEnd; li++)
                    {
                        int lineStart = TxtScript.GetCharacterIndexFromLineIndex(li);
                        Rect r = TxtScript.GetRectFromCharacterIndex(lineStart);
                        if (r == Rect.Empty || double.IsNaN(r.Top)) continue;
                        Point origin = TxtScript.TranslatePoint(new Point(0, r.Top), LineHighlightCanvas);
                        double lineH = r.Height > 0 ? r.Height : 16.0;
                        var rect = new System.Windows.Shapes.Rectangle
                        {
                            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5500CC55")),
                            Height = lineH, Width = 20000, Tag = "multi", IsHitTestVisible = false
                        };
                        Canvas.SetLeft(rect, 0);
                        Canvas.SetTop(rect, origin.Y);
                        LineHighlightCanvas.Children.Add(rect);
                    }
                    _currentHighlightLine = selStart;
                }
                else
                {
                    int lineStart = TxtScript.GetCharacterIndexFromLineIndex(selStart);
                    Rect r2 = TxtScript.GetRectFromCharacterIndex(lineStart);
                    if (r2 == Rect.Empty || double.IsNaN(r2.Top))
                    { LineHighlightRect.Visibility = Visibility.Collapsed; return; }
                    Point origin = TxtScript.TranslatePoint(new Point(0, r2.Top), LineHighlightCanvas);
                    double lineH = r2.Height > 0 ? r2.Height : 16.0;
                    Canvas.SetTop(LineHighlightRect, origin.Y);
                    LineHighlightRect.Height     = lineH;
                    LineHighlightRect.Visibility = Visibility.Visible;
                    _currentHighlightLine = selStart;
                }
            }
            catch { LineHighlightRect.Visibility = Visibility.Collapsed; }
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject d)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                if (child is ScrollViewer sv) return sv;
                var found = FindScrollViewer(child);
                if (found != null) return found;
            }
            return null;
        }

        private void TxtScript_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateLineNumbers();
            // Re-run search if active so match list stays current
            if (!string.IsNullOrEmpty(_lastSearchTerm))
                RunSearch(_lastSearchTerm, resetIndex: false);
        }
        // ScriptScrollViewer is the single ScrollViewer wrapping TxtScript.
        // It owns both vertical and horizontal scrollbars.
        private void ScriptScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Sync line number panel to vertical scroll
            if (LineNumScroll != null) LineNumScroll.ScrollToVerticalOffset(e.VerticalOffset);
            // Refresh green line highlight position
            if (_currentHighlightLine >= 0) HighlightCurrentLine();
            // Refresh search highlight overlay (positions are viewport-relative)
            if (_searchMatches.Count > 0 && !string.IsNullOrEmpty(_lastSearchTerm))
                ApplySearchHighlights(_lastSearchTerm);
        }

        // Forward mouse wheel from ScriptScrollViewer explicitly (WPF sometimes swallows it)
        /// <summary>
        /// Forces a layout update on any left-click in the script area.
        /// After long idle periods, WPF's TextBox layout goes stale and
        /// GetCharacterIndexFromPoint returns -1 for empty-area clicks.
        /// UpdateLayout() re-validates the layout so hit-testing works correctly.
        /// </summary>
        private void ScriptScrollViewer_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            TxtScript?.UpdateLayout();
        }

        // ── Drag-and-drop: start tracking on mouse-down ──────────────
        private void TxtScript_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            if (TxtScript == null || string.IsNullOrEmpty(TxtScript.Text)) return;

            var pt = e.GetPosition(TxtScript);

            if (!_isDragging)
            {
                // Start drag once mouse moves more than 12px from down-point. A larger threshold
                // means a normal click (with minor hand movement) is never mistaken for a drag,
                // which previously could resurrect a multi-line selection.
                if (!_dragPending) return;
                var delta = pt - _dragStartPoint;
                if (Math.Abs(delta.Y) < 12 && Math.Abs(delta.X) < 12) return;

                // Threshold exceeded - begin drag
                _isDragging = true;
                TxtScript.CaptureMouse();
                TxtScript.Cursor = System.Windows.Input.Cursors.Hand;
                ShowDragIndicator();
                // A real drag started - restore the highlight to the captured source range so the
                // user can see which lines are being moved (mousedown collapsed it to one line).
                if (_dragSourceEnd > _dragSourceStart)
                {
                    _savedSelStartLine = _dragSourceStart;
                    _savedSelEndLine   = _dragSourceEnd;
                    HighlightCurrentLine();
                }
            }

            // Update drop target line from current mouse Y
            _dragTargetLine = GetLineFromPoint(pt);
            UpdateDragIndicator(_dragTargetLine);
            e.Handled = true;
        }

        private void TxtScript_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                // Commit the drop
                TxtScript?.ReleaseMouseCapture();
                TxtScript.Cursor = System.Windows.Input.Cursors.Arrow;
                HideDragIndicator();
                _isDragging  = false;
                _dragPending = false;

                int src  = _dragSourceStart;
                int srcE = _dragSourceEnd;
                int tgt  = _dragTargetLine;

                _dragSourceStart = _dragSourceEnd = _dragTargetLine = -1;

                if (tgt >= 0 && !(tgt >= src && tgt <= srcE + 1))
                    DragDropMoveLines(src, srcE, tgt);

                e.Handled = true;
            }
            else
            {
                // Not a line-move drag. A plain (non-Shift) click must ALWAYS collapse to the single
                // clicked line, regardless of _dragPending state or any selection the read-only
                // TextBox produced between mouse-down and mouse-up. Mouse range-select is not a
                // feature here - multi-line selection is built with Shift+Arrow or Shift+Click - so
                // converting a mouse selection span into a multi-line highlight is exactly what made
                // "click line A then click line B" leave lines A..B highlighted. Only an explicit
                // Shift+click extends the selection.
                bool shiftDown =
                    (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
                _dragPending = false;

                if (TxtScript != null && !string.IsNullOrEmpty(TxtScript.Text))
                {
                    if (shiftDown && TxtScript.SelectionLength > 0)
                    {
                        // Shift+click: honour the native range as a multi-line selection.
                        int s  = TxtScript.GetLineIndexFromCharacterIndex(TxtScript.SelectionStart);
                        int en = TxtScript.GetLineIndexFromCharacterIndex(TxtScript.SelectionStart + TxtScript.SelectionLength);
                        _savedSelStartLine = Math.Min(s, en);
                        _savedSelEndLine   = Math.Max(s, en);
                        HighlightCurrentLine();
                    }
                    else
                    {
                        // Plain click: hard-collapse to the clicked line and clear the native selection.
                        var pt = e.GetPosition(TxtScript);
                        // Robust on empty lines - see LineIndexFromPoint. Mouse-up must agree with
                        // mouse-down, or the up-handler would re-collapse to the wrong line.
                        int clickedLine = LineIndexFromPoint(pt);
                        _savedSelStartLine = clickedLine;
                        _savedSelEndLine   = clickedLine;
                        _dragLine          = -1;
                        _arrowAnchorLine   = -1;
                        int lineStart = TxtScript.GetCharacterIndexFromLineIndex(clickedLine);

                        // The clicked line is already on screen (the user just clicked it), so a click
                        // must never move the view. Setting CaretIndex/SelectionLength on this
                        // (unfocused, read-only) TextBox, plus the param-panel rebuild that follows,
                        // can make WPF auto-scroll the caret into view - which on an empty line
                        // resolved oddly and jumped to the bottom. Capture the scroll offsets now and
                        // restore them after all that settles, so the position is pinned exactly where
                        // it was. Restored at Loaded priority so it runs AFTER the deferred
                        // SelectAddPSScript/BuildParamPanel work.
                        double savedV = ScriptScrollViewer?.VerticalOffset   ?? 0;
                        double savedH = ScriptScrollViewer?.HorizontalOffset ?? 0;

                        _flushingToScript = true;
                        try { TxtScript.CaretIndex = lineStart; TxtScript.SelectionLength = 0; }
                        finally { _flushingToScript = false; }
                        HighlightCurrentLine();
                        TrySelectCommandFromCurrentLine();

                        if (ScriptScrollViewer != null)
                            Dispatcher.InvokeAsync(() =>
                            {
                                ScriptScrollViewer.ScrollToVerticalOffset(savedV);
                                ScriptScrollViewer.ScrollToHorizontalOffset(savedH);
                            }, System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                }
            }
        }

        /// <summary>Start a potential drag from the current selection.</summary>
        private void BeginDragPending(Point ptInScript, int clickedLine)
        {
            _dragPending     = true;
            _dragStartPoint  = ptInScript;

            // A multi-line line-move drag is only intended when the click lands INSIDE an existing
            // multi-line selection. Otherwise (a fresh single-line click), the drag source is just
            // the clicked line - this prevents a small mouse movement from resurrecting a stale
            // multi-line block left over from a previous Comment/MoveUp/selection operation.
            bool clickInsideSelection =
                _savedSelStartLine >= 0 &&
                _savedSelEndLine > _savedSelStartLine &&
                clickedLine >= _savedSelStartLine &&
                clickedLine <= _savedSelEndLine;

            if (clickInsideSelection)
            {
                _dragSourceStart = _savedSelStartLine;
                _dragSourceEnd   = _savedSelEndLine;
            }
            else
            {
                _dragSourceStart = clickedLine;
                _dragSourceEnd   = clickedLine;
            }
            if (_dragSourceStart > _dragSourceEnd) (_dragSourceStart, _dragSourceEnd) = (_dragSourceEnd, _dragSourceStart);
        }

        /// <summary>
        /// Line index under a point, correct on EMPTY lines.
        ///
        /// TextBox.GetCharacterIndexFromPoint(snapToText: true) has a long-standing WPF quirk: for
        /// a point over an empty line it does not snap to that line's zero-length position but to
        /// the nearest rendered character, which for a blank line inside a script can be the end of
        /// the whole document. Every mouse-driven feature of this editor (click, right-click,
        /// drag-move, file drop) derived its line from that call, so a click on a blank line jumped
        /// the caret and highlight to the end of the script. This is that bug's single fix: all six
        /// call sites now come through here.
        ///
        /// The native candidate is kept whenever it is verifiably right (its own line rect contains
        /// the point's Y). When it is not, the correct line is found by binary search over
        /// line-start rects: GetRectFromCharacterIndex at a line start is just the caret rect, which
        /// is reliable for empty lines. Y alone decides the line, which is exactly right for this
        /// line-oriented editor - the caret is always placed at the line start anyway.
        /// </summary>
        private int LineIndexFromPoint(Point ptInScript)
        {
            if (TxtScript == null || string.IsNullOrEmpty(TxtScript.Text)) return 0;
            int last = TxtScript.LineCount - 1;
            try
            {
                int charIdx = TxtScript.GetCharacterIndexFromPoint(ptInScript, snapToText: true);
                if (charIdx >= 0)
                {
                    int cand = Math.Max(0, Math.Min(TxtScript.GetLineIndexFromCharacterIndex(charIdx), last));
                    var r = TxtScript.GetRectFromCharacterIndex(TxtScript.GetCharacterIndexFromLineIndex(cand));
                    if (r != Rect.Empty && !double.IsNaN(r.Top)
                        && ptInScript.Y >= r.Top && ptInScript.Y < r.Bottom)
                        return cand;
                }

                // Candidate missing or mis-snapped (the empty-line quirk): locate the line by Y.
                int lo = 0, hi = last;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    var rm = TxtScript.GetRectFromCharacterIndex(TxtScript.GetCharacterIndexFromLineIndex(mid));
                    if (rm == Rect.Empty || double.IsNaN(rm.Top)) break;
                    if      (ptInScript.Y <  rm.Top)    hi = mid - 1;
                    else if (ptInScript.Y >= rm.Bottom) lo = mid + 1;
                    else return mid;
                }
                // Above the first line lands on 0, below the last line on the last.
                return Math.Max(0, Math.Min(lo, last));
            }
            catch (Exception exSwallow)
            {
                System.Diagnostics.Debug.WriteLine("[CreatePackagePage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message);
                try { return Math.Max(0, Math.Min(TxtScript.GetLineIndexFromCharacterIndex(TxtScript.CaretIndex), last)); }
                catch { return 0; }
            }
        }

        private int GetLineFromPoint(Point ptInScript)
            => LineIndexFromPoint(ptInScript);

        private void ShowDragIndicator()
        {
            if (LineHighlightCanvas == null) return;
            _dragIndicator = new System.Windows.Shapes.Line
            {
                Stroke          = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF00CC55")),
                StrokeThickness = 2,
                X1 = 0, X2 = 20000,
                IsHitTestVisible = false,
                Tag = "dragdrop"
            };
            LineHighlightCanvas.Children.Add(_dragIndicator);
        }

        private void UpdateDragIndicator(int targetLine)
        {
            if (_dragIndicator == null || TxtScript == null) return;
            if (targetLine < 0 || targetLine >= TxtScript.LineCount) return;
            int charIdx = TxtScript.GetCharacterIndexFromLineIndex(targetLine);
            Rect r      = TxtScript.GetRectFromCharacterIndex(charIdx);
            if (r == Rect.Empty || double.IsNaN(r.Top)) return;
            Point origin = TxtScript.TranslatePoint(new Point(0, r.Top), LineHighlightCanvas);
            Canvas.SetTop(_dragIndicator, origin.Y - 1);
        }

        private void HideDragIndicator()
        {
            if (_dragIndicator != null && LineHighlightCanvas != null)
                LineHighlightCanvas.Children.Remove(_dragIndicator);
            _dragIndicator = null;
        }

        private void DragDropMoveLines(int srcStart, int srcEnd, int target)
        {
            if (TxtScript == null) return;
            var lines    = TxtScript.Text.Split('\n').ToList();
            int total    = lines.Count;
            srcStart     = Math.Max(0, Math.Min(srcStart, total - 1));
            srcEnd       = Math.Max(0, Math.Min(srcEnd,   total - 1));
            target       = Math.Max(0, Math.Min(target,   total));
            if (target >= srcStart && target <= srcEnd + 1) return;

            // Extract the dragged lines
            var moved    = lines.GetRange(srcStart, srcEnd - srcStart + 1);
            lines.RemoveRange(srcStart, srcEnd - srcStart + 1);

            // Adjust target after removal
            int insertAt = target > srcEnd ? target - (srcEnd - srcStart + 1) : target;
            insertAt     = Math.Max(0, Math.Min(insertAt, lines.Count));
            lines.InsertRange(insertAt, moved);

            // Apply and restore selection on moved lines
            ReplaceScriptLines(lines.ToArray(), targetLine: insertAt);
            int newEnd   = insertAt + (srcEnd - srcStart);
            int newTotal = TxtScript.Text.Split('\n').Length;
            _savedSelStartLine = Math.Min(insertAt, newTotal - 1);
            _savedSelEndLine   = Math.Min(newEnd,   newTotal - 1);
            _arrowAnchorLine   = -1;
            _dragLine          = -1;
            HighlightCurrentLine();
        }

        private void ScriptScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (ScriptScrollViewer == null) return;
            ScriptScrollViewer.ScrollToVerticalOffset(
                ScriptScrollViewer.VerticalOffset - e.Delta * 0.5);
            e.Handled = true;
        }
        private void TxtScript_SelectionChanged(object sender, RoutedEventArgs e)
        {
            UpdateCursorPos();
            // Auto-select the matching command whenever the caret moves to a new line
            // (covers keyboard navigation: arrow keys, Home, End, Ctrl+G, etc.)
            TrySelectCommandFromCurrentLine();
        }

        private void UpdateLineNumbers()
        {
            if (TxtScript==null||TxtLineNumbers==null) return;
            int n=TxtScript.Text.Split('\n').Length;
            TxtLineNumbers.Text=string.Join("\n",Enumerable.Range(1,n));
            UpdateCursorPos();
        }
        private void UpdateCursorPos()
        {
            if (TxtScript==null||TxtCursorPos==null) return;
            int idx=TxtScript.CaretIndex; string before=TxtScript.Text[..Math.Min(idx,TxtScript.Text.Length)];
            int ln=before.Count(c=>c=='\n')+1; int col=before.Length-(before.LastIndexOf('\n')+1)+1;
            TxtCursorPos.Text=$"Ln {ln}, Col {col}";
        }

        private void MarkScriptDirty()
        { _scriptDirty=true; if(TxtScriptDirty!=null) TxtScriptDirty.Visibility=Visibility.Visible; }

        private void FlashStatus(string msg, string hex)
        {
            // Status flash texts removed - they rendered over the Comment button/tab. Intentionally
            // a no-op so no "Saved" / "No changes" / "Reloaded" / "Nothing to undo" text appears.
        }

        // ══════════════════════════════════════════════════════════════
        // DIALOGS (Info tab)
        // ══════════════════════════════════════════════════════════════
        private void ShowSuccessDialogWithScript(string pkgRoot, string packageName, System.Collections.Generic.List<string>? actionLog = null)
        {
            // Build log base name from app variable fields - spaces removed, invalid chars sanitised
            string San(string s) {
                foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
                return s.Replace(" ", "").Trim('_');
            }
            var logParts = new[]
            {
                San(TxtAppVendor?.Text.Trim()   ?? ""),
                San(TxtAppName?.Text.Trim()     ?? ""),
                San(TxtAppVersion?.Text.Trim()  ?? ""),
                San(TxtAppRevision?.Text.Trim() ?? ""),
            }.Where(p => !string.IsNullOrEmpty(p)).ToArray();
            string logBaseName = logParts.Length > 0
                ? string.Join("_", logParts)
                : packageName.Replace(" ", "");
            string logFile = Path.Combine(Path.GetTempPath(), "CreatePackage_" + logBaseName + ".log");
            try
            {
                var logLines = actionLog ?? new System.Collections.Generic.List<string>
                {
                    "Package Creation Log -- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "Package Name : " + packageName,
                    "Location     : " + pkgRoot,
                    new string('-', 60)
                };
                File.WriteAllLines(logFile, logLines, System.Text.Encoding.UTF8);
            }
            catch { logFile = ""; }

            var win = BuildNewDialog("Create Package - Complete");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - Complete");
            int r = 0;

            // Time row - matches AutoPackage / CreateMST pattern
            var endTime   = DateTime.Now;
            var elapsed   = _pkgCreateStartTime != DateTime.MinValue ? endTime - _pkgCreateStartTime : TimeSpan.Zero;
            string elapsedStr = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
                : elapsed.TotalSeconds >= 1
                    ? $"{(int)elapsed.TotalSeconds}s"
                    : $"{(int)elapsed.TotalMilliseconds}ms";
            string timeVal = _pkgCreateStartTime != DateTime.MinValue
                ? $"Started: {_pkgCreateStartTime:HH:mm:ss}  Finished: {endTime:HH:mm:ss}  Duration: {elapsedStr}"
                : $"Finished: {endTime:HH:mm:ss}";
            NdAddRow(cg, r++, "Time", timeVal, lblFg, boxBg, boxBdr, valFg, true); NdAddSpacer(cg, r++);

            NdAddRow(cg, r++, "Path",     pkgRoot,     lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            if (!string.IsNullOrEmpty(logFile))
            { NdAddRow(cg, r++, "Log",      logFile, lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++); }

            // Show the review note only when the script actually received auto-added commands.
            // Injected lines sit directly after the Installation / Uninstallation markers and
            // run until the next "##" section header, so scan that range only. This excludes the
            // boilerplate (e.g. $adtSession.InstallPhase) that begins the following block, which
            // would otherwise be miscounted as an added command.
            bool cmdsAdded = false;
            try
            {
                string? ps1Path = Directory.GetFiles(pkgRoot, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault();
                if (ps1Path != null)
                {
                    string script = File.ReadAllText(ps1Path).Replace("\r\n", "\n").Replace("\r", "\n");
                    static bool SectionHasCommand(string s, string startMarker)
                    {
                        int idx = s.IndexOf(startMarker, StringComparison.Ordinal);
                        if (idx < 0) return false;
                        int lineEnd = s.IndexOf('\n', idx);
                        if (lineEnd < 0) return false;
                        foreach (var l in s.Substring(lineEnd + 1).Split('\n'))
                        {
                            string t = l.Trim();
                            if (t.StartsWith("##")) break;                       // reached the next section header
                            if (t.Length > 0 && !t.StartsWith("#")) return true; // a real command line
                        }
                        return false;
                    }
                    cmdsAdded = SectionHasCommand(script, "## <Perform Installation tasks here>")
                             || SectionHasCommand(script, "## <Perform Uninstallation tasks here>");
                }
            }
            catch { }

            if (cmdsAdded)
            {
                NdAddRow(cg, r++, "Note",
                    "Commands are added automatically. Please review both install and uninstall sections and make any necessary changes before deploying the application.",
                    lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            }
            string capturedLog = logFile;
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = NdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = NdMakeButtons(sec,
                ("📁 Open Path",   () => { try { string? ps1 = Directory.GetFiles(pkgRoot, "Invoke-AppDeployToolkit.ps1", SearchOption.AllDirectories).FirstOrDefault(); string folderToOpen = ps1 != null ? Path.GetDirectoryName(ps1)! : pkgRoot; Process.Start("explorer.exe", "\"" + folderToOpen + "\""); } catch { } }),
                ("📄 Open Log",    () => { try { Process.Start(new ProcessStartInfo(capturedLog) { UseShellExecute = true }); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CreatePackage] open log failed: " + ex.Message); } }),
                ("✕ Close",        () => { win.Close(); ActivateTab(true); })
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
        }

        private void ShowValidationError(List<string> fields)
        {
            var win = BuildNewDialog("Create Package - Required Fields Missing");
            win.Width = 360; win.MaxWidth = 360;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - Required Fields Missing");
            int r = 0;
            foreach (var f in fields)
            {
                NdAddRow(cg, r++, "Missing", f, lblFg, boxBg, boxBdr, valFg, false);
                NdAddSpacer(cg, r++);
            }
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = NdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = NdMakeButtons(sec, ("✔ OK", () => win.Close()));
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
        }

        private bool ShowFolderExistsDialog(string pkgRoot, string packageName)
        {
            bool result = false;
            var win = BuildNewDialog("Create Package - Folder Exists");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - Folder Exists");
            int r = 0;
            NdAddRow(cg, r++, "Path",     pkgRoot,     lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            NdAddRow(cg, r++, "Action",   "Folder already exists. Do you want to overwrite it?", lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var folderIcon = NdDecisionIcon();
            Grid.SetRow(folderIcon, r); Grid.SetColumn(folderIcon, 0);
            cg.Children.Add(folderIcon);
            var btns = NdMakeButtons(sec,
                ("📁 Open Path", () => { try { Process.Start("explorer.exe", pkgRoot); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CreatePackage] open path failed: " + ex.Message); } }),
                ("✔ Yes", () => { result = true;  win.Close(); }),
                ("✕ No",  () => { result = false; win.Close(); })
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
            return result;
        }

        /// <summary>
        /// Create-time abort notice: the PSADT Template Folder exists but contains no
        /// Invoke-AppDeployToolkit.ps1 anywhere under it (module decoys excluded - see
        /// FindDeploymentPs1). Same dialog family as "Folder Exists" (BuildNewDialog shell,
        /// label + value rows), per request. One OK button, because there is nothing to
        /// decide: without the script the package has no entry point, so creation does not
        /// start. The Path row is where to copy the script TO, with Open Path beside it.
        /// </summary>
        private void ShowTemplateScriptMissingDialog(string templatePath)
        {
            var win = BuildNewDialog("Create Package - Script Not Found");
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - Script Not Found");
            int r = 0;
            NdAddRow(cg, r++, "Missing", "Invoke-AppDeployToolkit.ps1", lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            NdAddRow(cg, r++, "Path",    templatePath,                  lblFg, boxBg, boxBdr, valFg); NdAddSpacer(cg, r++);
            // Deliberately does NOT name a folder to put the script in. The script may live at the
            // template root or in a per-app subfolder (both layouts are in use), and the app cannot
            // know which one this template intends - naming either guesses wrong half the time.
            // "Copy the PSADT template items" sidesteps it: a complete template carries the script
            // wherever that template keeps it.
            NdAddRow(cg, r++, "Action",  "Package not created. Copy the PSADT template items to " + templatePath + ", then click Create again.", lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = NdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);
            var btns = NdMakeButtons(sec,
                ("📁 Open Path", () => { try { Process.Start("explorer.exe", "\"" + templatePath + "\""); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CreatePackage] open path failed: " + ex.Message); } }),
                ("✔ OK", () => win.Close())
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
        }

        private bool ShowPs1LockedDialog(string ps1Path)
        {
            bool result=false; string fn=Path.GetFileName(ps1Path);
            var win=MakeDialogWindow(520,280);
            var outer=MakeDialogGrid();
            var hdr=MakeDialogHeaderThemed("🔒","Script File is Currently Open","neutral",()=>{result=false;win.Close();});
            var body=new StackPanel{Margin=new Thickness(22,18,22,10)};
            body.Children.Add(new TextBlock{Text=fn+" is currently open in another application.",FontSize=13,FontWeight=FontWeights.SemiBold,Foreground=TC("TextPrimaryBrush"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)});
            body.Children.Add(new TextBlock{Text="Please close the file in your editor, then click Continue to proceed - or click Cancel to abort.",FontSize=12,Foreground=TC("TextSecondaryBrush"),TextWrapping=TextWrapping.Wrap});
            var ftr=new Border{BorderBrush=TC("DlgBorderBrush"),BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(20,12,20,12),Background=TC("DlgFooterBgBrush")};
            var fs=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            var bCancel=DlgBtnStyled("✕ Cancel","neutral",110); bCancel.Click+=(_,_)=>{result=false;win.Close();};
            var bCont=DlgBtnStyled("✔ Continue","green",120);
            bCont.Click+=(_,_)=>{result=true;win.Close();}; fs.Children.Add(bCancel); fs.Children.Add(bCont);
            ftr.Child=fs; Grid.SetRow(body,1); Grid.SetRow(ftr,2);
            outer.Children.Add(hdr); outer.Children.Add(body); outer.Children.Add(ftr);
            var root=MakeDialogRoot(win); root.Child=outer; win.Content=root; win.ShowDialog(); return result;
        }

        // ── Dialog factory helpers ──
        private Window MakeDialogWindow(double w, double h) => new Window {
            Width=w, Height=h, WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Owner=Window.GetWindow(this), ResizeMode=ResizeMode.NoResize,
            Background=TC("WindowBgBrush"),
            WindowStyle=WindowStyle.None, AllowsTransparency=true
        };
        private static Window MakeDialogWindowStatic(double w, double h) => new Window {
            Width=w, Height=h, WindowStartupLocation=WindowStartupLocation.CenterScreen,
            ResizeMode=ResizeMode.NoResize,
            Background=TC("WindowBgBrush"),
            WindowStyle=WindowStyle.None, AllowsTransparency=true
        };
        private static Border MakeDialogRoot(Window win) {
            var r=new Border{Background=TC("DlgBodyBgBrush"),BorderBrush=TC("DlgBorderBrush"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12)};
            r.MouseLeftButtonDown+=(_,_)=>win.DragMove(); return r;
        }
        private static Grid MakeDialogGrid() {
            var g=new Grid();
            g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            g.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto}); return g;
        }
        private static Border MakeDialogHeader(string icon, string title, Color bg, Color fg, Action onClose)
        {
            var hdr=new Border{Background=new SolidColorBrush(bg),CornerRadius=new CornerRadius(12,12,0,0),Padding=new Thickness(20,14,20,14)};
            var g=new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto}); g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)}); g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var ic=new TextBlock{Text=icon,FontSize=22,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)};
            var ti=new TextBlock{Text=title,FontSize=15,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(fg),VerticalAlignment=VerticalAlignment.Center};
            var cl=new Button{Content="✕",FontSize=13,Foreground=TC("TextSecondaryBrush"),Background=Brushes.Transparent,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
            cl.Click+=(_,_)=>onClose(); Grid.SetColumn(ic,0); Grid.SetColumn(ti,1); Grid.SetColumn(cl,2);
            g.Children.Add(ic); g.Children.Add(ti); g.Children.Add(cl); hdr.Child=g; Grid.SetRow(hdr,0); return hdr;
        }
        // Themed overload - preferred
        private static Border MakeDialogHeaderThemed(string icon, string title, string style, Action onClose)
        {
            string bgKey = style switch { "red"=>"DlgHdrRedBg","amber"=>"DlgHdrAmberBg","green"=>"DlgHdrGreenBg","blue"=>"DlgHdrBlueBg",_=>"DlgHdrNeutralBg" };
            string fgKey = style switch { "red"=>"DlgHdrRedFg","amber"=>"DlgHdrAmberFg","green"=>"DlgHdrGreenFg","blue"=>"DlgHdrBlueFg",_=>"DlgHdrNeutralFg" };
            Color bg = Application.Current.TryFindResource(bgKey) is Color c ? c : Color.FromRgb(0x25,0x28,0x44);
            Color fg = Application.Current.TryFindResource(fgKey) is Color f ? f : Color.FromRgb(0xEA,0xED,0xF5);
            return MakeDialogHeader(icon, title, bg, fg, onClose);
        }
        static UIElement DlgInfoRow(string label, string value) {
            var g=new Grid{Margin=new Thickness(0,0,0,10)};
            g.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto}); g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            var lbl=new TextBlock{Text=label,FontSize=11,Foreground=TC("TextSecondaryBrush"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0),MinWidth=70};
            var box=new Border{Background=TC("InputBgBrush"),BorderBrush=TC("BorderMainBrush"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Padding=new Thickness(10,6,10,6)};
            box.Child=new TextBlock{Text=value,FontSize=11,Foreground=TC("TextPrimaryBrush"),TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas")};
            Grid.SetColumn(lbl,0); Grid.SetColumn(box,1); g.Children.Add(lbl); g.Children.Add(box); return g;
        }
        // DlgBtn "style" tokens: "neutral" | "blue" | "green" | "red" | "amber"
        static Button DlgBtn(string text, string _bgIgnored, string _borderIgnored, double width)
            => DlgBtnStyled(text, "neutral", width);
        static Button DlgBtnStyled(string text, string style, double width)
        {
            string bgKey     = style switch { "blue"=>"DlgBtnBlueBgBrush",   "green"=>"DlgBtnGreenBgBrush",
                                              "red" =>"DlgBtnRedBgBrush",    "amber"=>"DlgBtnAmberBgBrush",
                                              _     =>"DlgBtnNeutralBgBrush" };
            string fgKey     = style switch { "blue"=>"DlgBtnBlueFgBrush",   "green"=>"DlgBtnGreenFgBrush",
                                              "red" =>"DlgBtnRedFgBrush",    "amber"=>"DlgBtnAmberFgBrush",
                                              _     =>"DlgBtnNeutralFgBrush" };
            string borderKey = style switch { "blue"=>"DlgBtnBlueBorderBrush","green"=>"DlgBtnGreenBorderBrush",
                                              "red" =>"DlgBtnRedBorderBrush", "amber"=>"DlgBtnAmberBorderBrush",
                                              _     =>"DlgBtnNeutralBorderBrush" };
            return new Button { Content=text, Width=width, Height=36, Margin=new Thickness(0,0,10,0),
                Background=TC(bgKey), Foreground=TC(fgKey), BorderBrush=TC(borderKey),
                BorderThickness=new Thickness(1), FontSize=12, FontWeight=FontWeights.SemiBold,
                Cursor=Cursors.Hand };
        }

        // ══════════════════════════════════════════════════════════════
        // UTILITY - locked file / editor close
        // ══════════════════════════════════════════════════════════════
        private static bool IsFileLocked(string p) { try{using var fs=new FileStream(p,FileMode.Open,FileAccess.ReadWrite,FileShare.None);return false;}catch(IOException){return true;}catch{return false;} }
        private static void TryClosePs1InEditors(string _) { try{foreach(var proc in Process.GetProcesses()){try{string n=proc.ProcessName.ToLowerInvariant();if(n is "powershell_ise" or "notepad" or "notepad++" or "code" or "powershell")proc.CloseMainWindow();}catch{}}}catch{} }

        // ─── Embedded PSM1 help (pre-parsed from PSAppDeployToolkit.psm1) ───
        // NOTE: The embedded PSADT help-text database was removed. Help is now sourced
        // live from the user's installed PSAppDeployToolkit module (.psm1) or the official
        // online documentation. This avoids bundling PSADT's
        // copyrighted documentation text inside this application.
        // ═══════════════════════════════════════════════════════════════
        // SCRIPT SEARCH - Find in TxtScript with yellow highlight
        // ═══════════════════════════════════════════════════════════════

        private void TxtScriptSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string term = TxtScriptSearch.Text;

            // Show/hide placeholder and clear button
            TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(term)
                ? Visibility.Visible : Visibility.Collapsed;
            BtnClearScriptSearch.Visibility = string.IsNullOrEmpty(term)
                ? Visibility.Collapsed : Visibility.Visible;

            if (string.IsNullOrEmpty(term))
            {
                ClearSearchHighlights();
                TxtSearchCount.Visibility = Visibility.Collapsed;
                BtnSearchPrev.IsEnabled   = false;
                BtnSearchNext.IsEnabled   = false;
                return;
            }

            RunSearch(term, resetIndex: true);
        }

        private void TxtScriptSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Return ||
                e.Key == System.Windows.Input.Key.Enter)
            {
                if ((System.Windows.Input.Keyboard.Modifiers &
                     System.Windows.Input.ModifierKeys.Shift) != 0)
                    NavigateSearch(-1);
                else
                    NavigateSearch(+1);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                BtnClearScriptSearch_Click(sender, e);
                e.Handled = true;
            }
        }

        private void BtnClearScriptSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtScriptSearch.Clear();
            TxtScriptSearch.Focus();
        }

        private void BtnSearchPrev_Click(object sender, RoutedEventArgs e)
            => NavigateSearch(-1);

        private void BtnSearchNext_Click(object sender, RoutedEventArgs e)
            => NavigateSearch(+1);

        // ── Core search engine ──────────────────────────────────────────
        private void RunSearch(string term, bool resetIndex)
        {
            _lastSearchTerm = term;
            _searchMatches.Clear();

            string text = TxtScript.Text;
            int idx = 0;
            while (true)
            {
                idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                _searchMatches.Add(idx);
                idx += term.Length;
            }

            if (resetIndex)
                _searchIndex = _searchMatches.Count > 0 ? 0 : -1;

            ApplySearchHighlights(term);
            UpdateSearchNavUI();

            // Only scroll to the match when the user has just typed a search term,
            // not when called from ScrollChanged or TextChanged (resetIndex=false).
            if (resetIndex && _searchIndex >= 0)
                ScrollToMatch(_searchIndex);
        }

        private void NavigateSearch(int direction)
        {
            if (_searchMatches.Count == 0) return;

            int newIndex = _searchIndex + direction;

            // Detect wrap-around
            if (newIndex >= _searchMatches.Count || newIndex < 0)
            {
                string msg = direction > 0
                    ? "Search completed. Do you want to search again?"
                    : "Search completed. Do you want to search again?";

                bool wrapResult = ShowSearchWrapDialog(msg);
                if (wrapResult)
                {
                    // Wrap around
                    _searchIndex = direction > 0 ? 0 : _searchMatches.Count - 1;
                }
                else
                {
                    // Clear search
                    TxtScriptSearch.Clear();
                    return;
                }
            }
            else
            {
                _searchIndex = newIndex;
            }

            ApplySearchHighlights(_lastSearchTerm);
            UpdateSearchNavUI();
            ScrollToMatch(_searchIndex);
        }

        // Highlight all matches using DrawingVisual overlay on ScriptEditorGrid.
        // Current match  → orange  rgba(255, 140, 0, 210)
        // Other matches  → yellow  rgba(255, 220, 0, 160)
        // Clearing (empty term or no matches) removes all highlights (Fix 2).
        private void ApplySearchHighlights(string term)
        {
            RemoveSearchOverlay();
            if (string.IsNullOrEmpty(term) || _searchMatches.Count == 0) return;

            // Yellow for all non-current matches; orange for the current match
            var yellowBrush  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(160, 255, 220, 0));   // yellow
            var orangeBrush  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(210, 255, 140, 0));   // orange

            var overlay = new System.Windows.Media.DrawingVisual();
            using (var dc = overlay.RenderOpen())
            {
                // Draw non-current matches first (yellow), then current on top (orange)
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 0; i < _searchMatches.Count; i++)
                    {
                        bool isCurrent = (i == _searchIndex);
                        if (pass == 0 && isCurrent)  continue;  // skip current on first pass
                        if (pass == 1 && !isCurrent) continue;  // skip others on second pass

                        var brush = isCurrent ? orangeBrush : yellowBrush;
                        foreach (var r in GetCharRects(_searchMatches[i], term.Length))
                            dc.DrawRectangle(brush, null, r);
                    }
                }
            }

            if (LineHighlightCanvas != null)
            {
                _searchOverlay = new OverlayVisualHost(overlay);
                _searchOverlay.IsHitTestVisible = false;
                // Add to LineHighlightCanvas (which is inside ScriptOverlayClip - clipped)
                LineHighlightCanvas.Children.Add(_searchOverlay);
            }
            // NOTE: ScrollToMatch is intentionally NOT called here.
            // ApplySearchHighlights is called on every scroll event to redraw
            // the overlay at the new viewport position. Calling ScrollToMatch
            // here would snap the viewport back on every scroll (locking it).
            // ScrollToMatch is only called from NavigateSearch and RunSearch.
        }

        private OverlayVisualHost? _searchOverlay;

        private void RemoveSearchOverlay()
        {
            if (_searchOverlay != null && LineHighlightCanvas != null)
            {
                LineHighlightCanvas.Children.Remove(_searchOverlay);
                _searchOverlay = null;
            }
        }

        private void ClearSearchHighlights()
        {
            _searchMatches.Clear();
            _searchIndex = -1;
            _lastSearchTerm = "";
            RemoveSearchOverlay();
            // Hide the match counter and reset nav buttons
            if (TxtSearchCount   != null) TxtSearchCount.Visibility   = Visibility.Collapsed;
            if (BtnSearchPrev    != null) BtnSearchPrev.IsEnabled      = false;
            if (BtnSearchNext    != null) BtnSearchNext.IsEnabled      = false;
        }

        // Get the rendered Rect of characters [start..start+length) in LineHighlightCanvas space.
        // The OverlayVisualHost is parented to LineHighlightCanvas, so DrawingVisual coords
        // must be in LineHighlightCanvas coordinate space.
        // TxtScript → LineHighlightCanvas via TranslatePoint handles scroll offset correctly.
        private List<System.Windows.Rect> GetCharRects(int start, int length)
        {
            var rects = new List<System.Windows.Rect>();
            try
            {
                if (start < 0 || start + length > TxtScript.Text.Length) return rects;
                var r1 = TxtScript.GetRectFromCharacterIndex(start);
                var r2 = TxtScript.GetRectFromCharacterIndex(start + length);
                if (r1.IsEmpty || r2.IsEmpty) return rects;

                // Translate from TxtScript viewport space → LineHighlightCanvas space
                Point p1 = TxtScript.TranslatePoint(new Point(r1.X, r1.Y), LineHighlightCanvas);
                Point p2 = TxtScript.TranslatePoint(new Point(r2.X, r2.Y), LineHighlightCanvas);

                double x      = Math.Min(p1.X, p2.X);
                double y      = p1.Y;
                double width  = Math.Abs(p2.X - p1.X);
                double height = Math.Max(r1.Height, r2.Height);
                if (width < 4) width = 8;
                rects.Add(new System.Windows.Rect(x, y, width, height));
            }
            catch { }
            return rects;
        }

        private void ScrollToMatch(int idx)
        {
            if (idx < 0 || idx >= _searchMatches.Count) return;
            int pos = _searchMatches[idx];
            TxtScript.CaretIndex = pos;
            // Scroll the wrapping ScrollViewer so the match line is visible
            if (ScriptScrollViewer != null)
            {
                // Get the Y position of the match in TxtScript space, translate to ScrollViewer space
                try
                {
                    Rect r = TxtScript.GetRectFromCharacterIndex(pos);
                    if (!r.IsEmpty)
                    {
                        Point pt = TxtScript.TranslatePoint(new Point(0, r.Top), ScriptScrollViewer);
                        double target = ScriptScrollViewer.VerticalOffset + pt.Y - ScriptScrollViewer.ViewportHeight / 3;
                        ScriptScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
                    }
                }
                catch { }
            }
        }

        private void UpdateSearchNavUI()
        {
            int total = _searchMatches.Count;
            bool hasMatches = total > 0;

            BtnSearchPrev.IsEnabled = hasMatches;
            BtnSearchNext.IsEnabled = hasMatches;

            if (hasMatches)
            {
                TxtSearchCount.Text       = $"{_searchIndex + 1}/{total}";
                TxtSearchCount.Visibility = Visibility.Visible;
                TxtSearchCount.Foreground = FindResource("AccentBlueBrush") as System.Windows.Media.Brush
                                            ?? System.Windows.Media.Brushes.SkyBlue;
            }
            else
            {
                TxtSearchCount.Text       = "0/0";
                TxtSearchCount.Visibility = Visibility.Visible;
                TxtSearchCount.Foreground = System.Windows.Media.Brushes.OrangeRed;
            }
        }


        // ══ New-style dialog helpers (Auto Package / Create Intunewin format) ══════

        private Window BuildNewDialog(string title)
        {
            // Use a visually distinct background so the dialog stands out from the page
            var winBg     = Application.Current.TryFindResource("NavBarBgBrush")   as Brush
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
                FontFamily = new FontFamily("Segoe UI"),
                ShowInTaskbar = false
            };
            var outer = new Border
            {
                Background = winBg, BorderBrush = borderClr,
                BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black
                }
            };
            win.Content = outer;
            return win;
        }

        private (Grid outerGrid, Grid contentGrid,
                 Brush boxBg, Brush boxBdr, Brush lblFg, Brush valFg, Style? sec)
            BuildNewDialogShell(Window win, string titleText)
        {
            var borderClr  = Application.Current.TryFindResource("BorderMainBrush") as Brush ?? Brushes.LightGray;
            var titleBarBg = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));
            var txtPri     = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? Brushes.Black;
            var txtSec     = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            // Use white text on the dark title bar for contrast
            Brush titleTxtBrush = Brushes.White;
            Brush titleSecBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            var outer      = (Border)win.Content;
            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;
            var tb = new Border { Background = titleBarBg, BorderBrush = borderClr, BorderThickness = new Thickness(0,0,0,1), CornerRadius = new CornerRadius(10,10,0,0), Padding = new Thickness(16,6,12,6) };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ti  = new TextBlock { Text = "📦", FontSize = 14, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) };
            var tt  = new TextBlock { Text = titleText, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxtBrush, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button   { Content = "✕", FontSize = 11, Foreground = titleSecBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6,2,6,2) };
            tcl.Click += (_, _) => win.Close();
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg; og.Children.Add(tb);
            var cg = new Grid { Margin = new Thickness(8), Background = Brushes.Transparent };
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cg, 1); og.Children.Add(cg);
            // Use WindowBgBrush for boxes so they appear white/light against the darker dialog bg
            var boxBg  = Application.Current.TryFindResource("WindowBgBrush")      as Brush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            var boxBdr = Application.Current.TryFindResource("BorderMainBrush")    as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDF, 0xEA));
            var lblFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));
            var valFg  = Application.Current.TryFindResource("TextPrimaryBrush")   as Brush ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x2E));
            var sec    = Application.Current.TryFindResource("SecondaryButtonStyle") as Style;
            return (og, cg, boxBg, boxBdr, lblFg, valFg, sec);
        }

        private static void NdAddRow(Grid g, int row, string label, string value,
            Brush lblFg, Brush boxBg, Brush boxBdr, Brush valFg, bool mono = true)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var box = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10,6,10,6) };
            box.Child = new TextBlock { Text = value, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = valFg, FontFamily = new FontFamily(mono ? "Consolas" : "Segoe UI") };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            Grid.SetRow(box, row); Grid.SetColumn(box, 1);
            g.Children.Add(lbl); g.Children.Add(box);
        }

        private static void NdAddSpacer(Grid g, int row, double h = 8)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        }

        private static void NdAddIconRow(Grid g, int row, string icon)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tb = new TextBlock
            {
                Text = icon, FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
        }

        private static System.Windows.Controls.Primitives.UniformGrid NdMakeButtons(
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
                var margin = i == 0 ? new Thickness(0,0,4,0) : i == buttons.Length-1 ? new Thickness(4,0,0,0) : new Thickness(4,0,4,0);
                var b = new Button { Content = buttons[i].label, Style = sec, FontSize = 11, FontWeight = FontWeights.SemiBold, Height = 28, Padding = new Thickness(6,0,6,0), Margin = margin };
                if (i == primary) NdApplyPrimaryLook(b);
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
        private static void NdApplyPrimaryLook(Button b)
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
        private static TextBlock NdDecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };

        private bool ShowSearchWrapDialog(string message)
        {
            bool result = false;
            var win = BuildNewDialog("Create Package - Search");
            win.Width = 360; win.MaxWidth = 360;
            var (_, cg, boxBg, boxBdr, lblFg, valFg, sec) = BuildNewDialogShell(win, "Create Package - Search");
            int r = 0;
            NdAddRow(cg, r++, "Status", message, lblFg, boxBg, boxBdr, valFg, false); NdAddSpacer(cg, r++);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = NdDecisionIcon();
            Grid.SetRow(rowIcon, r); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);
            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var btns = NdMakeButtons(sec,
                ("✔ Yes", () => { result = true;  win.Close(); }),
                ("✕ No",  () => { result = false; win.Close(); })
            );
            Grid.SetRow(btns, r); Grid.SetColumn(btns, 1);
            cg.Children.Add(btns);
            win.ShowDialog();
            return result;
        }

        // ── OverlayVisualHost - lightweight UIElement wrapper for DrawingVisual ──
        private sealed class OverlayVisualHost : System.Windows.FrameworkElement
        {
            private readonly System.Windows.Media.Visual _visual;
            public OverlayVisualHost(System.Windows.Media.Visual v)
            {
                _visual = v;
                AddVisualChild(v);
                AddLogicalChild(v);
            }
            protected override int VisualChildrenCount => 1;
            protected override System.Windows.Media.Visual GetVisualChild(int index) => _visual;
        }


    }

    /// <summary>Settings persisted in CreatePackage.json.</summary>
    public class CreatePackageConfig
    {
        /// <summary>Default PSADT Template Folder path.</summary>
        public string? PSADTTemplateFolder    { get; set; }
        // Legacy alias - older JSON files may still use "TemplateFolder"
        public string? TemplateFolder         { get; set; }
        /// <summary>Default Package Output Folder path.</summary>
        public string? PackageOutputFolder   { get; set; }
        /// <summary>Default Source File or Folder path.</summary>
        public string? SourceFolder          { get; set; }
        // Legacy alias - older JSON files may still use "SourcePath"
        public string? SourcePath            { get; set; }

        // ── Per-file-type command injection flags ─────────────────────────
        // "Yes" / "No" (or true/false). Default = true (inject all types).
        public string? AddMsiCommandToPs1 { get; set; }
        public string? AddMstCommandToPs1 { get; set; }
        public string? AddMspCommandToPs1 { get; set; }
        public string? AddExeCommandToPs1 { get; set; }
        public string? AddBatCommandToPs1 { get; set; }
        public string? AddIssCommandToPs1 { get; set; }
        public string? AddRegCommandToPs1 { get; set; }
        public string? AddCerCommandToPs1 { get; set; }
        public string? AddInfCommandToPs1    { get; set; }
        public string? AddPS1CommandToPs1    { get; set; }
        public string? AddCloseAppsCommandToPs1 { get; set; }

        // Helper: parse "Yes"/"No"/true/false → bool (default true)
        public static bool Flag(string? v) =>
            v == null || string.Equals(v.Trim(), "yes", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        // Strict helper: only true when explicitly "Yes" or "true" (no default)
        public static bool FlagStrict(string? v) =>
            v != null && (string.Equals(v.Trim(), "yes",  StringComparison.OrdinalIgnoreCase)
                       || string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        // If "Yes", InstallName is auto-populated in the Info tab (default true)
        public string? AddInstallName  { get; set; }
        // If "Yes", InstallTitle is auto-populated in the Info tab (default true)
        public string? AddInstallTitle { get; set; }

        // ARP flags - if "Yes", inject Set-ADTRegistryKey for NoModify/NoRepair/NoRemove
        // after ## <Perform Post-Installation tasks here> when not already in uninstall key
        public string? AddNoModify { get; set; }
        public string? AddNoRepair { get; set; }
        public string? AddNoRemove { get; set; }

        // App identity - populated at runtime so PatchInstallCommands can build a specific ARP comment
        public string? AppVendor  { get; set; }
        public string? AppName    { get; set; }
        public string? AppVersion { get; set; }
    }
}
