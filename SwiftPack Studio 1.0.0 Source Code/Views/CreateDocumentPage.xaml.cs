using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SwiftPack.Models;
using SwiftPack.Services;
namespace SwiftPack.Views
{
    public partial class CreateDocumentPage : Page
    {
        private readonly List<string> _uatUsers = new();
        // Last non-empty detection block shown, keyed by package — used to keep the
        // Detection box populated across an Uninstall (when the app is gone from the registry).
        private static string _lastDetectionText = "";
        private static string _lastDetectionPkg  = "";
        // ── CreateDocument.json config ─────────────────────────────────────────
        private static CreateDocumentConfig _docCfg = new();
        private static void LoadDocConfig()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new[]
                {
                    @"C:\Program Files\Smekal\SwiftPack Studio\Templates\CreateDocument.json",
                    System.IO.Path.Combine(exeDir, "Templates", "CreateDocument.json"),
                    System.IO.Path.Combine(exeDir, "CreateDocument.json"),
                };
                foreach (var p in candidates)
                {
                    if (!File.Exists(p)) continue;
                    var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var cfg  = System.Text.Json.JsonSerializer.Deserialize<CreateDocumentConfig>(File.ReadAllText(p), opts);
                    if (cfg != null) { _docCfg = cfg; break; }
                }
            }
            catch { }
        }
        // Runtime overrides — set by auto-package flow from per-app JSON (take priority over CreateDocument.json)
        public static string? RuntimeCopyTxtOverride  { get; set; } = null;
        public static string? RuntimeCopyDocxOverride { get; set; } = null;
        /// <summary>Documents folder to copy exported files into — set by auto-package flow.</summary>
        public static string? RuntimeDocumentsFolder  { get; set; } = null;
        // Priority: per-app JSON override → CreateDocument.json flag
        private static bool ShouldCopyTxt =>
            string.Equals(RuntimeCopyTxtOverride  ?? _docCfg.CopytxtToDocuments,  "Yes", StringComparison.OrdinalIgnoreCase);
        private static bool ShouldCopyDocx =>
            string.Equals(RuntimeCopyDocxOverride ?? _docCfg.CopydocxToDocuments, "Yes", StringComparison.OrdinalIgnoreCase);
        // Fixed default. DocumentsPath was removed from CreateDocument.json and the
        // Settings dialog; exports and copies go here unless the auto-package flow has
        // set RuntimeDocumentsFolder.
        // Note: RuntimeDocumentsFolder is intentionally NOT used here — that folder is for
        // CSV/HTML/LOG files (Validate Package + Execute Package). The .txt/.docx from
        // Create Document go to the default C:\Temp\Create Document\.
        private static string ResolveDocumentsFolder() => @"C:\Temp\Create Document";
        /// <summary>Copies file to the resolved Documents folder if the file exists.</summary>
        private static void TryCopyToDocuments(string filePath)
        {
            try
            {
                string folder = ResolveDocumentsFolder();
                if (string.IsNullOrEmpty(folder) || !File.Exists(filePath)) return;
                Directory.CreateDirectory(folder);
                File.Copy(filePath, System.IO.Path.Combine(folder, System.IO.Path.GetFileName(filePath)), overwrite: true);
            }
            catch { /* best-effort */ }
        }
        // Resolves the package's own Documents folder: the auto-package flow's
        // RuntimeDocumentsFolder (<PackageRoot>\Documents) when set, otherwise
        // C:\Packages\<PackageName>\Documents. Used for the .docx copy.
        private static string ResolvePackageDocumentsFolder(string baseName) =>
            !string.IsNullOrWhiteSpace(RuntimeDocumentsFolder)
                ? RuntimeDocumentsFolder!
                : System.IO.Path.Combine(@"C:\Packages", baseName, "Documents");
        /// <summary>Copies file to the package's Documents folder if the file exists.</summary>
        private static void TryCopyToPackageDocuments(string filePath, string baseName)
        {
            try
            {
                string folder = ResolvePackageDocumentsFolder(baseName);
                if (string.IsNullOrEmpty(folder) || !File.Exists(filePath)) return;
                Directory.CreateDirectory(folder);
                File.Copy(filePath, System.IO.Path.Combine(folder, System.IO.Path.GetFileName(filePath)), overwrite: true);
            }
            catch { /* best-effort */ }
        }
        public CreateDocumentPage()
        {
            InitializeComponent();
            LoadDocConfig(); // load CreateDocument.json settings
            // ── Mouse click positioning for all TextBoxes ─────────────────────
            // Click before text → go to start; click after → go to end;
            // click+drag → select text. This handles the "empty area" clicks.
            AddHandler(TextBox.PreviewMouseDownEvent,
                new System.Windows.Input.MouseButtonEventHandler(OnTextBoxPreviewMouseDown), true);
        }
        private static void OnTextBoxPreviewMouseDown(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            // Find the TextBox that was clicked
            if (e.OriginalSource is not System.Windows.DependencyObject src) return;
            var tb = FindParent<TextBox>(src);
            if (tb == null || string.IsNullOrEmpty(tb.Text)) return;
            // Get click position relative to the TextBox
            Point pt = e.GetPosition(tb);
            double midX = tb.ActualWidth / 2;
            // If clicking in the empty area (not on text), focus and position caret
            // The PART_ContentHost ScrollViewer is hit-test visible only on text chars;
            // clicks on padding/empty space reach the Border which is the OriginalSource.
            // We detect "empty area" when OriginalSource is Border (not the content host).
            bool clickedEmptyArea = e.OriginalSource is System.Windows.Controls.Border
                                 || e.OriginalSource is System.Windows.Controls.Grid;
            if (clickedEmptyArea)
            {
                tb.Focus();
                if (pt.X < midX)
                    tb.SelectionStart = 0;
                else
                    tb.SelectionStart = tb.Text.Length;
                tb.SelectionLength = 0;
                e.Handled = true;
            }
        }
        private static T? FindParent<T>(System.Windows.DependencyObject child) where T : System.Windows.DependencyObject
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T typed) return typed;
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return null;
        }
        // ── Auto-fill from AutoPackage session ────────────────────────────────
        /// <summary>
        /// Fills fields from the session. Only overwrites a field when the session has a
        /// non-empty value — preserving anything the user has already typed.
        /// Log paths and intunewin data always overwrite (they come from completed features).
        /// </summary>
        /// <summary>Clears every field on the page — called before LoadSession so no stale data remains.</summary>
        public void ClearAllFields()
        {
            TxtPackageName.Text = TxtAppNameDisp.Text = TxtVendorDisp.Text = TxtVersionDisp.Text = "";
            TxtSourceMedia.Text = TxtCloseAppsDoc.Text = TxtOSDisp.Text = TxtDateDisp.Text = "";
            TxtInstallLog.Text  = TxtUninstallLog.Text = "";
            TxtIntunewinName.Text = TxtIntunewinSize.Text = "";
            TxtIntuneName.Text  = TxtPublisher.Text = TxtIntuneVersion.Text = "";
            TxtInstallCmd.Text  = TxtUninstallCmd.Text = "";
            TxtDescription.Text = TxtAssignmentGroup.Text = TxtDetectionTypeDoc.Text = "";
            TxtInstallBehaviour.Text = "";
            TxtDeploymentType.Text = "";
            // Clear Request Details fields too
            try
            {
                foreach (var tb in FindVisualChildren<TextBox>(this))
                    tb.Text = "";
            }
            catch { }
        }
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
            }
        }
        public void LoadSession(CreateDocumentSession s)
        {
            void Set(TextBox tb, string val) { tb.Text = val ?? ""; }
            // ── Package Information ───────────────────────────────────────────
            Set(TxtPackageName,  s.PackageName);
            Set(TxtAppNameDisp,  s.AppName);
            Set(TxtVendorDisp,   s.AppVendor);
            Set(TxtVersionDisp,  s.AppVersion);
            Set(TxtSourceMedia,  s.SourceMediaLocation);
            Set(TxtCloseAppsDoc, s.CloseApps);
            // OS — always refresh; Date — always refresh from session or today
            TxtOSDisp.Text   = GetOsString();
            TxtDateDisp.Text = string.IsNullOrWhiteSpace(s.ScriptDate)
                ? DateTime.Now.ToString("yyyy-MM-dd") : s.ScriptDate;
            // ── Deployment Configuration ──────────────────────────────────────
            Set(TxtInstallCmd,      s.InstallCommandLine);
            Set(TxtUninstallCmd,    s.UninstallCommandLine);
            Set(TxtDescription,     s.Description);
            Set(TxtAssignmentGroup, s.GroupName);
            // Log paths — always overwrite
            TxtInstallLog.Text   = s.InstallLogPath   ?? "";
            TxtUninstallLog.Text = s.UninstallLogPath ?? "";
            // Intunewin — always overwrite
            TxtIntunewinSize.Text = s.IntunewinFileSize ?? "";
            TxtIntunewinName.Text = !string.IsNullOrEmpty(s.IntunewinPath)
                ? Path.GetFileName(s.IntunewinPath) : "";
            // Intune app fields — always overwrite
            Set(TxtIntuneName,    s.AppName);
            Set(TxtPublisher,     s.AppVendor);
            Set(TxtIntuneVersion, s.AppVersion);
            // Install Behaviour — always set (text box; default to System when blank)
            TxtInstallBehaviour.Text = string.IsNullOrWhiteSpace(s.InstallBehaviour) ? "System" : s.InstallBehaviour;
            // Detection rules — multi-line block. Sources in priority order:
            //   1) s.DetectionSummary   — LIVE Publish Package Detection tab (MSI/File/Registry
            //                             cards as currently shown — wins whenever it appears,
            //                             so the post-install auto-prefill shows here even
            //                             before the per-app json is saved).
            //   2) per-app Intune JSON  — the file the Publish Package page saves (its checkboxes).
            //   3) AutoPackage log       — MSI/File/Registry captured at packaging (survives uninstall).
            //   4) per-field session     — Auto Package flow values.
            //   5) uninstall hive         — read the installed app directly (Execute Package flow).
            //   6) preserved last value   — keep the previous detection for the SAME package, so an
            //                               Uninstall (app gone from registry) does NOT wipe the box.
            string detText = s.DetectionSummary ?? "";
            if (string.IsNullOrWhiteSpace(detText))
                detText = ReadDetectionFromPerAppJson(s.IntunewinPath, s.PackageRootFolder);
            if (string.IsNullOrWhiteSpace(detText))
                detText = ReadDetectionFromPackageLog(s.PackageName);
            if (string.IsNullOrWhiteSpace(detText))
            {
                var det = new List<string>();
                if (!string.IsNullOrWhiteSpace(s.DetectionType))
                    det.Add($"Detection Type   : {s.DetectionType}");
                if (!string.IsNullOrEmpty(s.ProductCode))
                    det.Add($"MSI Product Code : {s.ProductCode}");
                if (!string.IsNullOrEmpty(s.DetectionRegKey))
                    det.Add($"Registry Key     : {s.DetectionRegKey}");
                string filePart =
                    !string.IsNullOrEmpty(s.DetectionFilePath) && !string.IsNullOrEmpty(s.DetectionFileName)
                        ? $"{s.DetectionFilePath}\\{s.DetectionFileName}"
                        : !string.IsNullOrEmpty(s.DetectionFileName) ? s.DetectionFileName
                        : s.DetectionFilePath ?? "";
                if (!string.IsNullOrEmpty(filePart))
                    det.Add($"File             : {filePart}");
                detText = string.Join(Environment.NewLine, det);
            }
            if (string.IsNullOrWhiteSpace(detText))
                detText = ReadDetectionFromUninstallHive(s.ProductCode, s.AppName, s.AppVendor);
            // Preserve-on-uninstall: if every source is empty (e.g. the app was just uninstalled
            // and is gone from the registry), keep the last detection we showed for THIS package
            // instead of blanking it. A genuinely different package will not match the cache key.
            if (string.IsNullOrWhiteSpace(detText))
            {
                if (!string.IsNullOrWhiteSpace(_lastDetectionText) &&
                    string.Equals(_lastDetectionPkg, s.PackageName ?? "", StringComparison.OrdinalIgnoreCase))
                    detText = _lastDetectionText;
            }
            else
            {
                _lastDetectionText = detText;
                _lastDetectionPkg  = s.PackageName ?? "";
            }
            TxtDetectionTypeDoc.Text = detText;
            // Deployment type — always set (text box)
            TxtDeploymentType.Text = s.DeploymentType ?? "";
        }
        // ══════════════════════════════════════════════════════════════
        // DETECTION FROM THE AUTOPACKAGE LOG  (self-contained)
        // The Auto Package flow writes an "Auto-prefill Detection" block into
        // %TEMP%\AutoPackage_<name>.log, capturing File/Registry/MSI BEFORE the
        // source is uninstalled. That log survives the uninstall, so this is the
        // reliable place to recover detection when the registry no longer has it.
        // Returns "" when no log / no detection lines.
        // ══════════════════════════════════════════════════════════════
        private static string ReadDetectionFromPackageLog(string? packageName)
        {
            try
            {
                string tempDir = System.IO.Path.GetTempPath();
                if (!Directory.Exists(tempDir)) return "";
                var logs = Directory.GetFiles(tempDir, "AutoPackage_*.log");
                if (logs.Length == 0) return "";
                // Prefer a log whose name shares a prefix with the package name; else newest.
                string? chosen = null;
                if (!string.IsNullOrWhiteSpace(packageName))
                {
                    string norm = new string(packageName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                    if (norm.Length >= 4)
                    {
                        string key = norm.Substring(0, Math.Min(8, norm.Length));
                        chosen = logs.OrderByDescending(File.GetLastWriteTime).FirstOrDefault(f =>
                            new string(System.IO.Path.GetFileNameWithoutExtension(f).Where(char.IsLetterOrDigit).ToArray())
                                .ToLowerInvariant().Contains(key));
                    }
                }
                chosen ??= logs.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (chosen == null || !File.Exists(chosen)) return "";
                static string V(string line) { int i = line.IndexOf(" : "); return i >= 0 ? line.Substring(i + 3).Trim() : ""; }
                string msiCode = "", fPath = "", fName = "", fVal = "", rKey = "", rValName = "", rVal = "";
                foreach (string raw in File.ReadLines(chosen))
                {
                    string l = raw.Trim();
                    if (l.Length > 11 && l[0] == '[' && l[9] == ']' && l[10] == ' ') l = l.Substring(11).Trim();
                    if      (l.StartsWith("Detection.MSI.ProductCode", StringComparison.OrdinalIgnoreCase)) msiCode  = V(l);
                    else if (l.StartsWith("Detection.File.FolderPath", StringComparison.OrdinalIgnoreCase)) fPath    = V(l);
                    else if (l.StartsWith("Detection.File.Path",       StringComparison.OrdinalIgnoreCase)) fPath    = V(l);
                    else if (l.StartsWith("Detection.File.Name",       StringComparison.OrdinalIgnoreCase)) fName    = V(l);
                    else if (l.StartsWith("Detection.File.Value",      StringComparison.OrdinalIgnoreCase)) fVal     = V(l);
                    else if (l.StartsWith("Detection.Reg.KeyPath",     StringComparison.OrdinalIgnoreCase)) rKey     = V(l);
                    else if (l.StartsWith("Detection.Reg.ValueName",   StringComparison.OrdinalIgnoreCase)) rValName = V(l);
                    else if (l.StartsWith("Detection.Reg.Value",       StringComparison.OrdinalIgnoreCase)) rVal     = V(l);
                }
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(msiCode))
                {
                    sb.AppendLine("MSI");
                    sb.AppendLine("  Product code : " + msiCode);
                }
                if (!string.IsNullOrWhiteSpace(fPath) || !string.IsNullOrWhiteSpace(fName))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("File");
                    if (!string.IsNullOrWhiteSpace(fPath)) sb.AppendLine("  Path          : " + fPath);
                    if (!string.IsNullOrWhiteSpace(fName)) sb.AppendLine("  File or folder: " + fName);
                    if (!string.IsNullOrWhiteSpace(fVal))
                    {
                        sb.AppendLine("  Method        : String (version)");
                        sb.AppendLine("  Value         : Equal to " + fVal);
                    }
                }
                if (!string.IsNullOrWhiteSpace(rKey))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("Registry");
                    sb.AppendLine("  Key path      : " + rKey);
                    if (!string.IsNullOrWhiteSpace(rValName)) sb.AppendLine("  Value name    : " + rValName);
                    if (!string.IsNullOrWhiteSpace(rVal))
                    {
                        sb.AppendLine("  Method        : Version comparison");
                        sb.AppendLine("  Value         : Equal to " + rVal);
                    }
                }
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }
        // ══════════════════════════════════════════════════════════════
        // DETECTION FROM THE PER-APP INTUNE JSON  (self-contained)
        // Reads the companion <intunewin-basename>.json that the Publish Package
        // page auto-saves and formats the ENABLED MSI / File / Registry rules —
        // so whatever is CHECKED on that page's Detection tab appears here.
        // Located via the intunewin path, else the package's Intune\ folder.
        // Returns "" when no JSON or no enabled rule.
        // ══════════════════════════════════════════════════════════════
        private static string ReadDetectionFromPerAppJson(string? intunewinPath, string? packageRootFolder)
        {
            try
            {
                string? jsonPath = null;
                if (!string.IsNullOrWhiteSpace(intunewinPath))
                {
                    string cand = System.IO.Path.ChangeExtension(intunewinPath, ".json");
                    if (File.Exists(cand)) jsonPath = cand;
                }
                if (jsonPath == null && !string.IsNullOrWhiteSpace(packageRootFolder))
                {
                    foreach (string dir in new[] { System.IO.Path.Combine(packageRootFolder, "Intune"), packageRootFolder })
                    {
                        if (!Directory.Exists(dir)) continue;
                        var j = Directory.GetFiles(dir, "*.json")
                            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                        if (j != null) { jsonPath = j; break; }
                    }
                }
                if (jsonPath == null || !File.Exists(jsonPath)) return "";
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;
                string G(string k) => root.TryGetProperty(k, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (p.GetString() ?? "") : "";
                bool Yes(string k) => G(k).Equals("Yes", StringComparison.OrdinalIgnoreCase);
                // Script mode
                if (G("RulesFormat").IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string sp = G("DetectionScriptFile");
                    return string.IsNullOrWhiteSpace(sp) ? "" : "Detection script: " + sp;
                }
                var sb = new System.Text.StringBuilder();
                if (Yes("DetectionTypeMSI"))
                {
                    sb.AppendLine("MSI");
                    if (!string.IsNullOrWhiteSpace(G("DetectionMsiProductCode")))
                        sb.AppendLine("  Product code : " + G("DetectionMsiProductCode"));
                    if (Yes("DetectionMsiVersionCheck"))
                        sb.AppendLine("  Version      : " +
                            (string.IsNullOrWhiteSpace(G("DetectionMsiOperator")) ? "" : G("DetectionMsiOperator") + " ") +
                            G("DetectionMsiValue"));
                }
                if (Yes("DetectionTypeFile"))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("File");
                    if (!string.IsNullOrWhiteSpace(G("DetectionFileFolderPath"))) sb.AppendLine("  Path          : " + G("DetectionFileFolderPath"));
                    if (!string.IsNullOrWhiteSpace(G("DetectionFileFileName")))   sb.AppendLine("  File or folder: " + G("DetectionFileFileName"));
                    string method = G("DetectionFileDetectionMethod");
                    if (!string.IsNullOrWhiteSpace(method)) sb.AppendLine("  Method        : " + method);
                    string val = method.Equals("Size in MB", StringComparison.OrdinalIgnoreCase) ? G("DetectionFileSizeValue")
                               : method.StartsWith("Date",    StringComparison.OrdinalIgnoreCase) ? G("DetectionFileDateValue")
                               : G("DetectionFileValue");
                    bool needsVal = !string.IsNullOrWhiteSpace(method)
                        && !method.Equals("File or folder exists", StringComparison.OrdinalIgnoreCase);
                    if (needsVal && !string.IsNullOrWhiteSpace(val))
                        sb.AppendLine("  Value         : " +
                            (string.IsNullOrWhiteSpace(G("DetectionFileOperator")) ? "" : G("DetectionFileOperator") + " ") + val +
                            (string.IsNullOrWhiteSpace(G("DetectionFileTimeValue")) ? "" : " " + G("DetectionFileTimeValue")));
                    if (Yes("DetectionFile32Bit")) sb.AppendLine("  32-bit app on 64-bit clients");
                }
                if (Yes("DetectionTypeRegistry"))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("Registry");
                    if (!string.IsNullOrWhiteSpace(G("DetectionRegKeyPath")))   sb.AppendLine("  Key path      : " + G("DetectionRegKeyPath"));
                    if (!string.IsNullOrWhiteSpace(G("DetectionRegValueName"))) sb.AppendLine("  Value name    : " + G("DetectionRegValueName"));
                    string rmethod = G("DetectionRegDetectionMethod");
                    if (!string.IsNullOrWhiteSpace(rmethod)) sb.AppendLine("  Method        : " + rmethod);
                    bool rNeedsVal = !string.IsNullOrWhiteSpace(rmethod)
                        && !rmethod.Equals("Key exists",           StringComparison.OrdinalIgnoreCase)
                        && !rmethod.Equals("Key does not exist",   StringComparison.OrdinalIgnoreCase)
                        && !rmethod.Equals("Value exists",         StringComparison.OrdinalIgnoreCase)
                        && !rmethod.Equals("Value does not exist", StringComparison.OrdinalIgnoreCase);
                    if (rNeedsVal && !string.IsNullOrWhiteSpace(G("DetectionRegistryValue")))
                        sb.AppendLine("  Value         : " +
                            (string.IsNullOrWhiteSpace(G("DetectionRegOperator")) ? "" : G("DetectionRegOperator") + " ") + G("DetectionRegistryValue"));
                    if (Yes("DetectionReg32Bit")) sb.AppendLine("  32-bit app on 64-bit clients");
                }
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }
        // ══════════════════════════════════════════════════════════════
        // DETECTION FROM THE UNINSTALL HIVE  (self-contained)
        // After an install (Execute Package flow), the app's detection data is
        // present in the Windows uninstall registry hive. This reads it directly
        // and formats an MSI / File / Registry block — located by MSI product
        // code first, then by app name (+ vendor). Returns "" if not found.
        // ══════════════════════════════════════════════════════════════
        private static string ReadDetectionFromUninstallHive(string? productCode, string? appName, string? appVendor)
        {
            try
            {
                InstalledDetection? info = !string.IsNullOrWhiteSpace(productCode)
                    ? ReadInstalledByProductCode(productCode!) : null;
                bool have = info != null && (!string.IsNullOrWhiteSpace(info.InstallLocation)
                                          || !string.IsNullOrWhiteSpace(info.RegKeyPath)
                                          || !string.IsNullOrWhiteSpace(info.DisplayVersion));
                if (!have && !string.IsNullOrWhiteSpace(appName))
                {
                    info = ReadInstalledByName(appName!, appVendor ?? "");
                    have = info != null && (!string.IsNullOrWhiteSpace(info.InstallLocation)
                                         || !string.IsNullOrWhiteSpace(info.RegKeyPath)
                                         || !string.IsNullOrWhiteSpace(info.DisplayVersion));
                }
                if (!have || info == null) return "";
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(productCode))
                {
                    sb.AppendLine("MSI");
                    sb.AppendLine("  Product code : " + productCode!.Trim());
                    if (!string.IsNullOrWhiteSpace(info.DisplayVersion))
                        sb.AppendLine("  Version      : Greater than or equal to " + info.DisplayVersion);
                }
                if (!string.IsNullOrWhiteSpace(info.InstallLocation) || !string.IsNullOrWhiteSpace(info.MainExeName))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("File");
                    if (!string.IsNullOrWhiteSpace(info.InstallLocation)) sb.AppendLine("  Path          : " + info.InstallLocation);
                    if (!string.IsNullOrWhiteSpace(info.MainExeName))     sb.AppendLine("  File or folder: " + info.MainExeName);
                    string ver = !string.IsNullOrWhiteSpace(info.MainExeVersion) ? info.MainExeVersion : info.DisplayVersion;
                    if (!string.IsNullOrWhiteSpace(ver))
                    {
                        sb.AppendLine("  Method        : String (version)");
                        sb.AppendLine("  Value         : Equal to " + ver);
                    }
                }
                if (!string.IsNullOrWhiteSpace(info.RegKeyPath))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("Registry");
                    sb.AppendLine("  Key path      : " + info.RegKeyPath);
                    sb.AppendLine("  Value name    : DisplayVersion");
                    if (!string.IsNullOrWhiteSpace(info.DisplayVersion))
                    {
                        sb.AppendLine("  Method        : Version comparison");
                        sb.AppendLine("  Value         : Equal to " + info.DisplayVersion);
                    }
                }
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }
        private sealed record InstalledDetection(
            string RegKeyPath, string InstallLocation, string DisplayVersion,
            string MainExeName, string MainExeVersion);
        private static InstalledDetection? ReadInstalledByProductCode(string productCode)
        {
            string pc = productCode.Trim();
            if (!pc.StartsWith("{")) pc = "{" + pc;
            if (!pc.EndsWith("}"))   pc += "}";
            string[] subs =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + pc,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + pc,
            };
            foreach (string sub in subs)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(sub);
                    if (key == null) continue;
                    return BuildDetectionFromKey(key, "HKEY_LOCAL_MACHINE\\" + sub);
                }
                catch { }
            }
            return null;
        }
        private static InstalledDetection? ReadInstalledByName(string appName, string appVendor)
        {
            string[] hives =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            InstalledDetection? best = null; int bestScore = 0;
            foreach (string hivePath in hives)
            {
                try
                {
                    using var hive = Registry.LocalMachine.OpenSubKey(hivePath);
                    if (hive == null) continue;
                    foreach (string subName in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = hive.OpenSubKey(subName);
                            if (sub == null) continue;
                            string dispName = sub.GetValue("DisplayName") as string ?? "";
                            if (string.IsNullOrWhiteSpace(dispName)) continue;
                            int score =
                                string.Equals(dispName, appName, StringComparison.OrdinalIgnoreCase) ? 3 :
                                appName.Contains(dispName, StringComparison.OrdinalIgnoreCase)        ? 2 :
                                dispName.Contains(appName, StringComparison.OrdinalIgnoreCase)        ? 1 : 0;
                            if (score == 0) continue;
                            if (!string.IsNullOrWhiteSpace(appVendor))
                            {
                                string pub = sub.GetValue("Publisher") as string ?? "";
                                if (!string.IsNullOrWhiteSpace(pub) &&
                                    (pub.Contains(appVendor, StringComparison.OrdinalIgnoreCase) ||
                                     appVendor.Contains(pub, StringComparison.OrdinalIgnoreCase)))
                                    score += 1;
                            }
                            var det = BuildDetectionFromKey(sub, "HKEY_LOCAL_MACHINE\\" + hivePath + "\\" + subName);
                            if (score > bestScore) { bestScore = score; best = det; }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            if (best != null && bestScore >= 2) return best;
            if (best != null && bestScore == 1 && !string.IsNullOrWhiteSpace(best.InstallLocation)) return best;
            return null;
        }
        // Reads InstallLocation / DisplayVersion / main exe (name + version) from an
        // open uninstall registry key.
        private static InstalledDetection BuildDetectionFromKey(RegistryKey key, string regKeyPath)
        {
            string instLoc  = (key.GetValue("InstallLocation") as string ?? "").TrimEnd('\\');
            string dispVer  = key.GetValue("DisplayVersion") as string ?? "";
            string dispIcon = key.GetValue("DisplayIcon")    as string ?? "";
            if (string.IsNullOrWhiteSpace(instLoc) && !string.IsNullOrWhiteSpace(dispIcon))
            {
                string ip = dispIcon.Split(',')[0].Trim().Trim('"');
                if (ip.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    instLoc = (System.IO.Path.GetDirectoryName(ip) ?? "").TrimEnd('\\');
            }
            string exePath = "", exeName = "", exeVer = "";
            if (!string.IsNullOrWhiteSpace(dispIcon))
            {
                string ie = dispIcon.Split(',')[0].Trim().Trim('"');
                if (ie.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(ie))
                { exePath = ie; exeName = System.IO.Path.GetFileName(ie); }
            }
            if (string.IsNullOrWhiteSpace(exePath) && Directory.Exists(instLoc))
            {
                try
                {
                    var exes = Directory.GetFiles(instLoc, "*.exe", SearchOption.TopDirectoryOnly);
                    if (exes.Length > 0)
                    {
                        string b = exes.OrderByDescending(e => new FileInfo(e).Length).First();
                        exePath = b; exeName = System.IO.Path.GetFileName(b);
                    }
                }
                catch { }
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
            return new InstalledDetection(regKeyPath, instLoc, dispVer, exeName, exeVer);
        }
        private void AutoFillFromSession()
        {
            if (Application.Current.MainWindow is MainWindow mw)
                LoadSession(mw.BuildDocumentSession());
        }

        // ── Live mirror from the Publish Package page ─────────────────────────
        // Called by MainWindow whenever ANYTHING is changed manually on the Publish
        // Package page — updates the overlapping fields here immediately so both
        // pages always show the same data. Only non-empty values overwrite (an
        // empty publish field never erases data filled from another source).
        public void ApplyLivePublishData(string appName, string publisher, string version,
            string category, string description, string installCmd, string uninstallCmd,
            string installBehaviour, string detectionSummary)
        {
            void Upd(TextBox tb, string v) { if (!string.IsNullOrWhiteSpace(v)) tb.Text = v; }
            Upd(TxtIntuneName,       appName);
            Upd(TxtPublisher,        publisher);
            Upd(TxtIntuneVersion,    version);
            Upd(TxtCategory,         category);
            Upd(TxtDescription,      description);
            Upd(TxtInstallCmd,       installCmd);
            Upd(TxtUninstallCmd,     uninstallCmd);
            Upd(TxtInstallBehaviour, installBehaviour);
            if (!string.IsNullOrWhiteSpace(detectionSummary))
            {
                TxtDetectionTypeDoc.Text = detectionSummary;
                _lastDetectionText       = detectionSummary;   // keep preserve-on-uninstall cache in step
            }
        }

        // ── Build model from UI ───────────────────────────────────────────────
        private CreateDocumentModel BuildModel()
        {
            string deployType = string.IsNullOrWhiteSpace(TxtDeploymentType.Text)
                ? "" : TxtDeploymentType.Text.Trim();
            string cat = TxtCategory.Text.Trim();
            string installBeh = string.IsNullOrWhiteSpace(TxtInstallBehaviour.Text)
                ? "System" : TxtInstallBehaviour.Text.Trim();
            var m = new CreateDocumentModel
            {
                // Package Information
                PackageName          = TxtPackageName.Text,
                ApplicationName      = TxtAppNameDisp.Text,
                ApplicationVendor    = TxtVendorDisp.Text,
                ApplicationVersion   = TxtVersionDisp.Text,
                SourceMediaLocation  = TxtSourceMedia.Text,
                PackageCreatedBy     = TxtCreatedBy.Text,
                PackageValidatedBy   = TxtValidatedBy.Text,
                CloseApplications    = TxtCloseAppsDoc.Text,
                Shortcuts            = TxtShortcutsDoc.Text,
                InstallLogPath       = TxtInstallLog.Text,
                UninstallLogPath     = TxtUninstallLog.Text,
                // Request Details
                RequestId            = TxtRequestId.Text,
                SourceLocation       = TxtSourceLocation.Text,
                RequestorName        = TxtRequestorName.Text,
                ApplicationOwner     = TxtAppOwner.Text,
                Department           = TxtDepartment.Text,
                Country              = TxtCountry.Text,
                // Deployment Configuration
                IntunewinFileName    = TxtIntunewinName.Text,
                IntunewinFileSize    = TxtIntunewinSize.Text,
                IntuneName           = TxtIntuneName.Text,
                Publisher            = TxtPublisher.Text,
                IntuneVersion        = TxtIntuneVersion.Text,
                CategoryName         = cat,
                Description          = TxtDescription.Text,
                InstallCommandLine   = TxtInstallCmd.Text,
                UninstallCommandLine = TxtUninstallCmd.Text,
                InstallBehaviour     = installBeh,
                OperatingSystem      = TxtOSDisp.Text,
                DetectionType        = TxtDetectionTypeDoc.Text,
                Dependencies         = TxtDependencies.Text,
                Supersedence         = TxtSupersedence.Text,
                DeploymentType       = deployType,
                AssignmentGroup      = TxtAssignmentGroup.Text,
                PackageCompletedOn   = TxtDateDisp.Text,
                RollbackPlan         = TxtRollbackPlan.Text,
                // Other Details
                KnownIssues          = TxtKnownIssues.Text,
                AdditionalInfo       = TxtAdditionalInfo.Text,
            };
            foreach (string u in _uatUsers) m.UatUsers.Add(u);
            return m;
        }
        // ── Validation ────────────────────────────────────────────────────────
        private bool Validate(out string err)
        {
            // No mandatory fields — export always allowed
            err = string.Empty;
            return true;
        }
        // ── UAT tag input ─────────────────────────────────────────────────────
        private void TxtUatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            string name = TxtUatInput.Text.Trim();
            if (string.IsNullOrEmpty(name)) { e.Handled = true; return; }
            _uatUsers.Add(name);
            InsertUatTag(name);
            TxtUatInput.Text = string.Empty;
            e.Handled = true;
        }
        private void InsertUatTag(string name)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(235, 241, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(99, 138, 220)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 2, 6, 2), Margin = new Thickness(0, 0, 6, 3),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = name, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 80, 180)),
                VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button { Content = "×", FontSize = 13, Width = 18, Height = 18,
                Padding = new Thickness(0), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 80, 180)),
                Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0) };
            btn.Click += (_, _) => { _uatUsers.Remove(name); UatTagsPanel.Children.Remove(border); };
            sp.Children.Add(btn);
            border.Child = sp;
            UatTagsPanel.Children.Insert(UatTagsPanel.Children.IndexOf(TxtUatInput), border);
        }
        // ── Buttons ───────────────────────────────────────────────────────────
        /// <summary>
        /// Called by the auto-package flow when CopytxtToDocuments or CopydocxToDocuments = "Yes".
        /// Builds the document model from current fields and writes the files directly to
        /// RuntimeDocumentsFolder without showing a SaveFileDialog.
        /// </summary>
        public void AutoExportToDocuments()
        {
            // Auto-create the .txt in the package's Documents folder when
            // CopytxtToDocuments = "Yes" in CreateDocument.json. When the flag is "No"
            // (or absent) nothing is written.
            // The per-package folder comes from RuntimeDocumentsFolder, which MainWindow
            // sets to <PackageRoot>\Documents (e.g.
            //   C:\Packages\MicrosoftCorporation_MicrosoftSharedView_8.0.5725.0_01\Documents).
            // When RuntimeDocumentsFolder is empty (rare — no active package context) we
            // fall back to the C:\Temp\Create Document default.
            try
            {
                if (!ShouldCopyTxt && !ShouldCopyDocx) return;   // both flags "No" — nothing to do

                Validate(out _);
                var m = BuildModel();
                string baseName = SafeName(m);
                string folder = !string.IsNullOrWhiteSpace(RuntimeDocumentsFolder)
                    ? RuntimeDocumentsFolder!
                    : ResolveDocumentsFolder();
                Directory.CreateDirectory(folder);

                if (ShouldCopyTxt)
                    try { DocumentExportService.ExportTxt(m, System.IO.Path.Combine(folder, "CreateDocument_" + baseName + ".txt")); }
                    catch { /* best-effort */ }

                if (ShouldCopyDocx)
                    try { DocumentExportService.ExportDocx(m, System.IO.Path.Combine(folder, "CreateDocument_" + baseName + ".docx")); }
                    catch { /* best-effort */ }
            }
            catch { /* never interrupt the package flow */ }
        }
        private void BtnExportTxt_Click(object sender, RoutedEventArgs e)
        {
            Validate(out _);
            var m = BuildModel();
            var d = new SaveFileDialog { FileName = "CreateDocument_" + SafeName(m) + ".txt",
                Filter = "Text file (*.txt)|*.txt", DefaultExt = ".txt" };
            if (d.ShowDialog() != true) return;
            try
            {
                DocumentExportService.ExportTxt(m, d.FileName);
                SetStatus($"Saved: {d.FileName}", ok: true);
                OfferOpen(d.FileName);
                if (ShouldCopyTxt) TryCopyToDocuments(d.FileName);
            }
            catch (Exception ex) { SetStatus($"Export failed: {ex.Message}", ok: false); }
        }
        private void BtnExportDocx_Click(object sender, RoutedEventArgs e)
        {
            Validate(out _);
            var m = BuildModel();
            var d = new SaveFileDialog { FileName = "CreateDocument_" + SafeName(m) + ".docx",
                Filter = "Word document (*.docx)|*.docx", DefaultExt = ".docx" };
            if (d.ShowDialog() != true) return;
            try
            {
                DocumentExportService.ExportDocx(m, d.FileName);
                SetStatus($"Saved: {d.FileName}", ok: true);
                OfferOpen(d.FileName);
                if (ShouldCopyDocx) TryCopyToPackageDocuments(d.FileName, SafeName(m));
            }
            catch (Exception ex) { SetStatus($"Export failed: {ex.Message}", ok: false); }
        }
        // Save writes to the package's Documents folder, gated by the CreateDocument.json flags:
        //   CopytxtToDocuments  = "Yes" -> writes CreateDocument_<package>.txt
        //   CopydocxToDocuments = "Yes" -> writes CreateDocument_<package>.docx
        // Destination = ResolvePackageDocumentsFolder = RuntimeDocumentsFolder
        // (GetParent(scriptDir)\Documents) when set, else C:\Packages\<package>\Documents.
        private void BtnDocSave_Click(object sender, RoutedEventArgs e)
        {
            // A manual Save must honor CreateDocument.json, not stale per-run overrides left by a
            // prior auto-package chain (BtnDocSave is only ever a real click — the chain uses
            // AutoExportToDocuments instead).
            RuntimeCopyTxtOverride  = null;
            RuntimeCopyDocxOverride = null;

            Validate(out _);
            var m = BuildModel();
            string baseName = SafeName(m);

            if (!ShouldCopyTxt && !ShouldCopyDocx)
            {
                SetStatus("Nothing saved — both 'Copy to Documents' flags are No in CreateDocument.json.", ok: false);
                return;
            }

            string folder = ResolvePackageDocumentsFolder(baseName);
            try
            {
                Directory.CreateDirectory(folder);
                string? lastPath = null;
                if (ShouldCopyTxt)
                {
                    string txt = System.IO.Path.Combine(folder, "CreateDocument_" + baseName + ".txt");
                    DocumentExportService.ExportTxt(m, txt);
                    lastPath = txt;
                }
                if (ShouldCopyDocx)
                {
                    string docx = System.IO.Path.Combine(folder, "CreateDocument_" + baseName + ".docx");
                    DocumentExportService.ExportDocx(m, docx);
                    lastPath = docx;
                }
                SetStatus($"Saved to: {folder}", ok: true);
                if (lastPath != null) OfferOpen(lastPath);
            }
            catch (Exception ex) { SetStatus($"Save failed: {ex.Message}", ok: false); }
        }
        private void BtnDocClear_Click(object sender, RoutedEventArgs e)
        {
            // Confirm first: New clears every field, so give the user a way out.
            if (!ShowThemedConfirm("New", "\u2753", "blue",
                    "This will clear all fields and start a new document. Continue?"))
                return;

            // Starting a fresh document abandons the previous package's context, so drop the
            // per-run package Documents folder. Otherwise a later manual Save would inherit the
            // PREVIOUS package's folder (RuntimeDocumentsFolder is a static set during the flow
            // and is not otherwise cleared until a new chain starts). After New, Save falls back
            // to C:\Packages\<name>\Documents until the next Execute re-establishes the context.
            RuntimeDocumentsFolder = null;

            // Clear ALL fields
            foreach (var tb in new[] {
                TxtPackageName, TxtAppNameDisp, TxtVendorDisp, TxtVersionDisp,
                TxtSourceMedia, TxtCreatedBy, TxtValidatedBy, TxtShortcutsDoc,
                TxtInstallLog, TxtUninstallLog,
                TxtRequestId, TxtSourceLocation, TxtRequestorName, TxtAppOwner,
                TxtDepartment, TxtCountry,
                TxtIntunewinName, TxtIntunewinSize, TxtIntuneName, TxtPublisher,
                TxtIntuneVersion, TxtCategory, TxtDescription, TxtInstallCmd, TxtUninstallCmd,
                TxtInstallBehaviour, TxtDetectionTypeDoc, TxtDependencies, TxtSupersedence,
                TxtAssignmentGroup, TxtDateDisp, TxtOSDisp, TxtCloseAppsDoc,
                TxtRollbackPlan, TxtKnownIssues, TxtAdditionalInfo, TxtDeploymentType })
                tb.Text = string.Empty;
            _uatUsers.Clear();
            foreach (var b in UatTagsPanel.Children.OfType<Border>().ToList())
                UatTagsPanel.Children.Remove(b);
            TxtUatInput.Text = string.Empty;
            SetStatus("All fields cleared.", ok: true);
        }

        // ── Settings: edit CreateDocument.json ─────────────────────────────────
        // Modal editor over CreateDocument.json. Fetches the current key/value pairs
        // and lets the user modify, delete and add fields, then writes them back via a
        // JsonObject round-trip. Mirrors the Create Package - Settings dialog.
        private void BtnDocSettings_Click(object sender, RoutedEventArgs e) => OpenCreateDocumentSettings();

        // Resolve the CreateDocument.json path: prefer the first existing candidate
        // (same order LoadDocConfig uses), otherwise fall back to the Program Files copy.
        private static string GetDocConfigPath()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new[]
                {
                    @"C:\Program Files\Smekal\SwiftPack Studio\Templates\CreateDocument.json",
                    System.IO.Path.Combine(exeDir, "Templates", "CreateDocument.json"),
                    System.IO.Path.Combine(exeDir, "CreateDocument.json"),
                };
                foreach (var p in candidates)
                    if (File.Exists(p)) return p;
                return candidates[0];   // default write location if none exist yet
            }
            catch { return @"C:\Program Files\Smekal\SwiftPack Studio\Templates\CreateDocument.json"; }
        }

        private void OpenCreateDocumentSettings()
        {
            string cfgPath = GetDocConfigPath();

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
                DocStyledMessage(Window.GetWindow(this), "Create Document - Settings",
                    "CreateDocument.json is not valid JSON. Fix the file, then try again.\n\n" + cfgPath, "⚠");
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
                Width = 620, SizeToContent = SizeToContent.Height,
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
            var tt = new TextBlock { Text = "Create Document - Settings", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var tcl = new Button { Content = "✕", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => cwin.Close();
            Grid.SetColumn(ti, 0); Grid.SetColumn(tt, 1); Grid.SetColumn(tcl, 2);
            tg.Children.Add(ti); tg.Children.Add(tt); tg.Children.Add(tcl);
            tb.Child = tg; og.Children.Add(tb);

            var form = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form };
            Grid.SetRow(scroll, 1); og.Children.Add(scroll);

            form.Children.Add(new TextBlock { Text = "Auto-copy flags used when creating a Package Delivery Document.", FontSize = 10.5, Foreground = lblFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

            string ReadStr(System.Text.Json.Nodes.JsonNode? n)
            {
                if (n == null) return "";
                try { return n.GetValue<string>(); } catch { }
                try { return n.ToJsonString().Trim('"'); } catch { }
                return "";
            }

            // (section, JSON key, label, kind)  kind: path = folder path (browse), yn = Yes/No
            var fields = new (string section, string key, string label, string kind)[]
            {
                ("Options", "CopytxtToDocuments",  "Copy .txt To Documents",  "yn"),
                ("Options", "CopydocxToDocuments", "Copy .docx To Documents", "yn"),
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
                var lbl = new TextBlock { Text = f.label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = lblFg, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lbl, 0); rowGrid.Children.Add(lbl);

                if (f.kind == "yn")
                {
                    bool isYes = cur.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    var rbY = new RadioButton { Content = "Yes", FontSize = 11, IsChecked = isYes,  Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Cursor = Cursors.Hand };
                    var rbN = new RadioButton { Content = "No",  FontSize = 11, IsChecked = !isYes, Foreground = valFg, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
                    var rbPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    rbPanel.Children.Add(rbY); rbPanel.Children.Add(rbN);   // grouped by their parent panel
                    Grid.SetColumn(rbPanel, 1); rowGrid.Children.Add(rbPanel);
                    editors[f.key] = rbY;   // on save: rbY.IsChecked -> "Yes"/"No"
                }
                else // "path" - folder path with browse button in the box
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
            DocApplyPrimaryLook(bSave);
            bSave.Click += (_, _) =>
            {
                try
                {
                    // Write the edited fields back into the JSON (any other keys are preserved).
                    foreach (var f in fields)
                    {
                        string val = f.kind == "yn"
                            ? ((editors[f.key] as RadioButton)?.IsChecked == true ? "Yes" : "No")
                            : ((editors[f.key] as TextBox)?.Text ?? "").Trim();
                        root[f.key] = val;
                    }
                    // DocumentsPath is retired. Preserving unknown keys is right in general,
                    // but this one is OUR removed setting: dropping it here is how a config
                    // written by an older build sheds it on the next save.
                    root.AsObject().Remove("DocumentsPath");

                    // Build the bytes in memory first so a failure cannot corrupt the file.
                    byte[] jsonBytes;
                    using (var ms = new MemoryStream())
                    {
                        using (var jw = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
                            root.WriteTo(jw);
                        jsonBytes = ms.ToArray();
                    }
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cfgPath) ?? "");
                    File.WriteAllBytes(cfgPath, jsonBytes);

                    LoadDocConfig();   // refresh cached settings so they apply immediately

                    cwin.Close();
                    DocStyledMessage(Window.GetWindow(this), "Create Document - Settings",
                        "Settings written to CreateDocument.json.", "✅");
                }
                catch (Exception ex)
                {
                    DocStyledMessage(cwin, "Create Document - Settings",
                        "Save failed:\n" + ex.Message + "\n\n" + cfgPath, "⚠");
                }
            };
            btnBar.Children.Add(bCancel); btnBar.Children.Add(bSave);
            og.Children.Add(btnBar);

            cwin.ShowDialog();
        }

        // Small branded message dialog matching the app's window style (Settings flow).
        private void DocStyledMessage(Window? owner, string title, string message, string icon)
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
            var dIcon = DocDecisionIcon();
            Grid.SetColumn(dIcon, 0);
            actionRow.Children.Add(dIcon);
            var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(btnBar, 1);
            var bOk = new Button { Content = "✔  OK", Style = sec, Height = 30, Padding = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, IsDefault = true };
            DocApplyPrimaryLook(bOk);
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
        private static void DocApplyPrimaryLook(Button b)
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
        private static TextBlock DocDecisionIcon()
            => new TextBlock
            {
                Text = "\u2753",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0x9B, 0xF5)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };
        // ── Helpers ───────────────────────────────────────────────────────────
        private void SetStatus(string msg, bool ok)
        {
            TxtDocStatus.Text = msg;
            TxtDocStatus.Foreground = ok
                ? new SolidColorBrush(Color.FromRgb(22, 163, 74))
                : new SolidColorBrush(Color.FromRgb(220, 38, 38));
        }
        private static string SafeName(CreateDocumentModel m)
        {
            // The document is named after the package, e.g.
            // IgorPavlov_7-Zip26.01_x64edition__26.01.00.0_014. Prefer the Package Name,
            // then the .intunewin base name, then App + Version, then a generic fallback.
            string raw = m.PackageName;
            if (string.IsNullOrWhiteSpace(raw))
                raw = System.IO.Path.GetFileNameWithoutExtension(m.IntunewinFileName ?? "");
            if (string.IsNullOrWhiteSpace(raw))
                raw = $"{m.ApplicationName}_{m.ApplicationVersion}".Trim('_');
            if (string.IsNullOrWhiteSpace(raw))
                raw = "PackageDeliveryDocument";
            foreach (char c in Path.GetInvalidFileNameChars()) raw = raw.Replace(c, '_');
            return raw.Trim();
        }
        private void OfferOpen(string path)
        {
            if (ShowThemedConfirm("Document saved", "\u2705", "blue",
                    "Document saved.\nOpen the file location?", "Open", "Close"))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        // ── Themed dialog (matches the app's other windows) ─────────────────────
        private static Brush DlgBrush(string key, Color fallback)
            => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

        private static ControlTemplate? _dlgBtnTpl;
        private static ControlTemplate DlgBtnTpl()
            => _dlgBtnTpl ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\">" +
                "<Border Background=\"{TemplateBinding Background}\" BorderBrush=\"{TemplateBinding BorderBrush}\" " +
                "BorderThickness=\"{TemplateBinding BorderThickness}\" CornerRadius=\"16\">" +
                "<ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" Margin=\"14,0\"/>" +
                "</Border></ControlTemplate>");

        /// <summary>Professional themed Yes/No dialog, styled like the app's other windows. Returns true on the affirmative button.</summary>
        private bool ShowThemedConfirm(string title, string icon, string style, string message,
                                       string okText = "Yes", string cancelText = "No")
        {
            bool result = false;
            var winBg    = DlgBrush("NavBarBgBrush",    Color.FromRgb(0x2A, 0x2D, 0x3A));
            var titleBar = new SolidColorBrush(Color.FromRgb(0x53, 0x56, 0x63));
            var txt      = DlgBrush("TextPrimaryBrush", Color.FromRgb(0xF0, 0xF0, 0xF0));
            var accent   = DlgBrush("AccentBlueBrush",  Color.FromRgb(0x42, 0x9B, 0xF5));
            var cardBg   = DlgBrush("CardBgBrush",      Color.FromRgb(0x2F, 0x32, 0x40));
            var bdrClr   = DlgBrush("BorderMainBrush",  Color.FromRgb(0x3A, 0x3D, 0x4A));

            var win = new Window
            {
                Title = "Create Document - " + title,
                Width = 420, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent,
                FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false,
            };

            var outer = new Border
            {
                Background = winBg, BorderBrush = titleBar, BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black },
            };
            win.Content = outer;

            var og = new Grid();
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            og.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.Child = og;

            // Title bar (draggable)
            var tbar = new Border { Background = titleBar, CornerRadius = new CornerRadius(10, 10, 0, 0),
                                    Padding = new Thickness(16, 6, 10, 6) };
            tbar.MouseLeftButtonDown += (_, _) => win.DragMove();
            Grid.SetRow(tbar, 0);
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tt = new TextBlock { Text = "Create Document - " + title, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            var tcl = new Button { Content = "\u2715", FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand, Padding = new Thickness(6, 2, 6, 2) };
            tcl.Click += (_, _) => { result = false; win.Close(); };
            Grid.SetColumn(tt, 0); Grid.SetColumn(tcl, 1);
            tg.Children.Add(tt); tg.Children.Add(tcl);
            tbar.Child = tg;
            og.Children.Add(tbar);

            // Body: icon + message, then right-aligned buttons
            var bodyPanel = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
            Grid.SetRow(bodyPanel, 1);
            var msgRow = new StackPanel { Orientation = Orientation.Horizontal };
            var cIcon = DocDecisionIcon();
            cIcon.VerticalAlignment = VerticalAlignment.Center;
            cIcon.Margin = new Thickness(0, 0, 12, 0);
            msgRow.Children.Add(cIcon);
            msgRow.Children.Add(new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = txt, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 });
            bodyPanel.Children.Add(msgRow);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var okBtn = new Button { Content = "\u2714  " + okText, Height = 32, MinWidth = 96, FontSize = 12,
                FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Foreground = Brushes.White,
                Background = accent, BorderThickness = new Thickness(0), Template = DlgBtnTpl(),
                Margin = new Thickness(0, 0, 8, 0) };
            var noBtn = new Button { Content = "\u2715  " + cancelText, Height = 32, MinWidth = 96, FontSize = 12,
                Cursor = Cursors.Hand, Foreground = txt, Background = cardBg, BorderBrush = bdrClr,
                BorderThickness = new Thickness(1), Template = DlgBtnTpl() };
            okBtn.Click += (_, _) => { result = true; win.Close(); };
            noBtn.Click += (_, _) => { result = false; win.Close(); };
            btnRow.Children.Add(okBtn); btnRow.Children.Add(noBtn);
            bodyPanel.Children.Add(btnRow);
            og.Children.Add(bodyPanel);

            win.ShowDialog();
            return result;
        }
        private static string GetOsString()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    string prod = key.GetValue("ProductName")    as string ?? string.Empty;
                    string disp = key.GetValue("DisplayVersion") as string ?? string.Empty;
                    string arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                    return $"{prod} {disp} {arch}".Trim();
                }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }
    }
    /// <summary>Settings from CreateDocument.json - controls the auto-copy flags.</summary>
    public class CreateDocumentConfig
    {
        /// <summary>"Yes" = auto-copy .txt to the Documents folder after export.</summary>
        public string? CopytxtToDocuments  { get; set; }
        /// <summary>"Yes" = auto-copy .docx to the Documents folder after export.</summary>
        public string? CopydocxToDocuments { get; set; }
    }
}
