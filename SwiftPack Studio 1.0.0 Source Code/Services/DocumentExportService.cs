using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SwiftPack.Models;

namespace SwiftPack.Services
{
    public static class DocumentExportService
    {
        public static void ExportDocx(CreateDocumentModel m, string path)
            => File.WriteAllBytes(path, BuildDocx(m));

        public static void ExportTxt(CreateDocumentModel m, string path)
            => File.WriteAllText(path, BuildTxt(m), new UTF8Encoding(false));

        // ── TXT ───────────────────────────────────────────────────────────────
        private static string BuildTxt(CreateDocumentModel m)
        {
            string uat = m.UatUsers.Any() ? string.Join(", ", m.UatUsers) : "None";
            var sb = new StringBuilder();
            string eq   = new string('=', 80);
            string dash = new string('-', 72);

            sb.AppendLine(eq);
            sb.AppendLine("              PACKAGE DELIVERY DOCUMENT");
            sb.AppendLine($"     {m.ApplicationName} {m.ApplicationVersion}  |  {m.ApplicationVendor}");
            sb.AppendLine($"     Generated: {DateTime.Now:yyyy-MM-dd HH:mm}  |  Created by: {Nz(m.PackageCreatedBy)}");
            sb.AppendLine(eq); sb.AppendLine();

            void Blk(string hdr, (string k, string v)[] rows)
            {
                sb.AppendLine(dash); sb.AppendLine(hdr); sb.AppendLine(dash);
                foreach (var (k, v) in rows)
                {
                    // Multi-line values (e.g. Detection Rules) keep their line breaks:
                    // first line after the label, remaining lines indented to align.
                    string[] lines = (v ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    sb.AppendLine($"{k,-30}: {lines[0]}");
                    for (int i = 1; i < lines.Length; i++)
                        sb.AppendLine(new string(' ', 32) + lines[i]);
                }
                sb.AppendLine();
            }

            Blk("PACKAGE INFORMATION", new[]
            {
                ("Package Name",         Nz(m.PackageName)),
                ("Application Name",     Nz(m.ApplicationName)),
                ("Application Vendor",   Nz(m.ApplicationVendor)),
                ("Application Version",  Nz(m.ApplicationVersion)),
                ("Package Location",     Nz(m.SourceMediaLocation)),
                ("Package Created By",   Nz(m.PackageCreatedBy)),
                ("Package Validated By", Nz(m.PackageValidatedBy)),
                ("Close Applications",   Nz(m.CloseApplications)),
                ("Shortcuts",            Nz(m.Shortcuts)),
                ("Install Log",          Nz(m.InstallLogPath)),
                ("Uninstall Log",        Nz(m.UninstallLogPath)),
                ("MSI Log",              Nz(m.MsiLogPath)),
            });
            Blk("REQUEST DETAILS", new[]
            {
                ("Request Number",       Nz(m.RequestId)),
                ("Source Location",      Nz(m.SourceLocation)),
                ("Requestor Name/Email", Nz(m.RequestorName)),
                ("App Owner Name/Email", Nz(m.ApplicationOwner)),
                ("Department",           Nz(m.Department)),
                ("Country",              Nz(m.Country)),
            });
            Blk("DEPLOYMENT CONFIGURATION", new[]
            {
                ("Intunewin File Name",  Nz(m.IntunewinFileName)),
                ("Intunewin File Size",  Nz(m.IntunewinFileSize)),
                ("Name",                 Nz(m.IntuneName)),
                ("Publisher",            Nz(m.Publisher)),
                ("App Version",          Nz(m.IntuneVersion)),
                ("Category",             Nz(m.CategoryName)),
                ("Description",          Nz(m.Description)),
                ("Install Command",      Nz(m.InstallCommandLine)),
                ("Uninstall Command",    Nz(m.UninstallCommandLine)),
                ("Install Behaviour",    Nz(m.InstallBehaviour)),
                ("Operating System",     Nz(m.OperatingSystem)),
                ("Detection Rules",      Nz(m.DetectionType)),
                ("Dependencies",         Nz(m.Dependencies)),
                ("Supersedence",         Nz(m.Supersedence)),
                ("Deployment Type",      Nz(m.DeploymentType)),
                ("Assignment Group",     Nz(m.AssignmentGroup)),
                ("UAT Users",            uat),
                ("Rollback Plan",        Nz(m.RollbackPlan)),
            });
            Blk("OTHER DETAILS", new[]
            {
                ("Known Issues",         string.IsNullOrWhiteSpace(m.KnownIssues) ? "None identified." : m.KnownIssues),
                ("Additional Info",      Nz(m.AdditionalInfo)),
            });

            sb.AppendLine(eq); sb.AppendLine("                     END OF DOCUMENT"); sb.AppendLine(eq);
            return sb.ToString();
        }

        // ── DOCX ──────────────────────────────────────────────────────────────
        private static byte[] BuildDocx(CreateDocumentModel m)
        {
            string uat = m.UatUsers.Any() ? string.Join(", ", m.UatUsers) : "None";
            var sb = new StringBuilder();

            sb.Append(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
<w:body>
");
            sb.Append(WPara("Package Delivery Document", "1F3E6E", 40, bold: true, align: "center", spAfter: 80));
            sb.Append(WPara($"{Xe(m.ApplicationName)} {Xe(m.ApplicationVersion)}  ·  {Xe(m.ApplicationVendor)}", "555555", 22, align: "center", spAfter: 50));
            sb.Append(WPara($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}  ·  Created by: {Xe(Nz(m.PackageCreatedBy))}", "999999", 18, align: "center", spAfter: 240));

            sb.Append(WTable("Package Information", new[]
            {
                ("Package Name",         Nz(m.PackageName)),
                ("Application Name",     Nz(m.ApplicationName)),
                ("Application Vendor",   Nz(m.ApplicationVendor)),
                ("Application Version",  Nz(m.ApplicationVersion)),
                ("Package Location",     Nz(m.SourceMediaLocation)),
                ("Package Created By",   Nz(m.PackageCreatedBy)),
                ("Package Validated By", Nz(m.PackageValidatedBy)),
                ("Close Applications",   Nz(m.CloseApplications)),
                ("Shortcuts",            Nz(m.Shortcuts)),
                ("Install Log",          Nz(m.InstallLogPath)),
                ("Uninstall Log",        Nz(m.UninstallLogPath)),
                ("MSI Log",              Nz(m.MsiLogPath)),
            }));
            sb.Append(WTable("Request Details", new[]
            {
                ("Request Number",           Nz(m.RequestId)),
                ("Source Location",          Nz(m.SourceLocation)),
                ("Requestor Name & Email",   Nz(m.RequestorName)),
                ("App Owner Name & Email",   Nz(m.ApplicationOwner)),
                ("Department Name",          Nz(m.Department)),
                ("Country",                  Nz(m.Country)),
            }));
            sb.Append(WTable("Deployment Configuration", new[]
            {
                ("Intunewin File Name",  Nz(m.IntunewinFileName)),
                ("Intunewin File Size",  Nz(m.IntunewinFileSize)),
                ("Name",                 Nz(m.IntuneName)),
                ("Publisher",            Nz(m.Publisher)),
                ("App Version",          Nz(m.IntuneVersion)),
                ("Category",             Nz(m.CategoryName)),
                ("Description",          Nz(m.Description)),
                ("Install Command",      Nz(m.InstallCommandLine)),
                ("Uninstall Command",    Nz(m.UninstallCommandLine)),
                ("Install Behaviour",    Nz(m.InstallBehaviour)),
                ("Operating System",     Nz(m.OperatingSystem)),
                ("Detection Rules",      Nz(m.DetectionType)),
                ("Dependencies",         Nz(m.Dependencies)),
                ("Supersedence",         Nz(m.Supersedence)),
                ("Deployment Type",      Nz(m.DeploymentType)),
                ("Assignment Group",     Nz(m.AssignmentGroup)),
                ("UAT Users",            uat),
                ("Rollback Plan",        Nz(m.RollbackPlan)),
            }));
            sb.Append(WTable("Other Details", new[]
            {
                ("Known Issues",      string.IsNullOrWhiteSpace(m.KnownIssues) ? "None identified." : m.KnownIssues),
                ("Additional Information", Nz(m.AdditionalInfo)),
            }));

            sb.Append("<w:sectPr/></w:body></w:document>");

            // Wrap in ZIP
            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                Ze(zip, "[Content_Types].xml", ContentTypesXml());
                Ze(zip, "_rels/.rels", RootRelsXml());
                Ze(zip, "word/document.xml", sb.ToString());
                Ze(zip, "word/_rels/document.xml.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships""/>");
                Ze(zip, "docProps/app.xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Properties xmlns=""http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"">
  <Application>SwiftPack Studio</Application>
</Properties>");
                Ze(zip, "docProps/core.xml", $@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<cp:coreProperties xmlns:cp=""http://schemas.openxmlformats.org/package/2006/metadata/core-properties""
                   xmlns:dc=""http://purl.org/dc/elements/1.1/""
                   xmlns:dcterms=""http://purl.org/dc/terms/""
                   xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <dc:title>Package Delivery Document – {Xe(m.ApplicationName)} {Xe(m.ApplicationVersion)}</dc:title>
  <dc:creator>{Xe(m.PackageCreatedBy)}</dc:creator>
  <dcterms:created xsi:type=""dcterms:W3CDTF"">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created>
</cp:coreProperties>");
            }
            return ms.ToArray();
        }

        private static void Ze(System.IO.Compression.ZipArchive z, string n, string xml)
        {
            var e = z.CreateEntry(n, System.IO.Compression.CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(xml);
        }

        private static string ContentTypesXml() => @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml""  ContentType=""application/xml""/>
  <Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/>
  <Override PartName=""/docProps/core.xml"" ContentType=""application/vnd.openxmlformats-package.core-properties+xml""/>
  <Override PartName=""/docProps/app.xml""  ContentType=""application/vnd.openxmlformats-officedocument.extended-properties+xml""/>
</Types>";

        private static string RootRelsXml() => @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"" Target=""docProps/core.xml""/>
  <Relationship Id=""rId3"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties"" Target=""docProps/app.xml""/>
</Relationships>";

        private static string WPara(string text, string hex, int halfPts,
            bool bold = false, string align = "left", int spAfter = 120)
        {
            string b = bold ? "<w:b/>" : "";
            return $@"<w:p>
  <w:pPr><w:jc w:val=""{align}""/><w:spacing w:after=""{spAfter}""/></w:pPr>
  <w:r><w:rPr>{b}<w:color w:val=""{hex}""/>
    <w:sz w:val=""{halfPts}""/><w:szCs w:val=""{halfPts}""/>
    <w:rFonts w:ascii=""Segoe UI"" w:hAnsi=""Segoe UI""/>
  </w:rPr><w:t xml:space=""preserve"">{text}</w:t></w:r>
</w:p>
";
        }

        private static string WTable(string hdr, IEnumerable<(string Label, string Value)> rows)
        {
            var sb = new StringBuilder();
            sb.Append(@"<w:tbl>
<w:tblPr>
  <w:tblW w:w=""9360"" w:type=""dxa""/>
  <w:tblLayout w:val=""fixed""/>
  <w:tblBorders>
    <w:top     w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""BBBBBB""/>
    <w:left    w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""BBBBBB""/>
    <w:bottom  w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""BBBBBB""/>
    <w:right   w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""BBBBBB""/>
    <w:insideH w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""BBBBBB""/>
    <w:insideV w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""BBBBBB""/>
  </w:tblBorders>
</w:tblPr>
<w:tblGrid><w:gridCol w:w=""2880""/><w:gridCol w:w=""6480""/></w:tblGrid>
");
            sb.Append($@"<w:tr>
  <w:tc>
    <w:tcPr><w:gridSpan w:val=""2""/><w:tcW w:w=""9360"" w:type=""dxa""/>
      <w:shd w:val=""clear"" w:color=""auto"" w:fill=""1F3E6E""/>
      <w:tcMar><w:top w:w=""80"" w:type=""dxa""/><w:left w:w=""140"" w:type=""dxa""/>
               <w:bottom w:w=""80"" w:type=""dxa""/><w:right w:w=""140"" w:type=""dxa""/></w:tcMar>
    </w:tcPr>
    <w:p><w:r><w:rPr><w:b/><w:color w:val=""FFFFFF""/>
      <w:sz w:val=""22""/><w:szCs w:val=""22""/>
      <w:rFonts w:ascii=""Segoe UI"" w:hAnsi=""Segoe UI""/>
    </w:rPr><w:t xml:space=""preserve"">{Xe(hdr)}</w:t></w:r></w:p>
  </w:tc>
</w:tr>
");
            foreach (var (label, value) in rows)
            {
                // Value cell: one <w:p> per line so multi-line values (e.g. Detection
                // Rules) keep their line breaks. Multi-line values switch to Consolas
                // so the colon-aligned block looks exactly like the Create Document page.
                string[] vLines = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                string   vFont  = vLines.Length > 1 ? "Consolas" : "Segoe UI";
                var vp = new StringBuilder();
                foreach (string ln in vLines)
                    vp.Append($@"<w:p><w:r><w:rPr><w:sz w:val=""20""/><w:szCs w:val=""20""/>
      <w:rFonts w:ascii=""{vFont}"" w:hAnsi=""{vFont}""/>
    </w:rPr><w:t xml:space=""preserve"">{Xe(ln)}</w:t></w:r></w:p>
");
                sb.Append($@"<w:tr>
  <w:tc>
    <w:tcPr><w:tcW w:w=""2880"" w:type=""dxa""/>
      <w:shd w:val=""clear"" w:color=""auto"" w:fill=""D9E8F6""/>
      <w:tcMar><w:top w:w=""80"" w:type=""dxa""/><w:left w:w=""140"" w:type=""dxa""/>
               <w:bottom w:w=""80"" w:type=""dxa""/><w:right w:w=""140"" w:type=""dxa""/></w:tcMar>
    </w:tcPr>
    <w:p><w:r><w:rPr><w:b/><w:sz w:val=""20""/><w:szCs w:val=""20""/>
      <w:rFonts w:ascii=""Segoe UI"" w:hAnsi=""Segoe UI""/>
    </w:rPr><w:t xml:space=""preserve"">{Xe(label)}</w:t></w:r></w:p>
  </w:tc>
  <w:tc>
    <w:tcPr><w:tcW w:w=""6480"" w:type=""dxa""/>
      <w:tcMar><w:top w:w=""80"" w:type=""dxa""/><w:left w:w=""140"" w:type=""dxa""/>
               <w:bottom w:w=""80"" w:type=""dxa""/><w:right w:w=""140"" w:type=""dxa""/></w:tcMar>
    </w:tcPr>
    {vp}
  </w:tc>
</w:tr>
");
            }
            sb.AppendLine("</w:tbl>");
            sb.AppendLine(@"<w:p><w:pPr><w:spacing w:after=""80""/></w:pPr></w:p>");
            return sb.ToString();
        }

        private static string Nz(string? s) => string.IsNullOrWhiteSpace(s) ? "N/A" : s;
        private static string Xe(string? s) =>
            System.Security.SecurityElement.Escape(string.IsNullOrWhiteSpace(s) ? "" : s) ?? "";
    }
}
