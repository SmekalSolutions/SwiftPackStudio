// EtwQaCapture.cs
// Real-time capture of EXE installer activity using:
//   - FileSystemWatcher  (files, folders, shortcuts, certificates)
//   - Registry polling   (registry writes, services, env vars)
//
// Usage:
//   1. var cap = new EtwQaCapture(exeName, exeArgs, log);
//   2. cap.StartCapture();
//   3. ... run installer (WaitForExit loop) ...
//   4. cap.StopCapture();   ← writes .svr file
//
// Output: C:\Temp\<FileNameWithoutExt> - For QA.svr (JSON)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SwiftPack
{
    // ═══════════════════════════════════════════════════════════
    //  DATA MODEL — .svr JSON schema
    // ═══════════════════════════════════════════════════════════
    public class QaSnapshot
    {
        public string SnapVersion  { get; set; } = "1.0";
        public string FileName      { get; set; } = "";
        public string FilePath      { get; set; } = "";
        public string Arguments    { get; set; } = "";
        public string CaptureTime  { get; set; } = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        [JsonPropertyName("Files")]
        public List<string> Files { get; set; } = new();

        [JsonPropertyName("Folders")]
        public List<string> Folders { get; set; } = new();

        [JsonPropertyName("Shortcuts")]
        public List<string> Shortcuts { get; set; } = new();

        [JsonPropertyName("Registry")]
        public List<QaRegEntry> Registry { get; set; } = new();

        [JsonPropertyName("EnvVars")]
        public List<QaEnvVar> EnvVars { get; set; } = new();

        [JsonPropertyName("Services")]
        public List<QaService> Services { get; set; } = new();

        [JsonPropertyName("Certificates")]
        public List<string> Certificates { get; set; } = new();

        // Items excluded by the ValidateSource.json exclusion patterns at capture time.
        // Format: "Category|DisplayPath|Value". The user's manual checkbox exclusions are added
        // later by ValidateSourcePage when the .svr is saved. Always written (may be empty) so the
        // schema is consistent and Validate Package / MSI creation can rely on the field.
        [JsonPropertyName("ExcludedPaths")]
        public List<string> ExcludedPaths { get; set; } = new();
    }

    public class QaRegEntry
    {
        public string Key   { get; set; } = "";
        public string Value { get; set; } = "";
        public string Data  { get; set; } = "";
        public string Type  { get; set; } = "REG_SZ";
    }

    public class QaEnvVar
    {
        public string Name  { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public class QaService
    {
        public string Name        { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Binary      { get; set; } = "";
        public string StartType   { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════
    //  CAPTURE ENGINE
    // ═══════════════════════════════════════════════════════════
    public class EtwQaCapture : IDisposable
    {
        private readonly string _exeName;       // e.g. "FileCompare Setup 1.0.exe"
        private readonly string _exeArgs;       // e.g. "/VERYSILENT /NORESTART"
        private string _exeFullPath = "";        // full path to source file if available
        private string _msiProductCode = "";     // product code read from MSI before install starts
        private readonly string _outputPath;    // <QAsnapPath>\FileCompare Setup 1.0 - For QA.svr
        private readonly string _diagPath;      // %TEMP%\FileCompare Setup 1.0 - Capture.txt
        private readonly Action<string> _log;

        // Thread-safe collections
        private readonly ConcurrentDictionary<string, byte>        _files     = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte>        _folders   = new(StringComparer.OrdinalIgnoreCase);
        // Folders whose FSW event was Created (genuinely new dirs, e.g. robocopy /E target).
        // These are scanned recursively after capture to pick up files whose own Created event
        // was missed (fast copies can drop individual file events while the dir event survives).
        private readonly ConcurrentDictionary<string, byte>        _createdFolders = new(StringComparer.OrdinalIgnoreCase);
        // Files the caller explicitly asked us to include after the run (e.g. a script's computed
        // copy-destination files parsed from a .bat). Added in ScanInstallDirectories if they exist
        // on disk — so an already-existing robocopy target shows exactly the files the script copies,
        // and NOT stray pre-existing files that happen to share the destination folder.
        private readonly ConcurrentDictionary<string, byte>        _explicitScanFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte>        _shortcuts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte>        _certs     = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, QaRegEntry>  _registry  = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, QaEnvVar>    _envVars   = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, QaService>   _services  = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource?         _cts;
        private Task?                            _captureTask;
        private readonly List<FileSystemWatcher> _watchers = new();

        // Registry snapshots taken BEFORE capture starts (for diffing)
        private Dictionary<string, string> _regEnvBefore  = new();
        private Dictionary<string, string> _regSvcBefore      = new();
        private Dictionary<string, string> _regUninstallBefore = new();

        // Paths where EXE installers typically write files
        private static readonly string[] _watchPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),  // C:\ProgramData
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), // Public Desktop
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),         // Common Start Menu
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),        // AppData\Roaming
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),   // AppData\Local
        };

        // Noise paths to ignore
        private static readonly string[] _noisePaths = new[]
        {
            @"\Microsoft\Windows\WER\",
            @"\Microsoft\Windows\Caches\",
            @"\CryptnetUrlCache\",
            @"\Windows\SoftwareDistribution\",
            @"\Windows\Prefetch\",
            @"\Temp\",
            @"\AppData\Local\Temp\",
            @"\AppData\Roaming\Microsoft\Windows\Recent\",

            // Windows background noise — never contain app installation files
            @"\ConnectedDevicesPlatform\",   // Windows activity sync (ActivitiesCache.db)
            @"\INetCache\IE\",               // Windows weather widget / IE cache SVGs

            // UWP / Microsoft Store app sandbox churn. These directories live under
            // AppData\Local\Packages\<PackageFamilyName>\ and constantly change in the
            // background (e.g. myHP, Xbox, notification DBs). A classic .exe/.msi installer
            // never writes its payload here, so dropping them removes the bulk of the noise
            // (the LocalState\features-*.json, QueueDb.db, *-session-snapshot.json, etc.).
            @"\AppData\Local\Packages\",          // entire per-user UWP sandbox tree
            @"\AppData\Local\Microsoft\Windows\Notifications\", // toast/notification DBs
            @"\AppData\Local\Microsoft\Windows\WebCache\",      // IE/Edge web cache
            @"\AppData\Local\Microsoft\Windows\Explorer\",      // thumbnail/iconcache DBs
            @"\AppData\Local\Microsoft\Windows\INetCache\",     // browser/system cache
            @"\AppData\Local\D3DSCache\",         // Direct3D shader cache
            @"\AppData\Local\Microsoft\CLR_v",    // .NET NGen / CLR usage logs
            @"\AppData\Local\ConnectedDevicesPlatform\",
        };

        public string OutputPath => _outputPath;
        public string DiagPath   => _diagPath;

        /// <summary>
        /// Registers specific destination files that should be included after the run if they exist
        /// on disk, regardless of whether the file watcher saw any activity. Used for script copy
        /// destinations (e.g. a .bat's robocopy target files), so an already-existing target still
        /// shows exactly the files the script copies. Files that don't exist on disk are skipped.
        /// </summary>
        public void AddExplicitScanFiles(IEnumerable<string> files)
        {
            if (files == null) return;
            foreach (var f in files)
                if (!string.IsNullOrWhiteSpace(f))
                    _explicitScanFiles.TryAdd(f.TrimEnd('\\'), 0);
        }

        /// <summary>Sets the full source file path (used for MSI product code lookup).</summary>
        public void SetSourcePath(string fullPath)
        {
            _exeFullPath = fullPath;
            // Pre-read product code from MSI now (before msiexec locks the file)
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if ((ext == ".msi" || ext == ".msp") && File.Exists(fullPath))
            {
                try
                {
                    Type? t = Type.GetTypeFromProgID("WindowsInstaller.Installer");
                    if (t != null)
                    {
                        dynamic installer = Activator.CreateInstance(t)!;
                        dynamic db = installer.OpenDatabase(fullPath, 0);
                        dynamic view = db.OpenView("SELECT `Value` FROM `Property` WHERE `Property`='ProductCode'");
                        view.Execute();
                        dynamic? rec = view.Fetch();
                        _msiProductCode = rec?.StringData[1] as string ?? "";
                        File.AppendAllText(_diagPath,
                            $"[{DateTime.Now:HH:mm:ss}] MSI ProductCode read: {_msiProductCode}\n");
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(_diagPath,
                        $"[{DateTime.Now:HH:mm:ss}] MSI ProductCode read FAILED: {ex.Message}\n");
                }
            }
        }

        // ── Live read-only snapshots for UI during capture ────────
        // Returns point-in-time copies safe to enumerate on the UI thread
        public IReadOnlyList<string>      LiveFiles     => _files.Keys.ToList();
        public IReadOnlyList<string>      LiveFolders   => _folders.Keys.ToList();
        public IReadOnlyList<string>      LiveShortcuts => _shortcuts.Keys.ToList();
        public IReadOnlyList<QaRegEntry>  LiveRegistry  => _registry.Values.ToList();
        public IReadOnlyList<QaService>   LiveServices  => _services.Values.ToList();
        public IReadOnlyList<string>      LiveCerts     => _certs.Keys.ToList();

        public EtwQaCapture(string exeName, string exeArgs, Action<string> log)
        {
            _exeName = exeName;
            _exeArgs = exeArgs;

            // Resolve .svr output folder: ValidateSource.json SVRPath → C:\Temp\Validate Source
            string qasnapDir = ResolveQAsnapDir();

            // Build initial base name from FileVersionInfo (best effort before install)
            // ValidateSourcePage will rename the file post-capture once registry data is available.
            string initialBase = BuildInitialBaseName(exeName);
            _outputPath = Path.Combine(qasnapDir, initialBase + ".svr");

            // Diag log: %TEMP%\ValidateSource_<base>.log (will be overwritten by WriteActionLog)
            string tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            _diagPath = Path.Combine(tempDir, "ValidateSource_" + initialBase + ".log");
            _log = log;

            Directory.CreateDirectory(qasnapDir);
            Directory.CreateDirectory(tempDir);
        }

        /// <summary>
        /// Builds an initial base name from the EXE's FileVersionInfo.
        /// Used before install completes; ValidateSourcePage renames post-capture.
        /// Format: Vendor_ProductName_Version (omitting blank parts).
        /// </summary>
        private static string BuildInitialBaseName(string exeName)
        {
            string vendor = "", name = "", version = "";
            try
            {
                if (File.Exists(exeName))
                {
                    var vi  = System.Diagnostics.FileVersionInfo.GetVersionInfo(exeName);
                    vendor  = vi.CompanyName?.Trim()    ?? "";
                    name    = vi.ProductName?.Trim()    ?? "";
                    version = vi.ProductVersion?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(name))    name    = vi.FileDescription?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(version)) version = vi.FileVersion?.Trim()     ?? "";
                }
            }
            catch { }

            static string San(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
                s = s.Replace(" ", ""); // remove spaces
                return s.Trim().Trim('_');
            }

            var parts = new[] { San(vendor), San(name), San(version) }
                .Where(p => !string.IsNullOrEmpty(p)).ToArray();

            // Fall back to exe filename if no version info available
            if (parts.Length == 0)
                parts = new[] { San(Path.GetFileNameWithoutExtension(exeName)) };

            return string.Join("_", parts);
        }

        // ── Public API ───────────────────────────────────────────────
        public void StartCapture()
        {
            _log($"[QA Capture] Starting capture for: {_exeName}");
            _log($"[QA Capture] Output: {_outputPath}");
            File.AppendAllText(_diagPath,
                $"[{DateTime.Now:HH:mm:ss}] StartCapture: {_exeName}\n");

            // Take before-snapshots of registry areas we track
            _regEnvBefore       = SnapshotEnvVarRegistry();
            _regSvcBefore       = SnapshotServiceRegistry();
            _regUninstallBefore = SnapshotUninstallRegistry(); // ← snapshot existing apps BEFORE install

            _cts = new CancellationTokenSource();

            // Start file system watchers
            StartFileWatchers();

            // Start registry polling in background
            _captureTask = Task.Run(() => PollRegistryLoop(_cts.Token));

            _log("[QA Capture] Watching: files, folders, shortcuts, registry, env vars, services...");
        }

        public void StopCapture()
        {
            try { File.AppendAllText(_diagPath, $"[{DateTime.Now:HH:mm:ss}] StopCapture called\n"); } catch { }
            _cts?.Cancel();

            // Stop file watchers
            foreach (var w in _watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();

            // Wait for registry poller to finish
            try { _captureTask?.Wait(3000); } catch { }

            // ── Post-install directory scan ──────────────────────────
            // The FSW only fires for files written DURING capture. Files that existed before
            // (e.g. DuplicateCleaner.exe dated 2007) are missed. Fix: scan every parent folder
            // of any captured file/folder and add all files found there.
            ScanInstallDirectories();

            // Final registry diff
            DiffAndCollectRegistry();

            // Write snapshot file
            WriteSnapshot();

            _log($"[QA Capture] Capture complete. Snapshot saved: {_outputPath}");
        }

        /// <summary>
        /// After install completes, scan only the exact directories where the FSW detected
        /// NEW file writes — to pick up pre-existing files the watcher missed.
        /// Only uses parent dirs of captured FILES (not folder-change dirs, which are too broad).
        /// </summary>
        private void ScanInstallDirectories()
        {
            try
            {
                // Use only parent dirs of FSW-captured FILES — not _folders.Keys.
                // The FSW fires Changed on parent dirs whenever any child changes,
                // so _folders contains too many broad dirs. _files only contains
                // paths where the installer actually wrote a file.
                var dirsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in _files.Keys)
                {
                    string? dir = Path.GetDirectoryName(f);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        dirsToScan.Add(dir);
                }

                File.AppendAllText(_diagPath,
                    $"[{DateTime.Now:HH:mm:ss}] ScanInstallDirectories: {dirsToScan.Count} targeted dirs\n");

                foreach (var dir in dirsToScan)
                {
                    try
                    {
                        // Non-recursive: only files in this exact directory
                        foreach (var file in Directory.EnumerateFiles(dir))
                        {
                            if (!File.Exists(file)) continue;
                            if (IsNoisePath(file)) continue;   // skip background-noise files
                            _files.TryAdd(file, 0);
                            if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                                _shortcuts.TryAdd(file, 0);
                        }
                    }
                    catch { }
                }

                // Recursively scan folders the FSW saw CREATED (e.g. a robocopy /E target like
                // AppData\Roaming\abc). Fast copies can drop the per-file Created event while the
                // directory event survives, leaving the folder captured but its files invisible.
                // These are specific new folders (not broad Changed parents), so a recursive scan
                // is safe and recovers the missed files (e.g. SharedView.mst).
                foreach (var cdir in _createdFolders.Keys)
                {
                    if (!Directory.Exists(cdir) || IsNoisePath(cdir)) continue;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(cdir, "*", SearchOption.AllDirectories))
                        {
                            if (!File.Exists(file) || IsNoisePath(file)) continue;
                            _files.TryAdd(file, 0);
                            if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                                _shortcuts.TryAdd(file, 0);
                        }
                        // Also record any sub-folders created underneath, so the folder tree is complete.
                        foreach (var sub in Directory.EnumerateDirectories(cdir, "*", SearchOption.AllDirectories))
                            if (!IsNoisePath(sub)) _folders.TryAdd(sub, 0);
                    }
                    catch { }
                }

                // Explicit caller-supplied destination FILES (e.g. a script's copy destinations
                // computed from the source). Add each if it exists on disk — this covers the
                // "already exists" case (robocopy skips files that already exist), while including
                // ONLY the files the script actually copies, not stray pre-existing files that
                // happen to live in the same destination folder. Missing files are skipped, so a
                // not-yet-created target naturally shows nothing.
                foreach (var efile in _explicitScanFiles.Keys)
                {
                    try
                    {
                        if (!File.Exists(efile) || IsNoisePath(efile)) continue;
                        _files.TryAdd(efile, 0);
                        if (efile.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                            _shortcuts.TryAdd(efile, 0);
                        // Record the file's parent folder so the folder tree is complete.
                        string? pdir = Path.GetDirectoryName(efile);
                        if (!string.IsNullOrEmpty(pdir) && Directory.Exists(pdir) && !IsNoisePath(pdir))
                            _folders.TryAdd(pdir, 0);
                    }
                    catch { }
                }

                File.AppendAllText(_diagPath,
                    $"[{DateTime.Now:HH:mm:ss}] ScanInstallDirectories complete. Files: {_files.Count}\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(_diagPath,
                    $"[{DateTime.Now:HH:mm:ss}] ScanInstallDirectories FAILED: {ex.Message}\n");
            }
        }

                public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            foreach (var w in _watchers) try { w.Dispose(); } catch { }
        }

        // ── File System Watchers ─────────────────────────────────────
        private void StartFileWatchers()
        {
            foreach (var basePath in _watchPaths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct())
            {
                try
                {
                    var w = new FileSystemWatcher(basePath)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter          = NotifyFilters.FileName
                                              | NotifyFilters.DirectoryName
                                              | NotifyFilters.LastWrite,
                        EnableRaisingEvents   = true,
                        InternalBufferSize    = 65536, // 64KB buffer for busy installers
                    };
                    w.Created += OnEntryCreated;
                    w.Changed += OnEntryChanged;
                    _watchers.Add(w);
                }
                catch { /* Skip unwatchable paths */ }
            }
        }

        private void OnEntryCreated(object sender, FileSystemEventArgs e) => RecordEntry(e.FullPath, created: true);
        private void OnEntryChanged(object sender, FileSystemEventArgs e) => RecordEntry(e.FullPath, created: false);

        private void RecordEntry(string fullPath, bool created)
        {
            try
            {
                // Drop well-known background-noise paths (UWP sandbox churn, caches, temp,
                // notification DBs, ...). These are never part of an installer's payload and
                // otherwise flood the GUI with unrelated activity. Applied here so the noise
                // never enters the live collections in the first place.
                if (IsNoisePath(fullPath)) return;

                // Do NOT apply user/JSON exclusions during live capture — capture everything
                // else. Those exclusions are applied later in WriteSnapshot() and by
                // ValidateSourcePage.

                // Always check if it's a directory — FSW Changed events fire on parent dirs
                // when files inside them change, so we must handle both Created and Changed.
                if (Directory.Exists(fullPath))
                {
                    _folders.TryAdd(fullPath, 0); // always add, not just on Created
                    // A Created event on a directory means a genuinely new folder (e.g. the
                    // robocopy /E target). Remember it so ScanInstallDirectories can scan inside
                    // it for files whose own Created event was missed.
                    if (created) _createdFolders.TryAdd(fullPath, 0);
                    return;
                }

                // It's a file — only add if it still exists (runtime-only files may vanish)
                if (!File.Exists(fullPath)) return;

                _files.TryAdd(fullPath, 0);

                // Shortcuts
                if (fullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    _shortcuts.TryAdd(fullPath, 0);

                // Certificates
                if (fullPath.EndsWith(".cer", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.EndsWith(".crt", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
                    _certs.TryAdd(fullPath, 0);
            }
            catch { }
        }

        private static bool IsNoisePath(string path)
        {
            foreach (var noise in _noisePaths)
                if (path.IndexOf(noise, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // Converts a registry value to a string. REG_MULTI_SZ comes back as string[], whose
        // .ToString() is the useless "System.String[]" — join those into readable text instead.
        private static string RegDataToString(object? val) =>
            val is string[] arr ? string.Join("; ", arr) : (val?.ToString() ?? "");

        // ── Registry Polling ─────────────────────────────────────────
        private void PollRegistryLoop(CancellationToken ct)
        {
            // Poll every 500ms during installation
            while (!ct.IsCancellationRequested)
            {
                try { Thread.Sleep(500); } catch { return; }
                if (ct.IsCancellationRequested) break;

                // Collect any new registry writes we can detect in real-time
                // (full diff happens in DiffAndCollectRegistry after stop)
                CollectNewRegistryEntries();
            }
        }

        private void CollectNewRegistryEntries()
        {
            // Live-poll uninstall hive during install for NEW entries only.
            // FIX: Capture ALL registry values for each matching entry (not just 4).
            // The previous partial capture (only DisplayName/Version/Publisher/InstallLocation)
            // caused the "|DisplayName" guard in DiffAndCollectRegistry Section 3 to fire,
            // which skipped the full value enumeration at stop time — leaving only 4 of ~20 values.
            // Now the live poller writes complete entries, and Section 3 at stop time overwrites
            // them again with a fresh full read (last-write wins, ensuring final accuracy).
            try
            {
                string pcClean = _msiProductCode.Trim('{', '}').ToUpperInvariant();
                // HKCU is scanned as well as HKLM. A per-user installer (Inno Setup and NSIS both
                // default to it when not elevated, and some installers write to
                // AppData\Local\Programs) registers its uninstall entry under HKCU only. Reading
                // HKLM alone meant no DisplayName was ever found for those, which is what leaves
                // App Name empty and stops the .svr being created automatically.
                foreach (var hiveRoot in new[] {
                    (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                    (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                    (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") })
                {
                    using var key = hiveRoot.Item1.OpenSubKey(hiveRoot.Item2);
                    if (key == null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        bool isNew      = !_regUninstallBefore.ContainsKey(sub);
                        bool isMsiMatch = !string.IsNullOrEmpty(pcClean) &&
                                          sub.Trim('{', '}').ToUpperInvariant() == pcClean;
                        if (!isNew && !isMsiMatch) continue;

                        using var entry = key.OpenSubKey(sub);
                        if (entry == null) continue;
                        string name = entry.GetValue("DisplayName") as string ?? "";
                        if (string.IsNullOrEmpty(name)) continue;

                        // Was hardcoded to HKEY_LOCAL_MACHINE, which would mislabel HKCU entries.
                        string hiveName = ReferenceEquals(hiveRoot.Item1, Registry.CurrentUser)
                            ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
                        string regKey = $@"{hiveName}\{hiveRoot.Item2}\{sub}";

                        // Capture ALL values so the live UI shows complete data,
                        // and so the "|DisplayName" guard in Section 3 never blocks
                        // a partial entry from being overwritten with the full set.
                        foreach (var valName in entry.GetValueNames())
                        {
                            var val = entry.GetValue(valName);
                            if (val == null) continue;
                            string type = entry.GetValueKind(valName) switch
                            {
                                RegistryValueKind.DWord        => "REG_DWORD",
                                RegistryValueKind.QWord        => "REG_QWORD",
                                RegistryValueKind.Binary       => "REG_BINARY",
                                RegistryValueKind.MultiString  => "REG_MULTI_SZ",
                                RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
                                _                              => "REG_SZ",
                            };
                            _registry[$"{regKey}|{valName}"] = new QaRegEntry
                            { Key = regKey, Value = string.IsNullOrEmpty(valName) ? "(Default)" : valName, Data = RegDataToString(val), Type = type };
                        }
                    }
                }
            }
            catch { }
        }

        // ── Registry Before/After Diff ───────────────────────────────
        private Dictionary<string, string> SnapshotEnvVarRegistry()
        {
            var snap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
                if (key == null) return snap;
                foreach (var name in key.GetValueNames())
                    snap[name] = key.GetValue(name)?.ToString() ?? "";
            }
            catch { }
            return snap;
        }

        private Dictionary<string, string> SnapshotServiceRegistry()
        {
            var snap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (key == null) return snap;
                foreach (var sub in key.GetSubKeyNames())
                    snap[sub] = sub; // just track names
            }
            catch { }
            return snap;
        }

        private void DiffAndCollectRegistry()
        {
            // FIX: Retry MSI product code read if the initial attempt in SetSourcePath failed.
            // By the time StopCapture() calls this method, msiexec has already completed and
            // the .msi file is no longer locked, so the COM read will succeed on retry.
            if (string.IsNullOrEmpty(_msiProductCode))
            {
                string ext = Path.GetExtension(_exeFullPath).ToLowerInvariant();
                if ((ext == ".msi" || ext == ".msp") && File.Exists(_exeFullPath))
                {
                    try
                    {
                        Type? t = Type.GetTypeFromProgID("WindowsInstaller.Installer");
                        if (t != null)
                        {
                            dynamic installer = Activator.CreateInstance(t)!;
                            dynamic db = installer.OpenDatabase(_exeFullPath, 0);
                            dynamic view = db.OpenView("SELECT `Value` FROM `Property` WHERE `Property`='ProductCode'");
                            view.Execute();
                            dynamic? rec = view.Fetch();
                            _msiProductCode = rec?.StringData[1] as string ?? "";
                            File.AppendAllText(_diagPath,
                                $"[{DateTime.Now:HH:mm:ss}] MSI ProductCode retry succeeded: {_msiProductCode}\n");
                        }
                    }
                    catch (Exception retryEx)
                    {
                        File.AppendAllText(_diagPath,
                            $"[{DateTime.Now:HH:mm:ss}] MSI ProductCode retry FAILED: {retryEx.Message}\n");
                    }
                }
            }

            // 1. Env vars — diff HKLM\SYSTEM\...\Environment before/after
            try
            {
                var envAfter = SnapshotEnvVarRegistry();
                foreach (var kv in envAfter)
                    if (!_regEnvBefore.TryGetValue(kv.Key, out var before) ||
                        !string.Equals(before, kv.Value, StringComparison.OrdinalIgnoreCase))
                        _envVars[kv.Key] = new QaEnvVar { Name = kv.Key, Value = kv.Value };
            }
            catch { }

            // 2. Services — diff HKLM\SYSTEM\...\Services before/after (only NEW services)
            try
            {
                var svcAfter = SnapshotServiceRegistry();
                foreach (var svcName in svcAfter.Keys)
                {
                    if (_regSvcBefore.ContainsKey(svcName)) continue;
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{svcName}");
                    if (key == null) continue;
                    string displayName = key.GetValue("DisplayName") as string ?? svcName;
                    string imagePath   = key.GetValue("ImagePath")   as string ?? "";
                    string startType   = (key.GetValue("Start") is int st)
                        ? st switch { 2 => "Auto", 3 => "Manual", 4 => "Disabled", _ => st.ToString() }
                        : "";
                    _services[svcName] = new QaService
                    { Name = svcName, DisplayName = displayName, Binary = imagePath, StartType = startType };
                }
            }
            catch { }

            // 3. Uninstall keys — two strategies, no keyword filtering:
            //    A) NEW entries: any key not in the before-snapshot → fresh install
            //    B) MSI product code match: find exact GUID regardless of before-snapshot
            try
            {
                string pcClean = _msiProductCode.Trim('{', '}').ToUpperInvariant();
                var diag3 = new System.Text.StringBuilder();
                diag3.AppendLine($"[{DateTime.Now:HH:mm:ss}] Section3: pcClean={pcClean}, beforeCount={_regUninstallBefore.Count}");

                foreach (var (subPath, hiveName) in new[] {
                    (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "HKEY_LOCAL_MACHINE"),
                    (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "HKEY_LOCAL_MACHINE") })
                {
                    using var key = Registry.LocalMachine.OpenSubKey(subPath);
                    if (key == null) continue;

                    foreach (var sub in key.GetSubKeyNames())
                    {
                        bool isNew      = !_regUninstallBefore.ContainsKey(sub);
                        bool isMsiMatch = !string.IsNullOrEmpty(pcClean) &&
                                          sub.Trim('{', '}').ToUpperInvariant() == pcClean;

                        if (isMsiMatch)
                            diag3.AppendLine($"  MSI match found: sub={sub} isNew={isNew}");

                        if (!isNew && !isMsiMatch) continue;

                        using var entry = key.OpenSubKey(sub);
                        if (entry == null) continue;

                        string displayName = entry.GetValue("DisplayName") as string ?? "";
                        if (string.IsNullOrEmpty(displayName)) continue;

                        diag3.AppendLine($"  Capturing uninstall entry: {displayName} [{sub}]");

                        string fullKey = $@"{hiveName}\{subPath}\{sub}";
                        foreach (var valName in entry.GetValueNames())
                        {
                            var val = entry.GetValue(valName);
                            if (val == null) continue;
                            string type = entry.GetValueKind(valName) switch
                            {
                                RegistryValueKind.DWord        => "REG_DWORD",
                                RegistryValueKind.QWord        => "REG_QWORD",
                                RegistryValueKind.Binary       => "REG_BINARY",
                                RegistryValueKind.MultiString  => "REG_MULTI_SZ",
                                RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
                                _                              => "REG_SZ",
                            };
                            _registry[$"{fullKey}|{valName}"] = new QaRegEntry
                            { Key = fullKey, Value = string.IsNullOrEmpty(valName) ? "(Default)" : valName, Data = RegDataToString(val), Type = type };
                        }
                    }
                }
                File.AppendAllText(_diagPath, diag3.ToString());
            }
            catch (Exception ex3)
            {
                File.AppendAllText(_diagPath,
                    $"[{DateTime.Now:HH:mm:ss}] Section3 ERROR: {ex3.Message}\n");
            }

            // 3b. FIX: MSI filename-based fallback — captures new Uninstall entries by matching
            // DisplayName against words from the .msi filename. This handles cases where both
            // isNew and isMsiMatch fail (e.g. product code COM read failed AND the key existed
            // as a different version before). Operates only on keys NOT in the before-snapshot.
            try
            {
                string msiBaseName = Path.GetFileNameWithoutExtension(_exeFullPath).ToLowerInvariant();
                // Strip trailing version numbers from filename for a cleaner keyword
                string msiKeyword = Regex.Replace(msiBaseName, @"[\s._\-]*[\d][\d._\-]*$", "").Trim();
                // Also strip common installer suffixes
                msiKeyword = Regex.Replace(msiKeyword, @"[\s_]*(setup|install|installer|x64|x86)[\s_]*$",
                    "", RegexOptions.IgnoreCase).Trim();

                if (msiKeyword.Length >= 3)
                {
                    File.AppendAllText(_diagPath,
                        $"[{DateTime.Now:HH:mm:ss}] Section3b: MSI fallback scan, keyword='{msiKeyword}'\n");

                    foreach (var (subPath3b, hiveName3b) in new[] {
                        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",        "HKEY_LOCAL_MACHINE"),
                        (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "HKEY_LOCAL_MACHINE") })
                    {
                        using var key3b = Registry.LocalMachine.OpenSubKey(subPath3b);
                        if (key3b == null) continue;

                        foreach (var sub in key3b.GetSubKeyNames())
                        {
                            // Only look at entries that weren't there before install
                            if (_regUninstallBefore.ContainsKey(sub)) continue;

                            using var entry = key3b.OpenSubKey(sub);
                            if (entry == null) continue;
                            string dn = entry.GetValue("DisplayName") as string ?? "";
                            if (dn.IndexOf(msiKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            string fullKey3b = $@"{hiveName3b}\{subPath3b}\{sub}";
                            // Skip if already captured by Section 3
                            bool alreadyCaptured = _registry.Keys.Any(k =>
                                k.StartsWith(fullKey3b, StringComparison.OrdinalIgnoreCase));
                            if (alreadyCaptured) continue;

                            File.AppendAllText(_diagPath,
                                $"[{DateTime.Now:HH:mm:ss}] Section3b: Capturing fallback entry '{dn}' [{sub}]\n");

                            foreach (var valName in entry.GetValueNames())
                            {
                                var val = entry.GetValue(valName);
                                if (val == null) continue;
                                string type3b = entry.GetValueKind(valName) switch
                                {
                                    RegistryValueKind.DWord        => "REG_DWORD",
                                    RegistryValueKind.QWord        => "REG_QWORD",
                                    RegistryValueKind.Binary       => "REG_BINARY",
                                    RegistryValueKind.MultiString  => "REG_MULTI_SZ",
                                    RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
                                    _                              => "REG_SZ",
                                };
                                _registry[$"{fullKey3b}|{valName}"] = new QaRegEntry
                                { Key = fullKey3b, Value = string.IsNullOrEmpty(valName) ? "(Default)" : valName, Data = RegDataToString(val), Type = type3b };
                            }
                        }
                    }
                }
            }
            catch (Exception ex3b)
            {
                File.AppendAllText(_diagPath,
                    $"[{DateTime.Now:HH:mm:ss}] Section3b ERROR: {ex3b.Message}\n");
            }

            // 4. App-specific registry keys — only scan keys matching the app name keyword.
            //    This scan walks HKLM/HKCU\SOFTWARE looking for the app's own keys. It is gated
            //    hard because a short or generic keyword (e.g. "test" from test.bat) matches
            //    substrings all over SOFTWARE (TestPlatform, Attestation, latest, ...) and floods
            //    the GUI with hundreds of unrelated entries (Classes byte-stream handlers, etc.).
            try
            {
                // Extract keyword: "FileCompare Setup 1.0.exe" -> "filecompare"
                string keyword = Path.GetFileNameWithoutExtension(_exeName).Split(' ')[0];
                keyword = Regex.Replace(keyword, @"[\d._-]+$", "").Trim();

                // Generic words that match far too broadly across SOFTWARE — never use them as a
                // scan keyword (they produce noise, not the app's keys).
                var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "test", "setup", "install", "installer", "update", "updater",
                    "app", "main", "run", "start", "temp", "data", "tool", "tools",
                    "win", "win32", "win64", "x64", "x86", "the", "new", "out",
                    "script", "batch", "exec", "execute", "cmd", "bat", "ps1",
                };

                // Only scan for installer-style sources. Scripts (.bat/.cmd/.ps1) and other
                // non-installers don't create app-named SOFTWARE keys; running the scan for them
                // only surfaces noise. Section 3/3b still capture any real Uninstall entries.
                string srcExt = Path.GetExtension(_exeFullPath).ToLowerInvariant();
                bool isInstallerSource = srcExt is ".exe" or ".msi" or ".msp";

                if (keyword.Length >= 4 && !stopWords.Contains(keyword) && isInstallerSource)
                {
                    ScanRegistryForKeyword(Registry.LocalMachine, "SOFTWARE",
                        "HKEY_LOCAL_MACHINE\\SOFTWARE", keyword, 0);
                    ScanRegistryForKeyword(Registry.CurrentUser, "SOFTWARE",
                        "HKEY_CURRENT_USER\\SOFTWARE", keyword, 0);
                }
                else
                {
                    File.AppendAllText(_diagPath,
                        $"[{DateTime.Now:HH:mm:ss}] Section4 skipped: keyword='{keyword}', ext='{srcExt}' (generic/short/non-installer)\n");
                }
            }
            catch { }
        }

        private void ScanRegistryForKeyword(RegistryKey hive, string subPath,
            string displayPath, string keyword, int depth)
        {
            if (depth > 5) return;
            try
            {
                using var key = hive.OpenSubKey(subPath);
                if (key == null) return;

                // A key "matches" only when its OWN leaf name contains the keyword (e.g.
                // ...\SOFTWARE\Smekal matches "smekal"). We deliberately do NOT match on the
                // full path: an ancestor that happened to contain the substring would otherwise
                // pull in every descendant value (the old behaviour that flooded the GUI).
                string leafName = subPath.Contains('\\')
                    ? subPath.Substring(subPath.LastIndexOf('\\') + 1)
                    : subPath;
                bool keyMatches = leafName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

                if (keyMatches)
                {
                    foreach (var valName in key.GetValueNames())
                    {
                        var val = key.GetValue(valName);
                        if (val == null) continue;
                        string mapKey = $"{displayPath}|{valName}";
                        if (_registry.ContainsKey(mapKey)) continue;
                        string type = key.GetValueKind(valName) switch
                        {
                            RegistryValueKind.DWord        => "REG_DWORD",
                            RegistryValueKind.QWord        => "REG_QWORD",
                            RegistryValueKind.Binary       => "REG_BINARY",
                            RegistryValueKind.MultiString  => "REG_MULTI_SZ",
                            RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
                            _                              => "REG_SZ",
                        };
                        _registry[mapKey] = new QaRegEntry
                        { Key = displayPath, Value = string.IsNullOrEmpty(valName) ? "(Default)" : valName, Data = RegDataToString(val), Type = type };
                    }
                }

                foreach (var sub in key.GetSubKeyNames())
                {
                    bool subMatches = sub.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                    // Recurse only when:
                    //   • we're already inside a matched subtree (keyMatches) — collect the app's
                    //     own child keys, OR
                    //   • this child's name matches the keyword — descend to find the app key, OR
                    //   • we're still at the shallow top level (depth < 1) — one level of search so
                    //     a direct child like SOFTWARE\Smekal is reachable.
                    // The previous "depth < 2" blanket recursion is removed: it walked the entire
                    // SOFTWARE\Classes tree regardless of the keyword.
                    if (keyMatches || subMatches || depth < 1)
                        ScanRegistryForKeyword(hive, $"{subPath}\\{sub}",
                            $"{displayPath}\\{sub}", keyword, depth + 1);
                }
            }
            catch { }
        }

        // Exclusion patterns read from ValidateSource.json (consolidated config).
        private sealed class ExclPatterns
        {
            public List<string> FilePaths      = new();
            public List<string> FileExtensions = new();
            public List<string> RegistryKeys   = new();
            public List<string> ShortcutNames  = new();
            public List<string> FolderPaths    = new();
        }

        // Loads exclusion arrays from ValidateSource.json. Returns empty patterns on any failure
        // (so capture still writes a valid .svr — just with no pattern-based exclusions).
        private static ExclPatterns LoadExclusionPatterns()
        {
            var p = new ExclPatterns();
            try
            {
                string exeDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                var candidates = new[]
                {
                    @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidateSource.json",
                    Path.Combine(exeDir, "Templates", "ValidateSource.json"),
                    Path.Combine(exeDir, "ValidateSource.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", "ValidateSource.json"),
                };
                foreach (var path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    void Fill(string prop, List<string> list)
                    {
                        if (root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Array)
                            foreach (var item in el.EnumerateArray())
                            {
                                var v = item.GetString();
                                if (!string.IsNullOrWhiteSpace(v)) list.Add(v!);
                            }
                    }
                    Fill("FilePaths",      p.FilePaths);
                    Fill("FileExtensions", p.FileExtensions);
                    Fill("RegistryKeys",   p.RegistryKeys);
                    Fill("ShortcutNames",  p.ShortcutNames);
                    Fill("FolderPaths",    p.FolderPaths);
                    break; // first found wins
                }
            }
            catch { }
            return p;
        }

        // Computes the "Category|DisplayPath|Value" exclusion entries for items in the snapshot
        // that match the ValidateSource.json exclusion patterns. This bakes the OS/noise exclusions
        // into the .svr at capture time, so Validate Package and MSI creation honor them even
        // before the user manually reviews the capture. (User checkbox exclusions are added later
        // by ValidateSourcePage when the .svr is saved.)
        private static List<string> ComputeExcludedPaths(QaSnapshot snap, ExclPatterns p)
        {
            var excluded = new List<string>();

            bool MatchPath(string path, List<string> patterns)
            {
                foreach (var pat in patterns)
                    if (!string.IsNullOrWhiteSpace(pat) &&
                        path.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                return false;
            }

            foreach (var f in snap.Files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (MatchPath(f, p.FilePaths) || MatchPath(f, p.FolderPaths) ||
                    p.FileExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                    excluded.Add($"File|{f}|");
            }
            foreach (var fo in snap.Folders)
                if (MatchPath(fo, p.FilePaths) || MatchPath(fo, p.FolderPaths))
                    excluded.Add($"Folder|{fo}|");
            foreach (var sc in snap.Shortcuts)
                if (MatchPath(sc, p.ShortcutNames))
                    excluded.Add($"Shortcut|{sc}|");
            foreach (var r in snap.Registry)
                foreach (var rk in p.RegistryKeys)
                    if (!string.IsNullOrWhiteSpace(rk) &&
                        r.Key.StartsWith(rk, StringComparison.OrdinalIgnoreCase))
                    { excluded.Add($"Registry|{r.Key}|{r.Value}"); break; }

            return excluded;
        }

        private void WriteSnapshot()
        {
            // Pattern-based exclusions (from ValidateSource.json) are baked into ExcludedPaths below.
            // The user's manual checkbox exclusions are applied later by ValidateSourcePage when the
            // user reviews and saves the .svr. Only runtime-only items (deleted before capture ended)
            // are dropped from the main lists.

            var snap = new QaSnapshot
            {
                FileName    = _exeName,
                FilePath    = _exeName,
                Arguments   = _exeArgs,
                CaptureTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),

                // Only include files that still exist — runtime-only files (e.g. temp lock files)
                // are created and deleted during install; they should not appear in the snapshot.
                // .lnk shortcuts are captured separately in Shortcuts — exclude from Files
                Files = _files.Keys
                    .Where(f => File.Exists(f) && !f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),

                // Only include folders that still exist on disk
                Folders = _folders.Keys
                    .Where(f => Directory.Exists(f))
                    .OrderBy(f => f)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),

                Shortcuts = _shortcuts.Keys
                    .Where(f => File.Exists(f))
                    .OrderBy(s => s)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),

                Registry = _registry.Values
                    .OrderBy(r => r.Key).ThenBy(r => r.Value)
                    .ToList(),

                EnvVars = _envVars.Values
                    .OrderBy(e => e.Name).ToList(),

                Services = _services.Values
                    .OrderBy(s => s.Name).ToList(),

                Certificates = _certs.Keys
                    .OrderBy(c => c).ToList(),
            };

            // Bake OS/noise exclusions (from ValidateSource.json patterns) into the .svr so
            // Validate Package and MSI creation honor them even before manual review. User
            // checkbox exclusions are added later by ValidateSourcePage when the .svr is saved.
            try { snap.ExcludedPaths = ComputeExcludedPaths(snap, LoadExclusionPatterns()); }
            catch { snap.ExcludedPaths = new(); }

            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };
            string json = JsonSerializer.Serialize(snap, opts);
            File.WriteAllText(_outputPath, json, new UTF8Encoding(false));
            File.AppendAllText(_diagPath,
                $"[{DateTime.Now:HH:mm:ss}] Snapshot written: {_outputPath} " +
                $"(files={snap.Files.Count}, folders={snap.Folders.Count}, " +
                $"shortcuts={snap.Shortcuts.Count}, registry={snap.Registry.Count}, " +
                $"envVars={snap.EnvVars.Count}, services={snap.Services.Count}, " +
                $"excluded={snap.ExcludedPaths.Count})\n");
        }

        private Dictionary<string, string> SnapshotUninstallRegistry()
        {
            var snap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Must cover the same hives the post-install scan reads, HKCU included. If the
                // baseline skipped HKCU while the scan read it, every pre-existing per-user app
                // would look brand new on the next capture.
                foreach (var (hive, sub) in new[] {
                    (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                    (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                    (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") })
                {
                    using var key = hive.OpenSubKey(sub);
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames())
                        snap[name] = name;
                }
            }
            catch { }
            return snap;
        }

        // ═══════════════════════════════════════════════════════════
        //  STATIC HELPERS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Detects Start-ADTProcess in a ps1 script and returns the EXE name and args.
        /// Returns null if no EXE install found.
        /// </summary>
        /// <summary>
        /// Extracts AppVendor / AppName / AppVersion from an Invoke-AppDeployToolkit.ps1
        /// and builds a ValidatePackage_Vendor_Name_Version log filename in %TEMP%.
        /// Returns empty string if nothing can be extracted.
        /// </summary>
        private static string BuildDiagLogPath(string ps1Path)
        {
            try
            {
                string text = File.ReadAllText(ps1Path, System.Text.Encoding.UTF8);
                string ExtractParam(string param)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(text,
                        $@"(?m)^\s*{param}\s*=\s*['""]([^'""]+)['""]",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    return m.Success ? m.Groups[1].Value.Trim() : "";
                }
                string vendor  = ExtractParam("AppVendor");
                string name    = ExtractParam("AppName");
                string version = ExtractParam("AppVersion");

                static string Safe(string s) =>
                    System.Text.RegularExpressions.Regex.Replace(s, @"[^\w\-.]", "");

                var parts = new[] { "ValidatePackage", Safe(vendor), Safe(name), Safe(version) }
                    .Where(p => !string.IsNullOrEmpty(p));
                string baseName = string.Join("_", parts);
                if (string.IsNullOrEmpty(baseName)) baseName = "ValidatePackage";
                return Path.Combine(Path.GetTempPath(), baseName + ".log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "ValidatePackage.log");
            }
        }

        public static (string FileName, string ExeArgs)? DetectExeInstall(string ps1Path)
        {
            // Name the diag log ValidatePackage_<Vendor>_<Name>_<Version>.log
            string diagLog = File.Exists(ps1Path)
                ? BuildDiagLogPath(ps1Path)
                : Path.Combine(Path.GetTempPath(), "ValidatePackage.log");
            var diag = new System.Text.StringBuilder();
            diag.AppendLine($"[{DateTime.Now:HH:mm:ss}] DetectExeInstall called: {ps1Path}");

            if (!File.Exists(ps1Path))
            {
                diag.AppendLine("  ps1 file does not exist!");
                File.AppendAllText(diagLog, diag.ToString());
                return null;
            }

            try
            {
                Directory.CreateDirectory(@"C:\Temp");

                // Search the passed ps1 AND all ps1 files in the same folder
                var candidateFiles = new List<string> { ps1Path };
                string? dir = Path.GetDirectoryName(ps1Path);
                if (dir != null)
                    foreach (var f in Directory.GetFiles(dir, "*.ps1", SearchOption.AllDirectories))
                        if (!candidateFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                            candidateFiles.Add(f);

                diag.AppendLine($"  Searching {candidateFiles.Count} ps1 files...");

                foreach (var file in candidateFiles)
                {
                    string text;
                    try { text = File.ReadAllText(file, System.Text.Encoding.UTF8); }
                    catch { continue; }

                    bool hasStart = text.IndexOf("Start-ADTProcess", StringComparison.OrdinalIgnoreCase) >= 0;
                    diag.AppendLine($"  {Path.GetFileName(file)}: hasStart-ADTProcess={hasStart}");

                    if (!hasStart) continue;

                    // Pattern 1: -FilePath immediately after command
                    var m = Regex.Match(text,
                        @"Start-ADTProcess\s*`?\s*-FilePath\s+[""']([^""']+\.exe)[""']",
                        RegexOptions.IgnoreCase | RegexOptions.Multiline);

                    // Pattern 2: -FilePath anywhere within 400 chars of Start-ADTProcess
                    if (!m.Success)
                        m = Regex.Match(text,
                            @"Start-ADTProcess[\s\S]{0,400}?-FilePath\s+[""']([^""']+\.exe)[""']",
                            RegexOptions.IgnoreCase);

                    // Pattern 3: unquoted path (no spaces)
                    if (!m.Success)
                        m = Regex.Match(text,
                            @"Start-ADTProcess[\s\S]{0,400}?-FilePath\s+(\S+\.exe)",
                            RegexOptions.IgnoreCase);

                    diag.AppendLine($"    regex match success={m.Success}" +
                        (m.Success ? $", group1={m.Groups[1].Value}" : ""));

                    if (!m.Success) continue;

                    string rawPath = m.Groups[1].Value.Trim('\'', '"');

                    // Strip PS variable prefix: $($adtSession.DirFiles)\ or $var\
                    rawPath = Regex.Replace(rawPath, @"^\$\([^)]+\)[/\\]?", "", RegexOptions.IgnoreCase);
                    rawPath = Regex.Replace(rawPath, @"^\$\w+[/\\]?", "", RegexOptions.IgnoreCase);

                    string exeName = Path.GetFileName(rawPath);
                    diag.AppendLine($"    exeName={exeName}");

                    if (string.IsNullOrWhiteSpace(exeName) ||
                        !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Extract -ArgumentList
                    // -ArgumentList usually appears AFTER -FilePath, so it is not inside m.Value.
                    // Search a window of the script starting at the Start-ADTProcess match.
                    int argFrom = m.Index;
                    int argLen  = Math.Min(600, text.Length - argFrom);
                    var argM = Regex.Match(text.Substring(argFrom, argLen),
                        @"-ArgumentList\s+[\('""](.*?)[\)'""]",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    string args = argM.Success ? argM.Groups[1].Value.Trim() : "";

                    diag.AppendLine($"  FOUND: exeName={exeName}, args={args}");
                    File.AppendAllText(diagLog, diag.ToString());
                    return (exeName, args);
                }

                diag.AppendLine("  No EXE install found in any ps1 file.");
                File.AppendAllText(diagLog, diag.ToString());
                return null;
            }
            catch (Exception ex)
            {
                File.AppendAllText(diagLog, diag + $"  EXCEPTION: {ex.Message}\n");
                return null;
            }
        }

        /// <summary>
        /// Reads SVRPath from ValidateSource.json.
        /// Falls back to C:\Temp\Validate Source if not set.
        /// </summary>
        private static string ResolveQAsnapDir()
        {
            const string DefaultDir = @"C:\Temp\Validate Source";

            string? resolved = null;
            try
            {
                string exeDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";

                // Priority: ValidateSource.json — SVRPath key (renamed from QasnapPath)
                var svCandidates = new[]
                {
                    @"C:\Program Files\Smekal\SwiftPack Studio\Templates\ValidateSource.json",
                    Path.Combine(exeDir, "Templates", "ValidateSource.json"),
                    Path.Combine(exeDir, "ValidateSource.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", "ValidateSource.json"),
                };
                foreach (var path in svCandidates)
                {
                    if (!File.Exists(path)) continue;
                    var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var propName in new[] { "SVRPath", "QasnapPath", "QAsnapPath" })
                    {
                        if (doc.RootElement.TryGetProperty(propName, out var el))
                        {
                            string v = el.GetString()?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(v))
                            {
                                resolved = Environment.ExpandEnvironmentVariables(v);
                                break;
                            }
                        }
                    }
                    // ValidateSource.json was found — whether SVRPath was set or not,
                    // use it. Empty SVRPath means
                    // "use the default C:\Temp\Validate Source".
                    if (resolved == null) resolved = DefaultDir;
                    break; // only read first found ValidateSource.json
                }

                // .svr output is always ValidateSource.json SVRPath (if set) or the default
                // C:\Temp\Validate Source.
            }
            catch { }

            // Use resolved path or fall back to default — always ensure folder exists
            string dir = !string.IsNullOrEmpty(resolved) ? resolved : DefaultDir;
            try { Directory.CreateDirectory(dir); }
            catch
            {
                // If creating the resolved path fails, fall back to default
                dir = DefaultDir;
                try { Directory.CreateDirectory(dir); } catch { }
            }
            return dir;
        }

        /// <summary>
        /// Returns the output path that will be used for a given EXE name.
        /// Respects QasnapPath from ValidateSource.json; falls back to C:\Temp\Validate Source.
        /// </summary>
        public static string GetOutputPath(string exeName) =>
            Path.Combine(ResolveQAsnapDir(),
                Path.GetFileNameWithoutExtension(exeName) + " - For QA.svr");
    }
}
