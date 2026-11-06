using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SwiftPack.Views
{
    // ══════════════════════════════════════════════════════════════════════
    // Azure AD / Graph settings loaded from swiftpack-settings.json
    // Expected path: C:\Program Files\Smekal\SwiftPack Studio\swiftpack-settings.json
    // ══════════════════════════════════════════════════════════════════════
    internal record GraphSettings(string ClientId, string TenantId);

    public partial class PublishPackagePage : Page
    {
        // ── Settings file path ──────────────────────────────────────────
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Smekal", "SwiftPack Studio", "Templates", "IntuneSettings.json");

        // Writes text to a path that may live under a protected location (C:\Program Files\...). A
        // plain File.WriteAllText there fails with UnauthorizedAccessException unless the app runs
        // elevated - and under UAC file virtualization it can even redirect the write to a per-user
        // VirtualStore shadow copy, so the app keeps reading the old Program Files file and the edit
        // "does not take". This tries the direct write first; on an access failure it stages the
        // content to a temp file and copies it into place through an elevated one-shot cmd (a single
        // UAC prompt). Returns true on success; on failure sets error and returns false.
        private static bool WriteProtectedFile(string path, string content, out string error)
        {
            error = "";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Fall through to the elevated copy below.
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            // Elevated fallback: stage to %TEMP%, then copy into the protected folder as admin.
            string tmp = Path.Combine(Path.GetTempPath(), "SwiftPack_settings_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmp, content);
                string destDir = Path.GetDirectoryName(path)!;
                // cmd: ensure the folder exists, then copy the staged file over the target. /y = no prompt.
                string args = $"/c md \"{destDir}\" 2>nul & copy /y \"{tmp}\" \"{path}\"";
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", args)
                {
                    UseShellExecute = true,     // required for Verb=runas (UAC)
                    Verb            = "runas",  // request elevation
                    CreateNoWindow  = true,
                    WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
                bool ok = proc != null && proc.ExitCode == 0 && File.Exists(path);
                if (!ok) error = "The elevated copy did not complete. Run SwiftPack Studio as administrator and try again.";
                return ok;
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                error = "Elevation was declined. Saving to a protected folder needs administrator rights.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ── Auth state ──────────────────────────────────────────────────
        private bool   _isSignedIn   = false;
        // Raised so an external host (the MainWindow top bar) can drive and mirror the
        // Intune sign-in button. Fired when the signed-in state or button-enabled state changes.
        public event Action<bool>? IntuneSignedInChanged;
        public event Action<bool>? IntuneSignInEnabledChanged;
        // Raised true while an interactive Intune sign-in is in progress, false when it finishes,
        // so the host (MainWindow) can show its overlay progress bar.
        public event Action<bool>? IntuneSignInBusyChanged;
        private string _accessToken  = "";
        private string _tenantName   = "";

        // ── Package data fed from CreatePackagePage ─────────────────────
        private string? _scriptPath;
        private string? _mainFilePath;
        private string? _appName;
        private string? _appVendor;
        private string? _appVersion;
        private string? _intunewinPath;

        // Subscribes auto-save triggers to user-driven change events for the whole page,
        // including controls created later (detection cards, list rows). Uses AddHandler with
        // handledEventsToo=true so it still fires even when inner handlers mark the event handled.
        private void AttachAutoSaveHandlers(DependencyObject _)
        {
            AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, ev) =>
                {
                    // Log output is programmatic - appending lines must not trigger json writes
                    // (the publish script reads that json while the log is streaming).
                    if (ReferenceEquals(ev.OriginalSource, LogTextBox)) return;
                    RequestAutoSave();
                }), true);
            AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
                new RoutedEventHandler((_, __) => RequestAutoSave()), true);
            AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
                new RoutedEventHandler((_, __) => RequestAutoSave()), true);
            AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
                new SelectionChangedEventHandler((_, __) => RequestAutoSave()), true);
        }

        // ── Auto-save (debounced) ─────────────────────────────────────
        // Whenever the user changes anything on the page, schedule a silent write of the
        // per-app json next to the .intunewin (same file/path the Save button writes). The
        // debounce coalesces rapid edits (typing, dropdown changes) into a single write.
        private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;
        private bool _autoSaveSuspended = false;   // set during bulk load so loading doesn't trigger writes

        // ── Live mirror to Create Document ────────────────────────────────────
        // Raised on EVERY (non-suspended) manual change anywhere on this page -
        // RequestAutoSave is the central change hook (text/checkbox/combo edits and
        // the detection rule cards all funnel through it). MainWindow subscribes
        // and pushes the current values to the Create Document page immediately.
        public event Action? LiveDataChanged;

        private void RequestAutoSave()
        {
            if (_autoSaveSuspended) return;
            try { LiveDataChanged?.Invoke(); } catch { }
            _autoSaveTimer ??= CreateAutoSaveTimer();
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        private System.Windows.Threading.DispatcherTimer CreateAutoSaveTimer()
        {
            var t = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                PerformAutoSave();
            };
            return t;
        }

        private void PerformAutoSave()
        {
            try
            {
                string path = TxtIntunewinPath?.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return; // no target yet
                // Do not create/update the companion JSON until every mandatory field is filled -
                // the file must only appear once the app is complete (same gate as the Save button).
                if (GetMissingMandatoryFields().Count > 0) return;
                string outputFolder = Path.GetDirectoryName(path) ?? "";
                if (string.IsNullOrWhiteSpace(outputFolder)) return;
                WriteIntuneAppSettingsJson(outputFolder);  // silent - no message box
            }
            catch { /* never interrupt the user for an auto-save failure */ }
        }


        // ── Active tab ──────────────────────────────────────────────────
        private int _activeTab = 1;

        // ── Detection rules ─────────────────────────────────────────────
        private readonly List<DetectionRule> _detectionRules = new();
        // Which rule types the IntuneSettings.json selection actually turned ON (captured in
        // LoadIntuneDefaults). The auto-populate path fills MSI/File/Registry detail values from the
        // installed app, but must NOT tick a rule the user did not select - so it enables each rule
        // from these flags rather than forcing all three on. Default true keeps the historic
        // behaviour if settings could not be read.
        private bool _settingsWantMsi  = true;
        private bool _settingsWantFile = true;
        private bool _settingsWantReg  = true;
        // Guard: LoadIntuneDefaults runs once per page instance only
        private bool _defaultsLoaded = false;
        // Guard: prevents TxtIntunewinPath_TextChanged from firing multiple times
        // when the path is set programmatically (Browse, Drop, AutoFill all set .Text)
        private bool _suppressPathChanged = false;
        // Track last MSI path to detect when a different package is loaded
        private string _lastMainFilePath = "";
        // Track last app name to detect when a different app is loaded
        private string _lastAppName      = "";
        // Track last intunewin path - avoid re-loading defaults on repeat navigation
        private string _lastIntunewinPath = "";
        // Log folder derived from Config\config.psd1
        private string _psadtLogFolder = "";

        public PublishPackagePage()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                InitDefaultReturnCodes();
                // Subscribe here so TxtIntunewinPath is guaranteed to be initialized
                TxtIntunewinPath.TextChanged += TxtIntunewinPath_TextChanged;
                // The Publish button depends on App Name + Publisher too - re-evaluate
                // live so filling them in (re)enables the button without touching the path.
                TxtAppName.TextChanged   += (_, _) => UpdatePublishButton();
                TxtPublisher.TextChanged += (_, _) => UpdatePublishButton();
                UpdatePublishButton();
                UpdateTabsEnabled();
                AttachCaretHandlerToAllTextBoxes(this);
                this.PreviewMouseLeftButtonDown += Page_PreviewMouseLeftButtonDown;
                // Always show the three fixed detection rule cards (MSI/File/Registry).
                RebuildDetectionRuleUI();
                // Debounced auto-save: persist the per-app json whenever the user edits anything.
                AttachAutoSaveHandlers(this);
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

        // ══════════════════════════════════════════════════════════════
        // DEFAULT RETURN CODE ROWS - populated on page load
        // ══════════════════════════════════════════════════════════════
        private static readonly (string Code, string Type)[] _defaultReturnCodes =
        {
            ("0",    "Success"),
            ("1707", "Success"),
            ("3010", "Soft reboot"),
            ("1641", "Hard reboot"),
            ("1618", "Retry"),
        };

        private void InitDefaultReturnCodes()
        {
            ReturnCodesPanel.Children.Clear();
            foreach (var (code, type) in _defaultReturnCodes)
                ReturnCodesPanel.Children.Add(MakeReturnCodeRow(code, type));
        }

        private Grid MakeReturnCodeRow(string code = "", string type = "Success")
        {
            var grid = new Grid { Margin = new Thickness(10, 2, 10, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var codeBox = new TextBox
            {
                Text       = code,
                Height     = 22, FontSize = 11, Width = 160,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Style      = (Style)Application.Current.TryFindResource("ModernTextBoxStyle"),
                Tag        = "Return code (number)"
            };
            Grid.SetColumn(codeBox, 0);

            var typeCmb = new ComboBox
            {
                Height = 22, FontSize = 11,
                Style  = (Style)Application.Current.TryFindResource("ModernComboBoxStyle")
            };
            foreach (var t in new[] { "Success", "Failed", "Soft reboot", "Hard reboot", "Retry" })
            {
                var item = new ComboBoxItem { Content = t };
                if (t == type) item.IsSelected = true;
                typeCmb.Items.Add(item);
            }
            if (typeCmb.SelectedIndex < 0) typeCmb.SelectedIndex = 0;
            Grid.SetColumn(typeCmb, 1);

            var del = new Button
            {
                Content         = "✕", Width = 20, Height = 20, FontSize = 10,
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor          = System.Windows.Input.Cursors.Hand,
                Margin          = new Thickness(4, 0, 0, 0)
            };
            var row = grid;
            del.Click += (_, _) => ReturnCodesPanel.Children.Remove(row);
            Grid.SetColumn(del, 2);

            grid.Children.Add(codeBox);
            grid.Children.Add(typeCmb);
            grid.Children.Add(del);
            return grid;
        }

        // ══════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════
        // CLEAR - clears user-entered fields then restores IntuneSettings.json defaults
        // ══════════════════════════════════════════════════════════════
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowThemedConfirm("New", "❓", "blue",
                    "This will clear all fields and start a new app, restoring default settings from IntuneSettings.json. Continue?"))
                return;

            ClearAllTabFields();

            // ── Header ───────────────────────────────────────────────
            TxtIntunewinPath.Text = "";

            // ── Restore defaults from IntuneSettings.json ───────────
            // Reset guard so LoadIntuneDefaults runs fresh and re-applies
            // all default values defined in the JSON (Category, Commands,
            // Detection rules, Assignments, etc.) without user overrides.
            _defaultsLoaded = false;
            LoadIntuneDefaults();
        }

        // ── Clears every data tab (App Info → Reports) plus the Log. ────
        // Does NOT touch the .intunewin path or restore IntuneSettings.json
        // defaults - callers decide those. Shared by New (BtnClear) and the
        // "companion JSON not found" branch in TxtIntunewinPath_TextChanged.
        private void ClearAllTabFields()
        {
            // ── Tab 1: App Info ──────────────────────────────────────
            TxtAppName.Text = "";
            TxtDescription.Text = "";
            TxtPublisher.Text = "";
            TxtAppVersion.Text = "";
            CmbCategory.SelectedIndex = 0;
            if (CmbFeatured != null) CmbFeatured.SelectedIndex = 0;
            if (TxtDeveloper != null) TxtDeveloper.Text = "";
            if (TxtOwner != null) TxtOwner.Text = "";
            if (TxtIconPath != null) TxtIconPath.Text = "";

            // ── Tab 2: Program ───────────────────────────────────────
            if (TxtInstallCmd != null)   TxtInstallCmd.Text   = "";
            if (TxtUninstallCmd != null) TxtUninstallCmd.Text = "";
            if (TxtInstallTime != null)  TxtInstallTime.Text  = "";
            RbUninstallNo.IsChecked   = true;
            RbInstallSystem.IsChecked = true;
            CmbRestartBehavior.SelectedIndex = 0;
            InitDefaultReturnCodes();

            // ── Tab 3: Requirements ──────────────────────────────────
            ChkX64.IsChecked = true;
            ChkX86.IsChecked = false;
            ChkArm64.IsChecked = false;
            CmbMinOs.SelectedIndex = 0;

            // ── Tab 4: Detection ─────────────────────────────────────
            _detectionRules.Clear();
            DetectionRulesList.Children.Clear();
            RbDetectRules.IsChecked = true;
            TxtDetectScriptPath.Text = "";
            RebuildDetectionRuleUI();   // show the three fixed cards (all unchecked)

            // ── Tab 7: Assignments ───────────────────────────────────
            AvailableList.Children.Clear();
            AvailableList.Children.Add(new TextBlock { Text = "No groups added.", FontSize = 11, Foreground = TC("TextMutedBrush"), Margin = new Thickness(8, 6, 8, 6) });

            // ── Tab 8: Reports ───────────────────────────────────────
            _reportApp = null;
            _deviceReport = null;
            _userReport = null;
            _reportKind = "device";
            _reportError = "";
            _reportCells.Clear();
            _searchMatches.Clear();
            _searchIdx = -1;
            if (TxtReportSearch != null) TxtReportSearch.Text = "";
            if (TxtReportSearchCount != null) TxtReportSearchCount.Text = "";
            TxtReportSummary.Text = "";
            TxtReportSummary.Visibility = Visibility.Collapsed;
            ReportRowsPanel.Children.Clear();
            var noReport = new TextBlock { Text = "No report loaded. Choose Device install status or User install status.", FontSize = 11, Margin = new Thickness(12, 10, 12, 10) };
            noReport.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            ReportRowsPanel.Children.Add(noReport);

            // ── Tab 8: Log ───────────────────────────────────────────
            ClearLog();
        }

        // ── Generic field clear for Publish & Deploy textboxes ──────────
        private void FieldClear_PD_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Parent is Grid g)
                foreach (UIElement child in g.Children)
                    if (child is TextBox tb) { tb.Clear(); tb.Focus(); return; }
        }

        private void BtnClearIntunewinPath_Click(object sender, RoutedEventArgs e)
        {
            if (TxtIntunewinPath != null) TxtIntunewinPath.Clear();
        }

        // ══════════════════════════════════════════════════════════════
        // SAVE - update companion <n>.json with current tab data
        // ══════════════════════════════════════════════════════════════
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtIntunewinPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowThemedMessage("Save", "⚠", "amber", "Please select a valid .intunewin file first.");
                return;
            }

            // ── Mandatory-field check before writing the companion JSON ──
            var missing = GetMissingMandatoryFields();
            if (missing.Count > 0)
            {
                ShowThemedMessage("Save", "⚠", "amber",
                    "Please fill in the required fields before saving:\n\n" +
                    string.Join("\n", missing.Select(m => "•  " + m)));
                return;
            }

            string outputFolder = Path.GetDirectoryName(path) ?? "";
            WriteIntuneAppSettingsJson(outputFolder);

            string jsonName = Path.GetFileNameWithoutExtension(path) + ".json";
            ShowThemedMessage("Saved", "ℹ", "blue", $"Settings saved to {Path.Combine(outputFolder, jsonName)}");
        }

        // ══════════════════════════════════════════════════════════════
        // MANDATORY FIELDS - shared by the Save button and the debounced
        // auto-save. Returns the list of required fields that are still
        // empty/unset; an empty list means the app is complete enough to
        // write its companion JSON.
        // ══════════════════════════════════════════════════════════════
        private List<string> GetMissingMandatoryFields()
        {
            var missing = new List<string>();

            // App Info
            if (string.IsNullOrWhiteSpace(TxtAppName?.Text))     missing.Add("App Info - Name");
            if (string.IsNullOrWhiteSpace(TxtDescription?.Text)) missing.Add("App Info - Description");
            if (string.IsNullOrWhiteSpace(TxtPublisher?.Text))   missing.Add("App Info - Publisher");

            // Program
            if (string.IsNullOrWhiteSpace(TxtInstallCmd?.Text))   missing.Add("Program - Install command");
            if (string.IsNullOrWhiteSpace(TxtUninstallCmd?.Text)) missing.Add("Program - Uninstall command");

            // Requirements
            bool anyArch = ChkX64?.IsChecked == true
                        || ChkX86?.IsChecked == true
                        || ChkArm64?.IsChecked == true;
            if (!anyArch)                       missing.Add("Requirements - operating system architecture (select at least one)");
            if (CmbMinOs?.SelectedItem == null) missing.Add("Requirements - minimum operating system");

            // Detection - custom script needs a path; manual rules need at least one type checked
            bool detectionOk = RbDetectScript?.IsChecked == true
                ? !string.IsNullOrWhiteSpace(TxtDetectScriptPath?.Text)
                : _detectionRules.Any(r => r.Enabled);
            if (!detectionOk) missing.Add("Detection - select at least one rule type (MSI, File or Registry) under manually configure detection rules or Use a custom detection script");

            return missing;
        }

        // ══════════════════════════════════════════════════════════════
        // CLOSE - same behaviour as Close button in Create Package
        // ══════════════════════════════════════════════════════════════
        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Application.Current.MainWindow?.Close();

        // ══════════════════════════════════════════════════════════════
        // AUTO-FILL - called by MainWindow when navigating here
        // ══════════════════════════════════════════════════════════════
        public void AutoFillFromPackage(
            string? scriptPath,
            string? mainFilePath,
            string? appName,
            string? appVendor,
            string? appVersion,
            string? intunewinPath,
            bool forceInstalledRefresh = false)
        {
            _scriptPath    = scriptPath;
            _mainFilePath  = mainFilePath;
            _appName       = appName;
            _appVendor     = appVendor;
            _appVersion    = appVersion;
            _intunewinPath = intunewinPath;
            _autoSaveSuspended = true;
            try
            {

            // ── Detect package change ─────────────────────────────────────────────
            // Trigger a full reset whenever ANY of the three identifiers changes.
            // Do NOT require non-empty: an empty new value (package not yet built) is
            // still a different state from a non-empty previous value.
            string newMainFile  = mainFilePath  ?? "";
            string newIntunewin = intunewinPath ?? "";
            string newAppName   = appName       ?? "";

            bool needsReset = !string.Equals(_lastMainFilePath,  newMainFile,   StringComparison.OrdinalIgnoreCase)
                           || !string.Equals(_lastIntunewinPath, newIntunewin,  StringComparison.OrdinalIgnoreCase)
                           || !string.Equals(_lastAppName,       newAppName,    StringComparison.OrdinalIgnoreCase);

            if (needsReset)
            {
                // Track the new package identity (always update, even if empty)
                _lastMainFilePath  = newMainFile;
                _lastIntunewinPath = newIntunewin;
                _lastAppName       = newAppName;

                // ── Full field reset - wipe all user data from previous app ────────
                // Tab 1
                TxtAppName.Text    = "";
                TxtPublisher.Text  = "";
                TxtAppVersion.Text = "";
                TxtDescription.Text = "";
                if (TxtDeveloper != null) TxtDeveloper.Text = "";
                if (TxtOwner     != null) TxtOwner.Text     = "";
                if (TxtIconPath  != null) TxtIconPath.Text  = "";

                // Tab 2
                if (TxtInstallCmd   != null) TxtInstallCmd.Text   = "";
                if (TxtUninstallCmd != null) TxtUninstallCmd.Text = "";
                if (TxtInstallTime  != null) TxtInstallTime.Text  = "";
                RbInstallSystem.IsChecked = true;
                RbUninstallNo.IsChecked   = true;

                // Tab 4 - reset detection rules to the three fixed (unchecked) cards
                _detectionRules.Clear();
                DetectionRulesList.Children.Clear();
                RebuildDetectionRuleUI();

                // Tab 7 - clear assignments
                AvailableList.Children.Clear();
                AvailableList.Children.Add(new TextBlock { Text = "No groups added.", FontSize = 11, Foreground = TC("TextMutedBrush"), Margin = new Thickness(8, 6, 8, 6) });

                // Reset guard so LoadIntuneDefaults re-populates JSON defaults
                _defaultsLoaded = false;
            }

            // ── Tab 1: App Info - always overwrite (even if reset didn't run) ──────
            // Use direct assignment instead of null-guard so stale values are cleared.
            TxtAppName.Text    = appName    ?? "";
            TxtPublisher.Text  = appVendor  ?? "";
            TxtAppVersion.Text = appVersion ?? "";

            // Auto-populate intunewin path
            if (!string.IsNullOrWhiteSpace(intunewinPath))
            {
                _suppressPathChanged = true;
                TxtIntunewinPath.Text = intunewinPath;
                _suppressPathChanged = false;
            }

            // ── Load defaults from IntuneSettings.json ───────────────
            LoadIntuneDefaults();

            // ── Tab 2: Program - Install / Uninstall command come straight from IntuneSettings.json
            // (set by LoadIntuneDefaults above). They are intentionally NOT overridden with a
            // script-derived command, so the boxes show exactly what the settings file holds.

            // ── Tab 1: Logo - auto-fill from <packageRoot>\Documents\Icon.png when the Execute step
            // created it (and the user has not already chosen a logo).
            AutoPopulateLogoFromDocuments(scriptPath, intunewinPath);

            // ── Tab 4: Detection - auto-detect from MSI ─────────────────
            // Only run when main file is MSI and product code not yet populated.
            if (!string.IsNullOrWhiteSpace(mainFilePath) &&
                mainFilePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(mainFilePath))
            {
                // .msi source → populate MSI (primary) + File + Registry rule values, but tick each
                // rule only if the user selected it in Settings (do not force all three on).
                var existingMsi = EnsureRule("MSI");
                existingMsi.Enabled = _settingsWantMsi;

                if (forceInstalledRefresh || string.IsNullOrWhiteSpace(existingMsi.MsiProductCode))
                {
                    var msiProps = ReadMsiProperties(mainFilePath);

                    // MSI rule
                    if (!string.IsNullOrWhiteSpace(msiProps.ProductCode))
                    {
                        existingMsi.MsiProductCode = msiProps.ProductCode;
                        existingMsi.MsiVersion     = appVersion ?? "";
                    }

                    // Try live registry first (more accurate than MSI file)
                    var installed = ReadInstalledAppInfo(msiProps.ProductCode);

                    // File rule - prefer registry InstallLocation > MSI ARPINSTALLLOCATION
                    var existingFile = EnsureRule("File");
                    existingFile.Enabled = _settingsWantFile;
                    if (forceInstalledRefresh ||
                        (string.IsNullOrWhiteSpace(existingFile.FilePath) &&
                         string.IsNullOrWhiteSpace(existingFile.FileName)))
                    {
                        string instLoc = !string.IsNullOrWhiteSpace(installed.InstallLocation)
                            ? installed.InstallLocation : msiProps.InstallDir;
                        string exeName = !string.IsNullOrWhiteSpace(installed.MainExeName)
                            ? installed.MainExeName : msiProps.MainExe;

                        if (!string.IsNullOrWhiteSpace(instLoc))  existingFile.FilePath = instLoc;
                        if (!string.IsNullOrWhiteSpace(exeName))  existingFile.FileName = exeName;

                        // Remember the installed main-file full path so the value box can be
                        // auto-filled for whichever Detection method is selected (version/size/date).
                        string fullExe = !string.IsNullOrWhiteSpace(installed.MainExePath)
                            ? installed.MainExePath
                            : (!string.IsNullOrWhiteSpace(instLoc) && !string.IsNullOrWhiteSpace(exeName)
                                ? Path.Combine(instLoc, exeName) : "");
                        if (!string.IsNullOrWhiteSpace(fullExe)) existingFile.FileFullPath = fullExe;

                        // Default method to String (version) and fill from the installed file.
                        if (string.IsNullOrWhiteSpace(existingFile.FileCheck))
                            existingFile.FileCheck = "String (version)";
                        if (string.IsNullOrWhiteSpace(existingFile.FileOperator))
                            existingFile.FileOperator = "Equal to";
                        string fileAuto = ComputeFileDetectionValue(existingFile.FileFullPath, existingFile.FileCheck);
                        existingFile.FileValue = !string.IsNullOrWhiteSpace(fileAuto)
                            ? fileAuto
                            : (!string.IsNullOrWhiteSpace(installed.MainExeVersion) ? installed.MainExeVersion : existingFile.FileValue);
                        // File32Bit is controlled by IntuneSettings.json
                        // ("Check"→true, "Uncheck"→false). Do not override with registry Is32Bit.
                    }

                    // Registry rule - use WOW6432Node path when 32-bit; populate version data
                    var existingReg = EnsureRule("Registry");
                    existingReg.Enabled = _settingsWantReg;
                    if (string.IsNullOrWhiteSpace(existingReg.RegKeyPath) &&
                        !string.IsNullOrWhiteSpace(msiProps.ProductCode))
                    {
                        existingReg.RegKeyPath  = !string.IsNullOrWhiteSpace(installed.RegKeyPath)
                            ? installed.RegKeyPath
                            : @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + msiProps.ProductCode;
                        existingReg.RegValue    = "DisplayVersion";
                        existingReg.RegCheck    = "Version comparison";
                        if (string.IsNullOrWhiteSpace(existingReg.RegOperator)) existingReg.RegOperator = "Equal to";
                        existingReg.RegValueData = !string.IsNullOrWhiteSpace(installed.DisplayVersion)
                            ? installed.DisplayVersion : (appVersion ?? "");
                        // Reg32Bit is controlled by IntuneSettings.json. Do not override.
                    }

                    RebuildDetectionRuleUI();
                }
            }

            // ── Tab 4: Detection - auto-detect from EXE installer ────────────
            // Runs when the main file is NOT an MSI (e.g. FileCompare Setup .exe).
            // Skips MSI rule; populates File + Registry rules by searching
            // the uninstall hive by DisplayName.
            bool isExeInstaller = !string.IsNullOrWhiteSpace(mainFilePath) &&
                !mainFilePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            if (isExeInstaller && !string.IsNullOrWhiteSpace(appName))
            {
                // .exe source → populate File + Registry rule values, but tick each rule only if the
                // user selected it in Settings (do not force them on).
                var existingFile = EnsureRule("File");
                var existingReg  = EnsureRule("Registry");
                existingFile.Enabled = _settingsWantFile;
                existingReg.Enabled  = _settingsWantReg;

                bool fileEmpty = forceInstalledRefresh ||
                                 (string.IsNullOrWhiteSpace(existingFile.FilePath) &&
                                  string.IsNullOrWhiteSpace(existingFile.FileName));
                bool regEmpty  = forceInstalledRefresh ||
                                 string.IsNullOrWhiteSpace(existingReg.RegKeyPath);

                if (fileEmpty || regEmpty)
                {
                    var installed = ReadInstalledAppByName(appName, appVendor ?? "");

                    if (fileEmpty && existingFile != null)
                    {
                        if (!string.IsNullOrWhiteSpace(installed.InstallLocation))
                            existingFile.FilePath = installed.InstallLocation;
                        if (!string.IsNullOrWhiteSpace(installed.MainExeName))
                            existingFile.FileName = installed.MainExeName;

                        // Remember the installed main-file full path for value auto-fill.
                        string fullExe = !string.IsNullOrWhiteSpace(installed.MainExePath)
                            ? installed.MainExePath
                            : (!string.IsNullOrWhiteSpace(installed.InstallLocation) && !string.IsNullOrWhiteSpace(installed.MainExeName)
                                ? Path.Combine(installed.InstallLocation, installed.MainExeName) : "");
                        if (!string.IsNullOrWhiteSpace(fullExe)) existingFile.FileFullPath = fullExe;

                        if (string.IsNullOrWhiteSpace(existingFile.FileCheck))
                            existingFile.FileCheck = "String (version)";
                        if (string.IsNullOrWhiteSpace(existingFile.FileOperator))
                            existingFile.FileOperator = "Equal to";
                        string fileAuto = ComputeFileDetectionValue(existingFile.FileFullPath, existingFile.FileCheck);
                        existingFile.FileValue = !string.IsNullOrWhiteSpace(fileAuto)
                            ? fileAuto
                            : (!string.IsNullOrWhiteSpace(installed.MainExeVersion) ? installed.MainExeVersion : existingFile.FileValue);
                    }

                    if (regEmpty && existingReg != null &&
                        !string.IsNullOrWhiteSpace(installed.RegKeyPath))
                    {
                        existingReg.RegKeyPath   = installed.RegKeyPath;
                        existingReg.RegValue     = "DisplayVersion";
                        existingReg.RegCheck     = "Version comparison";
                        if (string.IsNullOrWhiteSpace(existingReg.RegOperator)) existingReg.RegOperator  = "Equal to";
                        existingReg.RegValueData = !string.IsNullOrWhiteSpace(installed.DisplayVersion)
                            ? installed.DisplayVersion : (appVersion ?? "");
                    }

                    if (!string.IsNullOrWhiteSpace(installed.RegKeyPath) ||
                        !string.IsNullOrWhiteSpace(installed.InstallLocation))
                        RebuildDetectionRuleUI();
                }
            }

            // ── Installed-app refresh (Execute Package priority) ─────────────
            // When the app was just installed on this machine (forceInstalledRefresh) but we
            // had no usable main file above (e.g. user ran Execute Package directly), fill the
            // File/Registry/MSI rules straight from the live registry - by MSI product code if
            // the MSI rule already has one, otherwise by App name/vendor.
            if (forceInstalledRefresh)
            {
                var msiR  = EnsureRule("MSI");
                var fileR = EnsureRule("File");
                var regR  = EnsureRule("Registry");

                // Resolve installed info: product code first, then name/vendor.
                InstalledAppInfo info = default!;
                bool haveInfo = false;
                if (!string.IsNullOrWhiteSpace(msiR.MsiProductCode))
                {
                    info = ReadInstalledAppInfo(msiR.MsiProductCode);
                    haveInfo = !string.IsNullOrWhiteSpace(info.InstallLocation)
                            || !string.IsNullOrWhiteSpace(info.RegKeyPath)
                            || !string.IsNullOrWhiteSpace(info.DisplayVersion);
                }
                if (!haveInfo && !string.IsNullOrWhiteSpace(appName))
                {
                    info = ReadInstalledAppByName(appName, appVendor ?? "");
                    haveInfo = !string.IsNullOrWhiteSpace(info.InstallLocation)
                            || !string.IsNullOrWhiteSpace(info.RegKeyPath)
                            || !string.IsNullOrWhiteSpace(info.DisplayVersion);
                }

                if (haveInfo)
                {
                    // File rule
                    fileR.Enabled = _settingsWantFile;
                    if (!string.IsNullOrWhiteSpace(info.InstallLocation)) fileR.FilePath = info.InstallLocation;
                    if (!string.IsNullOrWhiteSpace(info.MainExeName))     fileR.FileName = info.MainExeName;
                    string fullExe = !string.IsNullOrWhiteSpace(info.MainExePath)
                        ? info.MainExePath
                        : (!string.IsNullOrWhiteSpace(info.InstallLocation) && !string.IsNullOrWhiteSpace(info.MainExeName)
                            ? Path.Combine(info.InstallLocation, info.MainExeName) : "");
                    if (!string.IsNullOrWhiteSpace(fullExe)) fileR.FileFullPath = fullExe;
                    if (string.IsNullOrWhiteSpace(fileR.FileCheck))    fileR.FileCheck = "String (version)";
                    if (string.IsNullOrWhiteSpace(fileR.FileOperator)) fileR.FileOperator = "Equal to";
                    string fAuto = ComputeFileDetectionValue(fileR.FileFullPath, fileR.FileCheck);
                    fileR.FileValue = !string.IsNullOrWhiteSpace(fAuto)
                        ? fAuto
                        : (!string.IsNullOrWhiteSpace(info.MainExeVersion) ? info.MainExeVersion : fileR.FileValue);

                    // Registry rule
                    regR.Enabled = _settingsWantReg;
                    if (!string.IsNullOrWhiteSpace(info.RegKeyPath)) regR.RegKeyPath = info.RegKeyPath;
                    if (string.IsNullOrWhiteSpace(regR.RegValue))    regR.RegValue = "DisplayVersion";
                    if (string.IsNullOrWhiteSpace(regR.RegCheck))    regR.RegCheck = "Version comparison";
                    if (string.IsNullOrWhiteSpace(regR.RegOperator)) regR.RegOperator = "Equal to";
                    if (!string.IsNullOrWhiteSpace(info.DisplayVersion)) regR.RegValueData = info.DisplayVersion;

                    // MSI rule - fill version if we have a product code, but tick only if selected.
                    if (!string.IsNullOrWhiteSpace(msiR.MsiProductCode))
                    {
                        msiR.Enabled = _settingsWantMsi;
                        if (string.IsNullOrWhiteSpace(msiR.MsiVersion) && !string.IsNullOrWhiteSpace(info.DisplayVersion))
                            msiR.MsiVersion = info.DisplayVersion;
                    }

                    RebuildDetectionRuleUI();
                }
            }

            // Config.psd1 - cache PSADT log folder path
            string cfgLog = ReadLogPathFromConfigPsd1(scriptPath);
            if (!string.IsNullOrWhiteSpace(cfgLog))
                _psadtLogFolder = cfgLog;

            UpdatePublishButton();
            }
            finally { _autoSaveSuspended = false; }
            RequestAutoSave();   // persist the freshly auto-filled data once
        }

        // ══════════════════════════════════════════════════════════════
        // PREFILL FILE RULE FROM INSTALLED APP  (Execute Package flow)
        // ══════════════════════════════════════════════════════════════
        // LIVE DETECTION SUMMARY  (for the Create Document page)
        // Formats the CURRENT state of the Detection tab - the enabled
        // MSI / File / Registry rule cards (or the detection script) -
        // into the same multi-line text block the Create Document page
        // shows in its Detection Rules box. Reads the in-memory
        // _detectionRules, so it reflects whatever is on screen RIGHT NOW,
        // even before the per-app json has been saved (e.g. straight after
        // the post-install auto-prefill from Execute Package, exit code 0).
        // Returns "" when nothing is enabled / populated.
        // ══════════════════════════════════════════════════════════════
        public string BuildDetectionSummary()
        {
            try
            {
                // Script mode wins outright, mirroring the per-app json reader.
                if (RbDetectScript?.IsChecked == true)
                {
                    string sp = TxtDetectScriptPath?.Text?.Trim() ?? "";
                    return string.IsNullOrWhiteSpace(sp) ? "" : "Detection script: " + sp;
                }

                DetectionRule? msiR  = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("MSI",      StringComparison.OrdinalIgnoreCase));
                DetectionRule? fileR = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("File",     StringComparison.OrdinalIgnoreCase));
                DetectionRule? regR  = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("Registry", StringComparison.OrdinalIgnoreCase));

                var sb = new System.Text.StringBuilder();

                // ── MSI ──
                if (msiR?.Enabled == true && !string.IsNullOrWhiteSpace(msiR.MsiProductCode))
                {
                    sb.AppendLine("MSI");
                    sb.AppendLine("  Product code : " + msiR.MsiProductCode.Trim());
                    bool verOn = msiR.MsiCheck.Equals("Version comparison", StringComparison.OrdinalIgnoreCase)
                              || msiR.MsiCheck.Equals("Check version",      StringComparison.OrdinalIgnoreCase);
                    if (verOn && !string.IsNullOrWhiteSpace(msiR.MsiVersion))
                        sb.AppendLine("  Version      : " +
                            (string.IsNullOrWhiteSpace(msiR.MsiOperator) ? "" : msiR.MsiOperator.Trim() + " ") +
                            msiR.MsiVersion.Trim());
                }

                // ── File ──
                if (fileR?.Enabled == true &&
                    (!string.IsNullOrWhiteSpace(fileR.FilePath) || !string.IsNullOrWhiteSpace(fileR.FileName)))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("File");
                    if (!string.IsNullOrWhiteSpace(fileR.FilePath)) sb.AppendLine("  Path          : " + fileR.FilePath.Trim());
                    if (!string.IsNullOrWhiteSpace(fileR.FileName)) sb.AppendLine("  File or folder: " + fileR.FileName.Trim());
                    string fMethod = fileR.FileCheck ?? "";
                    if (!string.IsNullOrWhiteSpace(fMethod)) sb.AppendLine("  Method        : " + fMethod.Trim());
                    bool fNeedsVal = !string.IsNullOrWhiteSpace(fMethod)
                        && !fMethod.Equals("File or folder exists", StringComparison.OrdinalIgnoreCase);
                    if (fNeedsVal && !string.IsNullOrWhiteSpace(fileR.FileValue))
                        sb.AppendLine("  Value         : " +
                            (string.IsNullOrWhiteSpace(fileR.FileOperator) ? "" : fileR.FileOperator.Trim() + " ") +
                            fileR.FileValue.Trim() +
                            (string.IsNullOrWhiteSpace(fileR.FileTimeValue) ? "" : " " + fileR.FileTimeValue.Trim()));
                    if (fileR.File32Bit) sb.AppendLine("  32-bit app on 64-bit clients");
                }

                // ── Registry ──
                if (regR?.Enabled == true && !string.IsNullOrWhiteSpace(regR.RegKeyPath))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("Registry");
                    sb.AppendLine("  Key path      : " + regR.RegKeyPath.Trim());
                    if (!string.IsNullOrWhiteSpace(regR.RegValue)) sb.AppendLine("  Value name    : " + regR.RegValue.Trim());
                    string rMethod = regR.RegCheck ?? "";
                    if (!string.IsNullOrWhiteSpace(rMethod)) sb.AppendLine("  Method        : " + rMethod.Trim());
                    bool rNeedsVal = !string.IsNullOrWhiteSpace(rMethod)
                        && !rMethod.Equals("Key exists",           StringComparison.OrdinalIgnoreCase)
                        && !rMethod.Equals("Key does not exist",   StringComparison.OrdinalIgnoreCase)
                        && !rMethod.Equals("Value exists",         StringComparison.OrdinalIgnoreCase)
                        && !rMethod.Equals("Value does not exist", StringComparison.OrdinalIgnoreCase);
                    if (rNeedsVal && !string.IsNullOrWhiteSpace(regR.RegValueData))
                        sb.AppendLine("  Value         : " +
                            (string.IsNullOrWhiteSpace(regR.RegOperator) ? "" : regR.RegOperator.Trim() + " ") +
                            regR.RegValueData.Trim());
                    if (regR.Reg32Bit) sb.AppendLine("  32-bit app on 64-bit clients");
                }

                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }

        // ══════════════════════════════════════════════════════════════
        // LIVE SNAPSHOT FOR THE CREATE DOCUMENT PAGE
        // Returns the publish-page values that overlap with Create Document
        // fields, plus the formatted detection summary. Consumed by MainWindow's
        // LiveDataChanged subscription to mirror manual edits immediately.
        // ══════════════════════════════════════════════════════════════
        public (string AppName, string Publisher, string Version, string Category,
                string Description, string InstallCmd, string UninstallCmd,
                string InstallBehaviour, string DetectionSummary) GetLiveDocumentFields()
        {
            string T(TextBox? tb) => tb?.Text?.Trim() ?? "";
            return (T(TxtAppName), T(TxtPublisher), T(TxtAppVersion),
                    CmbCategory?.Text?.Trim() ?? "",
                    T(TxtDescription), T(TxtInstallCmd), T(TxtUninstallCmd),
                    RbInstallUser?.IsChecked == true ? "User" : "System",
                    BuildDetectionSummary());
        }

        // Called by MainWindow ~2 s after a successful Execute Package install
        // (exit code 0). Mirrors the AutoPackage prefillOnly pattern: populates
        // the File rule's Path / File-or-folder / value (+ time for Date methods)
        // for the CURRENTLY selected detection method, straight from the live
        // registry / file system of the now-installed app.
        // Pure UI/memory update - does NOT write the per-app json and does NOT
        // create the intunewin (auto-save stays suspended; no save is requested).
        // ══════════════════════════════════════════════════════════════
        public void PrefillFileRuleFromInstalledApp(string? appName = null, string? appVendor = null)
        {
            _autoSaveSuspended = true;
            try
            {
                var msiR  = EnsureRule("MSI");
                var fileR = EnsureRule("File");

                // Cascading identity: explicit args → values captured by AutoFillFromPackage →
                // whatever is currently on the App Info tab.
                string name   = !string.IsNullOrWhiteSpace(appName)   ? appName
                              : !string.IsNullOrWhiteSpace(_appName)  ? _appName!
                              : TxtAppName.Text.Trim();
                string vendor = !string.IsNullOrWhiteSpace(appVendor)  ? appVendor
                              : !string.IsNullOrWhiteSpace(_appVendor) ? _appVendor!
                              : TxtPublisher.Text.Trim();

                // Locate the installed app: MSI product code first, then name/vendor.
                InstalledAppInfo info = default!;
                bool haveInfo = false;
                if (!string.IsNullOrWhiteSpace(msiR.MsiProductCode))
                {
                    info = ReadInstalledAppInfo(msiR.MsiProductCode);
                    haveInfo = !string.IsNullOrWhiteSpace(info.InstallLocation)
                            || !string.IsNullOrWhiteSpace(info.MainExePath);
                }
                if (!haveInfo && !string.IsNullOrWhiteSpace(name))
                {
                    info = ReadInstalledAppByName(name, vendor ?? "");
                    haveInfo = !string.IsNullOrWhiteSpace(info.InstallLocation)
                            || !string.IsNullOrWhiteSpace(info.MainExePath);
                }
                if (!haveInfo) return;   // app not found in registry - nothing to fill

                // ── File rule: Path / File-or-folder / full path ──
                // Populate the values but tick the File rule only if it was selected in Settings.
                fileR.Enabled = _settingsWantFile;
                if (!string.IsNullOrWhiteSpace(info.InstallLocation)) fileR.FilePath = info.InstallLocation;
                if (!string.IsNullOrWhiteSpace(info.MainExeName))     fileR.FileName = info.MainExeName;
                string fullExe = !string.IsNullOrWhiteSpace(info.MainExePath)
                    ? info.MainExePath
                    : (!string.IsNullOrWhiteSpace(info.InstallLocation) && !string.IsNullOrWhiteSpace(info.MainExeName)
                        ? Path.Combine(info.InstallLocation, info.MainExeName) : "");
                if (!string.IsNullOrWhiteSpace(fullExe)) fileR.FileFullPath = fullExe;

                if (string.IsNullOrWhiteSpace(fileR.FileCheck))    fileR.FileCheck    = "String (version)";
                if (string.IsNullOrWhiteSpace(fileR.FileOperator)) fileR.FileOperator = "Equal to";

                // ── Value for the CURRENTLY selected detection method ──
                string v = ComputeFileDetectionValue(fileR.FileFullPath, fileR.FileCheck);
                if (!string.IsNullOrWhiteSpace(v))
                    fileR.FileValue = v;
                else if (!string.IsNullOrWhiteSpace(info.MainExeVersion) &&
                         fileR.FileCheck.Equals("String (version)", StringComparison.OrdinalIgnoreCase))
                    fileR.FileValue = info.MainExeVersion;

                // ── Time-of-day for Date created/modified ──
                if (fileR.FileCheck.StartsWith("Date", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(fileR.FileFullPath) && File.Exists(fileR.FileFullPath))
                {
                    try
                    {
                        DateTime ts = fileR.FileCheck.Equals("Date created", StringComparison.OrdinalIgnoreCase)
                            ? File.GetCreationTime(fileR.FileFullPath)
                            : File.GetLastWriteTime(fileR.FileFullPath);
                        fileR.FileTimeValue = ts.ToString("hh:mm:ss tt",
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch { }
                }

                RebuildDetectionRuleUI();   // refresh the card textboxes with the new values
            }
            catch { /* prefill is best-effort - never interrupt the install flow */ }
            finally { _autoSaveSuspended = false; }
            // Intentionally NO RequestAutoSave(): this stage must not write the per-app json.
        }

        // ══════════════════════════════════════════════════════════════
        // MSI PROPERTY READER  (Windows Installer COM)
        // ══════════════════════════════════════════════════════════════
        private record MsiProperties(string ProductCode, string InstallDir, string MainExe);

        private static string ReadMsiProductCode(string msiPath)
            => ReadMsiProperties(msiPath).ProductCode;

        private static MsiProperties ReadMsiProperties(string msiPath)
        {
            object? inst = null, db = null;
            string productCode = "", installDir = "", mainExe = "";
            try
            {
                Type t = Type.GetTypeFromProgID("WindowsInstaller.Installer")!;
                inst   = Activator.CreateInstance(t)!;
                db     = t.InvokeMember("OpenDatabase", BindingFlags.InvokeMethod, null, inst,
                             new object[] { msiPath, 0 });
                var dt = db!.GetType();

                string QueryFirst(string sql)
                {
                    object? vw2 = null;
                    try
                    {
                        vw2 = dt.InvokeMember("OpenView", BindingFlags.InvokeMethod, null, db,
                                  new object[] { sql });
                        var vt2 = vw2!.GetType();
                        vt2.InvokeMember("Execute", BindingFlags.InvokeMethod, null, vw2, null);
                        object? rec2 = vt2.InvokeMember("Fetch", BindingFlags.InvokeMethod, null, vw2, null);
                        if (rec2 != null)
                        {
                            string val = rec2.GetType().InvokeMember("StringData",
                                             BindingFlags.GetProperty, null, rec2,
                                             new object[] { 2 }) as string ?? "";
                            Marshal.ReleaseComObject(rec2);
                            return val.Trim();
                        }
                    }
                    catch { }
                    finally { if (vw2 != null) try { Marshal.ReleaseComObject(vw2); } catch { } }
                    return "";
                }

                productCode = QueryFirst("SELECT Property,Value FROM Property WHERE Property='ProductCode'");
                installDir  = QueryFirst("SELECT Property,Value FROM Property WHERE Property='ARPINSTALLLOCATION'");
                if (string.IsNullOrWhiteSpace(installDir))
                    installDir = QueryFirst("SELECT Property,Value FROM Property WHERE Property='INSTALLDIR'");

                // Find the main exe in the MSI File table
                object? vwF = null;
                try
                {
                    vwF = dt.InvokeMember("OpenView", BindingFlags.InvokeMethod, null, db,
                              new object[] { "SELECT FileName FROM File WHERE FileName LIKE '%.exe'" });
                    var vtF = vwF!.GetType();
                    vtF.InvokeMember("Execute", BindingFlags.InvokeMethod, null, vwF, null);
                    string bestExe = "";
                    object? recF;
                    while ((recF = vtF.InvokeMember("Fetch", BindingFlags.InvokeMethod, null, vwF, null)) != null)
                    {
                        string raw = recF.GetType().InvokeMember("StringData",
                                         BindingFlags.GetProperty, null, recF,
                                         new object[] { 1 }) as string ?? "";
                        Marshal.ReleaseComObject(recF);
                        // MSI stores short|long name - take the long name after '|'
                        string longName = raw.Contains('|') ? raw.Split('|')[1] : raw;
                        if (!string.IsNullOrWhiteSpace(longName)) bestExe = longName;
                    }
                    mainExe = bestExe;
                }
                catch { }
                finally { if (vwF != null) try { Marshal.ReleaseComObject(vwF); } catch { } }
            }
            catch { }
            finally
            {
                if (db   != null) try { Marshal.ReleaseComObject(db);   } catch { }
                if (inst != null) try { Marshal.ReleaseComObject(inst); } catch { }
            }
            return new MsiProperties(productCode, installDir, mainExe);
        }

        // ══════════════════════════════════════════════════════════════
        // CONFIG.PSD1 LOG PATH READER
        // ══════════════════════════════════════════════════════════════
        private static string ReadLogPathFromConfigPsd1(string? scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath)) return "";
            try
            {
                string? scriptDir = Path.GetDirectoryName(scriptPath);
                if (scriptDir == null) return "";
                string configPath = Path.Combine(scriptDir, "Config", "config.psd1");
                if (!File.Exists(configPath)) return "";
                string content = File.ReadAllText(configPath);
                var m = System.Text.RegularExpressions.Regex.Match(content,
                    @"LogPath\s*=\s*['""]([^'""]+)['""]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) return "";
                string raw = m.Groups[1].Value.Trim();
                // Expand common PowerShell variable aliases
                raw = System.Text.RegularExpressions.Regex.Replace(raw,
                    @"\$envSystemDrive", @"C:",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                raw = System.Text.RegularExpressions.Regex.Replace(raw,
                    @"\$env:SystemDrive", @"C:",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return raw.TrimEnd('\\');
            }
            catch { return ""; }
        }

        // ══════════════════════════════════════════════════════════════
        // INSTALLED APP INFO FROM REGISTRY
        // ══════════════════════════════════════════════════════════════
        private record InstalledAppInfo(
            string RegKeyPath, string InstallLocation, string DisplayVersion,
            string MainExePath, string MainExeName, string MainExeVersion, bool Is32Bit);

        private static InstalledAppInfo ReadInstalledAppInfo(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return new InstalledAppInfo("","","","","","",false);
            string pc = productCode.Trim();
            if (!pc.StartsWith("{")) pc = "{" + pc;
            if (!pc.EndsWith("}"))   pc += "}";
            string s64 = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + pc;
            string s32 = "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + pc;
            foreach (string sub in new[] { s64, s32 })
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sub);
                    if (key == null) continue;
                    bool is32bit = sub.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase);
                    string regKey   = "HKEY_LOCAL_MACHINE\\" + sub;
                    string instLoc  = (key.GetValue("InstallLocation") as string ?? "").TrimEnd(Path.DirectorySeparatorChar);
                    string dispVer  = key.GetValue("DisplayVersion") as string ?? "";
                    string dispIcon = key.GetValue("DisplayIcon")    as string ?? "";
                    if (string.IsNullOrWhiteSpace(instLoc) && !string.IsNullOrWhiteSpace(dispIcon))
                    {
                        // DisplayIcon may point to an exe, dll or ico - use its containing
                        // folder regardless of file type (e.g. FileCompare points to filecompare.dll).
                        string ip = dispIcon.Split(',')[0].Trim().Trim('"');
                        string dir = "";
                        try { dir = Path.GetDirectoryName(ip) ?? ""; } catch { }
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                            instLoc = dir.TrimEnd(Path.DirectorySeparatorChar);
                    }
                    // Last fallback: search standard Program Files folders by DisplayName
                    if (string.IsNullOrWhiteSpace(instLoc))
                    {
                        string dispName = key.GetValue("DisplayName") as string ?? "";
                        if (!string.IsNullOrWhiteSpace(dispName))
                        {
                            string[] roots = {
                                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                            };
                            foreach (string root in roots)
                            {
                                // Try exact display name match, then partial match
                                string candidate = Path.Combine(root, dispName);
                                if (Directory.Exists(candidate))
                                { instLoc = candidate; break; }
                                // Try subdirectories whose name contains the display name
                                if (Directory.Exists(root))
                                {
                                    // Match a folder when: the folder name contains the display
                                    // name, the display name contains the folder name, or both
                                    // share the first token (e.g. DisplayName "FileCompare 1.0
                                    // (x64 edition)" -> folder "FileCompare").
                                    string firstTok = dispName
                                        .Split(new[] { ' ', '(', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                        .FirstOrDefault() ?? dispName;
                                    var match = Directory.GetDirectories(root).FirstOrDefault(d =>
                                    {
                                        string fn = Path.GetFileName(d);
                                        return fn.Contains(dispName, StringComparison.OrdinalIgnoreCase)
                                            || dispName.Contains(fn, StringComparison.OrdinalIgnoreCase)
                                            || (firstTok.Length >= 3 && fn.Contains(firstTok, StringComparison.OrdinalIgnoreCase));
                                    });
                                    if (match != null) { instLoc = match; break; }
                                }
                            }
                        }
                    }
                    string exePath = "", exeName = "", exeVer = "";
                    if (!string.IsNullOrWhiteSpace(dispIcon))
                    {
                        string ie = dispIcon.Split(',')[0].Trim().Trim('"');
                        if (ie.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(ie))
                        { exePath = ie; exeName = Path.GetFileName(ie); }
                    }
                    if (string.IsNullOrWhiteSpace(exePath) && Directory.Exists(instLoc))
                    {
                        var exes = Directory.GetFiles(instLoc, "*.exe", SearchOption.TopDirectoryOnly);
                        if (exes.Length > 0)
                        {
                            string best = exes.OrderByDescending(e => new FileInfo(e).Length).First();
                            exePath = best; exeName = Path.GetFileName(best);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                    {
                        try
                        {
                            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                            exeVer = (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                            ? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}"
                            : fvi.FileVersion?.Trim() ?? fvi.ProductVersion?.Trim() ?? "";
                        }
                        catch { }
                    }
                    return new InstalledAppInfo(regKey, instLoc, dispVer, exePath, exeName, exeVer, is32bit);
                }
                catch { }
            }
            return new InstalledAppInfo("","","","","","",false);
        }

        // ══════════════════════════════════════════════════════════════
        // FIND INSTALLED APP BY NAME (for .exe installers without product code)
        // Searches all uninstall keys for a DisplayName match.
        // ══════════════════════════════════════════════════════════════
        private static InstalledAppInfo ReadInstalledAppByName(string appName, string appVendor)
        {
            if (string.IsNullOrWhiteSpace(appName))
                return new InstalledAppInfo("","","","","","",false);

            string[] hivePaths =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (string hivePath in hivePaths)
            {
                try
                {
                    using var hive = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(hivePath);
                    if (hive == null) continue;
                    bool is32 = hivePath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase);

                    // Collect all candidates and score them
                    InstalledAppInfo? bestMatch = null;
                    int bestScore = 0;

                    foreach (string subName in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = hive.OpenSubKey(subName);
                            if (sub == null) continue;
                            string dispName = sub.GetValue("DisplayName") as string ?? "";
                            if (string.IsNullOrWhiteSpace(dispName)) continue;

                            // Score: exact match = 3, appName contains dispName = 2, dispName contains appName = 1
                            int score = 0;
                            if (string.Equals(dispName, appName, StringComparison.OrdinalIgnoreCase))
                                score = 3;
                            else if (appName.Contains(dispName, StringComparison.OrdinalIgnoreCase))
                                score = 2;
                            else if (dispName.Contains(appName, StringComparison.OrdinalIgnoreCase))
                                score = 1;
                            // Boost score if publisher also matches
                            if (score > 0 && !string.IsNullOrWhiteSpace(appVendor))
                            {
                                string pub = sub.GetValue("Publisher") as string ?? "";
                                if (!string.IsNullOrWhiteSpace(pub) &&
                                    (pub.Contains(appVendor, StringComparison.OrdinalIgnoreCase) ||
                                     appVendor.Contains(pub, StringComparison.OrdinalIgnoreCase)))
                                    score += 1;
                            }
                            if (score == 0) continue;

                            string regKey   = "HKEY_LOCAL_MACHINE\\" + hivePath + "\\" + subName;
                            string instLoc  = (sub.GetValue("InstallLocation") as string ?? "").TrimEnd(Path.DirectorySeparatorChar);
                            string dispVer  = sub.GetValue("DisplayVersion") as string ?? "";
                            string dispIcon = sub.GetValue("DisplayIcon")    as string ?? "";

                            // Derive install folder from DisplayIcon
                            if (string.IsNullOrWhiteSpace(instLoc) && !string.IsNullOrWhiteSpace(dispIcon))
                            {
                                string ip = dispIcon.Split(',')[0].Trim().Trim('"');
                                if (ip.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                    instLoc = (Path.GetDirectoryName(ip) ?? "").TrimEnd(Path.DirectorySeparatorChar);
                            }
                            // Fallback: search program files by display name
                            if (string.IsNullOrWhiteSpace(instLoc))
                            {
                                string[] roots = {
                                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                };
                                foreach (string root in roots)
                                {
                                    string c1 = Path.Combine(root, dispName);
                                    if (Directory.Exists(c1)) { instLoc = c1; break; }
                                    string c2 = Path.Combine(root, appName);
                                    if (Directory.Exists(c2)) { instLoc = c2; break; }
                                    if (Directory.Exists(root))
                                    {
                                        var m = Directory.GetDirectories(root)
                                            .FirstOrDefault(d => Path.GetFileName(d).Contains(
                                                appName, StringComparison.OrdinalIgnoreCase));
                                        if (m != null) { instLoc = m; break; }
                                    }
                                }
                            }

                            string exePath = "", exeName = "", exeVer = "";
                            // Prefer exe from DisplayIcon
                            if (!string.IsNullOrWhiteSpace(dispIcon))
                            {
                                string ie = dispIcon.Split(',')[0].Trim().Trim('"');
                                if (ie.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(ie))
                                { exePath = ie; exeName = Path.GetFileName(ie); }
                            }
                            // Fallback: largest exe in install folder
                            if (string.IsNullOrWhiteSpace(exePath) && Directory.Exists(instLoc))
                            {
                                var exes = Directory.GetFiles(instLoc, "*.exe", SearchOption.TopDirectoryOnly);
                                if (exes.Length > 0)
                                {
                                    string best = exes.OrderByDescending(e => new FileInfo(e).Length).First();
                                    exePath = best; exeName = Path.GetFileName(best);
                                }
                            }
                            // Read file version
                            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                            {
                                try
                                {
                                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                                    exeVer = (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                                    ? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}"
                                    : fvi.FileVersion?.Trim() ?? fvi.ProductVersion?.Trim() ?? "";
                                }
                                catch { }
                            }

                            var candidate = new InstalledAppInfo(regKey, instLoc, dispVer, exePath, exeName, exeVer, is32);
                            if (score > bestScore) { bestScore = score; bestMatch = candidate; }
                        }
                        catch { }
                    }
                    if (bestMatch != null && bestScore >= 2) return bestMatch;
                    if (bestMatch != null && bestScore == 1 && string.IsNullOrWhiteSpace(bestMatch.InstallLocation) == false)
                        return bestMatch;
                }
                catch { }
            }
            return new InstalledAppInfo("","","","","","",false);
        }

        // ══════════════════════════════════════════════════════════════
        // TAB NAVIGATION
        // ══════════════════════════════════════════════════════════════
        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tagStr) return;
            if (!int.TryParse(tagStr, out int tab)) return;
            ShowTab(tab);
        }

        private void ShowTab(int tab)
        {
            _activeTab = tab;

            Tab1.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
            Tab2.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
            Tab3.Visibility = tab == 3 ? Visibility.Visible : Visibility.Collapsed;
            Tab4.Visibility = tab == 4 ? Visibility.Visible : Visibility.Collapsed;
            Tab5.Visibility = tab == 5 ? Visibility.Visible : Visibility.Collapsed;
            Tab6.Visibility = tab == 6 ? Visibility.Visible : Visibility.Collapsed;
            Tab7.Visibility = tab == 7 ? Visibility.Visible : Visibility.Collapsed;
            Tab8.Visibility = tab == 8 ? Visibility.Visible : Visibility.Collapsed;
            Tab9.Visibility = tab == 9 ? Visibility.Visible : Visibility.Collapsed;

            // Update tab button styles.
            //
            // Same mechanism as the Auto Package sub-nav: the pill fill lives on the Border
            // "indicator" and the text colour on the TextBlocks inside the Button. The old numbered
            // buttons swapped a whole Style; these carry their look in the markup, so only the two
            // colours change.
            //
            // Media types are fully qualified: this file has no `using System.Windows.Media` and
            // spells out System.Windows.Media.SolidColorBrush elsewhere (see TC).
            var accentBrush     = (System.Windows.Media.Brush)(FindResource("AccentBlueBrush")
                                   ?? System.Windows.Media.Brushes.DodgerBlue);
            var clearBrush      = (System.Windows.Media.Brush)System.Windows.Media.Brushes.Transparent;
            var activeTextBrush = (System.Windows.Media.Brush)System.Windows.Media.Brushes.White;
            var mutedBrush      = (System.Windows.Media.Brush)(FindResource("TextSecondaryBrush")
                                   ?? System.Windows.Media.Brushes.Gray);

            var indicators = new[] { Tab1Indicator, Tab2Indicator, Tab3Indicator, Tab4Indicator,
                                     Tab5Indicator, Tab6Indicator, Tab7Indicator, Tab8Indicator,
                                     Tab9Indicator };
            var buttons    = new[] { BtnTab1, BtnTab2, BtnTab3, BtnTab4, BtnTab5,
                                     BtnTab6, BtnTab7, BtnTab8, BtnTab9 };

            for (int i = 0; i < indicators.Length; i++)
            {
                bool isActive = (i + 1) == tab;
                if (indicators[i] != null)
                    indicators[i].Background = isActive ? accentBrush : clearBrush;

                // Only the LABEL takes the active colour. The leading glyph keeps its own colour so
                // the emoji icons stay legible on the blue pill, exactly as in Auto Package.
                if (buttons[i]?.Content is StackPanel sp)
                {
                    var labels = sp.Children.OfType<TextBlock>().ToList();
                    if (labels.Count > 0)
                        labels[^1].Foreground = isActive ? activeTextBrush : mutedBrush;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // SIGN IN - Microsoft Graph device code flow
        // ══════════════════════════════════════════════════════════════
        private async void BtnSignIn_Click(object sender, RoutedEventArgs e)
        {
            if (_isSignedIn)
            {
                SignOut();
                return;
            }

            GraphSettings? settings = LoadGraphSettings();
            if (settings == null)
            {
                ShowThemedMessage("Settings Required", "⚠", "amber", $"Settings file not found at:\n{SettingsPath}\n\n" +
                    "Please create this file with the following content:\n" +
                    "{\n" +
                    "  \"ClientId\": \"<your-app-client-id>\",\n" +
                    "  \"TenantId\": \"<your-tenant-id>\",\n" +
                    "  \"DefaultCategory\": \"Productivity\",\n" +
                    "  \"FeaturedApp\": \"No\",\n" +
                    "  \"Developer\": \"\",\n" +
                    "  \"Owner\": \"\"\n" +
                    "}");
                return;
            }

            BtnSignIn.IsEnabled = false;
            TxtAuthStatus.Text  = "Starting device code flow…";

            try
            {
                await AuthenticateDeviceCodeFlow(settings);
            }
            catch (Exception ex)
            {
                TxtAuthStatus.Text = $"Sign-in failed: {ex.Message}";
                TxtAuthSub.Text    = "Please try again.";
            }
            finally
            {
                BtnSignIn.IsEnabled = true;
            }
        }

        private void SignOut()
        {
            _isSignedIn  = false;
            _accessToken = "";
            _tenantName  = "";
            BtnSignIn.Content          = "🔑  Sign In";
            TxtAuthStatus.Text         = "Not signed in - Microsoft Graph connection required";
            TxtAuthSub.Text            = "Sign in with Azure AD to publish apps to Intune";
            PanelTenantInfo.Visibility = Visibility.Collapsed;
            UpdatePublishButton();
        }

        private async Task AuthenticateDeviceCodeFlow(GraphSettings settings)
        {
            // Step 1: Request device code
            string deviceCodeUrl = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/devicecode";
            string scope = "https://graph.microsoft.com/DeviceManagementApps.ReadWrite.All offline_access";

            using var http = new System.Net.Http.HttpClient();
            var dcResp = await http.PostAsync(deviceCodeUrl,
                new System.Net.Http.FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId,
                    ["scope"]     = scope
                }));

            string dcJson = await dcResp.Content.ReadAsStringAsync();
            using var dcDoc = JsonDocument.Parse(dcJson);
            var dcRoot = dcDoc.RootElement;

            if (!dcRoot.TryGetProperty("user_code",       out var userCodeEl)  ||
                !dcRoot.TryGetProperty("device_code",     out var deviceCodeEl)||
                !dcRoot.TryGetProperty("verification_uri", out var uriEl))
            {
                string err = dcRoot.TryGetProperty("error_description", out var ed)
                           ? ed.GetString() ?? "Unknown error" : dcJson;
                throw new InvalidOperationException(err);
            }

            string userCode   = userCodeEl.GetString()   ?? "";
            string deviceCode = deviceCodeEl.GetString() ?? "";
            string verifyUri  = uriEl.GetString()         ?? "https://microsoft.com/devicelogin";
            int    interval   = dcRoot.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;

            // Show instructions to user
            Dispatcher.Invoke(() =>
            {
                TxtAuthStatus.Text = $"Open browser and enter code:  {userCode}";
                TxtAuthSub.Text    = $"{verifyUri}";
            });

            // Try to open browser automatically
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                  { FileName = verifyUri, UseShellExecute = true }); } catch { }

            // Step 2: Poll for token
            string tokenUrl = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token";
            DateTime deadline = DateTime.UtcNow.AddMinutes(10);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval * 1000);

                var tokenResp = await http.PostAsync(tokenUrl,
                    new System.Net.Http.FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"]   = "urn:ietf:params:oauth2:grant-type:device_code",
                        ["client_id"]    = settings.ClientId,
                        ["device_code"]  = deviceCode
                    }));

                string tokenJson = await tokenResp.Content.ReadAsStringAsync();
                using var tDoc   = JsonDocument.Parse(tokenJson);
                var tRoot        = tDoc.RootElement;

                if (tRoot.TryGetProperty("access_token", out var at))
                {
                    _accessToken = at.GetString() ?? "";
                    _isSignedIn  = true;

                    // Fetch tenant display name
                    _tenantName = await FetchTenantName(http, _accessToken);

                    Dispatcher.Invoke(() =>
                    {
                        TxtAuthStatus.Text         = $"Signed in - {_tenantName}";
                        TxtAuthSub.Text            = "Connected to Microsoft Graph · DeviceManagementApps.ReadWrite.All";
                        TxtTenantName.Text         = _tenantName;
                        PanelTenantInfo.Visibility = Visibility.Visible;
                        BtnSignIn.Content          = "🔓  Sign Out";
                        UpdatePublishButton();
                    });
                    return;
                }

                // Check for authorization_pending (keep polling) vs real errors
                if (tRoot.TryGetProperty("error", out var errEl))
                {
                    string err = errEl.GetString() ?? "";
                    if (err == "authorization_pending" || err == "slow_down") continue;
                    string errDesc = tRoot.TryGetProperty("error_description", out var ed)
                                   ? ed.GetString() ?? err : err;
                    throw new InvalidOperationException(errDesc);
                }
            }

            throw new TimeoutException("Sign-in timed out. Please try again.");
        }

        private static async Task<string> FetchTenantName(
            System.Net.Http.HttpClient http, string token)
        {
            try
            {
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var r = await http.GetAsync("https://graph.microsoft.com/v1.0/organization");
                if (!r.IsSuccessStatusCode) return "";
                string json = await r.Content.ReadAsStringAsync();
                using var doc  = JsonDocument.Parse(json);
                var value = doc.RootElement.GetProperty("value");
                if (value.GetArrayLength() > 0 &&
                    value[0].TryGetProperty("displayName", out var dn))
                    return dn.GetString() ?? "";
            }
            catch { }
            return "";
        }

        // ── Logo auto-population from the package's Documents\Icon.png ──
        // The Execute step writes Icon.png under <packageRoot>\Documents. When a package is loaded
        // here, surface that path in the Logo box automatically, unless the user already set one.
        private void AutoPopulateLogoFromDocuments(string? scriptPath, string? intunewinPath)
        {
            try
            {
                if (TxtIconPath == null || !string.IsNullOrWhiteSpace(TxtIconPath.Text)) return;
                string pkgRoot = ResolvePackageRoot(scriptPath, intunewinPath);
                if (pkgRoot.Length == 0) return;
                string icon = Path.Combine(pkgRoot, "Documents", "Icon.png");
                if (File.Exists(icon)) TxtIconPath.Text = icon;
            }
            catch { }
        }

        // Resolves <packageRoot> from either the PSADT script (<root>\Package\Invoke-AppDeployToolkit.ps1)
        // or the intunewin (<root>\Intune\*.intunewin) - both sit one level under the package root.
        private static string ResolvePackageRoot(string? scriptPath, string? intunewinPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(scriptPath))
                {
                    string? sub  = Directory.GetParent(scriptPath!)?.FullName;      // ...\Package
                    string? root = sub != null ? Directory.GetParent(sub)?.FullName : null;
                    if (!string.IsNullOrEmpty(root)) return root!;
                }
                if (!string.IsNullOrWhiteSpace(intunewinPath))
                {
                    string? sub  = Directory.GetParent(intunewinPath!)?.FullName;   // ...\Intune
                    string? root = sub != null ? Directory.GetParent(sub)?.FullName : null;
                    if (!string.IsNullOrEmpty(root)) return root!;
                }
            }
            catch { }
            return "";
        }

        // ══════════════════════════════════════════════════════════════
        // LOAD DEFAULTS FROM IntuneSettings.json
        // ══════════════════════════════════════════════════════════════
        private void LoadIntuneDefaults()
        {
            // Run only once - prevents duplicate rules/assignments on re-navigation
            if (_defaultsLoaded) return;
            _defaultsLoaded = true;
            _autoSaveSuspended = true;

            // ── Helper: select a ComboBox item by its content string ──────────────
            static void SelectCombo(ComboBox cmb, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                foreach (ComboBoxItem item in cmb.Items)
                    if (string.Equals(item.Content?.ToString(), value,
                                      StringComparison.OrdinalIgnoreCase))
                    { cmb.SelectedItem = item; return; }
            }

            if (!File.Exists(SettingsPath)) return;
            try
            {
                string json = File.ReadAllText(SettingsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string G(string key) =>
                    root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString() ?? "" : "";
                bool HasVal(string key) => !string.IsNullOrWhiteSpace(G(key));
                bool Yes(string key) => G(key).Equals("Yes", StringComparison.OrdinalIgnoreCase);

                // The template uses the CANONICAL FLAT schema (same keys as the AutoPackage
                // per-app JSON). App-specific keys (IntuneAppName, version, Logo, IntunewinFilePath,
                // detection values, etc.) are normally blank in the template and are only applied
                // here when present; policy-style defaults (category, operators, OS, restart, etc.)
                // are applied as the starting point for a new package.

                // ── TAB 1: App Info ──────────────────────────────────────────────
                if (HasVal("IntuneAppName"))   TxtAppName.Text   = G("IntuneAppName");
                if (HasVal("IntuneAppVendor")) TxtPublisher.Text = G("IntuneAppVendor");
                if (HasVal("IntuneAppVersion"))TxtAppVersion.Text= G("IntuneAppVersion");
                if (HasVal("Description"))     TxtDescription.Text = G("Description");
                if (HasVal("IntuneCategory"))  SelectCombo(CmbCategory, G("IntuneCategory"));
                if (HasVal("FeaturedApp"))     SelectCombo(CmbFeatured, G("FeaturedApp"));
                if (HasVal("Developer"))       TxtDeveloper.Text = G("Developer");
                if (HasVal("Owner") && string.IsNullOrWhiteSpace(TxtOwner.Text))
                    TxtOwner.Text = G("Owner");
                if (HasVal("InformationURL"))  TxtInfoUrl.Text    = G("InformationURL");
                if (HasVal("PrivacyURL"))      TxtPrivacyUrl.Text = G("PrivacyURL");
                if (HasVal("Notes"))           TxtNotes.Text      = G("Notes");
                if (HasVal("Logo"))            TxtIconPath.Text   = G("Logo");

                // ── TAB 2: Program ───────────────────────────────────────────────
                if (HasVal("InstallCommand"))   TxtInstallCmd.Text   = G("InstallCommand");
                if (HasVal("UninstallCommand")) TxtUninstallCmd.Text = G("UninstallCommand");
                if (HasVal("InstallationTime")) TxtInstallTime.Text  = G("InstallationTime");
                if (HasVal("AllowAvailableUninstall"))
                {
                    bool allow = Yes("AllowAvailableUninstall");
                    RbUninstallYes.IsChecked = allow;
                    RbUninstallNo.IsChecked  = !allow;
                }
                if (HasVal("InstallBehavior"))
                {
                    bool user = G("InstallBehavior").Equals("User", StringComparison.OrdinalIgnoreCase);
                    RbInstallUser.IsChecked   = user;
                    RbInstallSystem.IsChecked = !user;
                }
                if (HasVal("DeviceRestartBehaviour"))
                    SelectCombo(CmbRestartBehavior, G("DeviceRestartBehaviour"));

                // ── TAB 3: Requirements ──────────────────────────────────────────
                if (HasVal("OperatingSystemArchitecture"))
                {
                    string arch = G("OperatingSystemArchitecture");
                    ChkX86.IsChecked   = arch.Contains("32",  StringComparison.OrdinalIgnoreCase);
                    ChkX64.IsChecked   = arch.Contains("64",  StringComparison.OrdinalIgnoreCase);
                    ChkArm64.IsChecked = arch.Contains("ARM", StringComparison.OrdinalIgnoreCase);
                }
                if (HasVal("MinimumOperatingSystem"))
                    SelectCombo(CmbMinOs, G("MinimumOperatingSystem"));

                // ── TAB 4: Detection - set the three persistent rules from the flat flags ──
                _detectionRules.Clear();
                var dMsi  = EnsureRule("MSI");
                var dFile = EnsureRule("File");
                var dReg  = EnsureRule("Registry");
                bool useScript = G("RulesFormat").IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0;
                RbDetectRules.IsChecked  = !useScript;
                RbDetectScript.IsChecked = useScript;
                if (useScript)
                {
                    if (HasVal("DetectionScriptFile")) TxtDetectScriptPath.Text = G("DetectionScriptFile");
                    if (RbSigYes != null)      RbSigYes.IsChecked      = Yes("DetectionScriptEnforceSignatureCheck");
                    if (RbSigNo  != null)      RbSigNo.IsChecked       = !Yes("DetectionScriptEnforceSignatureCheck");
                    if (RbScript32Yes != null) RbScript32Yes.IsChecked = Yes("DetectionScriptRunAs32Bit");
                    if (RbScript32No  != null) RbScript32No.IsChecked  = !Yes("DetectionScriptRunAs32Bit");
                }

                dMsi.Enabled        = Yes("DetectionTypeMSI");
                dMsi.MsiProductCode = G("DetectionMsiProductCode");
                dMsi.MsiCheck       = Yes("DetectionMsiVersionCheck") ? "Version comparison" : "Not configured";
                dMsi.MsiOperator    = G("DetectionMsiOperator");
                dMsi.MsiVersion     = G("DetectionMsiValue");

                // Remember the rule-type selection so the auto-populate path (which fills detail
                // values from the installed app) enables each rule from the user's choice rather
                // than ticking all three.
                _settingsWantMsi  = Yes("DetectionTypeMSI");
                _settingsWantFile = Yes("DetectionTypeFile");
                _settingsWantReg  = Yes("DetectionTypeRegistry");

                dFile.Enabled      = Yes("DetectionTypeFile");
                dFile.FilePath     = G("DetectionFileFolderPath");
                dFile.FileName     = G("DetectionFileFileName");
                dFile.FileCheck    = G("DetectionFileDetectionMethod");
                dFile.FileOperator = G("DetectionFileOperator");
                dFile.FileValue    = dFile.FileCheck.Equals("Size in MB", StringComparison.OrdinalIgnoreCase) ? G("DetectionFileSizeValue")
                                   : dFile.FileCheck.StartsWith("Date", StringComparison.OrdinalIgnoreCase)    ? G("DetectionFileDateValue")
                                   : G("DetectionFileValue");
                dFile.FileTimeValue = G("DetectionFileTimeValue");
                dFile.File32Bit    = Yes("DetectionFile32Bit");

                dReg.Enabled      = Yes("DetectionTypeRegistry");
                dReg.RegKeyPath   = G("DetectionRegKeyPath");
                dReg.RegValue     = G("DetectionRegValueName");
                dReg.RegCheck     = G("DetectionRegDetectionMethod");
                dReg.RegOperator  = G("DetectionRegOperator");
                dReg.RegValueData = G("DetectionRegistryValue");
                dReg.Reg32Bit     = Yes("DetectionReg32Bit");

                RebuildDetectionRuleUI();

                // ── TAB 5/6: Dependencies & Supersedence ──
                // Not loaded from the template. Dependencies and supersedences are per-app
                // and dynamic (any count); they live only in the per-app JSON under
                // DependencyAppName{n}/DependencyAppID{n}/... and are added on the
                // Dependencies / Supersedence tabs, so the shared template no longer carries them.

                // ── TAB 7: Assignments (semicolon- or comma-separated group names) ──
                static System.Collections.Generic.IEnumerable<string> SplitGroups(string raw)
                    => (raw ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(x => x.Trim()).Where(x => x.Length > 0);

                foreach (var grp in SplitGroups(G("AssignmentsAvailable")))
                    AddAssignmentRow(AvailableList, "Available", fullCols: true, fixedName: grp);
            }
            catch { }
            finally { _autoSaveSuspended = false; }
        }

        // ══════════════════════════════════════════════════════════════
        // SETTINGS LOADER
        // ══════════════════════════════════════════════════════════════
        private static GraphSettings? LoadGraphSettings()
        {
            if (!File.Exists(SettingsPath)) return null;
            try
            {
                string json = File.ReadAllText(SettingsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("ClientId", out var cid) &&
                    root.TryGetProperty("TenantId", out var tid))
                    return new GraphSettings(
                        cid.GetString() ?? "",
                        tid.GetString() ?? "");
            }
            catch { }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        // AUTO-FILL BUTTON
        // ══════════════════════════════════════════════════════════════
        private void BtnAutoFill_Click(object sender, RoutedEventArgs e)
        {
            if (_scriptPath != null || _appName != null)
            {
                AutoFillFromPackage(_scriptPath, _mainFilePath,
                    _appName, _appVendor, _appVersion, _intunewinPath);
                ShowThemedMessage("Auto-fill", "ℹ", "blue", "Fields auto-filled from the current package.");
            }
            else
            {
                ShowThemedMessage("Auto-fill", "⚠", "amber", "No package data available. Please create a package first.");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // BROWSE BUTTONS
        // ══════════════════════════════════════════════════════════════
        private void BtnBrowseIntunewin_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select .intunewin file",
                Filter = "Intunewin packages (*.intunewin)|*.intunewin|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _suppressPathChanged = true;
                TxtIntunewinPath.Text = dlg.FileName;
                _suppressPathChanged = false;
                TxtIntunewinPath_TextChanged(this, null!);
            }
        }

        private void BtnBrowseIcon_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select icon PNG or JPG",
                Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true) TxtIconPath.Text = dlg.FileName;
        }

        private void PathBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                      ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        // ── Page-level drag & drop - accepts .intunewin dropped anywhere ──
        private void PageGrid_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            bool hasIntunewin = files?.Any(f =>
                f.EndsWith(".intunewin", StringComparison.OrdinalIgnoreCase)) == true;
            e.Effects = hasIntunewin ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void PageGrid_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            string? intunewin = files.FirstOrDefault(f =>
                f.EndsWith(".intunewin", StringComparison.OrdinalIgnoreCase));
            if (intunewin != null)
            {
                _suppressPathChanged = true;
                TxtIntunewinPath.Text = intunewin;
                _suppressPathChanged = false;
                TxtIntunewinPath_TextChanged(this, null!);
                UpdatePublishButton();
            }
        }

        private void IntunewinPath_Drop(object sender, DragEventArgs e)
        {
            // Drop is handled by PageGrid_Drop - mark handled to prevent double-fire
            e.Handled = true;
        }

        private void IconPath_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0) TxtIconPath.Text = files[0];
        }

        // ══════════════════════════════════════════════════════════════
        // REQUIREMENTS TAB
        // ══════════════════════════════════════════════════════════════
        // REQUIREMENTS TAB - Additional requirement rules
        // ══════════════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════════════
        // DETECTION RULES TAB
        // ══════════════════════════════════════════════════════════════
        private void DetectMethod_Changed(object sender, RoutedEventArgs e)
        {
            if (PanelDetectRules == null || PanelDetectScript == null) return;
            bool rules = RbDetectRules.IsChecked == true;
            PanelDetectRules.Visibility  = rules ? Visibility.Visible  : Visibility.Collapsed;
            PanelDetectScript.Visibility = rules ? Visibility.Collapsed : Visibility.Visible;
        }

        // Browse for custom detection script file
        private void BtnBrowseDetectScript_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Select Detection Script",
                Filter = "PowerShell scripts (*.ps1)|*.ps1|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
                TxtDetectScriptPath.Text = dlg.FileName;
        }

        private void BtnAddDetectionRule_Click(object sender, RoutedEventArgs e)
        {
            // Remove placeholder text on first add
            var placeholder = DetectionRulesList.Children.OfType<TextBlock>().FirstOrDefault();
            if (placeholder != null) DetectionRulesList.Children.Remove(placeholder);

            // Show rule-type picker dialog
            ShowAddDetectionRuleDialog();
        }

        // Themed validation dialog for missing mandatory fields - matches this page's other dialogs.
        // ── Shared dialog in the Create / Validate Package style (screenshot 5) ──────
        // Dark title bar with the Publish Package icon, a labelled "Status" value box for
        // the message, and a large status icon beside the action buttons. Returns true
        // when an affirmative button is clicked.
        private bool ShowSvDialog(string title, string status, string bottomIcon, string style,
                                  (string Text, bool Affirmative)[] buttons, string? headerText = null)
        {
            // Header defaults to the "Publish Package - <title>" branding; callers can pass a
            // standalone header instead (used by the Intune sign-in / sign-out dialogs).
            string hdr = headerText ?? ("Publish Package - " + title);
            bool result = false;

            var winBg     = TC("NavBarBgBrush");
            var borderClr = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));

            var win = new Window
            {
                Title = hdr,
                Width = 440, MaxWidth = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false,
                Topmost = true,
            };
            // Bring it to the front even when the app is not the active window (e.g. the user was
            // in the browser), so an Intune sign-in result is never hidden behind other windows.
            win.Loaded += (_, __) => { try { win.Activate(); } catch { } };

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

            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            System.Windows.Media.Brush titleTxt = System.Windows.Media.Brushes.White;
            System.Windows.Media.Brush titleSec = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            var tb = new Border
            {
                Background = titleBarBg,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = TC("BorderMainBrush"),
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 6, 12, 6),
            };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);

            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ti  = new TextBlock { Text = "🚀", FontSize = 14, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt  = new TextBlock { Text = hdr, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var tcl = new Button   { Content = "✕", FontSize = 11, Foreground = titleSec, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => { result = false; win.Close(); };

            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg;
            og.Children.Add(tb);

            var cg = new Grid { Margin = new Thickness(8), Background = System.Windows.Media.Brushes.Transparent };
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cg, 1);
            og.Children.Add(cg);

            var boxBg  = TC("WindowBgBrush");
            var boxBdr = TC("BorderMainBrush");
            var lblFg  = TC("TextPrimaryBrush");

            var iconColor = style switch
            {
                "amber" => System.Windows.Media.Color.FromRgb(0xE6, 0x51, 0x00),
                "red"   => System.Windows.Media.Color.FromRgb(0xE5, 0x35, 0x35),
                _       => System.Windows.Media.Color.FromRgb(0x42, 0x9B, 0xF5),
            };

            int row = 0;
            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); row++;

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var statusLbl = new TextBlock { Text = "Status", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
            var statusBox = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6) };
            statusBox.Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320,
                Content = new TextBlock { Text = status, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = lblFg, FontFamily = new System.Windows.Media.FontFamily("Segoe UI") }
            };
            Grid.SetRow(statusLbl, row); Grid.SetColumn(statusLbl, 0);
            Grid.SetRow(statusBox, row); Grid.SetColumn(statusBox, 1);
            cg.Children.Add(statusLbl); cg.Children.Add(statusBox); row++;

            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); row++;

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIcon = new TextBlock { Text = bottomIcon, FontSize = 22, Foreground = new System.Windows.Media.SolidColorBrush(iconColor), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(rowIcon, row); Grid.SetColumn(rowIcon, 0);
            cg.Children.Add(rowIcon);

            var ug = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = buttons.Length };
            foreach (var b in buttons)
            {
                bool aff = b.Affirmative;
                var btn = DlgBtnStyled(b.Text, aff ? "blue" : "neutral", 100);
                btn.Margin = new Thickness(4, 0, 4, 0);
                btn.Click += (_, _) => { result = aff; win.Close(); };
                ug.Children.Add(btn);
            }
            Grid.SetRow(ug, row); Grid.SetColumn(ug, 1);
            cg.Children.Add(ug); row++;

            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            win.ShowDialog();
            return result;
        }

        // Themed single-OK message dialog (screenshot 5 style).
        private void ShowThemedMessage(string title, string icon, string style, string message, string? headerText = null)
            => ShowSvDialog(title, message, icon, style, new (string, bool)[] { ("OK", true) }, headerText);

        // Themed Yes/No confirmation dialog (screenshot 5 style). Returns true when confirmed.
        private bool ShowThemedConfirm(string title, string icon, string style, string message,
                                       string okText = "Yes", string cancelText = "No")
            => ShowSvDialog(title, message, "❓", style,
                            new (string, bool)[] { ("✔  " + okText, true), ("✕  " + cancelText, false) });

        // Themed validation dialog listing missing mandatory fields (screenshot 5 style).
        private void ShowMandatoryFieldsDialog(System.Collections.Generic.List<string> missing)
        {
            string status = "The following mandatory fields are not filled:\n\n• " +
                            string.Join("\n• ", missing) +
                            "\n\nPlease complete them before publishing.";
            ShowSvDialog("Mandatory fields missing", status, "⚠", "amber",
                         new (string, bool)[] { ("OK", true) });
        }

        private void ShowAddDetectionRuleDialog()
        {
            string? chosenType = null;
            var win = new Window
            {
                Width = 340, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = TC("WindowBgBrush")
            };

            var root = new Border
            {
                Background      = TC("DlgBodyBgBrush"),
                BorderBrush     = TC("DlgBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10)
            };
            root.MouseLeftButtonDown += (_, _) => win.DragMove();

            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hdr = MakeDialogHeaderThemed("📋", "Select Rule Type", "blue", () => win.Close());
            Grid.SetRow(hdr, 0);

            var body = new StackPanel { Margin = new Thickness(24, 16, 24, 8) };
            body.Children.Add(new TextBlock
            {
                Text = "Choose the detection rule type to add:",
                FontSize = 12, Foreground = TC("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 12)
            });
            var cmb = new ComboBox
            {
                Height = 28, FontSize = 12,
                Style  = (Style)Application.Current.TryFindResource("ModernComboBoxStyle")
            };
            foreach (var t in new[] { "MSI", "File", "Registry" })
                cmb.Items.Add(new ComboBoxItem { Content = t });
            cmb.SelectedIndex = 0;
            body.Children.Add(cmb);
            Grid.SetRow(body, 1);

            var ftr = new Border
            {
                Background      = TC("DlgFooterBgBrush"),
                BorderBrush     = TC("DlgBorderBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding         = new Thickness(20, 10, 20, 10)
            };
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var bCancel = DlgBtnStyled("Cancel", "neutral", 80);
            bCancel.Click += (_, _) => win.Close();
            var bOk = DlgBtnStyled("Add", "blue", 80);
            bOk.Click += (_, _) =>
            {
                chosenType = (cmb.SelectedItem as ComboBoxItem)?.Content?.ToString();
                win.Close();
            };
            btnRow.Children.Add(bCancel);
            btnRow.Children.Add(bOk);
            ftr.Child = btnRow;
            Grid.SetRow(ftr, 2);

            outer.Children.Add(hdr);
            outer.Children.Add(body);
            outer.Children.Add(ftr);
            root.Child = outer;
            win.Content = root;
            win.ShowDialog();

            if (string.IsNullOrEmpty(chosenType)) return;
            AddDetectionRule(new DetectionRule { RuleType = chosenType });
        }

        private void AddDetectionRule(DetectionRule rule)
        {
            _detectionRules.Add(rule);
            RebuildDetectionRuleUI();
        }

        // Ensures _detectionRules always contains exactly one MSI, one File and one
        // Registry rule (in that order). Existing rules are preserved; missing ones are
        // created (disabled). Any Script rule is kept aside but not shown as a card here.
        private DetectionRule EnsureRule(string type)
        {
            var r = _detectionRules.FirstOrDefault(x => x.RuleType.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (r == null)
            {
                r = new DetectionRule { RuleType = type, Enabled = false };
                _detectionRules.Add(r);
            }
            return r;
        }

        // A rule type may be TICKED only when it both was selected in Settings (rule.Enabled) AND
        // actually has values to detect on. This uncheck-when-empty guard means: an EXE source (no
        // MSI product code) never ticks MSI even if Settings selected it, and any rule left without
        // values stays unchecked. The detail fields still render either way, so the values remain
        // visible. Applied at render time so it covers every populate path uniformly.
        private static bool RuleHasValues(DetectionRule r)
        {
            if (r == null) return false;
            switch (r.RuleType)
            {
                case "MSI":
                    return !string.IsNullOrWhiteSpace(r.MsiProductCode);
                case "File":
                    return !string.IsNullOrWhiteSpace(r.FileName) || !string.IsNullOrWhiteSpace(r.FilePath);
                case "Registry":
                    return !string.IsNullOrWhiteSpace(r.RegKeyPath);
                default:
                    return true;
            }
        }

        private void RebuildDetectionRuleUI()
        {
            DetectionRulesList.Children.Clear();

            // Always present all three fixed rule types, in canonical order.
            var ordered = new[] { EnsureRule("MSI"), EnsureRule("File"), EnsureRule("Registry") };

            foreach (var rule in ordered)
            {
                var card = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(6),
                    Margin          = new Thickness(0, 0, 0, 8),
                };
                card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "BorderCardBrush");
                card.Tag = rule;

                var cardGrid = new Grid();
                // Col 0 = checkbox + rule type label (110px) | Col 1 = fields
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // ── Col 0: enable checkbox + rule type label ──
                var typeBadge = new Border
                {
                    BorderThickness   = new Thickness(0, 0, 1, 0),
                    Padding           = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background        = System.Windows.Media.Brushes.Transparent,
                };
                typeBadge.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");

                var typeStack = new StackPanel
                {
                    Orientation       = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var capturedRule = rule;
                // Enforce the uncheck-when-empty rule: a rule with no values cannot be ticked even
                // if it was selected in Settings. Correct the model too, so Save/Publish write the
                // same state the checkbox shows.
                if (rule.Enabled && !RuleHasValues(rule)) rule.Enabled = false;
                var chk = new CheckBox
                {
                    IsChecked         = rule.Enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 6, 0),
                    Cursor            = System.Windows.Input.Cursors.Hand,
                    ToolTip           = "Include this rule type in detection"
                };
                if (TryFindResource("RoundCheckBoxStyle") is Style chkStyle) chk.Style = chkStyle;

                var badgeText = new TextBlock
                {
                    Text              = rule.RuleType,
                    FontSize          = 11,
                    FontWeight        = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                badgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

                typeStack.Children.Add(chk);
                typeStack.Children.Add(badgeText);
                typeBadge.Child = typeStack;
                Grid.SetColumn(typeBadge, 0);

                // ── Col 1: rule detail fields (dimmed + disabled when unchecked) ──
                var fields = new StackPanel
                {
                    Margin     = new Thickness(16, 10, 8, 10),
                    Background = System.Windows.Media.Brushes.Transparent
                };
                Grid.SetColumn(fields, 1);

                if (rule.RuleType == "MSI")
                    BuildMsiRuleFields(fields, rule);
                else if (rule.RuleType == "File")
                    BuildFileRuleFields(fields, rule);
                else
                    BuildRegistryRuleFields(fields, rule);

                // Reflect enabled state in the UI; checkbox toggles it live.
                void ApplyEnabledState(bool on)
                {
                    capturedRule.Enabled = on;
                    fields.IsEnabled = on;
                    fields.Opacity   = on ? 1.0 : 0.45;
                }
                ApplyEnabledState(rule.Enabled);
                chk.Checked   += (_, _) => ApplyEnabledState(true);
                chk.Unchecked += (_, _) => ApplyEnabledState(false);

                cardGrid.Children.Add(typeBadge);
                cardGrid.Children.Add(fields);
                card.Child = cardGrid;
                DetectionRulesList.Children.Add(card);
            }
        }

        // ── MSI rule fields ──────────────────────────────────────────
        private void BuildMsiRuleFields(StackPanel fields, DetectionRule rule)
        {
            // MSI product code * - with browse MSI button
            fields.Children.Add(MakeInlineRowWithButton(
                "MSI product code", true, out TextBox codeBox,
                "📄", "Browse .msi file to auto-read product code",
                tb =>
                {
                    var dlg = new OpenFileDialog
                    {
                        Title  = "Select .msi file",
                        Filter = "MSI packages (*.msi)|*.msi|All files (*.*)|*.*"
                    };
                    if (dlg.ShowDialog() != true) return;
                    string code = ReadMsiProductCode(dlg.FileName);
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        tb.Text = code;
                        rule.MsiProductCode = code;
                    }
                    else
                        ShowThemedMessage("Not found", "⚠", "amber", "Could not read ProductCode from the selected MSI.");
                }));
            codeBox.Text = rule.MsiProductCode;
            codeBox.TextChanged += (_, _) => rule.MsiProductCode = codeBox.Text;

            // MSI product version check (Yes/No radio)
            var versionRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            versionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var vLbl = new TextBlock { Text = "MSI product version check", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center };
            vLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(vLbl, 0);

            var vRadios = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            bool versionCheckOn = rule.MsiCheck.Equals("Check version", StringComparison.OrdinalIgnoreCase)
                                || rule.MsiCheck.Equals("Version comparison", StringComparison.OrdinalIgnoreCase);
            var rbNo  = new RadioButton { Content = "No (default)", FontSize = 11,
                IsChecked = !versionCheckOn,
                Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 16, 0),
                GroupName = "MsiVerChk_" + _detectionRules.Count };
            rbNo.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimaryBrush");
            var rbYes = new RadioButton { Content = "Yes", FontSize = 11,
                IsChecked = versionCheckOn,
                Cursor = System.Windows.Input.Cursors.Hand,
                GroupName = "MsiVerChk_" + _detectionRules.Count };
            rbYes.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimaryBrush");
            vRadios.Children.Add(rbNo);
            vRadios.Children.Add(rbYes);
            Grid.SetColumn(vRadios, 1);

            versionRow.Children.Add(vLbl);
            versionRow.Children.Add(vRadios);
            fields.Children.Add(versionRow);

            // Operator + value (shown when Yes is selected, or pre-populated from rule)
            // Use Grid so "Operator" label aligns with labels above (200px col)
            var versionDetail = new Grid { Margin = new Thickness(0, 0, 0, 0),
                Visibility = versionCheckOn ? Visibility.Visible : Visibility.Collapsed };
            versionDetail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            versionDetail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var msiOpLabel = new TextBlock { Text = "Operator", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center };
            msiOpLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(msiOpLabel, 0);
            var msiOpContent = new StackPanel { Orientation = Orientation.Horizontal };
            var opCmb = new ComboBox { Height = 24, FontSize = 11, Width = 200, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.TryFindResource("ModernComboBoxStyle") };
            foreach (var op in new[] { "Equal to", "Not equal to", "Greater than", "Greater than or equal to", "Less than", "Less than or equal to" })
                opCmb.Items.Add(new ComboBoxItem { Content = op });
            // Apply operator from rule (default to index 3 = "Greater than or equal to")
            opCmb.SelectedIndex = 3;
            if (!string.IsNullOrWhiteSpace(rule.MsiOperator))
            {
                foreach (ComboBoxItem item in opCmb.Items)
                    if (string.Equals(item.Content?.ToString(), rule.MsiOperator,
                                      StringComparison.OrdinalIgnoreCase))
                    { opCmb.SelectedItem = item; break; }
            }
            opCmb.SelectionChanged += (_, _) =>
            {
                rule.MsiOperator = (opCmb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? rule.MsiOperator;
                RequestAutoSave();
            };
            var verTb = new TextBox { Height = 24, FontSize = 11, Width = 130,
                Padding = new Thickness(6, 0, 26, 0),
                Style = (Style)Application.Current.TryFindResource("ModernTextBoxStyle"), Tag = "e.g. 1.0.0.0" };
            if (!string.IsNullOrWhiteSpace(rule.MsiVersion)) verTb.Text = rule.MsiVersion;
            verTb.TextChanged += (_, _) => rule.MsiVersion = verTb.Text;
            var verWrap = new Grid { Width = 140 };
            var verX = new Button { Content = "✕", Width = 22, Height = 18, FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0,0,4,0), BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Clear" };
            verX.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
            verX.Click += (_, _) => { verTb.Clear(); rule.MsiVersion = ""; };
            verWrap.Children.Add(verTb); verWrap.Children.Add(verX);
            msiOpContent.Children.Add(opCmb);
            msiOpContent.Children.Add(verWrap);
            Grid.SetColumn(msiOpContent, 1);
            versionDetail.Children.Add(msiOpLabel);
            versionDetail.Children.Add(msiOpContent);
            fields.Children.Add(versionDetail);

            rbYes.Checked   += (_, _) => { versionDetail.Visibility = Visibility.Visible;   rule.MsiCheck = "Version comparison"; RequestAutoSave(); };
            rbNo.Checked    += (_, _) => { versionDetail.Visibility = Visibility.Collapsed; rule.MsiCheck = "Not configured";    RequestAutoSave(); };
        }

        // Resolves the full path of the rule's main file from FileFullPath, or from Path+FileName.
        private static string ResolveFileFullPath(DetectionRule rule)
        {
            if (!string.IsNullOrWhiteSpace(rule.FileFullPath) && File.Exists(rule.FileFullPath))
                return rule.FileFullPath;
            if (!string.IsNullOrWhiteSpace(rule.FilePath) && !string.IsNullOrWhiteSpace(rule.FileName))
            {
                try
                {
                    string combined = Path.Combine(
                        Environment.ExpandEnvironmentVariables(rule.FilePath), rule.FileName);
                    if (File.Exists(combined)) return combined;
                }
                catch { }
            }
            return "";
        }

        // Computes the detection value for the given method from a file on disk.
        // Returns "" if the file can't be read or the method needs no value.
        private static string ComputeFileDetectionValue(string fullPath, string method)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return "";
            try
            {
                switch (method)
                {
                    case "String (version)":
                    {
                        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(fullPath);
                        string ver = (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                            ? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}"
                            : fvi.FileVersion?.Trim() ?? fvi.ProductVersion?.Trim() ?? "";
                        return ver;
                    }
                    case "Size in MB":
                    {
                        long bytes = new FileInfo(fullPath).Length;
                        // Round to nearest MB (1 MB = 1024*1024 bytes)
                        long mb = (long)Math.Round(bytes / (1024.0 * 1024.0));
                        return mb.ToString();
                    }
                    case "Date modified":
                        return File.GetLastWriteTime(fullPath).ToString("yyyy-MM-dd");
                    case "Date created":
                        return File.GetCreationTime(fullPath).ToString("yyyy-MM-dd");
                    default:
                        return "";   // "File or folder exists" → no value
                }
            }
            catch { return ""; }
        }

        // Builds the UTC ISO-8601 instant (e.g. 2025-12-31T07:16:38Z) from a local date + time
        // for Date created/modified detection. Mirrors the AutoPackage converter so both features
        // emit the same DetectionFileDateUtc. Returns "" when not a date method or unparseable.
        private static string BuildDetectionFileDateUtc(string method, string dateStr, string timeStr)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(method) ||
                    !method.StartsWith("Date", StringComparison.OrdinalIgnoreCase))
                    return "";
                if (string.IsNullOrWhiteSpace(dateStr)) return "";

                string combined = (dateStr.Trim() + " " + (timeStr ?? "").Trim()).Trim();
                string[] fmts =
                {
                    "yyyy-MM-dd hh:mm:ss tt", "yyyy-MM-dd h:mm:ss tt", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
                    "MM/dd/yyyy hh:mm:ss tt", "MM/dd/yyyy h:mm:ss tt", "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy",
                    "MM-dd-yyyy hh:mm:ss tt", "MM-dd-yyyy h:mm:ss tt", "MM-dd-yyyy HH:mm:ss", "MM-dd-yyyy",
                };
                bool parsed = DateTime.TryParseExact(combined, fmts,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out DateTime localDt);
                if (!parsed && !DateTime.TryParse(combined, System.Globalization.CultureInfo.CurrentCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out localDt))
                    return "";
                DateTime utc = localDt.Kind == DateTimeKind.Utc ? localDt : localDt.ToUniversalTime();
                return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        // ── File rule fields ─────────────────────────────────────────
        private void BuildFileRuleFields(StackPanel fields, DetectionRule rule)        {
            // Path * - with folder browse button
            fields.Children.Add(MakeInlineRowWithButton(
                "Path", true, out TextBox pathBox,
                "📂", "Browse for install folder",
                tb =>
                {
                    using var fbd = new System.Windows.Forms.FolderBrowserDialog
                        { Description = "Select Install Folder", UseDescriptionForTitle = true };
                    if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    { tb.Text = fbd.SelectedPath; rule.FilePath = fbd.SelectedPath; }
                }));
            pathBox.Tag = @"e.g. %ProgramFiles%\MyApp";
            if (!string.IsNullOrWhiteSpace(rule.FilePath)) pathBox.Text = rule.FilePath;
            pathBox.TextChanged += (_, _) => rule.FilePath = pathBox.Text;

            // File or folder * - with file browse button
            fields.Children.Add(MakeInlineRowWithButton(
                "File or folder", true, out TextBox nameBox,
                "📄", "Browse for file",
                tb =>
                {
                    var dlg = new OpenFileDialog { Title = "Select File", Filter = "All files (*.*)|*.*" };
                    // Seed dialog to current path
                    string cur = rule.FilePath;
                    if (Directory.Exists(cur)) dlg.InitialDirectory = cur;
                    if (dlg.ShowDialog() == true)
                    {
                        string exePath = dlg.FileName;
                        tb.Text = Path.GetFileName(exePath);
                        rule.FileName = tb.Text;
                        // Auto-set Path if empty
                        if (string.IsNullOrWhiteSpace(pathBox.Text))
                        {
                            string dir = Path.GetDirectoryName(exePath) ?? "";
                            pathBox.Text = dir;
                            rule.FilePath = dir;
                        }
                        // Auto-read file version for String(version) detection
                        if (File.Exists(exePath))
                        {
                            try
                            {
                                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                                string ver = (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                            ? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}"
                            : fvi.FileVersion?.Trim() ?? fvi.ProductVersion?.Trim() ?? "";
                                if (!string.IsNullOrWhiteSpace(ver))
                                {
                                    rule.FileCheck    = "String (version)";
                                    rule.FileOperator = "Equal to";
                                    rule.FileValue    = ver;
                                    RebuildDetectionRuleUI();
                                }
                            }
                            catch { }
                        }
                    }
                }));
            nameBox.Tag = "e.g. myapp.exe";
            if (!string.IsNullOrWhiteSpace(rule.FileName)) nameBox.Text = rule.FileName;
            nameBox.TextChanged += (_, _) => rule.FileName = nameBox.Text;

            // Detection method *
            var dmRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            dmRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            dmRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dmLbl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var fileDmLblTb = new TextBlock { Text = "Detection method", FontSize = 11, Margin = new Thickness(0, 0, 3, 0) };
            fileDmLblTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            dmLbl.Children.Add(fileDmLblTb);
            dmLbl.Children.Add(new TextBlock { Text = "*", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0x58, 0x4F)), FontSize = 11 });
            Grid.SetColumn(dmLbl, 0);
            var dmCmb = new ComboBox { Height = 24, FontSize = 11, Style = (Style)Application.Current.TryFindResource("ModernComboBoxStyle") };
            foreach (var m in new[] { "File or folder exists", "Date modified", "Date created", "String (version)", "Size in MB" })
                dmCmb.Items.Add(new ComboBoxItem { Content = m });
            dmCmb.SelectedIndex = 0;
            // Apply FileCheck from rule
            if (!string.IsNullOrWhiteSpace(rule.FileCheck))
                foreach (ComboBoxItem ci in dmCmb.Items)
                    if (string.Equals(ci.Content?.ToString(), rule.FileCheck, StringComparison.OrdinalIgnoreCase))
                    { dmCmb.SelectedItem = ci; break; }
            Grid.SetColumn(dmCmb, 1);
            dmRow.Children.Add(dmLbl);
            dmRow.Children.Add(dmCmb);
            fields.Children.Add(dmRow);

            // Operator + value - Grid so label aligns with labels above (200px col)
            string initSel = (dmCmb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            bool initExists = initSel == "File or folder exists";
            // The row stays visible for ALL methods so the 32-bit checkbox is always shown;
            // only the operator / value / time controls hide for "File or folder exists".
            var opValueRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            opValueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            opValueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var fileOpLabel = new TextBlock { Text = "Operator", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center };
            fileOpLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(fileOpLabel, 0);
            var fileOpContent = new StackPanel { Orientation = Orientation.Horizontal };
            var fileOpCmb = new ComboBox { Height = 24, FontSize = 11, Width = 200, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.TryFindResource("ModernComboBoxStyle") };
            bool initDate = initSel.StartsWith("Date");
            foreach (var op in initDate
                ? new[] { "Equal to", "Not equal to", "More recent", "More recent or equal to", "Older", "Older or equal to" }
                : new[] { "Equal to", "Not equal to", "Greater than", "Greater than or equal to", "Less than", "Less than or equal to" })
                fileOpCmb.Items.Add(new ComboBoxItem { Content = op });
            fileOpCmb.SelectedIndex = 0;
            // Apply FileOperator from rule
            if (!string.IsNullOrWhiteSpace(rule.FileOperator))
                foreach (ComboBoxItem ci in fileOpCmb.Items)
                    if (string.Equals(ci.Content?.ToString(), rule.FileOperator, StringComparison.OrdinalIgnoreCase))
                    { fileOpCmb.SelectedItem = ci; break; }
            fileOpCmb.SelectionChanged += (_, _) =>
            {
                rule.FileOperator = (fileOpCmb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? rule.FileOperator;
                RequestAutoSave();
            };
            var fileValBox = new TextBox { Height = 24, FontSize = 11, Width = 130,
                Padding = new Thickness(6, 0, 26, 0),
                Style = (Style)Application.Current.TryFindResource("ModernTextBoxStyle"),
                Tag = initDate ? "yyyy-MM-dd" : initSel == "Size in MB" ? "e.g. 512" : "e.g. 1.0.0.0" };
            if (!string.IsNullOrWhiteSpace(rule.FileValue)) fileValBox.Text = rule.FileValue;
            fileValBox.TextChanged += (_, _) => rule.FileValue = fileValBox.Text;
            var fileValWrap = new Grid { Width = 140 };
            var fileValX = new Button { Content = "✕", Width = 22, Height = 18, FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0,0,4,0), BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Clear" };
            fileValX.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
            fileValX.Click += (_, _) => { fileValBox.Clear(); rule.FileValue = ""; };
            fileValWrap.Children.Add(fileValBox); fileValWrap.Children.Add(fileValX);

            // Time textbox - shown only for Date created/modified methods. Together with the
            // date box it produces DetectionFileTimeValue and the UTC instant (DetectionFileDateUtc).
            var fileTimeBox = new TextBox { Height = 24, FontSize = 11, Width = 110,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(6, 0, 0, 0),
                Style = (Style)Application.Current.TryFindResource("ModernTextBoxStyle"),
                Tag = "hh:mm:ss tt",
                Visibility = initDate ? Visibility.Visible : Visibility.Collapsed };
            if (!string.IsNullOrWhiteSpace(rule.FileTimeValue)) fileTimeBox.Text = rule.FileTimeValue;
            fileTimeBox.TextChanged += (_, _) => rule.FileTimeValue = fileTimeBox.Text;
            // 32-bit checkbox - inline after value textbox
            var cb32 = new CheckBox { Content = "Associated with a 32-bit app on 64-bit clients", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                IsChecked = rule.File32Bit,
                Cursor = System.Windows.Input.Cursors.Hand };
            cb32.SetResourceReference(CheckBox.ForegroundProperty, "TextPrimaryBrush");
            if (TryFindResource("RoundCheckBoxStyle") is Style cb32Style) cb32.Style = cb32Style;
            cb32.Checked   += (_, _) => { rule.File32Bit = true;  RequestAutoSave(); };
            cb32.Unchecked += (_, _) => { rule.File32Bit = false; RequestAutoSave(); };

            fileOpContent.Children.Add(fileOpCmb);
            fileOpContent.Children.Add(fileValWrap);
            fileOpContent.Children.Add(fileTimeBox);
            fileOpContent.Children.Add(cb32);
            Grid.SetColumn(fileOpContent, 1);
            opValueRow.Children.Add(fileOpLabel);
            opValueRow.Children.Add(fileOpContent);
            fields.Children.Add(opValueRow);

            // Initial visibility: for "File or folder exists" hide operator/value/time but keep
            // the row (and the 32-bit checkbox) visible.
            fileOpLabel.Visibility = initExists ? Visibility.Collapsed : Visibility.Visible;
            fileOpCmb.Visibility   = initExists ? Visibility.Collapsed : Visibility.Visible;
            fileValWrap.Visibility = initExists ? Visibility.Collapsed : Visibility.Visible;

            dmCmb.SelectionChanged += (_, _) =>
            {
                string sel = (dmCmb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                bool exists = sel == "File or folder exists";
                bool isDate = sel.StartsWith("Date");
                fileOpLabel.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
                fileOpCmb.Visibility   = exists ? Visibility.Collapsed : Visibility.Visible;
                fileValWrap.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
                fileOpCmb.Items.Clear();
                foreach (var op in isDate
                    ? new[] { "Equal to", "Not equal to", "More recent", "More recent or equal to", "Older", "Older or equal to" }
                    : new[] { "Equal to", "Not equal to", "Greater than", "Greater than or equal to", "Less than", "Less than or equal to" })
                    fileOpCmb.Items.Add(new ComboBoxItem { Content = op });
                fileOpCmb.SelectedIndex = 0;
                fileValBox.Tag = sel == "Size in MB" ? "e.g. 512" : sel.StartsWith("Date") ? "yyyy-MM-dd" : "e.g. 1.0.0.0";
                fileTimeBox.Visibility = isDate ? Visibility.Visible : Visibility.Collapsed;

                rule.FileCheck = sel;
                // Auto-fill the value for the newly selected method from the installed file,
                // if we can resolve it (full path captured during Auto-fill, or Path+FileName).
                string full = ResolveFileFullPath(rule);
                string autoVal = ComputeFileDetectionValue(full, sel);
                if (!string.IsNullOrWhiteSpace(autoVal))
                {
                    fileValBox.Text = autoVal;   // TextChanged updates rule.FileValue
                }
                // For Date methods, also auto-fill the time-of-day from the file timestamp.
                if (isDate && !string.IsNullOrWhiteSpace(full) && File.Exists(full))
                {
                    try
                    {
                        DateTime ts = sel == "Date created" ? File.GetCreationTime(full) : File.GetLastWriteTime(full);
                        fileTimeBox.Text = ts.ToString("hh:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch { }
                }
            };
        }

        // ── Registry rule fields ─────────────────────────────────────
        private void BuildRegistryRuleFields(StackPanel fields, DetectionRule rule)
        {
            // Key path * - with registry editor button
            // Declare keyBox before first call so the Value name lambda can capture it
            TextBox keyBox;
            fields.Children.Add(MakeInlineRowWithButton(
                "Key path", true, out keyBox,
                "🗝", "Open Registry Editor",
                _ =>
                {
                    // Read key path from rule (keyBox is the out param, use rule to avoid CS0165)
                    try
                    {
                        string kp = rule.RegKeyPath.Trim();
                        if (!string.IsNullOrWhiteSpace(kp))
                        {
                            using (var applets = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                                @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                                applets?.SetValue("LastKey", kp);
                        }
                        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("regedit"))
                            try { proc.Kill(); proc.WaitForExit(1500); } catch { }
                        System.Threading.Thread.Sleep(200);
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo("regedit.exe") { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        ShowThemedMessage("Error", "⚠", "amber", "Could not open Registry Editor:\n" + ex.Message);
                    }
                }));
            keyBox.Tag = @"e.g. HKEY_LOCAL_MACHINE\SOFTWARE\MyApp";
            if (!string.IsNullOrWhiteSpace(rule.RegKeyPath)) keyBox.Text = rule.RegKeyPath;
            keyBox.TextChanged += (_, _) => { rule.RegKeyPath = keyBox.Text; RequestAutoSave(); };

            // Value name (optional) - with registry editor button
            fields.Children.Add(MakeInlineRowWithButton(
                "Value name", false, out TextBox valNameBox,
                "🗝", "Open Registry Editor",
                _ =>
                {
                    try
                    {
                        string kp = keyBox.Text.Trim();
                        if (!string.IsNullOrWhiteSpace(kp))
                        {
                            using (var applets = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                                @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                                applets?.SetValue("LastKey", kp);
                        }
                        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("regedit"))
                            try { proc.Kill(); proc.WaitForExit(1500); } catch { }
                        System.Threading.Thread.Sleep(200);
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo("regedit.exe") { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        ShowThemedMessage("Error", "⚠", "amber", "Could not open Registry Editor:\n" + ex.Message);
                    }
                }));
            valNameBox.Tag = "Leave blank to detect key existence";
            if (!string.IsNullOrWhiteSpace(rule.RegValue)) valNameBox.Text = rule.RegValue;
            valNameBox.TextChanged += (_, _) => rule.RegValue = valNameBox.Text;

            // Detection method *
            var dmRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            dmRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            dmRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dmLbl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var regDmLblTb = new TextBlock { Text = "Detection method", FontSize = 11, Margin = new Thickness(0, 0, 3, 0) };
            regDmLblTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            dmLbl.Children.Add(regDmLblTb);
            dmLbl.Children.Add(new TextBlock { Text = "*", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0x58, 0x4F)), FontSize = 11 });
            Grid.SetColumn(dmLbl, 0);
            var regDmCmb = new ComboBox { Height = 24, FontSize = 11, Style = (Style)Application.Current.TryFindResource("ModernComboBoxStyle") };
            foreach (var m in new[] { "Key exists", "Key does not exist", "Value exists", "Value does not exist", "String comparison", "Integer comparison", "Version comparison" })
                regDmCmb.Items.Add(new ComboBoxItem { Content = m });
            regDmCmb.SelectedIndex = 0;
            // Apply RegCheck from rule
            if (!string.IsNullOrWhiteSpace(rule.RegCheck))
                foreach (ComboBoxItem ci in regDmCmb.Items)
                    if (string.Equals(ci.Content?.ToString(), rule.RegCheck, StringComparison.OrdinalIgnoreCase))
                    { regDmCmb.SelectedItem = ci; break; }
            Grid.SetColumn(regDmCmb, 1);
            dmRow.Children.Add(dmLbl);
            dmRow.Children.Add(regDmCmb);
            fields.Children.Add(dmRow);

            // Operator + value - Grid so label aligns with labels above (200px col)
            string initRegSel = (regDmCmb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            // "Key exists/does not exist" and "Value exists/does not exist" are presence checks -
            // they need no operator/comparison value row (the Value name field above suffices).
            static bool RegMethodNeedsValue(string m)
                => m != "Key exists" && m != "Key does not exist"
                && m != "Value exists" && m != "Value does not exist";
            bool initNeedsVal = RegMethodNeedsValue(initRegSel);
            var regOpValueRow = new Grid { Margin = new Thickness(0, 0, 0, 0),
                Visibility = initNeedsVal ? Visibility.Visible : Visibility.Collapsed };
            regOpValueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            regOpValueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var regOpLabel = new TextBlock { Text = "Operator", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center };
            regOpLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(regOpLabel, 0);
            var regOpContent = new StackPanel { Orientation = Orientation.Horizontal };
            var regOpCmb = new ComboBox { Height = 24, FontSize = 11, Width = 200, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.TryFindResource("ModernComboBoxStyle") };
            foreach (var op in new[] { "Equal to", "Not equal to", "Greater than", "Greater than or equal to", "Less than", "Less than or equal to" })
                regOpCmb.Items.Add(new ComboBoxItem { Content = op });
            regOpCmb.SelectedIndex = 0;
            // Apply RegOperator from rule
            if (!string.IsNullOrWhiteSpace(rule.RegOperator))
                foreach (ComboBoxItem ci in regOpCmb.Items)
                    if (string.Equals(ci.Content?.ToString(), rule.RegOperator, StringComparison.OrdinalIgnoreCase))
                    { regOpCmb.SelectedItem = ci; break; }
            regOpCmb.SelectionChanged += (_, _) =>
            {
                rule.RegOperator = (regOpCmb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? rule.RegOperator;
                RequestAutoSave();
            };
            var regValBox = new TextBox { Height = 24, FontSize = 11, Width = 130,
                Padding = new Thickness(4,0,20,0),
                Style = (Style)Application.Current.TryFindResource("ModernTextBoxStyle"), Tag = "value" };
            if (!string.IsNullOrWhiteSpace(rule.RegValueData)) regValBox.Text = rule.RegValueData;
            regValBox.TextChanged += (_, _) => rule.RegValueData = regValBox.Text;
            var regValWrapper = new Grid { Width = 140 };
            var regValXBtn = new Button { Content = "✕", Width = 22, Height = 18, FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0,0,4,0), BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Clear" };
            regValXBtn.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
            regValXBtn.Click += (_, _) => { regValBox.Clear(); rule.RegValueData = ""; };
            regValWrapper.Children.Add(regValBox); regValWrapper.Children.Add(regValXBtn);
            // 32-bit checkbox - inline after value textbox
            var cbReg32 = new CheckBox { Content = "Associated with a 32-bit app on 64-bit clients", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                IsChecked = rule.Reg32Bit,
                Cursor = System.Windows.Input.Cursors.Hand };
            cbReg32.SetResourceReference(CheckBox.ForegroundProperty, "TextPrimaryBrush");
            if (TryFindResource("RoundCheckBoxStyle") is Style cbReg32Style) cbReg32.Style = cbReg32Style;
            cbReg32.Checked   += (_, _) => { rule.Reg32Bit = true;  RequestAutoSave(); };
            cbReg32.Unchecked += (_, _) => { rule.Reg32Bit = false; RequestAutoSave(); };

            regOpContent.Children.Add(regOpCmb);
            regOpContent.Children.Add(regValWrapper);
            regOpContent.Children.Add(cbReg32);
            Grid.SetColumn(regOpContent, 1);
            regOpValueRow.Children.Add(regOpLabel);
            regOpValueRow.Children.Add(regOpContent);
            fields.Children.Add(regOpValueRow);

            // The row stays visible for ALL methods so the 32-bit checkbox is always shown;
            // only the operator / comparison-value controls hide for presence checks.
            regOpValueRow.Visibility = Visibility.Visible;
            regOpLabel.Visibility    = initNeedsVal ? Visibility.Visible : Visibility.Collapsed;
            regOpCmb.Visibility      = initNeedsVal ? Visibility.Visible : Visibility.Collapsed;
            regValWrapper.Visibility = initNeedsVal ? Visibility.Visible : Visibility.Collapsed;

            regDmCmb.SelectionChanged += (_, _) =>
            {
                string sel = (regDmCmb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                rule.RegCheck = sel;
                bool needsVal = RegMethodNeedsValue(sel);
                regOpLabel.Visibility    = needsVal ? Visibility.Visible : Visibility.Collapsed;
                regOpCmb.Visibility      = needsVal ? Visibility.Visible : Visibility.Collapsed;
                regValWrapper.Visibility = needsVal ? Visibility.Visible : Visibility.Collapsed;
                RequestAutoSave();
            };
        }

        /// <summary>Creates a 2-column label + textbox row for detection rule cards.</summary>
        private FrameworkElement MakeInlineRow(string label, bool required, out TextBox tb, int width = 0)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var lblTb = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, required ? 3 : 0, 0) };
            lblTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            lbl.Children.Add(lblTb);
            if (required)
                lbl.Children.Add(new TextBlock { Text = "*", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0x58, 0x4F)), FontSize = 11 });
            Grid.SetColumn(lbl, 0);

            // Textbox + X clear button overlay
            var tbWrapper = new Grid();
            Grid.SetColumn(tbWrapper, 1);
            if (width > 0) { tbWrapper.Width = width; tbWrapper.HorizontalAlignment = HorizontalAlignment.Left; }

            tb = new TextBox
            {
                Height = 24, FontSize = 11, Padding = new Thickness(6, 0, 24, 0),
                Style  = (Style)Application.Current.TryFindResource("ModernTextBoxStyle")
            };
            var xBtn = new Button
            {
                Content = "✕", Width = 20, Height = 16, FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Clear"
            };
            xBtn.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
            var capturedTb2 = tb;
            xBtn.Click += (_, _) => capturedTb2.Clear();

            tbWrapper.Children.Add(tb);
            tbWrapper.Children.Add(xBtn);

            row.Children.Add(lbl);
            row.Children.Add(tbWrapper);
            return row;
        }

        /// <summary>
        /// Like MakeInlineRow but adds an icon button (browse/registry) after the textbox.
        /// btnContent = emoji/text for the button, tooltip = hover text.
        /// onClick = action fired when button clicked, receives the TextBox.
        /// </summary>
        private FrameworkElement MakeInlineRowWithButton(
            string label, bool required, out TextBox tb,
            string btnContent, string tooltip, Action<TextBox> onClick)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var lblTb = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, required ? 3 : 0, 0) };
            lblTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            lbl.Children.Add(lblTb);
            if (required)
                lbl.Children.Add(new TextBlock { Text = "*", Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0x58, 0x4F)), FontSize = 11 });
            Grid.SetColumn(lbl, 0);

            // Textbox + X clear button overlay
            var tbWrapper = new Grid();
            Grid.SetColumn(tbWrapper, 1);

            tb = new TextBox { Height = 24, FontSize = 11, Padding = new Thickness(6, 0, 24, 0),
                Style = (Style)Application.Current.TryFindResource("ModernTextBoxStyle") };
            var xBtn2 = new Button
            {
                Content = "✕", Width = 20, Height = 16, FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0),
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Clear"
            };
            xBtn2.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
            var capturedTbX = tb;
            xBtn2.Click += (_, _) => capturedTbX.Clear();
            tbWrapper.Children.Add(tb);
            tbWrapper.Children.Add(xBtn2);

            var btn = new Button {
                Content = btnContent, Height = 24, Width = 28,
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 13,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = tooltip
            };
            btn.SetResourceReference(Button.BackgroundProperty, "FooterBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "TextPrimaryBrush");
            btn.SetResourceReference(Button.BorderBrushProperty, "BorderInputBrush");
            // Rounded corners via template
            var btnT = new ControlTemplate(typeof(Button));
            var btnBd = new FrameworkElementFactory(typeof(Border));
            btnBd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            btnBd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            btnBd.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            btnBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            var btnCp = new FrameworkElementFactory(typeof(ContentPresenter));
            btnCp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            btnCp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            btnBd.AppendChild(btnCp);
            btnT.VisualTree = btnBd;
            btn.Template = btnT;
            var capturedTb = tb;
            btn.Click += (_, _) => onClick(capturedTb);
            Grid.SetColumn(btn, 2);

            row.Children.Add(lbl);
            row.Children.Add(tbWrapper);
            row.Children.Add(btn);
            return row;
        }



        // ══════════════════════════════════════════════════════════════
        // RETURN CODES
        // ══════════════════════════════════════════════════════════════
        private void BtnAddReturnCode_Click(object sender, RoutedEventArgs e)
        {
            ReturnCodesPanel.Children.Add(MakeReturnCodeRow("", "Success"));
        }

        // ══════════════════════════════════════════════════════════════
        // DEPENDENCIES TAB
        // ══════════════════════════════════════════════════════════════
        private async void BtnAddDependency_Click(object sender, RoutedEventArgs e)
        {
            // Needs the signed-in session to query Intune for apps.
            if (!(_intuneSessionConnected && _psSession != null && _psSession.IsAlive))
            {
                ShowThemedMessage("Sign in required", "\u26a0", "amber",
                    "Sign in to Intune first, then add a dependency.");
                return;
            }

            var addBtn = sender as Button;
            if (addBtn != null) addBtn.IsEnabled = false;
            try
            {
                // Stay on the Dependencies tab; just write progress to the Log quietly.
                AppendLog("Loading applications from Intune...");
                var apps = await FetchIntuneAppsAsync();
                if (apps == null)
                {
                    ShowThemedMessage("Could not load apps", "\u26d4", "red",
                        "Could not load applications from Intune. See the Log tab for details.");
                    return;
                }

                // A dependency on the app itself is not valid - exclude the current app.
                string curName = TxtAppName.Text.Trim();
                var pick = apps.Where(a => !string.Equals(a.Name, curName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (pick.Count == 0)
                {
                    ShowThemedMessage("No apps", "\u26a0", "amber", "No other Win32 applications were found in Intune.");
                    return;
                }

                var chosen = ShowAddDependencyDialog(pick);
                if (chosen == null || chosen.Count == 0) return;

                // Avoid adding the same dependency twice.
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in DependenciesList.Children)
                    if (child is Grid g && g.Children.Count > 0 && g.Children[0] is TextBlock tbn)
                        existing.Add(tbn.Text.Trim());

                int added = 0;
                foreach (var app in chosen)
                {
                    if (existing.Contains(app.Name)) continue;
                    AddDependencyRow(app.Name, app.Id, autoInstall: true);
                    existing.Add(app.Name);
                    added++;
                }
                if (added > 0) RequestAutoSave();
                AppendLog("Added " + added + (added == 1 ? " dependency." : " dependencies."));
            }
            finally
            {
                if (addBtn != null) addBtn.IsEnabled = true;
            }
        }

        // Lists Win32 apps from Intune via the shared signed-in session. Returns null on error.
        private Task<List<IntuneAppItem>?> FetchIntuneAppsAsync() => IntuneSession.FetchAppsAsync(AppendLog);

        // Delegates to the shared IntunePickerDialogs so the app picker has a single
        // implementation (also used by the Auto Package Download tab). TC/DlgBtnStyled stay
        // here because the page's other dialogs use them.
        private List<IntuneAppItem>? ShowAddDependencyDialog(List<IntuneAppItem> apps, string dialogTitle = "Add dependency", string countHeader = "Dependency Count", Func<IntuneAppItem, string>? countSelector = null, bool singleSelect = false)
            => IntunePickerDialogs.ShowAppPicker(Window.GetWindow(this), "Publish Package - " + dialogTitle, countHeader, apps, countSelector, singleSelect);

        // ══════════════════════════════════════════════════════════════
        // SUPERSEDENCE TAB
        // ══════════════════════════════════════════════════════════════
        private async void BtnAddSupersedence_Click(object sender, RoutedEventArgs e)
        {
            // Needs the signed-in session to query Intune for apps.
            if (!(_intuneSessionConnected && _psSession != null && _psSession.IsAlive))
            {
                ShowThemedMessage("Sign in required", "\u26a0", "amber",
                    "Sign in to Intune first, then add a superseded app.");
                return;
            }

            var addBtn = sender as Button;
            if (addBtn != null) addBtn.IsEnabled = false;
            try
            {
                AppendLog("Loading applications from Intune...");
                var apps = await FetchIntuneAppsAsync();
                if (apps == null)
                {
                    ShowThemedMessage("Could not load apps", "\u26d4", "red",
                        "Could not load applications from Intune. See the Log tab for details.");
                    return;
                }

                // An app cannot supersede itself - exclude the current app.
                string curName = TxtAppName.Text.Trim();
                var pick = apps.Where(a => !string.Equals(a.Name, curName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (pick.Count == 0)
                {
                    ShowThemedMessage("No apps", "\u26a0", "amber", "No other Win32 applications were found in Intune.");
                    return;
                }

                var chosen = ShowAddDependencyDialog(pick, "Add app to supersede", "Superseded Count", a => a.SupersededCount.ToString());
                if (chosen == null || chosen.Count == 0) return;

                // Avoid adding the same superseded app twice.
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in SupersedenceList.Children)
                    if (child is Grid g && g.Children.Count > 0 && g.Children[0] is TextBlock tbn)
                        existing.Add(tbn.Text.Trim());

                int added = 0;
                foreach (var app in chosen)
                {
                    if (existing.Contains(app.Name)) continue;
                    AddSupersedenceRow(app.Name, app.Id, uninstallPrevious: false);
                    existing.Add(app.Name);
                    added++;
                }
                if (added > 0) RequestAutoSave();
                AppendLog("Added " + added + (added == 1 ? " superseded app." : " superseded apps."));
            }
            finally
            {
                if (addBtn != null) addBtn.IsEnabled = true;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // ASSIGNMENTS TAB
        // ══════════════════════════════════════════════════════════════

        // Available
        private async void BtnAddAvailable_Click(object sender, RoutedEventArgs e)
        {
            // Needs the signed-in session to query Entra for groups.
            if (!(_intuneSessionConnected && _psSession != null && _psSession.IsAlive))
            {
                ShowThemedMessage("Sign in required", "\u26a0", "amber",
                    "Sign in to Intune first, then add a group.");
                return;
            }

            var addBtn = sender as Button;
            if (addBtn != null) addBtn.IsEnabled = false;
            try
            {
                AppendLog("Loading Entra groups...");
                var groups = await FetchEntraGroupsAsync();
                if (groups == null)
                {
                    ShowThemedMessage("Could not load groups", "\u26d4", "red",
                        "Could not load groups from Entra. See the Log tab for details.");
                    return;
                }
                if (groups.Count == 0)
                {
                    ShowThemedMessage("No groups", "\u26a0", "amber", "No groups were found in Entra.");
                    return;
                }

                var chosen = ShowSelectGroupsDialog(groups);
                if (chosen == null || chosen.Count == 0) return;

                int added = 0;
                foreach (var grp in chosen)
                {
                    if (string.IsNullOrWhiteSpace(grp.Name)) continue;
                    AddAssignmentRow(AvailableList, "Available", fullCols: true, fixedName: grp.Name);
                    added++;
                }
                if (added > 0) RequestAutoSave();
                AppendLog("Added " + added + (added == 1 ? " group." : " groups."));
            }
            finally
            {
                if (addBtn != null) addBtn.IsEnabled = true;
            }
        }

        // Lists Entra (Azure AD) groups via the shared signed-in session. Returns null on error.
        private Task<List<EntraGroup>?> FetchEntraGroupsAsync() => IntuneSession.FetchGroupsAsync(AppendLog);


        // Searchable, multi-select Entra group picker (screenshot-5 style). Returns the chosen
        // groups, or null on cancel.
        // Delegates to the shared IntunePickerDialogs (single implementation).
        private List<EntraGroup>? ShowSelectGroupsDialog(List<EntraGroup> groups)
            => IntunePickerDialogs.ShowGroupPicker(Window.GetWindow(this), "Publish Package - Select groups", groups);

        // ══════════════════════════════════════════════════════════════
        // REPORTS TAB - app install status (device + user)
        // ══════════════════════════════════════════════════════════════
        private string _reportError = "";
        private IntuneAppItem? _reportApp;
        private ReportTable? _deviceReport;
        private ReportTable? _userReport;
        private string _reportKind = "device";

        // Find-in-results state for the report grid
        private readonly List<TextBlock> _reportCells = new List<TextBlock>();
        private readonly List<(TextBlock cell, System.Windows.Documents.Run run)> _searchMatches = new List<(TextBlock, System.Windows.Documents.Run)>();
        private int _searchIdx = -1;
        private static readonly System.Windows.Media.Brush ReportHlBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xEB, 0x3B));
        private static readonly System.Windows.Media.Brush ReportHlCurrentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xA7, 0x26));
        private static readonly System.Windows.Media.Brush ReportHlFg = System.Windows.Media.Brushes.Black;

        private sealed class ReportTable
        {
            public string Title = "";
            public List<string> Columns = new List<string>();
            public List<List<string>> Rows = new List<List<string>>();
        }

        private async void BtnSearchApp_Click(object sender, RoutedEventArgs e) => await SearchAppAndLoadBothAsync(sender as Button);

        private async void BtnDeviceReport_Click(object sender, RoutedEventArgs e)
        {
            _reportKind = "device";
            if (_reportApp != null && _deviceReport != null) { DisplayReport(_reportApp, _deviceReport); return; }
            await LoadReportAsync(sender as Button, "device");
        }

        private async void BtnUserReport_Click(object sender, RoutedEventArgs e)
        {
            _reportKind = "user";
            if (_reportApp != null && _userReport != null) { DisplayReport(_reportApp, _userReport); return; }
            await LoadReportAsync(sender as Button, "user");
        }

        private async void BtnRefreshReport_Click(object sender, RoutedEventArgs e)
        {
            if (_reportApp == null)
            {
                await SearchAppAndLoadBothAsync(sender as Button);
                return;
            }
            if (!(_intuneSessionConnected && _psSession != null && _psSession.IsAlive))
            {
                ShowThemedMessage("Sign in required", "\u26a0", "amber",
                    "Sign in to Intune first, then refresh.");
                return;
            }

            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;
            try
            {
                var app = _reportApp;
                _deviceReport = null;
                _userReport = null;

                TxtReportSummary.Visibility = Visibility.Visible;
                TxtReportSummary.Text = "Refreshing device and user install status for \"" + app.Name + "\", please wait...";
                ReportRowsPanel.Children.Clear();
                var loading = new TextBlock { Text = "Refreshing reports, please wait...", FontSize = 11, Margin = new Thickness(12, 10, 12, 10) };
                loading.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                ReportRowsPanel.Children.Add(loading);

                AppendLog("Refreshing device + user reports for: " + app.Name);
                var dev = await FetchDeviceReportAsync(app);
                var usr = await FetchUserReportAsync(app);
                _deviceReport = dev;
                _userReport = usr;

                if (_reportKind == "user" && usr != null) DisplayReport(app, usr);
                else if (dev != null) DisplayReport(app, dev);
                else if (usr != null) DisplayReport(app, usr);
                else
                {
                    ShowThemedMessage("Could not refresh", "\u26d4", "red",
                        string.IsNullOrWhiteSpace(_reportError)
                            ? "Could not refresh the reports for this app. See the Log tab for details."
                            : ("Graph returned this error:\n\n" + _reportError));
                    ReportRowsPanel.Children.Clear();
                    TxtReportSummary.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
            }
        }

        // Pick an app once and pull BOTH device and user reports, caching them so the two
        // view buttons just switch the displayed table (no repeated app search).
        private async Task SearchAppAndLoadBothAsync(Button? btn)
        {
            if (!(_intuneSessionConnected && _psSession != null && _psSession.IsAlive))
            {
                ShowThemedMessage("Sign in required", "\u26a0", "amber",
                    "Sign in to Intune first, then load a report.");
                return;
            }

            if (btn != null) btn.IsEnabled = false;
            try
            {
                AppendLog("Loading applications from Intune...");
                var apps = await FetchIntuneAppsAsync();
                if (apps == null)
                {
                    ShowThemedMessage("Could not load apps", "\u26d4", "red",
                        "Could not load applications from Intune. See the Log tab for details.");
                    return;
                }
                if (apps.Count == 0)
                {
                    ShowThemedMessage("No apps", "\u26a0", "amber", "No Win32 applications were found in Intune.");
                    return;
                }

                var chosen = ShowAddDependencyDialog(apps, "Select app for report", singleSelect: true);
                if (chosen == null || chosen.Count == 0) return;
                var app = chosen[0];
                _reportApp = app;
                _deviceReport = null;
                _userReport = null;

                TxtReportSummary.Visibility = Visibility.Visible;
                TxtReportSummary.Text = "Generating device and user install status for \"" + app.Name + "\", please wait...";
                ReportRowsPanel.Children.Clear();
                ReportRowsPanel.Children.Add(new TextBlock { Text = "Generating reports, please wait...", FontSize = 11, Foreground = TC("TextMutedBrush"), Margin = new Thickness(12, 10, 12, 10) });

                AppendLog("Loading device + user reports for: " + app.Name);
                var dev = await FetchDeviceReportAsync(app);
                var usr = await FetchUserReportAsync(app);
                _deviceReport = dev;
                _userReport = usr;

                if (dev != null) { _reportKind = "device"; DisplayReport(app, dev); }
                else if (usr != null) { _reportKind = "user"; DisplayReport(app, usr); }
                else
                {
                    ShowThemedMessage("Could not load reports", "\u26d4", "red",
                        string.IsNullOrWhiteSpace(_reportError)
                            ? "Could not load the reports for this app. See the Log tab for details."
                            : ("Graph returned this error:\n\n" + _reportError));
                    ReportRowsPanel.Children.Clear();
                    TxtReportSummary.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private async Task LoadReportAsync(Button? btn, string kind)
        {
            if (!(_intuneSessionConnected && _psSession != null && _psSession.IsAlive))
            {
                ShowThemedMessage("Sign in required", "\u26a0", "amber",
                    "Sign in to Intune first, then load a report.");
                return;
            }

            if (btn != null) btn.IsEnabled = false;
            try
            {
                AppendLog("Loading applications from Intune...");
                var apps = await FetchIntuneAppsAsync();
                if (apps == null)
                {
                    ShowThemedMessage("Could not load apps", "\u26d4", "red",
                        "Could not load applications from Intune. See the Log tab for details.");
                    return;
                }
                if (apps.Count == 0)
                {
                    ShowThemedMessage("No apps", "\u26a0", "amber", "No Win32 applications were found in Intune.");
                    return;
                }

                var chosen = ShowAddDependencyDialog(apps, "Select app for report", singleSelect: true);
                if (chosen == null || chosen.Count == 0) return;
                var app = chosen[0];

                // A different app invalidates any cached report from a previous app.
                if (_reportApp == null || !string.Equals(_reportApp.Id, app.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _deviceReport = null;
                    _userReport = null;
                }

                string nice = kind == "user" ? "User install status" : "Device install status";
                TxtReportSummary.Visibility = Visibility.Visible;
                TxtReportSummary.Text = nice + " is generating for \"" + app.Name + "\", please wait...";
                ReportRowsPanel.Children.Clear();
                ReportRowsPanel.Children.Add(new TextBlock { Text = "Generating report, please wait...", FontSize = 11, Foreground = TC("TextMutedBrush"), Margin = new Thickness(12, 10, 12, 10) });

                AppendLog("Loading " + kind + " report for: " + app.Name);
                ReportTable? t = kind == "user" ? await FetchUserReportAsync(app) : await FetchDeviceReportAsync(app);
                if (t == null)
                {
                    ShowThemedMessage("Could not load report", "\u26d4", "red",
                        string.IsNullOrWhiteSpace(_reportError)
                            ? "Could not load the report for this app. See the Log tab for details."
                            : ("Graph returned this error:\n\n" + _reportError));
                    ReportRowsPanel.Children.Clear();
                    TxtReportSummary.Visibility = Visibility.Collapsed;
                    return;
                }

                _reportApp = app;
                if (kind == "user") _userReport = t; else _deviceReport = t;
                DisplayReport(app, t);
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
            }
        }

        // Runs an Intune cached-report export job and returns the CSV rows as dictionaries.
        private async Task<List<Dictionary<string, string>>?> RunReportExportAsync(string reportName, string[]? selectCols, string appId)
        {
            _reportError = "";
            if (_psSession == null || !_psSession.IsAlive) { _reportError = "Not signed in."; return null; }
            if (string.IsNullOrWhiteSpace(appId)) { _reportError = "No app id."; return null; }

            string selPart = (selectCols != null && selectCols.Length > 0)
                ? "select=@(" + string.Join(",", selectCols.Select(c => "'" + c + "'")) + "); "
                : "";
            string cmd =
                "try { " +
                "$h=@{ Authorization = $Global:AuthenticationHeader['Authorization'] }; " +
                "$aid='" + appId + "'; " +
                "$bobj=@{ reportName='" + reportName + "'; filter=(\"(ApplicationId eq '\" + $aid + \"')\"); " + selPart + "format='csv'; localizationType='LocalizedValuesAsAdditionalColumn' }; " +
                "$b=$bobj | ConvertTo-Json -Compress; " +
                "$job=Invoke-RestMethod -Uri 'https://graph.microsoft.com/beta/deviceManagement/reports/exportJobs' -Headers $h -Method POST -Body $b -ContentType 'application/json' -ErrorAction Stop; " +
                "$jid=$job.id; $rurl=$null; " +
                "for ($k=0; $k -lt 60; $k++) { Start-Sleep -Milliseconds 1500; $stj=Invoke-RestMethod -Uri (\"https://graph.microsoft.com/beta/deviceManagement/reports/exportJobs('\" + $jid + \"')\") -Headers $h -Method GET -ErrorAction Stop; if ($stj.status -eq 'completed') { $rurl=$stj.url; break }; if ($stj.status -eq 'failed') { throw 'Export job failed' } }; " +
                "if (-not $rurl) { throw 'Export timed out (report still processing)' }; " +
                "$base=[System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), [System.Guid]::NewGuid().ToString('N')); $zipf=$base + '.zip'; $exd=$base + '_d'; " +
                "Invoke-WebRequest -Uri $rurl -OutFile $zipf -UseBasicParsing -ErrorAction Stop; " +
                "Expand-Archive -Path $zipf -DestinationPath $exd -Force; " +
                "$csvf=Get-ChildItem -Path $exd -Filter *.csv | Select-Object -First 1; " +
                "$data=@(); if ($csvf) { $data=@(Import-Csv -Path $csvf.FullName) }; " +
                "Write-Output ('##RPT##' + (@($data) | ConvertTo-Json -Compress -Depth 4) + '##RPTEND##'); " +
                "try { Remove-Item $zipf -Force -ErrorAction SilentlyContinue; Remove-Item $exd -Recurse -Force -ErrorAction SilentlyContinue } catch {} " +
                "} catch { $em=$_.Exception.Message; try { if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $em=[string]$_.ErrorDetails.Message } } catch {}; try { $rsp=$_.Exception.Response; if ($rsp -ne $null) { $rd=New-Object System.IO.StreamReader($rsp.GetResponseStream()); $bt=$rd.ReadToEnd(); $rd.Close(); if ($bt) { $em=$bt } } } catch {}; Write-Output ('##RPTERR##' + (([string]$em) -replace '\\s+',' ')) }";

            string payload = "", err = "";
            try
            {
                await _psSession.RunAsync(cmd, line =>
                {
                    int a = line.IndexOf("##RPT##", StringComparison.Ordinal);
                    int b = line.IndexOf("##RPTEND##", StringComparison.Ordinal);
                    if (a >= 0 && b > a) { payload = line.Substring(a + "##RPT##".Length, b - (a + "##RPT##".Length)); return; }
                    int ei = line.IndexOf("##RPTERR##", StringComparison.Ordinal);
                    if (ei >= 0) { err = line.Substring(ei + "##RPTERR##".Length); return; }
                }, 180000);
            }
            catch (Exception ex) { _reportError = ex.Message; AppendLog("Report error: " + ex.Message); return null; }

            if (!string.IsNullOrEmpty(err)) { _reportError = err; AppendLog("Report: " + err); return null; }

            var list = new List<Dictionary<string, string>>();
            if (string.IsNullOrWhiteSpace(payload)) return list;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                    foreach (var el in root.EnumerateArray()) list.Add(ElemToDict(el));
                else if (root.ValueKind == JsonValueKind.Object)
                    list.Add(ElemToDict(root));
                return list;
            }
            catch (Exception ex) { _reportError = "Parse error: " + ex.Message; AppendLog("Report parse error: " + ex.Message); return null; }
        }

        private static Dictionary<string, string> ElemToDict(JsonElement el)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (el.ValueKind == JsonValueKind.Object)
                foreach (var p in el.EnumerateObject())
                {
                    string v = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "")
                             : p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetRawText()
                             : p.Value.ValueKind == JsonValueKind.Null ? ""
                             : p.Value.ToString();
                    d[p.Name] = v;
                }
            return d;
        }

        private static string Val(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? (v ?? "") : "";

        private static string ValAny(Dictionary<string, string> d, params string[] keys)
        {
            foreach (var k in keys) if (d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return "";
        }

        private async Task<ReportTable?> FetchDeviceReportAsync(IntuneAppItem app)
        {
            var dicts = await RunReportExportAsync("DeviceInstallStatusByApp",
                new[] { "DeviceName", "UserPrincipalName", "InstallState", "InstallStateDetail" }, app.Id);
            if (dicts == null) return null;
            var t = new ReportTable { Title = "Device install status" };
            t.Columns.AddRange(new[] { "User", "Device", "App Name", "Version", "Status", "Detail" });
            foreach (var d in dicts)
            {
                string state = Val(d, "InstallState_loc"); if (string.IsNullOrWhiteSpace(state)) state = Val(d, "InstallState");
                string detail = Val(d, "InstallStateDetail_loc"); if (string.IsNullOrWhiteSpace(detail)) detail = Val(d, "InstallStateDetail");
                if (detail == "0") detail = "";
                t.Rows.Add(new List<string> { Val(d, "UserPrincipalName"), Val(d, "DeviceName"), app.Name, app.Version, StatusText(state), detail });
            }
            return t;
        }

        private async Task<ReportTable?> FetchUserReportAsync(IntuneAppItem app)
        {
            // No explicit select - let the report return its default columns (avoids invalid-column 400s).
            var dicts = await RunReportExportAsync("UserInstallStatusAggregateByApp", null, app.Id);
            if (dicts == null) return null;
            if (dicts.Count > 0) AppendLog("User report columns: " + string.Join(", ", dicts[0].Keys));
            var t = new ReportTable { Title = "User install status" };
            t.Columns.AddRange(new[] { "User name", "User", "App Name", "Version", "Installed", "Failed", "Pending", "Not installed" });
            foreach (var d in dicts)
            {
                string name = ValAny(d, "UserName", "UserDisplayName");
                string user = ValAny(d, "UserPrincipalName", "UserEmail");
                t.Rows.Add(new List<string> { name, user, app.Name, app.Version,
                    ValAny(d, "InstalledDeviceCount", "Installed"),
                    ValAny(d, "FailedDeviceCount", "Failed"),
                    ValAny(d, "PendingInstallDeviceCount", "PendingInstall", "Pending"),
                    ValAny(d, "NotInstalledDeviceCount", "NotInstalled") });
            }
            return t;
        }

        private static double ColWidth(string col)
        {
            switch (col)
            {
                case "User name": return 1.5;
                case "User":     return 1.7;
                case "Device":   return 1.6;
                case "App Name": return 1.6;
                case "Version":  return 0.9;
                case "Status":   return 1.1;
                case "Detail":   return 1.2;
                default:         return 0.9;
            }
        }

        private static Grid BuildCols(List<string> columns)
        {
            var g = new Grid();
            foreach (var col in columns)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWidth(col), GridUnitType.Star) });
            return g;
        }

        private void DisplayReport(IntuneAppItem app, ReportTable t)
        {
            ReportRowsPanel.Children.Clear();
            _reportCells.Clear();
            _searchMatches.Clear();
            _searchIdx = -1;

            string ver = string.IsNullOrWhiteSpace(app.Version) ? "" : (" v" + app.Version);
            TxtReportSummary.Visibility = Visibility.Visible;
            TxtReportSummary.Text = t.Title + "  -  " + app.Name + ver + "   |   " +
                t.Rows.Count + (t.Rows.Count == 1 ? " row" : " rows");

            var headBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(6, 6, 0, 0) };
            headBorder.SetResourceReference(Border.BackgroundProperty, "FooterBgBrush");
            headBorder.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");
            var hg = BuildCols(t.Columns);
            hg.Margin = new Thickness(12, 6, 12, 6);
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var x = new TextBlock { Text = t.Columns[c], FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                x.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Grid.SetColumn(x, c); hg.Children.Add(x);
            }
            headBorder.Child = hg;
            ReportRowsPanel.Children.Add(headBorder);

            if (t.Rows.Count == 0)
            {
                var noRec = new TextBlock { Text = "No records found for this app.", FontSize = 11, Margin = new Thickness(12, 10, 12, 10) };
                noRec.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                ReportRowsPanel.Children.Add(noRec);
                ApplyReportSearch();
                AppendLog("Report loaded: 0 records for " + app.Name + ".");
                return;
            }

            int statusCol = t.Columns.IndexOf("Status");
            foreach (var row in t.Rows)
            {
                var rb = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
                rb.SetResourceReference(Border.BorderBrushProperty, "BorderMainBrush");
                var g = BuildCols(t.Columns);
                g.Margin = new Thickness(12, 6, 12, 6);
                for (int c = 0; c < t.Columns.Count && c < row.Count; c++)
                {
                    string val = string.IsNullOrWhiteSpace(row[c]) ? "-" : row[c];
                    var tb = new TextBlock { Text = val, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                    tb.Tag = val; _reportCells.Add(tb);
                    if (c == statusCol) { tb.Foreground = StatusBrush(row[c]); tb.FontWeight = FontWeights.SemiBold; }
                    else tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    Grid.SetColumn(tb, c); g.Children.Add(tb);
                }
                rb.Child = g;
                ReportRowsPanel.Children.Add(rb);
            }
            ApplyReportSearch();
            AppendLog("Report loaded: " + t.Rows.Count + " records for " + app.Name + ".");
        }

        private void TxtReportSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtReportSearchHint != null)
                TxtReportSearchHint.Visibility = string.IsNullOrEmpty(TxtReportSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplyReportSearch();
        }

        private void TxtReportSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
                ReportSearchNav(shift ? -1 : 1);
                e.Handled = true;
            }
        }

        private void BtnReportSearchPrev_Click(object sender, RoutedEventArgs e) => ReportSearchNav(-1);
        private void BtnReportSearchNext_Click(object sender, RoutedEventArgs e) => ReportSearchNav(1);

        // Re-highlight every data cell for the current term and rebuild the ordered match list.
        private void ApplyReportSearch()
        {
            _searchMatches.Clear();
            _searchIdx = -1;
            string term = TxtReportSearch?.Text ?? "";
            var cmp = StringComparison.OrdinalIgnoreCase;

            foreach (var tb in _reportCells)
            {
                string original = tb.Tag as string ?? "";
                tb.Inlines.Clear();
                if (string.IsNullOrEmpty(term)) { tb.Text = original; continue; }
                int idx = 0;
                while (true)
                {
                    int found = original.IndexOf(term, idx, cmp);
                    if (found < 0)
                    {
                        if (idx < original.Length) tb.Inlines.Add(new System.Windows.Documents.Run(original.Substring(idx)));
                        break;
                    }
                    if (found > idx) tb.Inlines.Add(new System.Windows.Documents.Run(original.Substring(idx, found - idx)));
                    var hit = new System.Windows.Documents.Run(original.Substring(found, term.Length)) { Background = ReportHlBrush, Foreground = ReportHlFg };
                    tb.Inlines.Add(hit);
                    _searchMatches.Add((tb, hit));
                    idx = found + term.Length;
                }
            }

            if (_searchMatches.Count > 0) { _searchIdx = 0; EmphasizeCurrentMatch(true); }
            UpdateSearchCount();
        }

        private void EmphasizeCurrentMatch(bool scroll)
        {
            for (int i = 0; i < _searchMatches.Count; i++)
                _searchMatches[i].run.Background = (i == _searchIdx) ? ReportHlCurrentBrush : ReportHlBrush;
            if (scroll && _searchIdx >= 0 && _searchIdx < _searchMatches.Count)
                _searchMatches[_searchIdx].cell.BringIntoView();
        }

        private void ReportSearchNav(int dir)
        {
            if (_searchMatches.Count == 0) return;
            _searchIdx = (_searchIdx + dir + _searchMatches.Count) % _searchMatches.Count;
            EmphasizeCurrentMatch(true);
            UpdateSearchCount();
        }

        private void UpdateSearchCount()
        {
            if (TxtReportSearchCount == null) return;
            if (string.IsNullOrEmpty(TxtReportSearch?.Text)) { TxtReportSearchCount.Text = ""; return; }
            TxtReportSearchCount.Text = _searchMatches.Count == 0 ? "0/0" : ((_searchIdx + 1) + "/" + _searchMatches.Count);
        }

        private static string NormState(string state) =>
            new string((state ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

        private static string StatusText(string state)
        {
            switch (NormState(state))
            {
                case "installed":       case "1": return "Installed";
                case "failed":          case "2": return "Failed";
                case "notinstalled":    case "3": return "Not installed";
                case "uninstallfailed": case "4": return "Uninstall failed";
                case "pending":
                case "pendinginstall":  case "5": return "Pending";
                case "notapplicable":   case "6": return "Not applicable";
                case "unknown":
                case "":                return "Unknown";
                default:                return string.IsNullOrWhiteSpace(state) ? "Unknown" : state;
            }
        }

        private System.Windows.Media.Brush StatusBrush(string state)
        {
            switch (NormState(state))
            {
                case "installed":
                case "1":
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xA0, 0x43));
                case "failed":
                case "uninstallfailed":
                case "2":
                case "4":
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x35, 0x35));
                case "pending":
                case "pendinginstall":
                case "5":
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0x51, 0x00));
                default:
                    return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80));
            }
        }

        private async void BtnExportReport_Click(object sender, RoutedEventArgs e)
        {
            if (_reportApp == null)
            {
                ShowThemedMessage("Nothing to export", "\u26a0", "amber",
                    "Load a report first (Device install status or User install status), then export.");
                return;
            }

            string baseName = _reportApp.Name ?? "install_status";
            foreach (char ic in System.IO.Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ic, '_');

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export report",
                Filter = "Excel workbook (*.xlsx)|*.xlsx",
                FileName = baseName + "_InstallStatus.xlsx",
                DefaultExt = ".xlsx"
            };
            if (dlg.ShowDialog() != true) return;

            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;
            try
            {
                AppendLog("Preparing export (device + user)...");
                var dev = _deviceReport ?? await FetchDeviceReportAsync(_reportApp);
                var usr = _userReport ?? await FetchUserReportAsync(_reportApp);
                if (dev != null) _deviceReport = dev;
                if (usr != null) _userReport = usr;
                if (dev == null) { dev = new ReportTable { Title = "Device install status" }; dev.Columns.AddRange(new[] { "User", "Device", "App Name", "Version", "Status", "Detail" }); }
                if (usr == null) { usr = new ReportTable { Title = "User install status" }; usr.Columns.AddRange(new[] { "User name", "User", "App Name", "Version", "Installed", "Failed", "Pending", "Not installed" }); }

                WriteXlsx(dlg.FileName, new (string, ReportTable)[] { ("Device install status", dev), ("User install status", usr) });
                AppendLog("Report exported to: " + dlg.FileName);
                ShowThemedMessage("Exported", "\u2705", "blue", "Report saved to:\n" + dlg.FileName);
            }
            catch (Exception ex)
            {
                ShowThemedMessage("Export failed", "\u26d4", "red", "Could not save the file:\n" + ex.Message);
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
            }
        }

        // Minimal XLSX writer (one worksheet per report, inline strings, no dependencies).
        private static void WriteXlsx(string path, (string name, ReportTable table)[] sheets)
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create);
            using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);

            void Put(string entry, string content)
            {
                var en = zip.CreateEntry(entry);
                using var w = new System.IO.StreamWriter(en.Open(), new System.Text.UTF8Encoding(false));
                w.Write(content);
            }

            var ct = new System.Text.StringBuilder();
            ct.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            ct.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            ct.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            ct.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            ct.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (int i = 0; i < sheets.Length; i++)
                ct.Append("<Override PartName=\"/xl/worksheets/sheet" + (i + 1) + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            ct.Append("</Types>");
            Put("[Content_Types].xml", ct.ToString());

            Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");

            var wb = new System.Text.StringBuilder();
            wb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            wb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (int i = 0; i < sheets.Length; i++)
                wb.Append("<sheet name=\"" + XmlEsc(SheetName(sheets[i].name)) + "\" sheetId=\"" + (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
            wb.Append("</sheets></workbook>");
            Put("xl/workbook.xml", wb.ToString());

            var wr = new System.Text.StringBuilder();
            wr.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < sheets.Length; i++)
                wr.Append("<Relationship Id=\"rId" + (i + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + (i + 1) + ".xml\"/>");
            wr.Append("</Relationships>");
            Put("xl/_rels/workbook.xml.rels", wr.ToString());

            for (int i = 0; i < sheets.Length; i++)
            {
                var t = sheets[i].table;
                var sd = new System.Text.StringBuilder();
                sd.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                sd.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
                int rn = 1;
                sd.Append("<row r=\"" + rn + "\">");
                for (int c = 0; c < t.Columns.Count; c++) Cell(sd, c, rn, t.Columns[c]);
                sd.Append("</row>");
                foreach (var row in t.Rows)
                {
                    rn++;
                    sd.Append("<row r=\"" + rn + "\">");
                    for (int c = 0; c < row.Count; c++) Cell(sd, c, rn, row[c]);
                    sd.Append("</row>");
                }
                sd.Append("</sheetData></worksheet>");
                Put("xl/worksheets/sheet" + (i + 1) + ".xml", sd.ToString());
            }
        }

        private static void Cell(System.Text.StringBuilder sb, int col, int row, string val)
        {
            sb.Append("<c r=\"" + ColRef(col) + row + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + XmlEsc(val ?? "") + "</t></is></c>");
        }

        private static string ColRef(int col)
        {
            string s = ""; col++;
            while (col > 0) { int m = (col - 1) % 26; s = (char)('A' + m) + s; col = (col - 1) / 26; }
            return s;
        }

        private static string XmlEsc(string v) =>
            (v ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private static string SheetName(string n)
        {
            n = (n ?? "Sheet").Trim();
            foreach (char c in new[] { ':', '\\', '/', '?', '*', '[', ']' }) n = n.Replace(c, ' ');
            if (n.Length > 31) n = n.Substring(0, 31);
            return string.IsNullOrEmpty(n) ? "Sheet" : n;
        }

        private void AddAssignmentRow(StackPanel targetList, string assignType,
                                      bool fullCols, string? fixedName = null)
        {
            string groupName = fixedName ??
                Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter the Entra ID group name:", $"Add {assignType} Group", "");
            if (string.IsNullOrWhiteSpace(groupName)) return;

            // ── Duplicate check: skip if this group name already exists ──────
            bool alreadyExists = targetList.Children
                .OfType<Grid>()
                .Any(g => g.Children.OfType<StackPanel>()
                    .Any(sp => Grid.GetColumn(sp) == 0 &&
                               sp.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text
                                   ?.Trim().Equals(groupName, StringComparison.OrdinalIgnoreCase) == true));
            if (alreadyExists) return;

            // Remove placeholder
            var placeholder = targetList.Children.OfType<TextBlock>().FirstOrDefault();
            if (placeholder != null) targetList.Children.Remove(placeholder);

            var row = new Grid { Margin = new Thickness(8, 5, 8, 5) };

            if (fullCols)
            {
                // Required / Available: Group | Mode | End user notifications | Availability | Deadline | Delivery opt | ✕
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Group
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });                   // Mode
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });                  // End user notifications
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });                  // Availability
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });                  // Installation deadline
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });                  // Delivery optimization
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });                   // Delete

                // Col 0 - Group name with icon
                var groupPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                groupPanel.Children.Add(new TextBlock { Text = "👥", FontSize = 11, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                var gName = new TextBlock
                {
                    Text = groupName, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                gName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                groupPanel.Children.Add(gName);
                Grid.SetColumn(groupPanel, 0);

                // Col 1 - Mode: Included / Excluded
                var modeCmb = MakeSmallCombo(new[] { "Included", "Excluded" }, 0);
                Grid.SetColumn(modeCmb, 1);

                // Col 2 - End user notifications
                var notifCmb = MakeSmallCombo(new[]
                {
                    "Show all toast notifications",
                    "Show toast notifications for computer restarts",
                    "Hide all toast notifications"
                }, 0);
                Grid.SetColumn(notifCmb, 2);

                // Col 3 - Availability
                var availCmb = MakeSmallCombo(new[] { "As soon as possible", "Specific date/time" }, 0);
                Grid.SetColumn(availCmb, 3);

                // Col 4 - Installation deadline
                var deadlineCmb = MakeSmallCombo(new[] { "As soon as possible", "Specific date/time" }, 0);
                Grid.SetColumn(deadlineCmb, 4);

                // Col 5 - Delivery optimization priority
                var deliveryCmb = MakeSmallCombo(new[] { "Background", "Foreground" }, 0);
                Grid.SetColumn(deliveryCmb, 5);

                // Col 6 - Delete
                var del = MakeDeleteButton(() =>
                {
                    targetList.Children.Remove(row);
                    if (targetList.Children.Count == 0)
                        targetList.Children.Add(MakePlaceholder("No groups added."));
                    RequestAutoSave();
                });
                Grid.SetColumn(del, 6);

                row.Children.Add(groupPanel);
                row.Children.Add(modeCmb);
                row.Children.Add(notifCmb);
                row.Children.Add(availCmb);
                row.Children.Add(deadlineCmb);
                row.Children.Add(deliveryCmb);
                row.Children.Add(del);
            }
            else
            {
                // Uninstall: Group | Mode | ✕
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

                var groupPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                groupPanel.Children.Add(new TextBlock { Text = "👥", FontSize = 11, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                var gName = new TextBlock
                {
                    Text = groupName, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                gName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                groupPanel.Children.Add(gName);
                Grid.SetColumn(groupPanel, 0);

                var modeCmb = MakeSmallCombo(new[] { "Included", "Excluded" }, 0);
                Grid.SetColumn(modeCmb, 1);

                var del = MakeDeleteButton(() =>
                {
                    targetList.Children.Remove(row);
                    if (targetList.Children.Count == 0)
                        targetList.Children.Add(MakePlaceholder("No groups added."));
                    RequestAutoSave();
                });
                Grid.SetColumn(del, 2);

                row.Children.Add(groupPanel);
                row.Children.Add(modeCmb);
                row.Children.Add(del);
            }

            targetList.Children.Add(row);
            RequestAutoSave();
        }

        // ── Small helpers ────────────────────────────────────────────
        private static ComboBox MakeSmallCombo(string[] items, int selectedIndex)
        {
            var cmb = new ComboBox
            {
                Height = 22, FontSize = 10, Margin = new Thickness(0, 0, 4, 0),
                Style = (Style)Application.Current.TryFindResource("ModernComboBoxStyle")
            };
            foreach (var item in items)
                cmb.Items.Add(new ComboBoxItem { Content = item });
            cmb.SelectedIndex = selectedIndex;
            return cmb;
        }

        private static Button MakeDeleteButton(Action onClick)
        {
            var btn = new Button
            {
                Content = "✕", Width = 20, Height = 20, FontSize = 10,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Click += (_, _) => onClick();
            return btn;
        }

        private TextBlock MakePlaceholder(string text) => new TextBlock
        {
            Text = text, FontSize = 11, Margin = new Thickness(8, 6, 8, 6),
            Foreground = TC("TextMutedBrush")
        };



        // ══════════════════════════════════════════════════════════════
        // PUBLISH BUTTON STATE
        // ══════════════════════════════════════════════════════════════
        private void UpdatePublishButton()
        {
            string iwPath = (TxtIntunewinPath.Text ?? "").Trim().Trim('"');
            var missing = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(TxtAppName.Text))   missing.Add("App Name (App Info tab)");
            if (string.IsNullOrWhiteSpace(TxtPublisher.Text)) missing.Add("Publisher (App Info tab)");
            if (string.IsNullOrWhiteSpace(iwPath))            missing.Add("Intunewin file");
            else if (!File.Exists(iwPath))                    missing.Add($"Intunewin file not found on disk ({iwPath})");

            // Publish stays enabled at all times; the mandatory-field check on click decides
            // whether publishing proceeds (showing the missing-fields dialog when needed).
            BtnPublish.IsEnabled = true;
            // Keep a helpful hover hint about anything still missing.
            BtnPublish.ToolTip = missing.Count == 0
                ? "Publish to Intune"
                : "Still missing: " + string.Join("; ", missing);
            ToolTipService.SetShowOnDisabled(BtnPublish, true);
        }

        // ══════════════════════════════════════════════════════════════
        // EXPORT - collect all tab data → IntuneAppSettings.json
        // Written to the same output folder as the .intunewin file.
        // ══════════════════════════════════════════════════════════════
        public void WriteIntuneAppSettingsJson(string outputFolder,
            string? overrideAppName = null, string? overridePublisher = null, string? overrideAppVersion = null,
            string? overrideIntunewinPath = null)
        {
            try
            {
                Directory.CreateDirectory(outputFolder);

                // ── Helpers ───────────────────────────────────────────
                static string CmbVal(ComboBox c)
                    => (c.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? "";

                string TxtVal(TextBox? t)
                    => t?.Text.Trim() ?? "";

                // ── Detection rules → collapsed below into the flat doc (msiRule/fileRule/regRule). ──
                // (The dependencies & supersedence lists are still collected below.)

                // ── Tab 5: Dependencies ───────────────────────────────
                var dependencies = new List<Dictionary<string, string>>();
                foreach (var child in DependenciesList.Children)
                {
                    if (child is not Grid g) continue;
                    string name = "", autoInstall = "";
                    string appId = (g.Tag as string) ?? "";   // Intune app id kept on the row
                    foreach (UIElement el in g.Children)
                    {
                        if (el is TextBlock tb && Grid.GetColumn(tb) == 0) name = tb.Text.Trim();
                        if (el is StackPanel sp)
                        {
                            var rbYes = sp.Children.OfType<RadioButton>()
                                .FirstOrDefault(r => r.Content?.ToString()?.Contains("Yes") == true);
                            autoInstall = rbYes?.IsChecked == true ? "Yes" : "No";
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(name))
                        dependencies.Add(new Dictionary<string, string>
                            { ["AppName"] = name, ["AppID"] = appId, ["AutoInstall"] = autoInstall });
                }

                // ── Tab 6: Supersedence ───────────────────────────────
                var supersedence = new List<Dictionary<string, string>>();
                foreach (var child in SupersedenceList.Children)
                {
                    if (child is not Grid g) continue;
                    string name = "", uninstall = "";
                    string appId = (g.Tag as string) ?? "";   // Intune app id kept on the row
                    foreach (UIElement el in g.Children)
                    {
                        if (el is TextBlock tb && Grid.GetColumn(tb) == 0) name = tb.Text.Trim();
                        if (el is StackPanel sp)
                        {
                            var rbYes = sp.Children.OfType<RadioButton>()
                                .FirstOrDefault(r => r.Content?.ToString()?.Contains("Yes") == true);
                            uninstall = rbYes?.IsChecked == true ? "Yes" : "No";
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(name))
                        supersedence.Add(new Dictionary<string, string>
                            { ["AppName"] = name, ["AppID"] = appId, ["UninstallPreviousVersion"] = uninstall });
                }

                // ── Tab 7: Assignments ────────────────────────────────
                // Group name is in a StackPanel at col 0 containing [icon TextBlock, name TextBlock]
                static List<string> ReadGroupNames(StackPanel panel)
                {
                    var groups = new List<string>();
                    foreach (var child in panel.Children)
                    {
                        if (child is not Grid g) continue;
                        foreach (UIElement el in g.Children)
                        {
                            // Col 0 holds a StackPanel with [icon, name]
                            if (el is StackPanel sp && Grid.GetColumn(sp) == 0)
                            {
                                // Second TextBlock in the StackPanel is the group name
                                var nameTb = sp.Children.OfType<TextBlock>()
                                    .Skip(1).FirstOrDefault();
                                string name = nameTb?.Text?.Trim() ?? "";
                                if (!string.IsNullOrWhiteSpace(name))
                                    groups.Add(name);
                            }
                        }
                    }
                    return groups;
                }

                var availableGroups = ReadGroupNames(AvailableList);

                // ── Assemble full document in the CANONICAL FLAT schema ───────────────
                // Key names/order match the AutoPackage per-app JSON exactly so that both
                // features (Auto Package → Publish tab, and this Publish & Deploy feature)
                // produce identical key sets and can read each other's files.
                //
                // Fields the Publish & Deploy GUI does not yet expose (UAT users, App Owner
                // email, Send mail, Deployment type, MSI product code, file date/time/size,
                // and Dependency/Supersedence slots beyond what the list panels hold) are
                // emitted with empty/default values; they get real GUI controls in the
                // GUI-realignment stage.
                static string B(bool b) => b ? "Yes" : "No";

                // Collapse the rule list to the single MSI / File / Registry rule set.
                // All three always exist; the checkbox (rule.Enabled) decides inclusion.
                DetectionRule? msiRule  = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("MSI",      StringComparison.OrdinalIgnoreCase));
                DetectionRule? fileRule = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("File",     StringComparison.OrdinalIgnoreCase));
                DetectionRule? regRule  = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("Registry", StringComparison.OrdinalIgnoreCase));
                bool msiOn  = msiRule?.Enabled  == true;
                bool fileOn = fileRule?.Enabled == true;
                bool regOn  = regRule?.Enabled  == true;
                bool useScript = RbDetectScript.IsChecked == true;

                var archParts = new System.Collections.Generic.List<string>();
                if (ChkX86.IsChecked == true)   archParts.Add("32Bit");
                if (ChkX64.IsChecked == true)   archParts.Add("64Bit");
                if (ChkArm64.IsChecked == true) archParts.Add("Arm64");
                string archCanonical = string.Join(",", archParts);

                string scriptEnforce = (RbSigYes?.IsChecked == true) ? "Yes" : "No";
                string scriptRun32   = (RbScript32Yes?.IsChecked == true) ? "Yes" : "No";

                // Route the File detection value into the matching canonical key, following the
                // AutoPackage convention: Size→SizeValue, Date modified/created→DateValue,
                // version/other→DetectionFileValue. Methods that need no comparison value
                // ("File or folder exists") emit NO operator and NO value keys.
                string fileMethod = fileOn ? (fileRule?.FileCheck ?? "") : "";
                string fileVal    = fileOn ? (fileRule?.FileValue ?? "") : "";
                bool   fileIsSize = fileMethod.Equals("Size in MB", StringComparison.OrdinalIgnoreCase);
                bool   fileIsDate = fileMethod.StartsWith("Date", StringComparison.OrdinalIgnoreCase);
                bool   fileNeedsValue = fileOn && !string.IsNullOrWhiteSpace(fileMethod)
                                     && !fileMethod.Equals("File or folder exists", StringComparison.OrdinalIgnoreCase);
                string fileValueKey = (fileNeedsValue && !fileIsSize && !fileIsDate) ? fileVal : "";
                string fileSizeKey  = (fileNeedsValue && fileIsSize) ? fileVal : "";
                string fileDateKey  = (fileNeedsValue && fileIsDate) ? fileVal : "";
                string fileTimeKey  = (fileNeedsValue && fileIsDate) ? (fileRule?.FileTimeValue ?? "") : "";
                string fileDateUtc  = (fileNeedsValue && fileIsDate) ? BuildDetectionFileDateUtc(fileMethod, fileDateKey, fileTimeKey) : "";

                // Registry presence checks (Key/Value exists / does not exist) need no operator
                // or comparison value either.
                string regMethod = regOn ? (regRule?.RegCheck ?? "") : "";
                bool   regNeedsValue = regOn && !string.IsNullOrWhiteSpace(regMethod)
                                    && !regMethod.Equals("Key exists",           StringComparison.OrdinalIgnoreCase)
                                    && !regMethod.Equals("Key does not exist",   StringComparison.OrdinalIgnoreCase)
                                    && !regMethod.Equals("Value exists",         StringComparison.OrdinalIgnoreCase)
                                    && !regMethod.Equals("Value does not exist", StringComparison.OrdinalIgnoreCase);

                // MSI operator/value only apply when the version check is on.
                bool msiVerCheckOn = msiOn && (msiRule?.MsiCheck?.Equals("Version comparison", StringComparison.OrdinalIgnoreCase) == true
                                            || msiRule?.MsiCheck?.Equals("Check version",      StringComparison.OrdinalIgnoreCase) == true);

                var doc = new System.Collections.Specialized.OrderedDictionary
                {
                    ["IntuneAppName"]                 = overrideAppName    ?? TxtVal(TxtAppName),
                    ["IntuneAppVendor"]               = overridePublisher  ?? TxtVal(TxtPublisher),
                    ["IntuneAppVersion"]              = overrideAppVersion ?? TxtVal(TxtAppVersion),
                    ["IntuneCategory"]                = CmbVal(CmbCategory),
                    ["FeaturedApp"]                   = CmbVal(CmbFeatured),
                    ["Description"]                   = TxtVal(TxtDescription),
                    ["Developer"]                     = TxtVal(TxtDeveloper),
                    ["Owner"]                         = TxtVal(TxtOwner),
                    ["InformationURL"]                = TxtVal(TxtInfoUrl),
                    ["PrivacyURL"]                    = TxtVal(TxtPrivacyUrl),
                    ["Notes"]                         = TxtVal(TxtNotes),
                    ["Logo"]                          = TxtVal(TxtIconPath),
                    ["InstallCommand"]                = TxtVal(TxtInstallCmd),
                    ["UninstallCommand"]              = TxtVal(TxtUninstallCmd),
                    ["AllowAvailableUninstall"]       = RbUninstallYes.IsChecked == true ? "Yes" : "No",
                    ["InstallBehavior"]               = RbInstallUser.IsChecked == true ? "User" : "System",
                    ["InstallationTime"]              = TxtVal(TxtInstallTime),
                    ["DeviceRestartBehaviour"]        = CmbVal(CmbRestartBehavior),
                    ["MinimumOperatingSystem"]        = CmbVal(CmbMinOs),
                    ["OperatingSystemArchitecture"]   = archCanonical,
                    ["RulesFormat"]                   = useScript ? "Detection script" : "Manual detection rules",
                    ["DetectionTypeMSI"]              = B(msiOn),
                    ["DetectionTypeFile"]             = B(fileOn),
                    ["DetectionTypeRegistry"]         = B(regOn),
                    ["DetectionMsiProductCode"]       = msiOn ? (msiRule?.MsiProductCode ?? "") : "",
                    ["DetectionMsiVersionCheck"]      = msiVerCheckOn ? "Yes" : "No",
                    ["DetectionMsiOperator"]          = msiVerCheckOn ? (msiRule?.MsiOperator ?? "") : "",
                    ["DetectionMsiValue"]             = msiVerCheckOn ? (msiRule?.MsiVersion ?? "") : "",
                    ["DetectionFileFolderPath"]       = fileOn ? (fileRule?.FilePath ?? "") : "",
                    ["DetectionFileFileName"]         = fileOn ? (fileRule?.FileName ?? "") : "",
                    ["DetectionFileDetectionMethod"]  = fileOn ? (fileRule?.FileCheck ?? "") : "",
                    ["DetectionFileOperator"]         = fileNeedsValue ? (fileRule?.FileOperator ?? "") : "",
                    ["DetectionFileValue"]            = fileValueKey,
                    ["DetectionFileDateValue"]        = fileDateKey,
                    ["DetectionFileTimeValue"]        = fileTimeKey,
                    ["DetectionFileSizeValue"]        = fileSizeKey,
                    ["DetectionFileDateUtc"]          = fileDateUtc,
                    ["DetectionFile32Bit"]            = fileOn ? B(fileRule!.File32Bit) : "No",
                    ["DetectionRegKeyPath"]           = regOn ? (regRule?.RegKeyPath ?? "") : "",
                    ["DetectionRegValueName"]         = regOn ? (regRule?.RegValue ?? "") : "",
                    ["DetectionRegDetectionMethod"]   = regOn ? (regRule?.RegCheck ?? "") : "",
                    ["DetectionRegOperator"]          = regNeedsValue ? (regRule?.RegOperator ?? "") : "",
                    ["DetectionRegistryValue"]        = regNeedsValue ? (regRule?.RegValueData ?? "") : "",
                    ["DetectionReg32Bit"]             = regOn ? B(regRule!.Reg32Bit) : "No",
                    ["DetectionScriptFile"]           = useScript ? TxtVal(TxtDetectScriptPath) : "",
                    ["DetectionScriptEnforceSignatureCheck"] = scriptEnforce,
                    ["DetectionScriptRunAs32Bit"]     = scriptRun32,
                    ["DeploymentType"]                = "",
                    ["AssignmentsAvailable"]          = string.Join(";", availableGroups),
                    ["UATUsers"]                      = "",
                    ["AppOwnerEmail"]                 = "",
                    ["SendMailToAppOwner"]            = "No",
                };

                // Dependencies - dynamic count. Keys match the AutoPackage per-app JSON
                // (DependencyAppName{n}/DependencyAppID{n}/DependencyAutoInstall{n}). Nothing is
                // written when the list is empty, so the file carries dependency keys only after
                // the user adds one on the Dependencies tab.
                for (int i = 0; i < dependencies.Count; i++)
                {
                    int n = i + 1;
                    doc[$"DependencyAppName{n}"]     = dependencies[i]["AppName"];
                    doc[$"DependencyAppID{n}"]       = dependencies[i]["AppID"];
                    doc[$"DependencyAutoInstall{n}"] = dependencies[i]["AutoInstall"];
                }
                // Supersedences - dynamic count. Keys match the AutoPackage per-app JSON
                // (SupersedenceAppName{n}/SupersedenceAppID{n}/SupersedenceUninstallPreviousVersion{n}).
                // Nothing is written when the list is empty, so the file carries supersedence keys
                // only after the user adds one on the Supersedence tab.
                for (int i = 0; i < supersedence.Count; i++)
                {
                    int n = i + 1;
                    doc[$"SupersedenceAppName{n}"]                  = supersedence[i]["AppName"];
                    doc[$"SupersedenceAppID{n}"]                    = supersedence[i]["AppID"];
                    doc[$"SupersedenceUninstallPreviousVersion{n}"] = supersedence[i]["UninstallPreviousVersion"];
                }

                doc["IntunewinFilePath"] = overrideIntunewinPath ?? TxtVal(TxtIntunewinPath);

                string json = JsonSerializer.Serialize(doc,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        // Emit embedded quotes as \" instead of \u0022 - matches the
                        // AutoPackage per-app JSON and keeps the file human-readable.
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });

                // JSON file name: match the .intunewin base name so the companion file is
                // e.g. "VideoLAN_VLCmediaplayer_3.0.23_02.json". Prefer the explicit
                // overrideIntunewinPath (passed when exporting straight from the Create
                // Intunewin flow, where the Publish tab's TxtIntunewinPath is still empty),
                // then the Publish tab textbox, and only fall back to the generic name when
                // neither is available.
                string intunewinFile = !string.IsNullOrWhiteSpace(overrideIntunewinPath)
                    ? overrideIntunewinPath!
                    : TxtVal(TxtIntunewinPath);
                string jsonBaseName  = !string.IsNullOrWhiteSpace(intunewinFile)
                    ? Path.GetFileNameWithoutExtension(intunewinFile)
                    : "IntuneAppSettings";

                string outPath = Path.Combine(outputFolder, jsonBaseName + ".json");
                File.WriteAllText(outPath, json, System.Text.Encoding.UTF8);
            }
            catch { /* silently ignore */ }
        }

        // ══════════════════════════════════════════════════════════════
        // AUTO-FILL FROM INTUNEWIN - triggered when path is set
        // Checks for companion <name>.json → loads all tabs from it,
        // or shows a warning popup if the JSON is not found.
        // ══════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════
        // All data tabs (1-7) are always available so the user can fill in
        // App Info, Program, Requirements, etc. before a .intunewin file is
        // loaded. The Log tab (8) is always enabled too.
        private void UpdateTabsEnabled()
        {
            BtnTab1.IsEnabled = true; BtnTab2.IsEnabled = true; BtnTab3.IsEnabled = true;
            BtnTab4.IsEnabled = true; BtnTab5.IsEnabled = true; BtnTab6.IsEnabled = true;
            BtnTab7.IsEnabled = true;
            Tab1.IsEnabled = true; Tab2.IsEnabled = true; Tab3.IsEnabled = true;
            Tab4.IsEnabled = true; Tab5.IsEnabled = true; Tab6.IsEnabled = true;
            Tab7.IsEnabled = true;
        }

        private void TxtIntunewinPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Tabs are usable only when a valid .intunewin file is present - reflect that every change.
            UpdateTabsEnabled();
            // Guard against re-entrant calls (e.g. Browse sets text, Drop handler also sets it)
            if (_suppressPathChanged) return;

            string path = TxtIntunewinPath.Text.Trim();
            UpdatePublishButton();

            if (!path.EndsWith(".intunewin", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
                return;

            // Companion JSON: same folder, same base name, .json extension
            string jsonPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(path) ?? "",
                System.IO.Path.GetFileNameWithoutExtension(path) + ".json");

            if (File.Exists(jsonPath))
                LoadAllTabsFromJson(jsonPath);
            else
            {
                // No companion JSON next to this .intunewin. Clear every tab so stale
                // values from the previously loaded app do not linger, and do NOT write
                // a companion JSON automatically: suspend auto-save during the reset and
                // cancel the debounced write that setting the path just scheduled.
                _autoSaveSuspended = true;
                try
                {
                    ClearAllTabFields();
                    _defaultsLoaded = false;
                    LoadIntuneDefaults();
                }
                finally
                {
                    _autoSaveSuspended = false;
                }
                _autoSaveTimer?.Stop();

                ShowThemedMessage("Settings File Not Found", "⚠", "amber", $"{jsonPath} file is not found.");
            }

            // Once an intunewin is loaded (browse, drop, or handoff from Create Intunewin), surface
            // <packageRoot>\Documents\Icon.png in the Logo box - created by the Execute step - unless
            // a logo is already set.
            AutoPopulateLogoFromDocuments(null, path);

            UpdatePublishButton();
        }

        // ══════════════════════════════════════════════════════════════
        // LOAD ALL TABS FROM JSON - reads companion <name>.json and
        // populates every tab in Publish & Deploy.
        // ══════════════════════════════════════════════════════════════
        private void LoadAllTabsFromJson(string jsonPath)
        {
            _autoSaveSuspended = true;
            try
            {
                string raw = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
                using var doc  = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                string S(JsonElement el, string key)
                    => el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                       ? (p.GetString() ?? "") : "";
                string R(string key) => S(root, key);                 // root-level string
                bool RYes(string key) => R(key).Equals("Yes", StringComparison.OrdinalIgnoreCase);

                static void SelectCombo(ComboBox cmb, string value)
                {
                    if (string.IsNullOrWhiteSpace(value)) return;
                    foreach (ComboBoxItem item in cmb.Items)
                        if (string.Equals(item.Content?.ToString(), value,
                                          StringComparison.OrdinalIgnoreCase))
                        { cmb.SelectedItem = item; return; }
                }

                // Canonical FLAT schema reader - keys match the AutoPackage per-app JSON and the
                // WriteIntuneAppSettingsJson output exactly, so write→read round-trips.

                // ── Tab 1: App Info ───────────────────────────────────
                TxtAppName.Text     = R("IntuneAppName");
                TxtPublisher.Text   = R("IntuneAppVendor");
                TxtAppVersion.Text  = R("IntuneAppVersion");
                SelectCombo(CmbCategory, R("IntuneCategory"));
                SelectCombo(CmbFeatured, R("FeaturedApp"));
                TxtDescription.Text = R("Description");
                TxtDeveloper.Text   = R("Developer");
                TxtOwner.Text       = R("Owner");
                TxtInfoUrl.Text     = R("InformationURL");
                TxtPrivacyUrl.Text  = R("PrivacyURL");
                TxtNotes.Text       = R("Notes");
                TxtIconPath.Text    = R("Logo");

                // ── Tab 2: Program ────────────────────────────────────
                TxtInstallCmd.Text   = R("InstallCommand");
                TxtUninstallCmd.Text = R("UninstallCommand");
                TxtInstallTime.Text  = R("InstallationTime");

                RbUninstallYes.IsChecked = RYes("AllowAvailableUninstall");
                RbUninstallNo.IsChecked  = !RbUninstallYes.IsChecked;

                string behavior = R("InstallBehavior");
                RbInstallUser.IsChecked   = behavior.Equals("User", StringComparison.OrdinalIgnoreCase);
                RbInstallSystem.IsChecked = !RbInstallUser.IsChecked;

                SelectCombo(CmbRestartBehavior, R("DeviceRestartBehaviour"));

                // ── Tab 3: Requirements ───────────────────────────────
                string arch = R("OperatingSystemArchitecture");
                ChkX86.IsChecked   = arch.Contains("32",  StringComparison.OrdinalIgnoreCase);
                ChkX64.IsChecked   = arch.Contains("64",  StringComparison.OrdinalIgnoreCase);
                ChkArm64.IsChecked = arch.Contains("ARM", StringComparison.OrdinalIgnoreCase);
                SelectCombo(CmbMinOs, R("MinimumOperatingSystem"));

                // ── Tab 4: Detection - populate the three persistent rules from flat fields ──
                _detectionRules.Clear();
                var msiR = EnsureRule("MSI");
                var fileR = EnsureRule("File");
                var regR = EnsureRule("Registry");
                bool useScript = R("RulesFormat").IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0;

                RbDetectRules.IsChecked  = !useScript;
                RbDetectScript.IsChecked = useScript;
                if (useScript)
                {
                    TxtDetectScriptPath.Text = R("DetectionScriptFile");
                    if (RbSigYes != null)      RbSigYes.IsChecked      = RYes("DetectionScriptEnforceSignatureCheck");
                    if (RbSigNo  != null)      RbSigNo.IsChecked       = !RYes("DetectionScriptEnforceSignatureCheck");
                    if (RbScript32Yes != null) RbScript32Yes.IsChecked = RYes("DetectionScriptRunAs32Bit");
                    if (RbScript32No  != null) RbScript32No.IsChecked  = !RYes("DetectionScriptRunAs32Bit");
                }

                msiR.Enabled        = RYes("DetectionTypeMSI");
                msiR.MsiProductCode = R("DetectionMsiProductCode");
                msiR.MsiCheck       = RYes("DetectionMsiVersionCheck") ? "Version comparison" : "Not configured";
                msiR.MsiOperator    = R("DetectionMsiOperator");
                msiR.MsiVersion     = R("DetectionMsiValue");

                fileR.Enabled      = RYes("DetectionTypeFile");
                fileR.FilePath     = R("DetectionFileFolderPath");
                fileR.FileName     = R("DetectionFileFileName");
                fileR.FileCheck    = R("DetectionFileDetectionMethod");
                fileR.FileOperator = R("DetectionFileOperator");
                fileR.FileValue    = fileR.FileCheck.Equals("Size in MB", StringComparison.OrdinalIgnoreCase) ? R("DetectionFileSizeValue")
                                   : fileR.FileCheck.StartsWith("Date", StringComparison.OrdinalIgnoreCase)    ? R("DetectionFileDateValue")
                                   : R("DetectionFileValue");
                fileR.FileTimeValue = R("DetectionFileTimeValue");
                fileR.File32Bit    = RYes("DetectionFile32Bit");

                regR.Enabled      = RYes("DetectionTypeRegistry");
                regR.RegKeyPath   = R("DetectionRegKeyPath");
                regR.RegValue     = R("DetectionRegValueName");
                regR.RegCheck     = R("DetectionRegDetectionMethod");
                regR.RegOperator  = R("DetectionRegOperator");
                regR.RegValueData = R("DetectionRegistryValue");
                regR.Reg32Bit     = RYes("DetectionReg32Bit");

                RebuildDetectionRuleUI();

                // ── Tab 5: Dependencies (DependencyAppName{n}/DependencyAppID{n}/DependencyAutoInstall{n}) ──
                DependenciesList.Children.Clear();
                DependenciesList.Children.Add(new TextBlock { Text = "No dependencies added.",
                    FontSize = 11, Margin = new Thickness(10, 7, 10, 7), Foreground = TC("TextMutedBrush") });
                for (int i = 1; i <= 50; i++)
                {
                    string depName = R($"DependencyAppName{i}");
                    string depId   = R($"DependencyAppID{i}");
                    if (string.IsNullOrWhiteSpace(depName) && string.IsNullOrWhiteSpace(depId)) continue;
                    AddDependencyRow(depName, depId, RYes($"DependencyAutoInstall{i}"));
                }

                // ── Tab 6: Supersedence (SupersedenceAppName{n}/SupersedenceAppID{n}/SupersedenceUninstallPreviousVersion{n}) ──
                SupersedenceList.Children.Clear();
                SupersedenceList.Children.Add(new TextBlock { Text = "No supersedence added.",
                    FontSize = 11, Margin = new Thickness(10, 7, 10, 7), Foreground = TC("TextMutedBrush") });
                for (int i = 1; i <= 50; i++)
                {
                    string supName = R($"SupersedenceAppName{i}");
                    string supId   = R($"SupersedenceAppID{i}");
                    if (string.IsNullOrWhiteSpace(supName) && string.IsNullOrWhiteSpace(supId)) continue;
                    AddSupersedenceRow(supName, supId, RYes($"SupersedenceUninstallPreviousVersion{i}"));
                }

                // ── Tab 7: Assignments (semicolon-separated group names) ──
                AvailableList.Children.Clear();
                AvailableList.Children.Add(new TextBlock { Text = "No groups added.", FontSize = 11,
                    Foreground = TC("TextMutedBrush"), Margin = new Thickness(8, 6, 8, 6) });

                static System.Collections.Generic.IEnumerable<string> SplitGroups(string raw)
                    => (raw ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(x => x.Trim()).Where(x => x.Length > 0);

                foreach (var grp in SplitGroups(R("AssignmentsAvailable")))
                    AddAssignmentRow(AvailableList, "Available", fullCols: true, fixedName: grp);

                _defaultsLoaded = true;
            }
            catch (Exception ex)
            {
                ShowThemedMessage("Load Error", "⛔", "red", $"Failed to load settings from JSON:\n{ex.Message}");
            }
            finally { _autoSaveSuspended = false; }
        }

        // ── Add dependency row programmatically ───────────────────────
        // appId is the Intune application id chosen in the picker; it is kept on the
        // row's Tag so the save path can emit DependencyAppID{n} (matching the
        // AutoPackage per-app JSON). It is "" for rows without a known id.
        private void AddDependencyRow(string appName, string appId, bool autoInstall)
        {
            var placeholder = DependenciesList.Children.OfType<TextBlock>().FirstOrDefault();
            if (placeholder != null) DependenciesList.Children.Remove(placeholder);
            var row = new Grid { Margin = new Thickness(10, 5, 10, 5), Tag = appId ?? "" };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var nb = new TextBlock { Text = appName, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            nb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(nb, 0);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string grp = "DepAuto_" + DependenciesList.Children.Count;
            var rbNo  = new RadioButton { Content = "No (default)", FontSize = 11, IsChecked = !autoInstall, GroupName = grp, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0,0,12,0) };
            rbNo.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimaryBrush");
            var rbYes = new RadioButton { Content = "Yes", FontSize = 11, IsChecked = autoInstall, GroupName = grp, Cursor = System.Windows.Input.Cursors.Hand };
            rbYes.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(rbNo); panel.Children.Add(rbYes);
            Grid.SetColumn(panel, 1);
            var del = new Button { Content = "✕", Width = 20, Height = 20, FontSize = 10, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            del.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
            var r = row;
            del.Click += (_, _) => { DependenciesList.Children.Remove(r); if (DependenciesList.Children.Count == 0) { var tb = new TextBlock { Text = "No dependencies added.", FontSize = 11, Margin = new Thickness(10,7,10,7) }; tb.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush"); DependenciesList.Children.Add(tb); } };
            Grid.SetColumn(del, 2);
            row.Children.Add(nb); row.Children.Add(panel); row.Children.Add(del);
            DependenciesList.Children.Add(row);
        }

        // ── Add supersedence row programmatically ─────────────────────
        // appId is the Intune application id chosen in the picker; it is kept on the
        // row's Tag so the save path can emit SupersedenceAppID{n} (matching the
        // AutoPackage per-app JSON). It is "" for rows without a known id.
        private void AddSupersedenceRow(string appName, string appId, bool uninstallPrevious)
        {
            var placeholder = SupersedenceList.Children.OfType<TextBlock>().FirstOrDefault();
            if (placeholder != null) SupersedenceList.Children.Remove(placeholder);
            var row = new Grid { Margin = new Thickness(10, 5, 10, 5), Tag = appId ?? "" };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var nb = new TextBlock { Text = appName, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            nb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(nb, 0);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string grp = "SupUninst_" + SupersedenceList.Children.Count;
            var rbNo  = new RadioButton { Content = "No (Update)",  FontSize = 11, IsChecked = !uninstallPrevious, GroupName = grp, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0,0,14,0) };
            rbNo.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimaryBrush");
            var rbYes = new RadioButton { Content = "Yes (Replace)", FontSize = 11, IsChecked = uninstallPrevious, GroupName = grp, Cursor = System.Windows.Input.Cursors.Hand };
            rbYes.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(rbNo); panel.Children.Add(rbYes);
            Grid.SetColumn(panel, 1);
            var del = new Button { Content = "✕", Width = 20, Height = 20, FontSize = 10, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            del.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
            var r = row;
            del.Click += (_, _) => { SupersedenceList.Children.Remove(r); if (SupersedenceList.Children.Count == 0) { var tb = new TextBlock { Text = "No supersedence added.", FontSize = 11, Margin = new Thickness(10,7,10,7) }; tb.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush"); SupersedenceList.Children.Add(tb); } };
            Grid.SetColumn(del, 2);
            row.Children.Add(nb); row.Children.Add(panel); row.Children.Add(del);
            SupersedenceList.Children.Add(row);
        }

        // ── Intunewin metadata container ─────────────────────────────
        private record IntunewinMeta(
            string Name, string Publisher, string Version,
            string MsiProductCode,
            string InstallFolder, string MainExe,
            string RegistryKeyPath);

        /// <summary>
        /// Opens the .intunewin ZIP and reads Detection.xml at the root.
        /// Handles both element name conventions used by IntuneWinAppUtil:
        ///   <Name> and legacy <n> for the app name.
        /// Namespace: http://schemas.microsoft.com/IntuneWin32App
        /// </summary>
        private static IntunewinMeta ReadIntunewinMetadata(string intunewinPath)
        {
            string name = "", publisher = "", version = "",
                   msiCode = "", installFolder = "", mainExe = "", regKey = "";
            try
            {
                using var zip = ZipFile.OpenRead(intunewinPath);

                var entry = zip.Entries.FirstOrDefault(en =>
                    en.Name.Equals("Detection.xml", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    return new IntunewinMeta(name, publisher, version, msiCode, installFolder, mainExe, regKey);

                using var stream = entry.Open();
                var doc = XDocument.Load(stream);
                var root = doc.Root;
                if (root == null)
                    return new IntunewinMeta(name, publisher, version, msiCode, installFolder, mainExe, regKey);

                // Namespace used by IntuneWinAppUtil
                XNamespace ns = "http://schemas.microsoft.com/IntuneWin32App";

                // Helper: get element value, try with namespace first, then without
                string Elem(XElement parent, string tagName)
                {
                    return parent.Element(ns + tagName)?.Value?.Trim()
                        ?? parent.Element(tagName)?.Value?.Trim()
                        ?? "";
                }

                // App name - IntuneWinAppUtil uses <Name>; some older versions use <n>
                name      = Elem(root, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    name  = Elem(root, "n");
                publisher = Elem(root, "Publisher");
                version   = Elem(root, "Version");

                // SetupFile → use as exe hint if it's an .exe
                string setupFile = Elem(root, "SetupFile");
                if (!string.IsNullOrWhiteSpace(setupFile) &&
                    setupFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    mainExe = Path.GetFileName(setupFile);

                // MSI-specific info
                var msiInfo = root.Element(ns + "MsiInfo") ?? root.Element("MsiInfo");
                if (msiInfo != null)
                {
                    msiCode       = Elem(msiInfo, "MsiProductCode");
                    installFolder = Elem(msiInfo, "MsiInstallLocation");
                    if (string.IsNullOrWhiteSpace(installFolder))
                        installFolder = Elem(msiInfo, "MsiInstallDir");

                    // If version missing at top level, try MsiProductVersion
                    if (string.IsNullOrWhiteSpace(version))
                        version = Elem(msiInfo, "MsiProductVersion");

                    // Build standard uninstall registry key from product code
                    if (!string.IsNullOrWhiteSpace(msiCode))
                        regKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + msiCode;
                }
            }
            catch { /* silently skip on parse failure */ }

            return new IntunewinMeta(name, publisher, version,
                msiCode, installFolder, mainExe, regKey);
        }

        // ══════════════════════════════════════════════════════════════
        // PUBLISH TO INTUNE
        // ══════════════════════════════════════════════════════════════
        private async void BtnPublish_Click(object sender, RoutedEventArgs e)
        {
            // ── Publish via PublishPackage.ps1 (same engine as Auto Package) ──
            string intunewinPath = TxtIntunewinPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(intunewinPath) || !File.Exists(intunewinPath))
            {
                ShowThemedMessage("File not found", "⚠", "amber", "The .intunewin file was not found.\nPlease check the path.");
                return;
            }

            // 1. FIRST ACTION: save the per-app json so what is published == what is on screen.
            string outputFolder = Path.GetDirectoryName(intunewinPath) ?? "";
            WriteIntuneAppSettingsJson(outputFolder);
            string jsonPath = Path.ChangeExtension(intunewinPath, ".json");
            if (!File.Exists(jsonPath))
            {
                ShowThemedMessage("JSON missing", "⚠", "amber", "Per-app settings JSON could not be written:\n" + jsonPath);
                return;
            }

            // 2. Mandatory-field validation - list every missing field by tab.
            var missing = CollectMissingMandatoryFields();
            if (missing.Count > 0)
            {
                ShowMandatoryFieldsDialog(missing);
                return;
            }

            // 3. Resolve the publish script + Intune credentials.
            if (!TryResolveIntuneCreds(out string scriptPath, out string tenantId,
                                       out string clientId))
                return;

            // Require an interactive sign-in before publishing. Without one the publish would
            // just fail with an auth error, so block here with a clear message instead.
            bool sessionReady = _intuneSessionConnected && _psSession != null && _psSession.IsAlive;
            if (!sessionReady)
            {
                ShowThemedMessage("Sign in required", "\u26a0", "amber",
                    "You are not signed in to Intune.\n\nSign in once, then publish.");
                return;
            }

            // Confirm before publishing to Intune.
            if (!ShowThemedConfirm("Publish", "🚀", "blue",
                    $"Are you sure you want to publish \"{TxtAppName.Text.Trim()}\" to Intune?",
                    "Publish", "Cancel"))
                return;

            // No-secret / no-relogin: if signed in via the persistent session, publish inside
            // that same session so the session-bound token stays in the process that owns it.
            if (sessionReady)
            {
                // Duplicate check: if the app (name, or name+version) already exists in Intune,
                // offer to rename / re-version before publishing a separate copy.
                ShowTab(9);
                AppendLog("Checking Intune for an existing application...");
                bool dup = await CheckDuplicateInSessionAsync(scriptPath, jsonPath, tenantId, clientId);
                if (dup)
                {
                    string curName = TxtAppName.Text.Trim();
                    string curVer  = TxtAppVersion.Text.Trim();
                    bool hasVersion = !string.IsNullOrWhiteSpace(curVer);

                    // Suggest a unique name. If "<name>_Test" already exists, suggest
                    // "<name>_Test1", then "_Test2", and so on.
                    var existingApps = await FetchIntuneAppsAsync();
                    var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (existingApps != null)
                        foreach (var a in existingApps) existingNames.Add(a.Name);
                    string suggestedName = NextTestName(curName, existingNames);

                    if (!ShowDuplicateRenameDialog(suggestedName, hasVersion ? curVer : null,
                                                   out string newName, out string newVer))
                    {
                        AppendLog("Publish cancelled - the application already exists in Intune.");
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(newName)) TxtAppName.Text = newName;
                    if (hasVersion && !string.IsNullOrWhiteSpace(newVer)) TxtAppVersion.Text = newVer;
                    WriteIntuneAppSettingsJson(outputFolder);   // re-save with the new name / version
                }
                await PublishViaSessionAsync(scriptPath, jsonPath, tenantId, clientId, TxtAppName.Text.Trim());
                return;
            }

        }

        // ── Intune sign-in token reuse ────────────────────────────────────────
        private string _intuneAccessToken = "";
        private long   _intuneTokenExpiresUnix = 0;

        private bool HasValidIntuneToken =>
            !string.IsNullOrEmpty(_intuneAccessToken) &&
            DateTimeOffset.FromUnixTimeSeconds(_intuneTokenExpiresUnix) > DateTimeOffset.UtcNow.AddMinutes(2);

        // Resolves the publish script path and Intune TenantId/ClientId from IntuneSettings.json.
        // Shows a themed error and returns false if the script or required TenantId/ClientId are missing.
        private bool TryResolveIntuneCreds(out string scriptPath, out string tenantId,
                                           out string clientId)
        {
            scriptPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Smekal", "SwiftPack Studio", "Templates", "PublishPackage.ps1");
            tenantId = ""; clientId = "";

            if (!File.Exists(scriptPath))
            {
                ShowThemedMessage("Script missing", "\u26d4", "red", "Publish script not found:\n" + scriptPath +
                    "\n\nPlace PublishPackage.ps1 in the Templates folder.");
                return false;
            }

            string settingsProblem = "";
            if (!File.Exists(SettingsPath))
                settingsProblem = "Settings file not found:\n" + SettingsPath;
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                    var root = doc.RootElement;
                    string S(string k) => root.TryGetProperty(k, out var p) &&
                        p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";
                    tenantId = S("TenantId");
                    clientId = S("ClientId");
                }
                catch (Exception ex)
                {
                    settingsProblem = "Settings file is INVALID JSON (check for raw quotes):\n" +
                                      SettingsPath + "\n" + ex.Message;
                }
            }

            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
            {
                string detail = !string.IsNullOrWhiteSpace(settingsProblem)
                    ? settingsProblem + "\n\n"
                    : $"Settings file was read OK but values are blank:\n{SettingsPath}\n" +
                      $"  TenantId = \"{tenantId}\"\n  ClientId = \"{clientId}\"\n\n";
                ShowThemedMessage("Missing credentials", "\u26a0", "amber", "TenantId or ClientId is not configured.\n\n" + detail +
                    "Set TenantId and ClientId in Templates\\IntuneSettings.json.");
                return false;
            }
            return true;
        }

        // ── Persistent PowerShell session ───────────────────────────────────────────────
        // The session type and the app-wide connection now live in the shared IntuneSession
        // (IntuneSession.cs), so sign-in is shared across the app and the Auto Package pickers
        // reuse the same connection.


        // Session state now lives in the shared IntuneSession so sign-in is app-wide and
        // the Auto Package pickers reuse the same connection. These read-only views keep the
        // rest of this page's existing session checks and publish calls working unchanged.
        private PsSession? _psSession => IntuneSession.Session;
        private bool _intuneSessionConnected => IntuneSession.IsConnected;
        private int _signInGen = 0;
        // True while an interactive sign-in is running, so the window watcher and re-checks know
        // an attempt is in flight.
        private volatile bool _signInInProgress = false;

        // Ensures the IntuneWin32App module is present before it is imported.
        //
        // Install-Module on a machine that has never used PowerShellGet stops to ask for the
        // NuGet provider ("NuGet provider is required to continue" / [Y] Yes [N] No). This
        // session is hidden and non-interactive, so that question can never be answered: the
        // install never happens and Import-Module then fails with "no valid module file was
        // found in any module directory" - which is the sign-in failure being reported.
        // Bootstrapping the provider first is what makes the same install unattended.
        //
        // TLS 1.2 is forced because Windows PowerShell 5.1 still negotiates TLS 1.0 by default
        // and the PowerShell Gallery refuses it, which shows up as an unrelated-looking
        // "unable to resolve package source" error.
        //
        // Runs on every sign-in but costs one Get-Module lookup once the module is installed.
        //
        // BUNDLED COPY FIRST: InstallModules.cmd (run by the installer's psmodules task or the
        // Settings button) saves the module into Templates\Modules beside the app. Loading that
        // copy by FULL MANIFEST PATH is preferred over everything below - no PSModulePath scan
        // (which hydrates a OneDrive-redirected profile Modules folder), no per-user duplicate,
        // pinned version. Without this, a fresh machine that had just bundled the modules still
        // Gallery-installed a second copy into the user profile on first sign-in. The Gallery
        // path below is unchanged and remains the fallback when the bundle is absent.
        private static string EnsureIntuneModuleCmd
        {
            get
            {
                string bundleRoot = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Templates", "Modules", "IntuneWin32App")
                    .Replace("'", "''");
                return
                    "$__bundle = '" + bundleRoot + "'; $__bm = $null; " +
                    "if (Test-Path -LiteralPath $__bundle) { " +
                        "$__bm = Get-ChildItem -LiteralPath $__bundle -Directory -ErrorAction SilentlyContinue | ForEach-Object { " +
                            "$__v = $null; " +
                            "if ([version]::TryParse($_.Name, [ref]$__v)) { " +
                                "$__p = Join-Path $_.FullName 'IntuneWin32App.psd1'; " +
                                "if (Test-Path -LiteralPath $__p) { [pscustomobject]@{ V = $__v; P = $__p } } " +
                            "} " +
                        "} | Sort-Object -Property V -Descending | Select-Object -First 1 " +
                    "}; " +
                    "if ($__bm) { " +
                        "Import-Module $__bm.P -Force; " +
                        "Write-Output ('IntuneWin32App ' + $__bm.V + ' loaded from Templates\\Modules'); " +
                    "} else { " +
                    "if (-not (Get-Module -ListAvailable -Name IntuneWin32App)) { " +
                        "Write-Output 'IntuneWin32App module not found - installing it now (one time, this can take a minute)...'; " +
                        "try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }; " +
                        "if (-not (Get-PackageProvider -Name NuGet -ListAvailable -ErrorAction SilentlyContinue)) { " +
                            "Write-Output 'Installing the NuGet package provider...'; " +
                            "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Scope CurrentUser -Force -Confirm:$false | Out-Null " +
                        "}; " +
                        // The inbox PowerShellGet 1.0.0.1 on fresh machines does not know
                        // -AcceptLicense and throws "A parameter cannot be found"; pass it only
                        // when the installed PowerShellGet is new enough (>= 1.6.0).
                        "$__psg = Get-Module -ListAvailable -Name PowerShellGet | Sort-Object -Property Version -Descending | Select-Object -First 1; " +
                        "$__ia = @{ Name='IntuneWin32App'; Scope='CurrentUser'; Force=$true; AllowClobber=$true; Confirm=$false }; " +
                        "if ($__psg -and $__psg.Version -ge [version]'1.6.0') { $__ia['AcceptLicense'] = $true }; " +
                        "Install-Module @__ia; " +
                        "Write-Output 'IntuneWin32App module installed.' " +
                    "}; " +
                    "}; ";
            }
        }

        // "Sign in to Intune" - connects once inside a persistent PowerShell session so every
        // later publish reuses that in-process connection (no secret, no re-login).
        // Called by the MainWindow top-bar "Sign in to Intune" button (works from any page).
        // Appends a timestamped line to %temp%\Sign in to intune.log so the outcome of each
        // Intune sign-in / sign-out (success or error detail) is recorded on disk. Best-effort:
        // a logging failure never affects the sign-in itself.
        private static void WriteSignInLog(string line)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "Sign in to intune.log");
                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + line + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // Watches the Microsoft sign-in browser window for THIS attempt. It grabs the window by
        // its "Sign in to your account" title once it opens, remembers that exact handle, and if
        // that window is closed before the sign-in completes, fails the attempt immediately (a
        // "Signed in to Intune failed" dialog) instead of waiting on the PowerShell timeout.
        // Safe: tracks the specific handle (so page/title changes during the flow never trip it),
        // and generation / in-progress / IsConnected re-checks plus a grace period ensure a
        // sign-in that just succeeded is never failed by mistake.
        private async System.Threading.Tasks.Task MonitorSignInWindowAsync(int myGen)
        {
            try
            {
                IntPtr authWnd = IntPtr.Zero;
                for (int i = 0; i < 60 && authWnd == IntPtr.Zero; i++)   // up to ~30s for it to open
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    if (myGen != _signInGen || !_signInInProgress) return;
                    authWnd = FindAuthWindow();
                }
                if (authWnd == IntPtr.Zero) return;   // never found it - do not risk a false failure

                while (true)
                {
                    await System.Threading.Tasks.Task.Delay(600);
                    if (myGen != _signInGen || !_signInInProgress || IntuneSession.IsConnected) return;
                    if (!IsWindow(authWnd)) break;   // sign-in window was closed
                }

                // Grace, in case the window closed the instant sign-in completed.
                await System.Threading.Tasks.Task.Delay(1500);
                if (myGen != _signInGen || !_signInInProgress || IntuneSession.IsConnected) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (myGen != _signInGen || !_signInInProgress || IntuneSession.IsConnected) return;
                    _signInGen++;                    // supersede the stuck attempt
                    _signInInProgress = false;
                    try { IntuneSession.StartFresh(); } catch { }   // dispose the stuck PowerShell/browser wait
                    IntuneSession.SetConnected(false);
                    BtnPublish.IsEnabled = true;
                    IntuneSignInBusyChanged?.Invoke(false);
                    WriteSignInLog("Sign-in failed: the sign-in window was closed before completing.");
                    ShowThemedMessage("Sign-in failed", "\u26d4", "red",
                        "Sign in to Intune was cancelled - the sign-in window was closed.",
                        "Signed in to Intune failed");
                });
            }
            catch { }
        }

        // Finds the currently open Microsoft sign-in browser window by its page title and returns
        // its handle (IntPtr.Zero if none is open).
        private static IntPtr FindAuthWindow()
        {
            IntPtr result = IntPtr.Zero;
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    int len = GetWindowTextLength(h);
                    if (len <= 0) return true;
                    var sb = new System.Text.StringBuilder(len + 2);
                    GetWindowText(h, sb, sb.Capacity);
                    if (sb.ToString().IndexOf("Sign in to your account", StringComparison.OrdinalIgnoreCase) >= 0)
                    { result = h; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return result;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        public async void ToggleIntuneSignIn()
        {
            // Already signed in: this button now signs out, so a second sign-in cannot be
            // started by accident. Signing out drops the shared in-process Intune token.
            if (IntuneSession.IsConnected)
            {
                IntuneSession.SignOut();
                SetIntuneSignedInUi(false);
                AppendLog("Signed out of Intune.");
                WriteSignInLog("Signed out of Intune.");
                ShowThemedMessage("Signed out", "\u2705", "blue", "Signed out of Intune.", "Signed out of Intune");
                return;
            }

            WriteSignInLog("=== Sign in to Intune ===");
            if (!TryResolveIntuneCreds(out string scriptPath, out string tenantId,
                                       out string clientId))
            { WriteSignInLog("Sign-in aborted: Intune credentials are not configured."); return; }

            // Sign-in has to reach the Microsoft sign-in service. Check first so that being
            // offline shows a clear message instead of opening a browser that cannot load.
            if (!await HasInternetAsync())
            {
                ShowThemedMessage("No internet connection", "\u26a0", "amber",
                    "Signing in to Intune needs an internet connection.\n\n" +
                    "Please connect to the internet and try again.");
                WriteSignInLog("Sign-in failed: no internet connection.");
                return;
            }

            // Each click starts a fresh attempt. Bumping the generation supersedes any
            // earlier attempt still waiting on a browser (e.g. one the user closed without
            // signing in) so its callbacks can no longer change connection state or the UI.
            int myGen = ++_signInGen;

            ClearLog();
            IntuneSignInBusyChanged?.Invoke(true);
            _signInInProgress = true;
            // If the sign-in browser window is closed before sign-in finishes, fail immediately
            // instead of waiting for the PowerShell command to time out.
            _ = MonitorSignInWindowAsync(myGen);
            // The Sign in button stays enabled so a stuck attempt can be retried with a
            // single click; only Publish is blocked until this attempt finishes.
            BtnPublish.IsEnabled = false;
            try
            {
                // Always start from a clean shared session. A previous attempt may have left a
                // process stuck waiting on a closed browser; StartFresh disposes it, unblocking
                // that wait and stopping the new sign-in from queueing behind the stuck one.
                var session = IntuneSession.StartFresh();
                if (!session.Start())
                {
                    if (myGen == _signInGen)
                        ShowThemedMessage("Sign-in failed", "\u26d4", "red", "Could not start a PowerShell session.", "Signed in to Intune failed");
                    WriteSignInLog("Sign-in failed: could not start a PowerShell session.");
                    return;
                }

                AppendLog("=== Sign in to Intune ===");
                AppendLog("A browser window will open - complete the sign-in there.");
                WriteSignInLog("A browser window will open - complete the sign-in there.");

                string tid = tenantId.Replace("'", "''");
                string cid = clientId.Replace("'", "''");
                string connectCmd =
                    EnsureIntuneModuleCmd +
                    // By-name import ONLY when the bundle did not load: importing by name
                    // rescans PSModulePath and a newer profile copy would silently win over
                    // the bundled version that was just imported by manifest path.
                    "if (-not $__bm) { Import-Module IntuneWin32App -Force }; " +
                    "try { " +
                    $"Connect-MSIntuneGraph -TenantId '{tid}' -ClientId '{cid}' | Out-Null; " +
                    "if ($Global:AuthenticationHeader) { Write-Output '##CONN##OK' } else { Write-Output '##CONN##FAIL' } " +
                    "} catch { Write-Output ('##CONN##FAIL ' + $_.Exception.Message) }";

                bool connected = false;
                await session.RunAsync(connectCmd, line =>
                {
                    if (line.IndexOf("##CONN##OK", StringComparison.Ordinal) >= 0) { connected = true; WriteSignInLog("Connected."); }
                    else if (line.IndexOf("##CONN##FAIL", StringComparison.Ordinal) >= 0) { connected = false; WriteSignInLog("Connect failed. " + line.Replace("##CONN##FAIL", "").Trim()); }
                    else { AppendLog(line); WriteSignInLog(line); }
                }, 300000);

                // A newer sign-in or a sign-out superseded this attempt - leave the UI alone.
                if (myGen != _signInGen) return;

                IntuneSession.SetConnected(connected);
                if (connected)
                {
                    SetIntuneSignedInUi(true);
                    WriteSignInLog("Signed in to Intune.");
                    ShowThemedMessage("Signed in", "\u2705", "blue",
                        "Signed in to Intune.", "Signed in to Intune");
                }
                else
                {
                    WriteSignInLog("Sign-in failed: could not sign in to Intune.");
                    ShowThemedMessage("Sign-in failed", "\u26d4", "red",
                        "Could not sign in to Intune. See the Log tab for details.", "Signed in to Intune failed");
                }
            }
            catch (Exception ex)
            {
                if (myGen == _signInGen)
                {
                    AppendLog("Sign-in error: " + ex.Message);
                    WriteSignInLog("Sign-in error: " + ex.Message);
                    IntuneSession.SetConnected(false);
                    ShowThemedMessage("Sign-in failed", "\u26d4", "red", "Could not sign in to Intune:\n" + ex.Message, "Signed in to Intune failed");
                }
            }
            finally
            {
                // Only the current attempt owns the toolbar; a superseded attempt must not
                // re-enable Publish while a newer attempt is still running.
                if (myGen == _signInGen)
                {
                    _signInInProgress = false;
                    BtnPublish.IsEnabled = true;
                    IntuneSignInBusyChanged?.Invoke(false);
                }
            }
        }

        // Enables or disables the whole Publish toolbar (and the path box) so nothing can
        // be changed, navigated, or signed out while a publish is running.
        private void SetPublishToolbarEnabled(bool enabled)
        {
            // Disabled == a publish is running (see the call sites), so Auto Package waits.
            AppBusy.SetManual("Publish Package", !enabled);
            TxtIntunewinPath.IsEnabled = enabled;
            BtnClearPath.IsEnabled     = enabled;
            BtnBrowse.IsEnabled        = enabled;
            BtnPublish.IsEnabled       = enabled;
            BtnSave.IsEnabled          = enabled;
            BtnNew.IsEnabled           = enabled;
            IntuneSignInEnabledChanged?.Invoke(enabled);
        }

        // Flips the Intune button between "Sign in" and "Sign out" so a second sign-in
        // cannot be started while a session is already connected. Raised to the host
        // (MainWindow top bar) which owns the visible button.
        private void SetIntuneSignedInUi(bool signedIn)
        {
            IntuneSignedInChanged?.Invoke(signedIn);
        }

        // Quick reachability check for the Microsoft sign-in service. Returns false when the
        // machine is offline (no adapter, no DNS, or the endpoint is unreachable) so sign-in
        // can fail fast with a clear message instead of waiting on a browser that cannot load.
        private static async Task<bool> HasInternetAsync()
        {
            try
            {
                using (var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                using (var req = new System.Net.Http.HttpRequestMessage(
                           System.Net.Http.HttpMethod.Get, "https://login.microsoftonline.com/"))
                using (await hc.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    // Any response, even an error status, means the network reached the service.
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // Publishes inside the persistent, already-connected session (no new process, no login).
        private async Task PublishViaSessionAsync(string scriptPath, string jsonPath,
                                                  string tenantId, string clientId, string appName)
        {
            ClearLog();
            ShowTab(9);
            AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Starting publish to Intune (signed-in session)...");
            AppendLog("  Script   : " + scriptPath);
            AppendLog("  JsonPath : " + jsonPath);
            AppendLog("\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");

            SetPublishToolbarEnabled(false);
            PrgPublishProgress.Visibility = Visibility.Visible;
            int exit = -1;
            bool published = false;
            bool authDenied = false;
            try
            {
                string sp  = scriptPath.Replace("'", "''");
                string jp  = jsonPath.Replace("'", "''");
                string tid = tenantId.Replace("'", "''");
                string cid = clientId.Replace("'", "''");
                string cmd =
                    "$global:LASTEXITCODE = 0; try { " +
                    $"& '{sp}' -JsonPath '{jp}' -TenantId '{tid}' -ClientId '{cid}' -DuplicateAction CreateNew -UseExistingConnection " +
                    "} catch { Write-Host ('ERROR: ' + $_.Exception.Message); $global:LASTEXITCODE = 1 }; " +
                    "if ($null -eq $LASTEXITCODE) { $global:LASTEXITCODE = 0 }";
                exit = await _psSession!.RunAsync(cmd, line =>
                {
                    // The script prints ##DONE##<action> on its own success path.
                    if (line.IndexOf("##DONE##", StringComparison.Ordinal) >= 0) { published = true; return; }
                    if (!authDenied &&
                        (line.IndexOf("Forbidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         line.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         line.IndexOf("(401)", StringComparison.Ordinal) >= 0 ||
                         line.IndexOf("(403)", StringComparison.Ordinal) >= 0 ||
                         line.IndexOf("WWW-Authenticate", StringComparison.OrdinalIgnoreCase) >= 0))
                        authDenied = true;
                    AppendLog(line);
                }, 1800000);
            }
            catch (Exception ex)
            {
                AppendLog("EXCEPTION: " + ex.Message);
                if (_psSession == null || !_psSession.IsAlive)
                {
                    IntuneSession.SetConnected(false);
                    AppendLog("The signed-in session ended. Click 'Sign in' to reconnect.");
                }
            }
            finally
            {
                SetPublishToolbarEnabled(true);
                PrgPublishProgress.Visibility = Visibility.Collapsed;
            }

            AppendLog("\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
            if (published || exit == 0)
            {
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publish completed successfully.");
                ShowThemedMessage("Published", "\u2705", "blue",
                    $"\"{appName}\" was published to Intune successfully.");
            }
            else
            {
                AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Publish FAILED. See the Log tab for details.");
                // Most authorization failures here are a sign-in that has gone stale (the access
                // token lasts about an hour), which a fresh sign-in fixes - so lead with that.
                // The role and consent requirements follow as a fallback for first-time setup.
                if (authDenied)
                    ShowThemedMessage("Sign in again", "\u26a0", "amber",
                        "Publishing was not authorized. The Intune sign-in for this session may have expired (this can happen after about an hour).\n\n" +
                        "Please sign out and sign in again, then publish.\n\n" +
                        "If it still fails after signing in again, this account or the app registration may be missing permissions:\n\n" +
                        "1. The signed-in user needs an Intune role that can manage apps (the built-in \"Application Manager\" role, or a custom role with Mobile apps Create, Read, Update, Delete and Assign).\n\n" +
                        "2. The app registration needs the Microsoft Graph permission \"DeviceManagementApps.ReadWrite.All\" with admin consent.");
                else
                    ShowThemedMessage("Publish failed", "\u26d4", "red",
                        "Publish did not complete. See the Log tab for details.");
            }
        }

        // Quietly asks the signed-in session whether the app (name, or name+version) already
        // exists in Intune. Returns true if a duplicate was found.
        private async Task<bool> CheckDuplicateInSessionAsync(string scriptPath, string jsonPath,
                                                              string tenantId, string clientId)
        {
            if (_psSession == null || !_psSession.IsAlive) return false;
            string sp  = scriptPath.Replace("'", "''");
            string jp  = jsonPath.Replace("'", "''");
            string tid = tenantId.Replace("'", "''");
            string cid = clientId.Replace("'", "''");
            string cmd =
                "try { " +
                $"& '{sp}' -JsonPath '{jp}' -TenantId '{tid}' -ClientId '{cid}' -UseExistingConnection -CheckDuplicateOnly " +
                "} catch { Write-Host ('ERROR: ' + $_.Exception.Message) }";
            bool found = false;
            try
            {
                await _psSession.RunAsync(cmd, line =>
                {
                    if (line.IndexOf("##DUP##FOUND", StringComparison.Ordinal) >= 0) found = true;
                    // keep the check quiet - only the marker matters here
                }, 120000);
            }
            catch { /* if the check fails, let the publish proceed normally */ }
            return found;
        }

        // "Already exists" dialog (screenshot-5 style) with an editable App Name and, when a
        // version is supplied, an editable App Version. Returns true on Yes.
        // Suggests a unique app name for a duplicate copy:
        //   "<name>"        (no _Test) -> "<name>_Test", then "<name>_Test1", "_Test2", ...
        //   "<name>_Test"   -> "<name>_Test1", "_Test2", ...
        //   "<name>_Test3"  -> "<name>_Test4", ...
        private static string NextTestName(string currentName, ISet<string> existing)
        {
            currentName = (currentName ?? "").Trim();
            var m = System.Text.RegularExpressions.Regex.Match(currentName, @"^(.*_Test)(\d*)$");
            if (m.Success)
            {
                string stem = m.Groups[1].Value;
                int start = m.Groups[2].Value.Length == 0
                    ? 1
                    : (int.TryParse(m.Groups[2].Value, out var nn) ? nn + 1 : 1);
                for (int i = start; i < start + 100000; i++)
                {
                    string cand = stem + i;
                    if (!existing.Contains(cand)) return cand;
                }
                return stem + System.Guid.NewGuid().ToString("N").Substring(0, 4);
            }
            else
            {
                string baseName = currentName + "_Test";
                if (!existing.Contains(baseName)) return baseName;
                for (int i = 1; i < 100000; i++)
                {
                    string cand = baseName + i;
                    if (!existing.Contains(cand)) return cand;
                }
                return baseName + System.Guid.NewGuid().ToString("N").Substring(0, 4);
            }
        }

        private bool ShowDuplicateRenameDialog(string defaultName, string? defaultVersion,
                                               out string newName, out string newVersion)
        {
            newName = defaultName;
            newVersion = defaultVersion ?? "";
            bool result = false;

            var winBg     = TC("NavBarBgBrush");
            var borderClr = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));

            var win = new Window
            {
                Title = "Publish Package - Already exists",
                Width = 460, MaxWidth = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
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

            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            var titleBarBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));
            System.Windows.Media.Brush titleTxt = System.Windows.Media.Brushes.White;
            System.Windows.Media.Brush titleSec = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

            var tbar = new Border
            {
                Background = titleBarBg,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = TC("BorderMainBrush"),
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 6, 12, 6),
            };
            tbar.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tbar, 0);

            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ti  = new TextBlock { Text = "\U0001F680", FontSize = 14, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt  = new TextBlock { Text = "Publish Package - Already exists", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var tcl = new Button   { Content = "\u2715", FontSize = 11, Foreground = titleSec, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => { result = false; win.Close(); };

            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tbar.Child = tg;
            og.Children.Add(tbar);

            var lblFg = TC("TextPrimaryBrush");
            var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            Grid.SetRow(panel, 1);
            og.Children.Add(panel);

            panel.Children.Add(new TextBlock
            {
                Text = defaultVersion == null
                    ? "An application with this name already exists in Intune.\nEnter a new name and click Yes to publish a separate copy, or Cancel."
                    : "An application with this name and version already exists in Intune.\nEnter a new name and/or version and click Yes to publish a separate copy, or Cancel.",
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = lblFg, Margin = new Thickness(0, 0, 0, 12)
            });

            panel.Children.Add(new TextBlock { Text = "App Name", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, Margin = new Thickness(0, 0, 0, 3) });
            var nameBox = new TextBox { Text = defaultName, Style = (Style)Application.Current.TryFindResource("ModernTextBoxStyle"), Height = 28, FontSize = 11 };
            panel.Children.Add(nameBox);

            TextBox? versionBox = null;
            if (defaultVersion != null)
            {
                panel.Children.Add(new TextBlock { Text = "App Version", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, Margin = new Thickness(0, 10, 0, 3) });
                versionBox = new TextBox { Text = defaultVersion, Style = (Style)Application.Current.TryFindResource("ModernTextBoxStyle"), Height = 28, FontSize = 11 };
                panel.Children.Add(versionBox);
            }

            var btnRow = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Width = 216 };
            var yesBtn    = DlgBtnStyled("Yes", "blue", 100);    yesBtn.Margin = new Thickness(4, 0, 4, 0);
            var cancelBtn = DlgBtnStyled("Cancel", "neutral", 100); cancelBtn.Margin = new Thickness(4, 0, 4, 0);
            yesBtn.Click    += (_, _) => { result = true;  win.Close(); };
            cancelBtn.Click += (_, _) => { result = false; win.Close(); };
            btnRow.Children.Add(yesBtn); btnRow.Children.Add(cancelBtn);
            panel.Children.Add(btnRow);

            // Focus the name box but do NOT select the text; place the caret at the end.
            nameBox.Loaded += (_, _) =>
            {
                nameBox.Focus();
                nameBox.CaretIndex = nameBox.Text?.Length ?? 0;
            };
            win.ShowDialog();

            newName    = nameBox.Text?.Trim() ?? defaultName;
            newVersion = versionBox?.Text?.Trim() ?? (defaultVersion ?? "");
            return result;
        }

        // Sign-in via MSAL (the same library the IntuneWin32App module uses) so the token is
        // acquired exactly the way Microsoft Graph expects, then cached for the next publish.
        // REQUIRES NuGet package: Microsoft.Identity.Client
        private async Task<bool> AcquireIntuneTokenAsync(GraphSettings settings)
        {
            try
            {
                var app = Microsoft.Identity.Client.PublicClientApplicationBuilder
                    .Create(settings.ClientId)
                    .WithAuthority(Microsoft.Identity.Client.AzureCloudInstance.AzurePublic, settings.TenantId)
                    .WithRedirectUri("http://localhost")
                    .Build();

                string[] scopes = { "https://graph.microsoft.com/.default" };

                AppendLog("=== Sign in to Intune ===");
                AppendLog("A browser window is opening for sign-in...");

                Microsoft.Identity.Client.AuthenticationResult result;
                try
                {
                    var accounts = await app.GetAccountsAsync();
                    var first = System.Linq.Enumerable.FirstOrDefault(accounts);
                    if (first == null)
                        throw new Microsoft.Identity.Client.MsalUiRequiredException("no_account", "interactive sign-in required");
                    result = await app.AcquireTokenSilent(scopes, first).ExecuteAsync();
                }
                catch (Microsoft.Identity.Client.MsalUiRequiredException)
                {
                    result = await app.AcquireTokenInteractive(scopes)
                        .WithPrompt(Microsoft.Identity.Client.Prompt.SelectAccount)
                        .ExecuteAsync();
                }

                _intuneAccessToken      = result.AccessToken;
                _intuneTokenExpiresUnix = result.ExpiresOn.ToUnixTimeSeconds();

                using var http = new System.Net.Http.HttpClient();
                string tenant = await FetchTenantName(http, _intuneAccessToken);
                AppendLog("Signed in" + (string.IsNullOrEmpty(tenant) ? "" : " - " + tenant) + ".");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("Sign-in error: " + ex.Message);
                return false;
            }
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Friendly title for the completion dialog after a duplicate-handling action.
        private static string DuplicateActionTitle(string action) => action switch
        {
            "CreateNew"    => "New application created",
            _              => "Done",
        };

        // Friendly completion message for each duplicate-handling action.
        private static string DuplicateActionDoneText(string action, string name) => action switch
        {
            "CreateNew"    => $"A new application \"{name}\" was created successfully.",
            _              => "The action completed successfully.",
        };

        // Themed dialog (screenshot 5 style) shown when an app of the same name already
        // exists. Offers four radio choices + Yes/Cancel. Returns the chosen action
        // Returns "CreateNew" if confirmed, or "" if cancelled.
        private string ShowDuplicateActionDialog(string appName, string appVersion)
        {
            string result = "";

            var winBg     = TC("NavBarBgBrush");
            var borderClr = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63));

            var win = new Window
            {
                Title = "Publish Package - Existing application found",
                Width = 470, MaxWidth = 470,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
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

            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            // Title bar
            var tb = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = TC("BorderMainBrush"),
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 6, 12, 6),
            };
            tb.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tb, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            System.Windows.Media.Brush titleTxt = System.Windows.Media.Brushes.White;
            var ti  = new TextBlock { Text = "🚀", FontSize = 14, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var tt  = new TextBlock { Text = "Publish Package - Existing application found", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = titleTxt, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var tcl = new Button   { Content = "✕", FontSize = 11, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => { result = ""; win.Close(); };
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg;
            og.Children.Add(tb);

            // Body
            var cg = new Grid { Margin = new Thickness(8), Background = System.Windows.Media.Brushes.Transparent };
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cg, 1);
            og.Children.Add(cg);

            var boxBg  = TC("WindowBgBrush");
            var boxBdr = TC("BorderMainBrush");
            var lblFg  = TC("TextPrimaryBrush");

            int row = 0;
            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); row++;

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var statusLbl = new TextBlock { Text = "Status", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
            var box = new Border { Background = boxBg, BorderBrush = boxBdr, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8) };
            var bodyPanel = new StackPanel();
            string verPart = string.IsNullOrWhiteSpace(appVersion) ? "" : $" (version {appVersion})";
            bodyPanel.Children.Add(new TextBlock
            {
                Text = $"An application named \"{appName}\"{verPart} already exists in Intune.\nWhat would you like to do?",
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = lblFg,
                Margin = new Thickness(0, 0, 0, 8)
            });

            const string grp = "DupAction";
            RadioButton MakeOpt(string text, string tag, bool isChecked)
            {
                return new RadioButton
                {
                    Content = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = lblFg },
                    GroupName = grp, Tag = tag, IsChecked = isChecked,
                    Foreground = lblFg, Margin = new Thickness(0, 3, 0, 3)
                };
            }
            var optCreate = MakeOpt("Create new application with same app name and version", "CreateNew", true);
            bodyPanel.Children.Add(optCreate);
            box.Child = bodyPanel;

            Grid.SetRow(statusLbl, row); Grid.SetColumn(statusLbl, 0);
            Grid.SetRow(box, row);       Grid.SetColumn(box, 1);
            cg.Children.Add(statusLbl);  cg.Children.Add(box); row++;

            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); row++;

            cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var qIcon = new TextBlock { Text = "❓", FontSize = 22, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x9B, 0xF5)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(qIcon, row); Grid.SetColumn(qIcon, 0);
            cg.Children.Add(qIcon);

            var ug = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };
            var bYes    = DlgBtnStyled("✔  Yes", "blue", 100);
            var bCancel = DlgBtnStyled("✕  Cancel", "neutral", 100);
            bYes.Margin    = new Thickness(4, 0, 4, 0);
            bCancel.Margin = new Thickness(4, 0, 4, 0);
            bYes.Click += (_, _) =>
            {
                result = "CreateNew";
                win.Close();
            };
            bCancel.Click += (_, _) => { result = ""; win.Close(); };
            ug.Children.Add(bYes); ug.Children.Add(bCancel);
            Grid.SetRow(ug, row); Grid.SetColumn(ug, 1);
            cg.Children.Add(ug); row++;

            cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            win.ShowDialog();
            return result;
        }

        // ── Log tab helpers ──────────────────────────────────────────
        // Start offset (in LogTextBox.Text) of the last "  Uploading: N%" line, or -1. Progress
        // lines arrive once per percent from PublishPackage.ps1; appended naively a large upload
        // writes a hundred of them. Instead each one REPLACES the previous, so the Log shows a
        // single live percentage - and the first non-progress line ends the run, so the final
        // "Uploading: 100% - completed." stays in the log as the record of the upload. Same
        // mechanism as AutoPublishPage, matching the same script-side shadow.
        private int _uploadProgressStart = -1;
        private static bool IsUploadProgressLine(string t) =>
            t.TrimStart().StartsWith("Uploading: ", StringComparison.Ordinal) && t.Contains('%');

        private void AppendLog(string text)
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
                    LogTextBox.AppendText(text + Environment.NewLine);
                }
                if (!progress) _uploadProgressStart = -1;
                LogTextBox.ScrollToEnd();
            });
        }

        private void ClearLog()
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox?.Clear();
                _uploadProgressStart = -1;   // a stale offset must not survive into the next run
            });
        }

        // ── Mandatory-field validation (fields marked * in the UI) ──
        // Returns a list of human-readable "Tab - Field" names that are missing.
        private System.Collections.Generic.List<string> CollectMissingMandatoryFields()
        {
            var missing = new System.Collections.Generic.List<string>();

            // Tab 1: App Info
            if (string.IsNullOrWhiteSpace(TxtAppName.Text))     missing.Add("App Info - Name");
            if (string.IsNullOrWhiteSpace(TxtDescription.Text)) missing.Add("App Info - Description");
            if (string.IsNullOrWhiteSpace(TxtPublisher.Text))   missing.Add("App Info - Publisher");

            // Tab 2: Program
            if (string.IsNullOrWhiteSpace(TxtInstallCmd.Text))   missing.Add("Program - Install command");
            if (string.IsNullOrWhiteSpace(TxtUninstallCmd.Text)) missing.Add("Program - Uninstall command");

            // Tab 3: Requirements
            if (ChkX86.IsChecked != true && ChkX64.IsChecked != true && ChkArm64.IsChecked != true)
                missing.Add("Requirements - Operating system architecture (select at least one)");
            if (CmbMinOs.SelectedItem == null ||
                string.IsNullOrWhiteSpace((CmbMinOs.SelectedItem as ComboBoxItem)?.Content?.ToString()))
                missing.Add("Requirements - Minimum operating system");

            // Tab 4: Detection
            if (RbDetectScript.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(TxtDetectScriptPath.Text))
                    missing.Add("Detection - Script file");
            }
            else
            {
                var msiR  = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("MSI",      StringComparison.OrdinalIgnoreCase));
                var fileR = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("File",     StringComparison.OrdinalIgnoreCase));
                var regR  = _detectionRules.FirstOrDefault(r => r.RuleType.Equals("Registry", StringComparison.OrdinalIgnoreCase));
                bool msiOn  = msiR?.Enabled  == true;
                bool fileOn = fileR?.Enabled == true;
                bool regOn  = regR?.Enabled  == true;

                if (!msiOn && !fileOn && !regOn)
                {
                    missing.Add("Detection - at least one rule type (MSI / File / Registry) must be checked");
                }
                else
                {
                    if (msiOn && string.IsNullOrWhiteSpace(msiR!.MsiProductCode))
                        missing.Add("Detection (MSI) - MSI product code");
                    if (fileOn)
                    {
                        if (string.IsNullOrWhiteSpace(fileR!.FilePath))  missing.Add("Detection (File) - Path");
                        if (string.IsNullOrWhiteSpace(fileR!.FileName))  missing.Add("Detection (File) - File or folder");
                        if (string.IsNullOrWhiteSpace(fileR!.FileCheck)) missing.Add("Detection (File) - Detection method");
                    }
                    if (regOn)
                    {
                        if (string.IsNullOrWhiteSpace(regR!.RegKeyPath)) missing.Add("Detection (Registry) - Key path");
                        if (string.IsNullOrWhiteSpace(regR!.RegCheck))   missing.Add("Detection (Registry) - Detection method");
                    }
                }
            }

            return missing;
        }

        // ── Graph API: Create app ──────────────────────────────────
        private async Task<string> GraphCreateApp(System.Net.Http.HttpClient http)
        {
            string installBehavior = (RbInstallUser?.IsChecked == true) ? "user" : "system";
            string restartCode     = (CmbRestartBehavior.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

            // Build architecture list
            var archs = new List<string>();
            if (ChkX64.IsChecked   == true) archs.Add("x64");
            if (ChkX86.IsChecked   == true) archs.Add("x86");
            if (ChkArm64.IsChecked == true) archs.Add("arm64");

            string minOsRaw = (CmbMinOs.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Windows 10 21H2";

            // Build detection rules JSON
            var detectionRulesJson = BuildDetectionRulesJson();

            var body = new
            {
                odataType                 = "#microsoft.graph.win32LobApp",
                displayName               = TxtAppName.Text.Trim(),
                publisher                 = TxtPublisher.Text.Trim(),
                appVersion                = TxtAppVersion.Text.Trim(),
                description               = TxtDescription.Text.Trim(),
                informationUrl            = TxtInfoUrl.Text.Trim(),
                privacyInformationUrl     = TxtPrivacyUrl.Text.Trim(),
                installCommandLine        = TxtInstallCmd.Text.Trim(),
                uninstallCommandLine      = TxtUninstallCmd.Text.Trim(),
                installExperience         = new { runAsAccount = installBehavior },
                minimumSupportedWindowsRelease = MapMinOs(minOsRaw),
                applicableArchitectures   = string.Join(",", archs.Select(a => a)),
                rules                     = detectionRulesJson
            };

            string json = JsonSerializer.Serialize(body);
            var content = new System.Net.Http.StringContent(
                json, System.Text.Encoding.UTF8, "application/json");

            var resp = await http.PostAsync(
                "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps", content);

            string respJson = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Create app failed ({(int)resp.StatusCode}): {respJson}");

            using var doc = JsonDocument.Parse(respJson);
            return doc.RootElement.GetProperty("id").GetString() ?? "";
        }

        private List<object> BuildDetectionRulesJson()
        {
            var rules = new List<object>();
            foreach (var rule in _detectionRules)
            {
                if (rule.RuleType == "MSI")
                    rules.Add(new { odataType = "#microsoft.graph.win32LobAppProductCodeDetection",
                                    productCode = rule.MsiProductCode, productVersionOperator = "notConfigured" });
                else if (rule.RuleType == "File")
                    rules.Add(new { odataType = "#microsoft.graph.win32LobAppFileSystemDetection",
                                    path = "", fileOrFolderName = "", detectionType = "exists", check32BitOn64System = false });
                else if (rule.RuleType == "Registry")
                    rules.Add(new { odataType = "#microsoft.graph.win32LobAppRegistryDetection",
                                    keyPath = "", detectionType = "exists", check32BitOn64System = false });
            }
            return rules;
        }

        private static string MapMinOs(string friendly) => friendly switch
        {
            "Windows 10 1607" => "v10_1607", "Windows 10 1703" => "v10_1703",
            "Windows 10 1709" => "v10_1709", "Windows 10 1803" => "v10_1803",
            "Windows 10 1809" => "v10_1809", "Windows 10 1903" => "v10_1903",
            "Windows 10 1909" => "v10_1909", "Windows 10 2004" => "v10_2004",
            "Windows 10 20H2" => "v10_20H2", "Windows 10 21H1" => "v10_21H1",
            "Windows 10 21H2" => "v10_21H2", "Windows 10 22H2" => "v10_22H2",
            "Windows 11 21H2" => "v10_21H2", "Windows 11 22H2" => "v11_22H2",
            "Windows 11 23H2" => "v11_23H2", "Windows 11 24H2" => "v11_24H2",
            _ => "v10_21H2"
        };

        // ── Graph API: Upload content ─────────────────────────────
        private async Task GraphUploadContent(
            System.Net.Http.HttpClient http, string appId, string intunewinPath)
        {
            long fileSize = new FileInfo(intunewinPath).Length;

            // Create content version
            var cvResp = await http.PostAsync(
                $"https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/{appId}/microsoft.graph.win32LobApp/contentVersions",
                new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            string cvJson = await cvResp.Content.ReadAsStringAsync();
            if (!cvResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Create content version failed: {cvJson}");
            using var cvDoc = JsonDocument.Parse(cvJson);
            string cvId = cvDoc.RootElement.GetProperty("id").GetString() ?? "1";

            // Create file entry
            string fileEntryBody = JsonSerializer.Serialize(new
            {
                name     = Path.GetFileName(intunewinPath),
                size     = fileSize,
                sizeEncrypted = fileSize,
                isDependency  = false
            });
            var feResp = await http.PostAsync(
                $"https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/{appId}/microsoft.graph.win32LobApp/contentVersions/{cvId}/files",
                new System.Net.Http.StringContent(fileEntryBody, System.Text.Encoding.UTF8, "application/json"));
            string feJson = await feResp.Content.ReadAsStringAsync();
            if (!feResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Create file entry failed: {feJson}");
            using var feDoc = JsonDocument.Parse(feJson);
            string fileId  = feDoc.RootElement.GetProperty("id").GetString() ?? "";

            // Poll for Azure Storage URI
            string uploadUri = "";
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(2000);
                var pollResp = await http.GetAsync(
                    $"https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/{appId}/microsoft.graph.win32LobApp/contentVersions/{cvId}/files/{fileId}");
                string pollJson = await pollResp.Content.ReadAsStringAsync();
                using var pollDoc = JsonDocument.Parse(pollJson);
                if (pollDoc.RootElement.TryGetProperty("azureStorageUri", out var uri) &&
                    !string.IsNullOrWhiteSpace(uri.GetString()))
                {
                    uploadUri = uri.GetString()!;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(uploadUri))
                throw new InvalidOperationException("Timed out waiting for Azure Storage URI.");

            // Upload to Azure Blob Storage in 4MB chunks
            const int chunkSize = 4 * 1024 * 1024;
            byte[] buffer = new byte[chunkSize];
            using var fs = File.OpenRead(intunewinPath);
            using var azureClient = new System.Net.Http.HttpClient();
            azureClient.DefaultRequestHeaders.Add("x-ms-version", "2020-04-08");
            azureClient.DefaultRequestHeaders.Add("x-ms-blob-type", "BlockBlob");

            var blockIds = new List<string>();
            long offset  = 0;
            int  blockNum = 0;
            while (offset < fileSize)
            {
                int read = await fs.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) break;
                string blockId = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(blockNum.ToString("D5")));
                blockIds.Add(blockId);
                string putUrl = $"{uploadUri}&comp=block&blockid={Uri.EscapeDataString(blockId)}";
                var chunk     = new System.Net.Http.ByteArrayContent(buffer, 0, read);
                chunk.Headers.Add("x-ms-version", "2020-04-08");
                await azureClient.PutAsync(putUrl, chunk);
                offset += read;
                blockNum++;
            }

            // Commit block list
            string blockListXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>" +
                string.Join("", blockIds.Select(id => $"<Latest>{id}</Latest>")) + "</BlockList>";
            string putListUrl = $"{uploadUri}&comp=blocklist";
            var listContent   = new System.Net.Http.StringContent(blockListXml, System.Text.Encoding.UTF8, "application/xml");
            listContent.Headers.Add("x-ms-version", "2020-04-08");
            await azureClient.PutAsync(putListUrl, listContent);

            // Commit upload to Graph
            string commitBody = JsonSerializer.Serialize(new
            {
                fileEncryptionInfo = new
                {
                    fileDigest         = "",
                    fileDigestAlgorithm = "SHA256",
                    encryptionKey      = "",
                    macKey             = "",
                    initializationVector = "",
                    mac                = "",
                    profileIdentifier  = "ProfileVersion1"
                }
            });
            await http.PostAsync(
                $"https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/{appId}/microsoft.graph.win32LobApp/contentVersions/{cvId}/files/{fileId}/commit",
                new System.Net.Http.StringContent(commitBody, System.Text.Encoding.UTF8, "application/json"));
        }

        // ── Graph API: Commit content version ─────────────────────
        private async Task GraphCommitContent(System.Net.Http.HttpClient http, string appId)
        {
            // Set committed content version = 1
            string patchBody = JsonSerializer.Serialize(new
            {
                odataType               = "#microsoft.graph.win32LobApp",
                committedContentVersion = "1"
            });
            await http.SendAsync(new System.Net.Http.HttpRequestMessage(
                new System.Net.Http.HttpMethod("PATCH"),
                $"https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/{appId}")
            {
                Content = new System.Net.Http.StringContent(
                    patchBody, System.Text.Encoding.UTF8, "application/json")
            });

            // Poll until processing state = processed
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(3000);
                var r = await http.GetAsync(
                    $"https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/{appId}");
                string json = await r.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("uploadState", out var state) &&
                    (state.GetString() == "commitAppSuccess" || state.GetString() == "published"))
                    return;
            }
        }
        // ══════════════════════════════════════════════════════════════
        // DIALOG / THEME HELPERS  (local copies for PublishPackagePage)
        // ══════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════════
        //  SETTINGS - Templates\IntuneSettings.json
        // ══════════════════════════════════════════════════════════════════════
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => ShowIntuneSettingsDialog();

        // Shared option lists. These are the SAME lists the page's own controls are built from, so a
        // value chosen here always round-trips through LoadIntuneDefaults' SelectCombo (which matches
        // on the item's exact content string). Keep them in step with the XAML / RebuildDetectionRuleUI.
        private static readonly string[] CategoryOptions =
            { "(Not configured)", "Business", "Computer management", "Productivity", "Development tools",
              "Data management", "Utilities", "Photo & video", "Other" };
        private static readonly string[] RestartOptions =
            { "Determine behaviour based on return codes", "No specific action",
              "App install may force a device restart", "Intune will force a mandatory device restart" };
        private static readonly string[] MinOsOptions =
            { "Windows 10 1607", "Windows 10 1703", "Windows 10 1709", "Windows 10 1803", "Windows 10 1809",
              "Windows 10 1903", "Windows 10 1909", "Windows 10 2004", "Windows 10 20H2", "Windows 10 21H1",
              "Windows 10 21H2", "Windows 10 22H2", "Windows 11 21H2", "Windows 11 22H2", "Windows 11 23H2",
              "Windows 11 24H2" };
        private static readonly string[] OperatorOptions =
            { "Equal to", "Not equal to", "Greater than", "Greater than or equal to", "Less than",
              "Less than or equal to" };
        private static readonly string[] FileMethodOptions =
            { "File or folder exists", "Date modified", "Date created", "String (version)", "Size in MB" };
        private static readonly string[] RegMethodOptions =
            { "Key exists", "Key does not exist", "Value exists", "Value does not exist",
              "String comparison", "Integer comparison", "Version comparison" };

        /// <summary>
        /// Rows in the Settings dialog: (Section, JSON key, Label, Kind).
        ///
        /// Kind mirrors the control the PAGE uses for the same field, so the dialog reads the way the
        /// tab does:
        ///   text            free-text box            multi   multi-line box
        ///   browse          text box + Browse        check   single checkbox        -> "Yes" / "No"
        ///   radio:A|B       radio pair               combo:A|B|C  dropdown          -> the chosen label
        ///   arch            the 32/64/ARM64 checkbox trio (Requirements tab)
        ///
        /// Only POLICY defaults. The per-app keys (IntuneAppName / AppVendor / AppVersion,
        /// IntunewinFilePath, the detection VALUES, DeploymentType, UATUsers...) are filled by the page
        /// and are not editable here - but they ARE preserved on save, see ShowIntuneSettingsDialog.
        /// </summary>
        private static IEnumerable<(string Section, string Key, string Label, string Kind)> IntuneSettingRows()
        {
            yield return ("Authentication", "ClientId", "Client ID", "text");
            yield return ("Authentication", "TenantId", "Tenant ID", "text");

            yield return ("App Info", "IntuneCategory", "Category",        "combo:" + string.Join("|", CategoryOptions));
            yield return ("App Info", "FeaturedApp",    "Show this as a featured app", "combo:No|Yes");
            yield return ("App Info", "Description",    "Description",     "multi");
            yield return ("App Info", "InformationURL", "Information URL", "text");
            yield return ("App Info", "PrivacyURL",     "Privacy URL",     "text");
            yield return ("App Info", "Developer",      "Developer",       "text");
            yield return ("App Info", "Owner",          "Owner",           "text");
            yield return ("App Info", "Notes",          "Notes",           "multi");
            yield return ("App Info", "Logo",           "Logo",            "browse");

            yield return ("Program", "InstallCommand",          "Install command",              "text");
            yield return ("Program", "UninstallCommand",        "Uninstall command",            "text");
            yield return ("Program", "InstallationTime",        "Installation time required (mins)", "text");
            yield return ("Program", "AllowAvailableUninstall", "Allow available uninstall",    "radio:Yes|No");
            yield return ("Program", "InstallBehavior",         "Install behaviour",            "radio:System|User");
            yield return ("Program", "DeviceRestartBehaviour",  "Device restart behaviour",     "combo:" + string.Join("|", RestartOptions));

            yield return ("Requirements", "OperatingSystemArchitecture", "Operating system architecture", "arch");
            yield return ("Requirements", "MinimumOperatingSystem",      "Minimum operating system",      "combo:" + string.Join("|", MinOsOptions));

            yield return ("Detection", "RulesFormat",                  "Rules format",  "radio:Manual detection rules|Use a custom detection script");
            yield return ("Detection", "DetectionTypeMSI",             "MSI rule",      "check");
            yield return ("Detection", "DetectionMsiVersionCheck",     "MSI product version check", "radio:No|Yes");
            yield return ("Detection", "DetectionMsiOperator",         "MSI operator",  "combo:" + string.Join("|", OperatorOptions));
            yield return ("Detection", "DetectionTypeFile",            "File rule",     "check");
            yield return ("Detection", "DetectionFileDetectionMethod", "File detection method", "combo:" + string.Join("|", FileMethodOptions));
            yield return ("Detection", "DetectionFileOperator",        "File operator", "combo:" + string.Join("|", OperatorOptions));
            yield return ("Detection", "DetectionFile32Bit",           "File: associated with a 32-bit app on 64-bit clients", "check");
            yield return ("Detection", "DetectionTypeRegistry",        "Registry rule", "check");
            yield return ("Detection", "DetectionRegValueName",        "Registry value name",       "text");
            yield return ("Detection", "DetectionRegDetectionMethod",  "Registry detection method", "combo:" + string.Join("|", RegMethodOptions));
            yield return ("Detection", "DetectionRegOperator",         "Registry operator",         "combo:" + string.Join("|", OperatorOptions));
            yield return ("Detection", "DetectionReg32Bit",            "Registry: associated with a 32-bit app on 64-bit clients", "check");
            yield return ("Detection", "DetectionScriptEnforceSignatureCheck", "Script: enforce signature check", "radio:No|Yes");
            yield return ("Detection", "DetectionScriptRunAs32Bit",    "Script: run as 32-bit",     "radio:No|Yes");
        }

        /// <summary>
        /// Registry detection methods that are presence-only checks: they need no operator and no
        /// comparison value. Same predicate the detection rule UI uses (RegMethodNeedsValue).
        /// </summary>
        private static bool RegMethodNeedsValueS(string m) =>
            !string.IsNullOrWhiteSpace(m)
            && !m.Equals("Key exists",           StringComparison.OrdinalIgnoreCase)
            && !m.Equals("Key does not exist",   StringComparison.OrdinalIgnoreCase)
            && !m.Equals("Value exists",         StringComparison.OrdinalIgnoreCase)
            && !m.Equals("Value does not exist", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// File detection methods that need an operator and comparison value. "File or folder exists"
        /// is a presence-only check. Same predicate the detection rule UI uses.
        /// </summary>
        private static bool FileMethodNeedsValueS(string m) =>
            !string.IsNullOrWhiteSpace(m)
            && !m.Equals("File or folder exists", StringComparison.OrdinalIgnoreCase);

        private void ShowIntuneSettingsDialog()
        {
            // Parse into a JsonNode, not a typed class: the dialog only edits the keys in
            // IntuneSettingRows(), and writing the SAME node back means every other key in the file
            // (the per-app values, plus anything a future version adds) survives untouched.
            System.Text.Json.Nodes.JsonObject root;
            try
            {
                root = File.Exists(SettingsPath)
                    ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))
                        ?.AsObject() ?? new System.Text.Json.Nodes.JsonObject()
                    : new System.Text.Json.Nodes.JsonObject();
            }
            catch (Exception ex)
            {
                ShowThemedMessage("Settings", "\u26a0", "amber",
                    $"IntuneSettings.json could not be parsed - check the JSON syntax.\n\n{SettingsPath}\n\n{ex.Message}");
                return;
            }

            // GetValue<string> first: a hand-edited file may hold a bare true/false or a number, and
            // ToString() on those would give "true" / "1" rather than throwing.
            string Get(string key)
            {
                if (!root.TryGetPropertyValue(key, out var n) || n == null) return "";
                try   { return n.GetValue<string>(); }
                catch { return n.ToString(); }
            }
            bool IsYes(string key) =>
                Get(key).Equals("Yes",  StringComparison.OrdinalIgnoreCase)
             || Get(key).Equals("true", StringComparison.OrdinalIgnoreCase);

            var win = new Window
            {
                Width = 780,
                // Height was a fixed 740, which is taller than the screen once display scaling is
                // applied: a 1080p panel at 150% gives about 680 usable units, so the window was
                // 60 too tall, CenterOwner pushed its top off the screen and the button bar fell
                // off the bottom. It was unreachable rather than merely ugly.
                //
                // Same shape as Validate Source - Settings: grow to fit the content, but never past
                // the work area, and let the ScrollViewer on row 1 take the overflow. The floor of
                // 480 keeps it sane on a very short work area, and the 48 leaves room for the
                // taskbar and a margin.
                SizeToContent = SizeToContent.Height,
                MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 48),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                // Transparent, not WindowBgBrush. rootBorder below paints the background and has a
                // CornerRadius of 10; an opaque window background fills those corners in, which is
                // why this dialog had square corners while the themed ones are rounded.
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                ShowInTaskbar = false,
            };
            // A file/folder picker inside the dialog can steal foreground on close; hand it back.
            win.Closed += (_, __) => { try { win.Owner?.Activate(); } catch { } };
            var rootBorder = new Border
            {
                Background      = TC("DlgBodyBgBrush"),
                BorderBrush     = TC("DlgBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
            };

            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Slate title bar, not MakeDialogHeaderThemed("blue").
            //
            // The themed helper resolves DlgHdrBlueBg / DlgHdrBlueFg, which is #E8F0FE with #1A56CC
            // text in the light theme: a pale header with blue text. Every other dialog in the app
            // uses this #535663 bar with white text - it appears in eight files and about thirty-six
            // places, including six times in THIS file, against nine uses of the themed helper
            // across two files. The settings dialog was the odd one out, not the standard.
            //
            // Built inline to match the sibling dialog at line 5384 exactly, down to the 16,6,12,6
            // padding and the 0xCC-alpha white close glyph. The themed helper could not be reused
            // as-is: it hardcodes the close button to TextSecondaryBrush, which is dark and would
            // be invisible on a dark bar.
            // Locals are prefixed hdr* rather than the tb/tg/ti/tt/tcl the sibling dialog at line
            // 5384 uses. That method has no other locals; this one builds the settings form below
            // and already declares `tb` three times for text boxes inside nested blocks, so a plain
            // `tb` here is CS0136 - declared in an enclosing scope of a name used further down.
            var hdrBar = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x56, 0x63)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = TC("BorderMainBrush"),
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding = new Thickness(16, 6, 12, 6),
            };
            hdrBar.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(hdrBar, 0);
            var hdrGrid = new Grid();
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            System.Windows.Media.Brush hdrFg = System.Windows.Media.Brushes.White;
            var hdrIcon  = new TextBlock { Text = "\u2699", FontSize = 14, Foreground = hdrFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var hdrTitle = new TextBlock { Text = "Publish Package - Settings", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = hdrFg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var hdrClose = new Button   { Content = "✕", FontSize = 11, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            hdrClose.Click += (_, _) => win.Close();
            Grid.SetColumn(hdrIcon, 0); Grid.SetColumn(hdrTitle, 1); Grid.SetColumn(hdrClose, 2);
            hdrGrid.Children.Add(hdrIcon); hdrGrid.Children.Add(hdrTitle); hdrGrid.Children.Add(hdrClose);
            hdrBar.Child = hdrGrid;
            var hdr = hdrBar;
            Grid.SetRow(hdr, 0);
            outer.Children.Add(hdr);

            var lblFg    = TC("TextSecondaryBrush");
            var valFg    = TC("TextPrimaryBrush");
            var cmbStyle = Application.Current.TryFindResource("ModernComboBoxStyle") as Style;

            // RoundCheckBoxStyle and ClearBtnStyle are declared in THIS page's Page.Resources, not in
            // the app-level Styles.xaml, so they must be resolved with the instance TryFindResource
            // (which walks Page -> App). Application.Current.TryFindResource would return null and the
            // checkboxes would silently fall back to the square WPF default.
            var chkStyle     = TryFindResource("RoundCheckBoxStyle") as Style;
            var iconBtnStyle = TryFindResource("ClearBtnStyle")      as Style;

            CheckBox NewCheck(bool isChecked, string? content = null) 
            {
                var cb = new CheckBox
                {
                    Content   = content,
                    IsChecked = isChecked,
                    FontSize  = 11,
                    Foreground = valFg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor    = System.Windows.Input.Cursors.Hand,
                };
                if (chkStyle != null) cb.Style = chkStyle;
                return cb;
            }

            var grid = new Grid { Margin = new Thickness(22, 14, 22, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Readers: current value of each row, by key. Rows: the label + editor pair, so a row can
            // be collapsed as a unit (its Auto-height RowDefinition then measures 0).
            var readers = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);
            var rows    = new Dictionary<string, (FrameworkElement Lbl, FrameworkElement Ed)>(StringComparer.OrdinalIgnoreCase);

            // Assigned after the loop; every editor's change event calls through it so the Detection
            // section can re-evaluate which of its rows still apply. Declared here because the
            // editors capture the VARIABLE, not its value.
            Action refresh = () => { };

            string? lastSection = null;
            int r = 0;

            foreach (var (section, key, label, kind) in IntuneSettingRows())
            {
                if (section != lastSection)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var sec = new TextBlock
                    {
                        Text = section, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = valFg,
                        Margin = new Thickness(0, r == 0 ? 0 : 16, 0, 6),
                    };
                    Grid.SetRow(sec, r); Grid.SetColumn(sec, 0); Grid.SetColumnSpan(sec, 2);
                    grid.Children.Add(sec);
                    lastSection = section; r++;
                }

                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var lbl = new TextBlock
                {
                    Text = label, FontSize = 11, Foreground = lblFg, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3),
                };
                Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0);
                grid.Children.Add(lbl);

                FrameworkElement editor;

                if (kind.StartsWith("combo:", StringComparison.Ordinal))
                {
                    var items = kind.Substring(6).Split('|');
                    var cmb = new ComboBox { Height = 26, FontSize = 11, Margin = new Thickness(0, 3, 0, 3) };
                    if (cmbStyle != null) cmb.Style = cmbStyle;
                    foreach (var it in items) cmb.Items.Add(new ComboBoxItem { Content = it });

                    string cur = Get(key);
                    cmb.SelectedIndex = 0;
                    for (int i = 0; i < items.Length; i++)
                        if (string.Equals(items[i], cur, StringComparison.OrdinalIgnoreCase)) { cmb.SelectedIndex = i; break; }

                    cmb.SelectionChanged += (_, _) => refresh();
                    readers[key] = () => (cmb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                    editor = cmb;
                }
                else if (kind.StartsWith("radio:", StringComparison.Ordinal))
                {
                    var opts = kind.Substring(6).Split('|');
                    string cur = Get(key);

                    // RulesFormat is matched the way LoadIntuneDefaults matches it - on the word
                    // "script" - not by exact string, so a hand-edited value still lands correctly.
                    int sel = 0;
                    if (key.Equals("RulesFormat", StringComparison.OrdinalIgnoreCase))
                        sel = cur.IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                    else
                        for (int i = 0; i < opts.Length; i++)
                            if (string.Equals(opts[i], cur, StringComparison.OrdinalIgnoreCase)) { sel = i; break; }

                    // Own container per pair: WPF groups unnamed RadioButtons by their parent.
                    var pnl  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                    var btns = new List<RadioButton>();
                    for (int i = 0; i < opts.Length; i++)
                    {
                        var rb = new RadioButton
                        {
                            Content = opts[i], FontSize = 11, Foreground = valFg,
                            IsChecked = i == sel, VerticalAlignment = VerticalAlignment.Center,
                            Cursor = System.Windows.Input.Cursors.Hand,
                            Margin = new Thickness(0, 0, 16, 0),
                        };
                        rb.Checked += (_, _) => refresh();
                        btns.Add(rb);
                        pnl.Children.Add(rb);
                    }
                    readers[key] = () => btns.FirstOrDefault(b => b.IsChecked == true)?.Content?.ToString() ?? opts[0];
                    editor = pnl;
                }
                else if (kind == "check")
                {
                    var chk = NewCheck(IsYes(key));
                    chk.Margin     = new Thickness(0, 5, 0, 5);
                    chk.Checked   += (_, _) => refresh();
                    chk.Unchecked += (_, _) => refresh();
                    readers[key] = () => chk.IsChecked == true ? "Yes" : "No";
                    editor = chk;
                }
                else if (kind == "arch")
                {
                    // Requirements tab uses three checkboxes; the JSON stores one string that
                    // LoadIntuneDefaults substring-matches for "32" / "64" / "ARM".
                    string cur = Get(key);
                    var pnl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                    var c32 = NewCheck(cur.Contains("32",  StringComparison.OrdinalIgnoreCase), "32-bit");
                    var c64 = NewCheck(cur.Contains("64",  StringComparison.OrdinalIgnoreCase), "64-bit");
                    var cAr = NewCheck(cur.Contains("ARM", StringComparison.OrdinalIgnoreCase), "ARM64");
                    c32.Margin = new Thickness(0, 0, 16, 0);
                    c64.Margin = new Thickness(0, 0, 16, 0);
                    pnl.Children.Add(c32); pnl.Children.Add(c64); pnl.Children.Add(cAr);
                    readers[key] = () =>
                    {
                        var picked = new List<string>();
                        if (c32.IsChecked == true) picked.Add("32Bit");
                        if (c64.IsChecked == true) picked.Add("64Bit");
                        if (cAr.IsChecked == true) picked.Add("ARM64");
                        return string.Join(";", picked);
                    };
                    editor = pnl;
                }
                else if (kind == "browse")
                {
                    // Icon INSIDE the box, overlaid at the right edge - the same pattern the page uses
                    // for its clear buttons (Grid overlay + right padding on the TextBox to keep the
                    // text clear of the glyph), rather than a separate button beside it.
                    var g  = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    var tb = MakeSettingsTextBox(Get(key), valFg, 26, false);
                    tb.Margin  = new Thickness(0);
                    tb.Padding = new Thickness(8, 0, 30, 0);
                    g.Children.Add(tb);

                    var browse = new Button
                    {
                        Content  = "\U0001F4C2",
                        Width    = 22,
                        Height   = 20,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Margin   = new Thickness(0, 0, 5, 0),
                        ToolTip  = "Browse for a logo",
                    };
                    if (iconBtnStyle != null) browse.Style = iconBtnStyle;
                    browse.Click += (_, _) =>
                    {
                        var dlg = new OpenFileDialog
                        {
                            Title  = "Select logo",
                            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
                        };
                        if (dlg.ShowDialog() == true) tb.Text = dlg.FileName;
                    };
                    g.Children.Add(browse);

                    readers[key] = () => tb.Text ?? "";
                    editor = g;
                }
                else if (kind == "multi")
                {
                    var tb = MakeSettingsTextBox(Get(key), valFg, 54, true);
                    readers[key] = () => tb.Text ?? "";
                    editor = tb;
                }
                else
                {
                    var tb = MakeSettingsTextBox(Get(key), valFg, 26, false);
                    readers[key] = () => tb.Text ?? "";
                    editor = tb;
                }

                Grid.SetRow(editor, r); Grid.SetColumn(editor, 1);
                grid.Children.Add(editor);
                rows[key] = (lbl, editor);
                r++;
            }

            // ── Detection: the same conditional rows the Detection tab shows ────────────────
            // A flat list was wrong. On the tab a field only appears when it actually applies:
            //   * script mode replaces the MSI/File/Registry rules entirely
            //   * a rule's fields appear only when its checkbox is ticked
            //   * MSI operator appears only when "MSI product version check" = Yes
            //   * File operator appears only when the method is not "File or folder exists"
            //   * Registry operator appears only when the method is a comparison, not a presence check
            // The 32-bit checkboxes stay visible whenever their rule is on - the tab hides only the
            // operator and its value, not the bitness flag.
            void Show(string key, bool visible)
            {
                if (!rows.TryGetValue(key, out var pair)) return;
                var v = visible ? Visibility.Visible : Visibility.Collapsed;
                pair.Lbl.Visibility = v;
                pair.Ed.Visibility  = v;
            }

            void ApplyDetectionVisibility()
            {
                bool script = readers["RulesFormat"]().IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0;
                bool manual = !script;

                bool msiOn = manual && readers["DetectionTypeMSI"]().Equals("Yes", StringComparison.OrdinalIgnoreCase);
                bool fileOn = manual && readers["DetectionTypeFile"]().Equals("Yes", StringComparison.OrdinalIgnoreCase);
                bool regOn  = manual && readers["DetectionTypeRegistry"]().Equals("Yes", StringComparison.OrdinalIgnoreCase);

                bool msiVer      = msiOn  && readers["DetectionMsiVersionCheck"]().Equals("Yes", StringComparison.OrdinalIgnoreCase);
                bool fileNeedsOp = fileOn && FileMethodNeedsValueS(readers["DetectionFileDetectionMethod"]());
                bool regNeedsOp  = regOn  && RegMethodNeedsValueS(readers["DetectionRegDetectionMethod"]());

                Show("DetectionTypeMSI",             manual);
                Show("DetectionMsiVersionCheck",     msiOn);
                Show("DetectionMsiOperator",         msiVer);

                Show("DetectionTypeFile",            manual);
                Show("DetectionFileDetectionMethod", fileOn);
                Show("DetectionFileOperator",        fileNeedsOp);
                Show("DetectionFile32Bit",           fileOn);

                Show("DetectionTypeRegistry",        manual);
                Show("DetectionRegValueName",        regOn);
                Show("DetectionRegDetectionMethod",  regOn);
                Show("DetectionRegOperator",         regNeedsOp);
                Show("DetectionReg32Bit",            regOn);

                Show("DetectionScriptEnforceSignatureCheck", script);
                Show("DetectionScriptRunAs32Bit",            script);
            }

            refresh = ApplyDetectionVisibility;
            ApplyDetectionVisibility();

            var sv = new ScrollViewer
            {
                Content = grid,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Grid.SetRow(sv, 1);
            outer.Children.Add(sv);

            var btnBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 22, 16),
            };
            var bCancel = DlgBtnStyled("\u2715  Cancel", "neutral", 100);
            bCancel.Margin = new Thickness(0, 0, 8, 0);
            bCancel.Click += (_, _) => win.Close();

            var bSave = DlgBtnStyled("\U0001F4BE  Save", "blue", 100);
            bSave.Click += (_, _) =>
            {
                // These detection sub-values (a rule's method / operator / version-check / value
                // name / 32-bit flag) are KEPT even while their rule checkbox is off, so unchecking
                // a rule and re-checking it later restores exactly what was configured before. Intune
                // only reads them when the rule type is on (DetectionType* = Yes), so keeping them
                // while off is harmless. Everything else still blanks when hidden, so a stale value
                // from the other mode (script vs manual) is not persisted.
                var keepWhenHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "DetectionMsiVersionCheck", "DetectionMsiOperator",
                    "DetectionFileDetectionMethod", "DetectionFileOperator", "DetectionFile32Bit",
                    "DetectionRegValueName", "DetectionRegDetectionMethod", "DetectionRegOperator", "DetectionReg32Bit",
                };

                foreach (var kv in readers)
                {
                    bool hidden = rows.TryGetValue(kv.Key, out var pr)
                               && pr.Ed.Visibility != Visibility.Visible;
                    // Blank hidden rows, except the detection sub-values which we preserve so a
                    // re-checked rule shows its last saved settings.
                    root[kv.Key] = (hidden && !keepWhenHidden.Contains(kv.Key)) ? "" : kv.Value();
                }
                if (WriteProtectedFile(SettingsPath, root.ToJsonString(
                        new JsonSerializerOptions { WriteIndented = true }), out string saveErr))
                {
                    win.Close();
                    ShowThemedMessage("Settings", "\u2714", "green",
                        $"IntuneSettings.json saved.\n\n{SettingsPath}");
                }
                else
                {
                    ShowThemedMessage("Settings", "\u26a0", "amber",
                        $"IntuneSettings.json could not be saved.\n\n{SettingsPath}\n\n{saveErr}");
                }
            };
            btnBar.Children.Add(bCancel);
            btnBar.Children.Add(bSave);
            Grid.SetRow(btnBar, 2);
            outer.Children.Add(btnBar);

            rootBorder.Child = outer;
            win.Content = rootBorder;
            win.ShowDialog();
        }

        private static TextBox MakeSettingsTextBox(string text, System.Windows.Media.Brush fg,
                                                   double height, bool multiline)
        {
            var tb = new TextBox
            {
                Text            = text,
                FontSize        = 11,
                Height          = height,
                Margin          = new Thickness(0, 3, 0, 3),
                Padding         = new Thickness(8, multiline ? 4 : 0, 8, multiline ? 4 : 0),
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                AcceptsReturn   = multiline,
                TextWrapping    = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            };

            // Take the app's own text box style - that is where the rounded corners live. The radius
            // is set on a Border inside the style's ControlTemplate, so it cannot be produced by
            // setting Background / BorderBrush / BorderThickness on a bare TextBox, which is what the
            // dialog was doing. These are the same two styles the page's own fields use, so the boxes
            // here now match the tabs exactly (and follow the theme).
            string styleKey = multiline ? "MultiLineTextBoxStyle" : "ModernTextBoxStyle";
            if (Application.Current.TryFindResource(styleKey) is Style st)
            {
                tb.Style = st;
            }
            else
            {
                // Only if the theme dictionary is missing - keeps the box legible rather than
                // falling back to the raw WPF default.
                tb.Background      = TC("InputBgBrush");
                tb.Foreground      = fg;
                tb.BorderBrush     = TC("BorderMainBrush");
                tb.BorderThickness = new Thickness(1);
            }
            return tb;
        }

        private static System.Windows.Media.SolidColorBrush TC(string key)
        {
            var res = Application.Current.TryFindResource(key);
            return res is System.Windows.Media.SolidColorBrush b
                ? b
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
        }

        private static Border MakeDialogHeaderThemed(string icon, string title, string style, Action onClose)
        {
            string bgKey = style switch
            {
                "red"   => "DlgHdrRedBg",   "amber" => "DlgHdrAmberBg",
                "green" => "DlgHdrGreenBg", "blue"  => "DlgHdrBlueBg",
                _       => "DlgHdrNeutralBg"
            };
            string fgKey = style switch
            {
                "red"   => "DlgHdrRedFg",   "amber" => "DlgHdrAmberFg",
                "green" => "DlgHdrGreenFg", "blue"  => "DlgHdrBlueFg",
                _       => "DlgHdrNeutralFg"
            };
            var bg = Application.Current.TryFindResource(bgKey) is System.Windows.Media.Color c
                ? c : System.Windows.Media.Color.FromRgb(0x25, 0x28, 0x44);
            var fg = Application.Current.TryFindResource(fgKey) is System.Windows.Media.Color f
                ? f : System.Windows.Media.Color.FromRgb(0xEA, 0xED, 0xF5);
            return MakeDialogHeader(icon, title, bg, fg, onClose);
        }

        private static Border MakeDialogHeader(string icon, string title,
            System.Windows.Media.Color bg, System.Windows.Media.Color fg, Action onClose)
        {
            var hdr = new Border
            {
                Background   = new System.Windows.Media.SolidColorBrush(bg),
                CornerRadius = new CornerRadius(10, 10, 0, 0),
                Padding      = new Thickness(16, 12, 16, 12)
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ic = new TextBlock { Text = icon, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var ti = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(fg), VerticalAlignment = VerticalAlignment.Center };
            var cl = new Button { Content = "✕", FontSize = 12,
                Foreground = TC("TextSecondaryBrush"),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            cl.Click += (_, _) => onClose();

            Grid.SetColumn(ic, 0); Grid.SetColumn(ti, 1); Grid.SetColumn(cl, 2);
            g.Children.Add(ic); g.Children.Add(ti); g.Children.Add(cl);
            hdr.Child = g;
            Grid.SetRow(hdr, 0);
            return hdr;
        }

        private static Button DlgBtnStyled(string text, string style, double width)
        {
            string bgKey     = style switch { "blue"  => "DlgBtnBlueBgBrush",   "green" => "DlgBtnGreenBgBrush",
                                              "red"   => "DlgBtnRedBgBrush",    "amber" => "DlgBtnAmberBgBrush",
                                              _       => "DlgBtnNeutralBgBrush" };
            string fgKey     = style switch { "blue"  => "DlgBtnBlueFgBrush",   "green" => "DlgBtnGreenFgBrush",
                                              "red"   => "DlgBtnRedFgBrush",    "amber" => "DlgBtnAmberFgBrush",
                                              _       => "DlgBtnNeutralFgBrush" };
            string borderKey = style switch { "blue"  => "DlgBtnBlueBorderBrush",  "green" => "DlgBtnGreenBorderBrush",
                                              "red"   => "DlgBtnRedBorderBrush",   "amber" => "DlgBtnAmberBorderBrush",
                                              _       => "DlgBtnNeutralBorderBrush" };
            var btn = new Button
            {
                Content         = text,
                Width           = width,
                Height          = 32,
                Margin          = new Thickness(0, 0, 8, 0),
                Background      = TC(bgKey),
                Foreground      = TC(fgKey),
                BorderBrush     = TC(borderKey),
                BorderThickness = new Thickness(1),
                FontSize        = 12,
                FontWeight      = FontWeights.SemiBold,
                Cursor          = System.Windows.Input.Cursors.Hand
            };

            // Rounded-corner template; keeps the style colours and adds a subtle hover.
            var tpl = new ControlTemplate(typeof(Button));
            var bd  = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty,      TC(bgKey));
            bd.SetValue(Border.BorderBrushProperty,     TC(borderKey));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty,    new CornerRadius(6));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
            tpl.Triggers.Add(hov);
            btn.Template = tpl;

            return btn;
        }
    }  // end PublishPackagePage

    // ══════════════════════════════════════════════════════════════════════
    // Detection rule model
    // ══════════════════════════════════════════════════════════════════════
    internal class DetectionRule
    {
        public string RuleType       { get; set; } = "File";
        // Whether this card's radio button is selected as primary detection rule
        public bool   IsDefault      { get; set; } = false;
        public bool   Enabled        { get; set; } = false;  // checkbox state: include this rule type in detection
        // MSI
        public string MsiProductCode { get; set; } = "";
        public string MsiVersion     { get; set; } = "";
        public string MsiCheck       { get; set; } = "Not configured";
        public string MsiOperator    { get; set; } = "";
        // File
        public string FilePath       { get; set; } = "";
        public string FileName       { get; set; } = "";
        public string FileCheck      { get; set; } = "";
        public string FileOperator   { get; set; } = "";
        public string FileValue      { get; set; } = "";  // version/size/date value to compare
        public string FileFullPath    { get; set; } = "";  // full path of the installed main file (for size/date/version auto-fill)
        public string FileTimeValue   { get; set; } = "";  // time-of-day for Date created/modified detection
        public bool   File32Bit      { get; set; } = false;
        // Registry
        public string RegKeyPath     { get; set; } = "";
        public string RegValue       { get; set; } = "";
        public string RegCheck       { get; set; } = "";
        public string RegOperator    { get; set; } = "";
        public string RegValueData   { get; set; } = "";  // actual data value e.g. "4.0.12218.0"
        public bool   Reg32Bit       { get; set; } = false;
        // Script
        public string ScriptPath     { get; set; } = "";
        public bool   ScriptEnforce  { get; set; } = false;
        public bool   ScriptRun32    { get; set; } = false;
    }
}
