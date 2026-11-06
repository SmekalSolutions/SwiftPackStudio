using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;

namespace SwiftPack.Views
{
    /// <summary>
    /// Read-only "Document" tab. Shows Package Details (from the Download/Package/Execute
    /// tabs + the two %TEMP% AutoPackage logs) and Intune Details (from the Intune JSON).
    /// The same data model is used to write the .txt / .docx documents during the package
    /// flow when CopytxtToDocuments / CopydocxToDocuments is "Yes".
    /// </summary>
    public partial class AutoDocumentPage : Page
    {
        public AutoDocumentPage()
        {
            InitializeComponent();
            // The Execute tab announces every finished deploy; this page owns everything
            // document-shaped that must happen next: the log paths into the model and the
            // UI, and the .txt/.docx into the package's Documents folder. Subscribing here
            // keeps the strip self-contained - no host wiring required. One instance lives
            // for the whole app, so the static subscription never needs undoing.
            AutoExecutePage.DeployCompleted += OnAutoDeployCompleted;
        }

        // The model currently on screen. Populate used to write the TextBoxes and forget,
        // which is exactly why nothing could react to a deploy finishing: by then the data
        // had no owner.
        private DocumentModel? _model;

        // ── Data model ────────────────────────────────────────────────────
        public class DocumentModel
        {
            // Package Details
            public string PackageName     = "";
            public string DisplayName     = "";
            public string Publisher       = "";
            public string Version         = "";
            public string SourceLocation  = "";
            public string PackageLocation = "";
            public string ShortcutPath    = "";
            public string InstallLocation = "";
            public string InstallLogPath  = "";
            public string UninstallLogPath= "";
            // Intune Details
            public string IntuneName        = "";
            public string IntunePublisher   = "";
            public string IntuneDescription = "";
            public string IntuneCategory    = "";
            public string InstallCommand    = "";
            public string UninstallCommand  = "";
            public string InstallBehavior   = "";
            public string DetectionRule     = "";
            public string Dependencies      = "";
            public string Supersedence      = "";
            public string Assignments       = "";
            public string AppOwnerEmail     = "";
            public string UatUsers          = "";
        }

        // ── Populate the read-only UI ──────────────────────────────────────
        public void Populate(DocumentModel m)
        {
            if (m == null) return;
            _model = m;
            void Set(TextBox? tb, string v) { if (tb != null) tb.Text = v ?? ""; }

            Set(DocPkgName,         m.PackageName);
            Set(DocDisplayName,     m.DisplayName);
            Set(DocPkgPublisher,    m.Publisher);
            Set(DocPkgVersion,      m.Version);
            Set(DocSourceLocation,  m.SourceLocation);
            Set(DocPackageLocation, m.PackageLocation);
            Set(DocInstallLocation, m.InstallLocation);
            Set(DocInstallLog,      m.InstallLogPath);
            Set(DocUninstallLog,    m.UninstallLogPath);

            Set(DocIntuneName,        m.IntuneName);
            Set(DocIntunePublisher,   m.IntunePublisher);
            Set(DocIntuneDescription, m.IntuneDescription);
            Set(DocIntuneCategory,    m.IntuneCategory);
            Set(DocInstallCommand,    m.InstallCommand);
            Set(DocUninstallCommand,  m.UninstallCommand);
            Set(DocInstallBehavior,   m.InstallBehavior);
            Set(DocDetectionRule,     m.DetectionRule);
            Set(DocDependencies,      m.Dependencies);
            Set(DocSupersedence,      m.Supersedence);
            Set(DocAssignments,       m.Assignments);
            Set(DocAppOwnerEmail,     m.AppOwnerEmail);

            if (TxtDocStatus != null)
                TxtDocStatus.Text = string.IsNullOrWhiteSpace(m.PackageName) ? "" : "Auto-populated";

            // Ordering-proof hand-off: whichever happens LAST - the host populating this
            // tab, or the Execute tab finishing a deploy - completes the picture. The
            // event covers "populated first, deploy later"; this covers "deploy first,
            // populate later", which is the strip's actual sequence and the reason the
            // event alone changed nothing on screen.
            TrySyncFromExecute(refreshUi: true);
        }

        /// <summary>Clear all fields (e.g. when a new download starts).</summary>
        public void Clear()
        {
            Populate(new DocumentModel());
            if (TxtDocStatus != null) TxtDocStatus.Text = "";
        }

        // ── Deploy hand-off from the Execute tab ───────────────────────────
        private void OnAutoDeployCompleted(string deployMode, bool success,
            string? logInPackage, string? psadtLog, string scriptDir)
        {
            // All parameters are also available through the statics on AutoExecutePage,
            // which is what TrySyncFromExecute reads - so this handler only needs to
            // nudge the sync on the UI thread for the case where this page already
            // exists and is populated when the deploy finishes.
            Dispatcher.BeginInvoke(new Action(() => TrySyncFromExecute(refreshUi: true)));
        }

        /// <summary>
        /// Merge the Execute tab's recorded log paths into the current model and, when the
        /// settings ask for them, (re)write the .txt/.docx into the package's Documents
        /// folder. Anchored on the MODEL's PackageLocation: a recorded path counts only if
        /// it lives under that package or its file name starts with the package folder
        /// name, so one app's logs can never leak into another app's document. Idempotent -
        /// repeated calls rewrite the same files with the same content.
        /// </summary>
        private void TrySyncFromExecute(bool refreshUi)
        {
            try
            {
                var m = _model;
                if (m == null) return;
                string pkg = (m.PackageLocation ?? "").TrimEnd('\\', '/');
                if (pkg.Length == 0) return;
                string pkgName = Path.GetFileName(pkg);

                bool Matches(string? p) =>
                    !string.IsNullOrWhiteSpace(p) &&
                    (p!.StartsWith(pkg + "\\", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(p).StartsWith(pkgName + "_", StringComparison.OrdinalIgnoreCase));

                bool changed = false;
                string? li = AutoExecutePage.LastAutoInstallLogPath;
                if (string.IsNullOrWhiteSpace(m.InstallLogPath) && Matches(li))
                { m.InstallLogPath = li!; changed = true; }
                string? lu = AutoExecutePage.LastAutoUninstallLogPath;
                if (string.IsNullOrWhiteSpace(m.UninstallLogPath) && Matches(lu))
                { m.UninstallLogPath = lu!; changed = true; }

                if (changed && refreshUi)
                {
                    if (DocInstallLog   != null) DocInstallLog.Text   = m.InstallLogPath;
                    if (DocUninstallLog != null) DocUninstallLog.Text = m.UninstallLogPath;
                }

                // Documents: only once execute has demonstrably run for THIS package (at
                // least one matching log path), and only when the settings ask for them.
                if (string.IsNullOrWhiteSpace(m.InstallLogPath) && string.IsNullOrWhiteSpace(m.UninstallLogPath)) return;
                if (!Directory.Exists(pkg)) return;
                var (wantTxt, wantDocx) = ReadDocumentFlags();
                if (!wantTxt && !wantDocx) return;

                var written = GenerateDocuments(m, Path.Combine(pkg, "Documents"), wantTxt, wantDocx,
                    s => System.Diagnostics.Debug.WriteLine("[AutoDocumentPage] " + s));
                if (TxtDocStatus != null)
                    TxtDocStatus.Text = written.Count > 0
                        ? "Auto-populated · wrote " + string.Join(", ", written.Select(Path.GetFileName))
                        : "Auto-populated · document write FAILED (see debug log)";
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine("[AutoDocumentPage] sync failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // The Settings window's "Copy .txt / .docx To Documents" flags live in the GLOBAL
        // Templates\AutoPackage.json - the per-app json never carries them (its Copy*
        // properties are [JsonIgnore], so those keys are never serialized). Read straight
        // from the file each time so this page stays self-contained and always honours the
        // current setting; comment-skipping because config files here may carry // headers.
        private static (bool txt, bool docx) ReadDocumentFlags()
        {
            foreach (string p in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Templates", "AutoPackage.json"),
                @"C:\Program Files\Smekal\SwiftPack Studio\Templates\AutoPackage.json",
            })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p),
                        new System.Text.Json.JsonDocumentOptions
                        {
                            CommentHandling    = System.Text.Json.JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        });
                    bool Get(string key) =>
                        doc.RootElement.TryGetProperty(key, out var v) &&
                        v.ValueKind == System.Text.Json.JsonValueKind.String &&
                        string.Equals(v.GetString(), "Yes", StringComparison.OrdinalIgnoreCase);
                    return (Get("CopytxtToDocuments"), Get("CopydocxToDocuments"));
                }
                catch (Exception ex)
                { System.Diagnostics.Debug.WriteLine("[AutoDocumentPage] flags read failed (" + p + "): " + ex.Message); }
            }
            return (false, false);
        }

        // ── Document rendering (shared by .txt and .docx) ───────────────────
        // Ordered (label, value) rows grouped into the two sections.
        private static List<(string Label, string Value)> PackageRows(DocumentModel m) => new()
        {
            ("Package Name",      m.PackageName),
            ("Display Name",      m.DisplayName),
            ("Publisher",         m.Publisher),
            ("Version",           m.Version),
            ("Source Location",   m.SourceLocation),
            ("Package Location",  m.PackageLocation),
            ("Install Location",  m.InstallLocation),
            ("Install log path",  m.InstallLogPath),
            ("Uninstall log path",m.UninstallLogPath),
        };

        private static List<(string Label, string Value)> IntuneRows(DocumentModel m) => new()
        {
            ("Name",              m.IntuneName),
            ("Publisher",         m.IntunePublisher),
            ("Description",       m.IntuneDescription),
            ("Category",          m.IntuneCategory),
            ("Install command",   m.InstallCommand),
            ("Uninstall command", m.UninstallCommand),
            ("Install Behaviour", m.InstallBehavior),
            ("Detection Rule",    m.DetectionRule),
            ("Dependencies",      m.Dependencies),
            ("Supersedence",      m.Supersedence),
            ("Assignments",       m.Assignments),
            ("App Owner email",   m.AppOwnerEmail),
        };

        /// <summary>Build a plain-text rendering of the document.</summary>
        public static string BuildPlainText(DocumentModel m)
        {
            var sb = new StringBuilder();
            string eq   = new string('=', 80);
            string dash = new string('-', 72);

            sb.AppendLine(eq);
            sb.AppendLine("              PACKAGE DELIVERY DOCUMENT");
            sb.AppendLine($"     {m.DisplayName} {m.Version}  |  {m.Publisher}");
            sb.AppendLine($"     Generated: {DateTime.Now:yyyy-MM-dd HH:mm}  |  Created by: N/A");
            sb.AppendLine(eq);
            sb.AppendLine();

            void Blk(string hdr, List<(string Label, string Value)> rows)
            {
                sb.AppendLine(dash);
                sb.AppendLine(hdr);
                sb.AppendLine(dash);
                foreach (var (label, value) in rows)
                {
                    string[] lines = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    sb.AppendLine($"{label,-30}: {lines[0]}");
                    for (int i = 1; i < lines.Length; i++)
                        sb.AppendLine(new string(' ', 32) + lines[i]);
                }
                sb.AppendLine();
            }

            Blk("PACKAGE DETAILS", PackageRows(m));
            Blk("INTUNE DETAILS", IntuneRows(m));
            return sb.ToString();
        }

        /// <summary>
        /// Generate documents for the package and copy them into the package's Documents
        /// folder. Honours wantTxt / wantDocx. Returns the list of files written.
        /// Never throws — failures are reported via the optional log callback.
        /// </summary>
        public static List<string> GenerateDocuments(
            DocumentModel m,
            string documentsFolder,
            bool wantTxt,
            bool wantDocx,
            Action<string>? log = null)
        {
            var written = new List<string>();
            if (m == null) return written;
            void L(string s) { try { log?.Invoke(s); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoDocumentPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }

            try
            {
                if (!Directory.Exists(documentsFolder))
                    Directory.CreateDirectory(documentsFolder);
            }
            catch (Exception ex) { L($"[Document] Could not create folder '{documentsFolder}': {ex.Message}"); return written; }

            // If the model has no PackageName (e.g. the EXE flow, where TxtPackageName is not
            // populated), derive it from the package folder that contains the Documents folder
            // (e.g. Smekal_FileCompare_1.0_01). This names the .txt/.docx after the
            // package instead of a generic "Package", and keeps the document body consistent.
            if (string.IsNullOrWhiteSpace(m.PackageName))
            {
                try
                {
                    string parent = Path.GetDirectoryName(
                        documentsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
                    string folderName = Path.GetFileName(parent);
                    if (!string.IsNullOrWhiteSpace(folderName)) m.PackageName = folderName;
                }
                catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoDocumentPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); }
            }

            string baseName = string.IsNullOrWhiteSpace(m.PackageName) ? "Package" : m.PackageName;

            // ── .txt ────────────────────────────────────────────────────────
            if (wantTxt)
            {
                try
                {
                    string txtPath = Path.Combine(documentsFolder, baseName + ".txt");
                    File.WriteAllText(txtPath, BuildPlainText(m), new UTF8Encoding(false));
                    written.Add(txtPath);
                    L($"[Document] Wrote {txtPath}");
                }
                catch (Exception ex) { L($"[Document] .txt failed: {ex.Message}"); }
            }

            // ── .docx ─────────────────────────────────────────────────────
            if (wantDocx)
            {
                try
                {
                    string docxPath = Path.Combine(documentsFolder, baseName + ".docx");
                    WriteDocx(m, docxPath);
                    written.Add(docxPath);
                    L($"[Document] Wrote {docxPath}");
                }
                catch (Exception ex) { L($"[Document] .docx failed: {ex.Message}"); }
            }

            return written;
        }

        // ── Minimal DOCX writer (Open XML via System.IO.Packaging) ──────────
        // Builds a valid .docx without external packages by writing the WordprocessingML
        // parts directly. Keeps the document simple: a title, two section headings, and a
        // 2-column table per section.
        private static void WriteDocx(DocumentModel m, string path)
        {
            if (File.Exists(path)) { try { File.Delete(path); } catch (System.Exception exSwallow) { System.Diagnostics.Debug.WriteLine("[AutoDocumentPage] swallowed " + exSwallow.GetType().Name + ": " + exSwallow.Message); } }

            using var pkg = System.IO.Packaging.Package.Open(path, FileMode.Create, FileAccess.ReadWrite);

            var mainUri = new Uri("/word/document.xml", UriKind.Relative);
            var mainPart = pkg.CreatePart(mainUri,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
            pkg.CreateRelationship(mainUri, System.IO.Packaging.TargetMode.Internal,
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument",
                "rId1");

            string Esc(string? s)
            {
                s ??= "";
                // Remove characters that are not valid in XML 1.0, otherwise Word refuses to
                // open the .docx ("unreadable content"). Valid: tab, LF, CR, and the ranges
                // #x20-#xD7FF, #xE000-#xFFFD, plus well-formed surrogate pairs (#x10000+).
                var clean = new StringBuilder(s.Length);
                for (int ci = 0; ci < s.Length; ci++)
                {
                    char c = s[ci];
                    if (char.IsHighSurrogate(c) && ci + 1 < s.Length && char.IsLowSurrogate(s[ci + 1]))
                    {
                        clean.Append(c);
                        clean.Append(s[ci + 1]);
                        ci++;
                        continue;
                    }
                    if (c == '\t' || c == '\n' || c == '\r' ||
                        (c >= '\u0020' && c <= '\uD7FF') ||
                        (c >= '\uE000' && c <= '\uFFFD'))
                        clean.Append(c);
                }
                return System.Security.SecurityElement.Escape(clean.ToString()) ?? "";
            }

            string Para(string text, bool bold = false, int sizeHalfPt = 22)
            {
                string rpr = $"<w:rPr>{(bold ? "<w:b/>" : "")}<w:sz w:val=\"{sizeHalfPt}\"/></w:rPr>";
                return $"<w:p><w:pPr>{rpr}</w:pPr><w:r>{rpr}<w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r></w:p>";
            }

            string Row(string label, string value)
            {
                // Word won't break a line at '\', '/', '_' or '@', so a long unbroken path or
                // email (e.g. an install-log path) overflows its cell and runs off the page.
                // Insert a zero-width space (U+200B) after each of those characters: invisible,
                // and only acts as a wrap point when the text would otherwise overflow. '.' is
                // deliberately excluded so version numbers like 3.0.23 stay intact. U+200B is a
                // valid XML char and survives Esc() unchanged.
                static string Soften(string s)
                {
                    if (string.IsNullOrEmpty(s)) return s ?? "";
                    var sb = new StringBuilder(s.Length + 16);
                    foreach (char c in s)
                    {
                        sb.Append(c);
                        if (c == '\\' || c == '/' || c == '_' || c == '@') sb.Append('\u200B');
                    }
                    return sb.ToString();
                }

                string Cell(string t, bool bold, string w)
                {
                    // Split on newlines so multi-line values (Detection Rule) render line by line via <w:br/>.
                    string[] cl = (t ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    var run = new StringBuilder();
                    for (int i = 0; i < cl.Length; i++)
                    {
                        if (i > 0) run.Append("<w:br/>");
                        run.Append($"<w:t xml:space=\"preserve\">{Esc(Soften(cl[i]))}</w:t>");
                    }
                    return $"<w:tc><w:tcPr><w:tcW w:w=\"{w}\" w:type=\"dxa\"/></w:tcPr>" +
                           $"<w:p><w:r><w:rPr>{(bold ? "<w:b/>" : "")}<w:sz w:val=\"20\"/></w:rPr>" +
                           $"{run}</w:r></w:p></w:tc>";
                }
                return $"<w:tr>{Cell(label, true, "3000")}{Cell(value, false, "6000")}</w:tr>";
            }

            string Table(IEnumerable<(string Label, string Value)> rows)
            {
                var sb = new StringBuilder();
                sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"9000\" w:type=\"dxa\"/>" +
                          "<w:tblLayout w:type=\"fixed\"/>" +
                          "<w:tblBorders>" +
                          "<w:top w:val=\"single\" w:sz=\"4\" w:color=\"CCCCCC\"/>" +
                          "<w:left w:val=\"single\" w:sz=\"4\" w:color=\"CCCCCC\"/>" +
                          "<w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"CCCCCC\"/>" +
                          "<w:right w:val=\"single\" w:sz=\"4\" w:color=\"CCCCCC\"/>" +
                          "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"CCCCCC\"/>" +
                          "<w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"CCCCCC\"/>" +
                          "</w:tblBorders></w:tblPr>" +
                          "<w:tblGrid><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"6000\"/></w:tblGrid>");
                foreach (var (label, value) in rows) sb.Append(Row(label, value));
                sb.Append("</w:tbl>");
                return sb.ToString();
            }

            var body = new StringBuilder();
            body.Append(Para($"Package Document - {m.PackageName}", bold: true, sizeHalfPt: 30));
            body.Append(Para($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", bold: false, sizeHalfPt: 18));
            body.Append(Para("", false));
            body.Append(Para("Package Details", bold: true, sizeHalfPt: 26));
            body.Append(Table(PackageRows(m)));
            body.Append(Para("", false));
            body.Append(Para("Intune Details", bold: true, sizeHalfPt: 26));
            body.Append(Table(IntuneRows(m)));

            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:body>" + body +
                "<w:sectPr><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" " +
                "w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>" +
                "</w:body></w:document>";

            using var stream = mainPart.GetStream(FileMode.Create, FileAccess.Write);
            var bytes = new UTF8Encoding(false).GetBytes(xml);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
